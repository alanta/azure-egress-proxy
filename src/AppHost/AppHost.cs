using System.IO;

var builder = DistributedApplication.CreateBuilder(args);

const string sampleAppId = "11111111-1111-1111-1111-111111111111";
// The console's own identity. It holds no grant in allowlist/rulesets.json and needs none —
// every control-plane endpoint it calls consults no verb, which is what makes it safe to give a
// read-only component broad read access.
const string portalId = "66666666-6666-6666-6666-666666666666";
const string azuriteAccountName = "devstoreaccount1";
const string azuriteAccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
const string allowlistContainer = "egress-config";
const string allowlistBlob = "allowlist.json";
const string rulesetsBlob = "rulesets.json";
const string mockIdpJwks = "http://localhost:18080/jwks";
const string tokenIssuer = "https://mock-idp.local/";
const string tokenAudience = "egress-proxy";

var allowlistDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "allowlist"));
var allowlistPath = Path.Combine(allowlistDirectory, "allowlist.json");
var rulesetsPath = Path.Combine(allowlistDirectory, "rulesets.json");

var azuriteConnectionStringForHost =
    $"DefaultEndpointsProtocol=http;AccountName={azuriteAccountName};AccountKey={azuriteAccountKey};BlobEndpoint=http://127.0.0.1:10000/{azuriteAccountName};";
var azuriteConnectionStringForContainers =
    $"DefaultEndpointsProtocol=http;AccountName={azuriteAccountName};AccountKey={azuriteAccountKey};BlobEndpoint=http://azurite:10000/{azuriteAccountName};";

var azurite = builder.AddContainer("azurite", "mcr.microsoft.com/azure-storage/azurite", "3.34.0")
    .WithArgs("azurite-blob", "--blobHost", "0.0.0.0", "--blobPort", "10000", "--skipApiVersionCheck")
    .WithEndpoint(name: "blob", targetPort: 10000, port: 10000, isProxied: false);

var allowlistSeeder = builder.AddProject<Projects.AllowlistSeeder>("allowlist-seeder")
    .WithEnvironment("ALLOWLIST_CONNECTION_STRING", azuriteConnectionStringForHost)
    .WithEnvironment("ALLOWLIST_CONTAINER", allowlistContainer)
    .WithEnvironment("ALLOWLIST_BLOB", allowlistBlob)
    .WithEnvironment("ALLOWLIST_FILE", allowlistPath)
    .WithEnvironment("RULESETS_BLOB", rulesetsBlob)
    .WithEnvironment("RULESETS_FILE", rulesetsPath)
    .WaitFor(azurite);

var mockIdp = builder.AddDockerfile("mock-idp", "../../mock-idp")
    .WithHttpEndpoint(name: "http", targetPort: 8080, port: 18080, isProxied: false);

var proxy = builder.AddDockerfile("proxy", "../../proxy")
    .WithEndpoint(name: "proxy", targetPort: 4750, port: 14750, isProxied: false)
    .WithArgs("--egress-acl-file", "/render/acl.yaml")
    .WithEnvironment("SMOKESCREEN_ID_MODE", "basic-jwt")
    .WithEnvironment("JWKS_URL", "http://mock-idp:8080/jwks")
    .WithEnvironment("EXPECT_ISS", tokenIssuer)
    .WithEnvironment("EXPECT_AUD", tokenAudience)
    .WithEnvironment("ALLOWLIST_BLOB_CONNECTION_STRING", azuriteConnectionStringForContainers)
    .WithEnvironment("ALLOWLIST_CONTAINER", allowlistContainer)
    .WithEnvironment("ALLOWLIST_BLOB", allowlistBlob)
    .WithEnvironment("POLL_SECONDS", "5")
    .WaitFor(mockIdp)
    .WaitFor(azurite)
    .WaitFor(allowlistSeeder);

// Mode 2: the control plane is the sole writer of the allowlist blob the proxy polls read-only.
// It runs on the host, so it reaches Azurite and the mock IdP through their mapped ports, and it
// validates caller tokens against the same JWKS the proxy uses — one identity model for both planes.
builder.AddProject<Projects.ControlPlane>("control-plane")
    .WithEnvironment("ALLOWLIST_BLOB_CONNECTION_STRING", azuriteConnectionStringForHost)
    .WithEnvironment("ALLOWLIST_CONTAINER", allowlistContainer)
    .WithEnvironment("ALLOWLIST_BLOB", allowlistBlob)
    .WithEnvironment("RULESETS_BLOB", rulesetsBlob)
    .WithEnvironment("JWKS_URL", mockIdpJwks)
    .WithEnvironment("EXPECT_ISS", tokenIssuer)
    .WithEnvironment("EXPECT_AUD", tokenAudience)
    .WaitFor(mockIdp)
    .WaitFor(azurite)
    .WaitFor(allowlistSeeder);

// Mode 3: the read-only console. It calls the control plane as ITSELF — locally that means the
// mock IdP mints it a token for its own appid, exactly as the managed identity would in Azure, so
// the console exercises the real "portal is a machine caller" path rather than a bypass.
//
// The console holds no grant and needs none: every endpoint it uses consults no verb. That is
// worth seeing locally, because it is the property that makes a read-only console safe to give
// broad read access to.
//
// The Azure-only settings (workspace, subscription, scale set) are deliberately absent: those
// panels report themselves as unreadable rather than inventing numbers, which is also what an
// operator would see if the deployment were misconfigured.
builder.AddProject<Projects.Portal>("portal")
    .WithEnvironment("CONTROL_PLANE_URL", "http://localhost:5199")
    .WithEnvironment("CONTROL_PLANE_SCOPE", $"{tokenAudience}/.default")
    .WithEnvironment("EgressProxy__TokenEndpoint", "http://localhost:18080/token")
    .WithEnvironment("EgressProxy__ClientId", portalId)
    .WaitFor(mockIdp)
    .WaitFor(azurite);

builder.AddProject<Projects.SampleApp>("sample-app")
    .WithEnvironment("EgressProxy__Audience", "egress-proxy")
    .WithEnvironment("EgressProxy__ClientId", sampleAppId)
    .WithEnvironment("EgressProxy__TokenEndpoint", "http://localhost:18080/token")
    .WithEnvironment("HTTPS_PROXY", "http://localhost:14750")
    .WithEnvironment("NO_PROXY", "localhost,127.0.0.1,azurite,mock-idp,proxy")
    .WaitFor(proxy);

builder.Build().Run();
