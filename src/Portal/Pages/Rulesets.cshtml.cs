using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Clients;
using Portal.Components;

namespace Portal.Pages;

/// <summary>
/// The Rulesets surface: authored policy as the control plane holds it, the traffic each ruleset
/// is actually producing, and a sandbox that composes a candidate change without applying it.
///
/// <para><b>The one non-GET in the console lives here.</b> <c>POST /rulesets/{name}:check</c> is
/// the API's dry run — it validates exactly as a write would and stops before the blob. Everything
/// the sandbox produces ends in a snippet the operator applies through the pipeline, which stays
/// the source of truth: a change made outside it would be silently reverted by the next unrelated
/// deploy, because a push is a full replace (design.md D3).</para>
///
/// <para>Three swap targets rather than one, for the same reason the Overview has four handlers.
/// <c>#ruleset-list</c> polls itself every 60s because its denial counts come from Log Analytics;
/// <c>#ruleset-body</c> is what a row selection swaps, so the highlight, the detail and the
/// sandbox move together; <c>#ruleset-check</c> is swapped alone by the dry run and by "draft
/// change", so a poll can never wipe half-typed input.</para>
/// </summary>
public sealed class RulesetsModel(ConsoleData data, ILogger<RulesetsModel> logger) : PageModel
{
    /// <summary>Fixed, not operator-chosen. The window selector belongs to the Traffic surface;
    /// here the counts are context for a policy decision, and a day is what the mockup's
    /// "Denials 24h" column means.</summary>
    private const TrafficWindow Window = TrafficWindows.Default;

    /// <summary>Enough to see the shape of what a ruleset is being refused, without turning a
    /// detail panel into a log viewer — that is the Traffic surface's job.</summary>
    private const int ObservedHostLimit = 10;

    public PolicySnapshot? Policy { get; private set; }

    /// <summary>False when no audit workspace is configured. The denial columns then say so
    /// rather than rendering zeroes, which would read as "nothing is being refused".</summary>
    public bool TrafficConfigured { get; private set; } = true;

    /// <summary>
    /// Read for its <see cref="Freshness"/> as much as for its counts: it is the only audit DTO
    /// that carries a stamp, and it shares a cache entry with the Overview, so asking for it here
    /// costs nothing. <see cref="DecisionRow"/> has no stamp of its own, so this is what the
    /// traffic-fed panels on this surface date themselves by.
    /// </summary>
    public TrafficSummary? Traffic { get; private set; }

    public IReadOnlyList<RulesetRow> Rows { get; private set; } = [];

    /// <summary>Denials carrying an identity that no ruleset governs. The fallback governs them —
    /// attributing them to a ruleset that does not would be the exact wrong answer to the question
    /// this surface exists to answer.</summary>
    public int UnattributedDenials { get; private set; }

    /// <summary>Denials with no identity at all: credentials were presented and rejected. Not the
    /// 407 handshake, and not attributable to any ruleset.</summary>
    public int AnonymousDenials { get; private set; }

    /// <summary>The largest denial count in the window, so the list can point at where to look
    /// first rather than colouring every non-zero row red and pointing at nothing.</summary>
    public int BusiestDenials => Rows.Count == 0 ? 0 : Rows.Max(r => r.Denials);

    public RulesetView? Selected { get; private set; }

    /// <summary>Hosts the selected ruleset's subjects were refused, busiest first.</summary>
    public IReadOnlyList<ObservedHost> Denied { get; private set; } = [];

    /// <summary>Hosts a <c>report</c>-mode ruleset actually reached that <c>enforce</c> would have
    /// denied. What a promotion prompt leads with — never how long it has been in report.</summary>
    public IReadOnlyList<ObservedHost> OffList { get; private set; } = [];

    /// <summary>The candidate host set in the sandbox. Defaults to what the ruleset holds now, so
    /// the first dry run of an untouched form is an honest no-op rather than a deletion.</summary>
    public IReadOnlyList<string> Candidate { get; private set; } = [];

