using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Portal.Clients;

namespace Portal.Tests;

/// <summary>
/// The wave-1 → wave-2 contract, asserted. Everything below is something a surface will rely on
/// without re-deriving it, so a change here is a change to every surface.
/// </summary>
public class ClientContractTests
{
    // ---- defaults never widen ------------------------------------------------------------------

    /// <summary>
    /// The console must normalize exactly as the proxy and the renderer do. Displaying an
    /// unrecognised action as anything more permissive than what the proxy will actually do would
    /// misreport the posture in the safe-looking direction — the one failure mode a security
    /// console cannot have.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("allow-everything")]
    [InlineData("ENFORCE")]
    public void An_absent_or_unrecognised_action_reads_as_enforce(string? action) =>
        Assert.Equal(RulesetAction.Enforce, RulesetActions.Normalize(action));

    [Theory]
    [InlineData("report", RulesetAction.Report)]
    [InlineData("  Report  ", RulesetAction.Report)]
    [InlineData("OPEN", RulesetAction.Open)]
    public void An_explicit_permissive_action_is_honoured(string action, RulesetAction expected) =>
        Assert.Equal(expected, RulesetActions.Normalize(action));

    [Theory]
    [InlineData(RulesetAction.Enforce, "enforce")]
    [InlineData(RulesetAction.Report, "report")]
    [InlineData(RulesetAction.Open, "open")]
    public void The_wire_spelling_round_trips(RulesetAction action, string wire)
    {
        Assert.Equal(wire, action.ToWire());
        Assert.Equal(action, RulesetActions.Normalize(wire));
    }

    // ---- traffic windows are bounded -----------------------------------------------------------

    /// <summary>An unrecognised window narrows to the default rather than widening: the same
    /// instinct as action normalization, applied to what a query costs.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("30d")]
    [InlineData("all")]
    public void An_unrecognised_window_falls_back_to_the_default(string? value) =>
        Assert.Equal(TrafficWindows.Default, TrafficWindows.Parse(value));

    [Fact]
    public void Every_window_round_trips_through_its_query_value() =>
        Assert.All(Enum.GetValues<TrafficWindow>(),
            window => Assert.Equal(window, TrafficWindows.Parse(window.ToQueryValue())));

    [Fact]
    public void No_window_is_unbounded() =>
        Assert.All(Enum.GetValues<TrafficWindow>(), window =>
        {
            Assert.True(window.ToTimeSpan() > TimeSpan.Zero);
            Assert.True(window.ToTimeSpan() <= TimeSpan.FromDays(7),
                "a console window longer than a week is a Log Analytics bill, not a view");
        });

    // ---- the denial -> ruleset join ------------------------------------------------------------

    /// <summary>
    /// The join the console exists to close. The audit table's Role IS the workload appid, which
    /// is exactly subjects[].appid — no heuristics, and case-insensitive because Entra hands the
    /// same GUID back in either case.
    /// </summary>
    [Fact]
    public void A_denial_resolves_to_the_ruleset_governing_its_subject()
    {
        var snapshot = Snapshot(Ruleset("payments", RulesetAction.Enforce, Appid("11111111-1111-1111-1111-111111111111")));

        Assert.Equal("payments", snapshot.Governing("11111111-1111-1111-1111-111111111111")?.Name);
        Assert.Equal("payments", snapshot.Governing("11111111-1111-1111-1111-111111111111".ToUpperInvariant())?.Name);
    }

    /// <summary>A subject belonging to no ruleset falls to the fallback. The console must say so
    /// rather than attributing it to a ruleset that does not govern it.</summary>
    [Fact]
    public void A_subject_in_no_ruleset_resolves_to_nothing()
    {
        var snapshot = Snapshot(Ruleset("payments", RulesetAction.Enforce, Appid("11111111-1111-1111-1111-111111111111")));

        Assert.Null(snapshot.Governing("99999999-9999-9999-9999-999999999999"));
        Assert.Null(snapshot.Governing(null));
        Assert.Null(snapshot.Governing(""));
    }

    /// <summary>
    /// A netid ruleset joins on a source address, which is weaker by construction — the repo is
    /// emphatic that a source address is not an identity. The view must be able to say so, so the
    /// flag exists rather than the correlation degrading silently.
    /// </summary>
    [Fact]
    public void A_netid_ruleset_is_marked_as_attributed_by_source_address()
    {
        var network = Ruleset("billing", RulesetAction.Enforce, new SubjectView(null, "10.2.0.0/23"));
        var identity = Ruleset("payments", RulesetAction.Enforce, Appid("11111111-1111-1111-1111-111111111111"));
        var mixed = Ruleset("mixed", RulesetAction.Enforce,
            Appid("11111111-1111-1111-1111-111111111111"), new SubjectView(null, "10.3.0.0/24"));

        Assert.True(network.IsNetworkAttributed);
        Assert.False(identity.IsNetworkAttributed);
        // A ruleset with even one validated identity is not purely source-attributed.
        Assert.False(mixed.IsNetworkAttributed);
    }

    // ---- the portal has no way to write --------------------------------------------------------

