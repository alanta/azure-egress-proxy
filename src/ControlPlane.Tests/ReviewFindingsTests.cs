using System.Net;
using ControlPlane.Model;
using ControlPlane.Policy;
using ControlPlane.Rulesets;

namespace ControlPlane.Tests;

/// <summary>
/// Regression cover for the branch review findings. Each of these failed before the fix.
/// </summary>
public class ReviewFindingsTests
{
    private const string Pipeline = "22222222-2222-2222-2222-222222222222";
    private const string Workload = "11111111-1111-1111-1111-111111111111";

    // ---- Finding 1: state and rendered allowlist can diverge on partial failure --------------

    /// <summary>
    /// The state write is the linearization point, so once it succeeds the mutation is durable
    /// whatever happens next. A publish failure must therefore not be reported as if nothing had
    /// been written: the caller is told the change was saved but not yet visible to the proxy.
    /// </summary>
    [Fact]
    public async Task A_publish_failure_reports_that_state_was_committed_but_not_published()
    {
        var (service, store) = RulesetServiceTests.Build(Seed());
        store.FailPublish = () => new IOException("storage unavailable");

        var outcome = await service.PutAsync("payments", Onboard(), Pipeline, false, default);

        Assert.False(outcome.Succeeded);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, outcome.Error!.Status);
        Assert.Contains("was saved", outcome.Error.Message);
        Assert.Contains("previous configuration", outcome.Error.Message);

