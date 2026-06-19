using Azure.Data.Tables;
using Azure.Identity;
using CmCSP.Data;
using CmCSP.Models;
using Microsoft.EntityFrameworkCore;

namespace CmCSP.Services;

/// <summary>
/// Reads and writes the cost-collection audit trail.
///
/// Phase 4: when a <see cref="CmcspDbContext"/> factory is registered (i.e. the SQL data
/// platform is provisioned), audit rows are persisted to and read from the
/// <c>CollectionAudit</c> SQL table — the durable system of record. When SQL is not
/// configured the service falls back to the legacy Azure Table Storage table
/// (<c>cmcspcollectaudit</c>) so existing deployments keep working unchanged.
///
/// The CostCollectorJob calls <see cref="WriteAsync"/> at the end of every run; the
/// dashboard calls <see cref="GetLatestAsync"/> / <see cref="GetRecentAsync"/> to show
/// the last-run status without waiting on Log Analytics ingestion latency.
///
/// Table Storage fallback uses the same storage account as the distributed cache
/// (<see cref="CostManagementOptions.AzureCacheOptions.StorageAccountUri"/>) but a
/// dedicated table so audit rows never collide with cache entries.
///
/// Authentication: DefaultAzureCredential / managed identity for both back-ends.
/// </summary>
public sealed class CollectionAuditService
{
    public const string TableName = "cmcspcollectaudit";
    private const string PartitionKey = "collect";

    private readonly IDbContextFactory<CmcspDbContext>? _dbFactory;
    private readonly TableClient? _table;
    private readonly ILogger<CollectionAuditService> _logger;

    public CollectionAuditService(
        CostManagementOptions options,
        ILogger<CollectionAuditService> logger,
        IDbContextFactory<CmcspDbContext>? dbFactory = null)
    {
        _logger = logger;
        _dbFactory = dbFactory;

        // SQL is the system of record when available — skip the Table Storage client entirely.
        if (_dbFactory is not null)
        {
            _logger.LogInformation("CollectionAuditService: using SQL ({Table}) as the audit store.", nameof(CmcspDbContext.CollectionAudit));
            return;
        }

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

    /// <summary><c>true</c> when an audit store (SQL or Table Storage) is configured.</summary>
    public bool IsEnabled => _dbFactory is not null || _table is not null;

    /// <summary>Appends an audit row. No-op (with a warning) when audit storage is not configured.</summary>
    public async Task WriteAsync(CollectionAuditRecord record, CancellationToken ct = default)
    {
        if (_dbFactory is not null)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                db.CollectionAudit.Add(ToEntity(record));
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CollectionAuditService: failed to write audit row for run {CorrelationId} to SQL.", record.CorrelationId);
            }
            return;
        }

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
        if (_dbFactory is not null)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var rows = await db.CollectionAudit
                    .OrderByDescending(x => x.StartedUtc)
                    .Take(max)
                    .ToListAsync(ct);
                return rows.Select(Map).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CollectionAuditService: failed to read audit rows from SQL.");
                return [];
            }
        }

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

    private static CollectionAuditRecord Map(CollectionAuditEntity e) => new()
    {
        Status            = e.Status,
        Trigger           = e.Trigger,
        StartedUtc        = e.StartedUtc,
        FinishedUtc       = e.FinishedUtc,
        DurationMs        = e.DurationMs,
        SubscriptionCount = e.SubscriptionCount,
        MainRows          = e.MainRows,
        RgRows            = e.RgRows,
        TagRows           = e.TagRows,
        AmortRows         = e.AmortRows,
        Error             = e.Error,
        ReplicaName       = e.ReplicaName,
        CorrelationId     = e.CorrelationId
    };

    private static CollectionAuditEntity ToEntity(CollectionAuditRecord r) => new()
    {
        Status            = r.Status,
        Trigger           = r.Trigger,
        StartedUtc        = r.StartedUtc,
        FinishedUtc       = r.FinishedUtc,
        DurationMs        = r.DurationMs,
        SubscriptionCount = r.SubscriptionCount,
        MainRows          = r.MainRows,
        RgRows            = r.RgRows,
        TagRows           = r.TagRows,
        AmortRows         = r.AmortRows,
        Error             = r.Error,
        ReplicaName       = r.ReplicaName,
        CorrelationId     = r.CorrelationId
    };
}
