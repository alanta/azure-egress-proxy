using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Portal.Clients;

namespace Portal.Tests;

/// <summary>
/// The console is something an operator is looking at, so an unreachable dependency has to become
/// a sentence on the page in seconds. The library defaults do the opposite — the Azure SDK's
/// 100-second network timeout across four tries, and the standard resilience handler's 30-second
/// budget — and both are inherited silently, which is what makes them worth a test rather than a
/// comment.
/// </summary>
public class BudgetTests
{
    /// <summary>
    /// The standard handler's options are keyed by a string built from the client's name. Nothing
    /// fails loudly if that key stops matching: the Configure call simply tunes options no handler
    /// reads, and the 30-second default quietly returns. So the key is asserted against the name
    /// the handler actually registers.
    /// </summary>
    [Fact]
    public void A_panel_gives_up_in_seconds_not_minutes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        services.AddConsoleData(new ConfigurationBuilder().Build());

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(ClientRegistration.ControlPlaneResilienceKey);

        Assert.Equal(TimeSpan.FromSeconds(4), options.AttemptTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.TotalRequestTimeout.Timeout);
        Assert.Equal(1, options.Retry.MaxRetryAttempts);

        // The whole point: a dead dependency costs a page one refresh, not a coffee break.
        Assert.True(options.TotalRequestTimeout.Timeout < TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Every client resolves. Two of these are singletons that depend on other clients, which is
    /// the wiring most likely to be wrong in a way no unit test notices and the deployed console
    /// discovers on its first request.
    /// </summary>
    [Fact]
    public void The_console_resolves_its_clients()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        services.AddConsoleData(new ConfigurationBuilder().Build());

        // Validating on build is the point: it catches a singleton taking a scoped dependency,
        // which is exactly what RuntimeClient would have done by holding a typed HttpClient.
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RuntimeClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<InstanceAddressClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ConsoleData>());
    }

    /// <summary>
    /// The key above is only correct if it matches what the handler registers for this client.
    /// `AddStandardResilienceHandler` names its options `{clientName}-standard`, and for a typed
    /// client the name is the type's name.
    /// </summary>
    [Fact]
    public void The_resilience_key_names_the_control_plane_client()
    {
        Assert.Equal(
            nameof(ControlPlaneClient) + "-standard",
            ClientRegistration.ControlPlaneResilienceKey);
    }

    /// <summary>
    /// Every surface that can record a failed read has to show it. An empty panel that could not
    /// be filled and an empty panel with nothing to report look identical, and they are opposite
    /// findings — "no denials" versus "we could not ask".
    /// </summary>
    [Theory]
    [InlineData("Index")]
    [InlineData("Runtime")]
    [InlineData("Platform")]
    [InlineData("Lookup")]
    [InlineData("Rulesets")]
    public void A_surface_that_records_a_failed_read_renders_it(string surface)
    {
        var model = Repo.ReadText($"src/Portal/Pages/{surface}.cshtml.cs");
        if (!model.Contains("Error = ", StringComparison.Ordinal))
        {
            return;
        }

        Assert.Contains("_Unavailable", Repo.ReadText($"src/Portal/Pages/{surface}.cshtml"),
            StringComparison.Ordinal);
    }
}
