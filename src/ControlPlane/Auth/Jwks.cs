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
public sealed class JwksConfigurationRetriever(ILogger<JwksConfigurationRetriever> logger)
    : IConfigurationRetriever<OpenIdConnectConfiguration>
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

        // Every refresh passes through here, so a stale-key incident is diagnosable from the log:
        // the last successful fetch and the key ids then in play are both recorded.
        logger.LogInformation(
            "loaded {KeyCount} signing key(s) from {JwksUrl}: {KeyIds}",
            configuration.SigningKeys.Count,
            address,
            string.Join(",", configuration.SigningKeys.Select(k => k.KeyId ?? "(no kid)")));

        return configuration;
    }
}

public static class JwtAuthentication
{
    /// <summary>
    /// Mirrors the proxy's validation (proxy/main.go): RS256 against the JWKS, plus issuer,
    /// audience and a required expiry with a small clock-skew leeway.
    ///
    /// Outside Development this fails closed at startup rather than at request time: an unset
    /// issuer or audience would otherwise silently disable that check and accept tokens minted for
    /// any tenant or any resource, which is worse than not booting.
    /// </summary>
    public static IServiceCollection AddControlPlaneJwtAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var isDevelopment = environment.IsDevelopment();
        var jwksUrl = Required(configuration, "JWKS_URL");
        var issuer = configuration["EXPECT_ISS"];
        var audience = configuration["EXPECT_AUD"];

        if (!Uri.TryCreate(jwksUrl, UriKind.Absolute, out var jwksUri)
            || (jwksUri.Scheme != Uri.UriSchemeHttps && jwksUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"JWKS_URL must be an absolute http(s) URL; got '{jwksUrl}'.");
        }

        if (!isDevelopment)
        {
            if (jwksUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException(
                    $"JWKS_URL must use https outside Development; got '{jwksUrl}'. Signing keys "
                    + "fetched over plaintext can be substituted in transit.");
            }

            if (string.IsNullOrWhiteSpace(issuer))
            {
                throw new InvalidOperationException(
                    "EXPECT_ISS is required outside Development: without it any issuer's tokens "
                    + "would be accepted. Set it to the same value the proxy uses.");
            }

            if (string.IsNullOrWhiteSpace(audience))
            {
                throw new InvalidOperationException(
                    "EXPECT_AUD is required outside Development: without it a token minted for a "
                    + "different resource would be accepted. Set it to the same value the proxy uses.");
            }
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<ILoggerFactory>((options, loggerFactory) =>
            {
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    jwksUrl,
                    new JwksConfigurationRetriever(loggerFactory.CreateLogger<JwksConfigurationRetriever>()),
                    // Plaintext is tolerated only for the local mock IdP; the startup checks above
                    // have already refused an http URL outside Development.
                    new HttpDocumentRetriever { RequireHttps = !isDevelopment });

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

                options.Events = new JwtBearerEvents
                {
                    // A 401 on its own says nothing an operator can act on. Recording WHY the token
                    // was refused is what separates "the signer rotated" from "someone is probing".
                    OnAuthenticationFailed = context =>
                    {
                        loggerFactory.CreateLogger<JwtBearerEvents>().LogWarning(
                            "auth: token rejected ({Reason}) for {Method} {Path} from {RemoteIp}: {Message}",
                            ReasonCode(context.Exception),
                            context.Request.Method,
                            context.Request.Path,
                            context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "(unknown)",
                            context.Exception.Message);

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();
        return services;
    }

    /// <summary>Stable, greppable reason codes — the thing you alert and dashboard on.</summary>
    private static string ReasonCode(Exception exception) => exception switch
    {
        // Ordered most-derived first: several of these inherit from
        // SecurityTokenInvalidSignatureException, so a looser arm above would swallow them.
        SecurityTokenMalformedException => "malformed",
        SecurityTokenSignatureKeyNotFoundException => "unknown_signing_key",
        SecurityTokenInvalidSignatureException => "invalid_signature",
        SecurityTokenExpiredException => "expired",
        SecurityTokenNotYetValidException => "not_yet_valid",
        SecurityTokenNoExpirationException => "missing_expiry",
        SecurityTokenInvalidIssuerException => "issuer_mismatch",
        SecurityTokenInvalidAudienceException => "audience_mismatch",
        SecurityTokenInvalidAlgorithmException => "unsupported_algorithm",
        _ => "invalid",
    };

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{key} is required to validate caller tokens.");

    /// <summary>
    /// The caller's own identity. Claim precedence is <c>appid</c> then <c>azp</c>: <c>appid</c> is
    /// what Entra v1 and managed-identity tokens carry, <c>azp</c> is its v2 equivalent, and it is
    /// the same claim the proxy keys its ACL on — which is what lets writer≠subject be a plain
    /// string comparison across both planes. A token carrying neither cannot be authorized at all.
    /// </summary>
    public static string? CallerIdentity(this ClaimsPrincipal principal) =>
        principal.FindFirst("appid")?.Value
        ?? principal.FindFirst("azp")?.Value;
}
