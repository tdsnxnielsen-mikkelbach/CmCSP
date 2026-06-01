using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Drop-in wrapper around <see cref="IMemoryCache"/> that persists cache entries to
/// Azure Storage when <see cref="CostManagementOptions.AzureCacheOptions.Enabled"/> is true.
///
/// Routing strategy:
///   - Serialise the value to JSON.
///   - If the payload is ≤ <see cref="TableSizeLimit"/> bytes → store in Azure Table Storage.
///     The table row holds the JSON inline (fast, no extra request to read).
///   - If the payload is  > <see cref="TableSizeLimit"/> bytes → store in Azure Blob Storage.
///     The table row holds only a pointer (blob name); the actual data is in the blob.
///
/// This allows multiple Container App replicas to share cached data and survive restarts
/// without needing a Redis cache or a database.
///
/// Authentication: DefaultAzureCredential (Managed Identity on Azure, az login locally).
/// Required roles on the storage account:
///   - Storage Table Data Contributor
///   - Storage Blob Data Contributor
/// </summary>
public sealed class AzureStorageCacheService
{
    // Entries larger than this byte threshold are stored in blob; smaller go inline in table.
    private const int TableSizeLimit = 60 * 1024; // 60 KB (table row limit is 1 MB, use conservative threshold)

    private const string PartitionKey    = "cmcsp";
    private const string RowSuffix       = ""; // we use the cache key as RowKey directly
    private const string BlobPointerMark = "__blob:";

