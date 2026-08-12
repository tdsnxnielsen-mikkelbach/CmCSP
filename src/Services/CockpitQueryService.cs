using System.Globalization;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Builds the pre-aggregated, identity-joinable views consumed by the external "Cockpit" app:
/// a subscription → customer/tenant directory, monthly per-service cost with a caller-chosen
/// currency, and a monthly RI/SP coverage rollup.
///
/// It composes the existing cost/reservation services (which own fetching and caching) and the
/// <see cref="CustomerStore"/> ownership registry; it holds no cache of its own and never calls
/// the source feed or storage directly.
/// </summary>
public sealed class CockpitQueryService(
    ICostManagementService costService,
    ICostDetailsService costDetailsService,
    CustomerStore customers,
    CostManagementOptions options,
    ILogger<CockpitQueryService> logger)
{
    /// <summary>
    /// Returns the subscription directory: one entry per configured subscription with its display
    /// name and the customer / Entra tenant that owns it. Every entry carries a non-empty
    /// <see cref="SubscriptionDirectoryEntry.TenantId"/> (the CSP home tenant in single-tenant
    /// deployments), so a subscription can always be mapped to a customer.
    /// </summary>
    public async Task<List<SubscriptionDirectoryEntry>> GetSubscriptionDirectoryAsync(CancellationToken ct = default)
    {
        var names  = await costService.GetSubscriptionDisplayNamesAsync(ct);
        var owners = await customers.GetSubscriptionOwnersAsync(ct);
        var (homeId, homeTenant) = await ResolveHomeOwnerAsync(ct);

        return names
            .Select(kv =>
            {
                var (tenantId, customerId) = ResolveOwner(kv.Key, owners, homeId, homeTenant);
                return new SubscriptionDirectoryEntry(kv.Key, kv.Value, tenantId, customerId);
            })
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns per-subscription, per-month, per-service cost for the inclusive month range
    /// <paramref name="from"/>..<paramref name="to"/> (each "yyyy-MM"; defaults to the last 12
    /// months through the current month). Optionally filtered to a single subscription. Normalised
    /// amounts are converted to <paramref name="currency"/> (defaults to the configured target
    /// currency).
    /// </summary>
    public async Task<List<MonthlyCostRow>> GetMonthlyCostAsync(
        string? from, string? to, string? subscriptionId, string? currency, CancellationToken ct = default)
    {
        var (fromKey, toKey) = ResolveMonthRange(from, to);
        var targetCurrency = string.IsNullOrWhiteSpace(currency)
            ? options.TargetCurrency
            : currency.Trim().ToUpperInvariant();

        var rows   = await costService.GetMainCostDataAsync(ct);
        var owners = await customers.GetSubscriptionOwnersAsync(ct);
        var (homeId, homeTenant) = await ResolveHomeOwnerAsync(ct);

        var filtered = rows.Where(r =>
        {
            if (!string.IsNullOrWhiteSpace(subscriptionId) &&
                !r.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))
                return false;
            var key = MonthKey(r.Date);
            return key >= fromKey && key <= toKey;
        });

        return filtered
            .GroupBy(r => new
            {
                r.SubscriptionId,
                Month = MonthKey(r.Date),
                r.ServiceName,
                r.Currency
            })
            .Select(g =>
            {
                var (tenantId, _) = ResolveOwner(g.Key.SubscriptionId, owners, homeId, homeTenant);
                var normalizedTarget = g.Sum(r => r.NormalizedCost);
                return new MonthlyCostRow(
                    SubscriptionId:     g.Key.SubscriptionId,
                    TenantId:           tenantId,
                    Month:              FormatMonth(g.Key.Month),
                    ServiceName:        g.Key.ServiceName,
                    Cost:               g.Sum(r => r.Cost),
                    Currency:           g.Key.Currency,
                    NormalizedCost:     ConvertFromTarget(normalizedTarget, targetCurrency),
                    NormalizedCurrency: targetCurrency);
            })
            .OrderBy(r => r.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Month, StringComparer.Ordinal)
            .ThenBy(r => r.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns a per-subscription, per-month RI/SP coverage rollup for the inclusive month range
    /// (defaults to the current month), aggregating the existing per-reservation Used/Unused/Total
    /// figures. Optionally filtered to a single subscription.
    /// </summary>
    public async Task<List<MonthlyReservationRow>> GetMonthlyReservationsAsync(
        string? from, string? to, string? subscriptionId, CancellationToken ct = default)
    {
        var (fromKey, toKey) = ResolveMonthRange(from, to, defaultMonthsBack: 0);
        var fromDate = new DateOnly(fromKey / 100, fromKey % 100, 1);
        var toDate   = new DateOnly(toKey / 100, toKey % 100, 1).AddMonths(1).AddDays(-1);

        var rows = await costDetailsService.GetAllSubscriptionReservationsAsync(fromDate, toDate, ct);

        var filtered = rows.Where(r =>
            string.IsNullOrWhiteSpace(subscriptionId) ||
            r.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase));

        return filtered
            .GroupBy(r => new { r.SubscriptionId, Month = r.Period.ToString("yyyy-MM", CultureInfo.InvariantCulture) })
            .Select(g =>
            {
                var used  = g.Sum(r => r.UsedCost);
                var total = g.Sum(r => r.TotalCost);
                return new MonthlyReservationRow(
                    SubscriptionId: g.Key.SubscriptionId,
                    Month:          g.Key.Month,
                    UsedCost:       used,
                    UnusedCost:     g.Sum(r => r.UnusedCost),
                    TotalCost:      total,
                    UtilizationPct: total > 0 ? Math.Round(used / total * 100m, 1) : 0m);
            })
            .OrderBy(r => r.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Month, StringComparer.Ordinal)
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>The bootstrap home customer (id + tenant), falling back to the configured home tenant.</summary>
    private async Task<(long CustomerId, string TenantId)> ResolveHomeOwnerAsync(CancellationToken ct)
    {
        var home = await customers.GetHomeCustomerAsync(ct);
        return home is null
            ? (0L, customers.HomeTenantId)
            : (home.Id, string.IsNullOrWhiteSpace(home.TenantId) ? customers.HomeTenantId : home.TenantId);
    }

    private static (string TenantId, long CustomerId) ResolveOwner(
        string subscriptionId,
        IReadOnlyDictionary<string, (long CustomerId, string TenantId)> owners,
        long homeId,
        string homeTenant) =>
        owners.TryGetValue(subscriptionId, out var owner) && !string.IsNullOrWhiteSpace(owner.TenantId)
            ? (owner.TenantId, owner.CustomerId)
            : (homeTenant, homeId);

    /// <summary>Converts an amount already expressed in the target currency into <paramref name="toCurrency"/>.</summary>
    private decimal ConvertFromTarget(decimal amountInTarget, string toCurrency)
    {
        if (string.IsNullOrWhiteSpace(toCurrency) ||
            toCurrency.Equals(options.TargetCurrency, StringComparison.OrdinalIgnoreCase))
            return amountInTarget;

        // ExchangeRates[X] = target-currency units per 1 X, so target → X divides by the rate.
        if (options.ExchangeRates.TryGetValue(toCurrency, out var rate) && rate != 0m)
            return amountInTarget / rate;

        logger.LogWarning(
            "No exchange rate configured for currency '{Currency}'. Returning target-currency amount unconverted.",
            toCurrency);
        return amountInTarget;
    }

    private static int MonthKey(DateTime date) => date.Year * 100 + date.Month;

    private static string FormatMonth(int monthKey) =>
        $"{monthKey / 100:D4}-{monthKey % 100:D2}";

    /// <summary>
    /// Parses the "yyyy-MM" range into inclusive yyyyMM integer keys. Missing bounds default to a
    /// window ending in the current month; <paramref name="defaultMonthsBack"/> sets its width.
    /// </summary>
    private static (int FromKey, int ToKey) ResolveMonthRange(string? from, string? to, int defaultMonthsBack = 11)
    {
        var now = DateTime.UtcNow;
        var defaultTo   = now.Year * 100 + now.Month;
        var defaultFromDate = new DateTime(now.Year, now.Month, 1).AddMonths(-defaultMonthsBack);
        var defaultFrom = defaultFromDate.Year * 100 + defaultFromDate.Month;

        var fromKey = ParseMonth(from) ?? defaultFrom;
        var toKey   = ParseMonth(to)   ?? defaultTo;
        return fromKey <= toKey ? (fromKey, toKey) : (toKey, fromKey);
    }

    private static int? ParseMonth(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(value.Trim(), "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            return parsed.Year * 100 + parsed.Month;
        return null;
    }
}
