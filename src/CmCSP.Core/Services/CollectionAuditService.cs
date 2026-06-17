using Azure.Data.Tables;
using Azure.Identity;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Reads and writes the cost-collection audit trail in Azure Table Storage.
///
/// The CostCollectorJob calls <see cref="WriteAsync"/> at the end of every run; the
/// dashboard calls <see cref="GetLatestAsync"/> / <see cref="GetRecentAsync"/> to show
/// the last-run status without waiting on Log Analytics ingestion latency.
///
/// Uses the same storage account as the distributed cache
/// (<see cref="CostManagementOptions.AzureCacheOptions.StorageAccountUri"/>) but a
/// dedicated table so audit rows never collide with cache entries.
///
/// Authentication: DefaultAzureCredential. The writer (job MI) needs
/// 'Storage Table Data Contributor'; the reader (app MI) needs at least
/// 'Storage Table Data Reader' on the storage account.
/// </summary>
public sealed class CollectionAuditService
{
    public const string TableName = "cmcspcollectaudit";
    private const string PartitionKey = "collect";

    private readonly TableClient? _table;
    private readonly ILogger<CollectionAuditService> _logger;

    public CollectionAuditService(
        CostManagementOptions options,
        ILogger<CollectionAuditService> logger)
    {
        _logger = logger;

        var uri = options.AzureCache.StorageAccountUri;
        if (string.IsNullOrWhiteSpace(uri)) return;

        try
        {
            // Derive the table endpoint from the (blob) base URI, mirroring AzureStorageCacheService.
            var tableHost = new Uri(uri).Host.Replace(".blob.", ".table.");
            _table = new TableClient(new Uri($"https://{tableHost}"), TableName, new DefaultAzureCredential());
            _table.CreateIfNotExists();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CollectionAuditService: failed to initialise table client — audit disabled.");
            _table = null;
        }
    }

    /// <summary><c>true</c> when audit storage is configured and reachable.</summary>
    public bool IsEnabled => _table is not null;

    /// <summary>Appends an audit row. No-op (with a warning) when audit storage is not configured.</summary>
    public async Task WriteAsync(CollectionAuditRecord record, CancellationToken ct = default)
    {
        if (_table is null)
        {
            _logger.LogWarning("CollectionAuditService: audit storage not configured; skipping audit write.");
            return;
        }

        // Reverse-ticks row key so the newest run sorts first (ascending) in a partition scan.
        var rowKey = $"{DateTimeOffset.MaxValue.Ticks - record.StartedUtc.UtcTicks:D19}_{record.CorrelationId}";

        var entity = new TableEntity(PartitionKey, rowKey)
        {
            ["Status"]            = record.Status,
            ["Trigger"]           = record.Trigger,
            ["StartedUtc"]        = record.StartedUtc,
            ["FinishedUtc"]       = record.FinishedUtc,
            ["DurationMs"]        = record.DurationMs,
            ["SubscriptionCount"] = record.SubscriptionCount,
            ["MainRows"]          = record.MainRows,
            ["RgRows"]            = record.RgRows,
            ["TagRows"]           = record.TagRows,
            ["AmortRows"]         = record.AmortRows,
            ["Error"]             = record.Error,
            ["ReplicaName"]       = record.ReplicaName,
            ["CorrelationId"]     = record.CorrelationId
        };

        try
        {
            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CollectionAuditService: failed to write audit row for run {CorrelationId}.", record.CorrelationId);
        }
    }

    /// <summary>Returns the most recent audit rows, newest first (up to <paramref name="max"/>).</summary>
    public async Task<IReadOnlyList<CollectionAuditRecord>> GetRecentAsync(int max = 10, CancellationToken ct = default)
    {
        if (_table is null) return [];

        var results = new List<CollectionAuditRecord>(max);
        try
        {
            await foreach (var entity in _table.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{PartitionKey}'",
                maxPerPage: max,
                cancellationToken: ct))
            {
                results.Add(Map(entity));
                if (results.Count >= max) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CollectionAuditService: failed to read audit rows.");
        }
        return results;
    }

    /// <summary>Returns the single most recent audit row, or null if none exist.</summary>
    public async Task<CollectionAuditRecord?> GetLatestAsync(CancellationToken ct = default)
        => (await GetRecentAsync(1, ct)).FirstOrDefault();

    private static CollectionAuditRecord Map(TableEntity e) => new()
    {
        Status            = e.GetString("Status") ?? "Unknown",
        Trigger           = e.GetString("Trigger") ?? "manual",
        StartedUtc        = e.GetDateTimeOffset("StartedUtc") ?? default,
        FinishedUtc       = e.GetDateTimeOffset("FinishedUtc") ?? default,
        DurationMs        = e.GetInt64("DurationMs") ?? 0,
        SubscriptionCount = e.GetInt32("SubscriptionCount") ?? 0,
        MainRows          = e.GetInt32("MainRows") ?? 0,
        RgRows            = e.GetInt32("RgRows") ?? 0,
        TagRows           = e.GetInt32("TagRows") ?? 0,
        AmortRows         = e.GetInt32("AmortRows") ?? 0,
        Error             = e.GetString("Error"),
        ReplicaName       = e.GetString("ReplicaName"),
        CorrelationId     = e.GetString("CorrelationId") ?? string.Empty
    };
}
