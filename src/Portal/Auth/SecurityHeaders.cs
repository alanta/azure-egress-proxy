namespace Portal.Auth;

/// <summary>
/// The response headers the console is served under.
///
/// The Content-Security-Policy is the part with teeth, and its shape is a design decision rather
/// than a default (design.md D8 rule 1): <b>no <c>unsafe-eval</c> and no <c>unsafe-inline</c>
/// script</b>. The admin UI for an egress control does not need script evaluation to render
/// tables, and saying so here is what makes the corresponding rule in review enforceable —
/// <c>hx-vals='js:…'</c>, <c>hx-on:*</c>, and the eval-based htmx extensions all fail against
/// this policy rather than merely being discouraged.
///
/// <c>style-src</c> keeps <c>'unsafe-inline'</c>: the server-rendered SVG charts carry per-element
/// style attributes, which CSP counts as inline styles. That is a much smaller concession than
/// script evaluation and does not open a code-execution path.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// Self-only in every direction. The portal serves its own fonts (embedded), its own CSS, and
    /// one vendored copy of htmx, so nothing legitimate needs a remote origin — which also means
    /// this policy has no CDN to be relaxed for later.
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'self'; "
        + "script-src 'self'; "
        + "style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data:; "
        + "font-src 'self' data:; "
        + "connect-src 'self'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'; "
        + "base-uri 'none'; "
        + "object-src 'none'";

    public static IApplicationBuilder UsePortalSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            // The console is an operator tool; nothing it renders belongs in a frame elsewhere,
            // and frame-ancestors above says the same to anything that understands CSP.
            headers["X-Frame-Options"] = "DENY";

            await next();
        });
}
