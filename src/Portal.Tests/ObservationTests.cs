using Portal.Clients;
using Portal.Components;

namespace Portal.Tests;

/// <summary>
/// The "worth a look" panel. Its content is judgement rendered as copy, so what it does and does
/// not say is worth pinning down.
/// </summary>
public class ObservationTests
{
    private const string Payments = "11111111-1111-1111-1111-111111111111";
    private const string Stranger = "99999999-9999-9999-9999-999999999999";

    /// <summary>
    /// Time in report is NOT a signal. A ruleset can sit in report indefinitely as a legitimate
    /// steady state, so a promotion prompt leads with hosts observed off-list — and a report-mode
    /// ruleset with nothing off-list produces no row at all, however old it is.
    /// </summary>
    [Fact]
    public void A_report_ruleset_with_nothing_off_list_is_not_flagged()
    {
        var policy = Snapshot(Ruleset("checkout", RulesetAction.Report, Payments));

        var rows = Observations.From(policy, [], [], []);

        Assert.DoesNotContain(rows, o => o.Subject == "checkout");
    }

    [Fact]
    public void A_report_ruleset_is_flagged_by_what_it_still_needs()
    {
        var policy = Snapshot(Ruleset("checkout", RulesetAction.Report, Payments));
        var findings = new[]
        {
            new ReportFinding(Payments, "api.vendor.com", 12, DateTimeOffset.UtcNow),
            new ReportFinding(Payments, "cdn.vendor.com", 3, DateTimeOffset.UtcNow),
        };

        var row = Assert.Single(Observations.From(policy, [], findings, []), o => o.Subject == "checkout");

        Assert.Contains("2 host(s) observed off-list", row.Headline, StringComparison.Ordinal);
        // Copy from the operator's side, and honest about what report mode actually does.
        Assert.Contains("permits everything", row.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A denial from a subject no ruleset governs is attributed to the fallback, never to a
    /// ruleset that does not govern it. That misattribution would be exactly the wrong answer to
    /// the question the console exists to answer.
    /// </summary>
    [Fact]
    public void A_denial_from_an_ungoverned_subject_is_attributed_to_the_floor()
    {
        var policy = Snapshot(Ruleset("payments", RulesetAction.Enforce, Payments));

        var rows = Observations.From(policy, [Denial(Stranger, "api.vendor.com")], [], []);

        var row = Assert.Single(rows, o => o.Headline.Contains("matches no ruleset", StringComparison.Ordinal));
        Assert.Equal(Stranger, row.Subject);
        Assert.Contains("deny-all floor", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("payments", row.Detail, StringComparison.Ordinal);
    }

    /// <summary>One destination taking most of the denials is one missing rule or one misconfigured
    /// workload, which is worth saying rather than leaving in a table.</summary>
    [Fact]
    public void A_concentrated_destination_is_called_out_against_its_ruleset()
    {
        var policy = Snapshot(Ruleset("payments", RulesetAction.Enforce, Payments));
        var denials = Enumerable.Repeat(Denial(Payments, "api.vendor.com"), 8)
            .Concat([Denial(Payments, "other.example.com"), Denial(Payments, "third.example.com")])
            .ToList();

        var row = Assert.Single(Observations.From(policy, denials, [], []),
            o => o.Headline.Contains("api.vendor.com", StringComparison.Ordinal));

        Assert.Equal("payments", row.Subject);
        Assert.Equal(ObservationLevel.Prominent, row.Level);
    }

    /// <summary>Evenly spread denials are just traffic — the panel stays quiet rather than
    /// promoting whichever host happened to come first.</summary>
    [Fact]
    public void Evenly_spread_denials_are_not_called_out()
    {
        var policy = Snapshot(Ruleset("payments", RulesetAction.Enforce, Payments));
        var denials = Enumerable.Range(0, 10).Select(i => Denial(Payments, $"host{i}.example.com")).ToList();

        Assert.DoesNotContain(Observations.From(policy, denials, [], []),
            o => o.Headline.Contains("denials to", StringComparison.Ordinal));
    }

    /// <summary>
    /// A source challenged that never authenticated is what probing looks like — every
    /// authenticated connection produces exactly one credential-less CONNECT first.
    /// </summary>
    [Fact]
    public void Challenges_that_never_convert_are_reported_as_probing()
    {
        var challenges = new[]
        {
            new ChallengeConversion("10.0.0.5", 400, 0),
            new ChallengeConversion("10.0.0.6", 12, 12),
        };

        var row = Assert.Single(Observations.From(Snapshot(), [], [], challenges),
            o => o.Headline.Contains("never authenticated", StringComparison.Ordinal));

        Assert.Equal(ObservationLevel.Prominent, row.Level);
        Assert.Contains("400", row.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Nothing is wrong" is itself worth a row. A panel that only ever speaks up when something
    /// is off teaches an operator to read an empty panel as "not loaded".
    /// </summary>
    [Fact]
    public void A_healthy_window_still_says_something()
    {
        var rows = Observations.From(Snapshot(), [], [], [new ChallengeConversion("10.0.0.6", 12, 12)]);

        var row = Assert.Single(rows);
        Assert.Equal(ObservationLevel.Fine, row.Level);
        Assert.Equal("No probing detected", row.Headline);
    }

    /// <summary>An open ruleset is named as one whose host list is not consulted at all — the
    /// state, not a scolding.</summary>
    [Fact]
    public void An_open_ruleset_is_reported_for_what_it_means()
    {
        var policy = Snapshot(Ruleset("vendor-bridge", RulesetAction.Open, Payments));

        var row = Assert.Single(Observations.From(policy, [], [], []), o => o.Subject == "vendor-bridge");

        Assert.Equal("action is open", row.Headline);
        Assert.Contains("host list is not consulted", row.Detail, StringComparison.Ordinal);
    }

    /// <summary>Most prominent first, so the panel is read top-down.</summary>
    [Fact]
    public void Rows_are_ordered_by_prominence()
    {
        var policy = Snapshot(
            Ruleset("payments", RulesetAction.Enforce, Payments),
            Ruleset("vendor-bridge", RulesetAction.Open, "22222222-2222-2222-2222-222222222222"));

        var rows = Observations.From(policy, [Denial(Stranger, "a.example.com")], [], []);

        Assert.Equal(rows.OrderByDescending(o => o.Level).Select(o => o.Headline), rows.Select(o => o.Headline));
    }

    private static DecisionRow Denial(string role, string host) =>
        new(DateTimeOffset.UtcNow, role, "10.0.0.4", host, false, "host not in allowlist", false, "req-1");

    private static RulesetView Ruleset(string name, RulesetAction action, string appid) =>
        new(name, [new SubjectView(appid, null)], ["api.example.com"], action, "owner");

    private static PolicySnapshot Snapshot(params RulesetView[] rulesets) => new(
        rulesets, [], new FallbackView([], true), new Recency(null, null), Freshness.Now);
}
