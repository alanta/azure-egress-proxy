using Portal.Clients;

namespace Portal.Components;

/// <summary>How prominently an observation reads. Not a severity in the alerting sense.</summary>
public enum ObservationLevel
{
    /// <summary>Nothing is wrong, and saying so is worth a row — "no probing detected" is
    /// information, and a panel that only ever speaks up when something is off teaches an
    /// operator to read an empty panel as "not loaded".</summary>
    Fine,

    /// <summary>Worth a look when there is time.</summary>
    Notable,

    /// <summary>Worth a look now.</summary>
    Prominent,
}

/// <param name="Subject">The ruleset or source the row is about, rendered as the row's name.</param>
/// <param name="Headline">One line. The finding.</param>
/// <param name="Detail">One line. Why it might matter, and what it is not.</param>
/// <param name="Surface">Where "inspect" goes.</param>
public sealed record Observation(
    ObservationLevel Level,
    string? Subject,
    string Headline,
    string Detail,
    Surface Surface)
{
    public string LevelClass => Level switch
    {
        ObservationLevel.Prominent => "sev hi",
        ObservationLevel.Notable => "sev md",
        _ => "sev ok",
    };
}

/// <summary>
/// The "worth a look" panel's content.
///
/// <para><b>These are observations, not alerts.</b> The console does not page anyone and does not
/// have a notion of "wrong". It reports what the data says and leaves the judgement to the
/// operator — which is why every row carries a sentence about what the finding is *not*.</para>
///
/// <para>Two rules the copy must keep, both from design.md D9:</para>
/// <list type="bullet">
/// <item><b>Time in <c>report</c> is not a signal.</b> A ruleset can sit in <c>report</c>
/// indefinitely and that is a legitimate steady state, not rot. A promotion prompt therefore
/// leads with <i>hosts observed off-list</i>; last-modified is context and never a nudge. Nothing
/// below looks at how long anything has been anywhere.</item>
/// <item><b>Copy is written from the operator's side</b>, not the system's — "traffic matching no
/// ruleset" rather than "the fallback block".</item>
/// </list>
/// </summary>
public static class Observations
{
    /// <summary>A single destination this far above the rest is usually one missing rule or one
    /// misconfigured workload, which is worth saying out loud rather than leaving in a table.</summary>
    private const double ConcentrationThreshold = 0.4;

    public static IReadOnlyList<Observation> From(
        PolicySnapshot policy,
        IReadOnlyList<DecisionRow> denials,
        IReadOnlyList<ReportFinding> reportFindings,
        IReadOnlyList<ChallengeConversion> challenges)
    {
        var observations = new List<Observation>();

        observations.AddRange(ConcentratedDenials(policy, denials));
        observations.AddRange(UnmatchedSubjects(policy, denials));
        observations.AddRange(ReportModeGaps(policy, reportFindings));
        observations.AddRange(OpenRulesets(policy));
        observations.Add(Probing(challenges));

        return [.. observations
            .OrderByDescending(o => o.Level)
            .ThenBy(o => o.Subject, StringComparer.Ordinal)];
    }

