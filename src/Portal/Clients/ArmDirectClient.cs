using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace Portal.Clients;

/// <summary>
/// The two ARM reads the resource-manager SDK gets wrong here, made directly instead.
///
/// <para>This is the only place the console leaves the SDK, and it is worth saying why rather than
/// leaving it to look like an oversight. Both were found by deploying, and neither is our bug —
/// but both are ours to work around, because both fail in ways that look like something else. Both
/// are filed upstream, and <b>this whole file should be deleted when they are fixed</b>; the issue
/// numbers are on the two methods below so a reader who checks one can find the other.</para>
///
/// <para>Full write-ups, with reproductions that stand alone:
/// <see href="https://github.com/alanta/azure-egress-proxy/blob/main/AZURE-SDK-ISSUES.md">AZURE-SDK-ISSUES.md</see>.</para>
///
/// <list type="bullet">
/// <item><b>The scale set's instance addresses</b>
/// (<see href="https://github.com/Azure/azure-sdk-for-net/issues/61649">Azure/azure-sdk-for-net#61649</see>).
/// <c>GetVirtualMachineScaleSetPublicIPAddressesAsync</c> sends the API version its package was
/// built against — 2025-07-01 in Azure.ResourceManager.Network 1.16.1 — which is not rolled out
/// for that operation in every region. In swedencentral it answers <c>400: No registered resource
/// provider found</c>, which reads as a permissions problem and is a regional rollout gap.
/// <c>ArmClientOptions.SetApiVersion</c> does not reach it: the call is an extension method whose
/// version is not resolved through the type keys that option accepts.</item>
/// <item><b>The public IP prefix's allocated addresses</b>
/// (<see href="https://github.com/Azure/azure-sdk-for-net/issues/61648">Azure/azure-sdk-for-net#61648</see>).
/// Worse, because it fails silently:
/// <c>PublicIPPrefixData.PublicIPAddresses</c> deserialises as empty however many addresses the
/// service returns. The wire carries <c>properties.publicIPAddresses</c> at every API version the
/// region supports; the model does not surface it. The panel therefore reported <i>0 of 2 in
/// use</i> for a prefix that was fully consumed — an exhausted pool reading as an empty one, which
/// is exactly backwards for the question that panel exists to answer.</item>
/// </list>
///
/// <para>So both are read here, at a pinned version, against request shapes checked against a real
/// deployment before being chosen. The fields read back — ids, an address, a prefix and its length
/// — have been in this API since 2017, so tracking the newest version buys nothing and costs a
/// class of regional surprise. If the SDK ever handles both properly, this file should go.</para>
///
/// <para>Read-only, like everything else here: <c>GET</c>, and the type has no other verb.</para>
/// </summary>
public sealed class ArmDirectClient(
    IHttpClientFactory factory,
    TokenCredential credential,
    ILogger<ArmDirectClient> logger)
{
    /// <summary>
    /// Named rather than typed, because <see cref="RuntimeClient"/> is a singleton: a typed client
    /// injected into one would hold its handler for the life of the process, and the handler
    /// rotation that keeps DNS fresh would never happen.
    /// </summary>
    public const string HttpClientName = "arm-direct";

    /// <summary>Old enough to be everywhere, new enough to return what the panel needs.</summary>
    private const string ApiVersion = "2024-07-01";

    private static readonly string[] Scopes = ["https://management.azure.com/.default"];

    /// <summary>
    /// Instance id → public IP address. An empty map is a degraded panel, never an exception: the
    /// node table drops its address column and the rest of the runtime view still renders.
    ///
    /// <para><b>Workaround for
    /// <see href="https://github.com/Azure/azure-sdk-for-net/issues/61649">Azure/azure-sdk-for-net#61649</see></b>
    /// — <c>GetVirtualMachineScaleSetPublicIPAddressesAsync</c> sends an API version the region
    /// does not support, and <c>SetApiVersion</c> cannot override it. When that issue closes,
    /// replace this with the SDK call and check the Runtime surface still lists node addresses.</para>
    /// </summary>
    public async Task<Dictionary<string, string>> ForScaleSetAsync(
        string subscriptionId,
        string resourceGroup,
        string scaleSetName,
        CancellationToken cancellationToken)
    {
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);

            var path = $"subscriptions/{Uri.EscapeDataString(subscriptionId)}"
                + $"/resourceGroups/{Uri.EscapeDataString(resourceGroup)}"
                + $"/providers/Microsoft.Compute/virtualMachineScaleSets/{Uri.EscapeDataString(scaleSetName)}"
                + $"/publicIPAddresses?api-version={ApiVersion}";

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var http = factory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Reader on the hub resource group covers this call, so a refusal is worth a line
                // in the log — but it degrades the address column rather than the whole surface.
                logger.LogWarning(
                    "instance public IPs are not readable ({Status}); the node table will omit addresses",
                    (int)response.StatusCode);
                return addresses;
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));

            if (!document.RootElement.TryGetProperty("value", out var value))
            {
                return addresses;
            }

            foreach (var entry in value.EnumerateArray())
            {
                // .../virtualMachines/{instanceId}/networkInterfaces/... — the instance id is the
                // segment after virtualMachines, which is how an address is attributed to a node.
                var id = entry.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                var address = entry.TryGetProperty("properties", out var properties)
                    && properties.TryGetProperty("ipAddress", out var ip)
                        ? ip.GetString()
                        : null;

                if (id is null || string.IsNullOrEmpty(address))
                {
                    continue;
                }

                var segments = id.Split('/');
                var index = Array.FindIndex(segments, s =>
                    string.Equals(s, "virtualMachines", StringComparison.OrdinalIgnoreCase));

                if (index >= 0 && index + 1 < segments.Length)
                {
                    addresses[segments[index + 1]] = address;
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "instance public IPs could not be read; the node table will omit addresses");
        }

        return addresses;
    }
    /// <summary>
    /// The prefix, its length, and the ids of the addresses allocated from it. <c>null</c> when the
    /// read failed, so the panel can say so rather than claim an empty pool.
    ///
    /// <para><b>Workaround for
    /// <see href="https://github.com/Azure/azure-sdk-for-net/issues/61648">Azure/azure-sdk-for-net#61648</see></b>
    /// — <c>PublicIPPrefixData.PublicIPAddresses</c> deserialises as empty whatever the service
    /// returns. When that issue closes, replace this with the SDK call and check the egress pool
    /// panel against a prefix whose addresses are actually assigned: the failure mode is a silent
    /// zero, so a test on an unused prefix would pass either way.</para>
    /// </summary>
    public async Task<PrefixSnapshot?> PrefixAsync(
        string subscriptionId,
        string resourceGroup,
        string prefixName,
        CancellationToken cancellationToken)
    {
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);

            var path = $"subscriptions/{Uri.EscapeDataString(subscriptionId)}"
                + $"/resourceGroups/{Uri.EscapeDataString(resourceGroup)}"
                + $"/providers/Microsoft.Network/publicIPPrefixes/{Uri.EscapeDataString(prefixName)}"
                + $"?api-version={ApiVersion}";

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var http = factory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "reading the egress public IP prefix failed ({Status})", (int)response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));

            var root = document.RootElement;
            var properties = root.TryGetProperty("properties", out var p) ? p : default;

            var addresses = new List<string>();
            if (properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty("publicIPAddresses", out var allocated)
                && allocated.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in allocated.EnumerateArray())
                {
                    if (entry.TryGetProperty("id", out var id) && id.GetString() is { } value)
                    {
                        addresses.Add(value);
                    }
                }
            }

            return new PrefixSnapshot(
                root.TryGetProperty("name", out var name) ? name.GetString() : prefixName,
                properties.ValueKind == JsonValueKind.Object
                    && properties.TryGetProperty("ipPrefix", out var cidr) ? cidr.GetString() : null,
                properties.ValueKind == JsonValueKind.Object
                    && properties.TryGetProperty("prefixLength", out var length) ? length.GetInt32() : 0,
                addresses);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "reading the egress public IP prefix failed");
            return null;
        }
    }
}

/// <summary>What the egress pool panel needs from the prefix, and nothing else.</summary>
/// <param name="Name">The prefix resource's name.</param>
/// <param name="IPPrefix">Its CIDR.</param>
/// <param name="PrefixLength">The mask length, from which capacity is derived rather than counted.</param>
/// <param name="AddressIds">Ids of the addresses allocated from it — the "in use" half of the panel.</param>
public sealed record PrefixSnapshot(
    string? Name,
    string? IPPrefix,
    int PrefixLength,
    IReadOnlyList<string> AddressIds);
