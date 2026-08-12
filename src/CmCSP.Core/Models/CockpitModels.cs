namespace CmCSP.Models;

// ── Cockpit integration DTOs ──────────────────────────────────────────────────
// Pre-aggregated, identity-joinable shapes consumed by the external "Cockpit" app.
// Canonical join key across systems: TenantId = the Entra tenant GUID (matches the CSP
// customer's tenant and the MAU tenantId).

/// <summary>
/// One entry in the subscription directory: maps an Azure subscription to the customer /
/// Entra tenant that owns it, so callers can attribute Azure spend without every cost row
/// carrying the tenant. In single-tenant deployments <see cref="TenantId"/> resolves to the
/// CSP's own (home) tenant and <see cref="CustomerId"/> is the bootstrap home customer (0 when
/// no customer registry is provisioned).
/// </summary>
public sealed record SubscriptionDirectoryEntry(
    string SubscriptionId,
    string Name,
    string TenantId,
    long CustomerId);

/// <summary>
/// Per-subscription, per-month, per-service pre-aggregated cost. Amounts are provided in the
/// original billing currency (<see cref="Cost"/> / <see cref="Currency"/>) and converted to the
/// caller-requested currency (<see cref="NormalizedCost"/> / <see cref="NormalizedCurrency"/>),
/// which defaults to the configured target currency when the caller omits it.
/// </summary>
public sealed record MonthlyCostRow(
    string SubscriptionId,
    string TenantId,
    string Month,
    string ServiceName,
    decimal Cost,
    string Currency,
    decimal NormalizedCost,
    string NormalizedCurrency);

/// <summary>
/// Per-subscription, per-month reservation (RI/SP) coverage rollup derived from the existing
/// per-reservation figures. Drives the "covered" signal: how much reserved capacity was used
/// versus wasted in a month.
/// </summary>
public sealed record MonthlyReservationRow(
    string SubscriptionId,
    string Month,
    decimal UsedCost,
    decimal UnusedCost,
    decimal TotalCost,
    decimal UtilizationPct);
