namespace Portal.Auth;

/// <summary>
/// Turns "no session" into the right answer for the kind of request that asked.
///
/// A browser navigation gets the platform's login redirect, as it should. An htmx request must
/// not: htmx swaps whatever comes back into the target element, so a 302 that the fetch follows
/// would render an entire login page into a card on the dashboard — a page that then cannot post
/// its form anywhere useful, and that looks to the operator like the console broke rather than
/// like the session ended. htmx honours <c>HX-Redirect</c> on a response by navigating the whole
/// window, which is the only correct outcome.
///
/// This is design.md D8 rule 2, and it is middleware rather than a per-page check because the
/// failure mode is silent and every future surface would have to remember it.
/// </summary>
public sealed class SessionMiddleware(RequestDelegate next)
{
    /// <summary>Set by htmx on every request it issues.</summary>
    internal const string RequestHeader = "HX-Request";

    /// <summary>Tells htmx to navigate the browser rather than swap the body.</summary>
    internal const string RedirectHeader = "HX-Redirect";

    /// <summary>The auth sidecar's login route. Under Container Apps built-in auth a plain
    /// navigation here starts the OIDC flow and returns to the portal afterwards.</summary>
    internal const string LoginPath = "/.auth/login/aad";

    /// <summary>The accessor arrives per-invocation rather than through the constructor: the
    /// production implementation is scoped, and middleware is constructed once for the app.</summary>
    public async Task InvokeAsync(HttpContext context, IPortalUserAccessor user)
    {
        if (user.Current is not null || IsAnonymous(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (context.Request.Headers.ContainsKey(RequestHeader))
        {
            // 401 rather than 302: htmx does not swap non-2xx responses by default, so even a
            // client that ignored HX-Redirect would leave the stale panel in place rather than
            // paint a login form into it.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers[RedirectHeader] = LoginPath;
            return;
        }

        context.Response.Redirect(LoginPath);
    }

    /// <summary>
    /// Health probes and the static assets that a login page would itself need. Kept to prefixes
    /// with no operator data behind them — everything the console renders is authenticated.
    /// </summary>
    private static bool IsAnonymous(PathString path) =>
        path.StartsWithSegments("/health")
        || path.StartsWithSegments("/alive")
        || path.StartsWithSegments("/lib")
        || path.StartsWithSegments("/css")
        || path.StartsWithSegments("/fonts")
        || path.StartsWithSegments("/.auth");
}
