using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>Per-dataset row counts returned by a collection run.</summary>
public readonly record struct CostCollectionResult(int Main, int Rg, int Tag);

public interface ICostManagementService
{
    /// <summary>Daily costs grouped by SubscriptionName + MeterCategory (service).</summary>
    Task<List<CostRow>> GetMainCostDataAsync(CancellationToken ct = default);

    /// <summary>Daily costs grouped by SubscriptionName + ResourceGroupName.</summary>
    Task<List<CostRow>> GetRgCostDataAsync(CancellationToken ct = default);

    /// <summary>Daily costs grouped by SubscriptionName + TagKey.</summary>
    Task<List<CostRow>> GetTagCostDataAsync(CancellationToken ct = default);

    /// <summary>
    /// Daily costs grouped by SubscriptionName + MeterCategory using the AmortizedCost metric.
    /// Reservation purchase cost is spread evenly over the term, giving a smoother trend line.
    /// Always fetched from the Query API (not blob exports) since exports use ActualCost only.
    /// </summary>
    Task<List<CostRow>> GetAmortizedMainCostDataAsync(CancellationToken ct = default);

    /// <summary>Removes all cached results so the next call re-fetches from the API.</summary>
    void InvalidateCache();

    /// <summary>
    /// Ingests the latest cost data from the source feed into the durable store and warms the
    /// shared cache. For blob-export mode with the SQL data platform enabled this re-parses the
    /// export CSVs and upserts the aggregated rows into <c>CostFact</c>; otherwise it invalidates
    /// and re-fetches from the Query API. Returns the per-dataset row counts. Called by the
    /// CostCollectorJob (nightly + on-demand).
    /// </summary>
    Task<CostCollectionResult> RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns budgets defined at the subscription scope for all configured subscriptions.
    /// Only subscriptions that have at least one budget are included in the result.
    /// Amounts are normalised to the configured TargetCurrency.
    /// </summary>
    Task<List<SubscriptionBudget>> GetSubscriptionBudgetsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns Azure Advisor Cost recommendations for all configured subscriptions.
    /// Only the Cost category is fetched (right-sizing, idle resources, reserved instances, etc.).
    /// Annual saving amounts are normalised to the configured TargetCurrency.
    /// </summary>
    Task<List<AdvisorRecommendation>> GetAdvisorRecommendationsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the Advisor health scores for all five categories (Cost, Security, Reliability,
    /// Operational Excellence, Performance) for all configured subscriptions.
    /// One record per subscription per category; aggregate in the UI as needed.
    /// </summary>
    Task<List<AdvisorCategoryScore>> GetAdvisorScoresAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a dictionary mapping subscription ID → display name for all configured subscriptions.
    /// Falls back to the raw ID if the Subscriptions API is unavailable. Results are cached.
    /// </summary>
    Task<Dictionary<string, string>> GetSubscriptionDisplayNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the Microsoft Cost Management native forecast for the current calendar month,
    /// aggregated across all configured subscriptions. Each point is flagged
    /// <see cref="ForecastPoint.IsForecast"/> = false for actual days and true for projected days.
    /// Uses the same ARM token (no new permissions). Empty when the forecast API has no data
    /// (e.g. 204 No Content). <paramref name="metric"/> is "ActualCost" or "AmortizedCost".
    /// </summary>
    Task<List<ForecastPoint>> GetForecastAsync(string metric = "ActualCost", CancellationToken ct = default);

    /// <summary>
    /// Returns month-to-date spend split by PublisherType (Azure first-party vs Azure
    /// Marketplace / third-party) and service, across all configured subscriptions, using the
    /// same ARM token. Empty when the Query API is unavailable or the dimension is unsupported
    /// (e.g. some CSP/indirect subscriptions).
    /// </summary>
    Task<List<PublisherTypeCostRow>> GetPublisherBreakdownAsync(CancellationToken ct = default);
}
