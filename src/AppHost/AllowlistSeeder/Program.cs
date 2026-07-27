using Azure.Storage.Blobs;

// Seeds the blobs the local stack starts from:
//   allowlist.json  the proxy's document. In Mode 1 it is the config-as-code artifact; in Mode 2 it
//                   is a rendered projection the control plane overwrites on the first push.
//   rulesets.json   the control plane's own state — rulesets plus the platform grants. Optional, so
//                   a Mode 1 run without the control plane still works.
var connectionString = GetRequiredEnvironmentVariable("ALLOWLIST_CONNECTION_STRING");
var containerName = Environment.GetEnvironmentVariable("ALLOWLIST_CONTAINER") ?? "egress-config";

var containerClient = new BlobContainerClient(connectionString, containerName);
await containerClient.CreateIfNotExistsAsync();

await SeedAsync(
    GetRequiredEnvironmentVariable("ALLOWLIST_FILE"),
    Environment.GetEnvironmentVariable("ALLOWLIST_BLOB") ?? "allowlist.json");

await SeedAsync(
    Environment.GetEnvironmentVariable("RULESETS_FILE"),
    Environment.GetEnvironmentVariable("RULESETS_BLOB") ?? "rulesets.json");

async Task SeedAsync(string? sourceFile, string blobName)
{
    if (string.IsNullOrWhiteSpace(sourceFile))
    {
        return;
    }

    if (!File.Exists(sourceFile))
    {
        throw new FileNotFoundException($"Seed file '{sourceFile}' was not found.");
    }

    var blobClient = containerClient.GetBlobClient(blobName);
    await using var stream = File.OpenRead(sourceFile);
    await blobClient.UploadAsync(stream, overwrite: true);
    var properties = await blobClient.GetPropertiesAsync();

    Console.WriteLine(
        "Seeded blob: container={0} blob={1} etag={2} source={3}",
        containerName,
        blobName,
        properties.Value.ETag,
        sourceFile);
}

static string GetRequiredEnvironmentVariable(string key)
{
    var value = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Environment variable '{key}' is required.");
    }

    return value;
}
