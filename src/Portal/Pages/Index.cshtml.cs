using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Clients;
using Portal.Components;

namespace Portal.Pages;

/// <summary>
/// The Overview surface.
///
/// Built first, in wave 1, deliberately: it is the only surface that reads all three sources —
/// control plane, Log Analytics, and ARM/Azure Monitor — so once it works the remaining surfaces
/// are repetition rather than risk.
///
/// <para>Each panel is its own handler and its own swap target, refreshing on
/// <c>hx-trigger="every 60s"</c> against the server-side cache. That is not just tidiness: it means
/// one source being slow or unconfigured degrades one card rather than the page, which for a
/// console whose job is to be readable during an incident is the difference between useful and
/// not.</para>
/// </summary>
public sealed class IndexModel(ConsoleData data, ILogger<IndexModel> logger) : PageModel
{
    public PolicySnapshot? Policy { get; private set; }

    public TrafficSummary? Traffic { get; private set; }

    /// <summary>False when no audit workspace is configured. The panel says so rather than
    /// rendering zeroes — a count of 0 that nobody asked for reads as "all clear".</summary>
    public bool TrafficConfigured { get; private set; } = true;

    public IReadOnlyList<DecisionRow> Denials { get; private set; } = [];

    public ScaleSetStatus? ScaleSet { get; private set; }

    public EgressPool? Pool { get; private set; }

    public MetricSeries Throughput { get; private set; } = MetricSeries.Empty("Network Out Total", "bytes");

    public IReadOnlyList<Observation> Attention { get; private set; } = [];

    /// <summary>
    /// Denials bucketed into hours, oldest first, for the traffic sparkline. Empty hours are real
    /// zeroes rather than gaps: a chart that skipped quiet hours would compress the busy ones and
    /// make a spike look like the normal shape.
    /// </summary>
    public IReadOnlyList<double> DenialsPerHour
    {
        get
        {
            if (Denials.Count == 0)
            {
                return [];
            }

            var counts = Denials
                .GroupBy(d => new DateTimeOffset(
                    d.TimeGenerated.Year, d.TimeGenerated.Month, d.TimeGenerated.Day,
                    d.TimeGenerated.Hour, 0, 0, d.TimeGenerated.Offset))
                .ToDictionary(g => g.Key, g => (double)g.Count());

            var start = counts.Keys.Min();
            var end = counts.Keys.Max();

            return [.. Enumerable
                .Range(0, (int)(end - start).TotalHours + 1)
                .Select(hour => counts.GetValueOrDefault(start.AddHours(hour)))];
        }
    }

    /// <summary>Set when a source could not be read, so the panel can say so instead of rendering
    /// zeroes — a console that shows "0 denials" because the query failed is worse than one that
    /// shows nothing.</summary>
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Overview";
        ViewData["Surface"] = Surface.Overview.Key;

        await LoadPolicyAsync(cancellationToken);
        await LoadTrafficAsync(cancellationToken);
        await LoadRuntimeAsync(cancellationToken);
        await LoadAttentionAsync(cancellationToken);
    }

    // ---- panel handlers, one per swap target ---------------------------------------------------

    public async Task<IActionResult> OnGetPolicyPanelAsync(CancellationToken cancellationToken)
    {
        await LoadPolicyAsync(cancellationToken);
        return Partial("_OverviewPolicy", this);
    }

    public async Task<IActionResult> OnGetTrafficPanelAsync(CancellationToken cancellationToken)
    {
        await LoadTrafficAsync(cancellationToken);
        return Partial("_OverviewTraffic", this);
    }

    public async Task<IActionResult> OnGetRuntimePanelAsync(CancellationToken cancellationToken)
    {
        await LoadRuntimeAsync(cancellationToken);
        return Partial("_OverviewRuntime", this);
    }

    public async Task<IActionResult> OnGetAttentionPanelAsync(CancellationToken cancellationToken)
    {
        await LoadPolicyAsync(cancellationToken);
        await LoadAttentionAsync(cancellationToken);
        return Partial("_OverviewAttention", this);
    }

    // ---- loading -------------------------------------------------------------------------------

    private async Task LoadPolicyAsync(CancellationToken cancellationToken) =>
        Policy = await TryAsync(() => data.PolicyAsync(cancellationToken), "policy");

    private async Task LoadTrafficAsync(CancellationToken cancellationToken)
    {
        TrafficConfigured = data.TrafficIsConfigured;
        if (!TrafficConfigured)
        {
            return;
        }

        Traffic = await TryAsync(
            () => data.TrafficSummaryAsync(TrafficWindows.Default, cancellationToken), "traffic summary");
        Denials = await TryAsync(
            () => data.DenialsAsync(TrafficWindows.Default, cancellationToken), "denials") ?? [];
    }

    private async Task LoadRuntimeAsync(CancellationToken cancellationToken)
    {
        ScaleSet = await TryAsync(() => data.ScaleSetAsync(cancellationToken), "scale set");
        Pool = await TryAsync(() => data.EgressPoolAsync(cancellationToken), "egress pool");
        Throughput = await TryAsync(
            () => data.MetricAsync(RuntimeMetric.NetworkOut, TimeSpan.FromHours(1), cancellationToken),
            "throughput") ?? MetricSeries.Empty("Network Out Total", "bytes");
    }

    private async Task LoadAttentionAsync(CancellationToken cancellationToken)
    {
        if (Policy is null)
        {
            return;
        }

        var findings = await TryAsync(
            () => data.ReportFindingsAsync(TrafficWindows.Default, cancellationToken), "report findings") ?? [];
        var challenges = await TryAsync(
            () => data.ChallengeConversionAsync(TrafficWindows.Default, cancellationToken),
            "challenge conversion") ?? [];

        if (Denials.Count == 0)
        {
            Denials = await TryAsync(
                () => data.DenialsAsync(TrafficWindows.Default, cancellationToken), "denials") ?? [];
        }

        Attention = Observations.From(Policy, Denials, findings, challenges);
    }

    /// <summary>
    /// One failing source degrades its own panel and no other. The alternative — letting the
    /// exception reach the page — turns a Log Analytics hiccup into a console that cannot show the
    /// policy it could have read perfectly well.
    /// </summary>
    private async Task<T?> TryAsync<T>(Func<Task<T>> load, string what)
    {
        try
        {
            return await load();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "the overview could not read {What}", what);
            Error = $"{what} could not be read";
            return default;
        }
    }
}
