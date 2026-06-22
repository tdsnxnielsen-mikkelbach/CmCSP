using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Phase 7 — Azure inventory &amp; optimization.
///
/// Joins cost data to <b>live Azure resource inventory</b> so the dashboard can answer
/// "what is running and where can we save?", not just "what did it cost?". Three feeds,
/// all read-only and using the existing management bearer token:
///   • Azure Resource Graph — full inventory (tags / region / type) and orphaned-resource finder.
///   • Microsoft.Consumption  — reservation / savings-plan purchase recommendations.
///   • Microsoft.Capacity     — existing reservation orders + expiry dates.
///
/// Caching note: unlike the cost datasets (which flow CSV export → SQL CostFact → ICacheService
/// and are owned by CostCollectorJob), these are on-demand ARM reads. They are memoised here in a
/// small in-process TTL cache so a page refresh is cheap, without coupling the nightly collector to
/// Resource Graph. This deliberately stays outside the cost-cache contract in services-cache.
///
/// Graceful degradation: every feed needs <b>Reader</b> (and Microsoft.Consumption read) on the
/// target subscriptions. When the identity is missing that grant the ARM calls return 403 /
/// 404 / empty; we swallow those, log a warning, and surface <see cref="LastAccessDenied"/> so the
/// UI can show a "needs Reader role" banner with an empty state instead of an error.
/// </summary>
public sealed class OptimizationService
{
    private const string ResourceGraphApiVersion = "2024-04-01";
    private const string ConsumptionApiVersion   = "2024-08-01";
    private const string CapacityApiVersion       = "2022-11-01";

    // Resource Graph caps a single page at 1000 rows; we page until exhausted or this safety cap.
    private const int    ResourceGraphPageSize    = 1000;
    private const int    MaxInventoryRows          = 20_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory             _httpFactory;
    private readonly AzureTokenService               _tokenService;
    private readonly ICostManagementService          _costService;
    private readonly CostManagementOptions           _options;
    private readonly TenantScopeAccessor             _scopeAccessor;
    private readonly CustomerStore?                  _customers;
    private readonly ILogger<OptimizationService>    _logger;

    // ── in-process TTL memo (see caching note above) ─────────────────────────
    // Partitioned by the ambient tenant scope's cache-key prefix so a customer (or warmup) scope
    // that legitimately reads nothing — or is denied — never poisons the partner/home view, and
    // each tenant only ever sees its own inventory.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTime At, List<ResourceInventoryItem> Data)>            _inventory   = new();
    private readonly ConcurrentDictionary<string, (DateTime At, List<OrphanedResource> Data)>                 _orphans     = new();
    private readonly ConcurrentDictionary<string, (DateTime At, List<ReservationPurchaseRecommendation> Data)> _reservationRecs   = new();
    private readonly ConcurrentDictionary<string, (DateTime At, List<ReservationOrderInfo> Data)>             _reservationOrders = new();

    // Per-scope "most recent ARM read was denied" flag — the UI shows a Reader banner for the
    // scope it is actually viewing, not a stale value left by some other tenant's request.
    private readonly ConcurrentDictionary<string, bool> _accessDenied = new();

    /// <summary>True when the most recent ARM read for the current scope was denied (403/404).</summary>
    public bool LastAccessDenied => _accessDenied.TryGetValue(ScopeKey, out var v) && v;

    private TenantScope Scope    => _scopeAccessor?.Current ?? TenantScope.Unscoped;
    private string      ScopeKey => Scope.CacheKeyPrefix;

    public OptimizationService(
        IHttpClientFactory          httpFactory,
        AzureTokenService            tokenService,
        ICostManagementService       costService,
        CostManagementOptions        options,
        TenantScopeAccessor          scopeAccessor,
        ILogger<OptimizationService> logger,
        CustomerStore?               customers = null)
    {
        _httpFactory   = httpFactory;
        _tokenService  = tokenService;
        _costService   = costService;
        _options       = options;
        _scopeAccessor = scopeAccessor;
        _customers     = customers;
        _logger        = logger;
    }