    public RulesetAction CandidateAction { get; private set; }

    public CheckResult? Check { get; private set; }

    /// <summary>Set when the control plane rejected the candidate. The API runs the same
    /// validation as a real write, so a rejection here is a genuine one rather than a guess.</summary>
    public string? CheckError { get; private set; }

    /// <summary>Set when a source could not be read, so a panel can say so instead of rendering
    /// zeroes that look like an all-clear.</summary>
    public string? Error => _errors.Message;

    private readonly LoadErrors _errors = new();

    // ---- rendering helpers ---------------------------------------------------------------------

    public PageHeadModel Head => new(
        "Rulesets",
        "Authored policy, as the control plane holds it. Read-only — changes are pushed by pipelines.",
        DocumentStamp);

    /// <summary>
    /// The state document's own last-modified time, which is what the mockup puts in the freshness
    /// slot here. Document-scoped and labelled as such: any ruleset write moves it, so it cannot
    /// answer "when did THIS ruleset change?" (design.md D6).
    /// </summary>
    private FreshnessModel? DocumentStamp => Policy?.Recency.LastModified is { } modified
        ? new FreshnessModel($"Document changed {modified.ToLocalTime():d MMM yyyy, HH:mm}")
        : null;

    /// <summary>
    /// Policy and the audit workspace on one stamp, because the ruleset list mixes them: the names
    /// and actions come from the control plane, the denial counts from Log Analytics, and they are
    /// read on different cadences. A cached value keeps the stamp of the fetch that produced it.
    /// </summary>
    public FreshnessModel? Stamp => (Policy, Traffic) switch
    {
        ({ } policy, { } traffic) => FreshnessModel.From(("policy", policy.Freshness), ("audit", traffic.Freshness)),
        ({ } policy, null) => FreshnessModel.From(policy.Freshness),
        _ => null,
    };

    /// <summary>The audit workspace's stamp alone, for the panels fed only by it.</summary>
    public FreshnessModel? AuditStamp => Traffic is { } traffic
        ? FreshnessModel.From(("audit", traffic.Freshness))
        : null;

    /// <summary>The whole surface re-renders on selection, so the highlight, the detail panel and
    /// the sandbox cannot disagree about which ruleset is being looked at.</summary>
    public string SelectUrl(string name) => $"/Rulesets?handler=Select&name={Uri.EscapeDataString(name)}";

    /// <summary>Pushed into the address bar on selection, so "the ruleset I am looking at" is a
    /// shareable URL and the back button works — the same reason the tabs are real routes.</summary>
    public string PageUrl(string name) => $"/Rulesets?name={Uri.EscapeDataString(name)}";

    public string ListUrl => Selected is { } selected
        ? $"/Rulesets?handler=ListPanel&name={Uri.EscapeDataString(selected.Name)}"
        : "/Rulesets?handler=ListPanel";

    /// <summary>Pre-fills the sandbox with the ruleset's current hosts plus one observed host. The
    /// operator still has to run the dry run and still has to apply it from the pipeline; this only
    /// saves retyping the list that a full replace obliges them to restate.</summary>
    public string DraftUrl(string host) => Selected is { } selected
        ? $"/Rulesets?handler=Draft&name={Uri.EscapeDataString(selected.Name)}&add={Uri.EscapeDataString(host)}"
        : "/Rulesets";

    /// <summary>The candidate host set as the textarea holds it — one host per line, which is the
    /// shape of the file the team keeps in its repository.</summary>
    public string CandidateText => string.Join('\n', Candidate);

    /// <summary>
    /// The body of the <c>PUT</c> that would apply the candidate, matching
    /// <c>docs/control-plane.md</c> exactly. Content only: subjects are omitted, so the push
    /// restates the hosts and the action and leaves the bindings alone.
    /// </summary>
    public string SnippetBody => JsonSerializer.Serialize(new
    {
        content = new { allowed_hosts = Candidate, action = CandidateAction.ToWire() },
    });

