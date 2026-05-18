namespace CmCSP.Services;

using CmCSP.Models;

/// <summary>
/// Background service that refreshes the cost data cache from the Cost Management
/// Query API once per day at a configurable UTC hour
/// (<see cref="CostManagementOptions.ApiDailyRefreshHourUtc"/>).
///
/// Purpose: blob exports are "historical" – they land up to 24 h after the billing
/// period closes.  The Query API always returns up-to-date data (typically 2–4 h
/// behind real time), so a daily API refresh ensures dashboards show the latest
/// figures even when export blobs haven't yet been updated.
///
/// The refresh writes to the same cache keys as <see cref="BlobCostManagementService"/>
/// so any subsequent request within the cache TTL is served from the freshly fetched
/// API data.  Once the TTL expires, <see cref="BlobCostManagementService"/> resumes
/// serving from blob exports as normal.
///
/// This service is only registered when ExportBlob.Enabled = true.
/// </summary>
public sealed class DailyApiRefreshService : BackgroundService
{
    private readonly CostManagementService          _apiService;
    private readonly CostManagementOptions          _options;
    private readonly ILogger<DailyApiRefreshService> _logger;

    public DailyApiRefreshService(
        CostManagementService           apiService,
        CostManagementOptions           options,
        ILogger<DailyApiRefreshService> logger)
    {
        _apiService = apiService;
        _options    = options;
        _logger     = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var refreshHour = Math.Clamp(_options.ApiDailyRefreshHourUtc, 0, 23);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now     = DateTime.UtcNow;
            var nextRun = now.Date.AddHours(refreshHour);

            // If the target time today has already passed, schedule for tomorrow.
            if (nextRun <= now)
                nextRun = nextRun.AddDays(1);

            _logger.LogInformation(
                "DailyApiRefreshService: next API refresh scheduled at {NextRun:yyyy-MM-dd HH:mm} UTC.",
                nextRun);

            try
            {
                await Task.Delay(nextRun - DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _logger.LogInformation("DailyApiRefreshService: starting daily Cost Management API refresh.");

            try
            {
                // Invalidate so GetOrFetch re-queries instead of returning cached blobs.
                _apiService.InvalidateCache();

                // Sequential to respect the 5-req/min per-subscription rate limit.
                await _apiService.GetMainCostDataAsync(stoppingToken);
                await _apiService.GetRgCostDataAsync(stoppingToken);
                await _apiService.GetTagCostDataAsync(stoppingToken);

                _logger.LogInformation("DailyApiRefreshService: daily API refresh complete.");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DailyApiRefreshService: daily API refresh failed.");
                // Continue; next iteration will reschedule for the following day.
            }
        }
    }
}
