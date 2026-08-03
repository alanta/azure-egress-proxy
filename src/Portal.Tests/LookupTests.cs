using Portal.Clients;
using Portal.Pages;

namespace Portal.Tests;

/// <summary>
/// The two joins the Lookup surface makes. Both are answers an operator acts on, and both have a
/// wrong version that looks perfectly reasonable on screen — attributing an ungoverned subject to
/// a ruleset, or omitting a ruleset that reaches a host without listing it.
/// </summary>
public class LookupTests
{
    private const string Payments = "11111111-1111-1111-1111-111111111111";
    private const string Sibling = "22222222-2222-2222-2222-222222222222";
    private const string Stranger = "99999999-9999-9999-9999-999999999999";

    // ---- classification ------------------------------------------------------------------------

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", LookupKind.Appid)]
    [InlineData("  11111111-1111-1111-1111-111111111111  ", LookupKind.Appid)]
    [InlineData("10.1.0.0/23", LookupKind.Netid)]
    [InlineData("10.1.0.7", LookupKind.Netid)]
    [InlineData("api.stripe.com", LookupKind.Host)]
    [InlineData("", LookupKind.Empty)]
    [InlineData("   ", LookupKind.Empty)]
    public void The_query_is_classified_by_its_shape(string query, LookupKind expected) =>
        Assert.Equal(expected, Lookups.Classify(query));

    // ---- 9.1 resolution ------------------------------------------------------------------------

    [Fact]
    public void An_appid_resolves_to_the_ruleset_that_lists_it()
    {
        var policy = Snapshot(Ruleset("payments", RulesetAction.Enforce, Payments, Sibling));

        var resolution = Lookups.Resolve(policy, Payments, LookupKind.Appid);

        Assert.Equal("payments", resolution.Ruleset?.Name);
        Assert.False(resolution.FallsToFallback);
        // The blast radius of a change to the ruleset, which is the next question after "which one".
        Assert.Equal([Sibling], resolution.SharesWith);
    }

    /// <summary>
    /// A subject on no ruleset falls to the fallback. It must not be attributed to a ruleset that
    /// does not govern it, and "no match" must not be able to read as "no policy" — the deny-all
    /// floor still applies to it.
    /// </summary>
    [Fact]
    public void An_appid_on_no_ruleset_falls_to_the_fallback()
    {
        var policy = Snapshot(Ruleset("payments", RulesetAction.Enforce, Payments));

        var resolution = Lookups.Resolve(policy, Stranger, LookupKind.Appid);

        Assert.True(resolution.FallsToFallback);
        Assert.Null(resolution.Ruleset);
        Assert.Empty(resolution.SharesWith);
    }

    /// <summary>
    /// A netid subject is matched by source address. The resolution says so in its own words rather
    /// than presenting the match as though it came from a validated claim — a source address is not
    /// an identity, and the console must not imply that it is.
    /// </summary>
    [Fact]
    public void A_source_address_inside_a_netid_subject_matches_and_says_how()
    {
        var policy = Snapshot(Network("legacy-vms", RulesetAction.Enforce, "10.1.0.0/23"));

        var resolution = Lookups.Resolve(policy, "10.1.1.9", LookupKind.Netid);

        Assert.Equal("legacy-vms", resolution.Ruleset?.Name);
        Assert.Equal(LookupKind.Netid, resolution.Kind);
        Assert.Contains("source address", resolution.Match, StringComparison.Ordinal);
        Assert.Contains("not by a validated claim", resolution.Match, StringComparison.Ordinal);
    }

    [Fact]
    public void The_netid_subject_itself_matches_when_pasted_back_in()
    {
        var policy = Snapshot(Network("legacy-vms", RulesetAction.Enforce, "10.1.0.0/23"));

        Assert.Equal("legacy-vms", Lookups.Resolve(policy, "10.1.0.0/23", LookupKind.Netid).Ruleset?.Name);
    }

    [Fact]
    public void An_address_outside_every_netid_subject_falls_to_the_fallback()
    {
        var policy = Snapshot(Network("legacy-vms", RulesetAction.Enforce, "10.1.0.0/23"));

        var resolution = Lookups.Resolve(policy, "10.9.9.9", LookupKind.Netid);

        Assert.True(resolution.FallsToFallback);
    }

    /// <summary>An appid query never matches a netid subject, or the console would join an identity
    /// to a subnet.</summary>
    [Fact]
    public void An_appid_query_does_not_match_a_network_subject()
    {
        var policy = Snapshot(Network("legacy-vms", RulesetAction.Enforce, "10.1.0.0/23"));

        Assert.True(Lookups.Resolve(policy, Payments, LookupKind.Appid).FallsToFallback);
    }

