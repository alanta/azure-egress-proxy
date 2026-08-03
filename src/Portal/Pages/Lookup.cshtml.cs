using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Clients;
using Portal.Components;

namespace Portal.Pages;

/// <summary>
/// The Lookup surface — one box that answers the two questions an operator arrives with: *what
/// governs this workload?* and *who can reach this host?*
///
/// <para>Both are joins over the same policy snapshot, so the surface reads nothing but
/// <see cref="ConsoleData.PolicyAsync"/> and does its work in memory. The live search is therefore
/// a cache read per keystroke-batch rather than a control-plane call, which is what makes
/// <c>delay:300ms</c> a courtesy rather than a necessity.</para>
/// </summary>
public sealed class LookupModel(ConsoleData data, ILogger<LookupModel> logger) : PageModel
{
    /// <summary>What the operator typed. Bound from the query string so a resolution is a real URL
    /// — the same deep-linking property the tab bar gets from being real routes (design.md D8).</summary>
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    public PolicySnapshot? Policy { get; private set; }

    /// <summary>Which of the three things the query is. Drives which card the partial renders,
    /// because an identity and a hostname are different questions with different answers.</summary>
    public LookupKind Kind { get; private set; }

    /// <summary>Set for an identity query. Null for a hostname, and never null merely because
    /// nothing matched — see <see cref="LookupResolution.FallsToFallback"/>.</summary>
    public LookupResolution? Resolution { get; private set; }

    /// <summary>Set for a hostname query: every ruleset that reaches it, and why.</summary>
    public IReadOnlyList<ReverseMatch> Reverse { get; private set; } = [];

    /// <summary>True when traffic matching no ruleset at all reaches the queried host, because the
    /// fallback lists it. The floor is part of the answer to "who can reach this?".</summary>
    public bool FallbackReaches { get; private set; }

    /// <summary>The query as it was matched — trimmed, and lower-cased for a hostname. Rendered
    /// rather than the raw input so the card's title says what was actually looked up.</summary>
    public string Normalized { get; private set; } = string.Empty;

    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Lookup";
        ViewData["Surface"] = Surface.Lookup.Key;

        await LoadAsync(cancellationToken);
    }

    /// <summary>
    /// The swap target behind <c>hx-trigger="input changed delay:300ms"</c>. A GET with the query
    /// in the URL: the portal writes nothing, and a live search that issued a POST would be the
    /// first crack in that (README § Read-only).
    /// </summary>
    public async Task<IActionResult> OnGetResolveAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        return Partial("_LookupResult", this);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Policy = await data.PolicyAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The whole surface is one source, so a failed read leaves nothing to render — but it
            // says so rather than answering "nothing governs this subject", which would be a lie
            // in the permissive direction.
            logger.LogError(e, "the lookup surface could not read policy");
            Error = "the control plane could not be read";
            return;
        }

        Kind = Lookups.Classify(Query);
        Normalized = Lookups.Normalize(Query, Kind);

        if (Kind is LookupKind.Empty)
        {
            return;
        }

        if (Kind is LookupKind.Host)
        {
            Reverse = Lookups.Reverse(Policy, Normalized);
            FallbackReaches = Lookups.FallbackReaches(Policy.Fallback, Normalized);
            return;
        }

        Resolution = Lookups.Resolve(Policy, Normalized, Kind);
    }
}

/// <summary>What the operator typed, as the surface understands it.</summary>
public enum LookupKind
{
    Empty,

    /// <summary>A managed-identity client ID — the <c>appid</c>/<c>azp</c> claim the allowlist
    /// keys on, and the only kind of subject that is a validated identity.</summary>
    Appid,

    /// <summary>A source address or CIDR. Matched against <c>netid</c> subjects, which is a
    /// network match and not an identity — the resolution must say so.</summary>
    Netid,

    Host,
}

