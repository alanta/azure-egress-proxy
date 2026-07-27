using System.Text.Json.Serialization;

namespace ControlPlane.Model;

/// <summary>
/// The proxy's document (<c>egress-config/allowlist.json</c>) — a FROZEN contract. The Go proxy
/// parses exactly this shape (proxy/managed.go), so nothing here may change without a proxy
/// change; that is the whole reason the ruleset model lives in its own store instead of
/// renaming these fields. Mirrors allowlist/allowlist.schema.json.
/// </summary>
public sealed record AllowlistDocument
{
    public List<AllowlistModule> Modules { get; init; } = [];
    public Fallback? Fallback { get; init; }
}

public sealed record AllowlistModule
{
    public required string Id { get; init; }

    /// <summary>The workload's managed-identity client ID; the ACL key in the jwt/basic-jwt modes.</summary>
    public string? Appid { get; init; }

    /// <summary>Source CIDR; the ACL key in netid mode only.</summary>
    public string? Subnet { get; init; }

    public List<string> AllowedHosts { get; init; } = [];

    /// <summary>Always written explicitly, so the enforce/report/open choice is visible in the blob.</summary>
    public required string Action { get; init; }
}

/// <summary>
/// Projects the control plane's state onto the proxy's frozen document. Pure and deterministic:
/// the same state always renders the same bytes, which is what lets the ETag be a truthful change
/// signal and what the round-trip test pins.
/// </summary>
public static class AllowlistRenderer
{
    public static AllowlistDocument Render(StateDocument state) => new()
    {
        // Sorted by name so the output is stable across state whose ruleset order differs; the
        // proxy sorts by id anyway, but a stable render keeps blob diffs and ETags meaningful.
        Modules = [.. state.Rulesets.OrderBy(r => r.Name, StringComparer.Ordinal).SelectMany(RenderOne)],
        Fallback = state.Fallback,
    };

    /// <summary>
    /// One module per subject: the proxy keys the ACL on a single identity per entry, so a ruleset
    /// governing several subjects fans out into one entry each, all carrying the same content.
    /// A single-subject ruleset keeps the ruleset name as its id, which is why an existing
    /// hand-written allowlist.json renders back identically.
    /// </summary>
    private static IEnumerable<AllowlistModule> RenderOne(Ruleset ruleset)
    {
        var action = NormalizeAction(ruleset.Content.Action);
        var single = ruleset.Subjects.Count == 1;

        return ruleset.Subjects.Select((subject, index) => new AllowlistModule
        {
            Id = single ? ruleset.Name : $"{ruleset.Name}-{index + 1}",
            Appid = subject.Appid,
            Subnet = subject.Netid,
            AllowedHosts = [.. ruleset.Content.AllowedHosts],
            Action = action,
        });
    }

    /// <summary>
    /// Secure by default, matching the proxy's own normalizeAction: anything that is not an
    /// explicit permissive mode renders as <c>enforce</c>.
    /// </summary>
    public static string NormalizeAction(string? action) => action?.Trim().ToLowerInvariant() switch
    {
        "report" => "report",
        "open" => "open",
        _ => "enforce",
    };
}