    // ---- 9.2 reverse index ---------------------------------------------------------------------

    [Fact]
    public void A_ruleset_that_lists_the_host_is_listed_for_the_host()
    {
        var policy = Snapshot(Ruleset("payments", RulesetAction.Enforce, Payments));

        var match = Assert.Single(Lookups.Reverse(policy, "api.example.com"));

        Assert.Equal("payments", match.Ruleset.Name);
        Assert.Equal(ReverseReason.Listed, match.Reason);
    }

    [Fact]
    public void An_enforcing_ruleset_without_the_host_is_not_listed() =>
        Assert.Empty(Lookups.Reverse(
            Snapshot(Ruleset("payments", RulesetAction.Enforce, Payments)), "api.vendor.com"));

    /// <summary>
    /// An open ruleset reaches the host and every other host. It belongs in the index — leaving it
    /// out would tell an operator that fewer workloads can reach the host than actually can — but
    /// it is marked as reaching everything, because listing it as though this host had been granted
    /// to it would overstate how deliberate the reach is.
    /// </summary>
    [Fact]
    public void An_open_ruleset_is_listed_for_the_action_and_not_for_the_host()
    {
        var policy = Snapshot(Ruleset("vendor-bridge", RulesetAction.Open, Payments));

        var match = Assert.Single(Lookups.Reverse(policy, "anything.at.all"));

        Assert.Equal(ReverseReason.Open, match.Reason);
        Assert.Contains("reaches every host", match.Why, StringComparison.Ordinal);
    }

    /// <summary>Report permits off-list hosts and logs them, so it reaches the host too — for the
    /// same reason, and marked the same way.</summary>
    [Fact]
    public void A_report_ruleset_without_the_host_is_listed_for_the_action()
    {
        var policy = Snapshot(Ruleset("checkout", RulesetAction.Report, Payments));

        var match = Assert.Single(Lookups.Reverse(policy, "api.vendor.com"));

        Assert.Equal(ReverseReason.Report, match.Reason);
        Assert.Contains("permitted and logged", match.Why, StringComparison.Ordinal);
    }

    /// <summary>An open or report ruleset that does list the host is listed for the host: the
    /// deliberate grant is the more informative of the two reasons.</summary>
    [Fact]
    public void Listing_the_host_outranks_the_action_as_the_reason()
    {
        var policy = Snapshot(Ruleset("checkout", RulesetAction.Report, Payments));

        Assert.Equal(ReverseReason.Listed, Assert.Single(Lookups.Reverse(policy, "api.example.com")).Reason);
    }

    /// <summary>Deliberate grants first, so the table reads as "who was given this, then who gets
    /// it anyway".</summary>
    [Fact]
    public void Deliberate_grants_are_ordered_before_the_ones_that_reach_it_anyway()
    {
        var policy = Snapshot(
            Ruleset("vendor-bridge", RulesetAction.Open, Sibling),
            Ruleset("payments", RulesetAction.Enforce, Payments));

        Assert.Equal(["payments", "vendor-bridge"],
            Lookups.Reverse(policy, "api.example.com").Select(m => m.Ruleset.Name));
    }

    [Fact]
    public void Host_matching_ignores_case()
    {
        var policy = Snapshot(Ruleset("payments", RulesetAction.Enforce, Payments));

        Assert.Single(Lookups.Reverse(policy, Lookups.Normalize("API.Example.COM", LookupKind.Host)));
    }

    /// <summary>The floor is part of the answer to "who can reach this host?" — a reverse index
    /// that only counted rulesets would omit everything the fallback governs.</summary>
    [Fact]
    public void The_fallback_reaches_only_what_it_lists()
    {
        Assert.False(Lookups.FallbackReaches(new FallbackView([], true), "api.example.com"));
        Assert.False(Lookups.FallbackReaches(
            new FallbackView(["login.microsoftonline.com"], false), "api.example.com"));
        Assert.True(Lookups.FallbackReaches(
            new FallbackView(["login.microsoftonline.com"], false), "login.microsoftonline.com"));
    }

    // ---- fixtures ------------------------------------------------------------------------------

    private static RulesetView Ruleset(string name, RulesetAction action, params string[] appids) =>
        new(name, [.. appids.Select(a => new SubjectView(a, null))], ["api.example.com"], action, "owner");

    private static RulesetView Network(string name, RulesetAction action, string netid) =>
        new(name, [new SubjectView(null, netid)], ["api.example.com"], action, "owner");

    private static PolicySnapshot Snapshot(params RulesetView[] rulesets) => new(
        rulesets, [], new FallbackView([], true), new Recency(null, null), Freshness.Now);
}
