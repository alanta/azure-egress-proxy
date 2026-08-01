using System.Net;
using Azure;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources;

namespace Portal.Clients;

public sealed class RuntimeOptions
{
    /// <summary>Subscription holding the hub resource group.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>Hub resource group — the only scope the portal's identity has any role on.</summary>
    public string? ResourceGroup { get; set; }

    /// <summary>The proxy scale set.</summary>
    public string? ScaleSetName { get; set; }

    /// <summary>The public IP prefix the instance-level addresses are drawn from.</summary>
    public string? PublicIpPrefixName { get; set; }

    /// <summary>The internal load balancer in front of the proxy.</summary>
    public string? LoadBalancerName { get; set; }
}

/// <summary>
/// The deployment's runtime state, read from Azure — <b>not from the proxy</b>.
///
/// <para>The proxy exposes no HTTP admin surface and gains none here. Adding a metrics listener to
/// a security appliance means a new listening port with its own authentication and NSG question,
/// which would be its own proposal. So: ARM for configuration, Azure Monitor for metrics, one
/// managed identity holding <c>Reader</c> + <c>Monitoring Reader</c> on the hub resource group and
/// no write role anywhere (design.md D4).</para>
///
/// <para><b>Nothing here is live.</b> Azure Monitor's grain is one minute and every series says so
/// through <see cref="MetricSeries.Interval"/> and <see cref="Freshness"/>. Surfaces state the
/// recency; they must not present these numbers as current.</para>
///
/// <para>The "never reach into ARM" rule governs the <i>control-plane API</i>, where consulting
/// Azure about identity provenance would make policy decisions depend on Azure's view of the
/// world. This client reads ARM as an observer and decides nothing — the distinction is
/// deliberate and is not an erosion of that rule.</para>
/// </summary>
public sealed class RuntimeClient(
    ArmClient arm,
    MetricsQueryClient metrics,
    InstanceAddressClient instanceAddresses,
    RuntimeOptions options,
    ILogger<RuntimeClient> logger)
{
    /// <summary>Scale-set capacity plus the per-instance view, joined to the instance-level public
    /// IPs so one panel can answer "which node, which address".</summary>
    public async Task<ScaleSetStatus?> ScaleSetAsync(CancellationToken cancellationToken)
    {
        if (ResourceGroup() is not { } group || string.IsNullOrWhiteSpace(options.ScaleSetName))
        {
            return null;
        }

        try
        {
            var scaleSet = await group.GetVirtualMachineScaleSets()
                .GetAsync(options.ScaleSetName, cancellationToken: cancellationToken);

            var addresses = await InstanceAddressesAsync(options.ScaleSetName, cancellationToken);
            var instances = new List<ProxyInstance>();

            await foreach (var vm in scaleSet.Value.GetVirtualMachineScaleSetVms()
                .GetAllAsync(expand: "instanceView", cancellationToken: cancellationToken))
            {
                var view = vm.Data.InstanceView;
                var instanceId = vm.Data.InstanceId ?? string.Empty;
                instances.Add(new ProxyInstance(
                    instanceId,
                    vm.Data.OSProfile?.ComputerName,
                    vm.Data.ProvisioningState,
                    // The power state arrives as a status code like "PowerState/running"; the
                    // half after the slash is the only part worth showing an operator.
                    view?.Statuses?.FirstOrDefault(s => s.Code?.StartsWith("PowerState/", StringComparison.Ordinal) == true)
                        ?.Code?.Split('/').Last(),
                    view?.VmHealthStatus?.Code,
                    addresses.GetValueOrDefault(instanceId),
                    vm.Data.StorageProfile?.ImageReference?.Id
                        ?? vm.Data.StorageProfile?.ImageReference?.CommunityGalleryImageId
                        ?? vm.Data.StorageProfile?.ImageReference?.Sku));
            }

            var capacity = (int)(scaleSet.Value.Data.Sku?.Capacity ?? instances.Count);
            var running = instances.Count(i => string.Equals(i.PowerState, "running", StringComparison.OrdinalIgnoreCase));

            return new ScaleSetStatus(
                scaleSet.Value.Data.Name, capacity, running, instances, Freshness.Now);
        }
        catch (RequestFailedException e)
        {
            logger.LogError(e, "reading the proxy scale set failed ({Status})", e.Status);
            return null;
        }
    }

    /// <summary>
    /// Prefix capacity versus addresses in use. Capacity is derived from the prefix length rather
    /// than counted, so an exhausted pool reads as exhausted instead of as "all addresses healthy".
    /// </summary>
    public async Task<EgressPool?> EgressPoolAsync(CancellationToken cancellationToken)
    {
        if (ResourceGroup() is not { } group || string.IsNullOrWhiteSpace(options.PublicIpPrefixName))
        {
            return null;
        }

        try
        {
            var prefix = await group.GetPublicIPPrefixes()
                .GetAsync(options.PublicIpPrefixName, cancellationToken: cancellationToken);

            var data = prefix.Value.Data;
            var length = data.PrefixLength ?? 0;
            var addresses = (data.PublicIPAddresses ?? []).Select(a => a.Id?.ToString() ?? string.Empty).ToList();

            return new EgressPool(
                data.Name,
                data.IPPrefix,
                length,
                // An IPv4 prefix of length /n holds 2^(32-n) addresses. Azure reserves none of a
                // public IP prefix, unlike a subnet, so this is the usable count as well.
                length is > 0 and <= 32 ? 1 << (32 - length) : 0,
                addresses.Count,
                addresses,
                Freshness.Now);
        }
        catch (RequestFailedException e)
        {
            logger.LogError(e, "reading the egress public IP prefix failed ({Status})", e.Status);
            return null;
        }
    }

    /// <summary>
    /// One metric series over the given window. Every metric the console renders is named in
    /// <see cref="RuntimeMetric"/>, so no page passes an Azure metric string and no page can widen
    /// the resource a query runs against.
    /// </summary>
    public async Task<MetricSeries> MetricAsync(
        RuntimeMetric metric,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var (name, unit, resource) = Describe(metric);
        if (resource is null)
        {
            return MetricSeries.Empty(name, unit);
        }

        try
        {
            var response = await metrics.QueryResourceAsync(
                resource,
                [name],
                new MetricsQueryOptions
                {
                    TimeRange = new QueryTimeRange(window),
                    // Azure Monitor's floor for these metrics. Asking for finer would not produce
                    // finer data, and the UI must not imply it has any.
                    Granularity = TimeSpan.FromMinutes(1),
                    Aggregations = { MetricAggregationType.Average },
                },
                cancellationToken);

            var series = response.Value.Metrics.FirstOrDefault()?.TimeSeries.FirstOrDefault();
            var points = (series?.Values ?? [])
                .Select(v => new MetricPoint(v.TimeStamp, v.Average))
                .ToList();

            return new MetricSeries(name, unit, TimeSpan.FromMinutes(1), points, Freshness.Now);
        }
        catch (RequestFailedException e)
        {
            logger.LogError(e, "metric {Metric} query failed ({Status})", name, e.Status);
            return MetricSeries.Empty(name, unit);
        }
    }

    /// <summary>The Azure metric name, the unit to render, and the resource it lives on.</summary>
    private (string Name, string Unit, string? Resource) Describe(RuntimeMetric metric) => metric switch
    {
        RuntimeMetric.NetworkIn => ("Network In Total", "bytes", ScaleSetId()),
        RuntimeMetric.NetworkOut => ("Network Out Total", "bytes", ScaleSetId()),
        RuntimeMetric.CpuPercent => ("Percentage CPU", "%", ScaleSetId()),
        RuntimeMetric.VmAvailability => ("VmAvailabilityMetric", "", ScaleSetId()),
        RuntimeMetric.LoadBalancerDataPathAvailability => ("VipAvailability", "%", LoadBalancerId()),
        RuntimeMetric.LoadBalancerHealthProbeStatus => ("DipAvailability", "%", LoadBalancerId()),
        _ => (metric.ToString(), "", null),
    };

    /// <summary>
    /// Maps instance id to its instance-level public IP. The addresses live on the scale set's
    /// network interfaces rather than on the VM, so they need their own pass — and they are the
    /// half of the runtime view that actually matters to a partner allowlist.
    ///
    /// <para>Delegated to <see cref="InstanceAddressClient"/>, which calls ARM directly: the SDK
    /// sends an API version that is not available for this operation in every region, and answers
    /// a 400 that looks like a permissions problem. The client degrades to an empty map rather
    /// than throwing, so a node table without addresses is the worst case here.</para>
    /// </summary>
    private Task<Dictionary<string, string>> InstanceAddressesAsync(
        string scaleSetName,
        CancellationToken cancellationToken) =>
        instanceAddresses.ForScaleSetAsync(
            options.SubscriptionId!, options.ResourceGroup!, scaleSetName, cancellationToken);

    private ResourceGroupResource? ResourceGroup()
    {
        if (string.IsNullOrWhiteSpace(options.SubscriptionId) || string.IsNullOrWhiteSpace(options.ResourceGroup))
        {
            logger.LogWarning("no hub subscription/resource group configured; runtime surfaces will be empty");
            return null;
        }

        return arm.GetResourceGroupResource(ResourceGroupIdentifier());
    }

    private Azure.Core.ResourceIdentifier ResourceGroupIdentifier() =>
        ResourceGroupResource.CreateResourceIdentifier(options.SubscriptionId, options.ResourceGroup);

    private string? ScaleSetId() => string.IsNullOrWhiteSpace(options.ScaleSetName)
        ? null
        : $"{ResourceGroupIdentifier()}/providers/Microsoft.Compute/virtualMachineScaleSets/{options.ScaleSetName}";

    private string? LoadBalancerId() => string.IsNullOrWhiteSpace(options.LoadBalancerName)
        ? null
        : $"{ResourceGroupIdentifier()}/providers/Microsoft.Network/loadBalancers/{options.LoadBalancerName}";
}
