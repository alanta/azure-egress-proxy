namespace Portal.Clients;

/// <summary>
/// <b>The single entry point every surface uses.</b> The three raw clients are internal plumbing;
/// a Razor page takes this.
///
/// <para>Two reasons it exists rather than pages injecting the clients directly. First, design.md
/// D8 rule 3: polling reads a server-side cache, never Azure directly — routing every read through
/// one type is what makes that checkable rather than a convention each new surface has to
/// remember. Second, the cache keys live in one place, so two surfaces asking the same question
/// share an answer instead of paying for it twice.</para>
///
/// <para>Nothing here writes. <see cref="CheckAsync"/> is the dry run, which the API guarantees
/// writes nothing, and it is deliberately <b>not</b> cached — an operator adjusting a candidate
/// change expects each evaluation to be evaluated.</para>
/// </summary>
public sealed class ConsoleData(
    ControlPlaneClient controlPlane,
    AuditClient audit,
    RuntimeClient runtime,
    ResponseCache cache)
{
    // ---- policy ------------------------------------------------------------------------------

    public Task<PolicySnapshot> PolicyAsync(CancellationToken cancellationToken) =>
        cache.GetAsync("policy", CacheFor.Policy, controlPlane.ReadAsync, cancellationToken);

    /// <summary>Uncached: a dry run is an evaluation of what the operator just typed.</summary>
    public Task<CheckResult> CheckAsync(
        string ruleset,
        IReadOnlyList<string> allowedHosts,
        RulesetAction action,
        CancellationToken cancellationToken) =>
        controlPlane.CheckAsync(ruleset, allowedHosts, action, cancellationToken);

    // ---- traffic -----------------------------------------------------------------------------

    /// <summary>Whether an audit workspace is configured. A surface must check this before
    /// rendering a traffic count — see <see cref="AuditClient.IsConfigured"/>.</summary>
    public bool TrafficIsConfigured => audit.IsConfigured;

    public Task<TrafficSummary> TrafficSummaryAsync(TrafficWindow window, CancellationToken cancellationToken) =>
        cache.GetAsync($"traffic:summary:{window}", CacheFor.Traffic,
            token => audit.SummaryAsync(window, token), cancellationToken);

    public Task<IReadOnlyList<DecisionRow>> DenialsAsync(TrafficWindow window, CancellationToken cancellationToken) =>
        cache.GetAsync($"traffic:denials:{window}", CacheFor.Traffic,
            token => audit.DenialsAsync(window, token), cancellationToken);

    public Task<IReadOnlyList<DecisionRow>> DecisionsForRoleAsync(
        string role,
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        cache.GetAsync($"traffic:role:{role}:{window}", CacheFor.Traffic,
            token => audit.DecisionsForRoleAsync(role, window, token), cancellationToken);

    public Task<IReadOnlyList<AuthFailureGroup>> AuthFailuresAsync(
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        cache.GetAsync($"traffic:authfail:{window}", CacheFor.Traffic,
            token => audit.AuthFailuresAsync(window, token), cancellationToken);

    public Task<IReadOnlyList<ChallengeConversion>> ChallengeConversionAsync(
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        cache.GetAsync($"traffic:challenge:{window}", CacheFor.Traffic,
            token => audit.ChallengeConversionAsync(window, token), cancellationToken);

    public Task<IReadOnlyList<ReportFinding>> ReportFindingsAsync(
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        cache.GetAsync($"traffic:report:{window}", CacheFor.Traffic,
            token => audit.ReportFindingsAsync(window, token), cancellationToken);

    public Task<IReadOnlyList<ReportFinding>> ReportFindingsForRoleAsync(
        string role,
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        cache.GetAsync($"traffic:report:{role}:{window}", CacheFor.Traffic,
            token => audit.ReportFindingsForRoleAsync(role, window, token), cancellationToken);

    public Task<IReadOnlyList<TalkerRow>> TopTalkersAsync(
        TrafficWindow window,
        CancellationToken cancellationToken) =>
        cache.GetAsync($"traffic:talkers:{window}", CacheFor.Traffic,
            token => audit.TopTalkersAsync(window, token), cancellationToken);

    // ---- runtime -----------------------------------------------------------------------------

    public Task<ScaleSetStatus?> ScaleSetAsync(CancellationToken cancellationToken) =>
        cache.GetAsync("runtime:scaleset", CacheFor.Runtime, runtime.ScaleSetAsync, cancellationToken);

    public Task<EgressPool?> EgressPoolAsync(CancellationToken cancellationToken) =>
        cache.GetAsync("runtime:egresspool", CacheFor.Runtime, runtime.EgressPoolAsync, cancellationToken);

    /// <summary>A metric series. The window is quantised into the key so an hour view and a day
    /// view do not evict each other every time an operator switches between them.</summary>
    public Task<MetricSeries> MetricAsync(
        RuntimeMetric metric,
        TimeSpan window,
        CancellationToken cancellationToken) =>
        cache.GetAsync($"metric:{metric}:{window.TotalMinutes}", CacheFor.Metrics,
            token => runtime.MetricAsync(metric, window, token), cancellationToken);
}
