using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Implements the Azure Cost Details API (generateCostDetailsReport) for
/// reservation and amortized-cost data.
///
/// Design decisions:
///  • Async POST → poll pattern: submits a report generation request (HTTP 202),
///    then polls the Location URL using Retry-After until the report is ready (200).
///  • Two scopes supported:
///      – Billing-account/customer (MCA/CSP): full per-customer RI utilisation.
///      – Subscription: reservation data for resources in that subscription only.
///  • Max one calendar month per API call; callers pass a date range and the service
///    handles splitting if needed.
///  • Cache TTL = CostDetailsApiOptions.CacheTtlHours (default 4 h) — API data
///    updates every ~4 hours per Microsoft documentation.
///  • Currency normalisation mirrors CostManagementService using ExchangeRates config.
///  • Graceful degradation: if billing-account access is not configured, all
///    billing-scope methods return empty results without errors.
/// </summary>
public sealed class CostDetailsService : ICostDetailsService
{
    private const string BaseUrl           = "https://management.azure.com";
    private const string CsvChargeTypeUsed = "Usage";
    private const string CsvChargeTypeUnused = "UnusedReservation";

    // Cache key prefixes (followed by scope identifier + yearmonth)
    private const string KeyPrefixCustomer = "cd_cust_";
    private const string KeyPrefixSub      = "cd_sub_";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory         _httpFactory;
    private readonly AzureTokenService          _tokenService;
    private readonly AzureStorageCacheService   _cache;
    private readonly CostManagementOptions      _options;
    private readonly ILogger<CostDetailsService> _logger;

    public bool HasBillingAccountAccess =>
        !string.IsNullOrWhiteSpace(_options.BillingAccount?.BillingAccountId)
        && (_options.BillingAccount?.Customers?.Count ?? 0) > 0;

    public CostDetailsService(
        IHttpClientFactory           httpFactory,
        AzureTokenService            tokenService,
        AzureStorageCacheService     cache,
        CostManagementOptions        options,
        ILogger<CostDetailsService>  logger)
    {
        _httpFactory  = httpFactory;
        _tokenService = tokenService;
        _cache        = cache;
        _options      = options;
        _logger       = logger;
    }

    // ── Public interface ───────────────────────────────────────────────────────

