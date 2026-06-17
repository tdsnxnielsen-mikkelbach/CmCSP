namespace CmCSP.Services;

using CmCSP.Models;

/// <summary>
/// Reconciles export provisioning once on startup for all active subscriptions.
/// This covers subscriptions already present in configuration or persisted from prior UI adds.
/// </summary>
public sealed class SubscriptionExportReconcileService : BackgroundService
{
    private readonly SubscriptionStoreService _subscriptionStore;
    private readonly ExportProvisioningService _provisioner;
    private readonly CostManagementOptions _options;
    private readonly ILogger<SubscriptionExportReconcileService> _logger;

    public SubscriptionExportReconcileService(
        SubscriptionStoreService subscriptionStore,
        ExportProvisioningService provisioner,
        CostManagementOptions options,
        ILogger<SubscriptionExportReconcileService> logger)
    {
        _subscriptionStore = subscriptionStore;
        _provisioner = provisioner;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ExportBlob.Enabled)
        {
            _logger.LogDebug("SubscriptionExportReconcileService: skipped because ExportBlob mode is disabled.");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var correlationId = $"startup-{Guid.NewGuid():N}";
        var subscriptionIds = _subscriptionStore.AllIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subscriptionIds.Count == 0)
        {
            _logger.LogInformation("SubscriptionExportReconcileService[{CorrelationId}]: no active subscriptions to reconcile.", correlationId);
            return;
        }

        _logger.LogInformation(
            "SubscriptionExportReconcileService[{CorrelationId}]: reconciling export provisioning for {Count} subscription(s).",
            correlationId, subscriptionIds.Count);

        foreach (var subscriptionId in subscriptionIds)
        {
            try
            {
                var result = await _provisioner.ProvisionAsync(subscriptionId, correlationId, stoppingToken);
                if (result.Succeeded)
                {
                    _logger.LogInformation(
                        "SubscriptionExportReconcileService[{CorrelationId}]: {SubId} reconciled successfully via export '{ExportName}'.",
                        correlationId, subscriptionId, result.ExportName);
                    continue;
                }

                if (result.Skipped)
                {
                    _logger.LogInformation(
                        "SubscriptionExportReconcileService[{CorrelationId}]: skipped {SubId} - {Message}",
                        correlationId, subscriptionId, result.Message);
                    continue;
                }

                _logger.LogWarning(
                    "SubscriptionExportReconcileService[{CorrelationId}]: reconciliation for {SubId} did not complete: {Message}",
                    correlationId, subscriptionId, result.Message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "SubscriptionExportReconcileService[{CorrelationId}]: reconciliation failed for {SubId}.",
                    correlationId, subscriptionId);
            }
        }

        _logger.LogInformation("SubscriptionExportReconcileService[{CorrelationId}]: startup reconciliation complete.", correlationId);
    }
}
