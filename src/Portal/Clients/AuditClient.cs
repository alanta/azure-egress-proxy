using Azure;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;

namespace Portal.Clients;

public sealed class AuditOptions
{
    /// <summary>Workspace the DCR sends <c>EgressProxy_CL</c> to.</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>Rows any one table view will render. A console table is scrolled, not paged
    /// through to row 10,000, and an unbounded projection is a transfer cost as well as a
    /// rendering one.</summary>
    public int RowLimit { get; set; } = 500;
}

/// <summary>
/// Reads the proxy's decisions out of <c>EgressProxy_CL</c>.
///
/// <para><b>Every query here is bounded.</b> A time window from <see cref="TrafficWindow"/> and a
/// row cap, always — Log Analytics cost and latency are user-facing the moment a console queries
/// it, and an unbounded query against a busy workspace is a real expense (design.md, Risks). The
/// window is an enum rather than a string for exactly this reason: there is no free-text path from
/// a page to a KQL time range.</para>
///
/// <para><b>The event-type split is preserved, never blurred.</b> A <c>CANONICAL-PROXY-DECISION</c>
/// row with an empty <c>Role</c> is a <i>rejected credential</i>. A
/// <c>CANONICAL-PROXY-AUTH-REQUIRED</c> row is the credential-less 407 handshake, one per new
/// tunnel, and is not a denial. Keying either on <c>DecisionReason</c> text instead of on
/// <c>EventType</c> plus <c>Role</c> would hide authentication failures behind normal handshake
/// noise — the queries below key on the columns, and reason text is only ever displayed.</para>
/// </summary>
public sealed class AuditClient(LogsQueryClient client, AuditOptions options, ILogger<AuditClient> logger)
{
    private const string Table = "EgressProxy_CL";
    private const string Decision = "CANONICAL-PROXY-DECISION";
    private const string AuthRequired = "CANONICAL-PROXY-AUTH-REQUIRED";
    private const string Close = "CANONICAL-PROXY-CN-CLOSE";

    /// <summary>
    /// Whether there is a workspace to query at all. Surfaces must consult this before rendering
    /// a count: an unconfigured workspace yields the same empty result as a genuinely quiet
    /// window, and "0 denials" shown because nothing was asked is worse than showing nothing —
    /// it is a security console reporting a clean bill of health it never checked.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.WorkspaceId);

