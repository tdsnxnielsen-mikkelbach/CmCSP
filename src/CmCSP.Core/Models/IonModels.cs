using System.Text.Json.Serialization;

namespace CmCSP.Models;

// ── Ion Gateway DTOs ──────────────────────────────────────────────────────────
// Shapes returned by the TD SYNNEX Ion Gateway (gateway.…/api/v1/*). All money is per-unit
// and rounded to 2 decimals unless noted; JSON drops trailing zeros. Property names match the
// gateway's camelCase payload (deserialised case-insensitively). Nullable throughout so a
// partial/rolling upstream payload never breaks binding.

/// <summary>
/// One customer in the gateway bootstrap directory (Ion + PCT). Used to learn every addressable
/// customer's Entra tenant GUID and domain before enriching — the seed for onboarding customers
/// CmCSP does not hold natively. <see cref="Source"/> is <c>ion</c> (direct customers) or
/// <c>pct</c> (indirect-reseller customers).
/// </summary>
public sealed record IonDirectoryCustomer(
    string? TenantId,
    string? Domain,
    string? Name,
    string? Source,
    long? IonAccountId,
    string? ResellerName);

/// <summary>A single page of <see cref="IonDirectoryCustomer"/> plus the continuation token.</summary>
public sealed record IonCustomerDirectoryPage(
    List<IonDirectoryCustomer> Items,
    string? NextPageToken);

/// <summary>
/// A TD SYNNEX indirect reseller from Ion master data. <see cref="AccountId"/> is the join key to
/// a reseller's Ion customers and crawled orders.
/// </summary>
public sealed record IonReseller(
    long AccountId,
    string? Name,
    string? PrimaryContactEmail,
    bool DelegatedAdminEnabled);

/// <summary>
/// One crawled order line with transacted per-unit pricing. <see cref="Margin"/> here is the
/// <b>total line margin</b> ((price − cost) × quantity), an absolute amount — not a percentage.
/// </summary>
public sealed record IonOrderLine(
    string? ProductId,
    string? SkuId,
    string? MfgPartNumber,
    int? Quantity,
    decimal? Price,
    decimal? Cost,
    decimal? Msrp,
    decimal? Margin);

/// <summary>
/// A crawled reseller order: header totals plus <see cref="Lines"/>. Sourced from the nightly
/// Ion <c>/orders</c> crawl, so it reflects realised buy prices/margins per SKU for a reseller.
/// </summary>
public sealed record IonOrder(
    string? OrderId,
    long? CustomerId,
    string? CustomerName,
    DateTimeOffset? OrderDate,
    decimal? Total,
    decimal? Margin,
    string? CurrencyCode,
    List<IonOrderLine> Lines);

/// <summary>
/// A held subscription/plan with Ion price/cost/margin/msrp. <see cref="Margin"/> is the total
/// line margin (absolute), <see cref="Price"/>/<see cref="Cost"/>/<see cref="Msrp"/> are per-unit.
/// Key on <see cref="MfgPartNumber"/> — term and auto-renew are encoded in it.
/// </summary>
public sealed record IonSubscriptionDto(
    string? SubscriptionId,
    string? Name,
    int? CloudProviderId,
    string? Vendor,
    string? MfgPartNumber,
    string? CcpProductId,
    string? CcpSkuId,
    string? UnitType,
    string? Status,
    string? BillingCycle,
    string? BillingTerm,
    bool? IsTrial,
    bool? AutoRenew,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate,
    DateTimeOffset? RenewalDate,
    decimal? Price,
    decimal? Cost,
    decimal? Margin,
    decimal? Msrp,
    decimal? Total,
    string? Currency);

/// <summary>Body for the bulk subscriptions call (<c>POST /customers/subscriptions</c>, ≤ 500 keys).</summary>
public sealed record IonSubscriptionsBatchRequest(
    [property: JsonPropertyName("keys")] List<string> Keys,
    [property: JsonPropertyName("vendor")] string? Vendor);

/// <summary>One customer's resolved subscriptions in a bulk response.</summary>
public sealed record IonCustomerSubscriptions(
    string? Key,
    string? TenantId,
    string? Domain,
    List<IonSubscriptionDto> Subscriptions);

/// <summary>Bulk subscriptions response: resolved customers plus the keys that did not resolve.</summary>
public sealed record IonSubscriptionsBatchResponse(
    List<IonCustomerSubscriptions> Customers,
    List<string> NotFound);

/// <summary>
/// One fused subscription row: Partner Center list price (<see cref="UnitListPrice"/>) plus Ion
/// transacted cost/margin. <see cref="PricingSource"/> is <c>ion-reseller-sku</c> (Ion matched) or
/// <c>none</c> (list price only); <see cref="MatchedOn"/> is <c>mfgPartNumber</c> / <c>catalogItemId</c> /
/// null. Always check these before trusting the Ion side — it is reseller-level and SKU-approximate.
/// </summary>
public sealed record FusedPricingLineDto(
    string? SubscriptionId,
    string? OfferName,
    string? SkuTitle,
    string? ProductId,
    string? SkuId,
    string? CatalogItemId,
    string? SkuPartNumber,
    int? Quantity,
    string? Status,
    decimal? UnitListPrice,
    string? ListPriceCurrency,
    decimal? IonPrice,
    decimal? IonCost,
    decimal? IonMsrp,
    decimal? IonUnitMargin,
    decimal? IonLineMargin,
    decimal? IonMarginPct,
    string? IonCurrency,
    string? PricingSource,
    string? MatchedOn);

/// <summary>
/// A customer's fused pricing: PCT list price + Ion cost/margin per subscription line.
/// <see cref="ResellerMatched"/> = false means the PCT reseller name did not resolve to an Ion
/// account, so no Ion pricing is present.
/// </summary>
public sealed record FusedPricingDto(
    string? CustomerId,
    string? Domain,
    string? ResellerName,
    long? IonAccountId,
    bool ResellerMatched,
    List<FusedPricingLineDto> Lines,
    DateTimeOffset? SyncedAt,
    DateTimeOffset? EnrichedAt);

/// <summary>Body for the bulk fused-pricing call (<c>POST /pct/customers/pricing</c>, ≤ 500 keys).</summary>
public sealed record FusedPricingBatchRequest(
    [property: JsonPropertyName("keys")] List<string> Keys);

/// <summary>Bulk fused-pricing response: resolved customers plus the keys that did not resolve.</summary>
public sealed record FusedPricingBatchResponse(
    List<FusedPricingDto> Customers,
    List<string> NotFound);
