namespace CmCSP.Models;

/// <summary>Shared record types used across multiple page chart visualizations.</summary>
public static class ChartData
{
    /// <summary>Monthly cost point grouped by label and subscription (Home, TrendAndForecast, Budgets).</summary>
    public record MonthlyPoint(string Label, string SubscriptionId, decimal Cost);
}
