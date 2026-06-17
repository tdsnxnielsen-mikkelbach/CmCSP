namespace CmCSP.Models;

/// <summary>
/// The Advisor health score for one category on one subscription,
/// as returned by the Azure Advisor Score API.
/// Score is 0–100 (higher = healthier). Null means the API returned no data.
/// </summary>
public sealed class AdvisorCategoryScore
{
    public string SubscriptionId   { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;

    /// <summary>Lowercase category key: "cost", "security", "reliability", "operationalExcellence", "performance".</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Score 0–100. Null if the API returned no score for this category.</summary>
    public double? Score { get; set; }

    /// <summary>
    /// Number of active recommendation impact units for this category.
    /// 0 means no open recommendations (the category is fully healthy).
    /// </summary>
    public double ConsumptionUnits { get; set; }
}
