using Azure.Storage.Blobs;

var connectionString = GetRequiredEnvironmentVariable("ALLOWLIST_CONNECTION_STRING");
var allowlistFile = GetRequiredEnvironmentVariable("ALLOWLIST_FILE");
var containerName = Environment.GetEnvironmentVariable("ALLOWLIST_CONTAINER") ?? "egress-config";
var blobName = Environment.GetEnvironmentVariable("ALLOWLIST_BLOB") ?? "allowlist.json";

if (!File.Exists(allowlistFile))
{
    throw new FileNotFoundException($"Allowlist file '{allowlistFile}' was not found.");
}

var containerClient = new BlobContainerClient(connectionString, containerName);
await containerClient.CreateIfNotExistsAsync();

var blobClient = containerClient.GetBlobClient(blobName);
await using var stream = File.OpenRead(allowlistFile);
await blobClient.UploadAsync(stream, overwrite: true);
var properties = await blobClient.GetPropertiesAsync();

Console.WriteLine(
    "Seeded allowlist blob: container={0} blob={1} etag={2} source={3}",
    containerName,
    blobName,
    properties.Value.ETag,
    allowlistFile);

static string GetRequiredEnvironmentVariable(string key)
{
    var value = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Environment variable '{key}' is required.");
    }

    return value;
}
