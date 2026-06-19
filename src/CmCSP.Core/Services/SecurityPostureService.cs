using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Phase 8 — Azure security posture (Microsoft Defender for Cloud).
///
/// Reads the <b>secure score</b> for each configured subscription so the dashboard can show "how
/// secure is this estate?" next to the cost story. Two read-only ARM feeds, both using the existing
/// management bearer token:
///   • Microsoft.Security/secureScores         — the ASC Default <c>ascScore</c> percentage per sub.
///   • Microsoft.Security/secureScoreControls  — per-control healthy/unhealthy counts → "top findings".
///
/// Scope: <b>Azure security posture only</b> — this is not the Microsoft 365 Secure Score.
///
/// Caching note: like <see cref="OptimizationService"/> these are on-demand ARM reads, memoised in a
/// small in-process TTL cache so a page refresh is cheap. This deliberately stays outside the
/// cost-cache contract in services-cache (those flow CSV export → SQL → ICacheService).
///
/// Graceful degradation: <c>Microsoft.Security/*/read</c> is covered by the <b>Reader</b> grant the
/// app identity already has (Phase 7). When that is missing the ARM calls return 403/404/empty; we
/// swallow those, log a warning and surface <see cref="LastAccessDenied"/> so the UI can show a
/// "needs Security Reader role" banner with an empty state instead of an error.
/// </summary>
public sealed class SecurityPostureService
{
    private const string SecurityApiVersion = "2020-01-01";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory               _httpFactory;
    private readonly AzureTokenService                 _tokenService;
    private readonly ICostManagementService            _costService;
    private readonly CostManagementOptions             _options;
    private readonly ILogger<SecurityPostureService>   _logger;

