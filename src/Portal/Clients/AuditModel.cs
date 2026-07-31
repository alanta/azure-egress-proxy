namespace Portal.Clients;

/// <summary>
/// How far back a traffic view looks. Bounded by construction rather than by a default parameter:
/// design.md lists an unbounded console query against a busy workspace as a real expense, and a
/// free-text window on a page is how one gets issued. Every query the portal makes takes one of
/// these.
/// </summary>
public enum TrafficWindow
{
    LastHour,
    LastDay,
    LastWeek,
}

public static class TrafficWindows
{
    /// <summary>
    /// A day by default. An hour is too short to answer "what has this workload been denied?" for
    /// anything but a busy service; a week is a bill. A day covers a working day's investigation,
    /// which is what the denial → ruleset loop is for.
    /// </summary>
    public const TrafficWindow Default = TrafficWindow.LastDay;

    public static TimeSpan ToTimeSpan(this TrafficWindow window) => window switch
    {
        TrafficWindow.LastHour => TimeSpan.FromHours(1),
        TrafficWindow.LastWeek => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(1),
    };

    public static string Label(this TrafficWindow window) => window switch
    {
        TrafficWindow.LastHour => "last hour",
        TrafficWindow.LastWeek => "last 7 days",
        _ => "last 24 hours",
    };

    /// <summary>Parses a query-string value back to a window, falling back to the default. An
    /// unrecognised value narrows to the default rather than widening — the same instinct as
    /// action normalization, applied to cost.</summary>
    public static TrafficWindow Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "1h" or "hour" => TrafficWindow.LastHour,
        "7d" or "week" => TrafficWindow.LastWeek,
        _ => Default,
    };

    public static string ToQueryValue(this TrafficWindow window) => window switch
    {
        TrafficWindow.LastHour => "1h",
        TrafficWindow.LastWeek => "7d",
        _ => "24h",
    };
}

/// <summary>
/// One proxy decision, as the console shows it.
///
/// <see cref="Role"/> is the workload's <c>appid</c> from the <b>validated JWT</b> — the exact join
/// key into <c>subjects[].appid</c>, which is what makes a denial resolve to its governing ruleset
/// without heuristics. <see cref="SourceIp"/> is shown on every row but is <b>not</b> an identity:
/// a single Container Apps replica's traffic arrives from multiple rotating node IPs. It is the
/// only key a <c>netid</c> ruleset has, which is why that correlation is weaker by construction
/// and must be labelled as such where it is used.
/// </summary>
public sealed record DecisionRow(
    DateTimeOffset TimeGenerated,
    string? Role,
    string SourceIp,
    string Host,
    bool Allowed,
    string? DecisionReason,
    bool EnforceWouldDeny,
    string? RequestId);

/// <summary>
/// A rejected credential: a <c>CANONICAL-PROXY-DECISION</c> row with an empty <c>Role</c>.
///
/// The distinction from the 407 challenge is load-bearing and must not be blurred here. An empty
/// <c>Role</c> on a DECISION row means credentials <b>were</b> presented and rejected. The
/// credential-less handshake is a separate event type — see <see cref="ChallengeConversion"/>.
/// Widening either to a <c>DecisionReason</c> match would hide authentication failures.
/// </summary>
public sealed record AuthFailureGroup(
    string SourceIp,
    string Host,
    int Attempts,
    IReadOnlyList<string> Reasons,
    DateTimeOffset LastSeen);

/// <summary>
/// Challenged versus authenticated, per source — the probing signal.
///
/// Every authenticated connection produces exactly one credential-less <c>CONNECT</c> first, so
/// challenges roughly equalling authentications is the healthy shape. A stream of challenges that
/// never converts is what probing looks like, which is precisely why these rows are reclassified
/// at the DCR rather than dropped.
/// </summary>
/// <param name="Authenticated">Decisions from this source that carried a role.</param>
public sealed record ChallengeConversion(string SourceIp, int Challenges, int Authenticated)
{
    /// <summary>Challenged and never once authenticated. Not an alert — an observation.</summary>
    public bool NeverConverted => Authenticated == 0;
}

/// <summary>
/// A host a <c>report</c>-mode ruleset actually reached that <c>enforce</c> would have denied —
/// the <c>EnforceWouldDeny</c> signal, which is the whole point of the on-ramp.
///
/// This is what a promotion prompt leads with. Time in <c>report</c> is <b>not</b> a signal: a
/// ruleset can sit there indefinitely as a legitimate steady state, and the console must not nudge
/// on age (design.md D9).
/// </summary>
public sealed record ReportFinding(string Role, string Host, int Attempts, DateTimeOffset LastSeen);

/// <summary>Denials grouped for the overview and the top-talkers panel.</summary>
public sealed record HostCount(string Key, int Count);

/// <summary>Bytes moved, from the <c>CN-CLOSE</c> connection summaries.</summary>
public sealed record TalkerRow(string Role, string Host, long BytesIn, long BytesOut, int Connections);

/// <summary>The counts the Overview's traffic summary renders, in one round trip.</summary>
public sealed record TrafficSummary(
    int Denials,
    int AuthFailures,
    int UnconvertedChallenges,
    TrafficWindow Window,
    Freshness Freshness);
