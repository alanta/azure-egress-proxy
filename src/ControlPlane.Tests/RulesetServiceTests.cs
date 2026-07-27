using System.Net;
using ControlPlane.Model;
using ControlPlane.Policy;
using ControlPlane.Rulesets;
using ControlPlane.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using Polly.Retry;

namespace ControlPlane.Tests;

public class RulesetServiceTests
{
    private const string Pipeline = "22222222-2222-2222-2222-222222222222";
    private const string Platform = "99999999-9999-9999-9999-999999999999";
    private const string Workload = "11111111-1111-1111-1111-111111111111";
    private const string Stranger = "44444444-4444-4444-4444-444444444444";

    // ---- the onboarding default -------------------------------------------------------------

    /// <summary>report is the on-ramp DEFAULT: a team that says nothing gets observation, not
    /// enforcement it did not ask for.</summary>
    [Fact]
    public async Task Onboard_without_an_action_starts_in_report()
    {
        var (service, store) = Build(Seed());

        var outcome = await service.PutAsync("payments", Onboard(["api.stripe.com"]), Pipeline, false, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
        Assert.True(outcome.Created);
        Assert.Equal("report", outcome.Ruleset!.Content.Action);
        Assert.Equal("report", store.Current.Find("payments")!.Content.Action);
    }

    /// <summary>
    /// An explicit enforce at onboard is honoured. report PASSES all traffic and only logs it, so
    /// coercing it over an explicit request would give a brand-new workload more egress than it
    /// asked for — the opposite of what this system is for.
    /// </summary>
    [Fact]
    public async Task Onboard_asking_to_enforce_is_honoured()
    {
        var (service, store) = Build(Seed());

        var outcome = await service.PutAsync("payments", Onboard(["api.stripe.com"], "enforce"), Pipeline, false, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
        Assert.True(outcome.Created);
        Assert.Equal("enforce", outcome.Ruleset!.Content.Action);
        Assert.Equal("enforce", store.Current.Find("payments")!.Content.Action);
    }

    /// <summary>An explicit report or open is equally honoured — nothing is coerced either way.</summary>
    [Theory]
    [InlineData("report")]
    [InlineData("open")]
    public async Task Onboard_honours_any_explicit_action(string action)
    {
        var (service, _) = Build(Seed());

        var outcome = await service.PutAsync("payments", Onboard(["api.stripe.com"], action), Pipeline, false, default);

        Assert.Equal(action, outcome.Ruleset!.Content.Action);
    }

    [Fact]
    public async Task Promotion_is_an_explicit_later_push()
    {
        var (service, _) = Build(Seed());
        await service.PutAsync("payments", Onboard(["api.stripe.com"]), Pipeline, false, default);

        var outcome = await service.PutAsync("payments", Push(["api.stripe.com"], "enforce"), Pipeline, false, default);

        Assert.Equal("enforce", outcome.Ruleset!.Content.Action);
    }

    /// <summary>
    /// The rule the design turns on: adding a host must never drag an enforcing ruleset down to
    /// report, because that would weaken rules already in force. The audit event and the :check
    /// diff are the controls on widening instead.
    /// </summary>
    [Fact]
    public async Task Adding_a_host_never_downgrades_an_enforcing_ruleset()
    {
        var (service, store) = Build(Seed(Enforcing("payments", ["api.stripe.com"], Pipeline)));

        var outcome = await service.PutAsync(
            "payments", Push(["api.stripe.com", "api.newvendor.com"], "enforce"), Pipeline, false, default);

        Assert.Equal("enforce", outcome.Ruleset!.Content.Action);
        Assert.Equal(["api.newvendor.com"], outcome.Diff!.Added);
        Assert.Equal("enforce", store.Current.Find("payments")!.Content.Action);
    }

    [Fact]
    public async Task A_full_replace_reports_removed_hosts()
    {
        var (service, _) = Build(Seed(Enforcing("payments", ["a.example.com", "b.example.com"], Pipeline)));

        var outcome = await service.PutAsync("payments", Push(["a.example.com"], "enforce"), Pipeline, false, default);

        Assert.Equal(["b.example.com"], outcome.Diff!.Removed);
        Assert.Empty(outcome.Diff.Added);
    }

    // ---- authorization ----------------------------------------------------------------------

    [Fact]
    public async Task Onboard_without_the_verb_creates_nothing()
    {
        var (service, store) = Build(Seed());

        var outcome = await service.PutAsync("payments", Onboard(["api.stripe.com"]), Stranger, false, default);

        Assert.Equal(HttpStatusCode.Forbidden, outcome.Error!.Status);
        Assert.Empty(store.Current.Rulesets);
        Assert.Equal(0, store.Writes);
    }

    /// <summary>Trust-on-first-use: the creator gains update/offboard without any platform grant
    /// naming the ruleset, which is what makes onboarding cost one grant instead of a ticket.</summary>
    [Fact]
    public async Task Trust_on_first_use_makes_the_creator_the_owner()
    {
        // A grant carrying onboard only — everything after this comes from ownership.
        var (service, store) = Build(new StateDocument
        {
            Grants = [new Grant { Identity = Pipeline, Verbs = [Verbs.Onboard], Rulesets = [] }],
        });

        var created = await service.PutAsync("payments", Onboard(["api.stripe.com"]), Pipeline, false, default);
        Assert.True(created.Succeeded, created.Error?.Message);
        Assert.Equal(Pipeline, store.Current.Find("payments")!.Owner);

        var updated = await service.PutAsync("payments", Push(["api.stripe.com"], "enforce"), Pipeline, false, default);
        Assert.True(updated.Succeeded, updated.Error?.Message);

        var offboarded = await service.DeleteAsync("payments", Pipeline, default);
        Assert.True(offboarded.Succeeded, offboarded.Error?.Message);
    }

    [Fact]
    public async Task A_grant_scoped_to_another_ruleset_does_not_authorize_this_one()
    {
        var (service, _) = Build(new StateDocument
        {
            Rulesets = [Enforcing("payments", ["a.example.com"], owner: null)],
            Grants = [new Grant { Identity = Stranger, Verbs = [Verbs.Update], Rulesets = ["something-else"] }],
        });

        var outcome = await service.PutAsync("payments", Push(["evil.example.com"], "enforce"), Stranger, false, default);

        Assert.Equal(HttpStatusCode.Forbidden, outcome.Error!.Status);
    }

    [Fact]
    public async Task An_unscoped_platform_grant_authorizes_any_ruleset()
    {
        var (service, _) = Build(Seed(Enforcing("payments", ["a.example.com"], Pipeline)));

        var outcome = await service.PutAsync("payments", Push(["a.example.com"], "enforce"), Platform, false, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
    }

    // ---- the anti-hijack invariants ---------------------------------------------------------

    /// <summary>The attack the proxy exists to stop: a compromised workload widening its own rules.</summary>
    [Fact]
    public async Task A_governed_workload_can_never_write_rulesets()
    {
        var (service, store) = Build(new StateDocument
        {
            Rulesets = [Enforcing("sample-app", ["api.github.com"], owner: Workload, subject: Workload)],
            // Even with a full platform grant, being a subject disqualifies the caller.
            Grants = [new Grant { Identity = Workload, Verbs = [Verbs.Onboard, Verbs.Update, Verbs.Offboard] }],
        });

        var outcome = await service.PutAsync(
            "sample-app", Push(["api.github.com", "exfil.example.com"], "enforce"), Workload, false, default);

        Assert.Equal(HttpStatusCode.Forbidden, outcome.Error!.Status);
        Assert.Contains("writer must not be subject", outcome.Error.Message);
        Assert.Equal(0, store.Writes);
    }

    /// <summary>A module grows: the owner (who holds <c>bind</c> by trust-on-first-use) may add a
    /// second workload to an existing ruleset without a destructive offboard-and-re-onboard.</summary>
    [Fact]
    public async Task An_owner_may_bind_a_new_subject()
    {
        var (service, store) = Build(Seed(Enforcing("payments", ["a.example.com"], Pipeline, subject: Workload)));

        var outcome = await service.PutAsync("payments", new PushRequest
        {
            Subjects = [new Subject { Appid = Workload }, new Subject { Appid = Stranger }],
            Content = new RulesetContent { AllowedHosts = ["a.example.com"], Action = "enforce" },
        }, Pipeline, false, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
        var stored = store.Current.Find("payments")!.Subjects.Select(s => s.Appid).ToHashSet();
        Assert.Contains(Stranger, stored);
        Assert.Contains(Workload, stored);
    }

    /// <summary>Membership is the sensitive half of a write: an identity that holds only <c>update</c>
    /// (edits hosts) and is not the owner cannot move a workload under different rules.</summary>
    [Fact]
    public async Task An_update_without_bind_cannot_change_subjects()
    {
        // Owner is null, so Pipeline authorizes via its platform grant — which carries update, not bind.
        var (service, store) = Build(Seed(Enforcing("payments", ["a.example.com"], owner: null)));

        var outcome = await service.PutAsync("payments", new PushRequest
        {
            Subjects = [new Subject { Appid = Stranger }],
            Content = new RulesetContent { AllowedHosts = ["a.example.com"], Action = "enforce" },
        }, Pipeline, false, default);

        Assert.Equal(HttpStatusCode.Forbidden, outcome.Error!.Status);
        Assert.Contains("'bind'", outcome.Error.Message);
        Assert.Equal(0, store.Writes);
    }

    /// <summary>One-to-one holds on a bind too: a subject already governed elsewhere cannot be pulled
    /// into this ruleset.</summary>
    [Fact]
    public async Task Binding_a_subject_claimed_by_another_ruleset_is_rejected()
    {
        var (service, _) = Build(Seed(
            Enforcing("payments", ["a.example.com"], Pipeline, subject: Workload),
            Enforcing("analytics", ["b.example.com"], Platform, subject: Stranger)));

        var outcome = await service.PutAsync("payments", new PushRequest
        {
            Subjects = [new Subject { Appid = Workload }, new Subject { Appid = Stranger }],
            Content = new RulesetContent { AllowedHosts = ["a.example.com"], Action = "enforce" },
        }, Pipeline, false, default);

        Assert.Equal(HttpStatusCode.Conflict, outcome.Error!.Status);
        Assert.Contains("already belongs to ruleset 'analytics'", outcome.Error.Message);
    }

    /// <summary>A desired-state pipeline pushes one file every run, so restating the stored
    /// subjects unchanged must not be an error — only a *change* is refused.</summary>
    [Fact]
    public async Task An_update_may_restate_the_stored_subjects()
    {
        var subject = new Subject { Netid = "10.5.0.0/24" };
        var (service, _) = Build(new StateDocument
        {
            Rulesets =
            [
                new Ruleset
                {
                    Name = "payments",
                    Subjects = [subject],
                    Content = new RulesetContent { AllowedHosts = ["a.example.com"], Action = "enforce" },
                    Owner = Pipeline,
                },
            ],
        });

        var outcome = await service.PutAsync("payments", new PushRequest
        {
            Subjects = [subject],
            Content = new RulesetContent { AllowedHosts = ["a.example.com"], Action = "enforce" },
        }, Pipeline, false, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
    }

    /// <summary>One-to-one: first-come protects an already-onboarded subject from being claimed.</summary>
    [Fact]
    public async Task A_subject_claimed_by_another_ruleset_cannot_be_onboarded_again()
    {
        var (service, _) = Build(Seed(Enforcing("payments", ["a.example.com"], Pipeline, subject: Stranger)));

        var outcome = await service.PutAsync("payments-copy", new PushRequest
        {
            Subjects = [new Subject { Appid = Stranger }],
            Content = new RulesetContent { AllowedHosts = ["evil.example.com"] },
        }, Pipeline, false, default);

        Assert.Equal(HttpStatusCode.Conflict, outcome.Error!.Status);
        Assert.Contains("already belongs to ruleset 'payments'", outcome.Error.Message);
    }

    // ---- offboard ---------------------------------------------------------------------------

    [Fact]
    public async Task Offboard_removes_the_ruleset_and_frees_its_subjects()
    {
        var (service, store) = Build(Seed(Enforcing("payments", ["a.example.com"], Pipeline, subject: Stranger)));

        var outcome = await service.DeleteAsync("payments", Pipeline, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
        Assert.Empty(store.Current.Rulesets);
        // The rendered document no longer mentions the subject, so it falls to the deny block.
        Assert.Empty(store.PublishedAllowlist!.Modules);
    }

    [Fact]
    public async Task Offboard_of_an_absent_ruleset_is_not_found()
    {
        var (service, _) = Build(Seed());

        var outcome = await service.DeleteAsync("ghost", Pipeline, default);

        Assert.Equal(HttpStatusCode.NotFound, outcome.Error!.Status);
    }

    // ---- dry run ----------------------------------------------------------------------------

    [Fact]
    public async Task Check_returns_the_diff_and_writes_nothing()
    {
        var (service, store) = Build(Seed(Enforcing("payments", ["a.example.com", "b.example.com"], Pipeline)));

        var outcome = await service.PutAsync(
            "payments", Push(["a.example.com", "c.example.com"], "enforce"), Pipeline, dryRun: true, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
        Assert.Equal(["c.example.com"], outcome.Diff!.Added);
        Assert.Equal(["b.example.com"], outcome.Diff.Removed);
        Assert.Equal(0, store.Writes);
        Assert.Equal(0, store.PublishedCount);
    }

    [Fact]
    public async Task Check_shows_the_onboarding_default_before_a_real_push()
    {
        var (service, store) = Build(Seed());

        var outcome = await service.PutAsync(
            "payments", Onboard(["api.stripe.com"]), Pipeline, dryRun: true, default);

        Assert.Equal("report", outcome.Ruleset!.Content.Action);
        Assert.Equal(0, store.Writes);
    }

    // ---- the platform trust boundary --------------------------------------------------------

    /// <summary>
    /// Grants share the blob with the rulesets, so "the API cannot widen its own authority" is a
    /// code-level invariant: every write must copy the platform's grants through untouched.
    /// </summary>
    [Fact]
    public async Task A_write_never_touches_the_platform_grants_or_fallback()
    {
        var seed = Seed() with { Fallback = new Fallback { AllowedHosts = ["baseline.example.com"] } };
        var (service, store) = Build(seed);

        await service.PutAsync("payments", Onboard(["api.stripe.com"]), Pipeline, false, default);

        Assert.Equal(
            StateJson.Serialize(seed.Grants),
            StateJson.Serialize(store.Current.Grants));
        Assert.Equal(["baseline.example.com"], store.Current.Fallback!.AllowedHosts);
    }

    // ---- validation -------------------------------------------------------------------------

    [Theory]
    [InlineData("https://api.stripe.com")]
    [InlineData("*.stripe.com")]
    [InlineData("10.0.0.1")]
    public async Task A_host_that_is_not_an_fqdn_is_rejected(string host)
    {
        var (service, store) = Build(Seed(Enforcing("payments", ["a.example.com"], Pipeline)));

        var outcome = await service.PutAsync("payments", Push([host], "enforce"), Pipeline, false, default);

        Assert.Equal(HttpStatusCode.BadRequest, outcome.Error!.Status);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Onboard_requires_at_least_one_subject()
    {
        var (service, _) = Build(Seed());

        var outcome = await service.PutAsync("payments", Push(["a.example.com"]), Pipeline, false, default);

        Assert.Equal(HttpStatusCode.BadRequest, outcome.Error!.Status);
        Assert.Contains("at least one subject", outcome.Error.Message);
    }

    // ---- the write publishes the proxy's document -------------------------------------------

    [Fact]
    public async Task A_successful_write_publishes_the_rendered_allowlist()
    {
        var (service, store) = Build(Seed());

        await service.PutAsync("payments", Onboard(["api.stripe.com"]), Pipeline, false, default);

        var module = Assert.Single(store.PublishedAllowlist!.Modules);
        Assert.Equal("payments", module.Id);
        Assert.Equal("report", module.Action);
        Assert.Equal(["api.stripe.com"], module.AllowedHosts);
    }

    // ---- helpers ----------------------------------------------------------------------------

    internal static (RulesetService Service, InMemoryStateBlobStore Store) Build(
        StateDocument seed,
        int maxRetryAttempts = 5,
        Action? beforeWrite = null)
    {
        var store = new InMemoryStateBlobStore(seed) { BeforeWrite = beforeWrite };

        var services = new ServiceCollection();
        services.AddResiliencePipeline(RulesetService.PublishPipeline, pipeline => pipeline
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }));
        services.AddResiliencePipeline(RulesetService.RmwPipeline, pipeline => pipeline
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<StatePreconditionFailedException>(),
                MaxRetryAttempts = maxRetryAttempts,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }));

        var provider = services.BuildServiceProvider();

        var service = new RulesetService(
            store,
            provider.GetRequiredService<ResiliencePipelineProvider<string>>(),
            NullLogger<RulesetService>.Instance,
            new AuditLog(NullLogger<AuditLog>.Instance));

        return (service, store);
    }

    private static StateDocument Seed(params Ruleset[] rulesets) => new()
    {
        Rulesets = [.. rulesets],
        Grants =
        [
            new Grant { Identity = Pipeline, Verbs = [Verbs.Onboard, Verbs.Update, Verbs.Offboard], Rulesets = ["payments"] },
            new Grant { Identity = Platform, Verbs = [Verbs.Onboard, Verbs.Update, Verbs.Offboard] },
        ],
    };

    private static Ruleset Enforcing(string name, string[] hosts, string? owner, string subject = Workload) => new()
    {
        Name = name,
        Subjects = [new Subject { Appid = subject }],
        Content = new RulesetContent { AllowedHosts = [.. hosts], Action = "enforce" },
        Owner = owner,
    };

    private static PushRequest Onboard(string[] hosts, string? action = null) => new()
    {
        Subjects = [new Subject { Appid = Workload }],
        Content = new RulesetContent { AllowedHosts = [.. hosts], Action = action },
    };

    private static PushRequest Push(string[] hosts, string? action = null) => new()
    {
        Content = new RulesetContent { AllowedHosts = [.. hosts], Action = action },
    };
}
