using System.Text;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ControlPlane.Model;

namespace ControlPlane.Storage;

public sealed record StateSnapshot(StateDocument State, ETag? ETag);

/// <summary>Raised when the state blob changed under us — the signal the RMW retry loop is built on.</summary>
public sealed class StatePreconditionFailedException(Exception? inner = null)
    : Exception("the control-plane state blob changed during the read-modify-write", inner);

public interface IStateBlobStore
{
    Task<StateSnapshot> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Writes the state under <c>If-Match</c>, or creates it under <c>If-None-Match: *</c>
    /// when <paramref name="etag"/> is null. Throws <see cref="StatePreconditionFailedException"/>
    /// if it lost the race.</summary>
    Task WriteAsync(StateDocument state, ETag? etag, CancellationToken cancellationToken);

    /// <summary>Publishes the rendered projection the proxy polls. Unconditional: this store is the
    /// blob's sole writer, and the state write that preceded it is the linearization point.</summary>
    Task PublishAllowlistAsync(AllowlistDocument allowlist, CancellationToken cancellationToken);
}

public sealed class BlobStoreOptions
{
    /// <summary>Local/dev (Azurite). In Azure, leave unset and use <see cref="ServiceUri"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Azure: the blob service endpoint, accessed with the control plane's own managed
    /// identity — the sole Storage Blob Data Contributor on this container.</summary>
    public string? ServiceUri { get; set; }

    public string Container { get; set; } = "egress-config";

    /// <summary>The control plane's entire internal state: rulesets, platform grants, fallback.</summary>
    public string StateBlob { get; set; } = "rulesets.json";

    /// <summary>The rendered projection the proxy reads. Frozen schema.</summary>
    public string AllowlistBlob { get; set; } = "allowlist.json";
}

public sealed class StateBlobStore : IStateBlobStore
{
    private readonly BlobClient _state;
    private readonly BlobClient _allowlist;

    public StateBlobStore(BlobStoreOptions options)
        : this(options, clientOptions: null)
    {
    }

    /// <summary>
    /// Takes the SDK's client options so tests can substitute the transport and drive the real
    /// ETag/conditional-request behaviour without Azurite. DI never sees this overload — the
    /// container only considers public constructors — so the production path stays the one above.
    /// </summary>
    internal StateBlobStore(BlobStoreOptions options, BlobClientOptions? clientOptions)
    {
        var service = options.ConnectionString is { Length: > 0 } connectionString
            ? new BlobServiceClient(connectionString, clientOptions)
            : new BlobServiceClient(
                new Uri(options.ServiceUri ?? throw new InvalidOperationException(
                    "configure ControlPlane:Storage:ServiceUri (Azure) or :ConnectionString (Azurite)")),
                new DefaultAzureCredential(),
                clientOptions);

        var container = service.GetBlobContainerClient(options.Container);
        _state = container.GetBlobClient(options.StateBlob);
        _allowlist = container.GetBlobClient(options.AllowlistBlob);
    }

    public async Task<StateSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _state.DownloadContentAsync(cancellationToken);
            var state = StateJson.Deserialize<StateDocument>(response.Value.Content.ToString());
            return new StateSnapshot(state, response.Value.Details.ETag);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            // No state yet: an empty registry with no grants, so every write is denied until the
            // platform team seeds one. Fail closed, like the proxy's unreachable-blob start.
            return new StateSnapshot(new StateDocument(), null);
        }
    }

    public async Task WriteAsync(StateDocument state, ETag? etag, CancellationToken cancellationToken)
    {
        var conditions = etag is { } tag
            ? new BlobRequestConditions { IfMatch = tag }
            : new BlobRequestConditions { IfNoneMatch = ETag.All };

        await UploadAsync(_state, StateJson.Serialize(state), conditions, cancellationToken);
    }

    public Task PublishAllowlistAsync(AllowlistDocument allowlist, CancellationToken cancellationToken) =>
        UploadAsync(_allowlist, StateJson.Serialize(allowlist), conditions: null, cancellationToken);

    private static async Task UploadAsync(
        BlobClient blob,
        string json,
        BlobRequestConditions? conditions,
        CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(json + "\n"));

        try
        {
            await blob.UploadAsync(
                content,
                new BlobUploadOptions
                {
                    Conditions = conditions,
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                },
                cancellationToken);
        }
        // 412 is a lost If-Match race; 409 is a lost If-None-Match race (someone created it first).
        catch (RequestFailedException e) when (e.Status is 412 or 409)
        {
            throw new StatePreconditionFailedException(e);
        }
    }
}
