// CmCSP – Cache Cleanup Job
//
// Scans the Azure Table Storage cache partition for entries whose ExpiresAt
// timestamp has passed and deletes:
//   - The blob payload when the table row holds a "__blob:" pointer
//   - The table row itself
//
// Intended to run as an Azure Container Apps Scheduled Job (*/30 * * * *).
// The app-side AzureStorageCacheService also enforces TTL on every read, so
// this job is a storage-hygiene companion rather than a correctness dependency.
//
// Configuration (environment variables):
//   CACHE_TABLE_ENDPOINT   – Table Storage service endpoint, e.g.
//                            https://<account>.table.core.windows.net
//   CACHE_BLOB_ENDPOINT    – Blob Storage service endpoint, e.g.
//                            https://<account>.blob.core.windows.net
//   CACHE_TABLE_NAME       – Table name (default: cmcspcache)
//   CACHE_CONTAINER_NAME   – Blob container name (default: cmcspcache)
//   CACHE_PARTITION_KEY    – Partition key to scan (default: cmcsp)
//
// Authentication: DefaultAzureCredential (Managed Identity on Azure).
// Required roles on the storage account:
//   Storage Table Data Contributor
//   Storage Blob Data Contributor (scoped to the cache container)

using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;

const string BlobPointerMark = "__blob:";

var tableEndpoint  = Environment.GetEnvironmentVariable("CACHE_TABLE_ENDPOINT")
    ?? throw new InvalidOperationException("CACHE_TABLE_ENDPOINT is required.");
var blobEndpoint   = Environment.GetEnvironmentVariable("CACHE_BLOB_ENDPOINT")
    ?? throw new InvalidOperationException("CACHE_BLOB_ENDPOINT is required.");
var tableName      = Environment.GetEnvironmentVariable("CACHE_TABLE_NAME")      ?? "cmcspcache";
var containerName  = Environment.GetEnvironmentVariable("CACHE_CONTAINER_NAME")  ?? "cmcspcache";
var partitionKey   = Environment.GetEnvironmentVariable("CACHE_PARTITION_KEY")   ?? "cmcsp";

Log($"Cache cleanup starting. Table={tableName}, Container={containerName}, Partition={partitionKey}");

var cred               = new DefaultAzureCredential();
var tableClient        = new TableClient(new Uri(tableEndpoint), tableName, cred);
var blobContainerUri   = new Uri($"{blobEndpoint.TrimEnd('/')}/{containerName}");
var blobContainerClient = new BlobContainerClient(blobContainerUri, cred);

var now     = DateTimeOffset.UtcNow;
int scanned = 0, expired = 0, errors = 0;

await foreach (var entity in tableClient.QueryAsync<TableEntity>(
    filter: $"PartitionKey eq '{partitionKey}'",
    cancellationToken: CancellationToken.None))
{
    scanned++;
    try
    {
        // Skip entries that have no ExpiresAt or are not yet expired.
        if (!entity.TryGetValue("ExpiresAt", out var expObj) || expObj is not string expStr)
            continue;

        if (!DateTimeOffset.TryParse(expStr, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var expiry))
            continue;

        if (now <= expiry)
            continue;

        // Expired: delete the blob payload first so it is never left orphaned.
        if (entity.TryGetValue("Payload", out var payloadObj)
            && payloadObj is string payload
            && payload.StartsWith(BlobPointerMark, StringComparison.Ordinal))
        {
            var blobName = payload[BlobPointerMark.Length..];
            try
            {
                var deleted = await blobContainerClient
                    .GetBlobClient(blobName)
                    .DeleteIfExistsAsync();
                if (deleted.Value)
                    Log($"  Deleted blob: {blobName}");
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Blob already gone — safe to continue.
            }
        }

        await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag);
        expired++;
        Log($"  Deleted expired entry: key={entity.RowKey}, expired={expiry:O}");
    }
    catch (Exception ex)
    {
        errors++;
        LogError($"Error processing key={entity.RowKey}: {ex.Message}");
    }
}

Log($"Cleanup complete. Scanned={scanned}, Expired={expired}, Errors={errors}");

if (errors > 0)
{
    LogError("One or more entries could not be cleaned up. See errors above.");
    Environment.Exit(1);
}

static void Log(string msg)      => Console.WriteLine($"[{DateTime.UtcNow:O}] {msg}");
static void LogError(string msg) => Console.Error.WriteLine($"[{DateTime.UtcNow:O}] ERROR {msg}");
