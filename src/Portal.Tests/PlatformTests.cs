using Portal.Clients;
using Portal.Components;
using Portal.Pages;

namespace Portal.Tests;

/// <summary>
/// The Platform surface, where the two things that can go wrong are both understatements: a grant
/// that reaches every ruleset rendered as though it reached none, and a configuration view that
/// omits the deny-all floor. Neither is visible in a screenshot review — an empty cell looks like
/// an empty scope — so they are pinned here.
/// </summary>
public class PlatformTests
{
    /// <summary>
    /// Null <c>rulesets</c> means <b>every</b> ruleset, not none. This is the assertion the whole
    /// file exists for: a platform-team grant rendered as a blank scope would understate authority,
    /// which is the one direction a security console must never err in.
    /// </summary>
    [Fact]
    public void An_unscoped_grant_reads_as_every_ruleset()
    {
        var grant = new GrantView("9999", ["onboard", "update", "bind", "offboard"], null, null);

        Assert.True(grant.IsUnscoped);
        Assert.Equal("Every ruleset", PlatformModel.DescribeScope(grant));
    }

    /// <summary>Belt and braces on the same property, stated as the failure rather than the fix:
    /// whatever the wording becomes, an unscoped grant must never render as nothing.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("no ruleset")]
    [InlineData("—")]
    public void An_unscoped_grant_never_reads_as_an_empty_scope(string understatement)
    {
        var scope = PlatformModel.DescribeScope(new GrantView("9999", ["update"], null, null));

        Assert.NotEqual(understatement, scope, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A present-but-empty list is the opposite case and reaches nothing. Distinguishing
    /// the two is the whole reason <c>Rulesets</c> is nullable rather than merely empty.</summary>
    [Fact]
    public void A_grant_scoped_to_no_ruleset_says_so()
    {
        var grant = new GrantView("2222", ["update"], [], null);

        Assert.False(grant.IsUnscoped);
        Assert.Equal("No ruleset", PlatformModel.DescribeScope(grant));
        Assert.False(PlatformModel.ScopeIsNamed(grant));
    }

    [Fact]
    public void A_scoped_grant_names_its_rulesets()
    {
        var grant = new GrantView("2222", ["update", "offboard"], ["sample-app", "payments"], null);

        Assert.Equal("sample-app, payments", PlatformModel.DescribeScope(grant));

        // Ruleset names are identifiers, so the cell renders mono — the prose answers do not.
        Assert.True(PlatformModel.ScopeIsNamed(grant));
    }

    [Theory]
    [InlineData("onboard", "Onboard")]
    [InlineData("offboard", "Offboard")]
    [InlineData(" bind ", "Bind")]
    [InlineData("", "(empty)")]
    public void A_verb_renders_as_a_word(string wire, string expected) =>
        Assert.Equal(expected, PlatformModel.DescribeVerb(wire));

    /// <summary>
    /// 10.3 — the floor comes from the API, not from the host list being empty. A fallback that the
    /// API reported as deny-all reads as deny-all, and one that is not stays legible as a host
    /// count, so the two are never conflated.
    /// </summary>
    [Fact]
    public void The_deny_all_floor_is_rendered_from_what_the_api_said()
    {
        Assert.Equal("Deny-all", PillModel.For(new FallbackView([], DenyAll: true)).Text);
        Assert.Equal("1 host(s)", PillModel.For(new FallbackView(["status.example.com"], DenyAll: false)).Text);
    }

    /// <summary>
    /// 10.2, asserted against the template rather than trusted to review. The surface must say, in
    /// place, that authority is granted outside the portal and that the API has no write path for
    /// it — without that sentence the grants table reads as an admin screen somebody could edit.
    /// </summary>
    [Theory]
    [InlineData("granted outside this portal")]
    [InlineData("no write path")]
    public void The_authority_card_states_that_the_portal_does_not_grant_authority(string phrase) =>
        Assert.Contains(phrase, Repo.ReadText("src/Portal/Pages/Shared/_PlatformAuthority.cshtml"),
            StringComparison.Ordinal);

    /// <summary>The baseline card is not optional decoration: a configuration view that omits the
    /// floor misleads, so the page must render it.</summary>
    [Fact]
    public void The_surface_renders_the_fallback_alongside_the_grants()
    {
        var page = Repo.ReadText("src/Portal/Pages/Platform.cshtml");

        Assert.Contains("_PlatformAuthority", page, StringComparison.Ordinal);
        Assert.Contains("_PlatformBaseline", page, StringComparison.Ordinal);
    }
}