/// <summary>
/// What governs a subject.
/// </summary>
/// <param name="Ruleset">The governing ruleset, or null when nothing matched — which means the
/// fallback governs it. Never attribute a subject to a ruleset that does not list it.</param>
/// <param name="Kind">Which sort of subject matched, so the card can say whether the join was on a
/// validated claim or on a source address.</param>
/// <param name="Match">One line describing the match, written from the operator's side.</param>
/// <param name="SharesWith">The other subjects on the same ruleset — the blast radius of a change
/// to it, which is the thing an operator is usually one step away from asking.</param>
public sealed record LookupResolution(
    RulesetView? Ruleset,
    LookupKind Kind,
    string Match,
    IReadOnlyList<string> SharesWith)
{
    public bool FallsToFallback => Ruleset is null;
}

/// <summary>Why a ruleset appears in the reverse index. Not cosmetic: two of the three reasons
/// have nothing to do with the host that was typed.</summary>
public enum ReverseReason
{
    /// <summary>The host is on the ruleset's allowlist.</summary>
    Listed,

    /// <summary>The action is <c>report</c> and the host is not listed — off-list hosts are
    /// permitted anyway and logged with <c>EnforceWouldDeny</c>.</summary>
    Report,

    /// <summary>The action is <c>open</c>: every host is reachable, allowlist or not.</summary>
    Open,
}

/// <summary>One row of the reverse index.</summary>
public sealed record ReverseMatch(RulesetView Ruleset, ReverseReason Reason)
{
    /// <summary>The "why" column. A ruleset that reaches the host without listing it must not read
    /// as though the host were on its allowlist — that would misreport the posture as more
    /// deliberate than it is.</summary>
    public string Why => Reason switch
    {
        ReverseReason.Open => "open — reaches every host, list or not",
        ReverseReason.Report => "report — off-list hosts are permitted and logged",
        _ => "the host is on its allowlist",
    };

    /// <summary>The subject column: how many, and of which kind. The kind is carried because a
    /// netid ruleset is joined to traffic by source address rather than by a validated claim.</summary>
    public string Subjects
    {
        get
        {
            var appids = Ruleset.Subjects.Count(s => !s.IsNetwork);
            var netids = Ruleset.Subjects.Count - appids;

            return string.Join(" + ", new[]
            {
                appids > 0 ? $"{appids} appid" : null,
                netids > 0 ? $"{netids} netid" : null,
            }.Where(part => part is not null));
        }
    }
}

