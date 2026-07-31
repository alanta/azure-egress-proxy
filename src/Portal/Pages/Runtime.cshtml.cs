using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Clients;
using Portal.Components;

namespace Portal.Pages;

/// <summary>
/// The Runtime surface: the proxy fleet, the addresses it leaves the network from, and what Azure
/// Monitor says about the machine underneath.
///
/// <para><b>Nothing here is live.</b> Azure Monitor's grain is one minute and there is nothing
/// finer without adding a metrics listener to a security appliance, which would be its own
/// proposal (design.md D4). Every panel therefore states its recency rather than implying
/// immediacy, and a cached value keeps the timestamp of the fetch that produced it.</para>
///
/// <para>Panels are separate handlers and separate swap targets, each loaded through
/// <see cref="TryAsync"/>: ARM being slow degrades the fleet card while the metric cards keep
/// rendering, which for a console whose job is to be readable during an incident is the whole
/// point.</para>
/// </summary>
public sealed class RuntimeModel(ConsoleData data, ILogger<RuntimeModel> logger) : PageModel
{
    /// <summary>The window every chart on this surface covers. One hour of 1-minute samples is 60
    /// points, which is the shape the mockup's charts were drawn against.</summary>
    private static readonly TimeSpan ChartWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Beyond this the address grid stops being a grid. A /26 is already 64 chips; the deployment's
    /// prefix is far smaller, and a pool too large to draw still gets its numbers and its
    /// consequence line — it just does not get drawn address by address.
    /// </summary>
    public const int MaxDrawableSlots = 64;

    public ScaleSetStatus? ScaleSet { get; private set; }

    public EgressPool? Pool { get; private set; }

    /// <summary>Load-balancer data-path availability and health-probe status (task 11.4), as the
    /// two rows the fleet card closes with.</summary>
    public IReadOnlyList<LoadBalancerSignal> LoadBalancer { get; private set; } = [];

    public IReadOnlyList<RuntimeChart> Charts { get; private set; } = [];

    /// <summary>Set when a source could not be read. Panels key off their own null instead, so this
    /// exists for the log and for the one line the page head can honestly say.</summary>
    public string? Error { get; private set; }

    // ---- what the fleet card says --------------------------------------------------------------

    public string FleetTitle => ScaleSet is null
        ? "Not readable"
        : $"{ScaleSet.Running} of {ScaleSet.Capacity} instances online";

    /// <summary>Green while the node is powered on, deny-red otherwise. The dot is the first thing
    /// scanned down the list, so it must not read as healthy for a stopped node.</summary>
    public static string PowerColour(ProxyInstance instance) =>
        IsRunning(instance) ? "var(--allow)" : "var(--deny)";

    public static string PowerLabel(ProxyInstance instance) =>
        string.IsNullOrWhiteSpace(instance.PowerState) ? "power state unknown" : instance.PowerState;

    public static string NodeName(ScaleSetStatus scaleSet, ProxyInstance instance) =>
        instance.ComputerName ?? $"{scaleSet.Name}_{instance.InstanceId}";

    /// <summary>
    /// The application health extension's verdict — and <b>"no health probe" is a third state</b>,
    /// not a healthy one. A deployment without the extension configured reports nothing, and a
    /// panel that painted that green would be inventing an assurance Azure never gave.
    /// </summary>
    public static PillModel Health(ProxyInstance instance)
    {
        // The code arrives as "HealthState/healthy"; the half after the slash is the verdict.
        var state = instance.HealthState?.Split('/').Last();

        return state?.ToLowerInvariant() switch
        {
            "healthy" => new PillModel("Healthy", "enforce"),
            "unhealthy" => new PillModel("Unhealthy", "open"),
            null or "" => new PillModel("No health probe", "plain"),
            _ => new PillModel(state, "report"),
        };
    }

    /// <summary>The image, as the last segment of whatever the scale set carries — a gallery
    /// resource id is unreadable at this size and its trailing segment is the version.</summary>
    public static string ImageLabel(ProxyInstance instance) =>
        string.IsNullOrWhiteSpace(instance.ImageReference)
            ? "image unknown"
            : instance.ImageReference.Split('/').Last();

    public FreshnessModel? FleetFreshness => ScaleSet is null
        ? null
        : LoadBalancer.FirstOrDefault(signal => signal.HasSamples) is { } lb
            ? FreshnessModel.From(("Fleet", ScaleSet.Freshness), ("Load balancer", lb.Freshness))
            : FreshnessModel.From(ScaleSet.Freshness);