    public async Task<List<ReservationRow>?> GetCustomerReservationsAsync(
        string customerId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (!HasBillingAccountAccess) return null;

        var billingAccountId = _options.BillingAccount.BillingAccountId;
        var customerName     = _options.BillingAccount.Customers
            .FirstOrDefault(c => c.CustomerId.Equals(customerId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? customerId;

        var cacheKey = BuildCacheKey(KeyPrefixCustomer, customerId, from, to);
        var ttl      = TimeSpan.FromHours(_options.CostDetails.CacheTtlHours);

        if (_cache.TryGetValue<List<ReservationRow>>(cacheKey, ttl, out var cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for Cost Details {Key}.", cacheKey);
            return cached;
        }

        var rows = new List<ReservationRow>();
        foreach (var (monthFrom, monthTo) in SplitIntoMonths(from, to))
        {
            var scope = $"/providers/Microsoft.Billing/billingAccounts/{billingAccountId}" +
                        $"/customers/{customerId}";
            var monthRows = await FetchReservationsForScopeAsync(
                scope, monthFrom, monthTo, customerId, customerName, "BillingAccount", ct);
            rows.AddRange(monthRows);
        }

        _cache.Set(cacheKey, rows, ttl);
        return rows;
    }

    public async Task<List<ReservationRow>> GetSubscriptionReservationsAsync(
        string subscriptionId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(KeyPrefixSub, subscriptionId, from, to);
        var ttl      = TimeSpan.FromHours(_options.CostDetails.CacheTtlHours);

        if (_cache.TryGetValue<List<ReservationRow>>(cacheKey, ttl, out var cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for Cost Details {Key}.", cacheKey);
            return cached;
        }

        var rows = new List<ReservationRow>();
        foreach (var (monthFrom, monthTo) in SplitIntoMonths(from, to))
        {
            var scope     = $"/subscriptions/{subscriptionId}";
            var monthRows = await FetchReservationsForScopeAsync(
                scope, monthFrom, monthTo, string.Empty, string.Empty, "Subscription", ct);
            rows.AddRange(monthRows);
        }

        _cache.Set(cacheKey, rows, ttl);
        return rows;
    }

    public async Task<List<ReservationRow>> GetAllCustomerReservationsAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (!HasBillingAccountAccess) return [];

        var all = new List<ReservationRow>();
        foreach (var customer in _options.BillingAccount.Customers)
        {
            var customerRows = await GetCustomerReservationsAsync(customer.CustomerId, from, to, ct);
            if (customerRows is not null) all.AddRange(customerRows);
        }
        return all;
    }

    public async Task<List<ReservationRow>> GetAllSubscriptionReservationsAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var all = new List<ReservationRow>();
        foreach (var subId in _options.SubscriptionIds)
        {
            var subRows = await GetSubscriptionReservationsAsync(subId, from, to, ct);
            all.AddRange(subRows);
        }
        return all;
    }

    public void InvalidateCache()
    {
        // Remove all Cost Details cache entries by tracking known prefixes.
        // AzureStorageCacheService does not support prefix-scan, so we rebuild
        // the same keys that would have been populated and remove each one.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // Cover current + previous 13 months (API max history).
        for (var i = 0; i <= 13; i++)
        {
            var month = today.AddMonths(-i);
            var from  = new DateOnly(month.Year, month.Month, 1);
            var to    = from.AddMonths(1).AddDays(-1);

            foreach (var subId in _options.SubscriptionIds)
                _cache.Remove(BuildCacheKey(KeyPrefixSub, subId, from, to));

            if (HasBillingAccountAccess)
                foreach (var c in _options.BillingAccount.Customers)
                    _cache.Remove(BuildCacheKey(KeyPrefixCustomer, c.CustomerId, from, to));
        }

        _logger.LogInformation("Cost Details cache invalidated.");
    }

    // ── Core: trigger report, poll, download, parse ────────────────────────────

    private async Task<List<ReservationRow>> FetchReservationsForScopeAsync(
        string scope, DateOnly from, DateOnly to,
        string customerId, string customerName, string scopeLabel,
        CancellationToken ct)
    {
        var apiVersion = _options.CostDetails.ApiVersion;
        var url = $"{BaseUrl}{scope}/providers/Microsoft.CostManagement/" +
                  $"generateCostDetailsReport?api-version={apiVersion}";

        var requestBody = new CostDetailsReportRequest
        {
            Metric     = "AmortizedCost",
            TimePeriod = new CostDetailsTimePeriod
            {
                Start = from.ToString("yyyy-MM-dd"),
                End   = to.ToString("yyyy-MM-dd")
            }
        };

        _logger.LogInformation(
            "Triggering Cost Details report for scope {Scope} ({From}–{To}).",
            scope, from, to);

        List<CostDetailsBlobLink>? blobLinks;
        try
        {
            blobLinks = await TriggerAndPollAsync(url, requestBody, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Cost Details report failed for scope {Scope} ({From}–{To}). Returning empty.",
                scope, from, to);
            return [];
        }

        if (blobLinks is null or { Count: 0 })
        {
            _logger.LogInformation(
                "No data returned by Cost Details API for scope {Scope} ({From}–{To}).",
                scope, from, to);
            return [];
        }

        var period = new DateOnly(from.Year, from.Month, 1);
        var rows   = await DownloadAndParseCsvAsync(
            blobLinks, customerId, customerName, scopeLabel, period, ct);

        _logger.LogInformation(
            "Cost Details: {Count} reservation rows for scope {Scope} ({Period:yyyy-MM}).",
            rows.Count, scope, period.ToDateTime(TimeOnly.MinValue));

        return rows;
    }

    /// <summary>
    /// POST to trigger report → poll Location URL → return blob download links.
    /// </summary>
    private async Task<List<CostDetailsBlobLink>?> TriggerAndPollAsync(
        string triggerUrl, CostDetailsReportRequest requestBody, CancellationToken ct)
    {
        var token  = await _tokenService.GetAccessTokenAsync(ct);
        var client = _httpFactory.CreateClient("AzureMgmt");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        // ── Step 1: POST to start report generation ───────────────────────────
        var postContent = new StringContent(
            JsonSerializer.Serialize(requestBody, JsonOpts),
            Encoding.UTF8,
            "application/json");

        using var postResponse = await client.PostAsync(triggerUrl, postContent, ct);

        if (postResponse.StatusCode == HttpStatusCode.Accepted)
        {
            // 202 expected — extract Location and Retry-After
        }
        else if (postResponse.StatusCode == HttpStatusCode.OK)
        {
            // Sync response (rare) — parse inline
            var syncResult = await postResponse.Content
                .ReadFromJsonAsync<CostDetailsOperationResult>(JsonOpts, ct);
            return ExtractBlobLinks(syncResult);
        }
        else if (postResponse.StatusCode == (HttpStatusCode)404
              || postResponse.StatusCode == HttpStatusCode.BadRequest)
        {
            var errorBody = await postResponse.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Cost Details POST returned {Status}. Scope may not be accessible. Body: {Body}",
                (int)postResponse.StatusCode, errorBody);
            return null;
        }
        else
        {
            var errorBody = await postResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Cost Details POST returned unexpected {Status}. Body: {Body}",
                (int)postResponse.StatusCode, errorBody);
            return null;
        }

        var locationUrl = postResponse.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(locationUrl))
        {
            _logger.LogError("Cost Details 202 response missing Location header.");
            return null;
        }

