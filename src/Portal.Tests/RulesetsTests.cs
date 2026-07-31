using Portal.Clients;
using Portal.Pages;

namespace Portal.Tests;

/// <summary>
/// The Rulesets surface. What is asserted here is the handful of places where the surface makes a
/// decision rather than rendering one — the identity join, the candidate host set, and the two
/// rules a reviewer would otherwise have to re-derive from the mockup.
/// </summary>
public class RulesetsTests
{
    // ---- the join is on validated identity, never on a source address ---------------------------

    /// <summary>
    /// Only <c>appid</c> subjects join to traffic. A <c>netid</c> subject correlates on a source
    /// address, which the repo is emphatic is not an identity — so it is left out of the join
    /// entirely and said out loud in the UI, rather than being quietly treated as the same thing.
    /// </summary>
    [Fact]
    public void Only_validated_identities_take_part_in_the_traffic_join()
    {
        var ruleset = Ruleset("mixed", RulesetAction.Enforce,
            new SubjectView("11111111-1111-1111-1111-111111111111", null),
            new SubjectView(null, "10.2.0.0/23"));

        Assert.Equal(["11111111-1111-1111-1111-111111111111"], RulesetsModel.Appids(ruleset));
    }

    [Fact]
    public void A_purely_network_ruleset_contributes_no_join_key() =>
        Assert.Empty(RulesetsModel.Appids(
            Ruleset("legacy", RulesetAction.Report, new SubjectView(null, "10.9.0.0/24"))));

    // ---- the candidate host set -----------------------------------------------------------------

    /// <summary>
    /// A pasted list is read as forgivingly as the file it came from allows. Nothing here
    /// validates: the control plane does that, and a second opinion in the console would
    /// eventually disagree with the service that actually decides.
    /// </summary>
    [Theory]
    [InlineData("a.example.com\nb.example.com", new[] { "a.example.com", "b.example.com" })]
    [InlineData("a.example.com, b.example.com", new[] { "a.example.com", "b.example.com" })]
    [InlineData("  a.example.com  \r\n\r\n b.example.com ", new[] { "a.example.com", "b.example.com" })]
    [InlineData("a.example.com\nA.EXAMPLE.COM", new[] { "a.example.com" })]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void A_typed_host_list_is_read_forgivingly(string? typed, string[] expected) =>
        Assert.Equal(expected, RulesetsModel.ParseHosts(typed));

    /// <summary>Drafting the same host twice must not propose a duplicate, and must not disturb
    /// the order of a list the operator is reading.</summary>
    [Fact]
    public void Drafting_a_host_already_on_the_list_changes_nothing()
    {
        string[] hosts = ["api.stripe.com", "files.stripe.com"];

        Assert.Equal(hosts, RulesetsModel.Merge(hosts, "API.STRIPE.COM"));
        Assert.Equal(["api.stripe.com", "files.stripe.com", "api.vendor.com"],
            RulesetsModel.Merge(hosts, "api.vendor.com"));
    }

    /// <summary>
    /// An audit row's host carries the CONNECT port; an allowlist entry is the name alone. Crossing
    /// that gap here rather than in the operator's head is the difference between a snippet that
    /// applies and one the control plane rejects.
    /// </summary>
    [Theory]
    [InlineData("api.vendor.com:443", "api.vendor.com")]
    [InlineData("api.vendor.com", "api.vendor.com")]
    [InlineData("api.vendor.com:notaport", "api.vendor.com:notaport")]
    public void An_observed_host_loses_its_port_when_drafted(string observed, string drafted) =>
        Assert.Equal(drafted, RulesetsModel.WithoutPort(observed));

    // ---- what the list column means -------------------------------------------------------------

    /// <summary>
    /// Open does not consult the allowlist at all, so a count there would imply the list is doing
    /// something. The mockup shows a dash; this keeps it that way.
    /// </summary>
    [Fact]
    public void An_open_ruleset_shows_no_host_count()
    {
        Assert.Equal("—", Row(Ruleset("vendor-bridge", RulesetAction.Open, Appid())).HostCount);
        Assert.Equal("1", Row(Ruleset("payments", RulesetAction.Enforce, Appid())).HostCount);
    }

    [Theory]
    [InlineData("appid", "1 appid")]
    [InlineData("netid", "1 netid")]
    public void The_subject_column_says_which_kind_of_subject_it_is(string kind, string expected)
    {
        var subject = kind == "appid" ? Appid() : new SubjectView(null, "10.2.0.0/23");

        Assert.Equal(expected, Row(Ruleset("r", RulesetAction.Enforce, subject)).Subjects);
    }

    // ---- the two rules the mockup carries and a rewrite could lose ------------------------------

    /// <summary>
    /// 7.1: the list grows with the estate, so it scrolls in place under a sticky header and the
    /// detail panel below it stays reachable without scrolling the page.
    /// </summary>
    [Fact]
    public void The_ruleset_list_scrolls_in_place() =>
        Assert.Contains("tablewrap scroll",
            Repo.ReadText("src/Portal/Pages/Shared/_RulesetList.cshtml"), StringComparison.Ordinal);

    /// <summary>
    /// A push is a full replace, so a sandbox that rendered only the additions would misrepresent
    /// what its own snippet does. All four lists are rendered, and the two that take something
    /// away are the ones a rewrite is most likely to drop.
    /// </summary>
    [Theory]
    [InlineData("check.Added")]
    [InlineData("check.Removed")]
    [InlineData("check.Bound")]
    [InlineData("check.Unbound")]
    public void The_dry_run_renders_every_half_of_the_diff(string half) =>
        Assert.Contains(half,
            Repo.ReadText("src/Portal/Pages/Shared/_RulesetCheck.cshtml"), StringComparison.Ordinal);

    /// <summary>
    /// D3: the snippet is the point of the sandbox, and it names the pipeline as the source of
    /// truth rather than presenting the console as a way to apply policy.
    /// </summary>
    [Fact]
    public void The_snippet_names_the_pipeline_as_the_source_of_truth()
    {
        var template = Repo.ReadText("src/Portal/Pages/Shared/_RulesetCheck.cshtml");

        Assert.Contains("source of truth", template, StringComparison.Ordinal);
        Assert.Contains("curl -X PUT", template, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static SubjectView Appid() => new("11111111-1111-1111-1111-111111111111", null);

    private static RulesetView Ruleset(string name, RulesetAction action, params SubjectView[] subjects) =>
        new(name, subjects, ["api.example.com"], action, "22222222-2222-2222-2222-222222222222");

    private static RulesetRow Row(RulesetView ruleset) => new(ruleset, 0, 0);
}
