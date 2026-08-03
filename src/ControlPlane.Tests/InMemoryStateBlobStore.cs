using Azure;
using ControlPlane.Model;
using ControlPlane.Storage;

namespace ControlPlane.Tests;

/// <summary>
/// An in-memory stand-in for the blob, with the same optimistic-concurrency behaviour: every write
/// mints a new ETag and a stale <c>If-Match</c> is rejected. That is what makes the concurrency
/// tests meaningful without Azurite.
/// </summary>
public sealed class InMemoryStateBlobStore(StateDocument? initial = null) : IStateBlobStore
{
    /// <summary>
    /// The fake's clock. Real blob modification dates have one-second resolution, so a wall clock
    /// would leave two writes in the same second indistinguishable and the recency test flaky.
    /// One second per version is deterministic and still exercises the header's granularity.
    /// </summary>
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private string _state = StateJson.Serialize(initial ?? new StateDocument());
    private ETag _etag = new("0");
    private long _version;

    public AllowlistDocument? PublishedAllowlist { get; private set; }
    public int Writes { get; private set; }
    public int PublishedCount { get; private set; }

    /// <summary>Re-seeds in place. The API host resolves this store once as a singleton, so a test
    /// cannot hand it a fresh instance between cases — it has to reset the one it already holds.</summary>
    public void Reset(StateDocument seed)
    {
        lock (_gate)
        {
            _state = StateJson.Serialize(seed);
            _etag = new ETag((++_version).ToString());
            PublishedAllowlist = null;
            Writes = 0;
            PublishedCount = 0;
            BeforeWrite = null;
            FailPublish = null;
        }
    }

    /// <summary>Runs before each write, letting a test inject a competing writer to force a 412.</summary>
    public Action? BeforeWrite { get; set; }

    /// <summary>Called before each publication; return an exception to fail that attempt, or null
    /// to let it through. Simulates a transient storage fault on the second write — the window in
    /// which state is committed but the proxy's document is not.</summary>
    public Func<Exception?>? FailPublish { get; set; }

    public StateDocument Current
    {
        get
        {
            lock (_gate)
            {
                return StateJson.Deserialize<StateDocument>(_state);
            }
        }
    }

    public Task<StateSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(new StateSnapshot(
                StateJson.Deserialize<StateDocument>(_state), _etag, Epoch.AddSeconds(_version)));
        }
    }

    public Task WriteAsync(StateDocument state, ETag? etag, CancellationToken cancellationToken)
    {
        BeforeWrite?.Invoke();

        lock (_gate)
        {
            if (etag != _etag)
            {
                throw new StatePreconditionFailedException();
            }

            _state = StateJson.Serialize(state);
            _etag = new ETag((++_version).ToString());
            Writes++;
        }

        return Task.CompletedTask;
    }

    /// <summary>Simulates another writer landing a change, invalidating any ETag already read.</summary>
    public void ConcurrentlyReplace(StateDocument state)
    {
        lock (_gate)
        {
            _state = StateJson.Serialize(state);
            _etag = new ETag((++_version).ToString());
        }
    }

    public Task PublishAllowlistAsync(AllowlistDocument allowlist, CancellationToken cancellationToken)
    {
        if (FailPublish?.Invoke() is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            PublishedAllowlist = allowlist;
            PublishedCount++;
        }

        return Task.CompletedTask;
    }
}
