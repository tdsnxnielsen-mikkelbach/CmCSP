namespace CmCSP.Models;

public class CostManagementOptions
{
    public const string SectionName = "AzureCostManagement";

    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>All subscription IDs the service principal has Cost Management Reader access to.</summary>
    public List<string> SubscriptionIds { get; set; } = [];

    /// <summary>3-letter ISO 4217 currency code to normalise all costs into (e.g. "DKK").</summary>
    public string TargetCurrency { get; set; } = "DKK";

    /// <summary>
    /// Exchange rates relative to the target currency.
    /// Key = ISO currency code (e.g. "USD"), Value = how many TargetCurrency units equal 1 of that currency.
    /// Example: USD -> 6.89 means 1 USD = 6.89 DKK.
    /// </summary>
    public Dictionary<string, decimal> ExchangeRates { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 6.89m,
        ["EUR"] = 7.46m,
        ["GBP"] = 8.72m,
        ["SEK"] = 0.67m,
        ["NOK"] = 0.65m
    };

    /// <summary>How long to keep API results in memory before re-fetching. Default 60 minutes.</summary>
    public int CacheExpirationMinutes { get; set; } = 60;

    /// <summary>Monthly budget amount in TargetCurrency for the Budgets page.</summary>
    public decimal MonthlyBudget { get; set; } = 125_000m;

    /// <summary>Azure Cost Management REST API version to use.</summary>
    public string ApiVersion { get; set; } = "2025-03-01";
}
