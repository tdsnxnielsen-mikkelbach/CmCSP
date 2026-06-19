namespace CmCSP.Models;

// ── Phase 7: Azure inventory & optimization models ────────────────────────────
// All sourced from ARM with the existing management token (Reader / Consumption read).

/// <summary>
/// A single live Azure resource as returned by Azure Resource Graph. Used to enrich cost
/// data with real inventory (counts, tags, regions) that cost CSVs alone cannot provide.
/// </summary>
public sealed record ResourceInventoryItem(
    string Id,
    string Name,
    string Type,
    string ResourceGroup,
    string Location,
    string SubscriptionId,
    IReadOnlyDictionary<string, string> Tags)
{
    /// <summary>True when the resource carries no tags (a chargeback/governance gap).</summary>
    public bool IsUntagged => Tags.Count == 0;
}

/// <summary>
/// Tag-coverage rollup for the chargeback page: total live resources, how many are untagged, and a
/// per-tag-key resource count. Lets the UI show governance gaps next to cost-by-tag data.
/// </summary>
public sealed record InventoryTagCoverage(
    int TotalResources,
    int UntaggedResources,
    IReadOnlyDictionary<string, int> ResourcesPerTagKey)
{
    /// <summary>Percentage of resources that carry at least one tag (0–100).</summary>
    public double TaggedPercent =>
        TotalResources == 0 ? 0 : Math.Round((TotalResources - UntaggedResources) * 100.0 / TotalResources, 1);
}

/// <summary>
/// A resource flagged as wasteful — unattached, idle or stopped-but-allocated — with a
/// human-readable reason. Surfaced on the Optimization page as a "delete this / right-size"
/// savings list.
/// </summary>
public sealed record OrphanedResource(
    string Id,
    string Name,
    string Type,
    string ResourceGroup,
    string Location,
    string SubscriptionId,
    string Reason);

/// <summary>
/// A reservation / savings-plan purchase recommendation from Microsoft.Consumption, normalised
/// to the configured TargetCurrency. <see cref="NormalizedNetSavings"/> is the estimated saving
/// over the look-back window in the recommended term.
/// </summary>
public sealed record ReservationPurchaseRecommendation(
    string SubscriptionId,
    string ResourceType,
    string Sku,
    string Term,
    string Scope,
    string LookBackPeriod,
    decimal RecommendedQuantity,
    decimal NetSavings,
    decimal NormalizedNetSavings,
    string Currency);

/// <summary>
/// An existing reservation order with its term and <see cref="ExpiryDate"/> so customers can be
/// warned before a reservation lapses. Sourced from Microsoft.Capacity/reservationOrders.
/// </summary>
public sealed record ReservationOrderInfo(
    string OrderId,
    string DisplayName,
    string Term,
    string ProvisioningState,
    DateTime? ExpiryDate,
    int Quantity)
{
    /// <summary>Days until expiry (negative when already expired); null when no expiry date.</summary>
    public int? DaysUntilExpiry =>
        ExpiryDate is { } d ? (int)Math.Floor((d.Date - DateTime.UtcNow.Date).TotalDays) : null;
}
