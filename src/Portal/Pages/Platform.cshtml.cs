using System.Globalization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Clients;
using Portal.Components;

namespace Portal.Pages;

/// <summary>
/// The Platform surface: who may change policy, and what traffic matching no ruleset reaches.
///
/// <para>Both halves come from the same control-plane read, and neither has a write counterpart —
/// <c>grants</c> is platform-owned and edited out of band, and the API copies it through untouched
/// on every write so the control plane cannot widen its own authority (docs/control-plane.md
/// § Authorization). The page therefore has to read as a *statement of record* rather than an admin
/// screen someone could edit, which is what the banner on the authority card is for.</para>
///
/// <para>Unlike the other surfaces this one does not poll. Policy changes when a pipeline pushes,
/// which is not on a 60-second rhythm, and every read carries its own last-modified stamp — the
/// same reasoning as the Overview's policy card.</para>
/// </summary>
public sealed class PlatformModel(ConsoleData data, ILogger<PlatformModel> logger) : PageModel
{
    public PolicySnapshot? Policy { get; private set; }

    /// <summary>Set when the control plane could not be read, so the cards say so rather than
    /// rendering an empty grants table — "nobody may change policy" is a very different claim from
    /// "we could not ask".</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// The document's own recency, beside how old this portal's read of it is. Both, because they
    /// answer different questions: the first is when the configuration last changed, the second is
    /// how stale what you are looking at may be. The read age comes from the fetch that produced
    /// the value — a cached snapshot keeps its original stamp rather than being restamped as now.
    /// </summary>
    public FreshnessModel? Stamp
    {
        get
        {
            if (Policy is not { } policy)
            {
                return null;
            }

            var read = FreshnessModel.From(policy.Freshness).Text;

            // Document-scoped, and said so: any ruleset write moves it, so it can never be read as
            // "when did this grant change?".
            return new FreshnessModel(policy.Recency.LastModified is { } modified
                ? $"Document changed {modified.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)} · read {read}"
                : $"No state written yet · read {read}");
        }
    }

    /// <summary>
    /// What a grant is limited to.
    ///
    /// <para>A null <c>rulesets</c> means <b>every</b> ruleset — a platform-team grant. Rendering
    /// that as a blank scope would understate authority, which is the one direction a security
    /// console must never err in, so it is spelled out. An <c>onboard</c>-only holder is not
    /// special-cased down to "any ruleset it creates": overstating the reach of a grant is the safe
    /// mistake, understating it is not.</para>
    /// </summary>
    public static string DescribeScope(GrantView grant) => grant switch
    {
        { IsUnscoped: true } => "Every ruleset",

        // Present but empty: the grant names no ruleset, so it reaches none. Distinct from the
        // above, and worth saying rather than rendering as an empty cell that looks like a bug.
        { Rulesets.Count: 0 } => "No ruleset",

        _ => string.Join(", ", grant.Rulesets!),
    };

    /// <summary>True when the scope is a list of ruleset names, which are identifiers and render
    /// in mono. The two prose answers above are not.</summary>
    public static bool ScopeIsNamed(GrantView grant) => !grant.IsUnscoped && grant.Rulesets!.Count > 0;

    /// <summary>A verb as a pill label. The wire spelling is lower case; the pills read as words.</summary>
    public static string DescribeVerb(string verb) => string.IsNullOrWhiteSpace(verb)
        ? "(empty)"
        : string.Concat(char.ToUpperInvariant(verb.Trim()[0]), verb.Trim()[1..]);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Platform";
        ViewData["Surface"] = Surface.Platform.Key;

        try
        {
            Policy = await data.PolicyAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "the platform surface could not read the control plane");
            Error = "the control plane could not be read";
        }
    }
}
