namespace CmCSP.Models;

// ── Ion enrichment view models ────────────────────────────────────────────────
// Canonical, UI/API-ready shapes produced by IonEnrichmentService by composing the Ion Gateway
// and PCT feeds with CmCSP's own data. Each row is explicit about which side a value came from
// (native/Microsoft list price vs Ion transacted cost/margin) so the UI can label the source.

/// <summary>
/// A merged reseller directory row. Resellers are name-matched across the Ion master-data
/// directory and the Partner Center reseller list; either <see cref="IonAccountId"/> or
/// <see cref="PartnerCenterId"/> (or both) is present. <see cref="Source"/> records where it was seen.
/// </summary>
public sealed record IonResellerSummary(
    string Name,
    long? IonAccountId,
    string? PartnerCenterId,
    string? MpnId,
    string? PrimaryContactEmail,
    bool DelegatedAdminEnabled,
    int CustomerCount,
    string Source);

/// <summary>
/// One customer's subscription with both the native Microsoft list price and the Ion transacted
/// cost/margin, plus the match provenance. This is the row behind the margin/pricing views.
/// </summary>
public sealed record CustomerMarginLine(
    string? SubscriptionId,
    string? OfferName,
    string? SkuTitle,
    string? SkuPartNumber,
    int? Quantity,
    string? Status,
    decimal? UnitListPrice,       // native — Microsoft (PCT)
    string? ListPriceCurrency,    // native
    decimal? IonCost,             // Ion — per unit
    decimal? IonMarginPct,        // Ion — gross margin on sell price, %
    decimal? IonUnitMargin,       // Ion — per unit
    decimal? IonLineMargin,       // Ion — total for the matched order line
    string? IonCurrency,          // Ion
    string PricingSource,         // "ion-reseller-sku" | "none"
    string? MatchedOn);           // "mfgPartNumber" | "catalogItemId" | null

/// <summary>
/// A customer's fused pricing plus a rolled-up margin summary. <see cref="ResellerMatched"/> = false
/// means no Ion account resolved for the reseller, so the Ion side is empty (list price only).
/// </summary>
public sealed record CustomerMarginSummary(
    string? TenantId,
    string? Domain,
    string? DisplayName,
    string? ResellerName,
    long? IonAccountId,
    bool ResellerMatched,
    int SubscriptionCount,
    int MatchedCount,
    decimal TotalListPrice,       // native
    decimal TotalIonCost,         // Ion
    decimal TotalIonMargin,       // Ion (sum of unit margins × qty where matched)
    decimal? BlendedMarginPct,    // Ion — TotalIonMargin / (TotalIonCost + TotalIonMargin)
    string Currency,
    List<CustomerMarginLine> Lines);

/// <summary>
/// Ion buy price/margin attached to an Azure/CSP subscription by tenant. CmCSP joins this to its
/// native <c>CostFact</c> rows at the subscription/plan level (not per Azure meter): the Ion cost
/// and margin are the reseller's transacted figures for the plan the tenant holds.
/// </summary>
public sealed record TenantPlanMargin(
    string TenantId,
    string? Domain,
    string? PlanName,
    string? MfgPartNumber,
    decimal? IonCost,             // per unit
    decimal? IonPrice,            // per unit
    decimal? IonMargin,           // total line margin (absolute)
    decimal? IonMarginPct,
    string? Currency);