        // The state really did advance — the error must not imply otherwise.
        Assert.Equal(1, store.Writes);
        Assert.NotNull(store.Current.Find("payments"));
        Assert.Equal(0, store.PublishedCount);
    }

    /// <summary>Publication is a second write and its failures are usually transient, so it is
    /// retried before the caller is told anything — a blip must not surface as a 503.</summary>
    [Fact]
    public async Task A_transient_publish_failure_is_retried_and_succeeds()
    {
        var (service, store) = RulesetServiceTests.Build(Seed());
        var attempts = 0;
        store.FailPublish = () => attempts++ == 0 ? new IOException("transient") : null;

        var outcome = await service.PutAsync("payments", Onboard(), Pipeline, false, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
        Assert.Equal(2, attempts);
        Assert.Equal(1, store.PublishedCount);
        Assert.Equal(1, store.Writes);
    }

    /// <summary>Retrying the same push republishes, so the documented recovery actually works.</summary>
    [Fact]
    public async Task Retrying_after_a_publish_failure_republishes()
    {
        var (service, store) = RulesetServiceTests.Build(Seed());
        store.FailPublish = () => new IOException("storage unavailable");
        await service.PutAsync("payments", Onboard(), Pipeline, false, default);

        store.FailPublish = null;
        var retried = await service.PutAsync("payments", Onboard(), Pipeline, false, default);

        Assert.True(retried.Succeeded, retried.Error?.Message);
        Assert.Equal("payments", store.PublishedAllowlist!.Modules.Single().Id);
    }

    // ---- Finding 2: acl restatement equality was reference-based -----------------------------

    /// <summary>
    /// The acl arrives deserialized into fresh list instances, so record equality compared them by
    /// reference and refused an unchanged restatement. A desired-state pipeline pushes the acl it
    /// just read on every run, so this made those pushes fail intermittently by construction.
    /// </summary>
    [Fact]
    public async Task An_update_may_restate_an_identical_acl()
    {
        var stored = new Acl { Edit = ["alice"], Push = ["ci"], Admin = [] };
        var (service, _) = RulesetServiceTests.Build(Seed(WithAcl(stored)));

        var outcome = await service.PutAsync("payments", new PushRequest
        {
            // Same content, different instances — exactly what round-tripping through JSON yields.
            Acl = new Acl { Edit = ["alice"], Push = ["ci"], Admin = [] },
            Content = new RulesetContent { AllowedHosts = ["api.stripe.com"], Action = "enforce" },
        }, Pipeline, false, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
    }

    /// <summary>Order is not significant — these are identity sets, not sequences.</summary>
    [Fact]
    public async Task An_update_may_restate_an_identical_acl_in_a_different_order()
    {
        var stored = new Acl { Edit = ["alice", "bob"], Push = [], Admin = [] };
        var (service, _) = RulesetServiceTests.Build(Seed(WithAcl(stored)));

        var outcome = await service.PutAsync("payments", new PushRequest
        {
            Acl = new Acl { Edit = ["bob", "alice"], Push = [], Admin = [] },
            Content = new RulesetContent { AllowedHosts = ["api.stripe.com"], Action = "enforce" },
        }, Pipeline, false, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
    }

    /// <summary>A genuine acl change is still refused: the fix must not weaken the freeze.</summary>
    [Fact]
    public async Task An_update_that_really_changes_the_acl_is_still_rejected()
    {
        var stored = new Acl { Edit = ["alice"], Push = [], Admin = [] };
        var (service, _) = RulesetServiceTests.Build(Seed(WithAcl(stored)));

        var outcome = await service.PutAsync("payments", new PushRequest
        {
            Acl = new Acl { Edit = ["alice", "mallory"], Push = [], Admin = [] },
            Content = new RulesetContent { AllowedHosts = ["api.stripe.com"], Action = "enforce" },
        }, Pipeline, false, default);

        Assert.Equal(HttpStatusCode.BadRequest, outcome.Error!.Status);
        Assert.Contains("frozen", outcome.Error.Message);
    }

    // ---- Finding 3: CIDR validation accepted invalid octets ----------------------------------

    /// <summary>
    /// The proxy parses netids with net.ParseCIDR and silently drops the ones it cannot read, so a
    /// netid the control plane accepts but the proxy rejects produces a ruleset that governs
    /// nothing — the worst kind of failure here, because it looks configured.
    /// </summary>
    [Theory]
    [InlineData("999.999.999.999/24")]
    [InlineData("256.0.0.0/8")]
    [InlineData("10.2.0.300/23")]
    [InlineData("10.2.0.0/33")]
    [InlineData("10.2.0/23")]
    [InlineData("not-a-cidr")]
    public async Task An_out_of_range_netid_is_rejected(string netid)
    {
        var (service, store) = RulesetServiceTests.Build(Seed());

        var outcome = await service.PutAsync("payments", new PushRequest
        {
            Subjects = [new Subject { Netid = netid }],
            Content = new RulesetContent { AllowedHosts = ["api.stripe.com"] },
        }, Pipeline, false, default);

        Assert.Equal(HttpStatusCode.BadRequest, outcome.Error!.Status);
        Assert.Contains("not a CIDR", outcome.Error.Message);
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("10.2.0.0/23")]
    [InlineData("0.0.0.0/0")]
    [InlineData("255.255.255.255/32")]
    [InlineData("192.168.1.64/26")]
    public async Task A_valid_netid_is_accepted(string netid)
    {
        var (service, _) = RulesetServiceTests.Build(Seed());

        var outcome = await service.PutAsync("payments", new PushRequest
        {
            Subjects = [new Subject { Netid = netid }],
            Content = new RulesetContent { AllowedHosts = ["api.stripe.com"] },
        }, Pipeline, false, default);

        Assert.True(outcome.Succeeded, outcome.Error?.Message);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static StateDocument Seed(params Ruleset[] rulesets) => new()
    {
        Rulesets = [.. rulesets],
        Grants =
        [
            new Grant { Identity = Pipeline, Verbs = [Verbs.Onboard, Verbs.Update, Verbs.Offboard] },
        ],
    };

    private static Ruleset WithAcl(Acl acl) => new()
    {
        Name = "payments",
        Subjects = [new Subject { Appid = Workload }],
        Content = new RulesetContent { AllowedHosts = ["api.stripe.com"], Action = "enforce" },
        Acl = acl,
        Owner = Pipeline,
    };

    private static PushRequest Onboard() => new()
    {
        Subjects = [new Subject { Appid = Workload }],
        Content = new RulesetContent { AllowedHosts = ["api.stripe.com"] },
    };
}
