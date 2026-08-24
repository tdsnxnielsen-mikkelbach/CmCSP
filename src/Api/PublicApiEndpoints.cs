using CmCSP.Models;
using CmCSP.Services;

namespace CmCSP.Api;

/// <summary>
/// Maps the public REST API that mirrors the data the dashboard UI consumes. Every endpoint is
/// read-only (HTTP GET), returns JSON, is documented for OpenAPI/Scalar, and is protected by the
/// shared API key via <see cref="ApiKeyEndpointFilter"/>. Grouped under <c>/api/v1</c>.
/// </summary>
public static class PublicApiEndpoints
{
    public static IEndpointRouteBuilder MapPublicApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1")
            .AddEndpointFilter<ApiKeyEndpointFilter>()
            .WithGroupName("v1")
            .AllowAnonymous(); // Cookie/OIDC auth is bypassed; access is gated by the API key filter.

        MapSubscriptions(api);
        MapCost(api);
        MapAdvisor(api);
        MapReservations(api);
        MapOptimization(api);
        MapSecurity(api);
        MapSustainability(api);
        MapCollection(api);
        MapIon(api);

        return app;
    }

    // ── Subscription directory ─────────────────────────────────────────────────
    private static void MapSubscriptions(RouteGroupBuilder api)
    {
        api.MapGet("/subscriptions", (CockpitQueryService svc, CancellationToken ct) =>
                svc.GetSubscriptionDirectoryAsync(ct))
            .WithTags("Subscriptions")
            .WithSummary("Subscription directory")
            .WithDescription("Returns one entry per configured subscription with its display name and the " +
                "customer / Entra tenant that owns it, so Azure spend can be attributed to a customer. Each " +
                "entry always carries a non-empty tenantId (the CSP's own home tenant in single-tenant " +
                "deployments); customerId is the owning customer's id, or 0 when no customer registry is " +
                "provisioned. Use tenantId as the canonical join key.")
            .Produces<List<SubscriptionDirectoryEntry>>();
    }

    // ── Cost ──────────────────────────────────────────────────────────────────
    private static void MapCost(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/cost").WithTags("Cost");

        g.MapGet("/main", (ICostManagementService svc, CancellationToken ct) => svc.GetMainCostDataAsync(ct))
            .WithSummary("Daily cost by service")
            .WithDescription("Returns daily cost rows aggregated by subscription and Azure service " +
                "(MeterCategory) across every configured subscription. Amounts are provided both in the " +
                "original billing currency and normalised to the dashboard's target currency. This is the " +
                "dataset behind the main cost trend and service-breakdown charts.")
            .Produces<List<CostRow>>();

        g.MapGet("/resource-groups", (ICostManagementService svc, CancellationToken ct) => svc.GetRgCostDataAsync(ct))
            .WithSummary("Daily cost by resource group")
            .WithDescription("Returns daily cost rows aggregated by subscription and resource group. Use this " +
                "to attribute spend to a workload or team that owns a resource group.")
            .Produces<List<CostRow>>();

        g.MapGet("/tags", (ICostManagementService svc, CancellationToken ct) => svc.GetTagCostDataAsync(ct))
            .WithSummary("Daily cost by tag")
            .WithDescription("Returns daily cost rows aggregated by subscription and tag key, powering the " +
                "tag-based chargeback view. Untagged spend is grouped under an empty tag value.")
            .Produces<List<CostRow>>();

        g.MapGet("/amortized", (ICostManagementService svc, CancellationToken ct) => svc.GetAmortizedMainCostDataAsync(ct))
            .WithSummary("Daily amortized cost by service")
            .WithDescription("Like /cost/main but using the AmortizedCost metric: reservation purchase costs are " +
                "spread evenly across their term instead of appearing as a spike on the purchase day, giving a " +
                "smoother trend line for effective spend.")
            .Produces<List<CostRow>>();

        g.MapGet("/forecast", (string? metric, ICostManagementService svc, CancellationToken ct) =>
                svc.GetForecastAsync(metric ?? "ActualCost", ct))
            .WithSummary("Month-to-date actuals plus forecast")
            .WithDescription("Returns the Microsoft Cost Management native forecast for the current calendar month, " +
                "aggregated across all subscriptions. Each point is flagged as an actual (past day) or a forecast " +
                "(projected day). The optional 'metric' query parameter accepts 'ActualCost' (default) or " +
                "'AmortizedCost'.")
            .Produces<List<ForecastPoint>>();

        g.MapGet("/publisher-breakdown", (ICostManagementService svc, CancellationToken ct) => svc.GetPublisherBreakdownAsync(ct))
            .WithSummary("Month-to-date spend by publisher type")
            .WithDescription("Returns month-to-date spend split by PublisherType (Azure first-party versus Azure " +
                "Marketplace / third-party) and service. Empty when the dimension is unsupported for the " +
                "configured subscriptions.")
            .Produces<List<PublisherTypeCostRow>>();

        g.MapGet("/budgets", (ICostManagementService svc, CancellationToken ct) => svc.GetSubscriptionBudgetsAsync(ct))
            .WithSummary("Subscription budgets")
            .WithDescription("Returns budgets defined at subscription scope, with current spend, for every " +
                "subscription that has at least one budget. Amounts are normalised to the target currency.")
            .Produces<List<SubscriptionBudget>>();

        g.MapGet("/subscriptions", (ICostManagementService svc, CancellationToken ct) => svc.GetSubscriptionDisplayNamesAsync(ct))
            .WithSummary("Subscription id to display-name map")
            .WithDescription("Returns a dictionary mapping each configured subscription id to its display name. " +
                "Falls back to the raw id when the subscription's name cannot be resolved.")
            .Produces<Dictionary<string, string>>();

        g.MapGet("/monthly", (string? from, string? to, string? subscriptionId, string? currency,
                    CockpitQueryService svc, CancellationToken ct) =>
                svc.GetMonthlyCostAsync(from, to, subscriptionId, currency, ct))
            .WithSummary("Monthly cost by subscription and service")
            .WithDescription("Returns per-subscription, per-month, per-service pre-aggregated cost so callers " +
                "avoid pulling daily rows and aggregating client-side. The 'from' and 'to' query parameters are " +
                "months (yyyy-MM); when omitted they default to the last 12 months through the current month. " +
                "'subscriptionId' optionally filters to a single subscription. 'currency' (ISO 4217, e.g. EUR) " +
                "selects the currency for normalizedCost/normalizedCurrency and defaults to the configured " +
                "target currency. cost/currency stay in the original billing currency.")
            .Produces<List<MonthlyCostRow>>();
    }

    // ── Advisor ───────────────────────────────────────────────────────────────
    private static void MapAdvisor(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/advisor").WithTags("Advisor");

        g.MapGet("/recommendations", (ICostManagementService svc, CancellationToken ct) => svc.GetAdvisorRecommendationsAsync(ct))
            .WithSummary("Azure Advisor cost recommendations")
            .WithDescription("Returns Azure Advisor recommendations in the Cost category (right-sizing, idle " +
                "resources, reserved-instance opportunities, and similar) for every configured subscription. " +
                "Annual saving amounts are normalised to the target currency.")
            .Produces<List<AdvisorRecommendation>>();

        g.MapGet("/scores", (ICostManagementService svc, CancellationToken ct) => svc.GetAdvisorScoresAsync(ct))
            .WithSummary("Azure Advisor category scores")
            .WithDescription("Returns Azure Advisor health scores for all five categories (Cost, Security, " +
                "Reliability, Operational Excellence, Performance) with one record per subscription per category.")
            .Produces<List<AdvisorCategoryScore>>();
    }

    // ── Reservations (Cost Details API) ────────────────────────────────────────
    private static void MapReservations(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/reservations").WithTags("Reservations");

        g.MapGet("/subscriptions", (DateOnly? from, DateOnly? to, ICostDetailsService svc, CancellationToken ct) =>
                svc.GetAllSubscriptionReservationsAsync(DefaultFrom(from), DefaultTo(to), ct))
            .WithSummary("Reservation usage across subscriptions")
            .WithDescription("Returns the reservation cost breakdown (used versus unused, amortized) aggregated " +
                "across all configured subscriptions for the given date range. The 'from' and 'to' query " +
                "parameters are ISO dates (yyyy-MM-dd); when omitted they default to the current month to date. " +
                "Always available regardless of billing-account configuration.")
            .Produces<List<ReservationRow>>();

        g.MapGet("/customers", (DateOnly? from, DateOnly? to, ICostDetailsService svc, CancellationToken ct) =>
                svc.GetAllCustomerReservationsAsync(DefaultFrom(from), DefaultTo(to), ct))
            .WithSummary("Reservation usage at billing-account/customer scope")
            .WithDescription("Returns reservation cost data at billing-account/customer scope (MCA/CSP) for the " +
                "given date range. Returns an empty list when billing-account access is not configured. The " +
                "'from' and 'to' query parameters are ISO dates (yyyy-MM-dd); when omitted they default to the " +
                "current month to date.")
            .Produces<List<ReservationRow>>();

        g.MapGet("/monthly", (string? from, string? to, string? subscriptionId,
                    CockpitQueryService svc, CancellationToken ct) =>
                svc.GetMonthlyReservationsAsync(from, to, subscriptionId, ct))
            .WithSummary("Monthly RI/SP coverage rollup by subscription")
            .WithDescription("Returns a per-subscription, per-month rollup of reservation (RI/SP) usage — used, " +
                "unused and total cost plus utilisation percentage — that drives the 'covered' signal. The " +
                "'from' and 'to' query parameters are months (yyyy-MM); when omitted they default to the current " +
                "month. 'subscriptionId' optionally filters to a single subscription.")
            .Produces<List<MonthlyReservationRow>>();
    }

    // ── Optimization / Inventory ───────────────────────────────────────────────
    private static void MapOptimization(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/optimization").WithTags("Optimization");

        g.MapGet("/inventory", (OptimizationService svc, CancellationToken ct) => svc.GetInventoryAsync(ct))
            .WithSummary("Resource inventory")
            .WithDescription("Returns the live Azure resource inventory (from Azure Resource Graph) across every " +
                "configured subscription: resource id, type, location, resource group, and tags.")
            .Produces<List<ResourceInventoryItem>>();

        g.MapGet("/tag-coverage", (OptimizationService svc, CancellationToken ct) => svc.GetTagCoverageAsync(ct))
            .WithSummary("Tag coverage summary")
            .WithDescription("Returns the proportion of inventoried resources that carry governance tags, used to " +
                "measure tagging hygiene across the estate.")
            .Produces<InventoryTagCoverage>();

        g.MapGet("/orphaned", (OptimizationService svc, CancellationToken ct) => svc.GetOrphanedResourcesAsync(ct))
            .WithSummary("Orphaned resources")
            .WithDescription("Returns resources that appear to be unused and are candidates for deletion (for " +
                "example unattached managed disks and public IPs, empty network interfaces), so callers can " +
                "reclaim spend.")
            .Produces<List<OrphanedResource>>();

        g.MapGet("/reservation-recommendations", (OptimizationService svc, CancellationToken ct) => svc.GetReservationRecommendationsAsync(ct))
            .WithSummary("Reservation purchase recommendations")
            .WithDescription("Returns Microsoft Capacity reservation purchase recommendations ordered by " +
                "normalised net savings, so callers can prioritise the highest-value reservation purchases.")
            .Produces<List<ReservationPurchaseRecommendation>>();

        g.MapGet("/reservation-orders", (OptimizationService svc, CancellationToken ct) => svc.GetReservationOrdersAsync(ct))
            .WithSummary("Existing reservation orders")
            .WithDescription("Returns existing reservation orders with their expiry dates so callers can warn " +
                "about reservations lapsing soon. Requires Reservations Reader; degrades to an empty list when " +
                "the permission is absent.")
            .Produces<List<ReservationOrderInfo>>();
    }

    // ── Security ───────────────────────────────────────────────────────────────
    private static void MapSecurity(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/security").WithTags("Security");

        g.MapGet("/secure-scores", (SecurityPostureService svc, CancellationToken ct) => svc.GetSecureScoresAsync(ct))
            .WithSummary("Defender for Cloud secure scores")
            .WithDescription("Returns the Microsoft Defender for Cloud secure score per subscription (current " +
                "score and maximum), summarising overall security posture.")
            .Produces<List<SecureScoreSummary>>();

        g.MapGet("/findings", (SecurityPostureService svc, CancellationToken ct) => svc.GetTopFindingsAsync(ct))
            .WithSummary("Top security control findings")
            .WithDescription("Returns the highest-impact Defender for Cloud security control findings per " +
                "subscription, so callers can surface the controls that would most improve the secure score.")
            .Produces<List<SecurityControlFinding>>();
    }

    // ── Sustainability ─────────────────────────────────────────────────────────
    private static void MapSustainability(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/sustainability").WithTags("Sustainability");

        g.MapGet("/summary", (SustainabilityService svc, CancellationToken ct) => svc.GetEmissionSummaryAsync(ct))
            .WithSummary("Carbon emissions summary")
            .WithDescription("Returns the latest Azure Carbon Optimization emissions summary (total emissions and " +
                "month-over-month change) across the estate. May be null when no emissions report is available.")
            .Produces<CarbonEmissionSummary>();

        g.MapGet("/monthly", (SustainabilityService svc, CancellationToken ct) => svc.GetMonthlyEmissionsAsync(ct))
            .WithSummary("Monthly carbon emissions")
            .WithDescription("Returns the monthly carbon-emissions trend, one point per month, for charting the " +
                "emissions trajectory over time.")
            .Produces<List<CarbonEmissionMonth>>();

        g.MapGet("/by-type", (SustainabilityService svc, CancellationToken ct) => svc.GetEmissionsByTypeAsync(ct))
            .WithSummary("Carbon emissions by resource type")
            .WithDescription("Returns carbon emissions broken down by Azure resource type, identifying the " +
                "resource categories that contribute the most to the carbon footprint.")
            .Produces<List<CarbonEmissionByType>>();

        g.MapGet("/by-subscription", (SustainabilityService svc, CancellationToken ct) => svc.GetEmissionsBySubscriptionAsync(ct))
            .WithSummary("Carbon emissions by subscription")
            .WithDescription("Returns carbon emissions broken down by subscription, so callers can attribute the " +
                "carbon footprint to individual workloads or customers.")
            .Produces<List<CarbonEmissionBySubscription>>();
    }

    // ── Collection audit ───────────────────────────────────────────────────────
    private static void MapCollection(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/collection").WithTags("Collection");

        g.MapGet("/audit", (int? max, CollectionAuditService svc, CancellationToken ct) =>
                svc.GetRecentAsync(max is > 0 and <= 100 ? max.Value : 10, ct))
            .WithSummary("Recent collection runs")
            .WithDescription("Returns the most recent cost-collection job runs with their status, trigger, row " +
                "counts, and duration. The optional 'max' query parameter (1-100, default 10) caps the number " +
                "of runs returned.")
            .Produces<IReadOnlyList<CollectionAuditRecord>>();

        g.MapGet("/audit/latest", (CollectionAuditService svc, CancellationToken ct) => svc.GetLatestAsync(ct))
            .WithSummary("Latest collection run")
            .WithDescription("Returns the single most recent cost-collection job run, or null when no run has " +
                "been recorded yet. Use this for a lightweight freshness/health check of the data pipeline.")
            .Produces<CollectionAuditRecord>();
    }

    // ── Ion Gateway / Partner Center (margin enrichment) ───────────────────────
    private static void MapIon(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/ion").WithTags("Ion & Margin");

        g.MapGet("/resellers", (string? search, IonEnrichmentService svc, CancellationToken ct) =>
                svc.GetResellerDirectoryAsync(search, ct))
            .WithSummary("Reseller directory (Ion + Partner Center)")
            .WithDescription("Returns the merged indirect-reseller directory: TD SYNNEX Ion master-data " +
                "accounts name-matched with Microsoft Partner Center indirect resellers. Each row carries " +
                "whichever ids are known (ionAccountId and/or partnerCenterId + mpnId) and a 'source' of " +
                "'ion', 'pct', or 'ion+pct'. Use ionAccountId to read a reseller's crawled orders. The " +
                "optional 'search' query parameter filters by reseller name.")
            .Produces<List<IonResellerSummary>>();

        g.MapGet("/resellers/{accountId:long}/orders", (long accountId, IonGatewayService svc, CancellationToken ct) =>
                svc.GetResellerOrdersAsync(accountId, ct))
            .WithSummary("Reseller orders with line-level pricing (Ion)")
            .WithDescription("Returns every customer's crawled orders under an Ion reseller account id, with " +
                "line-level transacted pricing (productId, skuId, mfgPartNumber, quantity, and per-unit " +
                "price/cost/msrp plus the line's total margin). All figures are Ion-sourced (TD SYNNEX " +
                "StreamOne), not native Azure cost. Empty until the nightly Ion crawl has run for the account.")
            .Produces<List<IonOrder>>();

        g.MapGet("/customers/directory", (string? source, IonGatewayService svc, CancellationToken ct) =>
                svc.GetCustomerDirectoryAsync(source, ct))
            .WithSummary("Customer bootstrap directory (Ion + PCT)")
            .WithDescription("Returns every addressable customer across both upstreams — one row per customer " +
                "with its Entra tenant GUID, domain, name, source ('ion' or 'pct'), Ion account id, and " +
                "reseller name. This is the bootstrap feed used to learn and onboard customers CmCSP does not " +
                "hold natively. The optional 'source' query parameter ('ion' | 'pct') scopes to one upstream. " +
                "Paging is handled server-side; the full set is returned.")
            .Produces<List<IonDirectoryCustomer>>();

        g.MapGet("/margins", (IonEnrichmentService svc, CancellationToken ct) =>
                svc.GetPortfolioMarginsAsync(ct))
            .WithSummary("Portfolio margin summary (list price + Ion cost/margin)")
            .WithDescription("Returns one fused margin summary per active registered customer: the native " +
                "Microsoft list price (from Partner Center) alongside the Ion transacted cost and margin, with " +
                "rolled-up totals and a blended margin percentage. 'resellerMatched' = false means no Ion " +
                "account resolved for the customer's reseller, so only list price is present. Ion pricing is " +
                "reseller-level and SKU-approximate — check each line's pricingSource/matchedOn. Empty when the " +
                "customer registry is not provisioned.")
            .Produces<List<CustomerMarginSummary>>();

        g.MapGet("/margins/{key}", async (string key, IonEnrichmentService svc, CancellationToken ct) =>
            {
                var summary = await svc.GetCustomerMarginAsync(key, ct: ct);
                return summary is null ? Results.NotFound() : Results.Ok(summary);
            })
            .WithSummary("Customer margin detail (list price + Ion cost/margin)")
            .WithDescription("Returns the fused per-subscription margin detail for one customer, keyed by Entra " +
                "tenant GUID or primary domain. Each line pairs the native Microsoft unitListPrice with the Ion " +
                "cost, unit margin, margin percentage and matched order-line margin, plus the match provenance " +
                "(pricingSource, matchedOn). Returns 404 when the customer is not known to the gateway.")
            .Produces<CustomerMarginSummary>()
            .Produces(StatusCodes.Status404NotFound);

        g.MapGet("/cost-margin", async (string? tenantIds, string? vendor,
                    IonEnrichmentService svc, CustomerStore customers, CancellationToken ct) =>
            {
                var keys = string.IsNullOrWhiteSpace(tenantIds)
                    ? (await customers.GetActiveCustomersAsync(ct))
                        .Select(c => c.TenantId)
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : tenantIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                return await svc.GetTenantPlanMarginsAsync(keys, vendor ?? "azure", ct);
            })
            .WithSummary("Per-tenant plan cost/margin for Azure cost enrichment (Ion)")
            .WithDescription("Returns the Ion buy price and margin per tenant/plan so Azure/CSP cost can be " +
                "decorated with the reseller's transacted cost and margin. Join to native cost on tenant + plan " +
                "(mfgPartNumber), not per Azure meter — Ion cost is at the subscription/plan level. The optional " +
                "'tenantIds' query parameter is a comma-separated list of Entra tenant GUIDs (defaults to every " +
                "active registered customer); 'vendor' selects the Ion vendor filter ('azure' default, or " +
                "'microsoft').")
            .Produces<List<TenantPlanMargin>>();
    }

    // Date-range defaults: current month to date when the caller omits from/to.
    private static DateOnly DefaultFrom(DateOnly? from) =>
        from ?? new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

    private static DateOnly DefaultTo(DateOnly? to) =>
        to ?? DateOnly.FromDateTime(DateTime.UtcNow);
}
