using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

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
            builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = true,
                Proxy = registration.Proxy,
                DefaultProxyCredentials = registration.ProxyCredentials,
                // Pinned, not inherited: a pooled tunnel must be retired by us before the
                // proxy's own idle close (300 s), or the next reuse writes into a dead tunnel.
                PooledConnectionIdleTimeout = registration.PooledConnectionIdleTimeout
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

        // Two things end an idle tunnel before a long-lived pool would reuse it: the proxy's own
        // 300 s idle close, and (if keepalives ever stop covering the path) the 4-minute Azure
        // idle timers on both legs. Staying under the lower of the two is a contract, not a knob.
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

        return new EgressProxyRegistration(proxy, credentials, options.PooledConnectionIdleTimeout);
    }

    private static TokenCredential CreateDefaultTokenCredential() => new DefaultAzureCredential();
}

internal sealed record EgressProxyRegistration(
    IWebProxy Proxy,
    ICredentials ProxyCredentials,
    TimeSpan PooledConnectionIdleTimeout);
