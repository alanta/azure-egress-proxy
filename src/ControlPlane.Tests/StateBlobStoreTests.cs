using System.Net;
using System.Text.Json;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using ControlPlane.Model;
using ControlPlane.Storage;

namespace ControlPlane.Tests;

/// <summary>
/// Drives the real <see cref="StateBlobStore"/> — the real Azure SDK pipeline, the real conditional
/// headers — against a substituted transport. Azurite would test the same contract, but the parts
/// worth pinning here are the ones this store owns: which precondition it sends, and how it maps
/// storage's answers onto <see cref="StatePreconditionFailedException"/> and the empty-state case.
/// </summary>
public class StateBlobStoreTests
{
    private const string ConnectionString = "UseDevelopmentStorage=true";

    // ---- construction -------------------------------------------------------------------------

    [Fact]
    public void Configuring_neither_a_connection_string_nor_a_service_uri_fails_fast()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new StateBlobStore(new BlobStoreOptions()));

        Assert.Contains("ServiceUri", error.Message, StringComparison.Ordinal);
        Assert.Contains("ConnectionString", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The constructor DI actually calls — the tests below go through the transport-injecting
    /// overload, so without this the production path would never be exercised at all.
    /// </summary>
    [Fact]
    public void A_connection_string_alone_is_enough_to_construct_the_store()
    {
        var store = new StateBlobStore(new BlobStoreOptions { ConnectionString = ConnectionString });

        Assert.NotNull(store);
    }

    [Fact]
    public void A_service_uri_is_used_when_no_connection_string_is_configured()
    {
        // Managed identity in Azure: no secret in configuration, so the credential is constructed
        // rather than a connection string parsed. Reaching the blob clients at all proves the path.
        var transport = new FakeTransport(_ => Respond(HttpStatusCode.NotFound));
        var store = Build(new BlobStoreOptions { ServiceUri = "https://acct.blob.core.windows.net" }, transport);

        Assert.NotNull(store);
    }

    [Fact]
    public async Task The_configured_container_and_blob_names_are_the_ones_addressed()
    {
        var transport = new FakeTransport(_ => Respond(HttpStatusCode.NotFound));
        var store = Build(
            new BlobStoreOptions
            {
                ConnectionString = ConnectionString,
                Container = "custom-container",
                StateBlob = "custom-state.json",
                AllowlistBlob = "custom-allowlist.json",
            },
            transport);

        await store.ReadAsync(default);
        await Assert.ThrowsAnyAsync<Exception>(() => store.PublishAllowlistAsync(new AllowlistDocument(), default));

        Assert.Contains("/custom-container/custom-state.json", transport.Requests[0].Uri, StringComparison.Ordinal);
        Assert.Contains("/custom-container/custom-allowlist.json", transport.Requests[1].Uri, StringComparison.Ordinal);
    }

    // ---- reads --------------------------------------------------------------------------------

    [Fact]
    public async Task A_read_returns_the_stored_state_with_its_etag()
    {
        var stored = new StateDocument { Grants = [new Grant { Identity = "platform", Verbs = ["onboard"] }] };
        var transport = new FakeTransport(_ =>
            Respond(HttpStatusCode.OK, StateJson.Serialize(stored), etag: "\"abc123\""));

        var snapshot = await Build(transport).ReadAsync(default);

        Assert.Equal(new ETag("\"abc123\""), snapshot.ETag);
        var grant = Assert.Single(snapshot.State.Grants);
        Assert.Equal("platform", grant.Identity);
    }

    /// <summary>
    /// A missing blob is a fresh install, not a fault: an empty registry with no grants, so every
    /// write is denied until the platform team seeds one. The null ETag is what makes the first
    /// write go out as create-if-absent.
    /// </summary>
    [Fact]
    public async Task An_absent_blob_reads_as_empty_state_with_no_etag()
    {
        var snapshot = await Build(new FakeTransport(_ => Respond(HttpStatusCode.NotFound))).ReadAsync(default);

        Assert.Null(snapshot.ETag);
        Assert.Empty(snapshot.State.Grants);
        Assert.Empty(snapshot.State.Rulesets);
    }

    /// <summary>404 is the only status that reads as "empty"; anything else must surface.</summary>
    [Fact]
    public async Task A_read_failure_that_is_not_a_404_surfaces()
    {
        var store = Build(new FakeTransport(_ => Respond(HttpStatusCode.Forbidden)));

        var error = await Assert.ThrowsAsync<RequestFailedException>(() => store.ReadAsync(default));

        Assert.Equal(403, error.Status);
    }

    // ---- writes -------------------------------------------------------------------------------

    [Fact]
    public async Task A_write_with_an_etag_goes_out_as_if_match()
    {
        var transport = new FakeTransport(_ => Respond(HttpStatusCode.Created));

        await Build(transport).WriteAsync(new StateDocument(), new ETag("\"abc123\""), default);

        var request = Assert.Single(transport.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("\"abc123\"", request.Header("If-Match"));
        Assert.Null(request.Header("If-None-Match"));
    }

    /// <summary>
    /// No ETag means the read saw no blob, so the write must create rather than replace — otherwise
    /// two control planes racing on a fresh install would both "succeed" and one would be lost.
    /// </summary>
    [Fact]
    public async Task A_write_without_an_etag_goes_out_as_create_if_absent()
    {
        var transport = new FakeTransport(_ => Respond(HttpStatusCode.Created));

        await Build(transport).WriteAsync(new StateDocument(), etag: null, default);

        var request = Assert.Single(transport.Requests);
        Assert.Equal("*", request.Header("If-None-Match"));
        Assert.Null(request.Header("If-Match"));
    }

    [Fact]
    public async Task A_written_document_is_json_with_a_trailing_newline()
    {
        var transport = new FakeTransport(_ => Respond(HttpStatusCode.Created));
        var state = new StateDocument { Rulesets = [new Ruleset { Name = "payments" }] };

        await Build(transport).WriteAsync(state, new ETag("\"1\""), default);

        // The stored content type travels as x-ms-blob-content-type; the request's own Content-Type
        // is the upload envelope. Asserting the former is asserting what the blob ends up serving.
        var request = Assert.Single(transport.Requests);
        Assert.Equal("application/json", request.Header("x-ms-blob-content-type"));
        Assert.EndsWith("\n", request.Body, StringComparison.Ordinal);
        Assert.Equal("payments", StateJson.Deserialize<StateDocument>(request.Body).Rulesets[0].Name);
    }

    /// <summary>412 is a lost If-Match race — the signal the RMW retry loop is built on.</summary>
    [Fact]
    public async Task A_lost_if_match_race_becomes_a_precondition_failure()
    {
        var store = Build(new FakeTransport(_ => Respond(HttpStatusCode.PreconditionFailed)));

        var error = await Assert.ThrowsAsync<StatePreconditionFailedException>(
            () => store.WriteAsync(new StateDocument(), new ETag("\"stale\""), default));

        Assert.Equal(412, Assert.IsType<RequestFailedException>(error.InnerException).Status);
    }

    /// <summary>409 is the same race on the create path: someone else seeded the blob first.</summary>
    [Fact]
    public async Task A_lost_create_race_becomes_a_precondition_failure()
    {
        var store = Build(new FakeTransport(_ => Respond(HttpStatusCode.Conflict)));

        var error = await Assert.ThrowsAsync<StatePreconditionFailedException>(
            () => store.WriteAsync(new StateDocument(), etag: null, default));

        Assert.Equal(409, Assert.IsType<RequestFailedException>(error.InnerException).Status);
    }

    /// <summary>
    /// Only the two race statuses are absorbed. A 403 is a misconfigured identity, and turning it
    /// into a precondition failure would make the RMW loop retry it four more times and then report
    /// contention — hiding the real cause.
    /// </summary>
    [Fact]
    public async Task A_write_failure_that_is_not_a_lost_race_surfaces()
    {
        var store = Build(new FakeTransport(_ => Respond(HttpStatusCode.Forbidden)));

        var error = await Assert.ThrowsAsync<RequestFailedException>(
            () => store.WriteAsync(new StateDocument(), new ETag("\"1\""), default));

        Assert.Equal(403, error.Status);
    }

    // ---- publish ------------------------------------------------------------------------------

    /// <summary>
    /// Unconditional by design: this store is the allowlist blob's sole writer, and the state write
    /// that preceded it is the linearization point. A precondition here could only fail spuriously.
    /// </summary>
    [Fact]
    public async Task Publishing_the_allowlist_is_unconditional()
    {
        var transport = new FakeTransport(_ => Respond(HttpStatusCode.Created));
        var allowlist = new AllowlistDocument
        {
            Modules = [new AllowlistModule { Id = "payments", AllowedHosts = ["api.stripe.com"], Action = "enforce" }],
        };

        await Build(transport).PublishAllowlistAsync(allowlist, default);

        var request = Assert.Single(transport.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Null(request.Header("If-Match"));
        Assert.Null(request.Header("If-None-Match"));

        using var published = JsonDocument.Parse(request.Body);
        Assert.Equal("payments", published.RootElement.GetProperty("modules")[0].GetProperty("id").GetString());
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static StateBlobStore Build(FakeTransport transport) =>
        Build(new BlobStoreOptions { ConnectionString = ConnectionString }, transport);

    private static StateBlobStore Build(BlobStoreOptions options, FakeTransport transport) =>
        new(options, new BlobClientOptions
        {
            Transport = new HttpClientTransport(transport),
            // The SDK retries 5xx and timeouts by default; these tests only use statuses it does
            // not retry, but pinning it to one attempt keeps a mistake from looking like a hang.
            Retry = { MaxRetries = 0 },
        });

    private static HttpResponseMessage Respond(HttpStatusCode status, string? body = null, string? etag = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body ?? string.Empty),
        };

        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

        return response;
    }

    /// <summary>
    /// Stands in for storage. Records what the SDK actually put on the wire, because that — not the
    /// arguments the store was called with — is what a real blob would enforce.
    /// </summary>
    private sealed class FakeTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var headers = request.Headers
                .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

            Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!.ToString(), headers, body));

            return respond(request);
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string Uri,
        IReadOnlyDictionary<string, string> Headers,
        string Body)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }
}
