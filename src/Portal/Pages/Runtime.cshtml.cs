using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Portal.Clients;
using Portal.Components;

namespace Portal.Pages;

/// <summary>
/// The Runtime surface: <b>one path, drawn as one schematic</b> — traffic arrives at the load
/// balancer, crosses the proxy nodes, and leaves through the addresses in the egress prefix.
///
/// <para>The five panels this replaced each said a true thing about one resource and none of them
/// said the thing that matters most: a number in one stage constrains the next. A fleet of two
/// nodes on a /31 is at its ceiling, and reading that used to mean holding the fleet panel and the
/// address panel in your head at once. Every number the panels carried is still here, attached to
/// the stage it describes.</para>
///
/// <para><b>Nothing here is live.</b> Azure Monitor's grain is one minute and there is nothing
/// finer without adding a metrics listener to a security appliance, which would be its own
/// proposal (design.md D4). Every stage therefore states its recency rather than implying
/// immediacy, and a cached value keeps the timestamp of the fetch that produced it.</para>
///
/// <para>Stages are separate handlers and separate swap targets, each loaded through
/// <see cref="TryAsync"/>: ARM being slow degrades the fleet stage while the metric-fed stages keep
/// rendering, which for a console whose job is to be readable during an incident is the whole
/// point. The card is one card and still four targets — each wrapper is
/// <c>display: contents</c>, so it owns a station in the process line and a lane in the deck even
/// though the grid places the two in different rows.</para>
/// </summary>
public sealed class RuntimeModel(ConsoleData data, RuntimeOptions deployment, ILogger<RuntimeModel> logger)
    : PageModel
{
    /// <summary>The window every chart on this surface covers. One hour of 1-minute samples is 60
    /// points, which is the shape the mockup's charts were drawn against.</summary>
    private static readonly TimeSpan ChartWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// The port spokes reach the proxy on, as the inlet duct's chip. A fixed part of the
    /// architecture rather than a reading — <c>infra/modules/hub.bicep</c> sets it, the NSG floor
    /// allows exactly it, and the schematic labels the hop with it so the picture matches the
    /// deployment diagram.
    /// </summary>
    private const int ProxyPort = 4750;

    /// <summary>
    /// Beyond this the address grid stops being a grid. A /26 is already 64 chips; the deployment's
    /// prefix is far smaller, and a pool too large to draw still gets its numbers and its
    /// consequence line — it just does not get drawn address by address.
    /// </summary>
    public const int MaxDrawableSlots = 64;

    public ScaleSetStatus? ScaleSet { get; internal set; }

    public EgressPool? Pool { get; internal set; }

    /// <summary>Load-balancer data-path availability and health-probe status, as the two readouts
    /// in the load-balancer lane.</summary>
    public IReadOnlyList<LoadBalancerSignal> LoadBalancer { get; internal set; } = [];

    /// <summary>The trend recorder along the foot of the card. Measured on the scale set; labelled
    /// for the path, because "throughput leaving the prefix" is what an operator reads and they are
    /// the same bytes (design.md, Open).</summary>
    public RuntimeChart Throughput { get; private set; } = EmptyChart("Network out", "#5b8def", 1000, 58);

    public RuntimeChart Cpu { get; private set; } = EmptyChart("CPU", "#6a6bd6", 200, 34);

    public RuntimeChart Availability { get; private set; } = EmptyChart("Availability", "#2f9e6b", 200, 34);

    /// <summary>Set when a source could not be read. Stages key off their own null instead, so this
    /// exists for the log and for the one line the page head can honestly say.</summary>
    public string? Error => _errors.Message;

    private readonly LoadErrors _errors = new();

    // ---- the stations ----------------------------------------------------------------------

    /// <summary>
    /// The load balancer, as the first stage of the path. Its two readings used to be two rows in
    /// the footer of the fleet card, which is not where the first stage of the path belongs.
    /// </summary>
    public StageView LoadBalancerStage => new(
        "lb",
        "Load balancer",
        deployment.LoadBalancerName ?? "not configured",
        "/img/azure/load-balancer.svg",
        DataPath?.Latest is { } latest ? latest.ToString("N0") : "—",
        "% data path",
        LoadBalancerLamp is LampState.Unread ? "Not readable" : Probes?.Pill.Text,
        LoadBalancerLamp);

    public StageView FleetStage => new(
        "vmss",
        "Scale set",
        ScaleSet?.Name ?? deployment.ScaleSetName ?? "not configured",
        "/img/azure/vm-scale-set.svg",
        ScaleSet is null ? "—" : ScaleSet.Running.ToString(),
        ScaleSet is null ? string.Empty : $"of {ScaleSet.Capacity} instances online",
        ScaleSet is null ? "Not readable" : FleetSummary(ScaleSet),
        FleetLamp);

    public StageView PrefixStage => new(
        "pip",
        "IP prefix",
        Pool?.Name ?? deployment.PublicIpPrefixName ?? "not configured",
        "/img/azure/public-ip-prefix.svg",
        Pool is null ? "—" : Pool.InUse.ToString(),
        Pool is null ? string.Empty : $"of {Pool.Capacity} addresses in use",
        Pool is null ? "Not readable" : PrefixSummary(Pool),
        PrefixLamp);

    // ---- the lamps -------------------------------------------------------------------------

    /// <summary>
    /// The stage-level counterpart to <see cref="Health"/>'s refusal to paint a node green without
    /// a verdict (design.md D4). A stage the console could not read is <b>unlit and hatched</b>,
    /// never coloured — and never zero.
    /// </summary>
    public LampState LoadBalancerLamp
    {
        get
        {
            var readable = LoadBalancer.Where(signal => signal.HasSamples).ToList();
            return readable.Count == 0
                ? LampState.Unread
                : readable.Select(SignalLamp).Max();
        }
    }

    public LampState FleetLamp
    {
        get
        {
            if (ScaleSet is null)
            {
                return LampState.Unread;
            }

            if (FleetIsDegraded)
            {
                return LampState.Bad;
            }

            // No health verdict is not a healthy fleet, for the same reason it is not a healthy
            // node: the extension reported nothing and the console will not fill that in.
            return ScaleSet.Instances.Count == 0
                || ScaleSet.Instances.Any(node => Health(node).Variant is not "enforce")
                || ScaleSet.Instances.Any(NotSettled)
                ? LampState.Warn
                : LampState.Ok;
        }
    }

    public LampState PrefixLamp => PoolTone switch
    {
        StatTone.Good => LampState.Ok,
        StatTone.Warn => LampState.Warn,
        StatTone.Bad => LampState.Bad,
        _ => LampState.Unread,
    };

    /// <summary>Nodes missing, stopped or reporting themselves unhealthy — the states a spare
    /// address could actually replace, which is what the consequence line composes against.</summary>
    public bool FleetIsDegraded =>
        ScaleSet is not null
        && (ScaleSet.Running < ScaleSet.Capacity
            || ScaleSet.Instances.Any(node => !IsRunning(node))
            || ScaleSet.Instances.Any(node => Health(node).Variant is "open"));

    /// <summary>How many of the three instrumented stages the console could not read.</summary>
    public int UnreadStages =>
        new[] { LoadBalancerLamp, FleetLamp, PrefixLamp }.Count(lamp => lamp is LampState.Unread);

    // ---- the ducts -------------------------------------------------------------------------

    /// <summary>
    /// The inlet, from the spoke workloads. Owned by the load-balancer stage, because a duct is
    /// about entering the stage to its right (design.md D2).
    /// </summary>
    public DuctView InletDuct => new(2, $":{ProxyPort}", LoadBalancerLamp switch
    {
        LampState.Unread => DuctTone.Unknown,
        // Only a dead data path stops traffic. Failing probes take backends out of rotation; the
        // survivors still carry the connections, so this duct keeps moving.
        LampState.Bad => DuctTone.Stopped,
        _ => DuctTone.Flowing,
    });

    /// <summary>Load balancer into the scale set. The chip is a fact about the scale set — how much
    /// of the backend pool is actually there — which is why the scale set owns it.</summary>
    public DuctView PoolDuct => ScaleSet switch
    {
        null => new DuctView(4, "—", DuctTone.Unknown),
        { Running: 0, Capacity: > 0 } set => new DuctView(4, $"0 OF {set.Capacity}", DuctTone.Stopped),
        var set when set.Running < set.Capacity =>
            new DuctView(4, $"{set.Running} OF {set.Capacity}", DuctTone.Constrained),
        _ => new DuctView(4, "POOL", DuctTone.Flowing),
    };

    /// <summary>
    /// Scale set into the prefix.
    ///
    /// <para><b>A full prefix is amber, never red.</b> It caps growth; it does not stop today's
    /// requests. Hatching this duct as stopped would say traffic has ceased, which is false and is
    /// exactly the kind of over-claim a diagram makes easily (design.md D5).</para>
    /// </summary>
    public DuctView EgressDuct => Pool switch
    {
        null => new DuctView(6, "—", DuctTone.Unknown),
        { Available: 0 } => new DuctView(6, "FULL", DuctTone.Constrained),
        _ => new DuctView(6, "SNAT", DuctTone.Flowing),
    };

    /// <summary>Out to the partner endpoints. The prefix owns it because there is no stage beyond
    /// it, and the chip is the block partners allowlist.</summary>
    public DuctView OutletDuct => Pool is null
        ? new DuctView(8, "—", DuctTone.Unknown)
        : new DuctView(8, $"/{Pool.PrefixLength}", DuctTone.Flowing);

    // ---- what the whole path says ------------------------------------------------------------

    /// <summary>
    /// The consequence, in one sentence, spanning the deck — because it is a fact about the path
    /// rather than about any single stage.
    ///
    /// <para>The proxy egresses from instance-level public IPs drawn from the prefix, so the prefix
    /// — not any individual node — is the stable set of addresses partners allowlist on their side.
    /// Spare capacity is therefore "how many more nodes can exist before one of them leaves from an
    /// address nobody has allowlisted", which is a fleet limit an operator can act on, and not a
    /// percentage.</para>
    ///
    /// <para><b>The quiet-day tone is deliberate and must not be dropped.</b> A bar that disappears
    /// when nothing is wrong trains an operator not to look at it — and this bar is the accessible
    /// carrier of the schematic's thesis, because the ducts are CSS decoration and are invisible to
    /// assistive technology (design.md D3).</para>
    /// </summary>
    public ConsequenceView Consequence
    {
        get
        {
            if (UnreadStages > 0)
            {
                return new ConsequenceView(
                    "○",
                    UnreadStages == 1
                        ? "One of the three stages is unread."
                        : $"{(UnreadStages == 2 ? "Two" : "All three")} of the stages are unread.",
                    "Nothing here says the fleet is unhealthy or the prefix is empty — it says the "
                    + "source did not answer, and the console will not guess on its behalf. The "
                    + "fleet is neither constrained nor unconstrained until it can be read.",
                    "dim");
            }

            // Pool is non-null here: an unread prefix is an unread stage and was handled above.
            var pool = Pool!;

            if (pool.Available == 0)
            {
                return new ConsequenceView(
                    "⛔",
                    "The prefix is the fleet's ceiling.",
                    "Every address is assigned, so a further node has none left to take — it would "
                    + "egress from an address outside this block, one no partner has allowlisted, "
                    + "and its traffic would be refused at the partner's edge rather than here.",
                    "bad");
            }

            if (FleetIsDegraded)
            {
                return new ConsequenceView(
                    "✅",
                    "The fleet can recover on its own.",
                    $"{Nodes(pool.Available)} spare in the prefix, so a replacement node lands "
                    + "inside the block your partners have already allowlisted.",
                    "ok");
            }

            return new ConsequenceView(
                "ℹ️",
                "The prefix has room for the fleet to grow.",
                $"{Nodes(pool.Available)}, so the fleet can grow by that many nodes before one "
                + "would have to egress from an address outside this block — which no partner has "
                + "allowlisted.",
                string.Empty);
        }
    }

    /// <summary>
    /// The card's title: the state of the path in one line, as the two clauses an operator would
    /// actually put together — what the fleet is doing, and whether the prefix leaves it room.
    /// The bar below says the consequence; this says the reading.
    /// </summary>
    public string PathHeadline
    {
        get
        {
            if (UnreadStages == 0)
            {
                return $"{FleetClause()} — {PrefixClause()}";
            }

            var unread = new List<string>();
            if (LoadBalancerLamp is LampState.Unread)
            {
                unread.Add("the load balancer");
            }

            if (FleetLamp is LampState.Unread)
            {
                unread.Add("the fleet");
            }

            if (PrefixLamp is LampState.Unread)
            {
                unread.Add("the prefix");
            }

            var names = unread.Count == 1
                ? unread[0]
                : $"{string.Join(", ", unread.SkipLast(1))} and {unread[^1]}";

            return $"{char.ToUpperInvariant(names[0])}{names[1..]} could not be read — unread, "
                + "which is not the same as empty";
        }
    }

    private string FleetClause() => ScaleSet switch
    {
        null => "The fleet is unread",
        { Instances.Count: 0 } => "The scale set reports no instances",
        var set when FleetIsDegraded => $"{set.Running} of {set.Capacity} nodes online",
        var set when FleetLamp is LampState.Warn => $"{set.Running} nodes online, health unreported",
        var set => $"All {set.Running} nodes healthy",
    };

    private string PrefixClause() => Pool switch
    {
        null => "the prefix is unread",
        { Available: 0 } => "every address in the prefix already assigned",
        { Available: 1 } => "one address spare in the prefix",
        var pool => $"{pool.Available} addresses spare in the prefix",
    };

    // ---- freshness ---------------------------------------------------------------------------

    /// <summary>
    /// The card's stamp, one part per source. <b>Not one stamp for the card:</b> the stages are fed
    /// by different sources at different ages, so a single age would be the wrong claim about at
    /// least one of them. Each lane keeps its own stamp as well; this one exists so the head can
    /// say which source is missing when a lane has nothing to stamp at all.
    /// </summary>
    public FreshnessModel CardFreshness
    {
        get
        {
            var readable = new List<(string Label, Freshness Freshness)>();
            var missing = new List<string>();

            Add("fleet", ScaleSet?.Freshness);
            Add("prefix", Pool?.Freshness);
            Add("load balancer", LoadBalancer.FirstOrDefault(signal => signal.HasSamples)?.Freshness);
            Add("monitor", Throughput.HasSamples ? Throughput.Freshness : null);

            var parts = new List<string>();
            if (readable.Count > 0)
            {
                parts.Add(FreshnessModel.From([.. readable]).Text);
            }

            parts.AddRange(missing.Select(label => $"{label} unavailable"));

            var text = string.Join(" · ", parts);
            return new FreshnessModel(char.ToUpperInvariant(text[0]) + text[1..]);

            void Add(string label, Freshness? freshness)
            {
                if (freshness is { } value)
                {
                    readable.Add((label, value));
                }
                else
                {
                    missing.Add(label);
                }
            }
        }
    }

    public FreshnessModel? LoadBalancerFreshness =>
        LoadBalancer.FirstOrDefault(signal => signal.HasSamples) is { } signal
            ? FreshnessModel.From(signal.Freshness)
            : null;

    public FreshnessModel? FleetFreshness =>
        ScaleSet is null ? null : FreshnessModel.From(ScaleSet.Freshness);

    public FreshnessModel? PrefixFreshness => Pool is null ? null : FreshnessModel.From(Pool.Freshness);

    // ---- the lanes -----------------------------------------------------------------------------

    public LoadBalancerSignal? DataPath => LoadBalancer.ElementAtOrDefault(0);

    public LoadBalancerSignal? Probes => LoadBalancer.ElementAtOrDefault(1);

    /// <summary>Green while the node is powered on, deny-red otherwise. The dot is the first thing
    /// scanned down the list, so it must not read as healthy for a stopped node.</summary>
    public static string PowerColour(ProxyInstance instance) =>
        IsRunning(instance) ? "var(--allow)" : "var(--deny)";

    public static string PowerLabel(ProxyInstance instance) =>
        string.IsNullOrWhiteSpace(instance.PowerState) ? "power state unknown" : instance.PowerState;

    /// <summary>The node's own lamp, on the same three-state reading as the stations above it.</summary>
    public static LampState NodeLamp(ProxyInstance instance) => Health(instance).Variant switch
    {
        _ when !IsRunning(instance) => LampState.Bad,
        "open" => LampState.Bad,
        "enforce" => LampState.Ok,
        "plain" => LampState.Unread,
        _ => LampState.Warn,
    };

    /// <summary>The lamp's CSS modifier. <c>off</c> is unlit and hollow, which is what makes unread
    /// visually distinct from both healthy and unhealthy rather than a shade of one of them.</summary>
    public static string LampClass(LampState lamp) => lamp switch
    {
        LampState.Ok => "ok",
        LampState.Warn => "warn",
        LampState.Bad => "bad",
        _ => "off",
    };

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

    /// <summary>The prefix drawn address by address, with the ones actually assigned marked.</summary>
    public IReadOnlyList<PoolSlot> PoolSlots => Pool is null
        ? []
        : Slots(Pool, NodeAddresses());

    public StatTone PoolTone => Pool switch
    {
        null => StatTone.Neutral,
        { Available: 0 } => StatTone.Bad,
        { Available: <= 1 } => StatTone.Warn,
        { UtilisationPercent: > 80 } => StatTone.Warn,
        _ => StatTone.Good,
    };

    // ---- the test seam -------------------------------------------------------------------------

    /// <summary>
    /// Everything above this line is a pure function of the three properties the loaders fill, and
    /// this is how a test gets at those functions without a render and without Azure.
    ///
    /// <para>The <see cref="ConsoleData"/> hole is deliberate and is safe for exactly one reason:
    /// nothing reachable from a derivation touches it. Only <c>Load*Async</c> does, and a model
    /// built here is never loaded. If a derivation ever needs to fetch something, that is the
    /// design going wrong rather than this seam needing widening.</para>
    /// </summary>
    internal static RuntimeModel Derive(
        ScaleSetStatus? scaleSet = null,
        EgressPool? pool = null,
        IReadOnlyList<LoadBalancerSignal>? loadBalancer = null,
        RuntimeOptions? deployment = null) =>
        new(null!, deployment ?? new RuntimeOptions(), NullLogger<RuntimeModel>.Instance)
        {
            ScaleSet = scaleSet,
            Pool = pool,
            LoadBalancer = loadBalancer ?? [],
        };

    // ---- page lifecycle ------------------------------------------------------------------------

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Runtime";
        ViewData["Surface"] = Surface.Runtime.Key;

        await Task.WhenAll(
            LoadLoadBalancerAsync(cancellationToken),
            LoadFleetAsync(cancellationToken),
            LoadPoolAsync(cancellationToken),
            LoadChartsAsync(cancellationToken));
    }

    // ---- stage handlers, one per swap target ---------------------------------------------------
    //
    // Each returns a display:contents wrapper holding a station and its lane, so a slow source
    // degrades its own stage while the rest of the card keeps rendering (design.md D1). Every read
    // below goes through the server-side cache, never Azure.

    public async Task<IActionResult> OnGetLoadBalancerStageAsync(CancellationToken cancellationToken)
    {
        await LoadLoadBalancerAsync(cancellationToken);
        return Partial("_RuntimeLoadBalancer", this);
    }

    public async Task<IActionResult> OnGetFleetStageAsync(CancellationToken cancellationToken)
    {
        // The fleet lane draws its gauges from Azure Monitor and its nodes from ARM, so the stage
        // needs both — two caches, no Azure call.
        await Task.WhenAll(LoadFleetAsync(cancellationToken), LoadChartsAsync(cancellationToken));
        return Partial("_RuntimeFleet", this);
    }

    public async Task<IActionResult> OnGetPrefixStageAsync(CancellationToken cancellationToken)
    {
        // The prefix lane attributes addresses to nodes, so it needs the scale set too.
        await Task.WhenAll(LoadFleetAsync(cancellationToken), LoadPoolAsync(cancellationToken));
        return Partial("_RuntimePrefix", this);
    }

    public async Task<IActionResult> OnGetConsequenceAsync(CancellationToken cancellationToken)
    {
        // Composed across every stage, so it re-reads every source — all of them cached.
        await Task.WhenAll(
            LoadLoadBalancerAsync(cancellationToken),
            LoadFleetAsync(cancellationToken),
            LoadPoolAsync(cancellationToken));
        return Partial("_RuntimeConsequence", this);
    }

    // ---- loading -------------------------------------------------------------------------------

    private async Task LoadLoadBalancerAsync(CancellationToken cancellationToken)
    {
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

    private async Task LoadFleetAsync(CancellationToken cancellationToken) =>
        ScaleSet = await TryAsync(() => data.ScaleSetAsync(cancellationToken), "scale set");

    private async Task LoadPoolAsync(CancellationToken cancellationToken) =>
        Pool = await TryAsync(() => data.EgressPoolAsync(cancellationToken), "egress pool");

    private async Task LoadChartsAsync(CancellationToken cancellationToken)
    {
        // Three separate Azure Monitor queries with nothing to say to each other.
        var networkOutTask = MetricAsync(
            RuntimeMetric.NetworkOut, "Network Out Total", "bytes", "throughput", cancellationToken);
        var cpuTask = MetricAsync(
            RuntimeMetric.CpuPercent, "Percentage CPU", "%", "CPU", cancellationToken);
        var availabilityTask = MetricAsync(
            RuntimeMetric.VmAvailability, "VmAvailabilityMetric", "", "VM availability", cancellationToken);

        await Task.WhenAll(networkOutTask, cpuTask, availabilityTask);

        var networkOut = networkOutTask.Result;
        var cpu = cpuTask.Result;
        var availability = availabilityTask.Result;

        // Bytes per minute rendered as megabytes, which is the only unit conversion on this surface
        // and is stated in the suffix rather than assumed. The recorder is wide because it is the
        // one number that belongs to the path rather than to a stage.
        Throughput = Chart("Network out", networkOut, "#5b8def", scale: 1d / 1_000_000, digits: "N1",
            suffix: "MB/min", detail: Summarise(networkOut, 1d / 1_000_000, "N1"), width: 1000, height: 58);

        Cpu = Chart("CPU", cpu, "#6a6bd6", scale: 1, digits: "N0", suffix: "%",
            detail: Summarise(cpu, 1, "N0"), width: 200, height: 34,
            tone: cpu.Latest switch { null => StatTone.Neutral, > 80 => StatTone.Warn, _ => StatTone.Neutral });

        // VmAvailabilityMetric reports 1 when the VM is available and 0 when it is not, so the mean
        // over the window IS the share of samples that reported available. That is what the
        // percentage says, and the detail line says which samples it counted — the console does not
        // have 30 days of history and must not imply that it does.
        Availability = Chart("Availability", availability, "#2f9e6b", scale: 100, digits: "N0", suffix: "%",
            detail: availability.Points.Count == 0
                ? "no samples in this window"
                : $"{Average(availability) * 100:N1}% of the last hour's 1-minute samples reported available",
            width: 200, height: 34,
            value: Average(availability) * 100,
            // A series that returned nothing is not a fleet reporting itself less than available;
            // it gets no tone at all rather than the amber one.
            tone: availability.Points.Count == 0
                ? StatTone.Neutral
                : Average(availability) >= 1 ? StatTone.Good : StatTone.Warn);
    }

    /// <summary>
    /// A series, degraded to an empty one when the read fails.
    ///
    /// <para>An empty series carries <see cref="Freshness.Now"/>, which would be a lie if a stage
    /// stamped it — so no stage does: every freshness stamp on this surface is gated on the series
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

    /// <summary>One load-balancer readout. No samples is its own state: the metric being unreadable
    /// is not the load balancer reporting itself healthy.</summary>
    internal static LoadBalancerSignal Signal(
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

        // The whole series is kept, not just its latest sample: both metrics already arrive as a
        // full hour and drawing them turns "66%" into "since when" at no extra query.
        return new LoadBalancerSignal(label, pill, detail, latest is not null, series.Freshness, series);
    }

    private static LampState SignalLamp(LoadBalancerSignal signal) => signal.Latest switch
    {
        null => LampState.Unread,
        >= 100 => LampState.Ok,
        > 0 => LampState.Warn,
        _ => LampState.Bad,
    };

    private static RuntimeChart EmptyChart(string title, string colour, int width, int height) =>
        Chart(title, MetricSeries.Empty(title, string.Empty), colour, 1, "N0", string.Empty,
            "no samples in this window", width, height);

    private static RuntimeChart Chart(
        string title,
        MetricSeries series,
        string colour,
        double scale,
        string digits,
        string suffix,
        string detail,
        int width,
        int height,
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
                $"{title}, 1-minute samples, last hour",
                width,
                height),
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

    /// <summary>The scale set's sub-line: what is wrong with it, or that nothing is.</summary>
    private static string FleetSummary(ScaleSetStatus scaleSet)
    {
        var images = scaleSet.Instances.Select(ImageLabel).Distinct(StringComparer.Ordinal).ToList();
        var states = new List<string>();

        Count(scaleSet.Instances.Count(node => Health(node).Variant is "open"), "unhealthy");
        Count(scaleSet.Instances.Count(node => !IsRunning(node)), "not running");
        Count(scaleSet.Instances.Count(NotSettled), "provisioning");
        Count(scaleSet.Instances.Count(node => Health(node).Variant is "plain"), "without a health verdict");

        if (states.Count == 0)
        {
            states.Add(scaleSet.Instances.Count == 0 ? "no instances reported" : "all nodes healthy");
        }

        // One image is the normal case and worth naming; several means an upgrade is in flight and
        // the count is the useful thing to say.
        var image = images.Count switch
        {
            1 => images[0],
            > 1 => $"{images.Count} images",
            _ => null,
        };

        return image is null ? string.Join(" · ", states) : $"{image} · {string.Join(" · ", states)}";

        void Count(int count, string what)
        {
            if (count > 0)
            {
                states.Add($"{count} {what}");
            }
        }
    }

    private static string PrefixSummary(EgressPool pool) => pool.Available switch
    {
        0 => "0 spare · the fleet cannot add a node",
        1 => "1 spare · room for one more node",
        var spare => $"{spare} spare · room for {spare} more nodes",
    };

    /// <summary>Spare addresses said as a headcount, because that is what a spare address is.</summary>
    private static string Nodes(int available) => available == 1
        ? "One address is"
        : $"{available} addresses are";

    /// <summary>ARM still working on the instance — an Updating or Failed node is not a settled
    /// one, and the stage should not read as steady while it is in flight.</summary>
    private static bool NotSettled(ProxyInstance instance) =>
        instance.ProvisioningState is { } state
        && !string.Equals(state, "Succeeded", StringComparison.OrdinalIgnoreCase);

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
    /// are still marked — the prefix's own count is authoritative, and a lane that only marked what
    /// it could name would under-report how full the pool is, which is the one error this stage
    /// must not make.</para>
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
    /// One failing source degrades its own stage and no other — a Monitor throttle must not cost
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
            _errors.Record(what);
            return default;
        }
    }
}

