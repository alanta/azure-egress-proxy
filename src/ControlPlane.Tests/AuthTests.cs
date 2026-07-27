using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ControlPlane.Model;
using ControlPlane.Policy;

namespace ControlPlane.Tests;

/// <summary>
/// Token validation, exercised against a real JWKS endpoint rather than a stubbed handler, so
/// these assert the same parity the data plane relies on: the control plane must accept exactly
/// the tokens the proxy would accept, and refuse the rest.
/// </summary>
public class AuthTests(ControlPlaneFixture fixture) : IClassFixture<ControlPlaneFixture>
{
    private const string Pipeline = "22222222-2222-2222-2222-222222222222";

    // ---- accepted identity claims -----------------------------------------------------------

    /// <summary>appid is what Entra v1 and managed-identity tokens carry.</summary>
    [Fact]
    public async Task An_appid_claim_identifies_the_caller()
    {
        var response = await Send(new Dictionary<string, object> { ["appid"] = Pipeline });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>azp is the v2 equivalent, and is accepted as the fallback.</summary>
    [Fact]
    public async Task An_azp_claim_identifies_the_caller()
    {
        var response = await Send(new Dictionary<string, object> { ["azp"] = Pipeline });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>appid wins when both are present, matching the documented precedence.</summary>
    [Fact]
    public async Task Appid_takes_precedence_over_azp()
    {
        // Only the appid holds the grant, so acceptance proves which claim was used.
        var response = await Send(new Dictionary<string, object>
        {
            ["appid"] = Pipeline,
            ["azp"] = "44444444-4444-4444-4444-444444444444",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// A structurally valid token carrying neither claim cannot be attributed to anyone, so it
    /// cannot be authorized — it must not fall through to an unidentified write.
    /// </summary>
    [Fact]
    public async Task A_token_with_no_identity_claim_is_rejected()
    {
        var response = await Send(new Dictionary<string, object> { ["sub"] = Pipeline });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Store.Writes);
    }

    // ---- rejected tokens --------------------------------------------------------------------

    [Fact]
    public async Task A_token_from_the_wrong_issuer_is_rejected()
    {
        var token = fixture.Token(
            new Dictionary<string, object> { ["appid"] = Pipeline },
            issuer: "https://evil.example/");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(token)).StatusCode);
    }

    /// <summary>A token minted for another resource must not be replayable here.</summary>
    [Fact]
    public async Task A_token_for_the_wrong_audience_is_rejected()
    {
        var token = fixture.Token(
            new Dictionary<string, object> { ["appid"] = Pipeline },
            audience: "some-other-api");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(token)).StatusCode);
    }

    /// <summary>The forged-token case: correctly shaped, but signed by a key the JWKS never
    /// published.</summary>
    [Fact]
    public async Task A_token_signed_by_an_unknown_key_is_rejected()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await Get(fixture.TokenSignedByAnotherKey(Pipeline))).StatusCode);
    }

    /// <summary>
    /// Algorithm substitution: an HMAC-signed token must not be accepted just because it verifies
    /// under some key. This is what pinning ValidAlgorithms to RS256 buys.
    /// </summary>
    [Fact]
    public async Task A_token_signed_with_an_unsupported_algorithm_is_rejected()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await Get(fixture.TokenSignedWithHmac(Pipeline))).StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var token = fixture.Token(
            new Dictionary<string, object> { ["appid"] = Pipeline },
            lifetime: TimeSpan.FromMinutes(-10),
            issuedAgo: TimeSpan.FromMinutes(20));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(token)).StatusCode);
    }

    /// <summary>Clock skew is tolerated, so a just-expired token from a slightly fast signer still
    /// works — the proxy allows the same 60s leeway and the two must not disagree.</summary>
    [Fact]
    public async Task A_token_within_the_clock_skew_allowance_is_accepted()
    {
        var token = fixture.Token(
            new Dictionary<string, object> { ["appid"] = Pipeline },
            lifetime: TimeSpan.FromSeconds(-10),
            issuedAgo: TimeSpan.FromMinutes(5));

        Assert.Equal(HttpStatusCode.OK, (await Get(token)).StatusCode);
    }

    [Fact]
    public async Task A_malformed_token_is_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("not.a.jwt")).StatusCode);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private async Task<HttpResponseMessage> Get(string token)
    {
        var client = fixture.Reset(Seed());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetAsync("/rulesets");
    }

    /// <summary>Onboards with the given claims: a write, so it proves the caller was both
    /// authenticated and attributed to an identity holding a grant.</summary>
    private async Task<HttpResponseMessage> Send(IDictionary<string, object> claims)
    {
        var client = fixture.Reset(Seed());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.Token(claims));

        return await client.PutAsJsonAsync("/rulesets/payments", new
        {
            subjects = new[] { new { appid = "33333333-3333-3333-3333-333333333333" } },
            content = new { allowed_hosts = new[] { "api.stripe.com" } },
        });
    }

    private static StateDocument Seed() => new()
    {
        Grants = [new Grant { Identity = Pipeline, Verbs = [Verbs.Onboard, Verbs.Update, Verbs.Offboard] }],
    };
}
