namespace CmCSP.Models;

/// <summary>
/// A statistically significant cost spike detected for a single subscription + service on a
/// given day, relative to a trailing baseline. Computed in-process from cost rows already in
/// the durable store / cache — no additional API call.
/// </summary>
public sealed record CostAnomaly(
    string SubscriptionId,
    string SubscriptionName,
    string ServiceName,
    DateTime Date,
    decimal Cost,
    decimal Baseline,
    decimal DeltaPct,
    double ZScore);
