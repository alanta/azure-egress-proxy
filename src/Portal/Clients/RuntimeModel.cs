namespace Portal.Clients;

/// <summary>One proxy node, from the scale set's instance view.</summary>
/// <param name="ProvisioningState">ARM's view: Succeeded, Updating, Failed.</param>
/// <param name="PowerState">Running, deallocated, stopped.</param>
/// <param name="HealthState">The application health extension's verdict, when one is configured.</param>
/// <param name="PublicIp">The instance-level public IP this node egresses from — the address a
/// partner sees, and the reason the IP-pool panel is operational rather than decorative.</param>
public sealed record ProxyInstance(
    string InstanceId,
    string? ComputerName,
    string? ProvisioningState,
    string? PowerState,
    string? HealthState,
    string? PublicIp,
    string? ImageReference);

/// <summary>The scale set as a whole.</summary>
/// <param name="Capacity">What the scale set is configured for.</param>
/// <param name="Running">Instances actually powered on.</param>
public sealed record ScaleSetStatus(
    string Name,
    int Capacity,
    int Running,
    IReadOnlyList<ProxyInstance> Instances,
    Freshness Freshness);

/// <summary>
/// The egress address pool.
///
/// The proxy egresses from instance-level public IPs drawn from a prefix, not a NAT gateway. The
/// prefix is therefore the stable set of addresses partners allowlist on their side, which makes
/// "how much of it is in use" a question with an operational consequence: exhausting it means the
/// next node egresses from an address no partner has allowlisted.
/// </summary>
/// <param name="PrefixLength">The CIDR length, e.g. 28.</param>
/// <param name="Capacity">Addresses the prefix contains.</param>
/// <param name="InUse">Addresses currently assigned.</param>
public sealed record EgressPool(
    string Name,
    string? Prefix,
    int PrefixLength,
    int Capacity,
    int InUse,
    IReadOnlyList<string> Addresses,
    Freshness Freshness)
{
    public int Available => Math.Max(0, Capacity - InUse);

    public double UtilisationPercent => Capacity == 0 ? 0 : InUse * 100.0 / Capacity;
}

/// <summary>One point on a metric series.</summary>
public sealed record MetricPoint(DateTimeOffset Timestamp, double? Value);

/// <summary>
/// A named metric over time, ready for the hand-rolled SVG charts. No charting library is implied
/// — the mockups are server-rendered SVG and stay that way.
/// </summary>
/// <param name="Unit">Rendered as-is next to the value; the portal does not convert units.</param>
/// <param name="Interval">Azure Monitor's grain. One minute is the floor — there is no live data
/// here, and the UI says so rather than implying otherwise (design.md D4).</param>
public sealed record MetricSeries(
    string Name,
    string Unit,
    TimeSpan Interval,
    IReadOnlyList<MetricPoint> Points,
    Freshness Freshness)
{
    public double? Latest => Points.LastOrDefault(p => p.Value is not null)?.Value;

    public double? Peak => Points.Count == 0 ? null : Points.Max(p => p.Value);

    public static MetricSeries Empty(string name, string unit) =>
        new(name, unit, TimeSpan.FromMinutes(1), [], Freshness.Now);
}

/// <summary>Which metric a panel wants, named so a page never passes a raw Azure metric string.</summary>
public enum RuntimeMetric
{
    NetworkIn,
    NetworkOut,
    CpuPercent,
    VmAvailability,
    LoadBalancerDataPathAvailability,
    LoadBalancerHealthProbeStatus,
}
