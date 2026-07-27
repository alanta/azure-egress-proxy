using System.Net;
using System.Text.RegularExpressions;
using ControlPlane.Model;

namespace ControlPlane.Policy;

/// <summary>The three platform-granted verbs. Reads need none of them.</summary>
public static class Verbs
{
    public const string Onboard = "onboard";
    public const string Update = "update";
    public const string Offboard = "offboard";
}

/// <summary>What a push would change, before it is applied. Returned by <c>:check</c> so a pipeline
/// can gate on unexpected changes — the only control on widening an already-enforcing ruleset.</summary>
public sealed record HostDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

public sealed record PolicyError(HttpStatusCode Status, string Message);

/// <summary>
/// The rules that make a write safe, kept together and free of I/O so they can be tested directly
/// and re-evaluated on every read-modify-write attempt.
/// </summary>
public static partial class RulesetPolicy
{
    [GeneratedRegex(@"^(?=.{1,253}$)(?:(?!-)[A-Za-z0-9-]{1,63}(?<!-)\.)+(?:[A-Za-z]{2,63})$")]
    private static partial Regex HostnameRegex { get; }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex NameRegex { get; }

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex AppidRegex { get; }

    [GeneratedRegex(@"^(([0-9]{1,3}\.){3}[0-9]{1,3})\/(3[0-2]|[12]?[0-9])$")]
    private static partial Regex NetidRegex { get; }

    /// <summary>
    /// Authorizes a write. Ownership is checked first: trust-on-first-use means the onboarding
    /// identity holds update/offboard on what it created without any platform grant. Otherwise a
    /// platform grant must carry the verb, scoped to this ruleset (or unscoped). Reads never reach
    /// here — the egress posture is transparent by design.
    /// </summary>
    public static bool IsAuthorized(StateDocument state, string caller, string verb, string rulesetName, Ruleset? existing)
    {
        if (existing?.Owner is { } owner
            && string.Equals(owner, caller, StringComparison.OrdinalIgnoreCase)
            && verb is Verbs.Update or Verbs.Offboard)
        {
            return true;
        }

        return state.Grants.Any(g =>
            string.Equals(g.Identity, caller, StringComparison.OrdinalIgnoreCase)
            && g.Verbs.Contains(verb, StringComparer.OrdinalIgnoreCase)
            // onboard is registry-wide by nature: the ruleset does not exist yet, so there is
            // nothing for a scope to name.
            && (verb == Verbs.Onboard || g.Rulesets is null || g.Rulesets.Contains(rulesetName, StringComparer.Ordinal)));
    }

    /// <summary>
    /// The anti-hijack rule the whole design exists to protect: the identity writing a ruleset is
    /// never a workload the ruleset governs, so a compromised workload cannot widen its own
    /// allowlist. Checked against the subjects being written, and against every other ruleset's
    /// subjects too — a workload must not be able to write anyone's rules.
    /// </summary>
    public static PolicyError? CheckWriterIsNotSubject(StateDocument state, string caller, IEnumerable<Subject> subjects)
    {
        var all = subjects.Concat(state.Rulesets.SelectMany(r => r.Subjects));
        return all.Any(s => s.Appid is { } appid && string.Equals(appid, caller, StringComparison.OrdinalIgnoreCase))
            ? new PolicyError(HttpStatusCode.Forbidden,
                $"identity '{caller}' is a subject governed by the allowlist and may never write rulesets (writer must not be subject)")
            : null;
    }

    /// <summary>
    /// One-to-one: a subject belongs to at most one ruleset, so effective policy is always taken
    /// from exactly one place and the renderer never has to compose. First-come, which is what
    /// protects an already-onboarded subject from being claimed by another team.
    /// </summary>
    public static PolicyError? CheckSubjectsAreUnclaimed(StateDocument state, string rulesetName, IReadOnlyList<Subject> subjects)
    {
        foreach (var subject in subjects)
        {
            var claimant = state.Rulesets.FirstOrDefault(r =>
                !string.Equals(r.Name, rulesetName, StringComparison.Ordinal)
                && r.Subjects.Any(s => s.Key == subject.Key));

            if (claimant is not null)
            {
                return new PolicyError(HttpStatusCode.Conflict,
                    $"subject '{subject}' already belongs to ruleset '{claimant.Name}'; a subject belongs to at most one ruleset");
            }
        }

        var duplicate = subjects.GroupBy(s => s.Key).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null
            ? null
            : new PolicyError(HttpStatusCode.BadRequest, $"subject '{duplicate.First()}' is listed twice");
    }

