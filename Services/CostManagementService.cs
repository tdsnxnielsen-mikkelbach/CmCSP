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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ── cache keys ──────────────────────────────────────────────────────────
    private const string KeyMain = "cm_main";
    private const string KeyRg   = "cm_rg";
    private const string KeyTag  = "cm_tag";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly AzureTokenService _tokenService;
    private readonly CostManagementOptions _options;
    private readonly ILogger<CostManagementService> _logger;

    public CostManagementService(
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        AzureTokenService tokenService,
        CostManagementOptions options,
        ILogger<CostManagementService> logger)
    {
        _httpFactory  = httpFactory;
        _cache        = cache;
        _tokenService = tokenService;
        _options      = options;
        _logger       = logger;
    }

    // ── date range for queries: Jan 1st of prior year → today ───────────────
    private static DateOnly QueryStart =>
        DateOnly.FromDateTime(new DateTime(DateTime.UtcNow.Year - 1, 1, 1));
    private static DateOnly QueryEnd =>
        DateOnly.FromDateTime(DateTime.UtcNow);

    // ── public API ───────────────────────────────────────────────────────────

    public Task<List<CostRow>> GetMainCostDataAsync(CancellationToken ct = default) =>
        GetOrFetchAsync(KeyMain, QueryType.ByService, ct);

    public Task<List<CostRow>> GetRgCostDataAsync(CancellationToken ct = default) =>
        GetOrFetchAsync(KeyRg, QueryType.ByResourceGroup, ct);

    public Task<List<CostRow>> GetTagCostDataAsync(CancellationToken ct = default) =>
        GetOrFetchAsync(KeyTag, QueryType.ByTag, ct);

    public void InvalidateCache()
    {
        _cache.Remove(KeyMain);
        _cache.Remove(KeyRg);
        _cache.Remove(KeyTag);
        _logger.LogInformation("Cost Management cache invalidated.");
    }

    // ── internal fetch pipeline ──────────────────────────────────────────────

    private async Task<List<CostRow>> GetOrFetchAsync(
        string cacheKey, QueryType type, CancellationToken ct)
    {
        if (_cache.TryGetValue(cacheKey, out List<CostRow>? cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for {Key}.", cacheKey);
            return cached;
        }

        _logger.LogInformation("Cache miss for {Key}. Fetching from API…", cacheKey);

        var allRows = new List<CostRow>();

        if (_options.SubscriptionIds.Count == 0)
        {
            _logger.LogWarning("No subscription IDs configured – returning empty dataset.");
            return allRows;
        }

        foreach (var subId in _options.SubscriptionIds)
        {
            try
            {
                var rows = await FetchAllPagesAsync(subId, type, ct);
                allRows.AddRange(rows);
                _logger.LogInformation(
                    "Fetched {Count} rows for subscription {SubId} ({Type}).",
                    rows.Count, subId, type);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to fetch cost data for subscription {SubId} ({Type}). Skipping.",
                    subId, type);
            }
        }

        var expiry = TimeSpan.FromMinutes(_options.CacheExpirationMinutes);
        _cache.Set(cacheKey, allRows, expiry);
        _logger.LogInformation(
            "Cached {Total} combined rows under '{Key}' for {Min} min.",
            allRows.Count, cacheKey, _options.CacheExpirationMinutes);

        return allRows;
    }

    /// <summary>Handles pagination: initial POST then GET for each nextLink.</summary>
    private async Task<List<CostRow>> FetchAllPagesAsync(
        string subscriptionId, QueryType type, CancellationToken ct)
    {
        var allRows = new List<CostRow>();
        var body    = BuildQueryBody(type);
        var postUrl = $"https://management.azure.com/subscriptions/{subscriptionId}" +
                      $"/providers/Microsoft.CostManagement/query?api-version={_options.ApiVersion}";

        string? nextLink = null;
        bool    isFirst  = true;

        do
        {
            var (rows, next) = isFirst
                ? await ExecuteAsync(postUrl, body, isGet: false, subscriptionId, type, ct)
                : await ExecuteAsync(nextLink!, body: null, isGet: true, subscriptionId, type, ct);

            allRows.AddRange(rows);
            nextLink = next;
            isFirst  = false;

        } while (!string.IsNullOrEmpty(nextLink));

        return allRows;
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

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                HttpResponseMessage response = isGet
                    ? await client.GetAsync(url, ct)
                    : await client.PostAsJsonAsync(url, body, JsonOpts, ct);

                if ((int)response.StatusCode == 429)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? DefaultRetryDelay;
                    _logger.LogWarning(
                        "Rate-limited on subscription {SubId}. Waiting {Seconds}s (attempt {Attempt}/{Max}).",
                        subscriptionId, delay.TotalSeconds, attempt + 1, MaxRetries);

                    await Task.Delay(delay, ct);

                    // Refresh token in case it expired during the wait
                    token = await _tokenService.GetAccessTokenAsync(ct);
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);

                    continue;
                }

                response.EnsureSuccessStatusCode();

                var apiResponse = await response.Content
                    .ReadFromJsonAsync<CostApiResponse>(JsonOpts, ct);

                if (apiResponse?.Properties is null)
                    return ([], null);

                var rows = ParseRows(apiResponse.Properties, subscriptionId, type);
                return (rows, apiResponse.Properties.NextLink);
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

    private object BuildQueryBody(QueryType type)
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
            type      = "ActualCost",
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
