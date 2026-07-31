using System.Globalization;
using Portal.Clients;

namespace Portal.Components;

/// <summary>
/// One number and its label. <see cref="Tone"/> is the semantic ramp the mockups added on top of
/// ZEP — allow/report/open held separate from the accent hue so policy state reads at a glance
/// rather than requiring the label to be read.
/// </summary>
public sealed record StatModel(string Value, string Label, StatTone Tone = StatTone.Neutral, string? Suffix = null)
{
    public string CssClass => Tone switch
    {
        StatTone.Good => "k good",
        StatTone.Warn => "k warn",
        StatTone.Bad => "k bad",
        _ => "k",
    };
}

public enum StatTone
{
    Neutral,
    Good,
    Warn,
    Bad,
}

/// <summary>A small uppercase mono chip. <see cref="Variant"/> maps to the semantic ramp.</summary>
public sealed record PillModel(string Text, string Variant = "plain")
{
    /// <summary>
    /// A ruleset action as a pill. <c>enforce</c> is green, <c>report</c> amber, <c>open</c> red —
    /// the same reading as the stat tones, so a surface never has to decide what a policy state
    /// looks like. Note that <c>open</c> reads as the alarming colour on purpose: it is the state
    /// in which the allowlist constrains nothing.
    /// </summary>
    public static PillModel For(RulesetAction action) => action switch
    {
        RulesetAction.Report => new PillModel("Report", "report"),
        RulesetAction.Open => new PillModel("Open", "open"),
        _ => new PillModel("Enforce", "enforce"),
    };

    /// <summary>The fallback, rendered so the deny-all floor is legible rather than inferred.</summary>
    public static PillModel For(FallbackView fallback) => fallback.DenyAll
        ? new PillModel("Deny-all", "enforce")
        : new PillModel($"{fallback.AllowedHosts.Count} host(s)", "report");
}

/// <summary>
/// A table. Deliberately modest: it covers the list-of-rows shape every surface needs, and a
/// surface wanting richer cells (a pill in a column, a clickable row with an hx-get) writes the
/// table directly using the documented classes rather than growing this model until it can express
/// arbitrary markup.
/// </summary>
public sealed record TableModel(
    IReadOnlyList<ColumnModel> Columns,
    IReadOnlyList<IReadOnlyList<CellModel>> Rows,
    bool Scroll = false,
    string? EmptyMessage = null);

/// <param name="Numeric">Right-aligns and uses tabular figures, so digits line up down the column.</param>
public sealed record ColumnModel(string Header, bool Numeric = false);

/// <param name="Mono">For identifiers — appids, hosts, CIDRs — where the value IS the identifier.</param>
/// <param name="Tone">Colours the cell on the semantic ramp; null leaves it in body ink.</param>
public sealed record CellModel(string Text, bool Mono = false, bool Numeric = false, StatTone? Tone = null)
{
    public string CssClass
    {
        get
        {
            var classes = new List<string>();
            if (Numeric)
            {
                classes.Add("num");
            }

            if (Mono)
            {
                classes.Add("mono");
            }

            if (Tone is { } tone && tone != StatTone.Neutral)
            {
                classes.Add(tone switch
                {
                    StatTone.Good => "good",
                    StatTone.Warn => "warn",
                    _ => "bad",
                });
            }

            return string.Join(' ', classes);
        }
    }
}

/// <summary>An explanatory note in the flow of a surface. The console explains itself in place —
/// several of its facts (netid attribution, report semantics, where authority comes from) are
/// exactly the things an operator would otherwise get wrong.</summary>
public sealed record BannerModel(string Icon, string Text);

/// <summary>Allowed hosts, or a candidate diff. <see cref="Added"/>/<see cref="Removed"/> render
/// in the allow/deny ramp, because a push is a full replace and a removal is as much of the change
/// as an addition.</summary>
public sealed record HostListModel(
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string>? Added = null,
    IReadOnlyList<string>? Removed = null);

/// <summary>
/// The freshness stamp. Every panel fed by Azure carries one: Azure Monitor is 1-minute, Log
/// Analytics ingestion is minutes, and the portal caches on top of both — so the console states
/// its recency instead of implying the numbers are live.
/// </summary>
public sealed record FreshnessModel(string Text)
{
    public static FreshnessModel From(Freshness freshness, TimeProvider? clock = null)
    {
        var age = (clock ?? TimeProvider.System).GetUtcNow() - freshness.AsOf;
        return new FreshnessModel(Describe(age));
    }

    /// <summary>Several sources on one stamp, for a surface that mixes them.</summary>
    public static FreshnessModel From(params (string Label, Freshness Freshness)[] parts) =>
        new(string.Join(" · ", parts.Select(p =>
            $"{p.Label} {Describe(TimeProvider.System.GetUtcNow() - p.Freshness.AsOf)}")));

    private static string Describe(TimeSpan age) => age switch
    {
        { TotalSeconds: < 90 } => "just now",
        { TotalMinutes: < 60 } => $"~{(int)age.TotalMinutes} min ago",
        { TotalHours: < 24 } => $"~{(int)age.TotalHours} h ago",
        _ => $"{(int)age.TotalDays} d ago",
    };
}

/// <summary>
/// A series as server-rendered SVG. No charting library: the mockups are hand-rolled SVG and ZEP's
/// own MetricChart is too, so nothing here implies a client-side dependency — which is what keeps
/// the CSP free of <c>unsafe-eval</c> and the repo free of npm.
/// </summary>
/// <param name="Colour">A hex colour from the semantic ramp — deny red for denials, accent blue
/// for throughput, allow green for availability.</param>
public sealed record ChartModel(
    IReadOnlyList<double> Values,
    string Colour,
    string Label,
    int Width = 320,
    int Height = 46)
{
    public static ChartModel FromMetric(MetricSeries series, string colour, int height = 46) =>
        new([.. series.Points.Select(p => p.Value ?? 0)], colour, series.Name, Height: height);

    /// <summary>
    /// The line, as an SVG path. Scaled to the series' own range with a floor of zero, so a flat
    /// series renders flat rather than as noise amplified to fill the box — a chart that turns
    /// steady traffic into a jagged line is actively misleading on an operations console.
    /// </summary>
    public string LinePath => BuildPath(close: false);

    /// <summary>The same line, closed along the bottom, for the translucent fill underneath.</summary>
    public string AreaPath => BuildPath(close: true);

    public bool HasData => Values.Count > 1;

    /// <summary>Where to sit the "latest value" dot.</summary>
    public (double X, double Y) LastPoint => Values.Count == 0 ? (0, Height) : (Width, Y(Values[^1]));

    private double Max => Values.Count == 0 ? 1 : Math.Max(Values.Max(), double.Epsilon);

    private double Y(double value) =>
        Height - (value / Max * (Height - 6)) - 3;

    private string BuildPath(bool close)
    {
        if (!HasData)
        {
            return string.Empty;
        }

        var step = (double)Width / (Values.Count - 1);
        var points = Values.Select((value, index) => string.Create(CultureInfo.InvariantCulture,
            $"{(index == 0 ? "M" : "L")}{index * step:0.##},{Y(value):0.##}"));

        var path = string.Concat(points);
        return close
            ? string.Create(CultureInfo.InvariantCulture, $"{path}L{Width},{Height}L0,{Height}Z")
            : path;
    }
}

/// <summary>The title block every surface opens with, and the freshness stamp beside it.</summary>
public sealed record PageHeadModel(string Title, string? Lede = null, FreshnessModel? Freshness = null);
