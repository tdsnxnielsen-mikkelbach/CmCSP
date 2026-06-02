using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Fetches cost data from the Azure Cost Management Query API across all configured
/// subscriptions and normalises currency values into the configured TargetCurrency.
///
/// Design decisions:
///  • Three separate cached datasets (by service, by resource group, by tag) let each
///    dashboard page work from an in-memory list without re-querying the API.
///  • The cache TTL is configurable (default 60 min) to respect API rate limits.
///  • Multi-subscription: the API only accepts one subscription per POST, so we loop
///    over all configured subscription IDs and aggregate the results.
///  • Rate-limit handling: on HTTP 429 we honour the Retry-After header and retry up
///    to MaxRetries times with exponential back-off as fallback.
///  • Pagination: if the API returns nextLink we follow it with GET until exhausted.
///  • Currency normalisation: each row's cost is multiplied by the configured exchange
///    rate to produce NormalizedCost in the TargetCurrency. Unknown currencies fall
///    back to 1:1 with a warning logged.
/// </summary>
public sealed class CostManagementService : ICostManagementService
{
    private const int MaxRetries = 4;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(60);

    // ── Azure Cost Management API hard limits ─────────────────────────────────
    // Source: learn.microsoft.com/azure/cost-management-billing/costs/manage-automation
    //  • /query endpoint: 5 requests per minute per subscription  (429 + Retry-After)
    //  • Daily granularity: max 365 days per request              (400 if exceeded)
    //  • Monthly granularity: max 12 months per request           (400 if exceeded)
    //  • Response payload: ~84,000 records max; overflow via nextLink pagination
    private const int MaxQueryDays     = 365;
    private const int RowCapWarning    = 70_000; // warn when > 70 k of 84 k cap
    // Minimum gap between successive requests to the same subscription.
    // 5 req/min = one per 12 s; we use 13 s to give a comfortable margin.
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(13);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ── cache keys ──────────────────────────────────────────────────────────
    private const string KeyMain        = "cm_main";
    private const string KeyMainAmort   = "cm_main_amort";
    private const string KeyRg          = "cm_rg";
    private const string KeyTag         = "cm_tag";
    private const string KeyBudgets     = "cm_budgets";
    private const string KeyBudgetsSubs = "cm_budgets_subs";
    private const string KeyAdvisor       = "cm_advisor";
    private const string KeyAdvisorScores = "cm_advisor_scores";
    private const string KeySubNames      = "cm_sub_names";

    // Stable GA API versions for endpoints other than the Cost Management query endpoint.
    private const string BudgetsApiVersion      = "2023-11-01";
    private const string AdvisorApiVersion      = "2023-01-01";
    private const string SubscriptionsApiVersion = "2022-12-01";

    // ── per-subscription rate-limit gate ────────────────────────────────────
    // Tracks the last time a real API request was dispatched for each sub.
    // Checked before each ExecuteAsync call to enforce MinRequestInterval.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>
        _lastRequestTime = new();

    private readonly IHttpClientFactory            _httpFactory;
    private readonly AzureStorageCacheService       _cache;
    private readonly AzureTokenService              _tokenService;
    private readonly CostManagementOptions          _options;
    private readonly DataLoadingStateService        _loadingState;
    private readonly ILogger<CostManagementService> _logger;

    public CostManagementService(
        IHttpClientFactory           httpFactory,
        AzureStorageCacheService      cache,
        AzureTokenService             tokenService,
        CostManagementOptions         options,
        DataLoadingStateService        loadingState,
        ILogger<CostManagementService> logger)
    {
        _httpFactory  = httpFactory;
        _cache        = cache;
        _tokenService = tokenService;
        _options      = options;
        _loadingState = loadingState;
        _logger       = logger;
    }

