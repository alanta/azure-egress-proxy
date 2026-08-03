using Portal.Auth;
using Portal.Clients;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();

// Control plane, Log Analytics, and ARM/Azure Monitor — all read-only, all behind one cache.
builder.Services.AddConsoleData(builder.Configuration);

// The identity provider is chosen here and nowhere else — the rest of the portal sees only
// IPortalUserAccessor, and the control-plane client never sees the user at all (design.md D1/D2).
//
// An environment check rather than a configuration flag: a setting that silently disables
// authentication on an admin console is exactly the kind of thing that gets copied into a
// production parameter file, and there is no legitimate reason to want the development stub
// anywhere the auth sidecar exists.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IPortalUserAccessor, DevelopmentUserAccessor>();
}
else
{
    builder.Services.AddScoped<IPortalUserAccessor, ContainerAppsUserAccessor>();
}

var app = builder.Build();

app.UsePortalSecurityHeaders();
app.UseStaticFiles();
app.UseRouting();
app.UseMiddleware<SessionMiddleware>();

app.MapRazorPages();
app.MapDefaultEndpoints();

app.Run();

/// <summary>Exposed so the portal tests can host the app with stubbed clients.</summary>
public partial class Program;
