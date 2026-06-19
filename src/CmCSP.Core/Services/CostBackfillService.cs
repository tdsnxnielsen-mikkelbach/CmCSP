using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CmCSP.Data;
using CmCSP.Models;
using Microsoft.EntityFrameworkCore;

namespace CmCSP.Services;

/// <summary>
/// One-time historical backfill of Azure Cost Management blob exports into the
/// <see cref="CmcspDbContext.CostFacts"/> SQL table.
///
/// Unlike <see cref="BlobCostManagementService"/> (which only reads the rolling 365-day
/// window into the cache), this service reads <b>every</b> CSV export blob and upserts the
/// aggregated rows into SQL keyed by the <c>CostFact</c> natural key
/// (<c>Dataset, UsageDate, SubscriptionId, ServiceName, ResourceGroupName, Tag, Currency</c>).
///
/// Azure "MonthToDate" exports write a new cumulative CSV each run, so a given day's cost
/// appears in every later blob of that month. Blobs are processed oldest → newest and
/// merged with replacement, so the latest export always wins for each natural key — no
/// double-counting. The run is idempotent: re-running upserts the same rows.
///
/// Authentication: DefaultAzureCredential (managed identity in Azure, az login locally) for
/// blob reads; the SQL <see cref="CmcspDbContext"/> uses managed-identity token auth.
/// </summary>
public sealed class CostBackfillService(
    CmcspDbContext db,
    CostManagementOptions options,
    ILogger<CostBackfillService> logger)
{
    // ── Azure Cost Management export CSV column names (case-insensitive lookup) ──
    private const string ColDate         = "date";
    private const string ColSubId        = "subscriptionid";
    private const string ColSubName      = "subscriptionname";
    private const string ColMeterCat     = "metercategory";
    private const string ColRgName       = "resourcegroupname";
    private const string ColCost         = "costinbillingcurrency";
    private const string ColCostAlt      = "cost";
    private const string ColCurrency     = "billingcurrencycode";
    private const string ColCurrencyAlt  = "currency";
    private const string ColCurrencyAlt2 = "billingcurrency";
    private const string ColTags         = "tags";

    private const int SaveBatchSize = 5000;

    /// <summary>Outcome of a backfill run.</summary>
    public sealed record BackfillResult(int BlobsRead, int FactsUpserted, int FactsInserted, int FactsUpdated);

    /// <summary>
    /// Reads every export blob and upserts the aggregated cost rows into <c>CostFact</c>.
    /// </summary>
    public async Task<BackfillResult> RunAsync(CancellationToken ct = default)
    {
        var opts = options.ExportBlob;

        var facts = new Dictionary<string, CostFact>(StringComparer.Ordinal);

        var containerClient = BuildContainerClient(opts);

        // List all blobs under the configured prefix — NO date window.
        var blobs = new List<BlobItem>();
        await foreach (var page in containerClient
            .GetBlobsAsync(traits: BlobTraits.None, states: BlobStates.None,
                           prefix: opts.BlobPrefix, cancellationToken: ct)
            .AsPages())
        {
            blobs.AddRange(page.Values);
        }

        var relevant = blobs
            .Where(b => b.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => b.Properties.LastModified)
            .ToList();

        logger.LogInformation(
            "CostBackfill: found {Total} CSV blob(s) to ingest under prefix '{Prefix}'.",
            relevant.Count, opts.BlobPrefix);

        int blobsRead = 0;
        foreach (var blob in relevant)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var blobClient = containerClient.GetBlobClient(blob.Name);
                using var stream = await blobClient.OpenReadAsync(cancellationToken: ct);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                await ParseCsvIntoFactsAsync(reader, facts, blob.Name, ct);
                blobsRead++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "CostBackfill: failed to read blob {Name}. Skipping.", blob.Name);
            }
        }

        logger.LogInformation(
            "CostBackfill: parsed {Blobs} blob(s) into {Facts} unique fact row(s). Upserting into SQL…",
            blobsRead, facts.Count);

        var (inserted, updated) = await UpsertAsync(facts, ct);

        logger.LogInformation(
            "CostBackfill: upsert complete — {Inserted} inserted, {Updated} updated.",
            inserted, updated);

        return new BackfillResult(blobsRead, inserted + updated, inserted, updated);
    }

    // ── Upsert ───────────────────────────────────────────────────────────────

    private async Task<(int Inserted, int Updated)> UpsertAsync(
        Dictionary<string, CostFact> facts, CancellationToken ct)
    {
        // Load the natural keys that already exist so we update in place (idempotent re-runs).
        var existing = await db.CostFacts
            .ToDictionaryAsync(NaturalKey, f => f, StringComparer.Ordinal, ct);

        int inserted = 0, updated = 0, pending = 0;

        foreach (var (key, incoming) in facts)
        {
            if (existing.TryGetValue(key, out var current))
            {
                // Latest export wins — overwrite the money + display name.
                current.Cost             = incoming.Cost;
                current.NormalizedCost   = incoming.NormalizedCost;
                current.SubscriptionName = incoming.SubscriptionName;
                updated++;
            }
            else
            {
                db.CostFacts.Add(incoming);
                inserted++;
            }

            if (++pending >= SaveBatchSize)
            {
                await db.SaveChangesAsync(ct);
                pending = 0;
            }
        }

        if (pending > 0)
            await db.SaveChangesAsync(ct);

        return (inserted, updated);
    }

    private static string NaturalKey(CostFact f) =>
        $"{f.Dataset}|{f.UsageDate:yyyyMMdd}|{f.SubscriptionId}|{f.ServiceName}|{f.ResourceGroupName}|{f.Tag}|{f.Currency}";

    // ── CSV parsing ────────────────────────────────────────────────────────────

    private async Task ParseCsvIntoFactsAsync(
        StreamReader reader,
        Dictionary<string, CostFact> facts,
        string blobName,
        CancellationToken ct)
    {
        var headerLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            logger.LogWarning("CostBackfill: blob {Name} has an empty header. Skipping.", blobName);
            return;
        }

        var headers = ParseCsvLine(headerLine);
        var colMap  = headers
            .Select((h, i) => (Name: h.Trim().ToLowerInvariant(), Index: i))
            .ToDictionary(x => x.Name, x => x.Index);

        int idxDate    = FindCol(colMap, ColDate);
        int idxSubId   = FindCol(colMap, ColSubId);
        int idxSubName = FindCol(colMap, ColSubName);
        int idxMeter   = FindCol(colMap, ColMeterCat);
        int idxRg      = FindCol(colMap, ColRgName);
        int idxCost    = FindCol(colMap, ColCost, ColCostAlt);
        int idxCurr    = FindCol(colMap, ColCurrency, ColCurrencyAlt, ColCurrencyAlt2);
        int idxTags    = FindCol(colMap, ColTags);

        if (idxDate < 0 || idxCost < 0)
        {
            logger.LogWarning(
                "CostBackfill: blob {Name} is missing required columns (Date and/or cost). Skipping.", blobName);
            return;
        }

        // Per-blob accumulator so same-key rows within one blob are summed, then the whole
        // blob replaces earlier blobs' values for matching natural keys (latest wins).
        var blobFacts = new Dictionary<string, CostFact>(StringComparer.Ordinal);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = ParseCsvLine(line);

            if (!TryParseDate(GetField(fields, idxDate), out var date)) continue;

            var cost = ParseDecimal(GetField(fields, idxCost));
            if (cost == 0m) continue;

            var currency = GetField(fields, idxCurr).Trim();
            var subId    = GetField(fields, idxSubId).Trim();
            var subName  = GetField(fields, idxSubName).Trim();
            var meter    = GetField(fields, idxMeter).Trim();
            var rg       = GetField(fields, idxRg).Trim();
            var tagsJson = GetField(fields, idxTags).Trim();

            var normalised = NormaliseCurrency(cost, currency);
            var usageDate  = DateOnly.FromDateTime(date);

            // main — by meter/service
            Accumulate(blobFacts, new CostFact
            {
                Dataset          = "main",
                UsageDate        = usageDate,
                SubscriptionId   = subId,
                SubscriptionName = subName,
                ServiceName      = meter,
                Cost             = cost,
                Currency         = currency,
                NormalizedCost   = normalised
            });

            // rg — by resource group
            Accumulate(blobFacts, new CostFact
            {
                Dataset           = "rg",
                UsageDate         = usageDate,
                SubscriptionId    = subId,
                SubscriptionName  = subName,
                ResourceGroupName = rg,
                Cost              = cost,
                Currency          = currency,
                NormalizedCost    = normalised
            });

            // tag — one row per tag key, cost split evenly (mirrors BlobCostManagementService)
            var tagKeys = ParseTagKeys(tagsJson);
            if (tagKeys.Count == 0) tagKeys = [""];
            foreach (var tagKey in tagKeys)
            {
                Accumulate(blobFacts, new CostFact
                {
                    Dataset          = "tag",
                    UsageDate        = usageDate,
                    SubscriptionId   = subId,
                    SubscriptionName = subName,
                    Tag              = tagKey,
                    Cost             = cost / tagKeys.Count,
                    Currency         = currency,
                    NormalizedCost   = normalised / tagKeys.Count
                });
            }
        }

        // Replace earlier blobs' values with this (newer) blob's values.
        foreach (var (k, v) in blobFacts)
            facts[k] = v;
    }

    private static void Accumulate(Dictionary<string, CostFact> accum, CostFact fact)
    {
        var key = NaturalKey(fact);
        if (accum.TryGetValue(key, out var existing))
        {
            existing.Cost           += fact.Cost;
            existing.NormalizedCost += fact.NormalizedCost;
        }
        else
        {
            accum[key] = fact;
        }
    }

    // ── Helpers (mirror BlobCostManagementService) ──────────────────────────────

    private BlobContainerClient BuildContainerClient(CostManagementOptions.ExportBlobOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.StorageAccountUri))
        {
            var uri = new Uri($"{opts.StorageAccountUri.TrimEnd('/')}/{opts.ContainerName}");
            logger.LogInformation("CostBackfill: connecting to blob storage via DefaultAzureCredential: {Uri}", uri);
            return new BlobContainerClient(uri, new DefaultAzureCredential());
        }

        if (!string.IsNullOrWhiteSpace(opts.ConnectionString))
            return new BlobContainerClient(opts.ConnectionString, opts.ContainerName);

        throw new InvalidOperationException(
            "ExportBlob is not configured. Set AzureCostManagement:ExportBlob:StorageAccountUri (preferred) " +
            "or AzureCostManagement:ExportBlob:ConnectionString.");
    }

    private decimal NormaliseCurrency(decimal cost, string fromCurrency)
    {
        if (string.IsNullOrWhiteSpace(fromCurrency) ||
            fromCurrency.Equals(options.TargetCurrency, StringComparison.OrdinalIgnoreCase))
            return cost;

        if (options.ExchangeRates.TryGetValue(fromCurrency, out var rate))
            return cost * rate;

        return cost;
    }

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
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else { inQuotes = false; }
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

    private static int FindCol(Dictionary<string, int> map, params string[] candidates)
    {
        foreach (var c in candidates)
            if (map.TryGetValue(c, out var idx)) return idx;
        return -1;
    }

    private static decimal ParseDecimal(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }

    private static bool TryParseDate(string s, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        if (DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date))
        { date = DateTime.SpecifyKind(date, DateTimeKind.Utc); return true; }

        if (DateTime.TryParseExact(s.Trim(), "M/d/yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date))
        { date = DateTime.SpecifyKind(date, DateTimeKind.Utc); return true; }

        return false;
    }

    private static List<string> ParseTagKeys(string tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson) || tagsJson == "{}") return [];
        try
        {
            using var doc = JsonDocument.Parse(tagsJson);
            return doc.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToList();
        }
        catch { return []; }
    }
}
