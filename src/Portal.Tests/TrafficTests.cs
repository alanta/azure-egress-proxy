using Portal.Clients;
using Portal.Pages;

namespace Portal.Tests;

/// <summary>
/// The denial → owning ruleset join, which is the thing the console exists to do and the thing it
/// would be most damaging to get subtly wrong. A misattributed denial sends an operator to widen a
/// ruleset that does not govern the traffic — a policy change made for the wrong reason, on a
/// security control.
/// </summary>
public class TrafficTests
{
    private const string Payments = "11111111-1111-1111-1111-111111111111";
    private const string Stranger = "99999999-9999-9999-9999-999999999999";

    // ---- 8.2, the identity join ---------------------------------------------------------------

    /// <summary>The strong join: <c>Role</c> is the appid from the validated JWT, and
    /// <c>subjects[].appid</c> is the same value as authored.</summary>
    [Fact]
    public void A_denial_is_joined_to_its_ruleset_on_the_validated_appid()
    {
        var policy = Snapshot(AppidRuleset("payments", Payments));

        var denial = Assert.Single(TrafficModel.Attribute(policy, [Denial(Payments, "10.2.0.11")]));

        Assert.Equal(DenialAttribution.Identity, denial.Attribution);
        Assert.Equal("payments", denial.Ruleset?.Name);
    }

    /// <summary>Case-insensitively, because a GUID's casing is not part of its identity and an
    /// operator's paste is not evidence about which ruleset governs anything.</summary>
    [Fact]
    public void The_appid_join_ignores_case()
    {
        var policy = Snapshot(AppidRuleset("payments", Payments.ToUpperInvariant()));

        var denial = Assert.Single(TrafficModel.Attribute(policy, [Denial(Payments, "10.2.0.11")]));

        Assert.Equal(DenialAttribution.Identity, denial.Attribution);
    }

    // ---- 8.2, the source-address fallback -----------------------------------------------------

    /// <summary>
    /// A netid-mode row carries no appid to join on, so the source address is the only key there
    /// is. It resolves — and it is marked as the weaker join, which the row's rendering depends on.
    /// </summary>
    [Fact]
    public void A_row_with_no_appid_falls_back_to_the_source_address()
    {
        var policy = Snapshot(NetidRuleset("legacy-batch", "10.2.0.0/24"));

        var denial = Assert.Single(TrafficModel.Attribute(policy, [Denial(null, "10.2.0.14")]));

        Assert.Equal(DenialAttribution.Network, denial.Attribution);
        Assert.Equal("legacy-batch", denial.Ruleset?.Name);
    }

    [Fact]
    public void A_source_address_outside_every_netid_falls_to_the_fallback()
    {
        var policy = Snapshot(NetidRuleset("legacy-batch", "10.2.0.0/24"));

        var denial = Assert.Single(TrafficModel.Attribute(policy, [Denial(null, "10.9.9.9")]));

        Assert.Equal(DenialAttribution.Fallback, denial.Attribution);
        Assert.Null(denial.Ruleset);
    }

    /// <summary>
    /// The rule that keeps a source-address correlation from overruling a validated identity. A row
    /// that carries an appid was produced by a deployment keying on identity; if that appid matches
    /// no ruleset the subject is genuinely unmatched, whatever CIDR its address happens to sit in.
    /// Attributing it to a netid ruleset would be exactly the misattribution this surface exists to
    /// prevent — and would state a source address as an identity.
    /// </summary>
    [Fact]
    public void An_unknown_appid_is_not_rescued_by_a_matching_netid()
    {
        var policy = Snapshot(NetidRuleset("legacy-batch", "10.2.0.0/24"));

        var denial = Assert.Single(TrafficModel.Attribute(policy, [Denial(Stranger, "10.2.0.14")]));

        Assert.Equal(DenialAttribution.Fallback, denial.Attribution);
        Assert.Null(denial.Ruleset);
    }

    /// <summary>The identity join wins when both could apply. It is the stronger statement.</summary>
    [Fact]
    public void The_appid_join_takes_precedence_over_the_address()
    {
        var policy = Snapshot(
            NetidRuleset("legacy-batch", "10.2.0.0/24"),
            AppidRuleset("payments", Payments));

        var denial = Assert.Single(TrafficModel.Attribute(policy, [Denial(Payments, "10.2.0.14")]));

        Assert.Equal(DenialAttribution.Identity, denial.Attribution);
        Assert.Equal("payments", denial.Ruleset?.Name);
    }