    // ---- what the egress-pool card says --------------------------------------------------------

    public string PoolTitle => Pool is null
        ? "Not readable"
        : $"{Pool.InUse} of {Pool.Capacity} in use";

    public FreshnessModel? PoolFreshness => Pool is null ? null : FreshnessModel.From(Pool.Freshness);

    /// <summary>The prefix drawn address by address, with the ones actually assigned marked.</summary>
    public IReadOnlyList<PoolSlot> PoolSlots => Pool is null
        ? []
        : Slots(Pool, NodeAddresses());

    /// <summary>
    /// The consequence, in one sentence, because that is the whole reason this panel exists.
    ///
    /// <para>The proxy egresses from instance-level public IPs drawn from this prefix, so the
    /// prefix — not any individual node — is the stable set of addresses partners allowlist on
    /// their side. Spare capacity is therefore "how many more nodes can exist before one of them
    /// leaves from an address nobody has allowlisted", which is a fleet limit an operator can act
    /// on, and not a percentage.</para>
    /// </summary>
    public string PoolConsequence => Pool switch
    {
        null => string.Empty,
        { Available: 0 } =>
            "Every address in the prefix is assigned. A further node has none left to take, so it "
            + "would egress from an address outside this block — one no partner has allowlisted, "
            + "and its traffic is refused at the partner's edge rather than here.",
        { Available: 1 } =>
            "One address is spare. The fleet can grow by a single node before one would have to "
            + "egress from an address outside this block, which no partner has allowlisted.",
        var pool =>
            $"{pool.Available} addresses are spare, so the fleet can grow by {pool.Available} nodes "
            + "before one would have to egress from an address outside this block — which no "
            + "partner has allowlisted.",
    };

    /// <summary>Exhaustion and near-exhaustion are the two states worth interrupting the reader
    /// for; anything else is a note, not a banner.</summary>
    public bool PoolIsPressed => Pool is not null && Pool.Available <= 1;

    public StatTone PoolTone => Pool switch
    {
        null => StatTone.Neutral,
        { Available: 0 } => StatTone.Bad,
        { Available: <= 1 } => StatTone.Warn,
        { UtilisationPercent: > 80 } => StatTone.Warn,
        _ => StatTone.Good,
    };

    // ---- page lifecycle ------------------------------------------------------------------------

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Runtime";
        ViewData["Surface"] = Surface.Runtime.Key;

