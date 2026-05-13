namespace CmCSP.Models;

/// <summary>
/// A single aggregated cost row after parsing the Cost Management API response
/// and normalising the currency to the configured TargetCurrency.
/// </summary>
public sealed class CostRow
{
    /// <summary>UTC date this cost was incurred (granularity = Daily).</summary>
    public DateTime Date { get; set; }

    /// <summary>Original cost in the subscription's billing currency.</summary>
    public decimal Cost { get; set; }

    /// <summary>ISO 4217 billing currency returned by the API (e.g. "USD").</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Cost converted to the configured TargetCurrency using the exchange rate table.</summary>
    public decimal NormalizedCost { get; set; }

    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;

    // Populated by the ByService query
    public string ServiceName { get; set; } = string.Empty;

    // Populated by the ByResourceGroup query
    public string ResourceGroupName { get; set; } = string.Empty;

    // Populated by the ByTag query
    public string Tag { get; set; } = string.Empty;
}
