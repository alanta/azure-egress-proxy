using System.Globalization;
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
app.MapGet("/rulesets", async (HttpResponse response, RulesetService service, CancellationToken cancellationToken) =>
{
    var snapshot = await service.ReadSnapshotAsync(cancellationToken);
    StampRecency(response, snapshot);
    return Results.Ok(new { rulesets = snapshot.State.Rulesets.OrderBy(r => r.Name, StringComparer.Ordinal) });
}).RequireAuthorization();

app.MapGet("/rulesets/{name}", async (
    string name,
    HttpResponse response,
    RulesetService service,
    CancellationToken cancellationToken) =>
{
    var snapshot = await service.ReadSnapshotAsync(cancellationToken);
    if (snapshot.State.Find(name) is not { } ruleset)
    {
        return NotFound(name);
    }

    StampRecency(response, snapshot);
    return Results.Ok(ruleset);
}).RequireAuthorization();

// Who may change policy. Auth-only and verb-free like the other reads — knowing who holds
// authority is part of the transparent posture. The API reads grants here; it still never
// writes them, and there is deliberately no verb-bearing counterpart to this route.
app.MapGet("/grants", async (HttpResponse response, RulesetService service, CancellationToken cancellationToken) =>
{
    var snapshot = await service.ReadSnapshotAsync(cancellationToken);
    StampRecency(response, snapshot);
    return Results.Ok(new { grants = snapshot.State.Grants });
}).RequireAuthorization();

// What sources matching no ruleset may reach. Reported with an explicit deny_all rather than
// left to the reader to infer from an empty list: absent and empty both mean deny-all, and a
// view of the configuration that omits the floor is a misleading one.
app.MapGet("/fallback", async (HttpResponse response, RulesetService service, CancellationToken cancellationToken) =>
{
    var snapshot = await service.ReadSnapshotAsync(cancellationToken);
    var hosts = snapshot.State.Fallback?.AllowedHosts ?? [];

    StampRecency(response, snapshot);
    return Results.Ok(new { allowedHosts = hosts, denyAll = hosts.Count == 0 });
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

// A write reports both halves of what it changed: hosts (added/removed) and membership
// (bound/unbound). The membership half matters for the same reason the host diff does — a push is
// a full replace, so a pipeline needs to see what a bind took away as well as what it granted, and
// `:check` returns exactly this shape without writing.
static object Body(WriteOutcome outcome) => new
{
    ruleset = outcome.Ruleset,
    created = outcome.Created,
    added = outcome.Diff?.Added ?? [],
    removed = outcome.Diff?.Removed ?? [],
    bound = outcome.SubjectDiff?.Added ?? [],
    unbound = outcome.SubjectDiff?.Removed ?? [],
};

/// <summary>
/// Stamps a read with the recency of the state document it was served from, so a caller can report
/// when the configuration last changed without reading the blob itself. It is the whole document's
/// date: any ruleset write moves it for every read. Both values are absent before the state blob
/// exists. The API implements no conditional requests — these describe the read, they do not gate it.
/// </summary>
static void StampRecency(HttpResponse response, StateSnapshot snapshot)
{
    if (snapshot.LastModified is { } modified)
    {
        response.Headers.LastModified = modified.ToString("R", CultureInfo.InvariantCulture);
    }

    if (snapshot.ETag is { } etag)
    {
        // Storage hands back an already-quoted value; the tests' fake does not. An unquoted
        // ETag header is malformed, so quote what is not already an entity tag.
        var value = etag.ToString();
        response.Headers.ETag = value.StartsWith('"') || value.StartsWith("W/", StringComparison.Ordinal)
            ? value
            : $"\"{value}\"";
    }
}

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
