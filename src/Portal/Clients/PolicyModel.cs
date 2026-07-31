namespace Portal.Clients;

/// <summary>
/// When the values a panel is rendering were true. Every surface stamps this — design.md D4:
/// Azure Monitor is 1-minute, Log Analytics ingestion is minutes, and the portal caches on top of
/// both. The UI states freshness rather than implying immediacy, so this travels with the data
/// instead of being reconstructed at render time.
/// </summary>
/// <param name="AsOf">When the portal fetched the underlying data.</param>
/// <param name="Cached">True when this was served from the response cache rather than fetched.</param>
public sealed record Freshness(DateTimeOffset AsOf, bool Cached = false)
{
    public static Freshness Now => new(DateTimeOffset.UtcNow);
}

/// <summary>
/// The state document's own recency, straight from the control-plane read.
///
/// <b>Document-scoped.</b> Any ruleset write moves it, so it answers "when did the configuration
/// last change?" and never "when did THIS ruleset change?". A surface that labels it per-ruleset
/// is lying — per-ruleset stamps need a write-path change deferred to #33.
/// </summary>
public sealed record Recency(DateTimeOffset? LastModified, string? ETag);

/// <summary>
/// A ruleset's uniform action. Named rather than stringly-typed because the portal must
/// normalize exactly as the proxy does, and a raw string invites a surface to compare it by hand.
/// </summary>
public enum RulesetAction
{
    /// <summary>Off-list hosts are denied. The secure default.</summary>
    Enforce,

    /// <summary>Off-list hosts are permitted and logged with <c>EnforceWouldDeny</c>. A
    /// legitimate steady state, not rot — see design.md D9: time in report is not a signal.</summary>
    Report,

    /// <summary>Every host is reachable, allowlist or not.</summary>
    Open,
}

public static class RulesetActions
{
    /// <summary>
    /// Matches <c>AllowlistRenderer.NormalizeAction</c> and the proxy's own <c>normalizeAction</c>,
    /// deliberately: absent, empty, or unrecognised is <see cref="RulesetAction.Enforce"/>. A
    /// console that displayed an unrecognised action as anything more permissive than what the
    /// proxy will actually do would misreport the posture in the safe-looking direction, which is
    /// the one failure a security console cannot have. Defaults never widen.
    /// </summary>
    public static RulesetAction Normalize(string? action) => action?.Trim().ToLowerInvariant() switch
    {
        "report" => RulesetAction.Report,
        "open" => RulesetAction.Open,
        _ => RulesetAction.Enforce,
    };

    /// <summary>The wire spelling, for the copyable snippets the console emits.</summary>
    public static string ToWire(this RulesetAction action) => action switch
    {
        RulesetAction.Report => "report",
        RulesetAction.Open => "open",
        _ => "enforce",
    };
}

/// <summary>
/// One governed client. Exactly one of <see cref="Appid"/> and <see cref="Netid"/> is set.
///
/// The distinction is not cosmetic: an <c>appid</c> subject joins to traffic on the validated JWT
/// claim, a <c>netid</c> subject only on a source address. Surfaces must show which they are
/// looking at (see <see cref="IsNetwork"/>) rather than degrading silently — a source address is
/// not an identity, and the console should not imply it is.
/// </summary>
public sealed record SubjectView(string? Appid, string? Netid)
{
    public bool IsNetwork => Netid is not null;

    /// <summary>What to render. The identifier itself, because here it IS the identifier.</summary>
    public string Display => Appid ?? Netid ?? "(empty)";
}

/// <summary>A ruleset as the console reads it. Read-only: there is no path from this record back
/// to a write, by construction.</summary>
public sealed record RulesetView(
    string Name,
    IReadOnlyList<SubjectView> Subjects,
    IReadOnlyList<string> AllowedHosts,
    RulesetAction Action,
    string? Owner)
{
    /// <summary>True when the ruleset is joined to traffic by source address only, because every
    /// subject is a <c>netid</c>. The traffic view must say so.</summary>
    public bool IsNetworkAttributed => Subjects.Count > 0 && Subjects.All(s => s.IsNetwork);
}

/// <summary>Who may change policy. Read here, never written through the API.</summary>
/// <param name="Rulesets">Null means every ruleset — a platform-team grant, not an empty one.</param>
public sealed record GrantView(
    string Identity,
    IReadOnlyList<string> Verbs,
    IReadOnlyList<string>? Rulesets,
    string? Note)
{
    public bool IsUnscoped => Rulesets is null;
}

/// <summary>
/// The platform-owned baseline every unmatched source lands on. <see cref="DenyAll"/> comes from
/// the API rather than being inferred from an empty list, so the floor is stated rather than
/// implied.
/// </summary>
public sealed record FallbackView(IReadOnlyList<string> AllowedHosts, bool DenyAll);

/// <summary>Everything one control-plane read cycle produced, with the recency it was read at.</summary>
public sealed record PolicySnapshot(
    IReadOnlyList<RulesetView> Rulesets,
    IReadOnlyList<GrantView> Grants,
    FallbackView Fallback,
    Recency Recency,
    Freshness Freshness)
{
    /// <summary>The ruleset governing a subject, or null when it falls to the fallback. The join
    /// the console exists to close — see <see cref="RulesetView"/> and design.md's Context table.</summary>
    public RulesetView? Governing(string? appid) => string.IsNullOrEmpty(appid)
        ? null
        : Rulesets.FirstOrDefault(r => r.Subjects.Any(s =>
            string.Equals(s.Appid, appid, StringComparison.OrdinalIgnoreCase)));

    public int CountWith(RulesetAction action) => Rulesets.Count(r => r.Action == action);
}

/// <summary>
/// What <c>POST /rulesets/{name}:check</c> reported. Both halves, because a push is a full
/// replace: a candidate change takes hosts and subjects away as well as adding them, and a
/// console that showed only additions would misrepresent what the snippet is about to do.
/// </summary>
public sealed record CheckResult(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Bound,
    IReadOnlyList<string> Unbound)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Bound.Count == 0 && Unbound.Count == 0;
}
