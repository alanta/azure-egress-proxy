using Azure.Core;

namespace EgressProxy.Client;

/// <summary>
/// Options for wiring HttpClient egress traffic through the proxy.
/// </summary>
public sealed class EgressProxyOptions
{
    /// <summary>
    /// Gets or sets the proxy audience. Required when <c>HTTPS_PROXY</c> is configured.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Gets or sets the managed identity client ID used as Basic auth username.
    /// If omitted, <c>AZURE_CLIENT_ID</c> is used.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Gets or sets the token credential used to acquire managed-identity tokens.
    /// Defaults to <see cref="Azure.Identity.DefaultAzureCredential"/>.
    /// </summary>
    public TokenCredential? TokenCredential { get; set; }
}
