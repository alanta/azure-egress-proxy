using Azure.Identity;
using Azure.Monitor.Query;
using Azure.ResourceManager;

namespace Portal.Clients;

public static class ClientRegistration
{
    /// <summary>
    /// What a panel is allowed to spend before it gives up and says so.
    ///
    /// <para>This is a console an operator is looking at, not a pipeline. The library defaults are
    /// tuned for the opposite case: the Azure SDK retries four times against a 100-second network
    /// timeout, and <c>AddStandardResilienceHandler</c> allows 30 seconds and three retries — so a
    /// dependency that is not merely slow but <i>unreachable</i> takes the SDK over six minutes to
    /// admit it, during which the page has rendered nothing and the operator has no idea why.</para>
    ///
    /// <para>Waiting does not help here. Every one of these calls is a read that an operator can
    /// simply make again, and the panels already degrade one at a time
    /// (<c>IndexModel.TryAsync</c>), so failing fast costs a refresh and buys an answer. The one
    /// thing a retry genuinely earns is surviving a dropped connection, which is why there is one
    /// and not none.</para>
    /// </summary>
    /// <summary>
    /// The options name <c>AddStandardResilienceHandler</c> gives the typed client's pipeline:
    /// the HTTP client's name, which for a typed client is the type's name, plus <c>-standard</c>.
    /// </summary>
    internal const string ControlPlaneResilienceKey = nameof(ControlPlaneClient) + "-standard";

    internal static class Budget
    {
        /// <summary>One attempt against Azure. Above a human's patience, below their tolerance.</summary>
        internal static readonly TimeSpan Attempt = TimeSpan.FromSeconds(4);

        /// <summary>Everything one panel may spend, retries included.</summary>
        internal static readonly TimeSpan Total = TimeSpan.FromSeconds(10);

        /// <summary>A dropped connection deserves one more go, not a queue of them.</summary>
        internal const int Retries = 1;

        internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);
    }

    /// <summary>
    /// The same budget expressed for an Azure SDK client. Every client here is constructed with
    /// it, so no surface can inherit the 100-second default by being added later.
    /// </summary>
    private static T WithBudget<T>(this T options) where T : Azure.Core.ClientOptions
    {
        options.Retry.NetworkTimeout = Budget.Attempt;
        options.Retry.MaxRetries = Budget.Retries;
        options.Retry.Mode = Azure.Core.RetryMode.Fixed;
        options.Retry.Delay = Budget.RetryDelay;
        return options;
    }

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
            // The credential gets the budget too. A token acquisition that hangs fails every panel
            // at once, so it is the least useful place in the console to be patient.
            ?? new DefaultAzureCredential(new DefaultAzureCredentialOptions().WithBudget()));

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

        // ServiceDefaults already adds the standard resilience handler to every client, so this
        // retunes that handler rather than adding a second one — two pipelines would multiply
        // their budgets, which is how a 10-second intention becomes a 40-second wait. The options
        // are named after the client, a convention `A_panel_gives_up_in_seconds_not_minutes` pins
        // so a rename cannot silently restore the 30-second default.
        services.Configure<Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>(
            ControlPlaneResilienceKey,
            resilience =>
            {
                resilience.AttemptTimeout.Timeout = Budget.Attempt;
                resilience.TotalRequestTimeout.Timeout = Budget.Total;
                resilience.Retry.MaxRetryAttempts = Budget.Retries;
                resilience.Retry.Delay = Budget.RetryDelay;
                resilience.Retry.BackoffType = Polly.DelayBackoffType.Constant;
                // The breaker samples over a window the library requires to be at least twice the
                // attempt timeout; it is not part of the per-request budget.
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            });

        services.AddSingleton(provider => new LogsQueryClient(
            provider.GetRequiredService<Azure.Core.TokenCredential>(),
            new LogsQueryClientOptions().WithBudget()));
        services.AddSingleton(provider => new MetricsQueryClient(
            provider.GetRequiredService<Azure.Core.TokenCredential>(),
            new MetricsQueryClientOptions().WithBudget()));
        services.AddSingleton(provider => new ArmClient(
            provider.GetRequiredService<Azure.Core.TokenCredential>(),
            default(string),
            new ArmClientOptions().WithBudget()));

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