    public static PolicyError? ValidateName(string name) =>
        NameRegex.IsMatch(name)
            ? null
            : new PolicyError(HttpStatusCode.BadRequest, $"ruleset name '{name}' must be a lowercase slug (a-z, 0-9, '-')");

    public static PolicyError? ValidateSubjects(IReadOnlyList<Subject> subjects)
    {
        if (subjects.Count == 0)
        {
            return new PolicyError(HttpStatusCode.BadRequest, "a ruleset must declare at least one subject");
        }

        foreach (var subject in subjects)
        {
            var hasAppid = !string.IsNullOrWhiteSpace(subject.Appid);
            var hasNetid = !string.IsNullOrWhiteSpace(subject.Netid);

            if (hasAppid == hasNetid)
            {
                return new PolicyError(HttpStatusCode.BadRequest, "each subject sets exactly one of 'appid' or 'netid'");
            }

            if (hasAppid && !AppidRegex.IsMatch(subject.Appid!))
            {
                return new PolicyError(HttpStatusCode.BadRequest, $"subject appid '{subject.Appid}' is not a GUID");
            }

            if (hasNetid && !IsIPv4Cidr(subject.Netid!))
            {
                return new PolicyError(HttpStatusCode.BadRequest, $"subject netid '{subject.Netid}' is not a CIDR");
            }
        }

        return null;
    }

    /// <summary>
    /// The shape regex alone would accept 999.999.999.999/24, which the proxy's net.ParseCIDR then
    /// rejects — silently dropping the subnet mapping, so the ruleset would exist in the control
    /// plane and govern nothing. Parsing the octets here keeps the two ends agreeing.
    /// </summary>
    private static bool IsIPv4Cidr(string netid)
    {
        if (!NetidRegex.IsMatch(netid))
        {
            return false;
        }

        var slash = netid.IndexOf('/');
        return netid[..slash].Split('.').All(octet => byte.TryParse(octet, out _));
    }

    public static PolicyError? ValidateContent(RulesetContent content)
    {
        foreach (var host in content.AllowedHosts)
        {
            if (!HostnameRegex.IsMatch(host))
            {
                return new PolicyError(HttpStatusCode.BadRequest,
                    $"'{host}' is not a valid FQDN; allowed_hosts holds exact hostnames, not URLs, wildcards, or IPs");
            }
        }

        var duplicate = content.AllowedHosts
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            return new PolicyError(HttpStatusCode.BadRequest, $"host '{duplicate.Key}' is listed twice");
        }

        return content.Action is null or "enforce" or "report" or "open"
            ? null
            : new PolicyError(HttpStatusCode.BadRequest, $"action '{content.Action}' must be enforce, report, or open");
    }

    public static HostDiff Diff(IEnumerable<string> current, IEnumerable<string> proposed)
    {
        var before = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        var after = new HashSet<string>(proposed, StringComparer.OrdinalIgnoreCase);

        return new HostDiff(
            [.. after.Except(before, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)],
            [.. before.Except(after, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// <c>report</c> is the onboarding DEFAULT, not an override: a ruleset created without an
    /// explicit action starts in <c>report</c> so a new workload is observed before it is
    /// enforced. An explicitly requested <c>enforce</c> is honoured — <c>report</c> passes all
    /// traffic and only logs, so coercing it over an explicit request would hand a new workload
    /// *more* egress than it asked for, which is the wrong way round for this system.
    ///
    /// Nothing here ever lowers an existing ruleset's action either: a ruleset's evaluation is
    /// uniform, so a new host cannot sit in <c>report</c> on its own, and dropping the whole
    /// ruleset to <c>report</c> because a host was added would weaken rules already in force.
    /// Widening is governed by the audit trail and the <c>:check</c> diff instead.
    /// </summary>
    public static string EffectiveAction(bool isOnboard, string? requested) =>
        isOnboard && string.IsNullOrWhiteSpace(requested)
            ? "report"
            : AllowlistRenderer.NormalizeAction(requested);
}
