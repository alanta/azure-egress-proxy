using EgressProxy.Client;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddHttpClient();
var localEgressTokenCredential = EgressProxyLocalTokenCredential.CreateFromConfiguration(builder.Configuration);
builder.Services.AddEgressProxy(options =>
{
    options.Audience = builder.Configuration["EgressProxy:Audience"];
    options.ClientId = builder.Configuration["EgressProxy:ClientId"];
    options.TokenCredential = localEgressTokenCredential;
});

var app = builder.Build();
var expectedFrontDoorId = Environment.GetEnvironmentVariable("FRONTDOOR_ID");

if (!string.IsNullOrWhiteSpace(expectedFrontDoorId))
{
    app.Use(async (context, next) =>
    {
        // Health probes (ACA) reach the container directly, not via Front Door.
        if (context.Request.Path == "/healthz")
        {
            await next();
            return;
        }

        var providedFrontDoorId = context.Request.Headers["X-Azure-FDID"].ToString();
        if (!string.Equals(providedFrontDoorId, expectedFrontDoorId, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Forbidden" });
            return;
        }

        await next();
    });
}

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/try/allowed", (IHttpClientFactory factory, IConfiguration config, CancellationToken cancellationToken) =>
    TryHostAsync(factory, config["Demo:AllowedHost"] ?? "api.github.com", cancellationToken));

app.MapGet("/try/denied", (IHttpClientFactory factory, IConfiguration config, CancellationToken cancellationToken) =>
    TryHostAsync(factory, config["Demo:DeniedHost"] ?? "example.org", cancellationToken));

app.MapDefaultEndpoints();
app.Run();

static async Task<IResult> TryHostAsync(
    IHttpClientFactory factory,
    string host,
    CancellationToken cancellationToken)
{
    var client = factory.CreateClient();

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(10));

    try
    {
        var response = await client.GetAsync($"https://{host}", timeout.Token);
        return Results.Json(new
        {
            host,
            success = true,
            status = (int)response.StatusCode
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            host,
            success = false,
            error = ex.GetBaseException().Message
        });
    }
}
