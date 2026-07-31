using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ControlPlane.Model;
using ControlPlane.Policy;

namespace ControlPlane.Tests;

[Collection(ControlPlaneCollection.Name)]
public class ApiTests(ControlPlaneFixture fixture)
{
    private const string Pipeline = "22222222-2222-2222-2222-222222222222";
    private const string Stranger = "44444444-4444-4444-4444-444444444444";
    private const string Workload = "11111111-1111-1111-1111-111111111111";

    // ---- authentication ---------------------------------------------------------------------

    [Fact]
    public async Task A_request_without_a_token_is_unauthorized()
    {
        var client = fixture.Reset(Seed());

        var response = await client.GetAsync("/rulesets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_garbage_token_is_unauthorized()
    {
        var client = fixture.Reset(Seed());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.GetAsync("/rulesets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_unauthorized()
    {
        var client = fixture.Reset(Seed());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.Token(Pipeline, lifetime: TimeSpan.FromMinutes(-10)));

        var response = await client.GetAsync("/rulesets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- reads are open ---------------------------------------------------------------------

    /// <summary>Transparency is a feature: any authenticated caller sees the whole egress posture,
    /// not just what it may write.</summary>
    [Fact]
    public async Task Any_authenticated_caller_lists_every_ruleset()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Stranger);

        var response = await client.GetAsync("/rulesets");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.EnsureSuccessStatusCode();
        Assert.Equal("payments", body.GetProperty("rulesets")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Reading_an_absent_ruleset_is_not_found()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed()), Stranger);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/rulesets/ghost")).StatusCode);
    }

    // ---- platform configuration is readable --------------------------------------------------

    /// <summary>Who may change policy is part of the transparent posture: a caller holding no
    /// grant at all still reads them, exactly as it reads the rulesets.</summary>
    [Fact]
    public async Task Any_authenticated_caller_reads_the_grants()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Stranger);

        var response = await client.GetAsync("/grants");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.EnsureSuccessStatusCode();
        var grant = body.GetProperty("grants")[0];
        Assert.Equal(Pipeline, grant.GetProperty("identity").GetString());
        Assert.Equal("payments", grant.GetProperty("rulesets")[0].GetString());
    }