    private readonly IMemoryCache    _memory;
    private readonly TableClient?    _table;
    private readonly BlobContainerClient? _blobs;
    private readonly ILogger<AzureStorageCacheService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AzureStorageCacheService(
        IMemoryCache                         memory,
        CostManagementOptions                options,
        ILogger<AzureStorageCacheService>    logger)
    {
        _memory = memory;
        _logger = logger;

        var cfg = options.AzureCache;
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.StorageAccountUri)) return;

        try
        {
            var cred = new DefaultAzureCredential();

            var tableUri = new Uri($"https://{new Uri(cfg.StorageAccountUri).Host.Replace(".blob.", ".table.")}/{cfg.TableName}");
            _table = new TableClient(
                new Uri($"https://{new Uri(cfg.StorageAccountUri).Host.Replace(".blob.", ".table.")}"),
                cfg.TableName, cred);
            _table.CreateIfNotExists();

            var blobUri = new Uri(
                $"{cfg.StorageAccountUri.TrimEnd('/')}/{cfg.CacheContainerName}");
            _blobs = new BlobContainerClient(blobUri, cred);
            _blobs.CreateIfNotExists();

            _logger.LogInformation(
                "AzureStorageCacheService initialised. Table={Table}, BlobContainer={Container}",
                cfg.TableName, cfg.CacheContainerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AzureStorageCacheService: failed to connect to Azure Storage. " +
                "Falling back to in-memory cache only.");
            _table = null;
            _blobs = null;
        }
    }

    public bool IsAzureEnabled => _table is not null;

    // ── Public cache API ────────────────────────────────────────────────────

    /// <summary>
    /// Try to get a value. Checks in-memory first, then Azure Storage.
    /// If found in Azure but not in memory, re-populates the in-memory entry.
    /// </summary>
    public bool TryGetValue<T>(string key, TimeSpan memoryCacheTtl, out T? value)
    {
        if (_memory.TryGetValue(key, out T? memVal))
        {
            value = memVal;
            return true;
        }

        if (_table is null) { value = default; return false; }

        try
        {
            var response = _table.GetEntityIfExists<TableEntity>(PartitionKey, key);
            if (!response.HasValue) { value = default; return false; }

            var entity = response.Value!;

            // ── Strict TTL enforcement ────────────────────────────────────────
            // Check expiry before touching the blob to avoid a wasted network
            // round-trip for large payloads that have already expired.
            if (entity.TryGetValue("ExpiresAt", out var expObj)
                && expObj is string expStr
                && DateTimeOffset.TryParse(expStr, null,
                       System.Globalization.DateTimeStyles.RoundtripKind, out var expiry)
                && DateTimeOffset.UtcNow > expiry)
            {
                _logger.LogDebug(
                    "AzureStorageCacheService: key {Key} expired at {Expiry}. Deleting from storage.",
                    key, expiry);
                try
                {
                    // Delete the pointed blob first, then the table row.
                    if (entity.TryGetValue("Payload", out var expPayloadObj)
                        && expPayloadObj is string expPayload
                        && expPayload.StartsWith(BlobPointerMark, StringComparison.Ordinal))
                    {
                        var blobName = expPayload[BlobPointerMark.Length..];
                        _blobs?.GetBlobClient(blobName).DeleteIfExists();
                    }
                    _table.DeleteEntity(PartitionKey, key);
                }
                catch (Exception delEx)
                {
                    _logger.LogWarning(delEx,
                        "AzureStorageCacheService: failed to delete expired entry for key {Key}.", key);
                }
                value = default;
                return false;
            }

            string? payload;

            if (entity.TryGetValue("Payload", out var raw) && raw is string s)
            {
                if (s.StartsWith(BlobPointerMark, StringComparison.Ordinal))
                {
                    // Large payload — read from blob
                    var blobName = s[BlobPointerMark.Length..];
                    payload = DownloadBlobAsString(blobName);
                }
                else
                {
                    payload = s;
                }
            }
            else { value = default; return false; }

            if (payload is null) { value = default; return false; }

            value = JsonSerializer.Deserialize<T>(payload, JsonOpts);
            if (value is not null)
                _memory.Set(key, value, memoryCacheTtl);

            return value is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AzureStorageCacheService: read failed for key {Key}.", key);
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Stores a value in both in-memory cache and Azure Storage.
    /// </summary>
    public void Set<T>(string key, T value, TimeSpan ttl)
    {
        _memory.Set(key, value, ttl);

        if (_table is null) return;

        try
        {
            var json    = JsonSerializer.Serialize(value, JsonOpts);
            var bytes   = Encoding.UTF8.GetByteCount(json);
            string payload;

            if (bytes > TableSizeLimit && _blobs is not null)
            {
                var blobName = $"{key}-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
                UploadBlobString(blobName, json);
                payload = $"{BlobPointerMark}{blobName}";
                _logger.LogDebug(
                    "Cache key {Key}: payload {Bytes} bytes → stored in blob {BlobName}.",
                    key, bytes, blobName);
            }
            else
            {
                payload = json;
                _logger.LogDebug(
                    "Cache key {Key}: payload {Bytes} bytes → stored inline in table.",
                    key, bytes);
            }

            var entity = new TableEntity(PartitionKey, key)
            {
                ["Payload"]   = payload,
                ["ExpiresAt"] = DateTimeOffset.UtcNow.Add(ttl).ToString("O")
            };
            _table.UpsertEntity(entity, TableUpdateMode.Replace);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AzureStorageCacheService: write failed for key {Key}. In-memory entry retained.",
                key);
        }
    }

    /// <summary>Removes from both in-memory and Azure Storage.</summary>
    public void Remove(string key)
    {
        _memory.Remove(key);

        if (_table is null) return;
        try
        {
            var response = _table.GetEntityIfExists<TableEntity>(PartitionKey, key);
            if (response.HasValue)
            {
                var entity = response.Value!;
                if (entity.TryGetValue("Payload", out var raw) &&
                    raw is string s && s.StartsWith(BlobPointerMark, StringComparison.Ordinal))
                {
                    var blobName = s[BlobPointerMark.Length..];
                    _blobs?.GetBlobClient(blobName).DeleteIfExists();
                }
                _table.DeleteEntity(PartitionKey, key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AzureStorageCacheService: delete failed for key {Key}.", key);
        }
    }

    // ── Blob helpers ────────────────────────────────────────────────────────

    private void UploadBlobString(string blobName, string content)
    {
        var blob = _blobs!.GetBlobClient(blobName);
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
        blob.Upload(ms, overwrite: true);
    }

    private string? DownloadBlobAsString(string blobName)
    {
        try
        {
            var blob     = _blobs!.GetBlobClient(blobName);
            var download = blob.DownloadContent();
            return download.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("Cache blob {BlobName} not found.", blobName);
            return null;
        }
    }
}
