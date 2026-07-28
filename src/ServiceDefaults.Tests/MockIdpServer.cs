using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ServiceDefaults.Tests;

/// <summary>
/// A stand-in for the AppHost's mock IdP: one <c>GET /token?appid=...</c> route whose response each
/// test sets. The credential builds its own <see cref="HttpClient"/>, so the only way to exercise it
/// is to give it something real to call — which also means the query string it composes is checked
/// as the server received it rather than as the test assumed.
/// </summary>
public sealed class MockIdpServer : IAsyncLifetime
{
    private WebApplication _app = null!;

    /// <summary>Set per test. Returns the body; the status stays 200 unless <see cref="Status"/> changes.</summary>
    public Func<HttpContext, string> Respond { get; set; } = _ => string.Empty;

    public int Status { get; set; } = StatusCodes.Status200OK;

    /// <summary>The query string of the last request, so tests can assert how appid was appended.</summary>
    public string? LastQuery { get; private set; }

    public string TokenEndpoint { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _app = builder.Build();
        _app.MapGet("/token", async context =>
        {
            LastQuery = context.Request.QueryString.Value;
            var body = Respond(context);
            context.Response.StatusCode = Status;
            await context.Response.WriteAsync(body);
        });

        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        TokenEndpoint = $"{address}/token";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>Builds a JWT-shaped string. Only the payload matters — nothing verifies the signature.</summary>
    public static string Jwt(object payload, string signature = "signature") =>
        $"{Base64Url("{\"alg\":\"RS256\"}")}.{Base64Url(JsonSerializer.Serialize(payload))}.{signature}";

    public static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