    /// <summary>Denials, newest first. Each row carries the source IP as well as the role, because
    /// a <c>netid</c> ruleset has no other key and the operator needs to see which they are
    /// looking at.</summary>
    public Task<IReadOnlyList<DecisionRow>> DenialsAsync(
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        QueryAsync($"""
            {Table}
            | where EventType == "{Decision}" and Allow == false and isnotempty(Role)
            | project TimeGenerated, Role, SrcIp, Host, Allow, DecisionReason, EnforceWouldDeny, ReqId
            | order by TimeGenerated desc
            | take {options.RowLimit}
            """, window, ReadDecision, cancellationToken);

    /// <summary>Every decision for one subject — the ruleset detail panel's traffic half.</summary>
    public Task<IReadOnlyList<DecisionRow>> DecisionsForRoleAsync(
        string role,
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        QueryAsync($"""
            {Table}
            | where EventType == "{Decision}" and Role == "{Escape(role)}"
            | project TimeGenerated, Role, SrcIp, Host, Allow, DecisionReason, EnforceWouldDeny, ReqId
            | order by TimeGenerated desc
            | take {options.RowLimit}
            """, window, ReadDecision, cancellationToken);

    /// <summary>
    /// Credentials presented and rejected, grouped by source and host. An empty <c>Role</c> on a
    /// DECISION row is the whole test — see the class remarks for why it is not a reason match.
    /// </summary>
    public Task<IReadOnlyList<AuthFailureGroup>> AuthFailuresAsync(
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        QueryAsync($"""
            {Table}
            | where EventType == "{Decision}" and isempty(Role)
            | summarize attempts = count(), reasons = make_set(DecisionReason, 5),
                        lastSeen = max(TimeGenerated) by SrcIp, Host
            | order by attempts desc
            | take {options.RowLimit}
            """, window, row => new AuthFailureGroup(
                Text(row, "SrcIp"),
                Text(row, "Host"),
                Int(row, "attempts"),
                Strings(row, "reasons"),
                Time(row, "lastSeen")), cancellationToken);

    /// <summary>
    /// Sources challenged versus sources that then authenticated. A source that is challenged
    /// repeatedly and never converts is the probing signal the 407 rows are retained for.
    /// </summary>
    public Task<IReadOnlyList<ChallengeConversion>> ChallengeConversionAsync(
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        QueryAsync($"""
            {Table}
            | where EventType in ("{AuthRequired}", "{Decision}")
            | summarize challenges = countif(EventType == "{AuthRequired}"),
                        authenticated = countif(EventType == "{Decision}" and isnotempty(Role))
                        by SrcIp
            | order by challenges desc
            | take {options.RowLimit}
            """, window, row => new ChallengeConversion(
                Text(row, "SrcIp"), Int(row, "challenges"), Int(row, "authenticated")), cancellationToken);

    /// <summary>
    /// Off-list hosts a report-mode workload actually attempted. What a ruleset still needs before
    /// it can be promoted — and the only thing the console should lead a promotion prompt with.
    /// </summary>
    public Task<IReadOnlyList<ReportFinding>> ReportFindingsAsync(
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        QueryAsync($"""
            {Table}
            | where EventType == "{Decision}" and EnforceWouldDeny == true and isnotempty(Role)
            | summarize attempts = count(), lastSeen = max(TimeGenerated) by Role, Host
            | order by attempts desc
            | take {options.RowLimit}
            """, window, row => new ReportFinding(
                Text(row, "Role"), Text(row, "Host"), Int(row, "attempts"), Time(row, "lastSeen")),
            cancellationToken);

    /// <summary>Off-list hosts for one subject specifically — the promotion checklist for a
    /// single ruleset.</summary>
    public Task<IReadOnlyList<ReportFinding>> ReportFindingsForRoleAsync(
        string role,
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        QueryAsync($"""
            {Table}
            | where EventType == "{Decision}" and EnforceWouldDeny == true and Role == "{Escape(role)}"
            | summarize attempts = count(), lastSeen = max(TimeGenerated) by Role, Host
            | order by attempts desc
            | take {options.RowLimit}
            """, window, row => new ReportFinding(
                Text(row, "Role"), Text(row, "Host"), Int(row, "attempts"), Time(row, "lastSeen")),
            cancellationToken);

    /// <summary>Volume and top talkers, from the connection summaries rather than the decisions —
    /// byte counts are only known once a tunnel closes.</summary>
    public Task<IReadOnlyList<TalkerRow>> TopTalkersAsync(
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        QueryAsync($"""
            {Table}
            | where EventType == "{Close}"
            | summarize bytesIn = sum(BytesIn), bytesOut = sum(BytesOut), connections = count()
                        by Role, Host
            | order by bytesOut desc
            | take {options.RowLimit}
            """, window, row => new TalkerRow(
                Text(row, "Role"), Text(row, "Host"),
                Long(row, "bytesIn"), Long(row, "bytesOut"), Int(row, "connections")), cancellationToken);

    /// <summary>
    /// The three numbers the Overview's traffic summary shows, in one round trip rather than
    /// three. Cheaper, and — more importantly — the three cannot disagree about which window they
    /// counted over.
    /// </summary>
    public async Task<TrafficSummary> SummaryAsync(TrafficWindow window, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync($"""
            let challenged = {Table}
                | where EventType == "{AuthRequired}"
                | summarize challenges = count() by SrcIp;
            let converted = {Table}
                | where EventType == "{Decision}" and isnotempty(Role)
                | summarize authed = count() by SrcIp;
            let unconverted = challenged
                | join kind=leftouter converted on SrcIp
                | where isnull(authed) or authed == 0
                | summarize sources = count();
            {Table}
            | summarize denials = countif(EventType == "{Decision}" and Allow == false and isnotempty(Role)),
                        authFailures = countif(EventType == "{Decision}" and isempty(Role))
            | extend unconvertedSources = toscalar(unconverted)
            """, window, row => (
                Denials: Int(row, "denials"),
                AuthFailures: Int(row, "authFailures"),
                Unconverted: Int(row, "unconvertedSources")), cancellationToken);

        var summary = rows.FirstOrDefault();
        return new TrafficSummary(
            summary.Denials, summary.AuthFailures, summary.Unconverted, window, Freshness.Now);
    }

    /// <summary>
    /// One query, one bounded window, one projection. The window is applied by the SDK's
    /// <c>QueryTimeRange</c> rather than an <c>ago()</c> in the text, so it cannot be forgotten in
    /// a query and cannot be widened by anything a page passes in.
    /// </summary>
    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string query,
        TrafficWindow window,
        Func<LogsTableRow, T> read,
        CancellationToken cancellationToken)
    {
        var workspace = options.WorkspaceId;
        if (string.IsNullOrWhiteSpace(workspace))
        {
            // An unconfigured workspace is a deployment gap, not a portal fault. Empty panels with
            // a warning in the log beat a console that will not render its policy surfaces either.
            logger.LogWarning("no Log Analytics workspace configured; traffic surfaces will be empty");
            return [];
        }

        try
        {
            var response = await client.QueryWorkspaceAsync(
                workspace, query, new QueryTimeRange(window.ToTimeSpan()), cancellationToken: cancellationToken);

            return [.. response.Value.Table.Rows.Select(read)];
        }
        catch (RequestFailedException e)
        {
            logger.LogError(e, "Log Analytics query failed ({Status})", e.Status);
            throw;
        }
    }

    private static DecisionRow ReadDecision(LogsTableRow row) => new(
        Time(row, "TimeGenerated"),
        Text(row, "Role") is { Length: > 0 } role ? role : null,
        Text(row, "SrcIp"),
        Text(row, "Host"),
        Bool(row, "Allow"),
        Text(row, "DecisionReason") is { Length: > 0 } reason ? reason : null,
        Bool(row, "EnforceWouldDeny"),
        Text(row, "ReqId") is { Length: > 0 } id ? id : null);

    /// <summary>
    /// Quotes a value being interpolated into a query. The values reaching here are appids and
    /// ruleset names the API has already validated, but "already validated somewhere else" is not
    /// a property this file can check — so it escapes anyway.
    /// </summary>
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Text(LogsTableRow row, string column) =>
        row.GetString(column) ?? string.Empty;

    private static int Int(LogsTableRow row, string column) =>
        row.GetInt64(column) is { } value ? (int)value : 0;

    private static long Long(LogsTableRow row, string column) => row.GetInt64(column) ?? 0;

    private static bool Bool(LogsTableRow row, string column) => row.GetBoolean(column) ?? false;

    private static DateTimeOffset Time(LogsTableRow row, string column) =>
        row.GetDateTimeOffset(column) ?? DateTimeOffset.MinValue;

    private static IReadOnlyList<string> Strings(LogsTableRow row, string column)
    {
        // make_set produces a dynamic column, which the SDK surfaces as JSON text.
        var raw = row.GetString(column);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(raw) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [raw];
        }
    }
}