    /// <summary>One host taking most of the denials in the window.</summary>
    private static IEnumerable<Observation> ConcentratedDenials(
        PolicySnapshot policy,
        IReadOnlyList<DecisionRow> denials)
    {
        if (denials.Count == 0)
        {
            yield break;
        }

        var byHost = denials
            .GroupBy(d => (d.Role, d.Host))
            .Select(g => (g.Key.Role, g.Key.Host, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .First();

        var share = (double)byHost.Count / denials.Count;
        if (share < ConcentrationThreshold)
        {
            yield break;
        }

        var ruleset = policy.Governing(byHost.Role);
        yield return new Observation(
            ObservationLevel.Prominent,
            ruleset?.Name ?? byHost.Role,
            $"{byHost.Count} denials to {byHost.Host}",
            $"One destination accounts for {share:P0} of all denials in the window. "
            + "Either the rule is missing or the workload is misconfigured.",
            Surface.Traffic);
    }

    /// <summary>
    /// Denials from a subject no ruleset governs. Attributed to the fallback — never to a ruleset
    /// that does not govern it — because that misattribution is precisely the wrong answer to the
    /// question the console exists to answer.
    /// </summary>
    private static IEnumerable<Observation> UnmatchedSubjects(
        PolicySnapshot policy,
        IReadOnlyList<DecisionRow> denials)
    {
        var unmatched = denials
            .Where(d => d.Role is { Length: > 0 } && policy.Governing(d.Role) is null)
            .Select(d => d.Role!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unmatched.Count == 0)
        {
            yield break;
        }

        yield return new Observation(
            ObservationLevel.Prominent,
            unmatched.Count == 1 ? unmatched[0] : null,
            unmatched.Count == 1
                ? "One workload matches no ruleset"
                : $"{unmatched.Count} workloads match no ruleset",
            policy.Fallback.DenyAll
                ? "Their traffic falls to the deny-all floor, so everything they attempt is refused. "
                + "They are either not onboarded yet or were offboarded."
                : "Their traffic falls to the platform baseline rather than to a ruleset of their own.",
            Surface.Lookup);
    }

    /// <summary>
    /// What a report-mode ruleset still needs. Leads with the hosts, never with how long it has
    /// been in report — sitting in report is a legitimate steady state.
    /// </summary>
    private static IEnumerable<Observation> ReportModeGaps(
        PolicySnapshot policy,
        IReadOnlyList<ReportFinding> findings)
    {
        foreach (var ruleset in policy.Rulesets.Where(r => r.Action == RulesetAction.Report))
        {
            var offList = findings
                .Where(f => ruleset.Subjects.Any(s =>
                    string.Equals(s.Appid, f.Role, StringComparison.OrdinalIgnoreCase)))
                .Select(f => f.Host)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (offList == 0)
            {
                continue;
            }

            yield return new Observation(
                ObservationLevel.Notable,
                ruleset.Name,
                $"reporting, {offList} host(s) observed off-list",
                "Report mode permits everything and only logs it. These are the hosts the ruleset "
                + "would need before it could be promoted to enforce.",
                Surface.Rulesets);
        }
    }

    /// <summary>A ruleset whose allowlist constrains nothing.</summary>
    private static IEnumerable<Observation> OpenRulesets(PolicySnapshot policy)
    {
        foreach (var ruleset in policy.Rulesets.Where(r => r.Action == RulesetAction.Open))
        {
            yield return new Observation(
                ObservationLevel.Notable,
                ruleset.Name,
                "action is open",
                "Open permits all egress for these subjects and logs it. The host list is not "
                + "consulted at all while a ruleset is in this state.",
                Surface.Rulesets);
        }
    }

    /// <summary>
    /// Sources challenged that never authenticated. Every authenticated connection produces
    /// exactly one credential-less CONNECT first, so challenges roughly matching authentications is
    /// the healthy shape; a stream that never converts is what probing looks like.
    /// </summary>
    private static Observation Probing(IReadOnlyList<ChallengeConversion> challenges)
    {
        var probing = challenges.Where(c => c.NeverConverted).ToList();

        return probing.Count == 0
            ? new Observation(
                ObservationLevel.Fine,
                null,
                "No probing detected",
                "Every source that was challenged went on to authenticate. Challenges without a "
                + "matching authentication are the signal the 407 rows are kept for.",
                Surface.Traffic)
            : new Observation(
                ObservationLevel.Prominent,
                null,
                $"{probing.Count} source(s) challenged but never authenticated",
                $"{probing.Sum(p => p.Challenges)} credential-less connections that never became a "
                + "decision. This is what probing looks like from the proxy's side.",
                Surface.Traffic);
    }
}
