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

    /// <summary>
    /// Gets or sets how long an idle pooled CONNECT tunnel is kept before the client closes it.
    /// Must stay below the Azure idle timeout on both tunnel legs (4 min by default), or the
    /// platform reaps the tunnel first and the next write black-holes.
    /// Defaults to 1 minute.
    /// </summary>
    public TimeSpan PooledConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the TCP keepalive idle period applied to proxy connections. Keepalives keep
    /// a tunnel that is in-use but quiet (SSE, long-poll, gRPC streaming) off the platform's idle
    /// reaper, and surface an already-dead tunnel in seconds instead of on the next write.
    /// Set to <see cref="TimeSpan.Zero"/> or less to disable. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan TcpKeepAliveTime { get; set; } = TimeSpan.FromSeconds(30);
}
