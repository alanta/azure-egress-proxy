namespace Portal.Components;

/// <summary>
/// The six surfaces, as real routes.
///
/// The mockup switches tabs in JavaScript. Here they are ordinary links under
/// <c>hx-boost</c>: htmx swaps the body without a full page load, and because each is a genuine
/// URL the console also gets deep links, the back button, and a shareable address for "the thing
/// I am looking at" — none of which the mockup's tab switching has (design.md D8).
/// </summary>
/// <param name="Key">Drives <c>body[data-surface]</c>, which selects the per-surface background
/// tint. It must match the CSS: overview, rulesets, traffic, lookup, platform, runtime.</param>
public sealed record Surface(string Key, string Label, string Path)
{
    public static readonly Surface Overview = new("overview", "Overview", "/");
    public static readonly Surface Rulesets = new("rulesets", "Rulesets", "/Rulesets");
    public static readonly Surface Traffic = new("traffic", "Traffic", "/Traffic");
    public static readonly Surface Lookup = new("lookup", "Lookup", "/Lookup");
    public static readonly Surface Platform = new("platform", "Platform", "/Platform");
    public static readonly Surface Runtime = new("runtime", "Runtime", "/Runtime");

    /// <summary>Tab-bar order, which is also the order the mockups use: posture first, then what
    /// the proxy is refusing, then the lookups, then the platform and the machine.</summary>
    public static readonly IReadOnlyList<Surface> All =
        [Overview, Rulesets, Traffic, Lookup, Platform, Runtime];
}
