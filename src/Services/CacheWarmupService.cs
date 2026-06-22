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

    private readonly ICacheService     _cache;
    private readonly TimeSpan                      _memoryTtl;
    private readonly CostManagementOptions         _options;
    private readonly CustomerStore?                _customers;
    private readonly ILogger<CacheWarmupService>  _logger;

    public CacheWarmupService(
        ICacheService     cache,
        CostManagementOptions        options,
        ILogger<CacheWarmupService>  logger,
        CustomerStore?               customers = null)
    {
        _cache     = cache;
        _memoryTtl = TimeSpan.FromMinutes(options.CacheExpirationMinutes);
        _options   = options;
        _customers = customers;
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

        // Single-tenant / unscoped aggregate (empty prefix) — always warmed.
        var rehydrated = RehydratePartition(prefix: string.Empty, stoppingToken);

        // Phase 9: when multi-tenancy is on, also rehydrate each active customer's partition so a
        // partner drilling into a customer doesn't pay the storage-read latency on the first hit.
        if (_options.MultiTenancy.Enabled && _customers is { IsEnabled: true } && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                var customers = await _customers.GetActiveCustomersAsync(stoppingToken);
                foreach (var customer in customers)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    var prefix = TenantScope.CustomerCacheKeyPrefix(customer.Id);
                    rehydrated += RehydratePartition(prefix, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CacheWarmupService: per-customer rehydration failed.");
            }
        }

        _logger.LogInformation(
            "CacheWarmupService: rehydration complete ({Rehydrated} dataset(s)).",
            rehydrated);
    }

    // Rehydrates the four shared datasets for one tenant partition (prefix). Returns the count
    // of datasets found in the persistent cache (a miss means the collector hasn't produced it).
    private int RehydratePartition(string prefix, CancellationToken stoppingToken)
    {
        var rehydrated = 0;
        foreach (var baseKey in DatasetKeys)
        {
            if (stoppingToken.IsCancellationRequested) break;
            var key = prefix + baseKey;

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
                    _logger.LogDebug(
                        "CacheWarmupService: {Key} not in persistent cache yet — skipping (collector will populate).",
                        key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CacheWarmupService: failed to rehydrate {Key}.", key);
            }
        }
        return rehydrated;
    }
}
