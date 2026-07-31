using Portal.Auth;

namespace Portal.Tests;

/// <summary>
/// The four rules design.md D8 settles up front, asserted rather than remembered. Each of them is
/// the kind of thing that decays silently: a CSP relaxed to make one widget work, an htmx picked
/// up from a CDN because it was quicker, a login page rendered into a card.
/// </summary>
public class ScaffoldTests
{
    // ---- rule 1: no unsafe-eval ---------------------------------------------------------------

    /// <summary>
    /// The admin UI for an egress control should not need script evaluation to render tables.
    /// This is what makes "avoid hx-vals='js:…', hx-on:*, and the eval-based extensions" an
    /// enforceable rule rather than a review preference — all of them fail against this policy.
    /// </summary>
    [Theory]
    [InlineData("unsafe-eval")]
    [InlineData("unsafe-inline'; script-src")]
    public void The_policy_permits_no_script_evaluation(string forbidden) =>
        Assert.DoesNotContain(forbidden, SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);

    [Fact]
    public void Script_sources_are_self_only() =>
        Assert.Contains("script-src 'self';", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);

    /// <summary>Nothing the console renders belongs in someone else's frame.</summary>
    [Fact]
    public void The_console_cannot_be_framed() =>
        Assert.Contains("frame-ancestors 'none'", SecurityHeaders.ContentSecurityPolicy, StringComparison.Ordinal);

    /// <summary>
    /// The eval-based htmx affordances, scanned for directly. The CSP would block them at runtime,
    /// but a blocked attribute in a template is a panel that silently renders nothing — better to
    /// fail the build than to ship a surface that only breaks in the browser.
    /// </summary>
    [Theory]
    [InlineData("hx-on:")]
    [InlineData("hx-vals='js:")]
    [InlineData("hx-vals=\"js:")]
    public void No_template_uses_an_eval_based_affordance(string forbidden)
    {
        var offenders = Repo.Files("src/Portal", "*.cshtml")
            .Where(file => !file.Contains("/obj/", StringComparison.Ordinal))
            .Where(file => Repo.ReadText(file).Contains(forbidden, StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"'{forbidden}' needs unsafe-eval, which the portal's CSP does not grant. Found in: "
            + string.Join(", ", offenders));
    }

    // ---- rule 4: htmx is vendored and pinned --------------------------------------------------

    /// <summary>
    /// One copy, checked in, referenced by its versioned filename. The version in the name is why
    /// an upgrade cannot be served from a stale cache — and why this test catches a layout that
    /// still points at the file the upgrade deleted.
    /// </summary>
    [Fact]
    public void The_layout_references_exactly_the_vendored_htmx()
    {
        var vendored = Repo.Files("src/Portal/wwwroot/lib/htmx", "*.js").ToList();
        var file = Assert.Single(vendored);
        var name = Path.GetFileName(file);

        Assert.Contains($"/lib/htmx/{name}", Repo.ReadText("src/Portal/Pages/Shared/_Layout.cshtml"),
            StringComparison.Ordinal);
    }

    /// <summary>No CDN in the trust path of a security reference implementation.</summary>
    [Theory]
    [InlineData("unpkg.com")]
    [InlineData("cdn.jsdelivr.net")]
    [InlineData("cdnjs.cloudflare.com")]
    public void No_template_loads_a_script_from_a_cdn(string host)
    {
        var offenders = Repo.Files("src/Portal", "*.cshtml")
            .Where(file => !file.Contains("/obj/", StringComparison.Ordinal))
            .Where(file => Repo.ReadText(file).Contains(host, StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0, $"{host} referenced from: {string.Join(", ", offenders)}");
    }

    // ---- rule 2: session expiry must not render a login page into a div -----------------------

    /// <summary>
    /// htmx navigates the whole window on HX-Redirect. A 302 would instead be followed by the
    /// fetch and swapped into whatever element made the request, painting a login page into a
    /// card on the dashboard — which reads as a broken console rather than an ended session.
    /// </summary>
    [Fact]
    public async Task An_htmx_request_without_a_session_is_told_to_navigate()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/";
        context.Request.Headers[SessionMiddleware.RequestHeader] = "true";

        await Middleware().InvokeAsync(context, new NoSession());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(SessionMiddleware.LoginPath, context.Response.Headers[SessionMiddleware.RedirectHeader]);
    }

    /// <summary>An ordinary navigation still gets the ordinary redirect — the special case is
    /// htmx, not sign-in.</summary>
    [Fact]
    public async Task A_browser_navigation_without_a_session_is_redirected()
    {
        var context = new DefaultHttpContext { Request = { Path = "/" } };

        await Middleware().InvokeAsync(context, new NoSession());

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal(SessionMiddleware.LoginPath, context.Response.Headers.Location);
    }

    [Fact]
    public async Task A_signed_in_request_passes_through()
    {
        var reached = false;
        var context = new DefaultHttpContext { Request = { Path = "/" } };

        await new SessionMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        }).InvokeAsync(context, new DevelopmentUserAccessor());

        Assert.True(reached);
    }

    /// <summary>Health probes must not be redirected to a login page: Container Apps would read
    /// the 302 as an unhealthy replica and restart the portal in a loop.</summary>
    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    [InlineData("/lib/htmx/htmx-2.0.10.min.js")]
    public async Task Probes_and_static_assets_are_reachable_without_a_session(string path)
    {
        var reached = false;
        var context = new DefaultHttpContext { Request = { Path = path } };

        await new SessionMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        }).InvokeAsync(context, new NoSession());

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static SessionMiddleware Middleware() =>
        new(_ => throw new Xunit.Sdk.XunitException("the pipeline ran without a session"));

    private sealed class NoSession : IPortalUserAccessor
    {
        public PortalUser? Current => null;
    }
}
