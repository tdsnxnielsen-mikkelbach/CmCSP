using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Production-resilience alternative to <see cref="CostManagementService"/> that reads
/// cost data from Azure Blob Storage (pre-built by Azure Cost Management Exports) instead
/// of calling the Query API directly.
///
/// Advantages over the Query API implementation:
///  • No per-subscription rate limit (5 req/min) — reads blobs at storage throughput speed
///  • No 365-day query window restriction — all accumulated export files are readable
///  • No API call on cache miss — blob read is fast (&lt;1 s for typical export sizes)
///  • Works on App Service with a SystemAssigned Managed Identity (no client secret needed)
///
/// How it works:
///  1. On first request (cache miss) all CSV blobs under the configured prefix are listed.
///  2. Each blob newer than the rolling 365-day window is downloaded and parsed.
///  3. Rows are aggregated into the same three CostRow datasets the dashboard expects
///     (by Service, by ResourceGroup, by Tag), then all three caches are populated at once.
///  4. Subsequent requests within the TTL are pure in-memory cache hits.
///
/// Authentication:
///  • If StorageAccountUri is set: uses DefaultAzureCredential
///    (works with az login locally, Managed Identity / Workload Identity on Azure).
///  • If only ConnectionString is set: uses that (useful for local dev without az login).
///
/// Setup: Deploy bicep/main.bicep + bicep/export-sub.bicep and set
///        AzureCostManagement:ExportBlob:Enabled = true.
/// </summary>
public sealed class BlobCostManagementService : ICostManagementService
{
    // ── Azure Cost Management export CSV column names (case-insensitive lookup) ──
    // These are the columns produced by ActualCost exports with Daily granularity.
    private const string ColDate         = "date";
    private const string ColSubId        = "subscriptionid";
    private const string ColSubName      = "subscriptionname";
    private const string ColMeterCat     = "metercategory";
    private const string ColRgName       = "resourcegroupname";
    private const string ColCost         = "costinbillingcurrency";
    private const string ColCostAlt      = "cost";              // fallback column name
    private const string ColCurrency     = "billingcurrencycode";
    private const string ColCurrencyAlt  = "currency";          // fallback
    private const string ColTags         = "tags";

    // ── Cache ──────────────────────────────────────────────────────────────────
    private const string KeyMain = "cm_main";
    private const string KeyRg   = "cm_rg";
    private const string KeyTag  = "cm_tag";

    // Row window: same rolling 365-day window as the Query API service.
    private static DateTime RowCutoff =>
        DateTime.UtcNow.AddDays(-364).Date;

    // Prevent concurrent cold fetches from all three Get*Async callers.
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    private readonly AzureStorageCacheService             _cache;
    private readonly CostManagementOptions               _options;
    private readonly DataLoadingStateService             _loadingState;
    private readonly ILogger<BlobCostManagementService>  _logger;

    public BlobCostManagementService(
        AzureStorageCacheService             cache,
        CostManagementOptions                options,
        DataLoadingStateService              loadingState,
        ILogger<BlobCostManagementService>   logger)
    {
        _cache        = cache;
        _options      = options;
        _loadingState = loadingState;
        _logger       = logger;
    }

    // ── Public interface ───────────────────────────────────────────────────────

    public Task<List<CostRow>> GetMainCostDataAsync(CancellationToken ct = default) =>
        GetOrPopulateAsync(KeyMain, ct);

    public Task<List<CostRow>> GetRgCostDataAsync(CancellationToken ct = default) =>
        GetOrPopulateAsync(KeyRg, ct);

    public Task<List<CostRow>> GetTagCostDataAsync(CancellationToken ct = default) =>
        GetOrPopulateAsync(KeyTag, ct);

    public void InvalidateCache()
    {
        _cache.Remove(KeyMain);
        _cache.Remove(KeyRg);
        _cache.Remove(KeyTag);
        _loadingState.Update(KeyMain, LoadPhase.Idle);
        _loadingState.Update(KeyRg,   LoadPhase.Idle);
        _loadingState.Update(KeyTag,  LoadPhase.Idle);
        _logger.LogInformation("Blob cost cache invalidated.");
    }

    // ── Internal ───────────────────────────────────────────────────────────────

