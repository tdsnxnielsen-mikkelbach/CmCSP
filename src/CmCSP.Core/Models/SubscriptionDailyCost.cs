namespace CmCSP.Models;

public sealed record SubscriptionDailyCost(
    DateTime Date,
    string SubscriptionId,
    string SubscriptionName,
    long CustomerId,
    string TenantId,
    decimal NormalizedCost);