    private TimeSpan Ttl => TimeSpan.FromMinutes(_options.CacheExpirationMinutes);

    private bool Fresh(DateTime at) => DateTime.UtcNow - at < Ttl;

    // ── 1. Resource Graph inventory ──────────────────────────────────────────

    /// <summary>
    /// Returns every live resource across the configured subscriptions (id, type, region, tags),
    /// memoised for the configured TTL. Empty when Reader is missing (sets <see cref="LastAccessDenied"/>).
    /// </summary>
    public async Task<List<ResourceInventoryItem>> GetInventoryAsync(CancellationToken ct = default)
    {
        if (_inventory.TryGetValue(ScopeKey, out var c) && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_inventory.TryGetValue(ScopeKey, out var c2) && Fresh(c2.At)) return c2.Data;

            const string query =
                "Resources " +
                "| project id, name, type, resourceGroup, location, subscriptionId, tags " +
                "| order by type asc";

            var rows = await RunResourceGraphAsync(query, ct);
            var items = rows.Select(MapInventoryItem).ToList();
            _inventory[ScopeKey] = (DateTime.UtcNow, items);
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tag-coverage rollup for the chargeback page: how many live resources carry each tag key,
    /// plus the untagged count. Built from <see cref="GetInventoryAsync"/>.
    /// </summary>
    public async Task<InventoryTagCoverage> GetTagCoverageAsync(CancellationToken ct = default)
    {
        var inv = await GetInventoryAsync(ct);
        var perKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var untagged = 0;
        foreach (var r in inv)
        {
            if (r.IsUntagged) { untagged++; continue; }
            foreach (var key in r.Tags.Keys)
                perKey[key] = perKey.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return new InventoryTagCoverage(inv.Count, untagged, perKey);
    }

    // ── 2. Orphaned / untagged finder ────────────────────────────────────────

    /// <summary>
    /// Finds wasteful resources via Resource Graph: unattached managed disks, unassociated public
    /// IPs, orphaned NICs, empty App Service plans and stopped-but-allocated VMs. Each row carries a
    /// human-readable reason for the Optimization page.
    /// </summary>
    public async Task<List<OrphanedResource>> GetOrphanedResourcesAsync(CancellationToken ct = default)
    {
        if (_orphans.TryGetValue(ScopeKey, out var c) && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_orphans.TryGetValue(ScopeKey, out var c2) && Fresh(c2.At)) return c2.Data;

            const string query =
                "Resources " +
                "| where (type =~ 'microsoft.compute/disks' and tostring(properties.diskState) =~ 'Unattached') " +
                "   or (type =~ 'microsoft.network/publicipaddresses' and isnull(properties.ipConfiguration) and isnull(properties.natGateway)) " +
                "   or (type =~ 'microsoft.network/networkinterfaces' and isnull(properties.virtualMachine) and isnull(properties.privateEndpoint)) " +
                "   or (type =~ 'microsoft.web/serverfarms' and toint(properties.numberOfSites) == 0) " +
                "   or (type =~ 'microsoft.compute/virtualmachines' and tostring(properties.extended.instanceView.powerState.code) =~ 'PowerState/deallocated') " +
                "| extend reason = case(" +
                "    type =~ 'microsoft.compute/disks', 'Unattached managed disk'," +
                "    type =~ 'microsoft.network/publicipaddresses', 'Unassociated public IP'," +
                "    type =~ 'microsoft.network/networkinterfaces', 'Orphaned network interface'," +
                "    type =~ 'microsoft.web/serverfarms', 'Empty App Service plan (no sites)'," +
                "    type =~ 'microsoft.compute/virtualmachines', 'Stopped but allocated VM'," +
                "    'Other') " +
                "| project id, name, type, resourceGroup, location, subscriptionId, reason " +
                "| order by type asc";

            var rows = await RunResourceGraphAsync(query, ct);
            var items = rows.Select(MapOrphan).ToList();
            _orphans[ScopeKey] = (DateTime.UtcNow, items);
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 3. Reservation purchase recommendations + expiry ─────────────────────

    /// <summary>
    /// Reservation / savings-plan purchase recommendations from Microsoft.Consumption, per
    /// subscription, normalised to the configured TargetCurrency. Empty on missing access.
    /// </summary>
    public async Task<List<ReservationPurchaseRecommendation>> GetReservationRecommendationsAsync(
        CancellationToken ct = default)
    {
        if (_reservationRecs.TryGetValue(ScopeKey, out var c) && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_reservationRecs.TryGetValue(ScopeKey, out var c2) && Fresh(c2.At)) return c2.Data;

            var targets = await ResolveTargetsAsync(ct);
            var tasks = targets
                .Select(t => FetchReservationRecsForSubAsync(t.SubscriptionId, t.TenantId, ct));
            var perSub = await Task.WhenAll(tasks);
            var results = perSub
                .SelectMany(r => r)
                .OrderByDescending(r => r.NormalizedNetSavings)
                .ToList();

            _reservationRecs[ScopeKey] = (DateTime.UtcNow, results);
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Existing reservation orders with their expiry dates (Microsoft.Capacity), so the UI can warn
    /// about reservations lapsing soon. Tenant-scoped; needs Reservations Reader — degrades to empty.
    /// </summary>
    public async Task<List<ReservationOrderInfo>> GetReservationOrdersAsync(CancellationToken ct = default)
    {
        if (_reservationOrders.TryGetValue(ScopeKey, out var c) && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_reservationOrders.TryGetValue(ScopeKey, out var c2) && Fresh(c2.At)) return c2.Data;

            var results = await FetchReservationOrdersAsync(ct);
            _reservationOrders[ScopeKey] = (DateTime.UtcNow, results);
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Resource Graph plumbing ──────────────────────────────────────────────

    private async Task<List<JsonElement>> RunResourceGraphAsync(string query, CancellationToken ct)
    {
        var rows = new List<JsonElement>();

        // The subscriptions to inventory and the tenant authority each one needs. Querying the
        // in-scope subscriptions (rather than always the home registry) with the correct per-tenant
        // (GDAP) token is what lets a customer scope read its own resources — and stops a
        // customer-tenant token from being pointed at home subscriptions and returning 403.
        var targets = await ResolveTargetsAsync(ct);
        if (targets.Count == 0) return rows;

        var anyDenied = false;
        var anySuccess = false;

        // Group by tenant so each Resource Graph call carries one token + that tenant's subs.
        foreach (var group in targets.GroupBy(t => t.TenantId, StringComparer.OrdinalIgnoreCase))
        {
            var subs = group.Select(t => t.SubscriptionId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            try
            {
                using var client = _httpFactory.CreateClient("AzureMgmt");
                var token = await _tokenService.GetAccessTokenAsync(group.Key, ct);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var url = $"https://management.azure.com/providers/Microsoft.ResourceGraph/resources" +
                          $"?api-version={ResourceGraphApiVersion}";

                string? skipToken = null;
                do
                {
                    var options = new Dictionary<string, object>
                    {
                        ["resultFormat"] = "objectArray",
                        ["$top"]         = ResourceGraphPageSize
                    };
                    if (skipToken is not null) options["$skipToken"] = skipToken;

                    var body = new
                    {
                        subscriptions = subs,
                        query,
                        options
                    };

                    using var content = new StringContent(
                        JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync(url, content, ct);

                    if (!response.IsSuccessStatusCode)
                    {
                        var payload = await response.Content.ReadAsStringAsync(ct);
                        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                        {
                            anyDenied = true;
                            _logger.LogWarning(
                                "Resource Graph returned {Status} — the identity likely lacks Reader on the " +
                                "target subscriptions. Returning empty inventory.", (int)response.StatusCode);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Resource Graph returned {Status}. Body: {Body}", (int)response.StatusCode, payload);
                        }
                        break;
                    }

                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    var root = doc.RootElement;

                    if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in data.EnumerateArray())
                            rows.Add(el.Clone());
                    }

                    anySuccess = true;

                    skipToken = root.TryGetProperty("$skipToken", out var st) && st.ValueKind == JsonValueKind.String
                        ? st.GetString()
                        : null;
                }
                while (!string.IsNullOrEmpty(skipToken) && rows.Count < MaxInventoryRows && !ct.IsCancellationRequested);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resource Graph query failed for tenant {Tenant}.", group.Key ?? "(home)");
            }
        }

        // The banner reflects this scope only: flag denial when every group was rejected, and clear
        // it the moment any group succeeds, so a partial/empty-but-authorised result clears it too.
        if (anySuccess) _accessDenied[ScopeKey] = false;
        else if (anyDenied) _accessDenied[ScopeKey] = true;

        return rows;
    }

    /// <summary>
    /// The (subscriptionId, tenantId) pairs to read for the current scope. <c>tenantId</c> is
    /// <c>null</c> for the home authority. Single-tenant/unscoped → the configured home subs.
    /// A single customer → that customer's subs on its tenant (plus the home registry subs when that
    /// customer is the home customer). The partner aggregate → every active customer's subs (each on
    /// its own tenant) plus the home registry subs on the home token. Mirrors the cost service's
    /// publisher-target resolution so inventory is scoped identically to cost.
    /// </summary>
    private async Task<IReadOnlyList<(string SubscriptionId, string? TenantId)>> ResolveTargetsAsync(
        CancellationToken ct)
    {
        var scope = Scope;
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Legacy single-tenant path (or registry unavailable): home subs on the home token.
        if (scope.IsUnscoped || _customers is null || !_customers.IsEnabled)
        {
            foreach (var s in _options.SubscriptionIds) map[s] = null;
            return [.. map.Select(kv => (kv.Key, kv.Value))];
        }

        if (scope.IsDenied) return [];

        var customers = scope.IsPartner
            ? await _customers.GetActiveCustomersAsync(ct)
            : (await _customers.GetActiveCustomersAsync(ct))
                .Where(c => scope.CustomerIds.Contains(c.Id))
                .ToList();

        foreach (var c in customers)
        {
            var tid = string.IsNullOrWhiteSpace(c.TenantId) ? null : c.TenantId;
            foreach (var sub in await _customers.GetSubscriptionsAsync(c.Id, ct))
                map[sub.SubscriptionId] = tid;
        }

        // The home/partner's own subscriptions live in the SubStore registry (not the
        // CustomerSubscription table), read on the home token. Include them whenever the home
        // customer is in scope — the partner aggregate or a partner drilling into the home customer.
        var home = await _customers.GetHomeCustomerAsync(ct);
        var homeInScope = scope.IsPartner || (home is not null && scope.CustomerIds.Contains(home.Id));
        if (homeInScope)
            foreach (var s in _options.SubscriptionIds)
                if (!map.ContainsKey(s)) map[s] = null;

        return [.. map.Select(kv => (kv.Key, kv.Value))];
    }

    private static ResourceInventoryItem MapInventoryItem(JsonElement el)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (el.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var t in tagsEl.EnumerateObject())
                tags[t.Name] = t.Value.ValueKind == JsonValueKind.String ? t.Value.GetString() ?? string.Empty
                                                                          : t.Value.ToString();
        }

        return new ResourceInventoryItem(
            Id:             Str(el, "id"),
            Name:           Str(el, "name"),
            Type:           Str(el, "type"),
            ResourceGroup:  Str(el, "resourceGroup"),
            Location:       Str(el, "location"),
            SubscriptionId: Str(el, "subscriptionId"),
            Tags:           tags);
    }

    private static OrphanedResource MapOrphan(JsonElement el) => new(
        Id:             Str(el, "id"),
        Name:           Str(el, "name"),
        Type:           Str(el, "type"),
        ResourceGroup:  Str(el, "resourceGroup"),
        Location:       Str(el, "location"),
        SubscriptionId: Str(el, "subscriptionId"),
        Reason:         Str(el, "reason"));

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    // ── Consumption reservation recommendations ──────────────────────────────

    private async Task<List<ReservationPurchaseRecommendation>> FetchReservationRecsForSubAsync(
        string subId, string? tenantId, CancellationToken ct)
    {
        var results = new List<ReservationPurchaseRecommendation>();
        try
        {
            using var client = _httpFactory.CreateClient("AzureMgmt");
            var token = await _tokenService.GetAccessTokenAsync(tenantId, ct);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = $"https://management.azure.com/subscriptions/{subId}" +
                      $"/providers/Microsoft.Consumption/reservationRecommendations" +
                      $"?api-version={ConsumptionApiVersion}";

            while (!string.IsNullOrEmpty(url))
            {
                using var response = await client.GetAsync(url, ct);

                if (response.StatusCode == HttpStatusCode.NoContent) break;

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                        _accessDenied[ScopeKey] = true;
                    var payload = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "Reservation recommendations returned {Status} for subscription {SubId}. Body: {Body}",
                        (int)response.StatusCode, subId, payload);
                    break;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;

                if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rec in value.EnumerateArray())
                        if (TryMapReservationRec(rec, subId, out var mapped))
                            results.Add(mapped);
                }

                url = root.TryGetProperty("nextLink", out var nl) && nl.ValueKind == JsonValueKind.String
                    ? nl.GetString() ?? string.Empty
                    : string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch reservation recommendations for subscription {SubId}.", subId);
        }
        return results;
    }

    private bool TryMapReservationRec(JsonElement rec, string subId, out ReservationPurchaseRecommendation mapped)
    {
        mapped = default!;
        if (!rec.TryGetProperty("properties", out var p)) return false;

        var sku   = rec.TryGetProperty("sku", out var skuEl) && skuEl.ValueKind == JsonValueKind.String
                        ? skuEl.GetString() ?? string.Empty
                        : PropStr(p, "skuName");
        var term  = PropStr(p, "term");
        var scope = PropStr(p, "scope");
        var resourceType = PropStr(p, "resourceType");
        var lookBack = p.TryGetProperty("lookBackPeriod", out var lb)
            ? lb.ValueKind == JsonValueKind.Number ? $"Last{lb.GetInt32()}Days" : lb.GetString() ?? string.Empty
            : string.Empty;

        var quantity = PropDecimal(p, "recommendedQuantity");

        // netSavings is a plain number (legacy) or an { currency, value } object (modern).
        var (netSavings, currency) = ReadAmount(p, "netSavings");

        var normalized = NormaliseCurrency(netSavings, currency);

        // Skip noise: recommendations with no quantity or no savings aren't actionable.
        if (quantity <= 0 && netSavings <= 0) return false;

        mapped = new ReservationPurchaseRecommendation(
            SubscriptionId:      subId,
            ResourceType:        resourceType,
            Sku:                 sku,
            Term:                term,
            Scope:               scope,
            LookBackPeriod:      lookBack,
            RecommendedQuantity: quantity,
            NetSavings:          netSavings,
            NormalizedNetSavings: normalized,
            Currency:            string.IsNullOrEmpty(currency) ? _options.TargetCurrency : currency);
        return true;
    }

    // ── Capacity reservation orders (expiry) ─────────────────────────────────

    private async Task<List<ReservationOrderInfo>> FetchReservationOrdersAsync(CancellationToken ct)
    {
        var results = new List<ReservationOrderInfo>();

        // Reservation orders are listed per tenant (not per subscription), so call once for each
        // distinct in-scope tenant authority with that tenant's token.
        var targets = await ResolveTargetsAsync(ct);
        var tenants = targets
            .Select(t => t.TenantId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty(null)
            .ToList();

        foreach (var tenantId in tenants)
        {
            try
            {
                using var client = _httpFactory.CreateClient("AzureMgmt");
                var token = await _tokenService.GetAccessTokenAsync(tenantId, ct);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var url = $"https://management.azure.com/providers/Microsoft.Capacity/reservationOrders" +
                          $"?api-version={CapacityApiVersion}";

                while (!string.IsNullOrEmpty(url))
                {
                    using var response = await client.GetAsync(url, ct);

                    if (response.StatusCode == HttpStatusCode.NoContent) break;

                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                            _accessDenied[ScopeKey] = true;
                        var payload = await response.Content.ReadAsStringAsync(ct);
                        _logger.LogWarning(
                            "Reservation orders returned {Status}. Body: {Body}", (int)response.StatusCode, payload);
                        break;
                    }

                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    var root = doc.RootElement;

                    if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var order in value.EnumerateArray())
                            results.Add(MapReservationOrder(order));
                    }

                    url = root.TryGetProperty("nextLink", out var nl) && nl.ValueKind == JsonValueKind.String
                        ? nl.GetString() ?? string.Empty
                        : string.Empty;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch reservation orders for tenant {Tenant}.", tenantId ?? "(home)");
            }
        }

        return results.OrderBy(r => r.ExpiryDate ?? DateTime.MaxValue).ToList();
    }