    private async Task<List<CostRow>> GetOrPopulateAsync(string key, CancellationToken ct)
    {
        var ttl = TimeSpan.FromMinutes(_options.CacheExpirationMinutes);
        if (_cache.TryGetValue<List<CostRow>>(key, ttl, out var hit) && hit is not null)
        {
            _logger.LogDebug("Cache hit for {Key}.", key);
            if (_loadingState.For(key).Phase != LoadPhase.Ready)
                _loadingState.Update(key, LoadPhase.Ready, $"{hit.Count:N0} rows (cached)");
            return hit;
        }

        // One thread fetches; others wait and then get cache hits.
        await _fetchLock.WaitAsync(ct);
        try
        {
            // Re-check inside the lock — another thread may have just populated it.
            if (_cache.TryGetValue<List<CostRow>>(key, ttl, out hit) && hit is not null)
                return hit;

            await PopulateAllCachesAsync(ct);

            return _cache.TryGetValue<List<CostRow>>(key, ttl, out List<CostRow>? result) && result is not null
                ? result
                : [];
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>
    /// Downloads and parses all relevant export blobs, then populates all three
    /// cache entries in a single pass. Reading all blobs once and splitting the
    /// aggregations is more efficient than three separate blob-listing passes.
    /// </summary>
    private async Task PopulateAllCachesAsync(CancellationToken ct)
    {
        var opts = _options.ExportBlob;

        // Signal all three datasets as loading.
        _loadingState.Update(KeyMain, LoadPhase.Loading);
        _loadingState.Update(KeyRg,   LoadPhase.Loading);
        _loadingState.Update(KeyTag,  LoadPhase.Loading);

        var mainAccum = new Dictionary<string, CostRow>(StringComparer.Ordinal);
        var rgAccum   = new Dictionary<string, CostRow>(StringComparer.Ordinal);
        var tagAccum  = new Dictionary<string, CostRow>(StringComparer.Ordinal);

        bool anyError = false;

        try
        {
            var containerClient = BuildContainerClient(opts);

            // List all blobs under the configured prefix.
            var blobs = new List<BlobItem>();
            await foreach (var page in containerClient
                .GetBlobsAsync(traits: BlobTraits.None, states: BlobStates.None,
                               prefix: opts.BlobPrefix, cancellationToken: ct)
                .AsPages())
            {
                blobs.AddRange(page.Values);
            }

            // Keep only CSV blobs and those whose last-modified date is recent
            // enough to contain rows within the 365-day window.
            var cutoffDate = RowCutoff;
            var relevant = blobs
                .Where(b => b.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                         && (b.Properties.LastModified is null
                             || b.Properties.LastModified.Value.UtcDateTime >= cutoffDate))
                .OrderBy(b => b.Properties.LastModified)
                .ToList();

            _logger.LogInformation(
                "BlobCostManagementService: found {Total} CSV blob(s), {Relevant} within date window.",
                blobs.Count(b => b.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)),
                relevant.Count);

            if (relevant.Count == 0)
            {
                _logger.LogWarning(
                    "No relevant export blobs found under prefix '{Prefix}' in container '{Container}'. " +
                    "Ensure the export schedule has run at least once.",
                    opts.BlobPrefix, opts.ContainerName);
            }

            foreach (var blob in relevant)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    _logger.LogDebug("Reading blob: {Name}", blob.Name);
                    var blobClient = containerClient.GetBlobClient(blob.Name);
                    using var stream = await blobClient.OpenReadAsync(cancellationToken: ct);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                    await ParseCsvIntoAccumulatorsAsync(
                        reader, mainAccum, rgAccum, tagAccum, blob.Name, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    anyError = true;
                    _logger.LogError(ex, "Failed to read blob {Name}. Skipping.", blob.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _loadingState.Update(KeyMain, LoadPhase.Failed, "cancelled");
            _loadingState.Update(KeyRg,   LoadPhase.Failed, "cancelled");
            _loadingState.Update(KeyTag,  LoadPhase.Failed, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            anyError = true;
            _logger.LogError(ex, "BlobCostManagementService: failed to access blob storage.");
        }

        var mainList = mainAccum.Values.ToList();
        var rgList   = rgAccum.Values.ToList();
        var tagList  = tagAccum.Values.ToList();

        var expiry = TimeSpan.FromMinutes(_options.CacheExpirationMinutes);
        _cache.Set(KeyMain, mainList, expiry);
        _cache.Set(KeyRg,   rgList,   expiry);
        _cache.Set(KeyTag,  tagList,  expiry);

        var phase = anyError && mainList.Count == 0 ? LoadPhase.Failed : LoadPhase.Ready;
        _loadingState.Update(KeyMain, phase, anyError && mainList.Count == 0 ? "fetch failed" : $"{mainList.Count:N0} rows");
        _loadingState.Update(KeyRg,   phase, anyError && rgList.Count   == 0 ? "fetch failed" : $"{rgList.Count:N0} rows");
        _loadingState.Update(KeyTag,  phase, anyError && tagList.Count  == 0 ? "fetch failed" : $"{tagList.Count:N0} rows");

        _logger.LogInformation(
            "Blob cache populated. Main={Main}, RG={Rg}, Tag={Tag} rows.",
            mainList.Count, rgList.Count, tagList.Count);
    }

    private async Task ParseCsvIntoAccumulatorsAsync(
        StreamReader reader,
        Dictionary<string, CostRow> mainAccum,
        Dictionary<string, CostRow> rgAccum,
        Dictionary<string, CostRow> tagAccum,
        string blobName,
        CancellationToken ct)
    {
        // Build column index map from the header row.
        var headerLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            _logger.LogWarning("Blob {Name} has an empty header. Skipping.", blobName);
            return;
        }

        var headers = ParseCsvLine(headerLine);
        var colMap  = headers
            .Select((h, i) => (Name: h.Trim().ToLowerInvariant(), Index: i))
            .ToDictionary(x => x.Name, x => x.Index);

        // Locate required columns.
        int idxDate    = FindCol(colMap, ColDate);
        int idxSubId   = FindCol(colMap, ColSubId);
        int idxSubName = FindCol(colMap, ColSubName);
        int idxMeter   = FindCol(colMap, ColMeterCat);
        int idxRg      = FindCol(colMap, ColRgName);
        int idxCost    = FindCol(colMap, ColCost, ColCostAlt);
        int idxCurr    = FindCol(colMap, ColCurrency, ColCurrencyAlt);
        int idxTags    = FindCol(colMap, ColTags);

        if (idxDate < 0 || idxCost < 0)
        {
            _logger.LogWarning(
                "Blob {Name} is missing required columns (Date and/or cost column). " +
                "Known columns: {Cols}. Skipping.",
                blobName, string.Join(", ", colMap.Keys));
            return;
        }

        var cutoff = RowCutoff;
        int rowCount = 0;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = ParseCsvLine(line);

            if (!TryParseDate(GetField(fields, idxDate), out var date)) continue;
            if (date < cutoff) continue; // outside rolling window

            var cost     = ParseDecimal(GetField(fields, idxCost));
            if (cost == 0m) continue;    // skip zero-cost rows

            var currency = GetField(fields, idxCurr).Trim();
            var subId    = GetField(fields, idxSubId).Trim();
            var subName  = GetField(fields, idxSubName).Trim();
            var meter    = GetField(fields, idxMeter).Trim();
            var rg       = GetField(fields, idxRg).Trim();
            var tagsJson = GetField(fields, idxTags).Trim();

            var normalised = NormaliseCurrency(cost, currency);

            // ── cm_main: aggregate by Date + SubscriptionId + MeterCategory ──
            var mainKey = $"{date:yyyyMMdd}|{subId}|{meter}";
            if (mainAccum.TryGetValue(mainKey, out var mainRow))
            {
                mainRow.Cost           += cost;
                mainRow.NormalizedCost += normalised;
            }
            else
            {
                mainAccum[mainKey] = new CostRow
                {
                    Date             = date,
                    Cost             = cost,
                    Currency         = currency,
                    NormalizedCost   = normalised,
                    SubscriptionId   = subId,
                    SubscriptionName = subName,
                    ServiceName      = meter
                };
            }

            // ── cm_rg: aggregate by Date + SubscriptionId + ResourceGroupName ──
            var rgKey = $"{date:yyyyMMdd}|{subId}|{rg}";
            if (rgAccum.TryGetValue(rgKey, out var rgRow))
            {
                rgRow.Cost           += cost;
                rgRow.NormalizedCost += normalised;
            }
            else
            {
                rgAccum[rgKey] = new CostRow
                {
                    Date              = date,
                    Cost              = cost,
                    Currency          = currency,
                    NormalizedCost    = normalised,
                    SubscriptionId    = subId,
                    SubscriptionName  = subName,
                    ResourceGroupName = rg
                };
            }

            // ── cm_tag: one row per tag key found in the Tags JSON column ────
            // The export Tags column is a JSON dict: {"env":"prod","team":"ops"}
            // We expand so each tag key produces one row, matching the API's TagKey grouping.
            var tagKeys = ParseTagKeys(tagsJson);
            if (tagKeys.Count == 0) tagKeys = [""];  // preserve untagged rows

            foreach (var tagKey in tagKeys)
            {
                var tagDictKey = $"{date:yyyyMMdd}|{subId}|{tagKey}";
                var tagCostShare = cost / tagKeys.Count;
                var tagNormShare = normalised / tagKeys.Count;

                if (tagAccum.TryGetValue(tagDictKey, out var tagRow))
                {
                    tagRow.Cost           += tagCostShare;
                    tagRow.NormalizedCost += tagNormShare;
                }
                else
                {
                    tagAccum[tagDictKey] = new CostRow
                    {
                        Date             = date,
                        Cost             = tagCostShare,
                        Currency         = currency,
                        NormalizedCost   = tagNormShare,
                        SubscriptionId   = subId,
                        SubscriptionName = subName,
                        Tag              = tagKey
                    };
                }
            }

            rowCount++;
        }

        _logger.LogDebug("Parsed {Rows} data rows from blob {Name}.", rowCount, blobName);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private BlobContainerClient BuildContainerClient(CostManagementOptions.ExportBlobOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.StorageAccountUri))
        {
            var uri = new Uri($"{opts.StorageAccountUri.TrimEnd('/')}/{opts.ContainerName}");
            _logger.LogInformation(
                "Connecting to blob storage via DefaultAzureCredential: {Uri}", uri);
            return new BlobContainerClient(uri, new DefaultAzureCredential());
        }

        if (!string.IsNullOrWhiteSpace(opts.ConnectionString))
        {
            _logger.LogInformation(
                "Connecting to blob storage via connection string (container: {Container}).",
                opts.ContainerName);
            return new BlobContainerClient(opts.ConnectionString, opts.ContainerName);
        }

        throw new InvalidOperationException(
            "ExportBlob is enabled but neither StorageAccountUri nor ConnectionString is configured. " +
            "Set AzureCostManagement:ExportBlob:StorageAccountUri (preferred) or " +
            "AzureCostManagement:ExportBlob:ConnectionString.");
    }

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

    // ── CSV helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal RFC-4180-compatible CSV line parser. Handles quoted fields containing
    /// commas and escaped double-quotes (""). Azure export CSVs are well-formed.
    /// </summary>
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb     = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    // Check for escaped quote ""
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else                                             { inQuotes = false; }
                }
                else { sb.Append(c); }
            }
            else
            {
                if      (c == '"') { inQuotes = true; }
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else               { sb.Append(c); }
            }
        }
        fields.Add(sb.ToString());
        return [.. fields];
    }

    private static string GetField(string[] fields, int index) =>
        index >= 0 && index < fields.Length ? fields[index] : string.Empty;

    /// <summary>Returns the index for the first matching column name, or -1 if none found.</summary>
    private static int FindCol(Dictionary<string, int> map, params string[] candidates)
    {
        foreach (var c in candidates)
            if (map.TryGetValue(c, out var idx)) return idx;
        return -1;
    }

    private static decimal ParseDecimal(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        return decimal.TryParse(s,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var d) ? d : 0m;
    }

    /// <summary>
    /// Parses date strings in both "yyyy-MM-dd" (ISO) and "M/d/yyyy" (US) formats
    /// as Azure exports vary by region and billing scope.
    /// </summary>
    private static bool TryParseDate(string s, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        if (DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date))
        { date = DateTime.SpecifyKind(date, DateTimeKind.Utc); return true; }

        if (DateTime.TryParseExact(s.Trim(), "M/d/yyyy",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date))
        { date = DateTime.SpecifyKind(date, DateTimeKind.Utc); return true; }

        return false;
    }

    /// <summary>
    /// Extracts tag key names from the export Tags column.
    /// The column value is a JSON object like {"env":"prod","team":"ops"} or empty.
    /// Returns an empty list if the value is null/empty/not valid JSON.
    /// </summary>
    private static List<string> ParseTagKeys(string tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson) || tagsJson == "{}") return [];
        try
        {
            using var doc  = JsonDocument.Parse(tagsJson);
            return doc.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToList();
        }
        catch { return []; }
    }
}