    /// <summary>An absent fallback is the deny-all floor. The endpoint says so rather than
    /// returning an empty list and leaving the reader to infer it.</summary>
    [Fact]
    public async Task An_absent_fallback_reads_as_deny_all()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Stranger);

        var body = await (await client.GetAsync("/fallback")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("deny_all").GetBoolean());
        Assert.Empty(body.GetProperty("allowed_hosts").EnumerateArray());
    }

    /// <summary>An empty allowed_hosts list is deny-all too — the schema treats absent and empty
    /// alike, so the endpoint must not report an empty block as an open floor.</summary>
    [Fact]
    public async Task An_empty_fallback_reads_as_deny_all()
    {
        var seed = Seed(Existing()) with { Fallback = new Fallback() };
        var client = fixture.Authenticated(fixture.Reset(seed), Stranger);

        var body = await (await client.GetAsync("/fallback")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("deny_all").GetBoolean());
    }

    [Fact]
    public async Task A_populated_fallback_reads_as_its_hosts()
    {
        var seed = Seed(Existing()) with { Fallback = new Fallback { AllowedHosts = ["packages.example.com"] } };
        var client = fixture.Authenticated(fixture.Reset(seed), Stranger);

        var body = await (await client.GetAsync("/fallback")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("deny_all").GetBoolean());
        Assert.Equal("packages.example.com", body.GetProperty("allowed_hosts")[0].GetString());
    }

    /// <summary>
    /// The API reads these sections and never writes them. Grants in particular are platform-owned
    /// and edited out of band — a write route here would let the write path widen the authority
    /// that authorized it, which is the property the whole grants design rests on.
    /// </summary>
    [Theory]
    [InlineData("/grants")]
    [InlineData("/fallback")]
    public async Task Platform_sections_are_not_writable(string path)
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Pipeline);

        var put = await client.PutAsJsonAsync(path, new { });
        var post = await client.PostAsJsonAsync(path, new { });
        var delete = await client.DeleteAsync(path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.Equal(0, fixture.Store.Writes);
    }

    // ---- reads report state recency ----------------------------------------------------------

    /// <summary>Recency is document-scoped: it describes the state blob, so a write to ANY ruleset
    /// moves it for every read — including the two platform reads that the write did not touch.</summary>
    [Theory]
    [InlineData("/rulesets")]
    [InlineData("/rulesets/payments")]
    [InlineData("/grants")]
    [InlineData("/fallback")]
    public async Task A_read_reports_recency_that_advances_after_any_write(string path)
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Pipeline);

        var before = await client.GetAsync(path);
        var modifiedBefore = before.Content.Headers.LastModified;
        var etagBefore = before.Headers.ETag;
        Assert.NotNull(modifiedBefore);
        Assert.NotNull(etagBefore);

        (await client.PutAsJsonAsync("/rulesets/payments", Push(["api.github.com"], "enforce")))
            .EnsureSuccessStatusCode();

        var after = await client.GetAsync(path);

        Assert.True(after.Content.Headers.LastModified > modifiedBefore);
        Assert.NotEqual(etagBefore, after.Headers.ETag);
    }

    // ---- authorization ----------------------------------------------------------------------

    [Fact]
    public async Task A_caller_without_the_verb_is_forbidden()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Stranger);

        var response = await client.PutAsJsonAsync("/rulesets/payments", Push(["evil.example.com"], "enforce"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, fixture.Store.Writes);
    }

    // ---- the full loop ----------------------------------------------------------------------

    [Fact]
    public async Task Onboard_then_promote_then_offboard()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed()), Pipeline);

        // Onboard without an action: the on-ramp default applies.
        var onboarded = await client.PutAsJsonAsync("/rulesets/payments", new
        {
            subjects = new[] { new { appid = Workload } },
            content = new { allowed_hosts = new[] { "api.stripe.com" } },
        });
        Assert.Equal(HttpStatusCode.Created, onboarded.StatusCode);
        Assert.Equal("report", await ActionOf(onboarded));

        // The proxy's document is published as a side effect of the write.
        Assert.Equal("payments", fixture.Store.PublishedAllowlist!.Modules.Single().Id);

        // Promote: an explicit second push, authorized by trust-on-first-use ownership.
        var promoted = await client.PutAsJsonAsync("/rulesets/payments", Push(["api.stripe.com"], "enforce"));
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.Equal("enforce", await ActionOf(promoted));

        // Widening an enforcing ruleset keeps it enforcing, and reports what it added.
        var widened = await client.PutAsJsonAsync(
            "/rulesets/payments", Push(["api.stripe.com", "api.vendor.com"], "enforce"));
        var widenedBody = await widened.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("enforce", widenedBody.GetProperty("ruleset").GetProperty("content").GetProperty("action").GetString());
        Assert.Equal("api.vendor.com", widenedBody.GetProperty("added")[0].GetString());

        // Offboard: gone, and the subject falls to the deny block on the next reload.
        var offboarded = await client.DeleteAsync("/rulesets/payments");
        Assert.Equal(HttpStatusCode.OK, offboarded.StatusCode);
        Assert.Empty(fixture.Store.Current.Rulesets);
        Assert.Empty(fixture.Store.PublishedAllowlist!.Modules);
    }

    // ---- dry run ----------------------------------------------------------------------------

    [Fact]
    public async Task Check_returns_the_diff_and_writes_nothing()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Pipeline);

        var response = await client.PostAsJsonAsync(
            "/rulesets/payments:check", Push(["api.github.com", "api.vendor.com"], "enforce"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.EnsureSuccessStatusCode();
        Assert.Equal("api.vendor.com", body.GetProperty("added")[0].GetString());
        Assert.Equal("b.example.com", body.GetProperty("removed")[0].GetString());
        Assert.Equal(0, fixture.Store.Writes);
        Assert.Equal(0, fixture.Store.PublishedCount);
    }

    // ---- membership changes are visible to the caller ---------------------------------------

    /// <summary>
    /// A pipeline has to be able to confirm a membership change from the response, for the same
    /// reason it gates on the host diff: a push is a full replace, so a bind can take a workload
    /// away as well as add one, and the audit log is not something a pipeline can read.
    /// </summary>
    [Fact]
    public async Task A_bind_reports_which_subjects_were_bound_and_unbound()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Pipeline);

        var response = await client.PutAsJsonAsync("/rulesets/payments", new
        {
            // Swaps one governed workload for another: one bound, one unbound.
            subjects = new[] { new { appid = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" } },
            content = new { allowed_hosts = new[] { "api.github.com", "b.example.com" }, action = "enforce" },
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.EnsureSuccessStatusCode();
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", body.GetProperty("bound")[0].GetString());
        Assert.Equal(Workload, body.GetProperty("unbound")[0].GetString());
    }

    /// <summary>A push that leaves membership alone reports neither, so the fields are a signal
    /// rather than noise on every write.</summary>
    [Fact]
    public async Task A_content_only_push_reports_no_membership_change()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Pipeline);

        var response = await client.PutAsJsonAsync("/rulesets/payments", Push(["api.github.com"], "enforce"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(body.GetProperty("bound").EnumerateArray());
        Assert.Empty(body.GetProperty("unbound").EnumerateArray());
        Assert.Equal("b.example.com", body.GetProperty("removed")[0].GetString());
    }

    /// <summary>:check previews the membership change too, so a pipeline can gate on a bind
    /// before making it — the whole point of the dry run.</summary>
    [Fact]
    public async Task Check_previews_a_bind_without_writing()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Pipeline);

        var response = await client.PostAsJsonAsync("/rulesets/payments:check", new
        {
            subjects = new[] { new { appid = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" } },
            content = new { allowed_hosts = new[] { "api.github.com", "b.example.com" }, action = "enforce" },
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.EnsureSuccessStatusCode();
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", body.GetProperty("bound")[0].GetString());
        Assert.Equal(Workload, body.GetProperty("unbound")[0].GetString());
        Assert.Equal(0, fixture.Store.Writes);
    }

    /// <summary>An onboard binds nothing: every subject is new, so reporting them as "bound" would
    /// be noise. Membership reporting is about CHANGE to an existing ruleset.</summary>
    [Fact]
    public async Task An_onboard_reports_no_membership_change()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed()), Pipeline);

        var response = await client.PutAsJsonAsync("/rulesets/payments", new
        {
            subjects = new[] { new { appid = Workload } },
            content = new { allowed_hosts = new[] { "api.stripe.com" } },
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(body.GetProperty("bound").EnumerateArray());
        Assert.Empty(body.GetProperty("unbound").EnumerateArray());
    }

    // ---- the anti-hijack invariant, over HTTP ------------------------------------------------

    [Fact]
    public async Task A_governed_workload_is_forbidden_from_writing()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Workload);

        var response = await client.PutAsJsonAsync(
            "/rulesets/payments", Push(["api.github.com", "exfil.example.com"], "enforce"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, fixture.Store.Writes);
    }

    [Fact]
    public async Task An_invalid_host_is_a_bad_request()
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Pipeline);

        var response = await client.PutAsJsonAsync("/rulesets/payments", Push(["*.example.com"], "enforce"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>An explicitly null list is the same as an absent one: a hand-written client that
    /// serializes its empty fields as null gets the empty-push semantics, not a 500 from a
    /// non-nullable property that System.Text.Json wrote null over.</summary>
    [Theory]
    [InlineData("""{"content":{"allowed_hosts":null,"action":"enforce"}}""")]
    [InlineData("""{"content":null}""")]
    [InlineData("""{"content":{"allowed_hosts":[]},"acl":{"edit":null,"push":null,"admin":null}}""")]
    public async Task An_explicitly_null_list_is_treated_as_empty(string body)
    {
        var client = fixture.Authenticated(fixture.Reset(Seed(Existing())), Pipeline);

        var response = await client.PutAsync(
            "/rulesets/payments", new StringContent(body, Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();
        var stored = (await fixture.Store.ReadAsync(default)).State.Find("payments");
        Assert.Empty(stored!.Content.AllowedHosts);
    }

    private static async Task<string?> ActionOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())
        .GetProperty("ruleset").GetProperty("content").GetProperty("action").GetString();

    private static object Push(string[] hosts, string action) =>
        new { content = new { allowed_hosts = hosts, action } };

    private static StateDocument Seed(params Ruleset[] rulesets) => new()
    {
        Rulesets = [.. rulesets],
        Grants =
        [
            new Grant { Identity = Pipeline, Verbs = [Verbs.Onboard, Verbs.Update, Verbs.Offboard, Verbs.Bind], Rulesets = ["payments"] },
        ],
    };

    private static Ruleset Existing() => new()
    {
        Name = "payments",
        Subjects = [new Subject { Appid = Workload }],
        Content = new RulesetContent { AllowedHosts = ["api.github.com", "b.example.com"], Action = "enforce" },
    };
}
