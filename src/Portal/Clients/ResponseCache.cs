using System.Collections.Concurrent;

namespace Portal.Clients;

/// <summary>
/// How long a kind of answer stays good. Sized against what actually changes underneath, not
/// against how often a panel asks.
/// </summary>
public static class CacheFor
{
    /// <summary>
    /// Policy. The state document changes when a pipeline pushes, which is minutes-to-days apart,
    /// and every read carries its own last-modified stamp so a slightly stale answer is
    /// self-describing rather than misleading.
    /// </summary>
    public static readonly TimeSpan Policy = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Log Analytics. The longest window here, because this is the one that costs money per query
    /// and because ingestion latency is already minutes — a fresher cache would buy accuracy the
    /// underlying data does not have.
    /// </summary>
    public static readonly TimeSpan Traffic = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Azure Monitor metrics. Just under the panels' 60-second refresh, so a poll lands on a new
    /// value rather than re-serving the same one — and just over Azure Monitor's own 1-minute
    /// grain, so it never fetches faster than the data changes.
    /// </summary>
    public static readonly TimeSpan Metrics = TimeSpan.FromSeconds(55);

    /// <summary>ARM configuration — scale-set shape, prefix size. Changes on deployment.</summary>
    public static readonly TimeSpan Runtime = TimeSpan.FromMinutes(1);
}

/// <summary>
/// The server-side cache every data client sits behind.
///
/// <para><b>This is not an optimisation.</b> Design.md D8 rule 3 makes it a requirement: panels
/// refresh on <c>hx-trigger="every 60s"</c>, and with several panels per surface and several
/// operators watching, an uncached console issues a Log Analytics query per panel per operator per
/// minute. That is a real bill and a real rate limit, and it arrives exactly when the platform team
/// is all looking at the console — during an incident.</para>
///
/// <para><b>The cache is shared across operators, and that is correct here.</b> The portal serves
/// one audience tier with no per-user scoping (design.md D7), so there is no user-specific view for
/// one operator's cached answer to leak to another. If per-ruleset RBAC ever arrives, this key
/// space becomes wrong and must gain the identity — noted here because that is precisely the sort
/// of thing a later change would not think to look for.</para>
///
/// <para>Single-flight by design: concurrent callers for the same key await one fetch rather than
/// issuing one query each, which is what makes a simultaneous refresh across a team cost one query
/// rather than a dozen.</para>
/// </summary>
public sealed class ResponseCache(TimeProvider clock, ILogger<ResponseCache> logger)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the cached value, or fetches and stores one. The freshness the caller renders comes
    /// from the value itself — the DTOs carry <see cref="Freshness"/> — so a cached answer keeps
    /// the timestamp of the fetch that produced it rather than of the request that read it. A
    /// console that restamped cached data as "just now" would be stating a freshness it does not
    /// have, which is the one thing design.md D4 says not to do.
    /// </summary>
    public async Task<T> GetAsync<T>(
        string key,
        TimeSpan lifetime,
        Func<CancellationToken, Task<T>> fetch,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (_entries.TryGetValue(key, out var existing) && existing.Expires > now)
        {
            return await Unwrap<T>(existing, key, cancellationToken);
        }

        // The task is stored, not the result, so callers arriving mid-fetch join the one in
        // flight. AddOrUpdate re-checks under the dictionary's own lock, which is what keeps two
        // simultaneous misses from becoming two queries.
        var entry = _entries.AddOrUpdate(
            key,
            _ => New(fetch, now, lifetime),
            (_, current) => current.Expires > now ? current : New(fetch, now, lifetime));

        return await Unwrap<T>(entry, key, cancellationToken);
    }

    /// <summary>Drops everything. For the local loop, where a push should be visible immediately
    /// rather than after the policy TTL.</summary>
    public void Clear() => _entries.Clear();

    private async Task<T> Unwrap<T>(Entry entry, string key, CancellationToken cancellationToken)
    {
        try
        {
            return (T)await entry.Value.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // This caller gave up; the shared fetch keeps running for everyone else. Nothing to
            // evict — the entry is still perfectly good.
            throw;
        }
        catch (Exception e)
        {
            // A failed fetch must not be cached: the next request would replay the exception for
            // the whole TTL, turning one transient Azure error into two minutes of broken panels.
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            logger.LogWarning(e, "cached fetch for {Key} failed; the entry was evicted", key);
            throw;
        }
    }

    /// <summary>
    /// The shared fetch runs under <see cref="CancellationToken.None"/> on purpose. It is
    /// serving every caller waiting on this key, so binding it to whichever request happened to
    /// arrive first would let one operator closing a tab cancel the query the rest are waiting
    /// for. Individual callers still walk away on their own token (see <see cref="Unwrap"/>);
    /// the fetch itself is bounded by the SDK and HttpClient timeouts underneath.
    /// </summary>
    private Entry New<T>(Func<CancellationToken, Task<T>> fetch, DateTimeOffset now, TimeSpan lifetime) =>
        new(FetchAsync(fetch), now.Add(lifetime));

    private static async Task<object> FetchAsync<T>(Func<CancellationToken, Task<T>> fetch) =>
        (await fetch(CancellationToken.None))!;

    private sealed record Entry(Task<object> Value, DateTimeOffset Expires);
}
