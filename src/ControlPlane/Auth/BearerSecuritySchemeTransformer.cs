using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ControlPlane.Auth;

/// <summary>
/// Declares the bearer scheme on the generated document, so the API reference offers a token box
/// and sends `Authorization: Bearer ...` — without it every request from Scalar would just 401.
/// Locally the token comes from the mock IdP (`GET /token?appid=...`); in Azure it is the caller's
/// own managed-identity token, exactly as for the proxy.
/// </summary>
public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public const string SchemeName = "Bearer";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info.Title = "Egress control plane";
        document.Info.Description =
            "The validating write path in front of the egress allowlist. Reads are open to any "
            + "authenticated caller; writes need a platform-granted verb (onboard/update/offboard) "
            + "or trust-on-first-use ownership. See docs/control-plane.md.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description =
                "Managed-identity token, validated exactly as the proxy validates workload tokens "
                + "(RS256 against the JWKS, plus iss/aud/exp). Locally: "
                + "curl \"http://localhost:18080/token?appid=22222222-2222-2222-2222-222222222222\"",
        };

        // Applied document-wide: every endpoint here requires a valid token. Reads need no RBAC
        // verb beyond that, writes do — see docs/control-plane.md.
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeName, document)] = [],
        });

        return Task.CompletedTask;
    }
}
