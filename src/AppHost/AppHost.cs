using System.IO;

var builder = DistributedApplication.CreateBuilder(args);

const string sampleAppId = "11111111-1111-1111-1111-111111111111";
const string azuriteAccountName = "devstoreaccount1";
const string azuriteAccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
const string allowlistContainer = "egress-config";
const string allowlistBlob = "allowlist.json";

var allowlistPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "allowlist", "allowlist.json"));

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
    .WaitFor(azurite);

var mockIdp = builder.AddDockerfile("mock-idp", "../../mock-idp")
    .WithHttpEndpoint(name: "http", targetPort: 8080, port: 18080, isProxied: false);

var proxy = builder.AddDockerfile("proxy", "../../proxy")
    .WithEndpoint(name: "proxy", targetPort: 4750, port: 14750, isProxied: false)
    .WithArgs("--egress-acl-file", "/render/acl.yaml")
    .WithEnvironment("SMOKESCREEN_ID_MODE", "basic-jwt")
    .WithEnvironment("JWKS_URL", "http://mock-idp:8080/jwks")
    .WithEnvironment("EXPECT_ISS", "https://mock-idp.local/")
    .WithEnvironment("EXPECT_AUD", "egress-proxy")
    .WithEnvironment("ALLOWLIST_BLOB_CONNECTION_STRING", azuriteConnectionStringForContainers)
    .WithEnvironment("ALLOWLIST_CONTAINER", allowlistContainer)
    .WithEnvironment("ALLOWLIST_BLOB", allowlistBlob)
    .WithEnvironment("POLL_SECONDS", "5")
    .WaitFor(mockIdp)
    .WaitFor(azurite)
    .WaitFor(allowlistSeeder);

builder.AddProject<Projects.SampleApp>("sample-app")
    .WithEnvironment("EgressProxy__Audience", "egress-proxy")
    .WithEnvironment("EgressProxy__ClientId", sampleAppId)
    .WithEnvironment("EgressProxy__TokenEndpoint", "http://localhost:18080/token")
    .WithEnvironment("HTTPS_PROXY", "http://localhost:14750")
    .WithEnvironment("NO_PROXY", "localhost,127.0.0.1,azurite,mock-idp,proxy")
    .WaitFor(proxy);

builder.Build().Run();
