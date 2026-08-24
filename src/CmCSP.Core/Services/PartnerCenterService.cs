using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Typed client for the standalone <b>Partner Center Transfer (PCT)</b> API. It exposes Microsoft
/// Partner Center indirect-reseller data — resellers → customers → CSP subscriptions with the
/// Microsoft list price, already decorated with Ion cost/margin by the PCT service itself. CmCSP
/// uses it to drill into a reseller's customers and their per-SKU pricing, deeper than the Ion
/// Gateway's bootstrap directory.
///
/// <para>Auth: the shared API key is sent as <c>X-Api-Key</c> and the auth mode as <c>X-Auth-Mode</c>
/// (default <c>secureapp</c>); both are configured on the named client in Program.cs. The PCT service
/// acquires its own Partner Center token — CmCSP never handles Partner Center credentials.</para>
///
/// <para>Caching &amp; degradation: on-demand enrichment overlay memoised in <see cref="ICacheService"/>
/// under <c>pct_*</c> keys (outside the cost-cache contract). Returns empty when not configured or on
/// HTTP failure.</para>
/// </summary>
public sealed class PartnerCenterService(
    IHttpClientFactory httpFactory,
    PartnerCenterOptions options,
    ICacheService cache,
    ILogger<PartnerCenterService> logger)
{
    public const string HttpClientName = "PartnerCenter";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary><c>true</c> when a base URL and API key are configured.</summary>
    public bool IsConfigured => options.IsConfigured;

    private TimeSpan CacheTtl => TimeSpan.FromMinutes(Math.Max(1, options.CacheMinutes));

    /// <summary>Lists all indirect resellers linked to the partner account.</summary>
    public async Task<List<PctIndirectReseller>> GetIndirectResellersAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return [];

        const string cacheKey = "pct_resellers";
        if (cache.TryGetValue<List<PctIndirectReseller>>(cacheKey, CacheTtl, out var cached) && cached is not null)
            return cached;

        try
        {
            var client = CreateClient();
            var list = await client.GetFromJsonAsync<List<PctIndirectReseller>>("/api/v1/resellers", JsonOpts, ct) ?? [];
            cache.Set(cacheKey, list, CacheTtl);
            return list;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PCT reseller list fetch failed.");
            return [];
        }
    }

    /// <summary>Lists the customers managed by an indirect reseller (tenant + domain).</summary>
    public async Task<List<PctCustomer>> GetCustomersByResellerAsync(string resellerId, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(resellerId)) return [];

        var cacheKey = $"pct_reseller_customers_{resellerId}";
        if (cache.TryGetValue<List<PctCustomer>>(cacheKey, CacheTtl, out var cached) && cached is not null)
            return cached;

        try
        {
            var client = CreateClient();
            var list = await client.GetFromJsonAsync<List<PctCustomer>>(
                $"/api/v1/resellers/{Uri.EscapeDataString(resellerId)}/customers", JsonOpts, ct) ?? [];
            cache.Set(cacheKey, list, CacheTtl);
            return list;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PCT reseller-customers fetch failed for {Reseller}.", resellerId);
            return [];
        }
    }

    /// <summary>
    /// One page (25 customers) of a reseller's customers, each with all enriched subscriptions.
    /// When <paramref name="ion"/> is true (default) each subscription is decorated with Ion
    /// cost/margin in a single bulk call.
    /// </summary>
    public async Task<List<PctResellerCustomerSubscriptions>> GetResellerSubscriptionsAsync(
        string resellerId, int page = 1, bool ion = true, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(resellerId)) return [];

        try
        {
            var client = CreateClient();
            var url = $"/api/v1/resellers/{Uri.EscapeDataString(resellerId)}/subscriptions?page={page}&ion={ion.ToString().ToLowerInvariant()}";
            return await client.GetFromJsonAsync<List<PctResellerCustomerSubscriptions>>(url, JsonOpts, ct) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PCT reseller subscriptions fetch failed for {Reseller} page {Page}.", resellerId, page);
            return [];
        }
    }

    /// <summary>
    /// A customer's CSP subscriptions with SKU titles resolved and (when <paramref name="ion"/> is
    /// true) Ion cost/margin decorated. Keyed by the customer's Entra tenant GUID.
    /// </summary>
    public async Task<List<PctSubscription>> GetCustomerSubscriptionsEnrichedAsync(
        string customerId, bool ion = true, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(customerId)) return [];

        var cacheKey = $"pct_customer_subs_{customerId}_{ion}";
        if (cache.TryGetValue<List<PctSubscription>>(cacheKey, CacheTtl, out var cached) && cached is not null)
            return cached;

        try
        {
            var client = CreateClient();
            var url = $"/api/v1/customers/{Uri.EscapeDataString(customerId)}/subscriptions/enriched?ion={ion.ToString().ToLowerInvariant()}";
            var list = await client.GetFromJsonAsync<List<PctSubscription>>(url, JsonOpts, ct) ?? [];
            cache.Set(cacheKey, list, CacheTtl);
            return list;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PCT customer subscriptions fetch failed for {Customer}.", customerId);
            return [];
        }
    }

    private HttpClient CreateClient() => httpFactory.CreateClient(HttpClientName);
}
