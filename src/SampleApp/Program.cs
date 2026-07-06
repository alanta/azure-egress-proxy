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

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/try/allowed", (IHttpClientFactory factory, IConfiguration config, CancellationToken cancellationToken) =>
    TryHostAsync(factory, config["Demo:AllowedHost"] ?? "api.github.com", cancellationToken));

app.MapGet("/try/denied", (IHttpClientFactory factory, IConfiguration config, CancellationToken cancellationToken) =>
    TryHostAsync(factory, config["Demo:DeniedHost"] ?? "example.org", cancellationToken));

// Load-generation helper: fan out `n` concurrent proxied GETs to the allowed host in a
// single inbound request, so a remote caller can drive many proxy tunnels server-side
// (inside Azure, latency-free) rather than one-per-HTTP-round-trip. Not part of the demo
// surface; used to stress the proxy's per-connection CPU.
app.MapGet("/try/burst", async (IHttpClientFactory factory, IConfiguration config, int? n, CancellationToken cancellationToken) =>
{
    var host = config["Demo:AllowedHost"] ?? "api.github.com";
    var count = Math.Clamp(n ?? 50, 1, 500);
    var started = Environment.TickCount64;
    var ok = 0;
    var failed = 0;
    await Task.WhenAll(Enumerable.Range(0, count).Select(async _ =>
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("azure-egress-proxy-sample/1.0");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.GetAsync($"https://{host}", timeout.Token);
            Interlocked.Increment(ref ok);
        }
        catch
        {
            Interlocked.Increment(ref failed);
        }
    }));
    return Results.Json(new { requested = count, ok, failed, elapsedMs = Environment.TickCount64 - started });
});

app.MapDefaultEndpoints();
app.Run();

static async Task<IResult> TryHostAsync(
    IHttpClientFactory factory,
    string host,
    CancellationToken cancellationToken)
{
    var client = factory.CreateClient();
    // GitHub's API returns 403 to requests without a User-Agent; send one so an allowed
    // call yields a real 200 rather than a header-policy rejection.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("azure-egress-proxy-sample/1.0");

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
        }, statusCode: (int)response.StatusCode);
    }
    catch (Exception ex)
    {
        // The egress call didn't complete — most often the proxy denied the CONNECT
        // (blocked host), but also timeouts/DNS. Surface it as a real gateway error so
        // callers (and CI smoke tests) can distinguish a blocked request by HTTP status,
        // not just by parsing the body.
        return Results.Json(new
        {
            host,
            success = false,
            error = ex.GetBaseException().Message
        }, statusCode: StatusCodes.Status502BadGateway);
    }
}