    [Theory]
    [InlineData("10.2.0.0/24", "10.2.0.255", true)]
    [InlineData("10.2.0.0/24", "10.2.1.0", false)]
    [InlineData("10.2.0.0/16", "10.2.250.7", true)]
    [InlineData("10.2.0.8/29", "10.2.0.15", true)]
    [InlineData("10.2.0.8/29", "10.2.0.16", false)]
    [InlineData("0.0.0.0/0", "203.0.113.9", true)]
    [InlineData("10.2.0.0/32", "10.2.0.0", true)]
    [InlineData("nonsense", "10.2.0.1", false)]
    public void The_address_fallback_respects_the_prefix(string cidr, string source, bool matches)
    {
        var policy = Snapshot(NetidRuleset("infra", cidr));

        var denial = Assert.Single(TrafficModel.Attribute(policy, [Denial(null, source)]));

        Assert.Equal(matches ? DenialAttribution.Network : DenialAttribution.Fallback, denial.Attribution);
    }

    // ---- 8.3, the fallback --------------------------------------------------------------------

    /// <summary>
    /// <c>Governing</c> returning null means the platform floor applies. The console must say the
    /// fallback governs the row and must not reach for the nearest ruleset instead.
    /// </summary>
    [Fact]
    public void A_subject_no_ruleset_governs_is_attributed_to_the_fallback()
    {
        var policy = Snapshot(AppidRuleset("payments", Payments));

        var denial = Assert.Single(TrafficModel.Attribute(policy, [Denial(Stranger, "10.2.1.8")]));

        Assert.Equal(DenialAttribution.Fallback, denial.Attribution);
        Assert.Null(denial.Ruleset);
    }

    /// <summary>With no policy readable, nothing is guessed at: every row reports as unresolved
    /// rather than being attributed to a ruleset the console could not see.</summary>
    [Fact]
    public void Without_policy_no_row_is_attributed_to_a_ruleset()
    {
        var denials = TrafficModel.Attribute(null, [Denial(Payments, "10.2.0.11")]);

        Assert.All(denials, denial => Assert.Null(denial.Ruleset));
    }

    /// <summary>Every row keeps its source address, whichever way it was attributed. It is context
    /// an operator needs on all of them — and an identity on none of them.</summary>
    [Fact]
    public void Every_row_keeps_its_source_address()
    {
        var policy = Snapshot(AppidRuleset("payments", Payments));

        var denials = TrafficModel.Attribute(policy,
            [Denial(Payments, "10.2.0.11"), Denial(Stranger, "10.2.1.8"), Denial(null, "10.2.0.14")]);

        Assert.All(denials, denial => Assert.False(string.IsNullOrEmpty(denial.Row.SourceIp)));
    }

    // ---- 8.4, filters -------------------------------------------------------------------------

    /// <summary>
    /// Filters travel as real query-string values, which is what makes them shareable — and is why
    /// they need no <c>hx-vals='js:…'</c>, which the portal's CSP would refuse anyway.
    /// </summary>
    [Fact]
    public void A_filter_url_carries_the_window_and_the_filters()
    {
        var page = new TrafficModel(null!, null!);

        var url = page.FilterUrl("DenialsPanel", TrafficWindow.LastWeek, "payments", "api.vendor.com");

        Assert.Equal(
            "/Traffic?handler=DenialsPanel&window=7d&subject=payments&host=api.vendor.com", url);
    }

    /// <summary>An empty filter drops out of the URL rather than being pushed as an empty
    /// parameter, so "clear" produces the address an unfiltered view would have had.</summary>
    [Fact]
    public void An_empty_filter_leaves_the_url()
    {
        var page = new TrafficModel(null!, null!);

        Assert.Equal("/Traffic?window=24h", page.FilterUrl(subject: "", host: ""));
    }

    /// <summary>A value that would change the meaning of the URL is encoded, not interpolated.</summary>
    [Fact]
    public void A_filter_value_is_encoded()
    {
        var page = new TrafficModel(null!, null!);

        Assert.DoesNotContain("&window=1h&extra", page.FilterUrl(subject: "a&window=1h&extra"),
            StringComparison.Ordinal);
    }

    // ---- fixtures -----------------------------------------------------------------------------

    private static DecisionRow Denial(string? role, string sourceIp) => new(
        DateTimeOffset.UtcNow, role, sourceIp, "api.vendor.com:443", false,
        "Not in the allowed host list", false, "req-1");

    private static RulesetView AppidRuleset(string name, string appid) =>
        new(name, [new SubjectView(appid, null)], ["api.example.com"], RulesetAction.Enforce, null);

    private static RulesetView NetidRuleset(string name, string netid) =>
        new(name, [new SubjectView(null, netid)], ["api.example.com"], RulesetAction.Enforce, null);

    private static PolicySnapshot Snapshot(params RulesetView[] rulesets) =>
        new(rulesets, [], new FallbackView([], DenyAll: true), new Recency(null, null), Freshness.Now);
}
