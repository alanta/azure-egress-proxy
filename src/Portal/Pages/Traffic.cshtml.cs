using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Clients;
using Portal.Components;

namespace Portal.Pages;

/// <summary>
/// How a denial was resolved to the policy that governs it. The distinction is the point of the
/// surface: an operator acting on a denial needs to know whether the console <i>knows</i> whose
/// traffic this was, or merely <i>correlated</i> it.
/// </summary>
public enum DenialAttribution
{
    /// <summary>Joined on the <c>appid</c> claim from the validated JWT — the strong join.</summary>
    Identity,

    /// <summary>Correlated on source address against a <c>netid</c> subject's CIDR. Weaker by
    /// construction: a source address is not an identity, and a single Container Apps replica's
    /// traffic arrives from several rotating node IPs. The row must say so.</summary>
    Network,

    /// <summary>No ruleset governs this subject, so the platform fallback does. Never a
    /// ruleset — misattributing an unmatched subject is precisely the wrong answer to the
    /// question this surface exists to answer.</summary>
    Fallback,
}

/// <summary>One denial, resolved. <see cref="Ruleset"/> is null exactly when
/// <see cref="Attribution"/> is <see cref="DenialAttribution.Fallback"/>.</summary>
public sealed record DenialView(DecisionRow Row, RulesetView? Ruleset, DenialAttribution Attribution);

/// <summary>
/// The Traffic surface: every proxy decision, joined to the ruleset that governs the workload that
/// made it.
///
/// <para>The join in <see cref="Attribute"/> is the reason the console exists. Nothing else in the
/// system holds both the authored policy and the decisions it produced, so "which rule caused this
/// denial, and what would I change?" was previously a question answered by hand against two
/// stores.</para>
///
/// <para>Structured like the Overview: one handler per panel, one swap target per panel,
/// <c>hx-trigger="every 60s"</c> against the server-side cache, and a source that fails degrades
/// its own panel rather than the page.</para>
/// </summary>
public sealed class TrafficModel(ConsoleData data, ILogger<TrafficModel> logger) : PageModel
{
    /// <summary>A filter value long enough for an appid or an FQDN and no longer. The filters run
    /// in memory over rows the bounded queries already returned (see <see cref="LoadDenialsAsync"/>),
    /// so this is a rendering bound rather than a query one — but a console that echoed an
    /// unbounded string back into its own URLs is not worth having either.</summary>
    private const int MaxFilterLength = 128;

    // ---- filters, all of them round-tripped through the query string --------------------------

    /// <summary>From <c>TrafficWindows.Parse</c>, so an unrecognised value narrows to the default
    /// rather than widening. There is no path from this page to a free-text KQL time range.</summary>
    public TrafficWindow Window { get; private set; } = TrafficWindows.Default;

    /// <summary>A substring of the workload identity — the <c>Role</c>, i.e. the validated
    /// <c>appid</c> — or of the ruleset governing it.</summary>
    public string? Subject { get; private set; }

    /// <summary>A substring of the destination.</summary>
    public string? Host { get; private set; }

    public bool HasFilter => Subject is not null || Host is not null;

    // ---- data ---------------------------------------------------------------------------------

    /// <summary>False when no audit workspace is configured. Every panel here says so rather than
    /// rendering zeroes: a count of 0 that nobody asked for reads as "all clear".</summary>
    public bool TrafficConfigured { get; private set; } = true;

    public PolicySnapshot? Policy { get; private set; }

    public TrafficSummary? Summary { get; private set; }

    public IReadOnlyList<DenialView> Denials { get; private set; } = [];

    /// <summary>Denials before the subject/host filter, so a filtered panel can say how much of
    /// the window it is hiding rather than looking like a quiet window.</summary>
    public int DenialsInWindow { get; private set; }

    public IReadOnlyList<AuthFailureGroup> AuthFailures { get; private set; } = [];

    public IReadOnlyList<ChallengeConversion> Challenges { get; private set; } = [];

    public IReadOnlyList<TalkerRow> Talkers { get; private set; } = [];

    /// <summary>
    /// The stamp every Azure-fed panel on this surface renders.
    ///
    /// <para>It is the traffic summary's own <see cref="Freshness"/> — the timestamp of the fetch
    /// that produced it, kept through the cache and never restamped as "now". The other traffic
    /// queries share its client, its window and its two-minute cache lifetime and are loaded in the
    /// same request, so it describes the read cycle rather than one panel. When the summary could
    /// not be read there is no stamp, because inventing one is the failure D4 exists to prevent.</para>
    /// </summary>
    public Freshness? ReadAt => Summary?.Freshness;

