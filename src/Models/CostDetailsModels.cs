namespace CmCSP.Models;

// ── Cost Details API request / response models ────────────────────────────────
// POST .../generateCostDetailsReport?api-version=2023-11-01
// Response: 202 Accepted with Location + Retry-After headers.
// Poll Location until 200 OK with CostDetailsOperationResult body.

/// <summary>Request body for the generateCostDetailsReport endpoint.</summary>
public sealed class CostDetailsReportRequest
{
    /// <summary>
    /// "ActualCost" — charges as billed (one-time reservation purchase appears on purchase date).
    /// "AmortizedCost" — reservation purchase cost spread evenly across the term; reveals
    /// Used vs Unused breakdown when combined with ChargeType filtering.
    /// </summary>
    public string Metric { get; set; } = "AmortizedCost";

    /// <summary>Date range for the report. Max one calendar month per request.</summary>
    public CostDetailsTimePeriod? TimePeriod { get; set; }
}

/// <summary>Start/end date range (inclusive, YYYY-MM-DD).</summary>
public sealed class CostDetailsTimePeriod
{
    public string Start { get; set; } = string.Empty;
    public string End   { get; set; } = string.Empty;
}

/// <summary>Body returned by the polling Location URL once the operation completes.</summary>
public sealed class CostDetailsOperationResult
{
    /// <summary>Running | Completed | Failed | NoDataFound</summary>
    public string? Status { get; set; }

    public CostDetailsOperationResultProperties? Properties { get; set; }

    public CostDetailsOperationError? Error { get; set; }
}

/// <summary>Completed operation result containing download links.</summary>
public sealed class CostDetailsOperationResultProperties
{
    /// <summary>List of time-limited blob download URLs.</summary>
    public List<CostDetailsBlobLink>? Blobs { get; set; }
}

/// <summary>A single blob download link and its size.</summary>
public sealed class CostDetailsBlobLink
{
    public string? BlobLink  { get; set; }
    public long    ByteCount { get; set; }
}

/// <summary>Error detail returned when Status = "Failed".</summary>
public sealed class CostDetailsOperationError
{
    public string? Code    { get; set; }
    public string? Message { get; set; }
}

// ── Reservation row (parsed from Cost Details AmortizedCost CSV) ──────────────

/// <summary>
/// Aggregated reservation cost row combining Used and Unused charges for a single
/// reservation over a billing period. Populated from the AmortizedCost CSV where
/// ChargeType ∈ { "Usage", "UnusedReservation" }.
///
/// Works at both scopes:
///   • Billing-account/customer scope — CustomerId/CustomerName are populated;
///     captures all reservations purchased for the customer regardless of subscription.
///   • Subscription scope — CustomerId/CustomerName are empty; shows only
///     reservations that applied to the queried subscription.
/// </summary>
public sealed class ReservationRow
{
    public string ReservationId   { get; set; } = string.Empty;
    public string ReservationName { get; set; } = string.Empty;

    /// <summary>Azure service type (e.g. "Virtual Machines", "SQL Database").</summary>
    public string MeterCategory   { get; set; } = string.Empty;

    /// <summary>Product / SKU name from the CSV ProductName column.</summary>
    public string ProductName     { get; set; } = string.Empty;

    /// <summary>Reservation term: "1Year", "3Years", or empty if unknown.</summary>
    public string Term            { get; set; } = string.Empty;

    public string SubscriptionId   { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;

    /// <summary>Billing customer ID. Only populated when fetched at billing-account scope.</summary>
    public string CustomerId   { get; set; } = string.Empty;

    /// <summary>Billing customer display name. Only populated when fetched at billing-account scope.</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>Billing currency code from the CSV (e.g. "CHF").</summary>
    public string Currency     { get; set; } = string.Empty;

    // ── Costs in original billing currency ────────────────────────────────────
    public decimal UsedCost   { get; set; }
    public decimal UnusedCost { get; set; }
    public decimal TotalCost  { get; set; }

    // ── Costs normalised to TargetCurrency ────────────────────────────────────
    public decimal NormalizedUsedCost   { get; set; }
    public decimal NormalizedUnusedCost { get; set; }
    public decimal NormalizedTotalCost  { get; set; }

    /// <summary>Used / Total × 100. Zero when TotalCost is zero.</summary>
    public decimal UtilizationPct =>
        TotalCost > 0 ? Math.Round(UsedCost / TotalCost * 100m, 1) : 0m;

    /// <summary>Year+month the report covers (day is always 1).</summary>
    public DateOnly Period { get; set; }

    /// <summary>"BillingAccount" or "Subscription".</summary>
    public string Scope { get; set; } = string.Empty;
}
