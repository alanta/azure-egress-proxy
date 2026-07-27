using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlPlane.Model;

// The collection properties below normalize null in their init accessor. Request JSON may legally
// say "allowed_hosts": null, and System.Text.Json writes that null straight over the initializer —
// so a declared-non-nullable list arrives null and every consumer that iterates it throws a 500
// instead of returning a validation error. Absent and explicitly-null both mean "empty" here.

/// <summary>
/// The control plane's entire internal state — a SINGLE blob (<c>egress-config/rulesets.json</c>).
/// This is the authored truth; it is NOT what the proxy reads. The proxy's document
/// (<c>allowlist.json</c>) is a pure projection of this one, produced by <see cref="AllowlistRenderer"/>,
/// and its schema is frozen. See allowlist/rulesets.schema.json and docs/control-plane.md.
/// </summary>
public sealed record StateDocument
{
    public List<Ruleset> Rulesets { get; init => field = value ?? []; } = [];

    /// <summary>
    /// Platform-owned RBAC. It shares the blob with the rulesets, so the "the API cannot widen
    /// its own authority" property is enforced in code rather than by storage permissions: every
    /// write path copies this list through untouched (see <see cref="RulesetStore"/>).
    /// </summary>
    public List<Grant> Grants { get; init => field = value ?? []; } = [];

    /// <summary>Platform baseline for sources matching no ruleset. Absent or empty => deny-all.</summary>
    public Fallback? Fallback { get; init; }

    public Ruleset? Find(string name) =>
        Rulesets.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
}

public sealed record Ruleset
{
    public required string Name { get; init; }

    /// <summary>Set at onboard. A plain update never touches these (anti-hijack); changing membership
    /// afterwards needs the <c>bind</c> verb, which the onboarding owner holds by trust-on-first-use.</summary>
    public List<Subject> Subjects { get; init => field = value ?? []; } = [];

    public RulesetContent Content { get; init => field = value ?? new(); } = new();

    /// <summary>Reserved for the management portal (Mode 3); not consulted by the Mode 2 write path.</summary>
    public Acl? Acl { get; init; }

    /// <summary>Trust-on-first-use: the identity that onboarded this ruleset. Holds update/offboard/bind on it.</summary>
    public string? Owner { get; init; }
}

/// <summary>Exactly one of <see cref="Appid"/> or <see cref="Netid"/> is set.</summary>
public sealed record Subject
{
    public string? Appid { get; init; }
    public string? Netid { get; init; }

    /// <summary>Stable key for uniqueness checks and messages. Never serialized — it is derived,
    /// and writing it would put a field in the state blob that the schema does not allow.</summary>
    [JsonIgnore]
    public string Key => Appid is not null ? $"appid:{Appid.ToLowerInvariant()}" : $"netid:{Netid}";

    public override string ToString() => Appid ?? Netid ?? "(empty)";
}

/// <summary>The writable part of a ruleset — what an update replaces, in full.</summary>
public sealed record RulesetContent
{
    public List<string> AllowedHosts { get; init => field = value ?? []; } = [];

    /// <summary>
    /// Uniform across the whole ruleset: a ruleset has exactly one action and its hosts are never
    /// evaluated under differing ones. Forced to <c>report</c> at onboard; never downgraded by the
    /// control plane afterwards.
    /// </summary>
    public string? Action { get; init; }
}

public sealed record Acl
{
    public List<string> Edit { get; init => field = value ?? []; } = [];
    public List<string> Push { get; init => field = value ?? []; } = [];
    public List<string> Admin { get; init => field = value ?? []; } = [];
}

public sealed record Grant
{
    public required string Identity { get; init; }
    public List<string> Verbs { get; init => field = value ?? []; } = [];

    /// <summary>
    /// Scopes update/offboard/bind to these rulesets. Null means every ruleset (platform-team grants).
    /// Never scopes onboard, which is registry-wide by nature.
    /// </summary>
    public List<string>? Rulesets { get; init; }

    public string? Note { get; init; }
}

public sealed record Fallback
{
    public List<string> AllowedHosts { get; init => field = value ?? []; } = [];
}

public static class StateJson
{
    /// <summary>
    /// snake_case to match the hand-authored schema (<c>allowed_hosts</c>), indented because both
    /// documents are read and diffed by humans in git and in the portal-less present.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"empty {typeof(T).Name}");
}
