using ControlPlane.Auth;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace ControlPlane.Tests;

/// <summary>
/// The generated document is how anyone first drives this API, and a document that omits the bearer
/// scheme sends every Scalar request unauthenticated — a 401 that looks like a broken API rather
/// than a missing token. These pin the parts of the document that make the token box appear.
/// </summary>
public class BearerSecuritySchemeTransformerTests
{
    [Fact]
    public async Task The_bearer_scheme_is_declared_as_an_rs256_jwt_in_the_authorization_header()
    {
        var document = await TransformAsync(new OpenApiDocument());

        var scheme = Assert.Contains(
            BearerSecuritySchemeTransformer.SchemeName,
            (IDictionary<string, IOpenApiSecurityScheme>)document.Components!.SecuritySchemes!);

        Assert.Equal(SecuritySchemeType.Http, scheme.Type);
        Assert.Equal("bearer", scheme.Scheme);
        Assert.Equal("JWT", scheme.BearerFormat);
        Assert.Equal(ParameterLocation.Header, scheme.In);
    }

    /// <summary>
    /// Applied document-wide rather than per-endpoint: every route here requires a valid token, so
    /// an endpoint added later inherits the requirement instead of silently shipping without it.
    /// </summary>
    [Fact]
    public async Task Every_endpoint_requires_the_scheme_because_it_is_applied_document_wide()
    {
        var document = await TransformAsync(new OpenApiDocument());

        var requirement = Assert.Single(document.Security!);
        var reference = Assert.Single(requirement.Keys);
        Assert.Equal(BearerSecuritySchemeTransformer.SchemeName, reference.Reference!.Id);
    }

    [Fact]
    public async Task The_document_is_titled_and_points_at_the_written_docs()
    {
        var document = await TransformAsync(new OpenApiDocument());

        Assert.Equal("Egress control plane", document.Info.Title);
        Assert.Contains("docs/control-plane.md", document.Info.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The description is the only place a local caller is told how to mint a token, so it has to
    /// name the mock IdP route the AppHost actually exposes.
    /// </summary>
    [Fact]
    public async Task The_scheme_description_explains_how_to_obtain_a_token_locally()
    {
        var document = await TransformAsync(new OpenApiDocument());

        var scheme = document.Components!.SecuritySchemes![BearerSecuritySchemeTransformer.SchemeName];

        Assert.Contains("/token?appid=", scheme.Description, StringComparison.Ordinal);
        Assert.Contains("RS256", scheme.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A document that already carries components or requirements — anything another transformer or
    /// the generator itself contributed — must be added to, not replaced.
    /// </summary>
    [Fact]
    public async Task An_existing_scheme_from_another_transformer_survives()
    {
        var document = new OpenApiDocument
        {
            Components = new OpenApiComponents
            {
                SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["ApiKey"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey },
                },
            },
        };

        var transformed = await TransformAsync(document);

        Assert.Contains("ApiKey", (IDictionary<string, IOpenApiSecurityScheme>)transformed.Components!.SecuritySchemes!);
        Assert.Contains(
            BearerSecuritySchemeTransformer.SchemeName,
            (IDictionary<string, IOpenApiSecurityScheme>)transformed.Components.SecuritySchemes!);
    }

    private static async Task<OpenApiDocument> TransformAsync(OpenApiDocument document)
    {
        await new BearerSecuritySchemeTransformer().TransformAsync(document, Context(), default);
        return document;
    }

    private static OpenApiDocumentTransformerContext Context() => new()
    {
        DocumentName = "v1",
        DescriptionGroups = [],
        ApplicationServices = new ServiceCollection().BuildServiceProvider(),
    };
}