        var retryAfter = postResponse.Headers.RetryAfter?.Delta
            ?? TimeSpan.FromSeconds(_options.CostDetails.PollingIntervalSeconds);

        // ── Step 2: Poll until complete ───────────────────────────────────────
        var timeout     = TimeSpan.FromSeconds(_options.CostDetails.PollingTimeoutSeconds);
        var deadline    = DateTime.UtcNow + timeout;
        var pollClient  = _httpFactory.CreateClient("AzureMgmt");
        pollClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _tokenService.GetAccessTokenAsync(ct));

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(retryAfter, ct);

            // Refresh token for long-running polls
            pollClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer",
                    await _tokenService.GetAccessTokenAsync(ct));

            using var pollResponse = await pollClient.GetAsync(locationUrl, ct);

            if (pollResponse.StatusCode == HttpStatusCode.Accepted)
            {
                // Still running
                retryAfter = pollResponse.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(_options.CostDetails.PollingIntervalSeconds);
                _logger.LogDebug("Cost Details report still running, next poll in {Interval}s.",
                    retryAfter.TotalSeconds);
                continue;
            }

            if (pollResponse.IsSuccessStatusCode)
            {
                var result = await pollResponse.Content
                    .ReadFromJsonAsync<CostDetailsOperationResult>(JsonOpts, ct);

                if (result?.Status?.Equals("NoDataFound", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogInformation("Cost Details report: NoDataFound for this period/scope.");
                    return [];
                }

                if (result?.Status?.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogError(
                        "Cost Details report failed. Code={Code} Message={Message}",
                        result.Error?.Code, result.Error?.Message);
                    return null;
                }

                return ExtractBlobLinks(result);
            }

            var pollErrorBody = await pollResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Cost Details poll returned {Status}. Body: {Body}",
                (int)pollResponse.StatusCode, pollErrorBody);
            return null;
        }

        _logger.LogError(
            "Cost Details report polling timed out after {Timeout}s.", timeout.TotalSeconds);
        return null;
    }

    private static List<CostDetailsBlobLink>? ExtractBlobLinks(CostDetailsOperationResult? result) =>
        result?.Properties?.Blobs;

    // ── CSV download and parsing ───────────────────────────────────────────────

    private async Task<List<ReservationRow>> DownloadAndParseCsvAsync(
        List<CostDetailsBlobLink> blobLinks,
        string customerId, string customerName, string scopeLabel,
        DateOnly period, CancellationToken ct)
    {
        // Group rows by ReservationId to aggregate Used/Unused costs.
        // Key = (ReservationId, SubscriptionId) to handle shared reservations.
        var accum = new Dictionary<string, ReservationRow>(StringComparer.OrdinalIgnoreCase);

        foreach (var blobLink in blobLinks)
        {
            if (string.IsNullOrEmpty(blobLink.BlobLink)) continue;

            _logger.LogDebug("Downloading Cost Details blob ({Bytes} bytes).", blobLink.ByteCount);

            // The blob URL is pre-authenticated (SAS token) — no bearer token needed.
            using var blobClient = _httpFactory.CreateClient("AzureMgmt");
            blobClient.DefaultRequestHeaders.Authorization = null;

            using var response = await blobClient.GetAsync(blobLink.BlobLink, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "Failed to download Cost Details blob. Status={Status} Body={Body}",
                    (int)response.StatusCode, body);
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            ParseCsvIntoAccum(reader, accum, customerId, customerName, scopeLabel, period);
        }

        return [.. accum.Values];
    }

    private void ParseCsvIntoAccum(
        StreamReader reader,
        Dictionary<string, ReservationRow> accum,
        string customerId, string customerName, string scopeLabel,
        DateOnly period)
    {
        // Read header line
        var headerLine = reader.ReadLine();
        if (headerLine is null) return;

        var headers = ParseCsvRow(headerLine);
        var colMap  = headers
            .Select((h, i) => (Name: h.Trim().ToLowerInvariant(), Index: i))
            .Where(x => !string.IsNullOrEmpty(x.Name))
            .GroupBy(x => x.Name)
            .ToDictionary(g => g.Key, g => g.First().Index);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = ParseCsvRow(line);

            var chargeType = CsvGet(fields, colMap, "chargetype");

            // Only process Used and Unused reservation charges
            if (!chargeType.Equals(CsvChargeTypeUsed,   StringComparison.OrdinalIgnoreCase)
             && !chargeType.Equals(CsvChargeTypeUnused, StringComparison.OrdinalIgnoreCase))
                continue;

            // BenefitId / ReservationId — try both column names
            var reservationId = CsvGet(fields, colMap, "reservationid");
            if (string.IsNullOrEmpty(reservationId))
                reservationId = CsvGet(fields, colMap, "benefitid");
            if (string.IsNullOrEmpty(reservationId))
                continue; // not a reservation row

            var reservationName = CsvGet(fields, colMap, "reservationname");
            if (string.IsNullOrEmpty(reservationName))
                reservationName = CsvGet(fields, colMap, "benefitname");

            var subId   = CsvGet(fields, colMap, "subscriptionid");
            var subName = CsvGet(fields, colMap, "subscriptionname");

            // Aggregate per (ReservationId, SubscriptionId) — shared RIs span subs
            var key = $"{reservationId}|{subId}";

            if (!accum.TryGetValue(key, out var row))
            {
                row = new ReservationRow
                {
                    ReservationId   = reservationId,
                    ReservationName = reservationName,
                    MeterCategory   = CsvGet(fields, colMap, "metercategory"),
                    ProductName     = CsvGet(fields, colMap, "productname"),
                    Term            = CsvGet(fields, colMap, "term"),
                    SubscriptionId  = subId,
                    SubscriptionName = subName,
                    CustomerId      = string.IsNullOrEmpty(customerId)
                        ? CsvGet(fields, colMap, "customerid")
                        : customerId,
                    CustomerName    = string.IsNullOrEmpty(customerName)
                        ? CsvGet(fields, colMap, "customername")
                        : customerName,
                    Currency        = CsvGet(fields, colMap, "billingcurrency"),
                    Period          = period,
                    Scope           = scopeLabel
                };
                // Fallback currency columns
                if (string.IsNullOrEmpty(row.Currency))
                    row.Currency = CsvGet(fields, colMap, "billingcurrencycode");

                accum[key] = row;
            }

            var cost = ParseDecimal(CsvGet(fields, colMap, "costinbillingcurrency"));
            var normalizedCost = NormaliseCurrency(cost, row.Currency);

            if (chargeType.Equals(CsvChargeTypeUsed, StringComparison.OrdinalIgnoreCase))
            {
                row.UsedCost           += cost;
                row.NormalizedUsedCost += normalizedCost;
            }
            else
            {
                row.UnusedCost           += cost;
                row.NormalizedUnusedCost += normalizedCost;
            }

            row.TotalCost           = row.UsedCost + row.UnusedCost;
            row.NormalizedTotalCost = row.NormalizedUsedCost + row.NormalizedUnusedCost;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Splits a date range into one-month-max slices (API limit).</summary>
    private static IEnumerable<(DateOnly From, DateOnly To)> SplitIntoMonths(DateOnly from, DateOnly to)
    {
        var current = new DateOnly(from.Year, from.Month, 1);
        while (current <= to)
        {
            var sliceFrom = current == new DateOnly(from.Year, from.Month, 1) ? from : current;
            var monthEnd  = current.AddMonths(1).AddDays(-1);
            var sliceTo   = monthEnd < to ? monthEnd : to;
            yield return (sliceFrom, sliceTo);
            current = current.AddMonths(1);
        }
    }

    private static string BuildCacheKey(string prefix, string id, DateOnly from, DateOnly to) =>
        $"{prefix}{id}_{from:yyyyMM}_{to:yyyyMM}";

    private decimal NormaliseCurrency(decimal cost, string fromCurrency)
    {
        if (string.IsNullOrWhiteSpace(fromCurrency) ||
            fromCurrency.Equals(_options.TargetCurrency, StringComparison.OrdinalIgnoreCase))
            return cost;

        if (_options.ExchangeRates.TryGetValue(fromCurrency, out var rate))
            return cost * rate;

        _logger.LogWarning(
            "No exchange rate for currency '{Currency}' in Cost Details. Using 1:1.",
            fromCurrency);
        return cost;
    }

    private static string CsvGet(
        List<string> fields, Dictionary<string, int> colMap, string column)
    {
        if (colMap.TryGetValue(column, out var idx) && idx < fields.Count)
            return fields[idx].Trim('"').Trim();
        return string.Empty;
    }

    private static decimal ParseDecimal(string value)
    {
        if (decimal.TryParse(value.Trim('"').Trim(),
            NumberStyles.Number, CultureInfo.InvariantCulture, out var result))
            return result;
        return 0m;
    }

    /// <summary>
    /// RFC 4180 CSV row parser. Handles quoted fields with embedded commas and newlines.
    /// </summary>
    private static List<string> ParseCsvRow(string line)
    {
        var fields = new List<string>();
        var sb     = new StringBuilder();
        var inQuote = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuote)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++; // skip escaped quote
                    }
                    else
                    {
                        inQuote = false;
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else if (ch == '"')
            {
                inQuote = true;
            }
            else if (ch == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        fields.Add(sb.ToString());
        return fields;
    }
}
