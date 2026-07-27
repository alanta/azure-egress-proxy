using System.Net;
using System.Security.Claims;
using ControlPlane.Auth;
using ControlPlane.Model;
using ControlPlane.Policy;
using ControlPlane.Rulesets;
using ControlPlane.Storage;
using Polly;
using Polly.Retry;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = StateJson.Options.PropertyNamingPolicy;
    options.SerializerOptions.DefaultIgnoreCondition = StateJson.Options.DefaultIgnoreCondition;
});

// The API reference is generated from the endpoints themselves, and declares the bearer scheme
// so Scalar can send a real managed-identity token — the loop in ControlPlane.http, but clickable.
builder.Services.AddOpenApi(options => options.AddDocumentTransformer(new BearerSecuritySchemeTransformer()));

// Same env names the proxy uses, so one deployment configures both sides identically.
builder.Services.AddControlPlaneJwtAuth(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(new BlobStoreOptions
{
    ConnectionString = builder.Configuration["ALLOWLIST_BLOB_CONNECTION_STRING"],
    ServiceUri = builder.Configuration["STORAGE_SERVICE_URL"],
    Container = builder.Configuration["ALLOWLIST_CONTAINER"] ?? "egress-config",
    StateBlob = builder.Configuration["RULESETS_BLOB"] ?? "rulesets.json",
    AllowlistBlob = builder.Configuration["ALLOWLIST_BLOB"] ?? "allowlist.json",
});
builder.Services.AddSingleton<IStateBlobStore, StateBlobStore>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<RulesetService>();

// The whole read-modify-write is retried, not just the PUT: each attempt re-reads the state and
// re-applies the transform, so a collision with a write to a DIFFERENT ruleset lands on fresh
// state and succeeds silently. Only sustained contention on the same ruleset exhausts the budget.
builder.Services.AddResiliencePipeline(RulesetService.RmwPipeline, pipeline => pipeline
    .AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = new PredicateBuilder().Handle<StatePreconditionFailedException>(),
        MaxRetryAttempts = 5,
        BackoffType = DelayBackoffType.Exponential,
        Delay = TimeSpan.FromMilliseconds(50),
        UseJitter = true,
    }));

// Publishing the rendered document happens after the state write has already committed, so a
// transient failure here leaves the proxy stale. Retry it before giving up and telling the caller.
builder.Services.AddResiliencePipeline(RulesetService.PublishPipeline, pipeline => pipeline
    .AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = new PredicateBuilder().Handle<Exception>(),
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
        Delay = TimeSpan.FromMilliseconds(100),
        UseJitter = true,
    }));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Development only, for the same reason the health endpoints are (see ServiceDefaults): a
// deployed control plane should not publish its own API surface unauthenticated. Flip this if
// you want the reference available in Azure too — the endpoints themselves stay authorized.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options
        .WithTitle("Egress control plane")
        .AddPreferredSecuritySchemes(BearerSecuritySchemeTransformer.SchemeName));
}

// Reads are open to any authenticated caller, regardless of RBAC: the egress posture is
// transparent by design, so any team can see the whole picture. Only writes need a verb.
app.MapGet("/rulesets", async (RulesetService service, CancellationToken cancellationToken) =>
{
    var state = await service.ReadStateAsync(cancellationToken);
    return Results.Ok(new { rulesets = state.Rulesets.OrderBy(r => r.Name, StringComparer.Ordinal) });
}).RequireAuthorization();

app.MapGet("/rulesets/{name}", async (string name, RulesetService service, CancellationToken cancellationToken) =>
{
    var state = await service.ReadStateAsync(cancellationToken);
    return state.Find(name) is { } ruleset ? Results.Ok(ruleset) : NotFound(name);
}).RequireAuthorization();

// The core verb: onboard if absent, full replace of content if present.
app.MapPut("/rulesets/{name}", async (
    string name,
    PushRequest request,
    ClaimsPrincipal caller,
    RulesetService service,
    CancellationToken cancellationToken) =>
{
    if (caller.CallerIdentity() is not { } identity)
    {
        return MissingIdentity();
    }

    var outcome = await service.PutAsync(name, request, identity, dryRun: false, cancellationToken);
    return outcome.Succeeded
        ? Results.Json(Body(outcome), statusCode: outcome.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK)
        : Problem(outcome.Error!);
}).RequireAuthorization();

// Dry run: the same validation and the same onboard gate as PUT, and never a write. A pipeline
// gates on the diff here — the control over both widening and removal.
app.MapPost("/rulesets/{name}:check", async (
    string name,
    PushRequest request,
    ClaimsPrincipal caller,
    RulesetService service,
    CancellationToken cancellationToken) =>
{
    if (caller.CallerIdentity() is not { } identity)
    {
        return MissingIdentity();
    }

    var outcome = await service.PutAsync(name, request, identity, dryRun: true, cancellationToken);
    return outcome.Succeeded ? Results.Ok(Body(outcome)) : Problem(outcome.Error!);
}).RequireAuthorization();

// Offboard: the subjects fall to the fallback/deny block on the next reload, so decommission is
// fail-closed by construction.
app.MapDelete("/rulesets/{name}", async (
    string name,
    ClaimsPrincipal caller,
    RulesetService service,
    CancellationToken cancellationToken) =>
{
    if (caller.CallerIdentity() is not { } identity)
    {
        return MissingIdentity();
    }

    var outcome = await service.DeleteAsync(name, identity, cancellationToken);
    return outcome.Succeeded ? Results.Ok(Body(outcome)) : Problem(outcome.Error!);
}).RequireAuthorization();

app.MapDefaultEndpoints();
app.Run();

static object Body(WriteOutcome outcome) => new
{
    ruleset = outcome.Ruleset,
    created = outcome.Created,
    added = outcome.Diff?.Added ?? [],
    removed = outcome.Diff?.Removed ?? [],
};

static IResult Problem(PolicyError error) =>
    Results.Problem(detail: error.Message, statusCode: (int)error.Status);

static IResult NotFound(string name) =>
    Results.Problem(detail: $"ruleset '{name}' not found", statusCode: StatusCodes.Status404NotFound);

static IResult MissingIdentity() =>
    Results.Problem(
        detail: "the token carries no appid/azp claim, so the caller cannot be identified",
        statusCode: (int)HttpStatusCode.Unauthorized);

/// <summary>Exposed so the API tests can host the app with a stubbed store and IdP.</summary>
public partial class Program;