    /// <summary>Set when a source could not be read, so a panel can say so instead of rendering
    /// zeroes — "0 denials" shown because the query failed is worse than showing nothing.</summary>
    public string? Error { get; private set; }

    // ---- the join, 8.2 / 8.3 ------------------------------------------------------------------

    /// <summary>
    /// Resolves each denial to the policy that governs it. Static and pure so the rule can be
    /// tested directly rather than through a rendered page.
    ///
    /// <para>Three steps, in this order, and the order is the substance:</para>
    /// <list type="number">
    /// <item><b><c>Role</c> against <c>subjects[].appid</c></b>. <c>Role</c> is the <c>appid</c>
    /// claim from the JWT the proxy validated, and <c>subjects[].appid</c> is the same value as
    /// authored, so this is an identity match with no heuristic in it.</item>
    /// <item><b><c>SrcIp</c> inside a <c>netid</c> subject's CIDR</b>, but <i>only</i> when the
    /// appid join could never have applied — when the row carries no <c>Role</c>, or a <c>Role</c>
    /// that is not an appid at all (in <c>netid</c> mode the proxy's role is the module id). A
    /// deployment keying on <c>appid</c> that produced an appid this policy does not know has an
    /// <b>unmatched subject</b>, and letting a source address that happens to sit in some CIDR
    /// override that would be a source-address correlation quietly overruling a validated identity.
    /// Where this step does fire, the row is marked <see cref="DenialAttribution.Network"/> and the
    /// UI says the correlation is by address.</item>
    /// <item><b>Otherwise the fallback governs</b>. <c>PolicySnapshot.Governing</c> returning null
    /// means the platform floor applies, not that some other ruleset might.</item>
    /// </list>
    ///
    /// <para>With no policy readable, every row is reported as unresolved rather than guessed at.</para>
    /// </summary>
    public static IReadOnlyList<DenialView> Attribute(
        PolicySnapshot? policy,
        IEnumerable<DecisionRow> denials)
    {
        if (policy is null)
        {
            return [.. denials.Select(row => new DenialView(row, null, DenialAttribution.Fallback))];
        }

        return [.. denials.Select(row =>
        {
            if (policy.Governing(row.Role) is { } byIdentity)
            {
                return new DenialView(row, byIdentity, DenialAttribution.Identity);
            }

            if (!LooksLikeAppid(row.Role) && ByNetwork(policy, row.SourceIp) is { } byAddress)
            {
                return new DenialView(row, byAddress, DenialAttribution.Network);
            }

            return new DenialView(row, null, DenialAttribution.Fallback);
        })];
    }

    /// <summary>The first ruleset with a <c>netid</c> subject whose CIDR contains the source
    /// address. First rather than all: subjects are one-to-one with rulesets by construction, so a
    /// second match would be an overlapping-CIDR authoring error, not a case to model here.</summary>
    private static RulesetView? ByNetwork(PolicySnapshot policy, string sourceIp) =>
        IPAddress.TryParse(sourceIp, out var address)
            ? policy.Rulesets.FirstOrDefault(ruleset => ruleset.Subjects
                .Any(subject => subject.Netid is { } cidr && Contains(cidr, address)))
            : null;