    // ---- handlers ------------------------------------------------------------------------------

    public async Task OnGetAsync(string? name, CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Rulesets";
        ViewData["Surface"] = Surface.Rulesets.Key;

        await LoadAsync(name, cancellationToken);
    }

    /// <summary>The list's own 60-second refresh. Carries the selection so a poll cannot silently
    /// move the highlight off the row the operator is reading.</summary>
    public async Task<IActionResult> OnGetListPanelAsync(string? name, CancellationToken cancellationToken)
    {
        await LoadAsync(name, cancellationToken);
        return Partial("_RulesetList", this);
    }

    public async Task<IActionResult> OnGetSelectAsync(string? name, CancellationToken cancellationToken)
    {
        await LoadAsync(name, cancellationToken);
        return Partial("_RulesetBody", this);
    }

    /// <summary>"Draft change" on an observed-but-denied host: the sandbox alone, pre-filled.</summary>
    public async Task<IActionResult> OnGetDraftAsync(string? name, string? add, CancellationToken cancellationToken)
    {
        await LoadAsync(name, cancellationToken);

        if (Selected is not null && add is { Length: > 0 })
        {
            Candidate = Merge(Candidate, WithoutPort(add));
        }

        return Partial("_RulesetCheck", this);
    }

    /// <summary>
    /// The dry run — <b>the only non-GET the console makes</b>, and it writes nothing. It is a
    /// <c>POST</c> because that is the verb the API's <c>:check</c> endpoint takes, not because
    /// anything here changes state; <c>Portal.Tests/ReadOnlyTests</c> asserts that every
    /// write-shaped call in the portal is this one.
    /// </summary>
    public async Task<IActionResult> OnPostCheckAsync(
        string? name,
        string? hosts,
        string? action,
        CancellationToken cancellationToken)
    {
        await LoadAsync(name, cancellationToken);

        if (Selected is not { } selected)
        {
            return Partial("_RulesetCheck", this);
        }

        Candidate = ParseHosts(hosts);
        // Never compared by hand, and never widened by accident: an absent, empty or unrecognised
        // action posted back reads as enforce, exactly as the proxy would read it.
        CandidateAction = RulesetActions.Normalize(action);

        try
        {
            Check = await data.CheckAsync(selected.Name, Candidate, CandidateAction, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "the control plane rejected the dry run for {Ruleset}", selected.Name);
            CheckError = "The control plane rejected this candidate. It runs the same validation as "
                + "a real push, so this is a genuine rejection rather than a client-side guess.";
        }

        return Partial("_RulesetCheck", this);
    }

    // ---- loading -------------------------------------------------------------------------------

    private async Task LoadAsync(string? name, CancellationToken cancellationToken)
    {
        Policy = await TryAsync(() => data.PolicyAsync(cancellationToken), "policy");

        var denials = Array.Empty<DecisionRow>() as IReadOnlyList<DecisionRow>;
        var findings = Array.Empty<ReportFinding>() as IReadOnlyList<ReportFinding>;

        TrafficConfigured = data.TrafficIsConfigured;
        if (TrafficConfigured)
        {
            Traffic = await TryAsync(() => data.TrafficSummaryAsync(Window, cancellationToken), "traffic summary");
            denials = await TryAsync(() => data.DenialsAsync(Window, cancellationToken), "denials") ?? [];
            findings = await TryAsync(
                () => data.ReportFindingsAsync(Window, cancellationToken), "report findings") ?? [];
        }

        BuildRows(denials, findings);
        Select(name);
        await LoadSelectedAsync(cancellationToken);
    }

