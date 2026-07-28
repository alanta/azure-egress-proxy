using System.Net;
using ControlPlane.Model;
using ControlPlane.Policy;
using ControlPlane.Rulesets;

namespace ControlPlane.Tests;

/// <summary>
/// All rulesets share one blob, so a naive <c>If-Match</c> would 412 even when two pushes touch
/// different rulesets. These tests pin the fix: the whole read-modify-write is retried, so a losing
/// race re-reads fresh state, re-splices, and succeeds without the caller ever seeing it.
/// </summary>
public class ConcurrencyTests
{
    private const string Platform = "99999999-9999-9999-9999-999999999999";
    private const string Workload = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task A_collision_with_a_different_ruleset_is_absorbed_and_both_survive()
    {
        var intruder = new Ruleset
        {
            Name = "billing",
            Subjects = [new Subject { Netid = "10.9.0.0/24" }],
            Content = new RulesetContent { AllowedHosts = ["api.billing.example.com"], Action = "enforce" },
        };

        InMemoryStateBlobStore? store = null;
        var collided = false;

        // Exactly once, another writer lands a change to a DIFFERENT ruleset between our read and
        // our write — the false contention a single-blob optimistic lock creates.
        var (service, created) = RulesetServiceTests.Build(Seed(), beforeWrite: () =>
        {
            if (collided)
            {
                return;
            }

            collided = true;
            store!.ConcurrentlyReplace(Seed() with { Rulesets = [intruder] });
        });
        store = created;

        var outcome = await service.PutAsync("payments", Onboard(), Platform, false, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
        Assert.True(collided);
        Assert.Equal(
            ["billing", "payments"],
            store.Current.Rulesets.Select(r => r.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Sustained_contention_surfaces_as_conflict()
    {
        InMemoryStateBlobStore? store = null;

        // A writer that never yields: every attempt's ETag is stale by the time it writes.
        var (service, created) = RulesetServiceTests.Build(
            Seed(),
            maxRetryAttempts: 3,
            beforeWrite: () => store!.ConcurrentlyReplace(Seed()));
        store = created;

        var outcome = await service.PutAsync("payments", Onboard(), Platform, false, default);

        Assert.Equal(HttpStatusCode.Conflict, outcome.Error!.Status);
        Assert.Equal(0, store.Writes);
        // Nothing was published either: the proxy never sees a half-applied state.
        Assert.Equal(0, store.PublishedCount);
    }

    /// <summary>
    /// The delegate is re-executed per attempt, so it must be idempotent — which it is, because a
    /// PUT is a full replace of the ruleset's content rather than a merge.
    /// </summary>
    [Fact]
    public async Task Re_running_the_transform_yields_the_same_result_as_running_it_once()
    {
        InMemoryStateBlobStore? store = null;
        var attempts = 0;

        var (service, created) = RulesetServiceTests.Build(Seed(), beforeWrite: () =>
        {
            if (++attempts <= 2)
            {
                store!.ConcurrentlyReplace(Seed());
            }
        });
        store = created;

        var outcome = await service.PutAsync("payments", Onboard(), Platform, false, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
        Assert.Equal(3, attempts);
        Assert.Equal(1, store.Writes);

        var ruleset = Assert.Single(store.Current.Rulesets);
        Assert.Equal("payments", ruleset.Name);
        Assert.Equal(["api.stripe.com"], ruleset.Content.AllowedHosts);
    }

    private static StateDocument Seed() => new()
    {
        Grants = [new Grant { Identity = Platform, Verbs = [Verbs.Onboard, Verbs.Update, Verbs.Offboard] }],
    };

    private static PushRequest Onboard() => new()
    {
        Subjects = [new Subject { Appid = Workload }],
        Content = new RulesetContent { AllowedHosts = ["api.stripe.com"] },
    };
}
