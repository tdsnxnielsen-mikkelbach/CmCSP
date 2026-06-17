namespace CmCSP.Models;

/// <summary>
/// A single Azure Advisor Cost recommendation fetched from the Advisor REST API
/// and normalised to the configured TargetCurrency.
/// </summary>
public sealed class AdvisorRecommendation
{
    public string SubscriptionId   { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;

    /// <summary>Recommendation impact: "High", "Medium", or "Low".</summary>
    public string Impact { get; set; } = string.Empty;

    /// <summary>Affected ARM resource type, e.g. "Microsoft.Compute/virtualMachines".</summary>
    public string ImpactedField { get; set; } = string.Empty;

    /// <summary>Affected resource name.</summary>
    public string ImpactedValue { get; set; } = string.Empty;

    /// <summary>Human-readable description of the problem.</summary>
    public string Problem { get; set; } = string.Empty;

    /// <summary>Human-readable recommended action.</summary>
    public string Solution { get; set; } = string.Empty;

    /// <summary>Estimated annual saving in the currency returned by the Advisor API.</summary>
    public decimal AnnualSavingsAmount { get; set; }

    /// <summary>ISO 4217 currency code of <see cref="AnnualSavingsAmount"/>.</summary>
    public string SavingsCurrency { get; set; } = string.Empty;

    /// <summary>Annual saving normalised to the configured TargetCurrency.</summary>
    public decimal NormalizedAnnualSavings { get; set; }

    /// <summary>Full ARM resource ID of the affected resource.</summary>
    public string ResourceId { get; set; } = string.Empty;
}
