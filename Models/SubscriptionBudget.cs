namespace CmCSP.Models;

/// <summary>
/// A subscription-scope budget fetched from the Azure Consumption Budgets API.
/// Amounts are normalised to the configured TargetCurrency.
/// </summary>
public sealed record SubscriptionBudget(
    string SubscriptionId,
    string BudgetName,
    /// <summary>Budget limit in TargetCurrency.</summary>
    decimal Amount,
    /// <summary>Amount spent in the current budget period, in TargetCurrency.</summary>
    decimal CurrentSpend,
    /// <summary>e.g. "Monthly", "Quarterly", "Annually".</summary>
    string TimeGrain,
    /// <summary>Original billing currency returned by the API before normalisation.</summary>
    string OriginalCurrency
);
