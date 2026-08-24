namespace CmCSP.Models;

// ── Partner Center Transfer (PCT) DTOs ────────────────────────────────────────
// Shapes returned by the standalone PCT API (ca-pct.…/api/v1/*). Native fields come from
// Microsoft Partner Center (list price, SKU identity); the ion* fields are decorated by the PCT
// service from the Ion Gateway (cost/margin) and are omitted when unmatched. Property names match
// the PCT camelCase payload (deserialised case-insensitively).

/// <summary>An indirect reseller linked to the partner account (Partner Center).</summary>
public sealed record PctIndirectReseller(
    string? Id,
    string? MpnId,
    string? Name);

/// <summary>A customer managed by an indirect reseller. <see cref="Id"/> is the Entra tenant GUID.</summary>
public sealed record PctCustomer(
    string? Id,
    string? Name,
    string? Domain,
    DateTimeOffset? LastSyncedAt);

/// <summary>
/// A Microsoft CSP subscription with native Partner Center list price + SKU identity, decorated
/// with Ion cost/margin. <see cref="IonPricingSource"/> is <c>ion-reseller-sku</c> / <c>none</c> and
/// <see cref="IonMatchedOn"/> is <c>mfgPartNumber</c> / <c>catalogItemId</c> / null — check them
/// before trusting the Ion side (reseller-level, SKU-approximate). Ion fields are null when unmatched.
/// </summary>
public sealed record PctSubscription(
    string? Id,
    string? FriendlyName,
    string? OfferName,
    string? OfferId,
    string? SkuTitle,
    string? ProductType,
    string? Status,
    string? ProductId,
    string? SkuId,
    int? Quantity,
    string? BillingCycle,
    string? TermDuration,
    string? CommitmentEndDate,
    string? EffectiveStartDate,
    bool? AutoRenewEnabled,
    bool? IsTrial,
    string? CatalogItemId,
    string? SkuPartNumber,
    decimal? UnitListPrice,
    string? ListPriceCurrency,
    DateTimeOffset? LastSyncedAt,
    // ── Ion Gateway enrichment (per unit unless noted) ────────────────────────
    decimal? IonCost,
    decimal? IonUnitMargin,
    decimal? IonMarginPct,
    decimal? IonLineMargin,
    string? IonCurrency,
    string? IonPricingSource,
    string? IonMatchedOn);

/// <summary>One reseller customer with all of its enriched subscriptions (paged reseller view).</summary>
public sealed record PctResellerCustomerSubscriptions(
    string? CustomerId,
    string? CustomerName,
    string? Domain,
    List<PctSubscription> Subscriptions,
    DateTimeOffset? LastSyncedAt);
