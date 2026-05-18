using CmCSP.Models;

namespace CmCSP.Services;

public interface ICostManagementService
{
    /// <summary>Daily costs grouped by SubscriptionName + MeterCategory (service).</summary>
    Task<List<CostRow>> GetMainCostDataAsync(CancellationToken ct = default);

    /// <summary>Daily costs grouped by SubscriptionName + ResourceGroupName.</summary>
    Task<List<CostRow>> GetRgCostDataAsync(CancellationToken ct = default);

    /// <summary>Daily costs grouped by SubscriptionName + TagKey.</summary>
    Task<List<CostRow>> GetTagCostDataAsync(CancellationToken ct = default);

    /// <summary>Removes all cached results so the next call re-fetches from the API.</summary>
    void InvalidateCache();

    /// <summary>
    /// Returns budgets defined at the subscription scope for all configured subscriptions.
    /// Only subscriptions that have at least one budget are included in the result.
    /// Amounts are normalised to the configured TargetCurrency.
    /// </summary>
    Task<List<SubscriptionBudget>> GetSubscriptionBudgetsAsync(CancellationToken ct = default);
}