    private static ReservationOrderInfo MapReservationOrder(JsonElement order)
    {
        var name = order.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? string.Empty
            : string.Empty;

        var p = order.TryGetProperty("properties", out var props) ? props : default;

        DateTime? expiry = null;
        foreach (var field in new[] { "expiryDateTime", "expiryDate" })
        {
            if (p.ValueKind == JsonValueKind.Object &&
                p.TryGetProperty(field, out var e) && e.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(e.GetString(), out var parsed))
            {
                expiry = parsed.ToUniversalTime();
                break;
            }
        }

        return new ReservationOrderInfo(
            OrderId:           name,
            DisplayName:       PropStr(p, "displayName"),
            Term:              PropStr(p, "term"),
            ProvisioningState: PropStr(p, "provisioningState"),
            ExpiryDate:        expiry,
            Quantity:          (int)PropDecimal(p, "originalQuantity"));
    }

    // ── small JSON / currency helpers ────────────────────────────────────────

    private static string PropStr(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static decimal PropDecimal(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDecimal()
            : 0m;

    /// <summary>Reads a field that is either a plain number or an { currency, value } amount object.</summary>
    private static (decimal Value, string Currency) ReadAmount(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v))
            return (0m, string.Empty);

        if (v.ValueKind == JsonValueKind.Number)
            return (v.GetDecimal(), string.Empty);

        if (v.ValueKind == JsonValueKind.Object)
        {
            var value    = v.TryGetProperty("value", out var vv) && vv.ValueKind == JsonValueKind.Number ? vv.GetDecimal() : 0m;
            var currency = v.TryGetProperty("currency", out var cc) && cc.ValueKind == JsonValueKind.String ? cc.GetString() ?? string.Empty : string.Empty;
            return (value, currency);
        }
        return (0m, string.Empty);
    }

    private decimal NormaliseCurrency(decimal cost, string fromCurrency)
    {
        if (string.IsNullOrWhiteSpace(fromCurrency) ||
            fromCurrency.Equals(_options.TargetCurrency, StringComparison.OrdinalIgnoreCase))
            return cost;

        if (_options.ExchangeRates.TryGetValue(fromCurrency, out var rate))
            return cost * rate;

        _logger.LogWarning(
            "No exchange rate configured for currency '{Currency}'. Using 1:1 conversion.", fromCurrency);
        return cost;
    }

    /// <summary>Resolves subscription display names via the cost service's cached lookup.</summary>
    public Task<Dictionary<string, string>> GetSubscriptionNamesAsync(CancellationToken ct = default) =>
        _costService.GetSubscriptionDisplayNamesAsync(ct);
}