/// <summary>
/// A stage's reading, in three states — and <b>unread is one of them</b>.
///
/// <para>Ordered worst-last so a stage composed of several signals can take the maximum. Unread
/// sorts below Warn deliberately: "we could not read it" must never outrank a reading the console
/// actually has, and it is never the state used for a healthy stage (design.md D4).</para>
/// </summary>
public enum LampState
{
    Unread = 0,
    Ok = 1,
    Warn = 2,
    Bad = 3,
}

/// <summary>
/// What a duct says. Motion is a reading, not decoration: only a duct carrying traffic moves.
/// <see cref="Constrained"/> exists because a full prefix caps growth without stopping today's
/// requests, and drawing that as stopped would be an over-claim (design.md D5).
/// </summary>
public enum DuctTone
{
    Flowing,
    Constrained,
    Stopped,
    Unknown,
}

/// <summary>One station of the process line, plus the heading of the lane beneath it.</summary>
/// <param name="Anchor">The grid column class suffix — <c>lb</c>, <c>vmss</c> or <c>pip</c>.</param>
/// <param name="Sub">The sub-line, or the "not readable" note when <see cref="Unread"/>.</param>
public sealed record StageView(
    string Anchor,
    string Tag,
    string Name,
    string Icon,
    string Value,
    string Unit,
    string? Sub,
    LampState Lamp)
{
    /// <summary>Hatched, dashed and unlit — never coloured, and never reporting zero.</summary>
    public bool Unread => Lamp is LampState.Unread;

    public string LampClass => RuntimeModel.LampClass(Lamp);
}

