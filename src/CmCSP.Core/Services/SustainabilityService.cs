using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Phase 8 — Azure sustainability (Carbon Optimization emissions).
///
/// Reads estimated carbon emissions (kg CO₂e, scopes 1–3) for the configured subscriptions from the
/// Carbon Optimization API so the dashboard can report an environmental footprint alongside cost.
/// Three read-only report shapes from a single endpoint, all using the existing management token:
///   • OverallSummaryReport   — latest-month total + month-over-month change (KPIs).
///   • MonthlySummaryReport   — per-month series for the emissions trend chart.
///   • TopItemsSummaryReport  — emissions by Azure resource type (which services drive the footprint).
///
/// The API only serves a rolling ~12-month window with a lag; we discover the exact available range
/// from the service (the validation error reports it) and memo it, so requests never fall outside it.
///
/// Caching note: like <see cref="OptimizationService"/> these are on-demand ARM reads, memoised in a
/// small in-process TTL cache. This deliberately stays outside the cost-cache contract in services-cache.
///
/// Graceful degradation: a Subscription <b>Reader</b> grant (which the app identity already has from
/// Phase 7) is enough to view emissions; the dedicated Carbon Optimization Reader role is the
/// least-privilege alternative. When access is missing the call returns 403/404; we swallow it, log a
/// warning and surface <see cref="LastAccessDenied"/> so the UI shows a banner with an empty state.
/// </summary>
public sealed class SustainabilityService
{
    private const string CarbonApiVersion = "2025-04-01";
    private const string CarbonReportsUrl =
        "https://management.azure.com/providers/Microsoft.Carbon/carbonEmissionReports?api-version=" + CarbonApiVersion;

    private static readonly string[] AllScopes = ["Scope1", "Scope2", "Scope3"];

    private static readonly Regex AvailableRangeRegex = new(
        @"StartDate:\s*(\d{4}-\d{2}-\d{2}).*?EndDate:\s*(\d{4}-\d{2}-\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private readonly IHttpClientFactory             _httpFactory;
    private readonly AzureTokenService               _tokenService;
    private readonly ICostManagementService          _costService;
    private readonly CostManagementOptions           _options;
    private readonly ILogger<SustainabilityService>  _logger;

    // ── in-process TTL memo (see caching note above) ─────────────────────────
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTime At, CarbonEmissionSummary? Data)?      _summary;
    private (DateTime At, List<CarbonEmissionMonth> Data)?   _monthly;
    private (DateTime At, List<CarbonEmissionByType> Data)?  _byType;
    private (DateTime At, List<CarbonEmissionBySubscription> Data)? _bySub;
    private (DateOnly Start, DateOnly End)?                  _range;

    /// <summary>True when the most recent ARM read was denied (403/404) — the UI shows a Reader banner.</summary>
    public bool LastAccessDenied { get; private set; }

    public SustainabilityService(
        IHttpClientFactory             httpFactory,
        AzureTokenService               tokenService,
        ICostManagementService          costService,
        CostManagementOptions           options,
        ILogger<SustainabilityService>  logger)
    {
        _httpFactory  = httpFactory;
        _tokenService = tokenService;
        _costService  = costService;
        _options      = options;
        _logger       = logger;
    }

    private TimeSpan Ttl => TimeSpan.FromMinutes(_options.CacheExpirationMinutes);

    private bool Fresh(DateTime at) => DateTime.UtcNow - at < Ttl;

    // ── 1. Overall summary (KPIs) ────────────────────────────────────────────

