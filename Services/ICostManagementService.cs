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
}