    // ── in-process TTL memo (see caching note above) ─────────────────────────
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTime At, List<SecureScoreSummary> Data)?       _scores;
    private (DateTime At, List<SecurityControlFinding> Data)?   _findings;

    /// <summary>True when the most recent ARM read was denied (403/404) — the UI shows a Reader banner.</summary>
    public bool LastAccessDenied { get; private set; }

    public SecurityPostureService(
        IHttpClientFactory              httpFactory,
        AzureTokenService                tokenService,
        ICostManagementService           costService,
        CostManagementOptions            options,
        ILogger<SecurityPostureService>  logger)
    {
        _httpFactory  = httpFactory;
        _tokenService = tokenService;
        _costService  = costService;
        _options      = options;
        _logger       = logger;
    }

    private TimeSpan Ttl => TimeSpan.FromMinutes(_options.CacheExpirationMinutes);

    private bool Fresh(DateTime at) => DateTime.UtcNow - at < Ttl;

    // ── 1. Secure score per subscription ─────────────────────────────────────

    /// <summary>
    /// Returns the Defender for Cloud secure score for each configured subscription, memoised for the
    /// configured TTL. Empty when access is missing (sets <see cref="LastAccessDenied"/>).
    /// </summary>
    public async Task<List<SecureScoreSummary>> GetSecureScoresAsync(CancellationToken ct = default)
    {
        if (_scores is { } c && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_scores is { } c2 && Fresh(c2.At)) return c2.Data;

            using var client = CreateClient(out var tokenTask);
            await tokenTask(client, ct);

            var names = await _costService.GetSubscriptionDisplayNamesAsync(ct);
            var tasks = _options.SubscriptionIds.Select(subId => FetchScoreForSubAsync(client, subId, names, ct));
            var results = (await Task.WhenAll(tasks))
                .Where(s => s is not null)
                .Select(s => s!)
                .OrderByDescending(s => s.Percentage)
                .ToList();

            _scores = (DateTime.UtcNow, results);
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 2. Secure score controls → top findings ──────────────────────────────

    /// <summary>
    /// Returns secure-score controls with unhealthy resources across all configured subscriptions,
    /// ordered by weight then unhealthy count — the "top security findings" list. Empty on missing access.
    /// </summary>
    public async Task<List<SecurityControlFinding>> GetTopFindingsAsync(CancellationToken ct = default)
    {
        if (_findings is { } c && Fresh(c.At)) return c.Data;

        await _gate.WaitAsync(ct);
        try
        {
            if (_findings is { } c2 && Fresh(c2.At)) return c2.Data;

            using var client = CreateClient(out var tokenTask);
            await tokenTask(client, ct);

            var tasks = _options.SubscriptionIds.Select(subId => FetchControlsForSubAsync(client, subId, ct));
            var results = (await Task.WhenAll(tasks))
                .SelectMany(r => r)
                .Where(f => f.Unhealthy > 0)
                .OrderByDescending(f => f.Weight)
                .ThenByDescending(f => f.Unhealthy)
                .ToList();

            _findings = (DateTime.UtcNow, results);
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Resolves subscription display names via the cost service's cached lookup.</summary>
    public Task<Dictionary<string, string>> GetSubscriptionNamesAsync(CancellationToken ct = default) =>
        _costService.GetSubscriptionDisplayNamesAsync(ct);

    // ── plumbing ─────────────────────────────────────────────────────────────

    private HttpClient CreateClient(out Func<HttpClient, CancellationToken, Task> applyAuth)
    {
        var client = _httpFactory.CreateClient("AzureMgmt");
        applyAuth = async (c, ct) =>
        {
            var token = await _tokenService.GetAccessTokenAsync(ct);
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        };
        return client;
    }

    private async Task<SecureScoreSummary?> FetchScoreForSubAsync(
        HttpClient client, string subId, IReadOnlyDictionary<string, string> names, CancellationToken ct)
    {
        try
        {
            var url = $"https://management.azure.com/subscriptions/{subId}" +
                      $"/providers/Microsoft.Security/secureScores?api-version={SecurityApiVersion}";

            using var response = await client.GetAsync(url, ct);
            if (!HandleStatus(response, subId, "secure scores")) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                return null;

            // Prefer the ASC Default initiative ("ascScore"); fall back to the first entry.
            JsonElement? picked = null;
            foreach (var el in value.EnumerateArray())
            {
                if (Str(el, "name").Equals("ascScore", StringComparison.OrdinalIgnoreCase)) { picked = el; break; }
                picked ??= el;
            }
            if (picked is not { } scoreEl || !scoreEl.TryGetProperty("properties", out var p)) return null;

            var score      = p.TryGetProperty("score", out var s) ? s : default;
            var current    = Num(score, "current");
            var max        = (int)Num(score, "max");
            var percentage = Math.Round(Num(score, "percentage") * 100, 1);

            LastAccessDenied = false;
            return new SecureScoreSummary(
                SubscriptionId: subId,
                DisplayName:    names.TryGetValue(subId, out var n) ? n : subId,
                Current:        current,
                Max:            max,
                Percentage:     percentage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch secure score for subscription {SubId}.", subId);
            return null;
        }
    }

    private async Task<List<SecurityControlFinding>> FetchControlsForSubAsync(
        HttpClient client, string subId, CancellationToken ct)
    {
        var results = new List<SecurityControlFinding>();
        try
        {
            var url = $"https://management.azure.com/subscriptions/{subId}" +
                      $"/providers/Microsoft.Security/secureScoreControls?api-version={SecurityApiVersion}";

            while (!string.IsNullOrEmpty(url))
            {
                using var response = await client.GetAsync(url, ct);
                if (!HandleStatus(response, subId, "secure score controls")) break;

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;

                if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in value.EnumerateArray())
                    {
                        if (!el.TryGetProperty("properties", out var p)) continue;
                        var score = p.TryGetProperty("score", out var s) ? s : default;
                        results.Add(new SecurityControlFinding(
                            SubscriptionId: subId,
                            ControlName:    Str(p, "displayName"),
                            Healthy:        (int)Num(p, "healthyResourceCount"),
                            Unhealthy:      (int)Num(p, "unhealthyResourceCount"),
                            NotApplicable:  (int)Num(p, "notApplicableResourceCount"),
                            Percentage:     Math.Round(Num(score, "percentage") * 100, 1),
                            Weight:         (long)Num(p, "weight")));
                    }
                    if (value.GetArrayLength() > 0) LastAccessDenied = false;
                }

                url = root.TryGetProperty("nextLink", out var nl) && nl.ValueKind == JsonValueKind.String
                    ? nl.GetString() ?? string.Empty
                    : string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch secure score controls for subscription {SubId}.", subId);
        }
        return results;
    }

    /// <summary>Returns true on success; logs and flags <see cref="LastAccessDenied"/> on 403/404.</summary>
    private bool HandleStatus(HttpResponseMessage response, string subId, string what)
    {
        if (response.StatusCode == HttpStatusCode.NoContent) return false;
        if (response.IsSuccessStatusCode) return true;

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            LastAccessDenied = true;
            _logger.LogWarning(
                "{What} returned {Status} for subscription {SubId} — the identity likely lacks " +
                "Security Reader / Reader. Returning empty.", what, (int)response.StatusCode, subId);
        }
        else
        {
            _logger.LogWarning("{What} returned {Status} for subscription {SubId}.",
                what, (int)response.StatusCode, subId);
        }
        return false;
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