    /// <summary>
    /// Not "does not currently write" — there is no method to call. The source scan in
    /// <see cref="ReadOnlyTests"/> catches a write added anywhere in the portal; this catches one
    /// added to the type whose whole job is talking to the control plane.
    /// </summary>
    [Fact]
    public void The_control_plane_client_exposes_no_write()
    {
        var methods = typeof(ControlPlaneClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

        Assert.Equal(["CheckAsync", "ReadAsync"], methods.Order());
    }

    /// <summary>The same, for the facade every surface actually takes.</summary>
    [Fact]
    public void The_console_facade_exposes_no_write()
    {
        var writes = typeof(ConsoleData)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.Contains("Put", StringComparison.Ordinal)
                || m.Name.Contains("Delete", StringComparison.Ordinal)
                || m.Name.Contains("Write", StringComparison.Ordinal)
                || m.Name.Contains("Apply", StringComparison.Ordinal))
            .Select(m => m.Name);

        Assert.Empty(writes);
    }

    // ---- the cache -----------------------------------------------------------------------------

    /// <summary>
    /// Panels poll on `every 60s`. Several panels, several operators, three sources — uncached
    /// that is a Log Analytics query per panel per operator per minute, and it peaks exactly when
    /// the team is all watching the console during an incident.
    /// </summary>
    [Fact]
    public async Task A_second_read_inside_the_lifetime_does_not_reach_the_source()
    {
        var clock = new FakeClock();
        var cache = new ResponseCache(clock, NullLogger<ResponseCache>.Instance);
        var fetches = 0;

        Task<int> Fetch(CancellationToken _) => Task.FromResult(++fetches);

        Assert.Equal(1, await cache.GetAsync("k", TimeSpan.FromMinutes(2), Fetch, default));
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, await cache.GetAsync("k", TimeSpan.FromMinutes(2), Fetch, default));
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task A_read_after_the_lifetime_refetches()
    {
        var clock = new FakeClock();
        var cache = new ResponseCache(clock, NullLogger<ResponseCache>.Instance);
        var fetches = 0;

        Task<int> Fetch(CancellationToken _) => Task.FromResult(++fetches);

        await cache.GetAsync("k", TimeSpan.FromMinutes(2), Fetch, default);
        clock.Advance(TimeSpan.FromMinutes(3));
        await cache.GetAsync("k", TimeSpan.FromMinutes(2), Fetch, default);

        Assert.Equal(2, fetches);
    }

    /// <summary>Concurrent misses join one fetch. Without this a whole team hitting refresh at
    /// once costs one query each, which is the moment it matters most that it does not.</summary>
    [Fact]
    public async Task Concurrent_misses_share_one_fetch()
    {
        var cache = new ResponseCache(new FakeClock(), NullLogger<ResponseCache>.Instance);
        var fetches = 0;
        var gate = new TaskCompletionSource();

        async Task<int> Fetch(CancellationToken _)
        {
            Interlocked.Increment(ref fetches);
            await gate.Task;
            return 42;
        }

        var readers = Enumerable.Range(0, 8)
            .Select(_ => cache.GetAsync("k", TimeSpan.FromMinutes(1), Fetch, default))
            .ToArray();

        gate.SetResult();
        Assert.All(await Task.WhenAll(readers), value => Assert.Equal(42, value));
        Assert.Equal(1, fetches);
    }

    /// <summary>
    /// A failure must not be cached. Otherwise one transient Azure error becomes two minutes of
    /// broken panels, and the operator's instinct — refresh — does nothing.
    /// </summary>
    [Fact]
    public async Task A_failed_fetch_is_not_cached()
    {
        var cache = new ResponseCache(new FakeClock(), NullLogger<ResponseCache>.Instance);
        var attempts = 0;

        Task<int> Fetch(CancellationToken _) =>
            ++attempts == 1 ? Task.FromException<int>(new IOException("transient")) : Task.FromResult(7);

        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync("k", TimeSpan.FromMinutes(5), Fetch, default));
        Assert.Equal(7, await cache.GetAsync("k", TimeSpan.FromMinutes(5), Fetch, default));
    }

    /// <summary>
    /// The metric TTL sits just under the panels' 60-second poll, so a refresh lands on a new
    /// value; the traffic TTL sits well above it, because that is the query that costs money.
    /// </summary>
    [Fact]
    public void Cache_lifetimes_are_sized_against_the_sixty_second_poll()
    {
        Assert.True(CacheFor.Metrics < TimeSpan.FromSeconds(60));
        Assert.True(CacheFor.Metrics >= TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(10));
        Assert.True(CacheFor.Traffic > TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// A cached answer keeps the timestamp of the fetch that produced it. Restamping it as "just
    /// now" would state a freshness the data does not have, which is the one thing design.md D4
    /// says a surface must not do.
    /// </summary>
    [Fact]
    public async Task A_cached_value_keeps_the_freshness_of_its_fetch()
    {
        var clock = new FakeClock();
        var cache = new ResponseCache(clock, NullLogger<ResponseCache>.Instance);
        var stamped = new Freshness(clock.GetUtcNow());

        Task<Freshness> Fetch(CancellationToken _) => Task.FromResult(stamped);

        await cache.GetAsync("k", TimeSpan.FromMinutes(5), Fetch, default);
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(stamped.AsOf, (await cache.GetAsync("k", TimeSpan.FromMinutes(5), Fetch, default)).AsOf);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static SubjectView Appid(string appid) => new(appid, null);

    private static RulesetView Ruleset(string name, RulesetAction action, params SubjectView[] subjects) =>
        new(name, subjects, ["api.example.com"], action, "owner");

    private static PolicySnapshot Snapshot(params RulesetView[] rulesets) => new(
        rulesets, [], new FallbackView([], true), new Recency(null, null), Freshness.Now);

    /// <summary>A clock the test drives, so cache expiry is asserted rather than slept through.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
