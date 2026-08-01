using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace Portal.Clients;

/// <summary>
/// The scale set's instance-level public IP addresses, read from ARM directly rather than through
/// the resource-manager SDK.
///
/// <para>This is the one place the console leaves the SDK, and it is worth saying why rather than
/// leaving it to look like an oversight. <c>GetVirtualMachineScaleSetPublicIPAddressesAsync</c>
/// sends whichever API version the package was built against — 2025-07-01 in
/// Azure.ResourceManager.Network 1.16.1 — and that version is not rolled out for this operation in
/// every region. In swedencentral it answers <c>400: No registered resource provider found for
/// location 'swedencentral' and API version '2025-07-01'</c>, which reads like a permissions
/// problem and is really a regional rollout gap. <c>ArmClientOptions.SetApiVersion</c> does not
/// reach it: the call is an extension method whose version is not resolved through the type keys
/// that option accepts.</para>
///
/// <para>So the version is pinned here, where it is visible, against a request shape that was
/// checked against a real deployment before being chosen. The two fields read back — an id and an
/// address — have been in this API since 2017, so tracking the newest version buys nothing and
/// costs a class of regional surprise. If the SDK later resolves this properly, this file should
/// go.</para>
///
/// <para>Read-only, like everything else here: one <c>GET</c>, and the type has no other verb.</para>
/// </summary>
public sealed class InstanceAddressClient(
    IHttpClientFactory factory,
    TokenCredential credential,
    ILogger<InstanceAddressClient> logger)
{
    /// <summary>
    /// Named rather than typed, because <see cref="RuntimeClient"/> is a singleton: a typed client
    /// injected into one would hold its handler for the life of the process, and the handler
    /// rotation that keeps DNS fresh would never happen.
    /// </summary>
    public const string HttpClientName = "arm-instance-addresses";

    /// <summary>Old enough to be everywhere, new enough to return what the panel needs.</summary>
    private const string ApiVersion = "2024-07-01";

    private static readonly string[] Scopes = ["https://management.azure.com/.default"];

    /// <summary>
    /// Instance id → public IP address. An empty map is a degraded panel, never an exception: the
    /// node table drops its address column and the rest of the runtime view still renders.
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
}
