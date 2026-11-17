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
///   - Serialise the value to JSON and gzip-compress it.
///   - If the compressed payload is ≤ <see cref="TableSizeLimit"/> bytes → store in Azure Table Storage.
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
public sealed class AzureStorageCacheService : ICacheService
{
    // Entries larger than this byte threshold are stored in blob; smaller go inline in table.
    private const int TableSizeLimit = 60 * 1024; // 60 KB (table row limit is 1 MB, use conservative threshold)

    private const string PartitionKey    = "cmcsp";
    private const string RowSuffix       = ""; // we use the cache key as RowKey directly
    private const string BlobPointerMark = "__blob:";
    private const string CompressedMark = "__gzip:";
    private const string CompressedBlobMark = "__blob-gzip:";

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
    public async ValueTask<(bool Found, T? Value)> TryGetValueAsync<T>(
        string key, TimeSpan memoryCacheTtl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_memory.TryGetValue(key, out T? memVal))
            return (true, memVal);

        if (_table is null) return (false, default);

        try
        {
            var response = await _table.GetEntityIfExistsAsync<TableEntity>(
                PartitionKey, key, cancellationToken: ct);
            var entity = response.Value;
            if (entity is null) return (false, default);

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
                        && (expPayload.StartsWith(BlobPointerMark, StringComparison.Ordinal)
                            || expPayload.StartsWith(CompressedBlobMark, StringComparison.Ordinal)))
                    {
                        var blobName = expPayload.StartsWith(CompressedBlobMark, StringComparison.Ordinal)
                            ? expPayload[CompressedBlobMark.Length..]
                            : expPayload[BlobPointerMark.Length..];
                        if (_blobs is not null)
                            await _blobs.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: ct);
                    }
                    await _table.DeleteEntityAsync(PartitionKey, key, cancellationToken: ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception delEx)
                {
                    _logger.LogWarning(delEx,
                        "AzureStorageCacheService: failed to delete expired entry for key {Key}.", key);
                }
                return (false, default);
            }

            byte[]? payload;

            if (entity.TryGetValue("Payload", out var raw) && raw is string s)
            {
                if (s.StartsWith(CompressedMark, StringComparison.Ordinal))
                {
                    payload = Convert.FromBase64String(s[CompressedMark.Length..]);
                }
                else if (s.StartsWith(CompressedBlobMark, StringComparison.Ordinal))
                    payload = await DownloadBlobAsync(s[CompressedBlobMark.Length..], ct);
                else if (s.StartsWith(BlobPointerMark, StringComparison.Ordinal))
                    payload = await DownloadLegacyBlobAsync(s[BlobPointerMark.Length..], ct);
                else
                    payload = Encoding.UTF8.GetBytes(s);
            }
            else return (false, default);

            if (payload is null) return (false, default);

            var value = CachePayloadCodec.Deserialize<T>(payload, JsonOpts);
            if (value is not null)
                _memory.Set(key, value, memoryCacheTtl);

            return (value is not null, value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AzureStorageCacheService: read failed for key {Key}.", key);
            return (false, default);
        }
    }

    /// <summary>
    /// Stores a value in both in-memory cache and Azure Storage.
    /// </summary>
    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _memory.Set(key, value, ttl);

        if (_table is null) return;

        try
        {
            var bytes = CachePayloadCodec.Serialize(value, JsonOpts);
            var inlinePayload = $"{CompressedMark}{Convert.ToBase64String(bytes)}";
            string payload;

            if (Encoding.UTF8.GetByteCount(inlinePayload) > TableSizeLimit && _blobs is not null)
            {
                var blobName = $"{key}-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
                await UploadBlobAsync(blobName, bytes, ct);
                payload = $"{CompressedBlobMark}{blobName}";
                _logger.LogDebug(
                    "Cache key {Key}: payload {Bytes} bytes → stored in blob {BlobName}.",
                    key, bytes.Length, blobName);
            }
            else
            {
                payload = inlinePayload;
                _logger.LogDebug(
                    "Cache key {Key}: payload {Bytes} bytes → stored inline in table.",
                    key, bytes.Length);
            }

            var entity = new TableEntity(PartitionKey, key)
            {
                ["Payload"]   = payload,
                ["ExpiresAt"] = DateTimeOffset.UtcNow.Add(ttl).ToString("O")
            };
            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AzureStorageCacheService: write failed for key {Key}. In-memory entry retained.",
                key);
        }
    }

    /// <summary>Removes from both in-memory and Azure Storage.</summary>
    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _memory.Remove(key);

        if (_table is null) return;
        try
        {
            var response = await _table.GetEntityIfExistsAsync<TableEntity>(
                PartitionKey, key, cancellationToken: ct);
            var entity = response.Value;
            if (entity is not null)
            {
                if (entity.TryGetValue("Payload", out var raw) &&
                    raw is string s)
                {
                    var blobName = s.StartsWith(CompressedBlobMark, StringComparison.Ordinal)
                        ? s[CompressedBlobMark.Length..]
                        : s.StartsWith(BlobPointerMark, StringComparison.Ordinal)
                            ? s[BlobPointerMark.Length..]
                            : null;
                    if (blobName is not null && _blobs is not null)
                        await _blobs.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: ct);
                }
                await _table.DeleteEntityAsync(PartitionKey, key, cancellationToken: ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AzureStorageCacheService: delete failed for key {Key}.", key);
        }
    }

    // ── Blob helpers ────────────────────────────────────────────────────────

    private async Task UploadBlobAsync(string blobName, byte[] content, CancellationToken ct)
    {
        var blob = _blobs!.GetBlobClient(blobName);
        await blob.UploadAsync(BinaryData.FromBytes(content), overwrite: true, cancellationToken: ct);
    }

    private async Task<byte[]?> DownloadBlobAsync(string blobName, CancellationToken ct)
    {
        try
        {
            var download = await _blobs!.GetBlobClient(blobName).DownloadContentAsync(ct);
            return download.Value.Content.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("Cache blob {BlobName} not found.", blobName);
            return null;
        }
    }

    private async Task<byte[]?> DownloadLegacyBlobAsync(string blobName, CancellationToken ct)
    {
        return await DownloadBlobAsync(blobName, ct);
    }
}
