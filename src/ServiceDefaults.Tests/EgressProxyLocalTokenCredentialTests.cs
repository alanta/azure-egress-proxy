using System.Text;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using ServiceDefaults;

namespace ServiceDefaults.Tests;

/// <summary>
/// The local-development stand-in for managed identity: it fetches a token from the AppHost's mock
/// IdP and reports its expiry so the Azure SDK caches it like a real one. The failure modes worth
/// pinning are the ones that would otherwise show up as an opaque 401 much later — a credential
/// that returns a blank token, or one that reports the wrong expiry and never refreshes.
/// </summary>
public class EgressProxyLocalTokenCredentialTests : IClassFixture<MockIdpServer>
{
    private const string AppId = "22222222-2222-2222-2222-222222222222";

    private readonly MockIdpServer _idp;

    public EgressProxyLocalTokenCredentialTests(MockIdpServer idp)
    {
        _idp = idp;
        _idp.Status = 200;
        _idp.Respond = _ => MockIdpServer.Jwt(new { exp = 4102444800L });
    }

    // ---- configuration ------------------------------------------------------------------------

    [Fact]
    public void A_null_configuration_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => EgressProxyLocalTokenCredential.CreateFromConfiguration(null!));
    }

    /// <summary>
    /// No endpoint means "not running locally against the mock IdP", so the caller falls back to
    /// the real credential chain. Returning null rather than throwing is what makes the same
    /// configuration code work unchanged in Azure.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_token_endpoint_means_no_local_credential(string? endpoint)
    {
        var credential = EgressProxyLocalTokenCredential.CreateFromConfiguration(
            Configuration(("EgressProxy:TokenEndpoint", endpoint)));

        Assert.Null(credential);
    }

    /// <summary>
    /// Half-configured is a mistake, not a fallback: without a client ID the mock IdP cannot mint a
    /// token for any particular workload, so silently returning null would hand back the wrong
    /// identity instead of a clear error.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_endpoint_without_a_client_id_fails_fast(string? clientId)
    {
        var configuration = Configuration(
            ("EgressProxy:TokenEndpoint", _idp.TokenEndpoint),
            ("EgressProxy:ClientId", clientId));

        var error = Assert.Throws<InvalidOperationException>(
            () => EgressProxyLocalTokenCredential.CreateFromConfiguration(configuration));

        Assert.Contains("EgressProxy:ClientId", error.Message, StringComparison.Ordinal);
    }

    // ---- fetching -----------------------------------------------------------------------------

    [Fact]
    public async Task A_fetched_token_carries_the_expiry_from_its_own_exp_claim()
    {
        _idp.Respond = _ => MockIdpServer.Jwt(new { exp = 1893456000L });

        var token = await Credential().GetTokenAsync(new TokenRequestContext(["scope"]), default);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000L), token.ExpiresOn);
    }

    /// <summary>The Azure SDK calls the synchronous overload on some paths; it must work too.</summary>
    [Fact]
    public void The_synchronous_overload_fetches_the_same_token()
    {
        var jwt = MockIdpServer.Jwt(new { exp = 1893456000L });
        _idp.Respond = _ => jwt;

        var token = Credential().GetToken(new TokenRequestContext(["scope"]), default);

        Assert.Equal(jwt, token.Token);
    }

    /// <summary>
    /// The mock IdP echoes a shell-friendly trailing newline; leaving it on would produce an
    /// Authorization header the proxy rejects for a reason nothing in the logs would explain.
    /// </summary>
    [Fact]
    public async Task Surrounding_whitespace_is_trimmed_off_the_token()
    {
        var jwt = MockIdpServer.Jwt(new { exp = 4102444800L });
        _idp.Respond = _ => $"  {jwt}\n";

        var token = await Credential().GetTokenAsync(new TokenRequestContext(["scope"]), default);

        Assert.Equal(jwt, token.Token);
    }

    [Fact]
    public async Task The_client_id_is_appended_as_appid()
    {
        await Credential().GetTokenAsync(new TokenRequestContext(["scope"]), default);

        Assert.Equal($"?appid={AppId}", _idp.LastQuery);
    }

    /// <summary>An endpoint that already carries a query keeps it — appid joins with &amp;, not ?.</summary>
    [Fact]
    public async Task An_endpoint_with_an_existing_query_gets_appid_appended()
    {
        var credential = Credential($"{_idp.TokenEndpoint}?tenant=contoso");

        await credential.GetTokenAsync(new TokenRequestContext(["scope"]), default);

        Assert.Equal($"?tenant=contoso&appid={AppId}", _idp.LastQuery);
    }

    [Fact]
    public async Task A_client_id_needing_escaping_is_escaped()
    {
        var credential = Credential(_idp.TokenEndpoint, "a b&c");

        await credential.GetTokenAsync(new TokenRequestContext(["scope"]), default);

        Assert.Equal("?appid=a%20b%26c", _idp.LastQuery);
    }

    // ---- failures -----------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_idp_surfaces_as_an_http_error()
    {
        _idp.Status = 500;

        await Assert.ThrowsAsync<HttpRequestException>(
            async () => await Credential().GetTokenAsync(new TokenRequestContext(["scope"]), default));
    }

    [Fact]
    public async Task An_empty_response_is_rejected_rather_than_returned_as_a_token()
    {
        _idp.Respond = _ => "   ";

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Credential().GetTokenAsync(new TokenRequestContext(["scope"]), default));
    }

    [Fact]
    public async Task A_response_that_is_not_a_jwt_is_rejected()
    {
        _idp.Respond = _ => "not-a-jwt";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Credential().GetTokenAsync(new TokenRequestContext(["scope"]), default));

        Assert.Contains("not a valid JWT", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without exp the SDK would have no idea when to refresh. Defaulting to "never expires" would
    /// strand a long-running local session on a dead token, so this fails at fetch time instead.
    /// </summary>
    [Fact]
    public async Task A_payload_without_an_exp_claim_is_rejected()
    {
        _idp.Respond = _ => MockIdpServer.Jwt(new { sub = AppId });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Credential().GetTokenAsync(new TokenRequestContext(["scope"]), default));

        Assert.Contains("does not contain exp", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Some issuers serialise exp as a string; it is still a unix timestamp.</summary>
    [Fact]
    public async Task An_exp_claim_serialised_as_a_string_is_still_read()
    {
        _idp.Respond = _ => MockIdpServer.Jwt(new { exp = "1893456000" });

        var token = await Credential().GetTokenAsync(new TokenRequestContext(["scope"]), default);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000L), token.ExpiresOn);
    }

    [Theory]
    [InlineData("\"not-a-number\"")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task An_exp_claim_that_is_not_a_timestamp_is_rejected(string exp)
    {
        _idp.Respond = _ => $"{MockIdpServer.Base64Url("{\"alg\":\"RS256\"}")}"
            + $".{MockIdpServer.Base64Url($"{{\"exp\":{exp}}}")}.signature";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Credential().GetTokenAsync(new TokenRequestContext(["scope"]), default));

        Assert.Contains("exp claim is invalid", error.Message, StringComparison.Ordinal);
    }

    // ---- base64url decoding -------------------------------------------------------------------

    /// <summary>
    /// Payloads are base64url, not base64: the padding is stripped and two characters differ. A
    /// decoder that forgot either would work for most tokens and fail for roughly one in three.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_payload_is_decoded_at_every_padding_length(int filler)
    {
        // The filler shifts the payload's byte length modulo 3, which is what decides how many '='
        // characters were stripped — the case the decoder has to add back.
        _idp.Respond = _ => MockIdpServer.Jwt(new { exp = 1893456000L, pad = new string('x', filler) });

        var token = await Credential().GetTokenAsync(new TokenRequestContext(["scope"]), default);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000L), token.ExpiresOn);
    }

    [Fact]
    public async Task A_payload_using_the_url_safe_alphabet_is_decoded()
    {
        // Bytes chosen so standard base64 emits '+' and '/', which base64url writes as '-' and '_'.
        var payload = Encoding.UTF8.GetString([0xC3, 0xBF, 0xC3, 0xBE]);
        var encoded = MockIdpServer.Base64Url($"{{\"exp\":1893456000,\"pad\":\"{payload}\"}}");
        Assert.True(encoded.Contains('-') || encoded.Contains('_'), "payload must exercise the URL-safe alphabet");

        _idp.Respond = _ => $"{MockIdpServer.Base64Url("{\"alg\":\"RS256\"}")}.{encoded}.signature";

        var token = await Credential().GetTokenAsync(new TokenRequestContext(["scope"]), default);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000L), token.ExpiresOn);
    }

    /// <summary>
    /// A base64 segment can never be one character past a multiple of four, so this is a truncated
    /// or corrupted token rather than an encoding variant — worth its own message.
    /// </summary>
    [Fact]
    public async Task A_payload_of_an_impossible_length_is_rejected()
    {
        _idp.Respond = _ => $"{MockIdpServer.Base64Url("{\"alg\":\"RS256\"}")}.eyJleHAiOjF9Z.signature";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Credential().GetTokenAsync(new TokenRequestContext(["scope"]), default));

        Assert.Contains("not valid Base64Url", error.Message, StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private TokenCredential Credential(string? endpoint = null, string appId = AppId) =>
        EgressProxyLocalTokenCredential.CreateFromConfiguration(Configuration(
            ("EgressProxy:TokenEndpoint", endpoint ?? _idp.TokenEndpoint),
            ("EgressProxy:ClientId", appId)))!;

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
