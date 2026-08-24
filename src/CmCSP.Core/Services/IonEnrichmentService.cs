using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Orchestrates the two upstream integrations (<see cref="IonGatewayService"/> and
/// <see cref="PartnerCenterService"/>) with CmCSP's own customer registry to produce the
/// UI/API-ready enrichment views:
/// <list type="bullet">
///   <item>a merged reseller directory (Ion master data + Partner Center indirect resellers);</item>
///   <item>per-customer margin summaries (Microsoft list price + Ion cost/margin), fused;</item>
///   <item>per-tenant plan margin used to attach Ion buy price to native Azure/CSP cost;</item>
///   <item>bootstrap import of customers CmCSP does not hold into the <see cref="CustomerStore"/>.</item>
/// </list>
///
/// It holds no cache of its own (the two clients cache their raw responses) and never talks to the
/// upstreams directly. Everything degrades to empty results when neither integration is configured,
/// so the dashboard shows native data only.
/// </summary>
public sealed class IonEnrichmentService(
    IonGatewayService ion,
    PartnerCenterService pct,
    CustomerStore customers,
    ILogger<IonEnrichmentService> logger)
{
    /// <summary><c>true</c> when at least one upstream (gateway or PCT) is configured.</summary>
    public bool IsConfigured => ion.IsConfigured || pct.IsConfigured;

    // ── Reseller directory ───────────────────────────────────────────────────────

    /// <summary>
    /// Merges the Ion master-data reseller directory with the Partner Center indirect-reseller list,
    /// name-matched (case-insensitive), so each row carries whichever ids are known
    /// (<see cref="IonResellerSummary.IonAccountId"/> and/or <see cref="IonResellerSummary.PartnerCenterId"/>).
    /// </summary>
    public async Task<List<IonResellerSummary>> GetResellerDirectoryAsync(string? search = null, CancellationToken ct = default)
    {
        var ionResellersTask = ion.ListResellersAsync(search, ct);
        var pctResellersTask = pct.GetIndirectResellersAsync(ct);
        await Task.WhenAll(ionResellersTask, pctResellersTask);

        var byName = new Dictionary<string, IonResellerSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in ionResellersTask.Result)
        {
            var name = (r.Name ?? $"Account {r.AccountId}").Trim();
            byName[name] = new IonResellerSummary(
                Name: name,
                IonAccountId: r.AccountId,
                PartnerCenterId: null,
                MpnId: null,
                PrimaryContactEmail: r.PrimaryContactEmail,
                DelegatedAdminEnabled: r.DelegatedAdminEnabled,
                CustomerCount: 0,
                Source: "ion");
        }

        foreach (var r in pctResellersTask.Result)
        {
            var name = (r.Name ?? r.Id ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            if (byName.TryGetValue(name, out var existing))
            {
                byName[name] = existing with
                {
                    PartnerCenterId = r.Id,
                    MpnId = r.MpnId ?? existing.MpnId,
                    Source = "ion+pct"
                };
            }
            else
            {
                byName[name] = new IonResellerSummary(
                    Name: name,
                    IonAccountId: null,
                    PartnerCenterId: r.Id,
                    MpnId: r.MpnId,
                    PrimaryContactEmail: null,
                    DelegatedAdminEnabled: false,
                    CustomerCount: 0,
                    Source: "pct");
            }
        }

        // Apply the search filter to the merged set too (PCT has no server-side filter).
        var rows = byName.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(r => r.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── Customer margins (fused list price + Ion cost/margin) ────────────────────

    /// <summary>
    /// Fused margin summaries for the given customer keys (Entra tenant GUID or domain). Uses the
    /// gateway's bulk fused-pricing endpoint and rolls each customer's lines into totals.
    /// <paramref name="displayNames"/> maps a tenant GUID to a friendly name for display.
    /// </summary>
    public async Task<List<CustomerMarginSummary>> GetCustomerMarginsAsync(
        IReadOnlyCollection<string> keys,
        IReadOnlyDictionary<string, string>? displayNames = null,
        CancellationToken ct = default)
    {
        if (keys.Count == 0) return [];
        var batch = await ion.GetFusedPricingBatchAsync(keys, ct);
        return batch.Customers
            .Select(c => ToSummary(c, displayNames))
            .OrderByDescending(s => s.TotalIonMargin)
            .ToList();
    }

    /// <summary>Fused margin summary for a single customer (tenant GUID or domain), or null.</summary>
    public async Task<CustomerMarginSummary?> GetCustomerMarginAsync(
        string key, string? displayName = null, CancellationToken ct = default)
    {
        var dto = await ion.GetFusedPricingAsync(key, ct);
        if (dto is null) return null;
        var names = displayName is null ? null : new Dictionary<string, string> { [dto.CustomerId ?? key] = displayName };
        return ToSummary(dto, names);
    }

    /// <summary>
    /// Portfolio-wide margin summaries across every active registered customer (partner view). The
    /// tenant list comes from the <see cref="CustomerStore"/>; empty when the registry is not
    /// provisioned or no customers are onboarded.
    /// </summary>
    public async Task<List<CustomerMarginSummary>> GetPortfolioMarginsAsync(CancellationToken ct = default)
    {
        if (!customers.IsEnabled) return [];
        var active = await customers.GetActiveCustomersAsync(ct);
        var keys = active
            .Select(c => c.TenantId)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (keys.Count == 0) return [];

        var names = active
            .Where(c => !string.IsNullOrWhiteSpace(c.TenantId))
            .GroupBy(c => c.TenantId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase);

        return await GetCustomerMarginsAsync(keys, names, ct);
    }

    // ── Per-tenant plan margin (attach Ion buy price to native Azure cost) ───────

    /// <summary>
    /// Ion buy price/margin per tenant/plan for the given tenant GUIDs, so a cost page can attach
    /// the reseller's transacted cost/margin to its native Azure/CSP subscription. Joined at the
    /// plan level (<c>mfgPartNumber</c>), not per Azure meter.
    /// </summary>
    public async Task<List<TenantPlanMargin>> GetTenantPlanMarginsAsync(
        IReadOnlyCollection<string> tenantIds, string vendor = "azure", CancellationToken ct = default)
    {
        if (tenantIds.Count == 0) return [];
        var batch = await ion.GetSubscriptionsBatchAsync(tenantIds, vendor, ct);
        var rows = new List<TenantPlanMargin>();
        foreach (var cust in batch.Customers)
        {
            var tenant = cust.TenantId ?? cust.Key ?? string.Empty;
            foreach (var s in cust.Subscriptions)
            {
                rows.Add(new TenantPlanMargin(
                    TenantId: tenant,
                    Domain: cust.Domain,
                    PlanName: s.Name,
                    MfgPartNumber: s.MfgPartNumber,
                    IonCost: s.Cost,
                    IonPrice: s.Price,
                    IonMargin: s.Margin,
                    IonMarginPct: s.Price is > 0 && s.Cost is not null
                        ? Math.Round((s.Price.Value - s.Cost.Value) / s.Price.Value * 100m, 1)
                        : null,
                    Currency: s.Currency));
            }
        }
        return rows;
    }

    // ── Import / onboarding from Ion ─────────────────────────────────────────────

    /// <summary>
    /// Bootstraps CmCSP's customer registry from the Ion Gateway directory: pages every addressable
    /// customer and bulk-imports the ones carrying an Entra tenant GUID into the
    /// <see cref="CustomerStore"/> (as <c>Source=ion</c>). Existing customers (native or already
    /// imported) are skipped without re-inserting, so re-running is cheap and idempotent.
    /// <paramref name="progress"/> reports per-batch so the UI can show a load bar. Requires SQL.
    /// </summary>
    public async Task<IonImportResult> ImportDirectoryAsync(
        string? source = null,
        IProgress<CustomerImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!customers.IsEnabled)
            return new IonImportResult(0, 0, 0, "The SQL customer registry is not provisioned.");
        if (!ion.IsConfigured)
            return new IonImportResult(0, 0, 0, "The Ion Gateway is not configured.");

        var directory = await ion.GetCustomerDirectoryAsync(source, ct);
        var items = directory
            .Select(d => (
                TenantId: d.TenantId ?? string.Empty,
                DisplayName: d.Name ?? d.Domain ?? d.TenantId ?? string.Empty,
                Domain: d.Domain))
            .ToList();

        var (imported, skipped) = await customers.ImportIonCustomersBulkAsync(items, progress, ct);
        return new IonImportResult(directory.Count, imported, skipped, null);
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────────

    private static CustomerMarginSummary ToSummary(
        FusedPricingDto c, IReadOnlyDictionary<string, string>? displayNames)
    {
        var lines = (c.Lines ?? [])
            .Select(l => new CustomerMarginLine(
                SubscriptionId: l.SubscriptionId,
                OfferName: l.OfferName,
                SkuTitle: l.SkuTitle,
                SkuPartNumber: l.SkuPartNumber,
                Quantity: l.Quantity,
                Status: l.Status,
                UnitListPrice: l.UnitListPrice,
                ListPriceCurrency: l.ListPriceCurrency,
                IonCost: l.IonCost,
                IonMarginPct: l.IonMarginPct,
                IonUnitMargin: l.IonUnitMargin,
                IonLineMargin: l.IonLineMargin,
                IonCurrency: l.IonCurrency,
                PricingSource: string.IsNullOrWhiteSpace(l.PricingSource) ? "none" : l.PricingSource!,
                MatchedOn: l.MatchedOn))
            .ToList();

        var matched = lines.Where(l => l.PricingSource == "ion-reseller-sku").ToList();
        decimal Qty(CustomerMarginLine l) => l.Quantity is > 0 ? l.Quantity.Value : 1m;

        var totalList   = lines.Where(l => l.UnitListPrice is not null).Sum(l => l.UnitListPrice!.Value * Qty(l));
        var totalCost   = matched.Where(l => l.IonCost is not null).Sum(l => l.IonCost!.Value * Qty(l));
        var totalMargin = matched.Sum(l => l.IonLineMargin
            ?? (l.IonUnitMargin is not null ? l.IonUnitMargin.Value * Qty(l) : 0m));

        var sell = totalCost + totalMargin;
        decimal? blended = sell > 0 ? Math.Round(totalMargin / sell * 100m, 1) : null;

        var currency = lines.Select(l => l.IonCurrency).FirstOrDefault(c2 => !string.IsNullOrWhiteSpace(c2))
                    ?? lines.Select(l => l.ListPriceCurrency).FirstOrDefault(c2 => !string.IsNullOrWhiteSpace(c2))
                    ?? string.Empty;

        var tenant = c.CustomerId;
        var name = tenant is not null && displayNames is not null && displayNames.TryGetValue(tenant, out var dn)
            ? dn
            : c.ResellerName ?? c.Domain ?? tenant;

        return new CustomerMarginSummary(
            TenantId: tenant,
            Domain: c.Domain,
            DisplayName: name,
            ResellerName: c.ResellerName,
            IonAccountId: c.IonAccountId,
            ResellerMatched: c.ResellerMatched,
            SubscriptionCount: lines.Count,
            MatchedCount: matched.Count,
            TotalListPrice: Math.Round(totalList, 2),
            TotalIonCost: Math.Round(totalCost, 2),
            TotalIonMargin: Math.Round(totalMargin, 2),
            BlendedMarginPct: blended,
            Currency: currency,
            Lines: lines);
    }
}

/// <summary>Outcome of an Ion directory import: how many rows were seen, imported, and skipped.</summary>
public sealed record IonImportResult(int Total, int Imported, int Skipped, string? Error);