        await LoadFleetAsync(cancellationToken);
        await LoadPoolAsync(cancellationToken);
        await LoadChartsAsync(cancellationToken);
    }

    // ---- panel handlers, one per swap target ---------------------------------------------------

    public async Task<IActionResult> OnGetFleetPanelAsync(CancellationToken cancellationToken)
    {
        await LoadFleetAsync(cancellationToken);
        return Partial("_RuntimeFleet", this);
    }

    public async Task<IActionResult> OnGetPoolPanelAsync(CancellationToken cancellationToken)
    {
        // The pool panel attributes addresses to nodes, so it needs the scale set too. Both reads
        // hit the same 1-minute cache the fleet panel just filled, not Azure.
        await LoadFleetAsync(cancellationToken);
        await LoadPoolAsync(cancellationToken);
        return Partial("_RuntimePool", this);
    }

    public async Task<IActionResult> OnGetChartsPanelAsync(CancellationToken cancellationToken)
    {
        await LoadChartsAsync(cancellationToken);
        return Partial("_RuntimeCharts", this);
    }

    // ---- loading -------------------------------------------------------------------------------

    private async Task LoadFleetAsync(CancellationToken cancellationToken)
    {
        ScaleSet = await TryAsync(() => data.ScaleSetAsync(cancellationToken), "scale set");

        var dataPath = await MetricAsync(
            RuntimeMetric.LoadBalancerDataPathAvailability, "VipAvailability", "%",
            "data-path availability", cancellationToken);
        var probes = await MetricAsync(
            RuntimeMetric.LoadBalancerHealthProbeStatus, "DipAvailability", "%",
            "health-probe status", cancellationToken);

        LoadBalancer =
        [
            Signal("Data path", dataPath,
                available: "Available", degraded: "Degraded", down: "Unavailable"),
            Signal("Health probes", probes,
                available: "All probes passing", degraded: "Some probes failing", down: "All probes failing"),
        ];
    }

    private async Task LoadPoolAsync(CancellationToken cancellationToken) =>
        Pool = await TryAsync(() => data.EgressPoolAsync(cancellationToken), "egress pool");

    private async Task LoadChartsAsync(CancellationToken cancellationToken)
    {
        var networkOut = await MetricAsync(
            RuntimeMetric.NetworkOut, "Network Out Total", "bytes", "throughput", cancellationToken);
        var cpu = await MetricAsync(
            RuntimeMetric.CpuPercent, "Percentage CPU", "%", "CPU", cancellationToken);
        var availability = await MetricAsync(
            RuntimeMetric.VmAvailability, "VmAvailabilityMetric", "", "VM availability", cancellationToken);

        Charts =
        [
            // Bytes per minute rendered as megabytes, which is the only unit conversion on this
            // surface and is stated in the suffix rather than assumed.
            Chart("Network out", networkOut, "#5b8def", scale: 1d / 1_000_000, digits: "N1", suffix: "MB/min",
                detail: Summarise(networkOut, 1d / 1_000_000, "N1")),
            Chart("CPU", cpu, "#6a6bd6", scale: 1, digits: "N0", suffix: "%",
                detail: Summarise(cpu, 1, "N0")),
            // VmAvailabilityMetric reports 1 when the VM is available and 0 when it is not, so the
            // mean over the window IS the share of samples that reported available. That is what
            // the percentage says, and the detail line says which samples it counted — the console
            // does not have 30 days of history and must not imply that it does.
            Chart("Availability", availability, "#2f9e6b", scale: 100, digits: "N0", suffix: "%",
                detail: availability.Points.Count == 0
                    ? "no samples in this window"
                    : $"{Average(availability) * 100:N1}% of the last hour's 1-minute samples reported available",
                value: Average(availability) * 100,
                tone: Average(availability) >= 1 ? StatTone.Good : StatTone.Warn),
        ];
    }

    /// <summary>
    /// A series, degraded to an empty one when the read fails.
    ///
    /// <para>An empty series carries <see cref="Freshness.Now"/>, which would be a lie if a panel
    /// stamped it — so no panel does: every freshness stamp on this surface is gated on the series
    /// actually having samples, and a series without them says it has none instead.</para>
    /// </summary>
    private async Task<MetricSeries> MetricAsync(
        RuntimeMetric metric,
        string name,
        string unit,
        string what,
        CancellationToken cancellationToken) =>
        await TryAsync(() => data.MetricAsync(metric, ChartWindow, cancellationToken), what)
            ?? MetricSeries.Empty(name, unit);

    /// <summary>One load-balancer row. No samples is its own state: the metric being unreadable is
    /// not the load balancer reporting itself healthy.</summary>
    private static LoadBalancerSignal Signal(
        string label,
        MetricSeries series,
        string available,
        string degraded,
        string down)
    {
        var latest = series.Latest;
        var pill = latest switch
        {
            null => new PillModel("No data", "plain"),
            >= 100 => new PillModel(available, "enforce"),
            > 0 => new PillModel(degraded, "report"),
            _ => new PillModel(down, "open"),
        };

        var detail = latest is null
            ? "not configured, or not readable by the portal's identity"
            : $"{latest.Value:N0}% on the last 1-minute sample";

        return new LoadBalancerSignal(label, pill, detail, latest is not null, series.Freshness);
    }

    private static RuntimeChart Chart(
        string title,
        MetricSeries series,
        string colour,
        double scale,
        string digits,
        string suffix,
        string detail,
        double? value = null,
        StatTone tone = StatTone.Neutral)
    {
        var headline = (value ?? series.Latest * scale) is { } number && series.Points.Count > 0
            ? number.ToString(digits)
            : "—";

        return new RuntimeChart(
            title,
            headline,
            suffix,
            detail,
            tone,
            // The label doubles as the chart's accessible name and as the grain statement: these
            // are 1-minute samples and the console says so instead of letting the shape imply a
            // live feed (design.md D4).
            new ChartModel(
                [.. series.Points.Select(point => (point.Value ?? 0) * scale)],
                colour,
                "1-minute samples, last hour",
                Height: 90),
            series.Freshness,
            series.Points.Count > 0);
    }

    private static string Summarise(MetricSeries series, double scale, string digits) =>
        series.Points.Count == 0
            ? "no samples in this window"
            : $"avg {(Average(series) * scale).ToString(digits)} · peak {((series.Peak ?? 0) * scale).ToString(digits)}";

    private static double Average(MetricSeries series)
    {
        var values = series.Points.Where(point => point.Value is not null).ToList();
        return values.Count == 0 ? 0 : values.Average(point => point.Value!.Value);
    }

    /// <summary>Address → node label, for attributing an assigned address to the instance holding
    /// it. The addresses live on the scale set's NICs, which is why they arrive with the fleet
    /// rather than with the prefix.</summary>
    private Dictionary<string, string> NodeAddresses() =>
        ScaleSet is null
            ? []
            : ScaleSet.Instances
                .Where(instance => instance.PublicIp is not null)
                .GroupBy(instance => instance.PublicIp!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => NodeName(ScaleSet, group.First()), StringComparer.Ordinal);

    /// <summary>
    /// The prefix as individual addresses, with the assigned ones marked.
    ///
    /// <para>Two sources disagree in a specific and useful way. The prefix knows <b>how many</b>
    /// addresses are assigned; the scale set knows <b>which node holds which</b>. So the slots a
    /// node claims are marked and named, and any remaining assignments the console cannot attribute
    /// are still marked — the prefix's own count is authoritative, and a panel that only marked
    /// what it could name would under-report how full the pool is, which is the one error this
    /// panel must not make.</para>
    /// </summary>
    public static IReadOnlyList<PoolSlot> Slots(EgressPool pool, IReadOnlyDictionary<string, string> nodeAddresses)
    {
        if (pool.Capacity is <= 0 or > MaxDrawableSlots)
        {
            return [];
        }

        var first = FirstAddress(pool.Prefix);
        var slots = new List<PoolSlot>(pool.Capacity);

        for (var index = 0; index < pool.Capacity; index++)
        {
            var address = first is { } start ? Format(start + (uint)index) : null;
            var node = address is null ? null : nodeAddresses.GetValueOrDefault(address);

            slots.Add(new PoolSlot(
                // The last octet is enough to tell the chips apart; the full address is on the
                // chip's title, so the grid stays a grid.
                address is null ? $"#{index + 1}" : $".{address.Split('.').Last()}",
                address,
                node is not null,
                node));
        }

        for (var index = 0; index < slots.Count && slots.Count(slot => slot.InUse) < pool.InUse; index++)
        {
            if (!slots[index].InUse)
            {
                slots[index] = slots[index] with { InUse = true };
            }
        }

        return slots;
    }

    private static uint? FirstAddress(string? prefix)
    {
        if (prefix is null
            || !IPAddress.TryParse(prefix.Split('/')[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }

    private static string Format(uint address)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
        return new IPAddress(bytes).ToString();
    }

    private static bool IsRunning(ProxyInstance instance) =>
        string.Equals(instance.PowerState, "running", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One failing source degrades its own panel and no other — a Monitor throttle must not cost
    /// the operator the node list ARM answered perfectly well.
    /// </summary>
    private async Task<T?> TryAsync<T>(Func<Task<T>> load, string what)
    {
        try
        {
            return await load();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "the runtime surface could not read {What}", what);
            Error = $"{what} could not be read";
            return default;
        }
    }
}

/// <summary>A load-balancer reading, as the fleet card's footer renders it.</summary>
/// <param name="HasSamples">False when the metric returned nothing — which is "unreadable or not
/// configured", never "healthy".</param>
public sealed record LoadBalancerSignal(
    string Label,
    PillModel Pill,
    string Detail,
    bool HasSamples,
    Freshness Freshness);

/// <summary>A metric card: the headline number, what it means, and the series behind it.</summary>
public sealed record RuntimeChart(
    string Title,
    string Value,
    string Suffix,
    string Detail,
    StatTone Tone,
    ChartModel Chart,
    Freshness Freshness,
    bool HasSamples);

/// <summary>One address of the egress prefix.</summary>
/// <param name="Node">The instance holding it, when the console can attribute it. Null on an
/// assigned address it cannot — see <see cref="RuntimeModel.Slots"/>.</param>
public sealed record PoolSlot(string Label, string? Address, bool InUse, string? Node);
