using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;

namespace Portal.Clients;

public sealed class ControlPlaneOptions
{
    /// <summary>Base address of the control-plane API.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The scope the portal's managed identity requests a token for. The API validates
    /// <c>iss</c>/<c>aud</c> exactly as the proxy does, so this must line up with the
    /// deployment's <c>EXPECT_AUD</c>.
    /// </summary>
    public string? Scope { get; set; }
}

/// <summary>
/// The portal's view of the control-plane API — <b>read-only, with one dry run</b>.
///
/// Two properties hold this type together, and neither is incidental:
///
/// <para><b>It calls as itself.</b> The portal's own managed identity, never the operator's token
/// (design.md D2). A pass-through would pin the portal to Entra forever, because a non-Entra user
/// token cannot satisfy the API's <c>iss</c>/<c>aud</c> validation — the opposite of what keeping
/// human identity in the portal is for. This costs nothing today: every endpoint used here
/// consults no verb, so the portal exercises no authority it could misuse.</para>
///
/// <para><b>It has no write method.</b> Not "does not currently write" — there is no <c>PutAsync</c>
/// and no <c>DeleteAsync</c> to call. <see cref="CheckAsync"/> is the only non-GET, and it is the
/// API's dry run, which validates and returns without touching the blob. A test in
/// <c>Portal.Tests</c> scans the portal's source for write verbs so that adding one is a build
/// failure rather than a review catch.</para>
/// </summary>
public sealed class ControlPlaneClient(
    HttpClient http,
    TokenCredential credential,
    ControlPlaneOptions options,
    ILogger<ControlPlaneClient> logger)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// One read cycle: rulesets, grants, and the fallback, plus the recency the state document
    /// reported. Fetched together because every surface that shows one shows the posture around
    /// it, and three round trips behind one cache entry is cheaper than three cache entries that
    /// can disagree with each other about which state document they saw.
    /// </summary>
    public async Task<PolicySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var (rulesets, recency) = await GetAsync<RulesetsResponse>("rulesets", cancellationToken);
        var (grants, _) = await GetAsync<GrantsResponse>("grants", cancellationToken);
        var (fallback, _) = await GetAsync<FallbackResponse>("fallback", cancellationToken);

        return new PolicySnapshot(
            [.. (rulesets?.Rulesets ?? []).Select(ToView)],
            [.. (grants?.Grants ?? []).Select(g => new GrantView(g.Identity, g.Verbs ?? [], g.Rulesets, g.Note))],
            new FallbackView(fallback?.AllowedHosts ?? [], fallback?.DenyAll ?? true),
            recency,
            Freshness.Now);
    }

    /// <summary>
    /// Validates a candidate change and returns the diff it would produce — <b>writing nothing</b>.
    /// This is the escape hatch that keeps a read-only console from feeling crippled: the operator
    /// sees exactly what the change does, then applies it through the audited machine path from
    /// the snippet the console renders (design.md D3).
    ///
    /// <para>The API runs the same validation and the same onboard gate as a real write, so a
    /// rejection here is a genuine rejection rather than a client-side guess.</para>
    /// </summary>
    public async Task<CheckResult> CheckAsync(
        string ruleset,
        IReadOnlyList<string> allowedHosts,
        RulesetAction action,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            content = new { allowed_hosts = allowedHosts, action = action.ToWire() },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"rulesets/{Uri.EscapeDataString(ruleset)}:check")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        using var response = await SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await JsonSerializer.DeserializeAsync<CheckResponse>(
            await response.Content.ReadAsStreamAsync(cancellationToken), Json, cancellationToken);

        return new CheckResult(body?.Added ?? [], body?.Removed ?? [], body?.Bound ?? [], body?.Unbound ?? []);
    }

    private async Task<(T? Body, Recency Recency)> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await JsonSerializer.DeserializeAsync<T>(
            await response.Content.ReadAsStreamAsync(cancellationToken), Json, cancellationToken);

        return (body, new Recency(response.Content.Headers.LastModified, response.Headers.ETag?.Tag));
    }

    /// <summary>
    /// Attaches the portal's own managed-identity token. Acquired per request rather than cached
    /// here: <see cref="TokenCredential"/> implementations already cache and refresh, and a second
    /// cache in front of one is how a console ends up presenting an expired token for an hour.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var scope = options.Scope
            ?? throw new InvalidOperationException("configure ControlPlane:Scope for the portal's token");

        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

        logger.LogDebug("control plane {Method} {Path}", request.Method, request.RequestUri);
        return await http.SendAsync(request, cancellationToken);
    }

    private static RulesetView ToView(RulesetPayload ruleset) => new(
        ruleset.Name,
        [.. (ruleset.Subjects ?? []).Select(s => new SubjectView(s.Appid, s.Netid))],
        ruleset.Content?.AllowedHosts ?? [],
        RulesetActions.Normalize(ruleset.Content?.Action),
        ruleset.Owner);

    // ---- wire shapes -------------------------------------------------------------------------
    // Private and minimal on purpose: the API's response shape is the control plane's contract,
    // and the portal's surfaces depend on the DTOs above instead, so a field added there does not
    // reach a Razor page without someone deciding it should.

    private sealed record RulesetsResponse(List<RulesetPayload>? Rulesets);

    private sealed record RulesetPayload(
        string Name,
        List<SubjectPayload>? Subjects,
        ContentPayload? Content,
        string? Owner);

    private sealed record SubjectPayload(string? Appid, string? Netid);

    private sealed record ContentPayload(List<string>? AllowedHosts, string? Action);

    private sealed record GrantsResponse(List<GrantPayload>? Grants);

    private sealed record GrantPayload(
        string Identity,
        List<string>? Verbs,
        List<string>? Rulesets,
        string? Note);

    private sealed record FallbackResponse(List<string>? AllowedHosts, bool DenyAll);

    private sealed record CheckResponse(
        List<string>? Added,
        List<string>? Removed,
        List<string>? Bound,
        List<string>? Unbound);
}