/// <summary>A duct between two stations. <paramref name="Column"/> is the schematic's grid column,
/// which is what places it between the stations it joins.</summary>
public sealed record DuctView(int Column, string Chip, DuctTone Tone)
{
    public string ToneClass => Tone switch
    {
        DuctTone.Constrained => "warn",
        DuctTone.Stopped => "bad",
        DuctTone.Unknown => "dead",
        _ => string.Empty,
    };
}

/// <summary>The sentence that belongs to the path rather than to any one stage.</summary>
/// <param name="Tone">The <c>sx-conseq</c> modifier: <c>bad</c>, <c>ok</c>, <c>dim</c>, or empty
/// for the quiet day — which still renders (design.md D3).</param>
public sealed record ConsequenceView(string Icon, string Headline, string Detail, string Tone);

/// <summary>A load-balancer reading, as the load-balancer lane renders it.</summary>
/// <param name="HasSamples">False when the metric returned nothing — which is "unreadable or not
/// configured", never "healthy".</param>
/// <param name="Series">The full hour, kept so the lane can draw the shape rather than only the
/// latest sample.</param>
public sealed record LoadBalancerSignal(
    string Label,
    PillModel Pill,
    string Detail,
    bool HasSamples,
    Freshness Freshness,
    MetricSeries Series)
{
    public double? Latest => Series.Latest;

    /// <summary>A line on a track, not a filled area: these series sit at 100 nearly always, and a
    /// filled chart pinned at its own maximum reads as a progress bar, which is a different
    /// claim.</summary>
    public ChartModel Track => new(
        [.. Series.Points.Select(point => point.Value ?? 0)],
        "#5b8def",
        $"{Label}, 1-minute samples, last hour",
        260,
        24);
}

/// <summary>A metric readout: the headline number, what it means, and the series behind it.</summary>
public sealed record RuntimeChart(
    string Title,
    string Value,
    string Suffix,
    string Detail,
    StatTone Tone,
    ChartModel Chart,
    Freshness Freshness,
    bool HasSamples)
{
    public string ToneClass => Tone switch
    {
        StatTone.Good => "good",
        StatTone.Warn => "warn",
        StatTone.Bad => "bad",
        _ => string.Empty,
    };
}

/// <summary>One address of the egress prefix.</summary>
/// <param name="Node">The instance holding it, when the console can attribute it. Null on an
/// assigned address it cannot — see <see cref="RuntimeModel.Slots"/>.</param>
public sealed record PoolSlot(string Label, string? Address, bool InUse, string? Node);
