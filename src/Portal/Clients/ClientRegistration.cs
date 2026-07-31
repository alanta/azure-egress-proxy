using Azure.Identity;
using Azure.Monitor.Query;
using Azure.ResourceManager;

namespace Portal.Clients;

public static class ClientRegistration
{
    /// <summary>
    /// Wires the three read-only data sources and the cache in front of them.
    ///
    /// <para>One credential across all three: the portal's own user-assigned managed identity,
    /// holding <c>Reader</c> + <c>Monitoring Reader</c> on the hub resource group and <b>no write
    /// role anywhere</b>. That single identity is why this component concentrates more read power
    /// than anything else in the deployment — recorded as a risk in design.md, and the reason
    /// every client above is read-only in construction rather than by convention.</para>
    /// </summary>
    public static IServiceCollection AddConsoleData(this IServiceCollection services, IConfiguration configuration)
    {
        // In Azure: the portal's own user-assigned managed identity, selected by AZURE_CLIENT_ID,
        // the same way the control plane and the sample app do it.
        //
        // Locally: the mock IdP, through the same shim the sample app uses. Without it the console
        // could not talk to the local control plane at all, and the Aspire loop — push a ruleset,
        // provoke a denial, watch the console trace it back — would not exist.
        services.AddSingleton<Azure.Core.TokenCredential>(_ =>
            ServiceDefaults.EgressProxyLocalTokenCredential.CreateFromConfiguration(configuration)
            ?? new DefaultAzureCredential());

        services.AddSingleton(new ControlPlaneOptions
        {
            BaseUrl = configuration["CONTROL_PLANE_URL"],
            Scope = configuration["CONTROL_PLANE_SCOPE"],
        });

        services.AddSingleton(new AuditOptions
        {
            WorkspaceId = configuration["LOG_ANALYTICS_WORKSPACE_ID"],
        });

        services.AddSingleton(new RuntimeOptions
        {
            SubscriptionId = configuration["HUB_SUBSCRIPTION_ID"],
            ResourceGroup = configuration["HUB_RESOURCE_GROUP"],
            ScaleSetName = configuration["PROXY_SCALE_SET_NAME"],
            PublicIpPrefixName = configuration["EGRESS_IP_PREFIX_NAME"],
            LoadBalancerName = configuration["PROXY_LOAD_BALANCER_NAME"],
        });

        services.AddHttpClient<ControlPlaneClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<ControlPlaneOptions>();
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                // Trailing slash: relative request URIs are resolved against it, and without one
                // BaseAddress silently drops its last path segment.
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            }
        });

        services.AddSingleton(provider => new LogsQueryClient(
            provider.GetRequiredService<Azure.Core.TokenCredential>()));
        services.AddSingleton(provider => new MetricsQueryClient(
            provider.GetRequiredService<Azure.Core.TokenCredential>()));
        services.AddSingleton(provider => new ArmClient(
            provider.GetRequiredService<Azure.Core.TokenCredential>()));

        services.AddSingleton<AuditClient>();
        services.AddSingleton<RuntimeClient>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ResponseCache>();

        // Scoped, because ControlPlaneClient is typed-HttpClient-scoped. Surfaces take this and
        // never the clients underneath.
        services.AddScoped<ConsoleData>();

        return services;
    }
}
