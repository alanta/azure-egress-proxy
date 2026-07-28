using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;

namespace EgressProxy.Client;

/// <summary>
/// ServiceCollection extensions for configuring outbound HttpClient proxying.
/// </summary>
public static class EgressProxyServiceCollectionExtensions
{
    /// <summary>
    /// Upper bound for <see cref="EgressProxyOptions.PooledConnectionIdleTimeout"/>: the Azure
    /// default idle timeout on the internal load balancer rule and on the instance public IP
    /// SNAT flow (see <c>infra/modules/hub.bicep</c>, <c>proxyIdleTimeoutInMinutes</c>).
    /// </summary>
    internal static readonly TimeSpan MaxPooledConnectionIdleTimeout = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Configures HttpClientFactory defaults to route through <c>HTTPS_PROXY</c> using
    /// managed-identity credentials when proxy environment variables are present.
    /// </summary>
    /// <param name="services">Service collection to update.</param>
    /// <param name="configure">Optional options configuration callback.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when proxying is enabled but required options are missing.
    /// </exception>
    public static IServiceCollection AddEgressProxy(
        this IServiceCollection services,
        Action<EgressProxyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new EgressProxyOptions();
        configure?.Invoke(options);

        var registration = BuildRegistration(options, Environment.GetEnvironmentVariable);
        if (registration is null)
        {
            return services;
        }

        services.ConfigureHttpClientDefaults(builder =>
        {
            builder.ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new SocketsHttpHandler
                {
                    UseProxy = true,
                    Proxy = registration.Proxy,
                    DefaultProxyCredentials = registration.ProxyCredentials,
                    // Pinned, not inherited: the tunnel must be retired by us before Azure's
                    // idle reaper takes it, or the next reuse writes into a dead tunnel.
                    PooledConnectionIdleTimeout = registration.PooledConnectionIdleTimeout
                };

                if (registration.TcpKeepAliveTime > TimeSpan.Zero)
                {
                    var keepAliveTime = registration.TcpKeepAliveTime;
                    handler.ConnectCallback = (context, cancellationToken) =>
                        ConnectWithKeepAliveAsync(context, keepAliveTime, cancellationToken);
                }

                return handler;
            });
        });

        return services;
    }

    internal static EgressProxyRegistration? BuildRegistration(
        EgressProxyOptions options,
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var proxyValue = getEnvironmentVariable("HTTPS_PROXY");
        if (string.IsNullOrWhiteSpace(proxyValue))
        {
            proxyValue = getEnvironmentVariable("https_proxy");
        }

        if (string.IsNullOrWhiteSpace(proxyValue))
        {
            return null;
        }

        if (!Uri.TryCreate(proxyValue, UriKind.Absolute, out var proxyUri))
        {
            throw new InvalidOperationException($"Invalid HTTPS_PROXY value: '{proxyValue}'.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new InvalidOperationException("Egress proxy audience is required when HTTPS_PROXY is set.");
        }

        // Both tunnel legs (client → ILB → proxy, proxy → SNAT → destination) carry an Azure idle
        // timer of 4 minutes by default. A pool that outlives it hands out tunnels the platform
        // has already reaped, so this ordering is a contract, not a tuning knob.
        if (options.PooledConnectionIdleTimeout <= TimeSpan.Zero
            || options.PooledConnectionIdleTimeout >= MaxPooledConnectionIdleTimeout)
        {
            throw new InvalidOperationException(
                $"EgressProxyOptions.PooledConnectionIdleTimeout must be greater than zero and below "
                + $"{MaxPooledConnectionIdleTimeout.TotalMinutes} minutes so pooled tunnels are closed "
                + "before the Azure idle timeout reaps them.");
        }

        var clientId = options.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            clientId = getEnvironmentVariable("AZURE_CLIENT_ID");
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Managed identity client ID is required; set EgressProxyOptions.ClientId or AZURE_CLIENT_ID.");
        }

        var tokenCredential = options.TokenCredential ?? CreateDefaultTokenCredential();
        var noProxyValue = getEnvironmentVariable("NO_PROXY") ?? getEnvironmentVariable("no_proxy");
        var proxy = new NoProxyWebProxy(proxyUri, noProxyValue);
        var credentials = new EgressProxyCredentials(clientId, options.Audience, tokenCredential);

        return new EgressProxyRegistration(
            proxy,
            credentials,
            options.PooledConnectionIdleTimeout,
            options.TcpKeepAliveTime);
    }

    private static async ValueTask<Stream> ConnectWithKeepAliveAsync(
        SocketsHttpConnectionContext context,
        TimeSpan keepAliveTime,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            EnableKeepAlive(socket, keepAliveTime);
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void EnableKeepAlive(Socket socket, TimeSpan keepAliveTime)
    {
        // Probe cadence once the idle period elapses: 3 probes, 5s apart, so a dead tunnel
        // surfaces within ~15s of going quiet instead of on the application's next write.
        TrySetSocketOption(socket, SocketOptionLevel.Socket, SocketOptionName.KeepAlive, 1);
        TrySetSocketOption(
            socket,
            SocketOptionLevel.Tcp,
            SocketOptionName.TcpKeepAliveTime,
            Math.Max(1, (int)keepAliveTime.TotalSeconds));
        TrySetSocketOption(socket, SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
        TrySetSocketOption(socket, SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
    }

    // Keepalive tuning is best-effort: not every option is settable on every platform, and a
    // missing knob must not cost the caller their connection.
    private static void TrySetSocketOption(
        Socket socket,
        SocketOptionLevel level,
        SocketOptionName name,
        int value)
    {
        try
        {
            socket.SetSocketOption(level, name, value);
        }
        catch (SocketException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static TokenCredential CreateDefaultTokenCredential() => new DefaultAzureCredential();
}

internal sealed record EgressProxyRegistration(
    IWebProxy Proxy,
    ICredentials ProxyCredentials,
    TimeSpan PooledConnectionIdleTimeout,
    TimeSpan TcpKeepAliveTime);
