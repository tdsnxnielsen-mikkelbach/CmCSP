namespace CmCSP.Services;

using CmCSP.Models;

/// <summary>
/// Hosted service that rehydrates the per-replica in-memory cache from the shared
/// persistent cache (Azure Table/Blob) immediately on startup, so the first page a
/// user visits after a container restart or scale-out doesn't pay the storage-read
/// latency on the critical path.
///
/// This service is a <b>rehydrator only</b>: it never issues live Cost Management API
/// calls. Collection of fresh data is owned by <c>CostCollectorJob</c> (nightly +
/// on-demand). If a dataset isn't present in the persistent cache yet (e.g. a brand
/// new deployment before the collector's first run), warmup simply skips it — the
/// collector will populate it, and the first user request lazily falls back as before.
/// </summary>
public sealed class CacheWarmupService : BackgroundService
{
    // Cache keys owned by the cost services (see services-cache.instructions.md).
    private static readonly string[] DatasetKeys = ["cm_main", "cm_rg", "cm_tag", "cm_main_amort"];

    private readonly AzureStorageCacheService     _cache;
    private readonly TimeSpan                      _memoryTtl;
    private readonly ILogger<CacheWarmupService>  _logger;

    public CacheWarmupService(
        AzureStorageCacheService     cache,
        CostManagementOptions        options,
        ILogger<CacheWarmupService>  logger)
    {
        _cache     = cache;
        _memoryTtl = TimeSpan.FromMinutes(options.CacheExpirationMinutes);
        _logger    = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay lets ASP.NET Core finish its startup pipeline before we
        // touch storage and start logging cache messages.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        if (!_cache.IsAzureEnabled)
        {
            _logger.LogInformation(
                "CacheWarmupService: persistent cache disabled — nothing to rehydrate.");
            return;
        }

        _logger.LogInformation("CacheWarmupService: rehydrating in-memory cache from persistent storage.");

        var rehydrated = 0;
        foreach (var key in DatasetKeys)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                // TryGetValue re-populates the in-memory tier when the entry exists in
                // Azure Storage. A miss means the collector hasn't produced it yet —
                // skip rather than triggering a live API fetch.
                if (_cache.TryGetValue<List<CostRow>>(key, _memoryTtl, out var rows) && rows is not null)
                {
                    rehydrated++;
                    _logger.LogInformation(
                        "CacheWarmupService: rehydrated {Key} ({Rows} rows) from persistent cache.",
                        key, rows.Count);
                }
                else
                {
                    _logger.LogInformation(
                        "CacheWarmupService: {Key} not in persistent cache yet — skipping (collector will populate).",
                        key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CacheWarmupService: failed to rehydrate {Key}.", key);
            }
        }

        _logger.LogInformation(
            "CacheWarmupService: rehydration complete ({Rehydrated}/{Total} datasets).",
            rehydrated, DatasetKeys.Length);
    }
}
