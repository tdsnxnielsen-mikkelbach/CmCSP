using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Provides reservation and amortized-cost data via the Azure Cost Details API
/// (generateCostDetailsReport). Supports both billing-account/customer scope (MCA/CSP)
/// and subscription scope so callers receive the best available data.
/// </summary>
public interface ICostDetailsService
{
    /// <summary>
    /// Whether billing-account/customer scope is configured.
    /// When false, only subscription-scope queries are available.
    /// </summary>
    bool HasBillingAccountAccess { get; }

    // ── Reservation data (AmortizedCost, per-reservation Used/Unused breakdown) ─

    /// <summary>
    /// Fetches reservation cost breakdown for a specific CSP customer at
    /// billing-account/customer scope. Returns null when
    /// <see cref="HasBillingAccountAccess"/> is false.
    /// </summary>
    Task<List<ReservationRow>?> GetCustomerReservationsAsync(
        string customerId, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>
    /// Fetches reservation cost breakdown for a single subscription.
    /// Works without billing-account access; shows only reservations that applied
    /// to the queried subscription.
    /// </summary>
    Task<List<ReservationRow>> GetSubscriptionReservationsAsync(
        string subscriptionId, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>
    /// Fetches reservation data for all configured customers (billing-account scope).
    /// Returns an empty list when <see cref="HasBillingAccountAccess"/> is false.
    /// </summary>
    Task<List<ReservationRow>> GetAllCustomerReservationsAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>
    /// Fetches reservation data for all configured subscriptions (subscription scope).
    /// Always available regardless of billing-account configuration.
    /// </summary>
    Task<List<ReservationRow>> GetAllSubscriptionReservationsAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>Removes all Cost Details cached results so the next call re-fetches.</summary>
    void InvalidateCache();
}
