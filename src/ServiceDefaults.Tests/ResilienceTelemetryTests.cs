using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Telemetry;

namespace ServiceDefaults.Tests;

/// <summary>
/// The resilience handler logs a handled retry at Warning and the exhausted attempt at Error,
/// each with the full exception attached — several stack traces per transient failure. Polly
/// always attaches the exception when it logs, so the only way to lose the trace is to not log
/// the event, and <see cref="TelemetryOptions.SeverityProvider"/> is where that is decided.
/// These tests pin the split: the per-attempt chatter goes to Debug, everything else keeps its
/// own severity so a circuit opening still reaches the console.
/// </summary>
public class ResilienceTelemetryTests
{
    private static ResilienceEventSeverity SeverityOf(string eventName, ResilienceEventSeverity declared)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();

        var provider = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<TelemetryOptions>>().Value.SeverityProvider;

        Assert.NotNull(provider);
        return provider(new SeverityProviderArguments(
            source: new ResilienceTelemetrySource("test-standard", string.Empty, "Standard-Retry"),
            resilienceEvent: new ResilienceEvent(declared, eventName),
            context: ResilienceContextPool.Shared.Get()));
    }

    [Theory]
    [InlineData("ExecutionAttempt", ResilienceEventSeverity.Warning)]
    [InlineData("ExecutionAttempt", ResilienceEventSeverity.Error)]
    [InlineData("OnRetry", ResilienceEventSeverity.Warning)]
    public void Per_attempt_events_are_pushed_below_the_default_log_floor(
        string eventName, ResilienceEventSeverity declared)
    {
        Assert.Equal(ResilienceEventSeverity.Debug, SeverityOf(eventName, declared));
    }

    [Theory]
    [InlineData("OnCircuitOpened", ResilienceEventSeverity.Error)]
    [InlineData("OnTimeout", ResilienceEventSeverity.Warning)]
    [InlineData("OnRateLimiterRejected", ResilienceEventSeverity.Warning)]
    public void Every_other_resilience_event_keeps_its_own_severity(
        string eventName, ResilienceEventSeverity declared)
    {
        Assert.Equal(declared, SeverityOf(eventName, declared));
    }
}