    // ── date range for queries: rolling 365 days → today ───────────────────
    // Daily granularity is hard-capped at 365 days by the API (returns 400 otherwise).
    private static DateOnly QueryStart =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-(MaxQueryDays - 1)));
    private static DateOnly QueryEnd =>
        DateOnly.FromDateTime(DateTime.UtcNow);

    // ── public API ───────────────────────────────────────────────────────────

    public Task<List<CostRow>> GetMainCostDataAsync(CancellationToken ct = default) =>
        GetOrFetchAsync(KeyMain, QueryType.ByService, metric: "ActualCost", ct);

    public Task<List<CostRow>> GetAmortizedMainCostDataAsync(CancellationToken ct = default) =>
        GetOrFetchAsync(KeyMainAmort, QueryType.ByService, metric: "AmortizedCost", ct);

    public Task<List<CostRow>> GetRgCostDataAsync(CancellationToken ct = default) =>
        GetOrFetchAsync(KeyRg, QueryType.ByResourceGroup, metric: "ActualCost", ct);

    public Task<List<CostRow>> GetTagCostDataAsync(CancellationToken ct = default)
    {
        // The Cost Management Query API does not support the TagKey grouping dimension
        // for CSP / indirect subscriptions (returns 400 Bad Request).
        // Tag data is only available through scheduled blob exports (ExportBlob.Enabled = true).
        _loadingState.Update(KeyTag, LoadPhase.Ready, "export-only");
        _logger.LogDebug(
            "Tag Chargeback data skipped in API mode. Enable ExportBlob to access tag-based cost data.");
        return Task.FromResult<List<CostRow>>([]);
    }

    public void InvalidateCache()
    {
        _cache.Remove(KeyMain);
        _cache.Remove(KeyMainAmort);
        _cache.Remove(KeyRg);
        _cache.Remove(KeyTag);
        _cache.Remove(KeyBudgets);
        _cache.Remove(KeyBudgetsSubs);
        _cache.Remove(KeyAdvisor);
        _cache.Remove(KeyAdvisorScores);
        _cache.Remove(KeySubNames);
        // Reset phases so the loading banner re-appears on the next fetch.
        _loadingState.Update(KeyMain, LoadPhase.Idle);
        _loadingState.Update(KeyRg,   LoadPhase.Idle);
        _loadingState.Update(KeyTag,  LoadPhase.Idle);
        _logger.LogInformation("Cost Management cache invalidated.");
    }

    public async Task<List<SubscriptionBudget>> GetSubscriptionBudgetsAsync(CancellationToken ct = default)
    {
        var distinctSubs = _options.SubscriptionIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var currentSubFingerprint = string.Join('|', distinctSubs);

        var ttl = TimeSpan.FromMinutes(_options.CacheExpirationMinutes);
        if (_cache.TryGetValue<List<SubscriptionBudget>>(KeyBudgets, ttl, out var cached) && cached is not null)
        {
            if (_cache.TryGetValue<string>(KeyBudgetsSubs, ttl, out var cachedSubFingerprint)
                && string.Equals(cachedSubFingerprint, currentSubFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Cache hit for {Key}.", KeyBudgets);
                return cached;
            }

            _logger.LogInformation(
                "Budget cache subscription set changed. Refreshing budgets (cached: {CachedSubs}, current: {CurrentSubs}).",
                cachedSubFingerprint ?? "<none>", currentSubFingerprint);
        }

        _logger.LogInformation("Fetching subscription budgets for {Count} subscription(s).",
            _options.SubscriptionIds.Count);

        var results = new List<SubscriptionBudget>();
        using var client = _httpFactory.CreateClient("AzureMgmt");
        var token = await _tokenService.GetAccessTokenAsync(ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        foreach (var subId in distinctSubs)
        {
            try
            {
                var url = $"https://management.azure.com/subscriptions/{subId}" +
                          $"/providers/Microsoft.Consumption/budgets?api-version={BudgetsApiVersion}";

                var perSubCount = 0;
                while (!string.IsNullOrWhiteSpace(url))
                {
                    var response = await client.GetAsync(url, ct);

                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(ct);
                        _logger.LogWarning(
                            "Budgets API returned {Status} for subscription {SubId} – skipping remaining pages. Body: {Body}",
                            (int)response.StatusCode, subId, body);
                        break;
                    }

                    var list = await response.Content
                        .ReadFromJsonAsync<BudgetListResponse>(JsonOpts, ct);

                    if (list?.Value is null || list.Value.Count == 0)
                    {
                        url = list?.NextLink ?? string.Empty;
                        continue;
                    }

                    foreach (var b in list.Value)
                    {
                        if (b.Properties is null) continue;
                        var currency = b.Properties.CurrentSpend?.Unit ?? string.Empty;
                        results.Add(new SubscriptionBudget(
                            SubscriptionId:   subId,
                            BudgetName:       b.Name,
                            Amount:           NormaliseCurrency(b.Properties.Amount, currency),
                            CurrentSpend:     NormaliseCurrency(b.Properties.CurrentSpend?.Amount ?? 0m, currency),
                            TimeGrain:        b.Properties.TimeGrain,
                            OriginalCurrency: currency
                        ));
                        perSubCount++;
                    }

                    url = list.NextLink ?? string.Empty;
                }

                _logger.LogInformation("Found {Count} budget(s) for subscription {SubId}.",
                    perSubCount, subId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch budgets for subscription {SubId}.", subId);
            }
        }

        _cache.Set(KeyBudgets, results, ttl);
        _cache.Set(KeyBudgetsSubs, currentSubFingerprint, ttl);
        return results;
    }

    public async Task<List<AdvisorRecommendation>> GetAdvisorRecommendationsAsync(CancellationToken ct = default)
    {
        var ttl = TimeSpan.FromMinutes(_options.CacheExpirationMinutes);
        if (_cache.TryGetValue<List<AdvisorRecommendation>>(KeyAdvisor, ttl, out var cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for {Key}.", KeyAdvisor);
            return cached;
        }

        _logger.LogInformation("Fetching Advisor Cost recommendations for {Count} subscription(s) in parallel.",
            _options.SubscriptionIds.Count);

        using var client = _httpFactory.CreateClient("AzureMgmt");
        var token = await _tokenService.GetAccessTokenAsync(ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var tasks = _options.SubscriptionIds
            .Select(subId => FetchAdvisorRecsForSubAsync(client, subId, ct));
        var perSub = await Task.WhenAll(tasks);
        var results = perSub.SelectMany(r => r).ToList();

        _cache.Set(KeyAdvisor, results, ttl);
        return results;
    }

    private async Task<List<AdvisorRecommendation>> FetchAdvisorRecsForSubAsync(
        HttpClient client, string subId, CancellationToken ct)
    {
        var subResults = new List<AdvisorRecommendation>();
        try
        {
            var subName = await GetSubscriptionDisplayNameAsync(client, subId, ct);

            var url = $"https://management.azure.com/subscriptions/{subId}" +
                      $"/providers/Microsoft.Advisor/recommendations" +
                      $"?$filter=Category eq 'Cost'&api-version={AdvisorApiVersion}";

            while (!string.IsNullOrEmpty(url))
            {
                var response = await client.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "Advisor API returned {Status} for subscription {SubId} – skipping. Body: {Body}",
                        (int)response.StatusCode, subId, body);
                    break;
                }

                var list = await response.Content
                    .ReadFromJsonAsync<AdvisorListResponse>(JsonOpts, ct);

                if (list?.Value is not null)
                {
                    foreach (var rec in list.Value)
                    {
                        if (rec.Properties is null) continue;

                        var ext      = rec.Properties.ExtendedProperties;
                        var rawSaving = ext is not null && ext.TryGetValue("annualSavingsAmount", out var s)
                            ? decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                                               System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m
                            : 0m;
                        var currency = ext is not null && ext.TryGetValue("savingsCurrency", out var c) ? c : string.Empty;

                        subResults.Add(new AdvisorRecommendation
                        {
                            SubscriptionId          = subId,
                            SubscriptionName        = subName,
                            Impact                  = rec.Properties.Impact        ?? string.Empty,
                            ImpactedField           = rec.Properties.ImpactedField  ?? string.Empty,
                            ImpactedValue           = rec.Properties.ImpactedValue  ?? string.Empty,
                            Problem                 = rec.Properties.ShortDescription?.Problem  ?? string.Empty,
                            Solution                = rec.Properties.ShortDescription?.Solution ?? string.Empty,
                            AnnualSavingsAmount     = rawSaving,
                            SavingsCurrency         = currency,
                            NormalizedAnnualSavings = NormaliseCurrency(rawSaving, currency),
                            ResourceId              = rec.Properties.ResourceMetadata?.ResourceId ?? string.Empty
                        });
                    }
                }

                url = list?.NextLink ?? string.Empty;
            }

            _logger.LogInformation(
                "Fetched {Count} Advisor Cost recommendation(s) for subscription {SubId}.",
                subResults.Count, subId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Advisor recommendations for subscription {SubId}.", subId);
        }
        return subResults;
    }

    /// <summary>
    /// Calls GET /subscriptions/{id} to resolve the human-readable display name.
    /// Falls back to the raw subscription ID if the call fails.
    /// </summary>
    private async Task<string> GetSubscriptionDisplayNameAsync(
        HttpClient client, string subscriptionId, CancellationToken ct)
    {
        try
        {
            var url = $"https://management.azure.com/subscriptions/{subscriptionId}" +
                      $"?api-version={SubscriptionsApiVersion}";
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return subscriptionId;
            var info = await response.Content
                .ReadFromJsonAsync<SubscriptionInfoResponse>(JsonOpts, ct);
            return string.IsNullOrWhiteSpace(info?.DisplayName) ? subscriptionId : info.DisplayName;
        }
        catch
        {
            return subscriptionId;
        }
    }

    public async Task<Dictionary<string, string>> GetSubscriptionDisplayNamesAsync(CancellationToken ct = default)
    {
        var ttl = TimeSpan.FromMinutes(_options.CacheExpirationMinutes);
        if (_cache.TryGetValue<Dictionary<string, string>>(KeySubNames, ttl, out var cached) && cached is not null)
        {
            // Bypass the cache if any current subscription IDs are missing from it
            // (e.g. a subscription was added through the UI after the cache was populated).
            if (_options.SubscriptionIds.All(id => cached.ContainsKey(id)))
                return cached;
        }

        using var client = _httpFactory.CreateClient("AzureMgmt");
        var token = await _tokenService.GetAccessTokenAsync(ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var tasks = _options.SubscriptionIds.Select(async subId =>
        {
            var name = await GetSubscriptionDisplayNameAsync(client, subId, ct);
            return (subId, name);
        });
        var pairs = await Task.WhenAll(tasks);
        var result = pairs.ToDictionary(p => p.subId, p => p.name, StringComparer.OrdinalIgnoreCase);

        _cache.Set(KeySubNames, result, ttl);
        return result;
    }

    public async Task<List<AdvisorCategoryScore>> GetAdvisorScoresAsync(CancellationToken ct = default)
    {
        var ttl = TimeSpan.FromMinutes(_options.CacheExpirationMinutes);
        if (_cache.TryGetValue<List<AdvisorCategoryScore>>(KeyAdvisorScores, ttl, out var cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for {Key}.", KeyAdvisorScores);
            return cached;
        }

        _logger.LogInformation("Fetching Advisor scores for {Count} subscription(s) in parallel.",
            _options.SubscriptionIds.Count);

        using var client = _httpFactory.CreateClient("AzureMgmt");
        var token = await _tokenService.GetAccessTokenAsync(ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var tasks = _options.SubscriptionIds
            .Select(subId => FetchAdvisorScoresForSubAsync(client, subId, ct));
        var perSub = await Task.WhenAll(tasks);
        var results = perSub.SelectMany(r => r).ToList();

        if (results.Count == 0)
            _logger.LogWarning(
                "Advisor Score API returned no data for any subscription. " +
                "Verify the app registration has the Reader role on each subscription " +
                "and that Azure Advisor is enabled. Check for earlier warnings per subscription.");

        _cache.Set(KeyAdvisorScores, results, ttl);
        return results;
    }

    // Known top-level Advisor category names as returned by the API (PascalCase).
    // GUID entries are individual controls — we skip those.
    private static readonly Dictionary<string, string> AdvisorCategoryNormMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Advisor"]                = "advisor",              // overall aggregate
            ["Cost"]                   = "cost",
            ["Security"]               = "security",
            ["HighAvailability"]       = "reliability",          // portal label: "Reliability"
            ["Reliability"]            = "reliability",
            ["OperationalExcellence"]  = "operationalExcellence",
            ["Performance"]            = "performance",
        };

    private async Task<List<AdvisorCategoryScore>> FetchAdvisorScoresForSubAsync(
        HttpClient client, string subId, CancellationToken ct)
    {
        var subResults = new List<AdvisorCategoryScore>();
        try
        {
            var subName = await GetSubscriptionDisplayNameAsync(client, subId, ct);

            var url = $"https://management.azure.com/subscriptions/{subId}" +
                      $"/providers/Microsoft.Advisor/advisorScore?api-version={AdvisorApiVersion}";

            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);

                // Some subscriptions legitimately return 404 NotFound with
                // "Advisor score data is not available" (for example when
                // Advisor has not produced score data yet). Treat this as a
                // no-data condition, not a permissions error.
                if ((int)response.StatusCode == 404
                    && body.Contains("Advisor score data is not available", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Advisor Score API has no score data yet for subscription {SubId}. " +
                        "Skipping this subscription. Body: {Body}",
                        subId, body);
                    return subResults;
                }

                _logger.LogWarning(
                    "Advisor Score API returned {Status} for subscription {SubId} – skipping. " +
                    "Ensure the Reader role is assigned to the app registration. Body: {Body}",
                    (int)response.StatusCode, subId, body);
                return subResults;
            }

            var rawJson = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("Advisor Score raw response for {SubId}: {Json}", subId, rawJson);

            using var doc = JsonDocument.Parse(rawJson);

            if (!doc.RootElement.TryGetProperty("value", out var valueArray)
                || valueArray.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "Advisor Score API response for {SubId} has no 'value' array.", subId);
                return subResults;
            }

            foreach (var item in valueArray.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameProp)) continue;
                var rawCategory = nameProp.GetString();
                if (string.IsNullOrEmpty(rawCategory)) continue;

                // Skip GUID-named individual controls — only keep top-level categories.
                if (!AdvisorCategoryNormMap.TryGetValue(rawCategory, out var normalizedCategory))
                    continue;

                double? score = null;
                double  consumptionUnits = 0d;

                if (item.TryGetProperty("properties", out var props)
                    && props.ValueKind == JsonValueKind.Object)
                {
                    // Try "lastRefreshedScore" first (always present), then "score" as fallback.
                    foreach (var scoreKey in new[] { "lastRefreshedScore", "score" })
                    {
                        if (!props.TryGetProperty(scoreKey, out var scoreObj)
                            || scoreObj.ValueKind != JsonValueKind.Object) continue;

                        // The score value is in a property named "score" (not "current").
                        if (scoreObj.TryGetProperty("score", out var cur)
                            && cur.ValueKind == JsonValueKind.Number)
                        {
                            score = cur.GetDouble();
                            if (scoreObj.TryGetProperty("consumptionUnits", out var cu)
                                && cu.ValueKind == JsonValueKind.Number)
                                consumptionUnits = cu.GetDouble();
                            break;
                        }
                    }
                }

                subResults.Add(new AdvisorCategoryScore
                {
                    SubscriptionId   = subId,
                    SubscriptionName = subName,
                    Category         = normalizedCategory,
                    Score            = score,
                    ConsumptionUnits = consumptionUnits
                });
            }

            _logger.LogInformation(
                "Fetched Advisor scores for subscription {SubId} ({Count} categories: {Categories}).",
                subId, subResults.Count,
                string.Join(", ", subResults.Select(s => $"{s.Category}={s.Score:F0}")));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Advisor scores for subscription {SubId}.", subId);
        }
        return subResults;
    }

    // ── internal fetch pipeline ──────────────────────────────────────────────

    private async Task<List<CostRow>> GetOrFetchAsync(
        string cacheKey, QueryType type, string metric, CancellationToken ct)
    {
        if (_cache.TryGetValue<List<CostRow>>(cacheKey, TimeSpan.FromMinutes(_options.CacheExpirationMinutes), out var cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for {Key}.", cacheKey);
            // Ensure the UI shows Ready even when the warmup service didn't run
            // (e.g. the page called the service before the background service fired).
            if (_loadingState.For(cacheKey)?.Phase != LoadPhase.Ready)
                _loadingState.Update(cacheKey, LoadPhase.Ready,
                    $"{cached.Count:N0} rows (cached)");
            return cached;
        }

        _logger.LogInformation("Cache miss for {Key}. Fetching from API…", cacheKey);
        _loadingState.Update(cacheKey, LoadPhase.Loading);

        var allRows = new List<CostRow>();
        bool anyError = false;

        if (_options.SubscriptionIds.Count == 0)
        {
            _logger.LogWarning("No subscription IDs configured – returning empty dataset.");
            _loadingState.Update(cacheKey, LoadPhase.Failed, "No subscriptions configured");
            return allRows;
        }

        foreach (var subId in _options.SubscriptionIds)
        {
            try
            {
                var rows = await FetchAllPagesAsync(subId, type, metric, ct);
                allRows.AddRange(rows);
                _logger.LogInformation(
                    "Fetched {Count} rows for subscription {SubId} ({Type}).",
                    rows.Count, subId, type);
            }
            catch (Exception ex)
            {
                anyError = true;
                _logger.LogError(ex,
                    "Failed to fetch cost data for subscription {SubId} ({Type}). Skipping.",
                    subId, type);
            }
        }

        var expiry = TimeSpan.FromMinutes(_options.CacheExpirationMinutes);
        _cache.Set(cacheKey, allRows, expiry);
        _loadingState.Update(
            cacheKey,
            anyError && allRows.Count == 0 ? LoadPhase.Failed : LoadPhase.Ready,
            anyError && allRows.Count == 0 ? "fetch failed" : $"{allRows.Count:N0} rows");
        _logger.LogInformation(
            "Cached {Total} combined rows under '{Key}' for {Min} min.",
            allRows.Count, cacheKey, _options.CacheExpirationMinutes);

        return allRows;
    }

    /// <summary>Handles pagination: initial POST then GET for each nextLink.</summary>
    private async Task<List<CostRow>> FetchAllPagesAsync(
        string subscriptionId, QueryType type, string metric, CancellationToken ct)
    {
        var allRows = new List<CostRow>();
        var body    = BuildQueryBody(type, metric);
        var postUrl = $"https://management.azure.com/subscriptions/{subscriptionId}" +
                      $"/providers/Microsoft.CostManagement/query?api-version={_options.ApiVersion}";

        string? nextLink = null;
        bool    isFirst  = true;
        int     page     = 0;

        do
        {
            // Enforce MinRequestInterval between successive calls to this subscription
            // to stay comfortably inside the 5-req/min per-subscription rate limit.
            await ThrottleAsync(subscriptionId, ct);

            var (rows, next) = isFirst
                ? await ExecuteAsync(postUrl, body, isGet: false, subscriptionId, type, ct)
                : await ExecuteAsync(nextLink!, body: null, isGet: true, subscriptionId, type, ct);

            allRows.AddRange(rows);
            nextLink = next;
            isFirst  = false;
            page++;

            if (allRows.Count >= RowCapWarning)
                _logger.LogWarning(
                    "Subscription {SubId} ({Type}) has returned {Count} rows after {Pages} page(s), " +
                    "approaching the API's ~84,000-record cap. Consider narrowing the date range.",
                    subscriptionId, type, allRows.Count, page);

        } while (!string.IsNullOrEmpty(nextLink));

        return allRows;
    }

    /// <summary>
    /// Sleeps until at least <see cref="MinRequestInterval"/> has elapsed since the
    /// last request for <paramref name="subscriptionId"/>, then records the new timestamp.
    /// This keeps us inside the 5-requests-per-minute per-subscription quota.
    /// </summary>
    private async Task ThrottleAsync(string subscriptionId, CancellationToken ct)
    {
        var now  = DateTime.UtcNow;
        var last = _lastRequestTime.GetOrAdd(subscriptionId, DateTime.MinValue);
        var gap  = now - last;

        if (gap < MinRequestInterval)
        {
            var wait = MinRequestInterval - gap;
            _logger.LogDebug(
                "Rate-limit pacing: waiting {Ms}ms before next request for {SubId}.",
                (int)wait.TotalMilliseconds, subscriptionId);
            await Task.Delay(wait, ct);
        }

        _lastRequestTime[subscriptionId] = DateTime.UtcNow;
    }

    /// <summary>
    /// Executes a single API request with retry logic for rate limiting (429)
    /// and transient errors.
    /// </summary>
    private async Task<(List<CostRow> Rows, string? NextLink)> ExecuteAsync(
        string url,
        object? body,
        bool isGet,
        string subscriptionId,
        QueryType type,
        CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("AzureMgmt");
        var token = await _tokenService.GetAccessTokenAsync(ct);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        int rateLimitRetries = 0;
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                HttpResponseMessage response = isGet
                    ? await client.GetAsync(url, ct)
                    : await client.PostAsJsonAsync(url, body, JsonOpts, ct);

                if ((int)response.StatusCode == 429)
                {
                    if (rateLimitRetries >= MaxRetries)
                    {
                        _logger.LogError(
                            "Rate-limited too many times on subscription {SubId}. Giving up after {Count} rate-limit retries.",
                            subscriptionId, rateLimitRetries);
                        throw new InvalidOperationException(
                            $"Query rate-limited after {rateLimitRetries} retries for subscription {subscriptionId}.");
                    }

                    var delay = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate
                            ? retryDate - DateTimeOffset.UtcNow
                            : DefaultRetryDelay);
                    if (delay <= TimeSpan.Zero) delay = DefaultRetryDelay;

                    _logger.LogWarning(
                        "Rate-limited on subscription {SubId}. Waiting {Seconds}s (attempt {Attempt}/{Max}, rate-limit retry {RlRetry}/{RlMax}).",
                        subscriptionId, delay.TotalSeconds, attempt + 1, MaxRetries, rateLimitRetries + 1, MaxRetries);

                    await Task.Delay(delay, ct);
                    rateLimitRetries++;

                    // Refresh token in case it expired during the wait
                    token = await _tokenService.GetAccessTokenAsync(ct);
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);

                    // Do not consume a retry attempt for 429 — the wait itself is the backoff.
                    attempt--;
                    continue;
                }

                // 400 Bad Request = malformed query body – retrying won't help.
                if ((int)response.StatusCode == 400)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);

                    // CSP / indirect subscriptions do not support 'TagKey' as a grouping dimension.
                    // Return empty gracefully rather than throwing; the other subscriptions still contribute data.
                    if (errorBody.Contains("TagKey", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "Subscription {SubId}: 'TagKey' grouping is not supported (likely a CSP or indirect " +
                            "subscription). Tag Chargeback data will be unavailable for this subscription.",
                            subscriptionId);
                        return ([], null);
                    }

                    _logger.LogError(
                        "API returned 400 Bad Request for subscription {SubId} ({Type}).\nURL: {Url}\nResponse body: {Body}",
                        subscriptionId, type, url, errorBody);
                    throw new InvalidOperationException(
                        $"Cost Management API rejected the query (400 Bad Request) for subscription {subscriptionId}. " +
                        $"See logs for the full API error. Body: {errorBody}");
                }

                // Any other non-success status – log body and let EnsureSuccessStatusCode throw.
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogError(
                        "API returned {Status} for subscription {SubId} ({Type}). Response body: {Body}",
                        (int)response.StatusCode, subscriptionId, type, errorBody);
                }

                response.EnsureSuccessStatusCode();

                var apiResponse = await response.Content
                    .ReadFromJsonAsync<CostApiResponse>(JsonOpts, ct);

                if (apiResponse?.Properties is null)
                    return ([], null);

                var rows = ParseRows(apiResponse.Properties, subscriptionId, type);
                return (rows, apiResponse.Properties.NextLink);
            }
            catch (InvalidOperationException)
            {
                // InvalidOperationException is thrown above for 400 Bad Request – do not retry.
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetries - 1 && !ct.IsCancellationRequested)
            {
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                _logger.LogWarning(ex,
                    "Request failed for {SubId} (attempt {Attempt}/{Max}). Retrying in {Seconds}s.",
                    subscriptionId, attempt + 1, MaxRetries, backoff.TotalSeconds);
                await Task.Delay(backoff, ct);
            }
        }

        throw new InvalidOperationException(
            $"Query failed after {MaxRetries} attempts for subscription {subscriptionId}.");
    }

    // ── query body builder ───────────────────────────────────────────────────

    private object BuildQueryBody(QueryType type, string metric = "ActualCost")
    {
        // The API supports max 2 grouping dimensions per request.
        var grouping = type switch
        {
            QueryType.ByService => new[]
            {
                new { type = "Dimension", name = "SubscriptionName" },
                new { type = "Dimension", name = "MeterCategory" }
            },
            QueryType.ByResourceGroup => new[]
            {
                new { type = "Dimension", name = "SubscriptionName" },
                new { type = "Dimension", name = "ResourceGroupName" }
            },
            QueryType.ByTag => new[]
            {
                new { type = "Dimension", name = "SubscriptionName" },
                new { type = "Dimension", name = "TagKey" }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        return new
        {
            type      = metric,
            timeframe = "Custom",
            timePeriod = new
            {
                from = QueryStart.ToString("yyyy-MM-dd"),
                to   = QueryEnd.ToString("yyyy-MM-dd")
            },
            dataset = new
            {
                granularity = "Daily",
                aggregation = new
                {
                    totalCost = new { name = "PreTaxCost", function = "Sum" }
                },
                grouping
            }
        };
    }

    // ── response parser ──────────────────────────────────────────────────────

    private List<CostRow> ParseRows(
        CostApiProperties props, string subscriptionId, QueryType type)
    {
        // Build a lowercase name → column-index map for robust parsing.
        var colMap = props.Columns
            .Select((c, i) => (Name: c.Name.ToLowerInvariant(), Index: i))
            .ToDictionary(x => x.Name, x => x.Index);

        var rows = new List<CostRow>(props.Rows.Count);

        foreach (var row in props.Rows)
        {
            var cost         = GetDecimal(row, colMap, "pretaxcost");
            var currency     = GetString(row, colMap, "currency");
            var dateInt      = GetInt(row, colMap, "usagedate");
            var subName      = GetString(row, colMap, "subscriptionname");

            if (!TryParseDate(dateInt, out var date))
                continue;

            var normalised = NormaliseCurrency(cost, currency);

            var costRow = new CostRow
            {
                Date             = date,
                Cost             = cost,
                Currency         = currency,
                NormalizedCost   = normalised,
                SubscriptionId   = subscriptionId,
                SubscriptionName = subName
            };

            switch (type)
            {
                case QueryType.ByService:
                    costRow.ServiceName = GetString(row, colMap, "metercategory");
                    break;
                case QueryType.ByResourceGroup:
                    costRow.ResourceGroupName = GetString(row, colMap, "resourcegroupname");
                    break;
                case QueryType.ByTag:
                    costRow.Tag = GetString(row, colMap, "tagkey");
                    break;
            }

            rows.Add(costRow);
        }

        return rows;
    }

    // ── currency normalisation ───────────────────────────────────────────────

    private decimal NormaliseCurrency(decimal cost, string fromCurrency)
    {
        if (string.IsNullOrWhiteSpace(fromCurrency) ||
            fromCurrency.Equals(_options.TargetCurrency, StringComparison.OrdinalIgnoreCase))
            return cost;

        if (_options.ExchangeRates.TryGetValue(fromCurrency, out var rate))
            return cost * rate;

        _logger.LogWarning(
            "No exchange rate configured for currency '{Currency}'. Using 1:1 conversion.",
            fromCurrency);
        return cost;
    }

    // ── column value helpers ─────────────────────────────────────────────────

    private static decimal GetDecimal(
        List<JsonElement> row, Dictionary<string, int> map, string col)
    {
        if (map.TryGetValue(col, out var idx) && idx < row.Count)
        {
            var el = row[idx];
            return el.ValueKind == JsonValueKind.Number ? el.GetDecimal() : 0m;
        }
        return 0m;
    }

    private static string GetString(
        List<JsonElement> row, Dictionary<string, int> map, string col)
    {
        if (map.TryGetValue(col, out var idx) && idx < row.Count)
        {
            var el = row[idx];
            return el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;
        }
        return string.Empty;
    }

    private static int GetInt(
        List<JsonElement> row, Dictionary<string, int> map, string col)
    {
        if (map.TryGetValue(col, out var idx) && idx < row.Count)
        {
            var el = row[idx];
            return el.ValueKind == JsonValueKind.Number ? el.GetInt32() : 0;
        }
        return 0;
    }

    /// <summary>Parses a UsageDate integer in YYYYMMDD format.</summary>
    private static bool TryParseDate(int dateInt, out DateTime date)
    {
        if (dateInt == 0) { date = default; return false; }
        try
        {
            var s = dateInt.ToString("D8");
            date = new DateTime(
                int.Parse(s[..4]),
                int.Parse(s[4..6]),
                int.Parse(s[6..8]),
                0, 0, 0, DateTimeKind.Utc);
            return true;
        }
        catch
        {
            date = default;
            return false;
        }
    }
}

// ── internal enum ────────────────────────────────────────────────────────────
internal enum QueryType { ByService, ByResourceGroup, ByTag }