    /// <summary>
    /// Latest-month total emissions (kg CO₂e) with the prior month and change ratio. Null when no
    /// data / access (sets <see cref="LastAccessDenied"/>).
    /// </summary>
    public async Task<CarbonEmissionSummary?> GetEmissionSummaryAsync(CancellationToken ct = default)
    {
        if (_summary is { } c && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_summary is { } c2 && Fresh(c2.At)) return c2.Data;

            var range = await ResolveRangeAsync(ct);
            if (range is null) { _summary = (DateTime.UtcNow, null); return null; }

            var body = BuildBody("OverallSummaryReport", range.Value.End, range.Value.End);
            var rows = await PostReportAsync(body, ct);

            CarbonEmissionSummary? summary = null;
            if (rows.Count > 0)
            {
                var el = rows[0];
                summary = new CarbonEmissionSummary(
                    LatestMonthEmissions:       Num(el, "latestMonthEmissions"),
                    PreviousMonthEmissions:     Num(el, "previousMonthEmissions"),
                    MonthOverMonthChangeRatio:  Num(el, "monthOverMonthEmissionsChangeRatio"),
                    LatestMonthLabel:           range.Value.End.ToString("MMM yyyy", CultureInfo.InvariantCulture));
            }

            _summary = (DateTime.UtcNow, summary);
            return summary;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 2. Monthly trend ─────────────────────────────────────────────────────

    /// <summary>Per-month emissions (kg CO₂e) across the available window, oldest first.</summary>
    public async Task<List<CarbonEmissionMonth>> GetMonthlyEmissionsAsync(CancellationToken ct = default)
    {
        if (_monthly is { } c && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_monthly is { } c2 && Fresh(c2.At)) return c2.Data;

            var range = await ResolveRangeAsync(ct);
            if (range is null) { _monthly = (DateTime.UtcNow, []); return []; }

            var body = BuildBody("MonthlySummaryReport", range.Value.Start, range.Value.End);
            var rows = await PostReportAsync(body, ct);

            var months = rows
                .Select(el => new CarbonEmissionMonth(
                    Month:          DateOnly.TryParse(Str(el, "date"), CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out var d) ? d : default,
                    Emissions:      Num(el, "latestMonthEmissions"),
                    CarbonIntensity: Num(el, "carbonIntensity")))
                .Where(m => m.Month != default)
                .OrderBy(m => m.Month)
                .ToList();

            _monthly = (DateTime.UtcNow, months);
            return months;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 3. Emissions by resource type ────────────────────────────────────────

    /// <summary>Latest-month emissions (kg CO₂e) by Azure resource type, highest first (top 10).</summary>
    public async Task<List<CarbonEmissionByType>> GetEmissionsByTypeAsync(CancellationToken ct = default)
    {
        if (_byType is { } c && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_byType is { } c2 && Fresh(c2.At)) return c2.Data;

            var range = await ResolveRangeAsync(ct);
            if (range is null) { _byType = (DateTime.UtcNow, []); return []; }

            var body = BuildBody("TopItemsSummaryReport", range.Value.End, range.Value.End,
                categoryType: "ResourceType", topItems: 10);
            var rows = await PostReportAsync(body, ct);

            var items = rows
                .Select(el => new CarbonEmissionByType(
                    ItemName:                  Str(el, "itemName"),
                    LatestMonthEmissions:      Num(el, "latestMonthEmissions"),
                    PreviousMonthEmissions:    Num(el, "previousMonthEmissions"),
                    MonthOverMonthChangeRatio: Num(el, "monthOverMonthEmissionsChangeRatio")))
                // Drop the API's roll-up bucket for "everything outside the top items".
                .Where(i => !i.ItemName.StartsWith("Others-Exclude", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.LatestMonthEmissions)
                .ToList();

            _byType = (DateTime.UtcNow, items);
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Resolves subscription display names via the cost service's cached lookup.</summary>
    public Task<Dictionary<string, string>> GetSubscriptionNamesAsync(CancellationToken ct = default) =>
        _costService.GetSubscriptionDisplayNamesAsync(ct);

    // ── 4. Emissions by subscription ─────────────────────────────────────────

    /// <summary>
    /// Latest-month emissions (kg CO₂e) by subscription, highest first. Lets the UI show which
    /// subscription (and tenant) drives the footprint. Covers the configured subscriptions the app
    /// identity can read directly.
    /// </summary>
    public async Task<List<CarbonEmissionBySubscription>> GetEmissionsBySubscriptionAsync(CancellationToken ct = default)
    {
        if (_bySub is { } c && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_bySub is { } c2 && Fresh(c2.At)) return c2.Data;

            var range = await ResolveRangeAsync(ct);
            if (range is null) { _bySub = (DateTime.UtcNow, []); return []; }

            var body = BuildBody("TopItemsSummaryReport", range.Value.End, range.Value.End,
                categoryType: "SubscriptionId", topItems: 100);
            var rows = await PostReportAsync(body, ct);

            var items = rows
                .Select(el => new CarbonEmissionBySubscription(
                    SubscriptionId:            Str(el, "itemName"),
                    LatestMonthEmissions:      Num(el, "latestMonthEmissions"),
                    PreviousMonthEmissions:    Num(el, "previousMonthEmissions"),
                    MonthOverMonthChangeRatio: Num(el, "monthOverMonthEmissionsChangeRatio")))
                .Where(i => !i.SubscriptionId.StartsWith("Others-Exclude", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.LatestMonthEmissions)
                .ToList();

            _bySub = (DateTime.UtcNow, items);
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Discovers the rolling window the Carbon API currently serves. The service reports the valid
    /// range in its validation error, so we deliberately request an out-of-range month once and parse
    /// the "available range StartDate/EndDate" from the response. Memoised for the TTL.
    /// </summary>
    private async Task<(DateOnly Start, DateOnly End)?> ResolveRangeAsync(CancellationToken ct)
    {
        if (_range is { } r) return r;
        if (_options.SubscriptionIds.Count == 0) return null;

        try
        {
            using var client = await CreateClientAsync(ct);
            var probe = BuildBody("OverallSummaryReport", new DateOnly(2000, 1, 1), new DateOnly(2000, 1, 1));
            using var content = new StringContent(probe, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(CarbonReportsUrl, content, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                LastAccessDenied = true;
                _logger.LogWarning(
                    "Carbon Optimization API returned {Status} — the identity likely lacks Reader / " +
                    "Carbon Optimization Reader on the target subscriptions.", (int)response.StatusCode);
                return null;
            }

            var match = AvailableRangeRegex.Match(payload);
            if (match.Success &&
                DateOnly.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) &&
                DateOnly.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                _range = (start, end);
                return _range;
            }

            _logger.LogWarning("Could not determine Carbon emissions available date range. Body: {Body}", payload);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve Carbon emissions date range.");
            return null;
        }
    }

    private async Task<List<JsonElement>> PostReportAsync(string body, CancellationToken ct)
    {
        var rows = new List<JsonElement>();
        if (_options.SubscriptionIds.Count == 0) return rows;

        try
        {
            using var client = await CreateClientAsync(ct);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(CarbonReportsUrl, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadAsStringAsync(ct);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    LastAccessDenied = true;
                    _logger.LogWarning(
                        "Carbon Optimization API returned {Status} — the identity likely lacks emissions " +
                        "access. Returning empty.", (int)response.StatusCode);
                }
                else
                {
                    _logger.LogWarning(
                        "Carbon Optimization API returned {Status}. Body: {Body}", (int)response.StatusCode, payload);
                }
                return rows;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in value.EnumerateArray())
                    rows.Add(el.Clone());
            }

            if (rows.Count > 0) LastAccessDenied = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Carbon Optimization report request failed.");
        }
        return rows;
    }

    private string BuildBody(
        string reportType, DateOnly start, DateOnly end, string? categoryType = null, int? topItems = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["reportType"]       = reportType,
            ["subscriptionList"] = _options.SubscriptionIds,
            ["carbonScopeList"]  = AllScopes,
            ["dateRange"]        = new Dictionary<string, string>
            {
                ["start"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["end"]   = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            }
        };
        if (categoryType is not null) payload["categoryType"] = categoryType;
        if (topItems is not null)     payload["topItems"]     = topItems.Value;

        return JsonSerializer.Serialize(payload);
    }

    private async Task<HttpClient> CreateClientAsync(CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("AzureMgmt");
        var token = await _tokenService.GetAccessTokenAsync(ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static double Num(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : 0d;
}