    /// <summary>
    /// Whether a CIDR contains an address, by prefix bits over the raw address bytes. Written out
    /// rather than taken from a library so the netid semantics match the schema's
    /// (<c>a.b.c.d/len</c>) exactly, and so a malformed value is a non-match rather than an
    /// exception on a page that is only reading.
    /// </summary>
    private static bool Contains(string cidr, IPAddress address)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0
            || !IPAddress.TryParse(cidr[..slash], out var network)
            || !int.TryParse(cidr[(slash + 1)..], out var prefixLength)
            || network.AddressFamily != address.AddressFamily)
        {
            return false;
        }

        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (prefixLength < 0 || prefixLength > networkBytes.Length * 8)
        {
            return false;
        }

        for (var index = 0; index < networkBytes.Length && prefixLength > 0; index++, prefixLength -= 8)
        {
            var mask = prefixLength >= 8 ? (byte)0xFF : (byte)(0xFF << (8 - prefixLength));
            if ((networkBytes[index] & mask) != (addressBytes[index] & mask))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a role is shaped like a managed-identity client ID, which is what decides whether
    /// the appid join was applicable at all. A GUID rather than a lookup, because the question is
    /// "was this deployment keying on identity?" and not "does this policy know the value?" — those
    /// two being different is exactly what step 3 of <see cref="Attribute"/> reports.
    /// </summary>
    private static bool LooksLikeAppid(string? role) => Guid.TryParse(role, out _);

    // ---- request handling ---------------------------------------------------------------------

    public async Task OnGetAsync(
        string? window,
        string? subject,
        string? host,
        CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Traffic";
        ViewData["Surface"] = Surface.Traffic.Key;
        ReadFilters(window, subject, host);

        await LoadSummaryAsync(cancellationToken);
        await LoadDenialsAsync(cancellationToken);
        await LoadCredentialsAsync(cancellationToken);
        await LoadChallengesAsync(cancellationToken);
        await LoadVolumeAsync(cancellationToken);
    }

    // ---- panel handlers, one per swap target ---------------------------------------------------

    public async Task<IActionResult> OnGetSummaryPanelAsync(
        string? window, string? subject, string? host, CancellationToken cancellationToken)
    {
        ReadFilters(window, subject, host);
        await LoadSummaryAsync(cancellationToken);
        return Partial("_TrafficSummary", this);
    }

    public async Task<IActionResult> OnGetDenialsPanelAsync(
        string? window, string? subject, string? host, CancellationToken cancellationToken)
    {
        ReadFilters(window, subject, host);
        await LoadSummaryAsync(cancellationToken);
        await LoadDenialsAsync(cancellationToken);
        return Partial("_TrafficDenials", this);
    }

    public async Task<IActionResult> OnGetCredentialsPanelAsync(
        string? window, string? subject, string? host, CancellationToken cancellationToken)
    {
        ReadFilters(window, subject, host);
        await LoadSummaryAsync(cancellationToken);
        await LoadCredentialsAsync(cancellationToken);
        return Partial("_TrafficCredentials", this);
    }

    public async Task<IActionResult> OnGetChallengesPanelAsync(
        string? window, string? subject, string? host, CancellationToken cancellationToken)
    {
        ReadFilters(window, subject, host);
        await LoadSummaryAsync(cancellationToken);
        await LoadChallengesAsync(cancellationToken);
        return Partial("_TrafficChallenges", this);
    }

    public async Task<IActionResult> OnGetVolumePanelAsync(
        string? window, string? subject, string? host, CancellationToken cancellationToken)
    {
        ReadFilters(window, subject, host);
        await LoadSummaryAsync(cancellationToken);
        await LoadVolumeAsync(cancellationToken);
        return Partial("_TrafficVolume", this);
    }

    // ---- URLs ---------------------------------------------------------------------------------

    /// <summary>
    /// This surface's URL with one filter replaced — the address a filter control links to, and the
    /// address a panel polls. Real query-string values, built server-side: the filters are plain
    /// <c>hx-get</c> with <c>hx-push-url</c>, never <c>hx-vals='js:…'</c>, which the portal's CSP
    /// does not grant and which would make the filtered view unshareable anyway.
    /// </summary>
    public string FilterUrl(
        string? handler = null,
        TrafficWindow? window = null,
        string? subject = null,
        string? host = null)
    {
        var parts = new List<string>();

        if (handler is not null)
        {
            parts.Add($"handler={WebUtility.UrlEncode(handler)}");
        }

        parts.Add($"window={(window ?? Window).ToQueryValue()}");

        if ((subject ?? Subject) is { Length: > 0 } chosenSubject)
        {
            parts.Add($"subject={WebUtility.UrlEncode(chosenSubject)}");
        }

        if ((host ?? Host) is { Length: > 0 } chosenHost)
        {
            parts.Add($"host={WebUtility.UrlEncode(chosenHost)}");
        }

        return $"{Surface.Traffic.Path}?{string.Join('&', parts)}";
    }

    // ---- loading -------------------------------------------------------------------------------

    private void ReadFilters(string? window, string? subject, string? host)
    {
        Window = TrafficWindows.Parse(window);
        Subject = Clean(subject);
        Host = Clean(host);
    }

    private static string? Clean(string? value) => value?.Trim() switch
    {
        null or "" => null,
        { Length: > MaxFilterLength } long_ => long_[..MaxFilterLength],
        var trimmed => trimmed,
    };

    /// <summary>Policy is what every traffic row is joined against, so both panels that name a
    /// ruleset load it. It is a 30-second cache read, not a second control-plane call.</summary>
    private async Task LoadPolicyAsync(CancellationToken cancellationToken) =>
        Policy = await TryAsync(() => data.PolicyAsync(cancellationToken), "policy");

    private async Task LoadSummaryAsync(CancellationToken cancellationToken)
    {
        TrafficConfigured = data.TrafficIsConfigured;
        if (!TrafficConfigured)
        {
            return;
        }

        Summary = await TryAsync(() => data.TrafficSummaryAsync(Window, cancellationToken), "traffic summary");
    }

    /// <summary>
    /// The denials table: read the window, resolve each row against policy, then narrow.
    ///
    /// <para>The subject and host filters are applied <b>in memory</b> over the rows the bounded
    /// query already returned, deliberately. Interpolating an operator's typing into KQL is a
    /// second free-text path into a query language after the time range, and the window cap plus
    /// the client's row cap already bound what a filter has to sift. It also means a filter reuses
    /// the cached window rather than issuing a new query per keystroke's worth of typing.</para>
    /// </summary>
    private async Task LoadDenialsAsync(CancellationToken cancellationToken)
    {
        if (!TrafficConfigured)
        {
            return;
        }

        var rows = await TryAsync(() => data.DenialsAsync(Window, cancellationToken), "denials") ?? [];
        await LoadPolicyAsync(cancellationToken);

        var attributed = Attribute(Policy, rows);
        DenialsInWindow = attributed.Count;
        Denials = [.. attributed.Where(Matches)];
    }

    /// <summary>Subject matches the workload identity <i>or</i> the ruleset governing it, because
    /// an operator holding a ticket has one or the other and should not have to know which.</summary>
    private bool Matches(DenialView denial) =>
        (Subject is null
            || Has(denial.Row.Role, Subject)
            || Has(denial.Ruleset?.Name, Subject)
            || Has(denial.Row.SourceIp, Subject))
        && (Host is null || Has(denial.Row.Host, Host));

    private static bool Has(string? value, string needle) =>
        value is not null && value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private async Task LoadCredentialsAsync(CancellationToken cancellationToken)
    {
        if (!TrafficConfigured)
        {
            return;
        }

        var groups = await TryAsync(() => data.AuthFailuresAsync(Window, cancellationToken), "auth failures") ?? [];

        // Host, not subject: these rows have no Role by definition — that empty Role is what makes
        // them a rejected credential rather than a policy denial.
        AuthFailures = Host is null
            ? groups
            : [.. groups.Where(group => Has(group.Host, Host))];
    }

    private async Task LoadChallengesAsync(CancellationToken cancellationToken)
    {
        if (!TrafficConfigured)
        {
            return;
        }

        var sources = await TryAsync(
            () => data.ChallengeConversionAsync(Window, cancellationToken), "challenge conversion") ?? [];

        // The subject filter reads as a source-address filter here: a challenge predates any
        // identity, so there is nothing else to match on.
        Challenges = Subject is null
            ? sources
            : [.. sources.Where(source => Has(source.SourceIp, Subject))];
    }

    private async Task LoadVolumeAsync(CancellationToken cancellationToken)
    {
        if (!TrafficConfigured)
        {
            return;
        }

        var talkers = await TryAsync(() => data.TopTalkersAsync(Window, cancellationToken), "top talkers") ?? [];
        await LoadPolicyAsync(cancellationToken);

        Talkers = [.. talkers.Where(talker =>
            (Subject is null || Has(talker.Role, Subject))
            && (Host is null || Has(talker.Host, Host)))];
    }

    /// <summary>One failing source degrades its own panel and no other — the same rule the Overview
    /// keeps, and the reason this surface stays readable when Log Analytics is having a moment.</summary>
    private async Task<T?> TryAsync<T>(Func<Task<T>> load, string what)
    {
        try
        {
            return await load();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "the traffic surface could not read {What}", what);
            Error = $"{what} could not be read";
            return default;
        }
    }
}

/// <summary>Rendering helpers the traffic partials share. Here rather than in the shared component
/// set because nothing outside this surface has asked for them.</summary>
public static class TrafficFormat
{
    /// <summary>Bytes at operator resolution. Decimal units, matching how Azure reports egress.</summary>
    public static string Bytes(long value)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB", "PB"];
        double scaled = value;
        var unit = 0;

        while (scaled >= 1000 && unit < units.Length - 1)
        {
            scaled /= 1000;
            unit++;
        }

        return unit == 0 ? $"{value:N0} B" : $"{scaled:N1} {units[unit]}";
    }

    /// <summary>An identifier, shortened for a table cell but never re-formed into something that
    /// could be mistaken for a different value. The full value stays in the cell's title.</summary>
    public static string Short(string? value) => value switch
    {
        null or "" => "—",
        { Length: <= 13 } => value,
        _ => $"{value[..4]}…{value[^4..]}",
    };
}
