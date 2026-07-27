using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ControlPlane.Model;
using ControlPlane.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ControlPlane.Tests;

/// <summary>
/// Hosts the API against a real JWKS endpoint and an in-memory store, so the authN tests exercise
/// the genuine RS256/JWKS/iss/aud/exp path the proxy also uses rather than a stubbed handler.
/// </summary>
public sealed class ControlPlaneFixture : IAsyncLifetime
{
    private const string Issuer = "https://mock-idp.test/";
    private const string Audience = "egress-proxy";
    private const string KeyId = "test-key-1";

    private readonly RSA _rsa = RSA.Create(2048);
    private WebApplication _idp = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public InMemoryStateBlobStore Store { get; } = new();

    public async Task InitializeAsync()
    {
        _idp = await StartJwksServerAsync();

        // Program reads these during builder configuration, before any test hook could run, so
        // they have to be in the environment.
        Environment.SetEnvironmentVariable("JWKS_URL", $"{Address(_idp)}/jwks");
        Environment.SetEnvironmentVariable("EXPECT_ISS", Issuer);
        Environment.SetEnvironmentVariable("EXPECT_AUD", Audience);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStateBlobStore>();
                services.AddSingleton<IStateBlobStore>(_ => Store);
            }));
    }

    /// <summary>Re-seeds the store between tests; the host and its JWKS cache are shared.</summary>
    public HttpClient Reset(StateDocument seed)
    {
        Store.Reset(seed);
        return _factory.CreateClient();
    }

    public HttpClient Authenticated(HttpClient client, string appid)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(appid));
        return client;
    }

    public string Token(string appid, TimeSpan? lifetime = null) =>
        Token(claims: new Dictionary<string, object> { ["appid"] = appid, ["sub"] = appid }, lifetime: lifetime);

    /// <summary>
    /// Full control over the token, so the negative cases can vary one thing at a time: the
    /// identity claim, the issuer, the audience, the lifetime, or the signing key and algorithm.
    /// </summary>
    public string Token(
        IDictionary<string, object> claims,
        TimeSpan? lifetime = null,
        string? issuer = null,
        string? audience = null,
        SigningCredentials? signingCredentials = null,
        TimeSpan? issuedAgo = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? Audience,
            // nbf/iat default to "now", which would sit AFTER a deliberately past expiry and make
            // the token invalid for the wrong reason. Tests exercising expiry set this explicitly.
            NotBefore = DateTime.UtcNow.Subtract(issuedAgo ?? TimeSpan.Zero),
            IssuedAt = DateTime.UtcNow.Subtract(issuedAgo ?? TimeSpan.Zero),
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(5)),
            Claims = claims,
            SigningCredentials = signingCredentials ?? new SigningCredentials(
                new RsaSecurityKey(_rsa) { KeyId = KeyId },
                SecurityAlgorithms.RsaSha256),
        });

    /// <summary>A token signed by a key the JWKS does not publish — the forged-token case.</summary>
    public string TokenSignedByAnotherKey(string appid)
    {
        using var attacker = RSA.Create(2048);
        return Token(
            new Dictionary<string, object> { ["appid"] = appid },
            signingCredentials: new SigningCredentials(
                new RsaSecurityKey(attacker.ExportParameters(true)) { KeyId = KeyId },
                SecurityAlgorithms.RsaSha256));
    }

    /// <summary>An HMAC-signed token: the algorithm-substitution case the RS256 pin exists for.</summary>
    public string TokenSignedWithHmac(string appid) =>
        Token(
            new Dictionary<string, object> { ["appid"] = appid },
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey("a-shared-secret-long-enough-for-hmac-sha256"u8.ToArray()),
                SecurityAlgorithms.HmacSha256));

    private async Task<WebApplication> StartJwksServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.ExportParameters(false)));
        jwk.KeyId = KeyId;
        jwk.Use = "sig";
        jwk.Alg = SecurityAlgorithms.RsaSha256;

        var app = builder.Build();
        app.MapGet("/jwks", () => Results.Text(
            JsonSerializer.Serialize(new { keys = new[] { jwk } }), "application/json"));

        await app.StartAsync();
        return app;
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

    public async Task DisposeAsync()
    {
        _factory?.Dispose();

        if (_idp is not null)
        {
            await _idp.StopAsync();
            await _idp.DisposeAsync();
        }

        _rsa.Dispose();
    }
}