    /// <summary>
    /// The list, with each ruleset's traffic joined onto it.
    ///
    /// <para>The join is the audit table's <c>Role</c> against <c>subjects[].appid</c> — the
    /// validated JWT claim, and the only key that is an identity. A denial whose role matches no
    /// ruleset is counted separately and reported against the fallback; it is never quietly added
    /// to a ruleset that does not govern it.</para>
    /// </summary>
    private void BuildRows(IReadOnlyList<DecisionRow> denials, IReadOnlyList<ReportFinding> findings)
    {
        if (Policy is not { } policy)
        {
            Rows = [];
            return;
        }

        var denialsByRole = denials
            .Where(d => d.Role is { Length: > 0 })
            .GroupBy(d => d.Role!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        Rows =
        [
            .. policy.Rulesets.Select(ruleset =>
            {
                var appids = Appids(ruleset);

                // Report mode denies nothing, so its signal is the hosts enforce WOULD have denied.
                var offList = ruleset.Action == RulesetAction.Report
                    ? findings
                        .Where(f => appids.Contains(f.Role, StringComparer.OrdinalIgnoreCase))
                        .Select(f => f.Host)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count()
                    : 0;

                return new RulesetRow(
                    ruleset,
                    appids.Sum(appid => denialsByRole.GetValueOrDefault(appid)),
                    offList);
            }),
        ];

        // Attributed to the fallback, because that is what governs them.
        UnattributedDenials = denials.Count(d => d.Role is { Length: > 0 } && policy.Governing(d.Role) is null);
        AnonymousDenials = denials.Count(d => string.IsNullOrEmpty(d.Role));
    }

    /// <summary>The mockup opens with a ruleset selected; an operator arriving at the surface with
    /// an empty detail panel would have nothing to read.</summary>
    private void Select(string? name)
    {
        if (Policy is not { Rulesets.Count: > 0 } policy)
        {
            return;
        }

        Selected = policy.Rulesets.FirstOrDefault(r =>
            string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) ?? policy.Rulesets[0];

        Candidate = Selected.AllowedHosts;
        CandidateAction = Selected.Action;
    }

    private async Task LoadSelectedAsync(CancellationToken cancellationToken)
    {
        if (Selected is not { } selected || !TrafficConfigured)
        {
            return;
        }

        // Two Log Analytics reads per governed identity, which is why this is bounded rather than
        // sequential: a ruleset with a dozen subjects made the detail panel a dozen round trips
        // deep. The limit exists because the other end is a metered query service and a ruleset's
        // subject list has no ceiling — unbounded fan-out would turn one operator's click into a
        // burst the workspace charges for.
        //
        // Only appid subjects. A netid subject joins on a source address, which the audit rows
        // carry but which is not an identity — the panel says so rather than producing a table that
        // looks like the same answer.
        var denied = new ConcurrentBag<DecisionRow>();
        var offList = new ConcurrentBag<ReportFinding>();

        await Parallel.ForEachAsync(
            Appids(selected),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (appid, token) =>
            {
                var decisions = await TryAsync(
                    () => data.DecisionsForRoleAsync(appid, Window, token), "decisions") ?? [];

                foreach (var decision in decisions.Where(d => !d.Allowed))
                {
                    denied.Add(decision);
                }

                if (selected.Action != RulesetAction.Report)
                {
                    return;
                }

                var findings = await TryAsync(
                    () => data.ReportFindingsForRoleAsync(appid, Window, token), "report findings") ?? [];

                foreach (var finding in findings)
                {
                    offList.Add(finding);
                }
            });

        Denied = Group(denied.Select(d => (d.Host, 1, d.TimeGenerated)));
        OffList = Group(offList.Select(f => (f.Host, f.Attempts, f.LastSeen)));
    }

    // ---- pure helpers, so the joins above are testable without a web host ----------------------

    /// <summary>The validated identities a ruleset governs — the only ones that join to traffic.</summary>
    internal static IReadOnlyList<string> Appids(RulesetView ruleset) =>
        [.. ruleset.Subjects.Where(s => !s.IsNetwork && s.Appid is { Length: > 0 }).Select(s => s.Appid!)];

    /// <summary>
    /// A free-typed host list, read as forgivingly as a pasted file allows: newlines, commas and
    /// whitespace all separate, blanks vanish, and duplicates collapse. Nothing here validates —
    /// the control plane does that, and a second opinion in the console would eventually disagree
    /// with the service that actually decides.
    /// </summary>
    internal static IReadOnlyList<string> ParseHosts(string? text) =>
        [.. (text ?? string.Empty)
            .Split(['\n', '\r', ',', ' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Adds one host to a candidate list without disturbing the order of the rest.</summary>
    internal static IReadOnlyList<string> Merge(IReadOnlyList<string> hosts, string host) =>
        hosts.Contains(host, StringComparer.OrdinalIgnoreCase) ? hosts : [.. hosts, host];

    /// <summary>
    /// An audit row's host carries the CONNECT port (<c>api.vendor.com:443</c>); an allowlist entry
    /// is the name alone. Drafting a change from an observed host has to cross that gap, and doing
    /// it here rather than in the operator's head is the difference between a snippet that applies
    /// and one the control plane rejects.
    /// </summary>
    internal static string WithoutPort(string host) =>
        host.LastIndexOf(':') is var colon && colon > 0 && host[(colon + 1)..].All(char.IsAsciiDigit)
            ? host[..colon]
            : host;

    private static IReadOnlyList<ObservedHost> Group(IEnumerable<(string Host, int Attempts, DateTimeOffset Seen)> rows) =>
        [.. rows
            .GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ObservedHost(g.Key, g.Sum(r => r.Attempts), g.Max(r => r.Seen)))
            .OrderByDescending(h => h.Attempts)
            .ThenBy(h => h.Host, StringComparer.Ordinal)
            .Take(ObservedHostLimit)];

    /// <summary>One failing source degrades its own panel and no other — a Log Analytics hiccup
    /// must not cost the operator the policy the control plane returned perfectly well.</summary>
    private async Task<T?> TryAsync<T>(Func<Task<T>> load, string what)
    {
        try
        {
            return await load();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "the rulesets surface could not read {What}", what);
            _errors.Record(what);
            return default;
        }
    }
}

/// <summary>
/// A ruleset with the traffic it produced in the window.
/// </summary>
/// <param name="Denials">Refusals attributed by validated identity. Zero for a ruleset whose
/// subjects are all network ranges — see <see cref="RulesetView.IsNetworkAttributed"/>, which is
/// why such a row shows no count rather than a zero it has not earned.</param>
/// <param name="OffListHosts">Distinct hosts a <c>report</c>-mode ruleset reached that
/// <c>enforce</c> would have denied. The promotion signal; time in report is not one.</param>
public sealed record RulesetRow(RulesetView Ruleset, int Denials, int OffListHosts)
{
    /// <summary>How the subject set reads: "2 appid", "1 netid", or both.</summary>
    public string Subjects
    {
        get
        {
            var network = Ruleset.Subjects.Count(s => s.IsNetwork);
            var identity = Ruleset.Subjects.Count - network;

            return (identity, network) switch
            {
                (0, 0) => "none",
                (_, 0) => $"{identity} appid",
                (0, _) => $"{network} netid",
                _ => $"{identity} appid · {network} netid",
            };
        }
    }

    /// <summary>Open mode does not consult the host list at all, so the mockup shows a dash rather
    /// than a count that implies the list is doing something.</summary>
    public string HostCount => Ruleset.Action == RulesetAction.Open
        ? "—"
        : Ruleset.AllowedHosts.Count.ToString();

    /// <summary>Owners are GUIDs and the column is narrow; the head and tail are what an operator
    /// actually recognises. The detail panel carries the whole value.</summary>
    public string Owner => Ruleset.Owner is { Length: > 12 } owner
        ? $"{owner[..4]}…{owner[^4..]}"
        : Ruleset.Owner ?? "—";
}

/// <summary>A destination a ruleset's subjects reached for, with how often and how recently.</summary>
public sealed record ObservedHost(string Host, int Attempts, DateTimeOffset LastSeen);