/// <summary>
/// The joins themselves, kept pure and static so they can be exercised directly — the answers this
/// surface gives are the ones a mistake would be least visible in.
/// </summary>
internal static class Lookups
{
    public static LookupKind Classify(string? query)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return LookupKind.Empty;
        }

        // A client ID is a GUID and a netid is an address or a CIDR; neither shape is a legal
        // hostname, so the classification is unambiguous rather than heuristic.
        if (Guid.TryParse(trimmed, out _))
        {
            return LookupKind.Appid;
        }

        return IPAddress.TryParse(trimmed, out _) || IPNetwork.TryParse(trimmed, out _)
            ? LookupKind.Netid
            : LookupKind.Host;
    }

    /// <summary>Hostnames are case-insensitive; a client ID and a CIDR are compared as typed but
    /// trimmed, because a paste picks up whitespace far more often than it picks up case.</summary>
    public static string Normalize(string? query, LookupKind kind)
    {
        var trimmed = (query ?? string.Empty).Trim();
        return kind is LookupKind.Host ? trimmed.ToLowerInvariant() : trimmed;
    }

    public static LookupResolution Resolve(PolicySnapshot policy, string query, LookupKind kind) =>
        kind is LookupKind.Netid ? ResolveNetwork(policy, query) : ResolveIdentity(policy, query);

    private static LookupResolution ResolveIdentity(PolicySnapshot policy, string appid)
    {
        // The one join the console exists to close, and it already lives on the snapshot. Null
        // means the fallback governs the subject — say that, never attribute it to a ruleset.
        var ruleset = policy.Governing(appid);

        return new LookupResolution(
            ruleset,
            LookupKind.Appid,
            "subject appid — the validated token claim the proxy keys on",
            Others(ruleset, s => string.Equals(s.Appid, appid, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Resolved here rather than on <see cref="PolicySnapshot"/> because a netid match is not the
    /// same kind of answer: it is a source address falling inside a subnet, which is not an
    /// identity and is never treated as one. There is deliberately no <c>Governing(netid)</c>
    /// helper to reach for.
    /// </summary>
    private static LookupResolution ResolveNetwork(PolicySnapshot policy, string query)
    {
        foreach (var ruleset in policy.Rulesets)
        {
            foreach (var subject in ruleset.Subjects.Where(s => s.IsNetwork))
            {
                if (!NetworkMatches(subject.Netid!, query, out var exact))
                {
                    continue;
                }

                return new LookupResolution(
                    ruleset,
                    LookupKind.Netid,
                    exact
                        ? $"subject netid {subject.Netid} — matched by source address, not by a validated claim"
                        : $"inside subject netid {subject.Netid} — matched by source address, not by a validated claim",
                    Others(ruleset, s => ReferenceEquals(s, subject)));
            }
        }

        return new LookupResolution(null, LookupKind.Netid, "no netid subject covers this address", []);
    }

    /// <summary>An address inside the subnet counts, which is the whole point of a CIDR subject;
    /// an identical CIDR counts too, for an operator pasting the subject straight back in.</summary>
    private static bool NetworkMatches(string netid, string query, out bool exact)
    {
        exact = string.Equals(netid.Trim(), query, StringComparison.OrdinalIgnoreCase);
        if (exact)
        {
            return true;
        }

        return IPNetwork.TryParse(netid.Trim(), out var subnet)
            && IPAddress.TryParse(query, out var address)
            && subnet.Contains(address);
    }

    private static IReadOnlyList<string> Others(RulesetView? ruleset, Func<SubjectView, bool> self) =>
        ruleset is null ? [] : [.. ruleset.Subjects.Where(s => !self(s)).Select(s => s.Display)];

    /// <summary>
    /// Which rulesets reach a host.
    ///
    /// <para>An <c>open</c> ruleset reaches it and every other host, and a <c>report</c> ruleset
    /// permits off-list hosts too. Both belong here — a reverse index that listed only the
    /// allowlists would tell an operator that fewer workloads can reach the host than actually
    /// can, which is the one direction a security console must not be wrong in. They are labelled
    /// with the reason so neither reads as a deliberate grant of this host.</para>
    /// </summary>
    public static IReadOnlyList<ReverseMatch> Reverse(PolicySnapshot policy, string host) =>
    [
        .. policy.Rulesets
            .Select(ruleset => (ruleset, reason: ReasonFor(ruleset, host)))
            .Where(match => match.reason is not null)
            .Select(match => new ReverseMatch(match.ruleset, match.reason!.Value))
            // Deliberate grants first, then the two that reach the host without naming it, so the
            // table reads as "who was given this, and who gets it anyway".
            .OrderBy(match => match.Reason)
            .ThenBy(match => match.Ruleset.Name, StringComparer.OrdinalIgnoreCase),
    ];

    private static ReverseReason? ReasonFor(RulesetView ruleset, string host)
    {
        if (ruleset.AllowedHosts.Any(h => string.Equals(h.Trim(), host, StringComparison.OrdinalIgnoreCase)))
        {
            return ReverseReason.Listed;
        }

        // Never a string comparison: the action arrived normalized, and open and report are the
        // only two states that are ever implied by anything other than the list itself.
        return ruleset.Action switch
        {
            RulesetAction.Open => ReverseReason.Open,
            RulesetAction.Report => ReverseReason.Report,
            _ => null,
        };
    }

    /// <summary>Whether the floor itself reaches the host, so traffic matching no ruleset at all is
    /// part of the answer rather than an omission.</summary>
    public static bool FallbackReaches(FallbackView fallback, string host) =>
        !fallback.DenyAll
        && fallback.AllowedHosts.Any(h => string.Equals(h.Trim(), host, StringComparison.OrdinalIgnoreCase));
}
