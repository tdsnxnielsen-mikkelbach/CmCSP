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
    private readonly ILogger<OptimizationService>    _logger;

    // ── in-process TTL memo (see caching note above) ─────────────────────────
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTime At, List<ResourceInventoryItem> Data)?            _inventory;
    private (DateTime At, List<OrphanedResource> Data)?                 _orphans;
    private (DateTime At, List<ReservationPurchaseRecommendation> Data)? _reservationRecs;
    private (DateTime At, List<ReservationOrderInfo> Data)?             _reservationOrders;

    /// <summary>True when the most recent ARM read was denied (403/404) — the UI shows a Reader banner.</summary>
    public bool LastAccessDenied { get; private set; }

    public OptimizationService(
        IHttpClientFactory          httpFactory,
        AzureTokenService            tokenService,
        ICostManagementService       costService,
        CostManagementOptions        options,
        ILogger<OptimizationService> logger)
    {
        _httpFactory  = httpFactory;
        _tokenService = tokenService;
        _costService  = costService;
        _options      = options;
        _logger       = logger;
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
        if (_inventory is { } c && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_inventory is { } c2 && Fresh(c2.At)) return c2.Data;

            const string query =
                "Resources " +
                "| project id, name, type, resourceGroup, location, subscriptionId, tags " +
                "| order by type asc";

            var rows = await RunResourceGraphAsync(query, ct);
            var items = rows.Select(MapInventoryItem).ToList();
            _inventory = (DateTime.UtcNow, items);
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
        if (_orphans is { } c && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_orphans is { } c2 && Fresh(c2.At)) return c2.Data;

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
            _orphans = (DateTime.UtcNow, items);
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
        if (_reservationRecs is { } c && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_reservationRecs is { } c2 && Fresh(c2.At)) return c2.Data;

            using var client = _httpFactory.CreateClient("AzureMgmt");
            var token = await _tokenService.GetAccessTokenAsync(ct);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var tasks = _options.SubscriptionIds
                .Select(subId => FetchReservationRecsForSubAsync(client, subId, ct));
            var perSub = await Task.WhenAll(tasks);
            var results = perSub
                .SelectMany(r => r)
                .OrderByDescending(r => r.NormalizedNetSavings)
                .ToList();

            _reservationRecs = (DateTime.UtcNow, results);
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
        if (_reservationOrders is { } c && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_reservationOrders is { } c2 && Fresh(c2.At)) return c2.Data;

            var results = await FetchReservationOrdersAsync(ct);
            _reservationOrders = (DateTime.UtcNow, results);
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
        if (_options.SubscriptionIds.Count == 0) return rows;

        try
        {
            using var client = _httpFactory.CreateClient("AzureMgmt");
            var token = await _tokenService.GetAccessTokenAsync(ct);
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
                    subscriptions = _options.SubscriptionIds,
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
                        LastAccessDenied = true;
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

                skipToken = root.TryGetProperty("$skipToken", out var st) && st.ValueKind == JsonValueKind.String
                    ? st.GetString()
                    : null;
            }
            while (!string.IsNullOrEmpty(skipToken) && rows.Count < MaxInventoryRows && !ct.IsCancellationRequested);

            // A successful call clears a stale access-denied flag.
            if (rows.Count > 0) LastAccessDenied = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resource Graph query failed.");
        }
        return rows;
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
        HttpClient client, string subId, CancellationToken ct)
    {
        var results = new List<ReservationPurchaseRecommendation>();
        try
        {
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
                        LastAccessDenied = true;
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
        try
        {
            using var client = _httpFactory.CreateClient("AzureMgmt");
            var token = await _tokenService.GetAccessTokenAsync(ct);
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
                        LastAccessDenied = true;
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
            _logger.LogError(ex, "Failed to fetch reservation orders.");
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
