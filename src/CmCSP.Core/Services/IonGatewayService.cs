using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Typed client for the TD SYNNEX <b>Ion Gateway</b> — the enrichment hub that fuses Ion
/// (StreamOne) cost/margin with Partner Center list price. CmCSP calls it to attach the buy
/// price/margin it does not hold natively to its Azure cost, and to bootstrap the customers it
/// does not yet know from the gateway directory.
///
/// <para>Caching: like <see cref="OptimizationService"/>, this is an <b>on-demand enrichment
/// overlay</b>, not part of the collector-owned cost-cache contract (see the services-cache
/// instructions). Responses are memoised in <see cref="ICacheService"/> under <c>ion_*</c> keys
/// with a short TTL so page loads are cheap; the nightly <c>CostCollectorJob</c> does not produce
/// these keys and never needs to.</para>
///
/// <para>Graceful degradation: when <see cref="IonGatewayOptions.IsConfigured"/> is false (no
/// base URL / API key) every method returns an empty result and the dashboard shows native cost
/// only. Transport/HTTP failures are logged and swallowed the same way.</para>
/// </summary>
public sealed class IonGatewayService(
    IHttpClientFactory httpFactory,
    IonGatewayOptions options,
    ICacheService cache,
    ILogger<IonGatewayService> logger)
{
    public const string HttpClientName = "IonGateway";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary><c>true</c> when a base URL and API key are configured.</summary>
    public bool IsConfigured => options.IsConfigured;

    private TimeSpan CacheTtl => TimeSpan.FromMinutes(Math.Max(1, options.CacheMinutes));

    // ── Customers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Bootstrap: pages the full customer directory (Ion + PCT) into a single list. Optional
    /// <paramref name="source"/> = <c>ion</c> | <c>pct</c> scopes to one upstream. Each item carries
    /// the Entra tenant GUID and/or domain needed to enrich or onboard a customer CmCSP does not hold.
    /// </summary>
    public async Task<List<IonDirectoryCustomer>> GetCustomerDirectoryAsync(string? source = null, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];

        var cacheKey = $"ion_directory_{source ?? "all"}";
        if (cache.TryGetValue<List<IonDirectoryCustomer>>(cacheKey, CacheTtl, out var cached) && cached is not null)
            return cached;

        var all = new List<IonDirectoryCustomer>();
        string? pageToken = null;
        var guard = 0; // hard stop against a pathological upstream that never returns a null token
        try
        {
            var client = CreateClient();
            do
            {
                var query = BuildQuery(("source", source), ("pageToken", pageToken));
                var page = await client.GetFromJsonAsync<IonCustomerDirectoryPage>(
                    $"/api/v1/customers/directory{query}", JsonOpts, ct);
                if (page?.Items is { Count: > 0 }) all.AddRange(page.Items);
                pageToken = page?.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken) && ++guard < 200);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ion Gateway customer directory fetch failed; returning {Count} collected so far.", all.Count);
        }

        cache.Set(cacheKey, all, CacheTtl);
        return all;
    }

    /// <summary>
    /// Bulk-enriches customers by Entra tenant GUID (or domain) with their Ion subscriptions
    /// (price/cost/margin). <paramref name="vendor"/> = <c>azure</c> | <c>microsoft</c> | … or a numeric
    /// providerId, applied to every key. Sends at most 500 keys per call.
    /// </summary>
    public async Task<IonSubscriptionsBatchResponse> GetSubscriptionsBatchAsync(
        IReadOnlyCollection<string> keys, string? vendor = "azure", CancellationToken ct = default)
    {
        var empty = new IonSubscriptionsBatchResponse([], []);
        if (!IsConfigured || keys.Count == 0) return empty;

        try
        {
            var client = CreateClient();
            var body = new IonSubscriptionsBatchRequest(keys.Take(500).ToList(), vendor);
            var resp = await client.PostAsJsonAsync("/api/v1/customers/subscriptions", body, JsonOpts, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<IonSubscriptionsBatchResponse>(JsonOpts, ct) ?? empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ion Gateway bulk subscriptions call failed for {Count} keys.", keys.Count);
            return empty;
        }
    }

    /// <summary>
    /// Bulk fused pricing (PCT list price + Ion cost/margin) for customers by Entra tenant GUID or
    /// domain. Sends at most 500 keys per call.
    /// </summary>
    public async Task<FusedPricingBatchResponse> GetFusedPricingBatchAsync(
        IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        var empty = new FusedPricingBatchResponse([], []);
        if (!IsConfigured || keys.Count == 0) return empty;

        try
        {
            var client = CreateClient();
            var body = new FusedPricingBatchRequest(keys.Take(500).ToList());
            var resp = await client.PostAsJsonAsync("/api/v1/pct/customers/pricing", body, JsonOpts, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<FusedPricingBatchResponse>(JsonOpts, ct) ?? empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ion Gateway bulk fused-pricing call failed for {Count} keys.", keys.Count);
            return empty;
        }
    }

    /// <summary>Fused pricing (PCT list price + Ion cost/margin) for one customer by tenant GUID or domain.</summary>
    public async Task<FusedPricingDto?> GetFusedPricingAsync(string key, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(key)) return null;
        try
        {
            var client = CreateClient();
            return await client.GetFromJsonAsync<FusedPricingDto>(
                $"/api/v1/pct/customers/{Uri.EscapeDataString(key)}/pricing", JsonOpts, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ion Gateway fused-pricing call failed for {Key}.", key);
            return null;
        }
    }

    // ── Resellers ────────────────────────────────────────────────────────────────

    /// <summary>Lists Ion indirect-reseller accounts (master data). Optional name filter.</summary>
    public async Task<List<IonReseller>> ListResellersAsync(string? search = null, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];

        var cacheKey = $"ion_resellers_{search ?? "all"}";
        if (cache.TryGetValue<List<IonReseller>>(cacheKey, CacheTtl, out var cached) && cached is not null)
            return cached;

        try
        {
            var client = CreateClient();
            var query = BuildQuery(("search", search));
            var list = await client.GetFromJsonAsync<List<IonReseller>>($"/api/v1/resellers{query}", JsonOpts, ct) ?? [];
            cache.Set(cacheKey, list, CacheTtl);
            return list;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ion Gateway reseller list fetch failed.");
            return [];
        }
    }

    /// <summary>
    /// Every customer's crawled orders (with line-level pricing) under a reseller account id.
    /// Empty until the nightly crawl completes or if the account has no orders.
    /// </summary>
    public async Task<List<IonOrder>> GetResellerOrdersAsync(long accountId, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];

        var cacheKey = $"ion_orders_{accountId}";
        if (cache.TryGetValue<List<IonOrder>>(cacheKey, CacheTtl, out var cached) && cached is not null)
            return cached;

        try
        {
            var client = CreateClient();
            var list = await client.GetFromJsonAsync<List<IonOrder>>(
                $"/api/v1/resellers/{accountId}/orders", JsonOpts, ct) ?? [];
            cache.Set(cacheKey, list, CacheTtl);
            return list;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ion Gateway reseller orders fetch failed for account {Account}.", accountId);
            return [];
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        var client = httpFactory.CreateClient(HttpClientName);
        // Base address + X-Api-Key are configured on the named client in Program.cs.
        return client;
    }

    private static string BuildQuery(params (string Key, string? Value)[] parts)
    {
        var pairs = parts
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}")
            .ToArray();
        return pairs.Length == 0 ? string.Empty : "?" + string.Join("&", pairs);
    }
}
