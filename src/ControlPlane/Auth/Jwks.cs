using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ControlPlane.Auth;

/// <summary>
/// Loads signing keys from a bare JWKS endpoint — the same <c>JWKS_URL</c> the proxy already
/// validates workload tokens against, so the data plane and the control plane share one identity
/// model. (Entra publishes OIDC discovery, but the local mock IdP serves only the key set, and the
/// proxy targets the key set too.) Wrapped in a ConfigurationManager so key rotation and caching
/// are handled for us.
/// </summary>
public sealed class JwksConfigurationRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address,
        IDocumentRetriever retriever,
        CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel);
        var configuration = new OpenIdConnectConfiguration { JwksUri = address };

        foreach (var key in new JsonWebKeySet(json).GetSigningKeys())
        {
            configuration.SigningKeys.Add(key);
        }

        return configuration;
    }
}

public static class JwtAuthentication
{
    /// <summary>
    /// Mirrors the proxy's validation (proxy/main.go): RS256 against the JWKS, plus issuer,
    /// audience and a required expiry with a small clock-skew leeway.
    /// </summary>
    public static IServiceCollection AddControlPlaneJwtAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var jwksUrl = configuration["JWKS_URL"]
            ?? throw new InvalidOperationException("JWKS_URL is required to validate caller tokens");
        var issuer = configuration["EXPECT_ISS"];
        var audience = configuration["EXPECT_AUD"];

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    jwksUrl,
                    new JwksConfigurationRetriever(),
                    // The mock IdP and in-cluster metadata endpoints are plain HTTP; in Azure the
                    // JWKS URL is https and this relaxation never comes into play.
                    new HttpDocumentRetriever { RequireHttps = jwksUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) });

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidateIssuer = !string.IsNullOrEmpty(issuer),
                    ValidIssuer = issuer,
                    ValidateAudience = !string.IsNullOrEmpty(audience),
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(60),
                };
            });

        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// The caller's own identity: <c>appid</c> on v1 / managed-identity tokens, <c>azp</c> on v2 —
    /// the same claim the proxy keys the ACL on, which is what lets writer≠subject be a simple
    /// string comparison.
    /// </summary>
    public static string? CallerIdentity(this ClaimsPrincipal principal) =>
        principal.FindFirst("appid")?.Value
        ?? principal.FindFirst("azp")?.Value;
}
