namespace CmCSP.Services;

/// <summary>
/// Hosted service that pre-warms all three cost data caches immediately on startup,
/// so the first page a user visits doesn't block waiting for Azure API calls.
///
/// Datasets are fetched sequentially (not concurrently) because all three query the
/// same subscriptions and concurrent fetches would compete for the 5-req/min
/// per-subscription rate limit that <see cref="CostManagementService"/> enforces.
/// </summary>
public sealed class CacheWarmupService : BackgroundService
{
    private readonly ICostManagementService      _costService;
    private readonly ILogger<CacheWarmupService> _logger;

    public CacheWarmupService(
        ICostManagementService      costService,
        ILogger<CacheWarmupService> logger)
    {
        _costService = costService;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay lets ASP.NET Core finish its startup pipeline before we
        // issue API calls and start logging cost-service messages.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        _logger.LogInformation("CacheWarmupService: starting pre-warm of all cost datasets.");

        try
        {
            await _costService.GetMainCostDataAsync(stoppingToken); // cm_main (most pages)
            await _costService.GetRgCostDataAsync(stoppingToken);   // cm_rg
            await _costService.GetTagCostDataAsync(stoppingToken);  // cm_tag

            _logger.LogInformation("CacheWarmupService: all datasets pre-warmed successfully.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("CacheWarmupService: cancelled during application shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CacheWarmupService: pre-warm failed.");
        }
    }
}
