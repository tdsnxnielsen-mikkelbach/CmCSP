using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using CmCSP.Data;
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
    private const string ColCurrencyAlt2 = "billingcurrency";   // CSP export format (no "code" suffix)
    private const string ColTags         = "tags";

    // ── Cache ──────────────────────────────────────────────────────────────────
    private const string KeyMain = "cm_main";
    private const string KeyRg   = "cm_rg";
    private const string KeyTag  = "cm_tag";

    // SQL CostFact dataset discriminators (durable persistence of parsed rows).
    private const string DatasetMain = "main";
    private const string DatasetRg   = "rg";
    private const string DatasetTag  = "tag";
    private const int    SaveBatchSize = 5000;

    // Row window: same rolling 365-day window as the Query API service.
    private static DateTime RowCutoff =>
        DateTime.UtcNow.AddDays(-364).Date;

    // Prevent concurrent cold fetches from all three Get*Async callers.
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    // When set, only export rows whose SubscriptionId is in this set are parsed/persisted.
    private HashSet<string>? _subscriptionFilter;

    /// <summary>
    /// Restricts collection to a subset of subscriptions. Used by the CostCollectorJob to
    /// partition work across parallel executions (COLLECT_PARTITION_INDEX / COLLECT_PARTITION_COUNT);
    /// because <c>CostFact</c>'s natural key is per-subscription, disjoint partitions never conflict.
    /// <c>null</c> (the default, e.g. in the web app) parses every subscription found in the exports.
    /// </summary>
    public ISet<string>? SubscriptionFilter
    {
        get => _subscriptionFilter;
        set => _subscriptionFilter = value is null
            ? null
            : new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
    }

    private readonly ICacheService             _cache;
    private readonly CostManagementOptions               _options;
    private readonly DataLoadingStateService             _loadingState;
    private readonly ILogger<BlobCostManagementService>  _logger;
    private readonly CostManagementService?              _apiService;
    private readonly IDbContextFactory<CmcspDbContext>?  _dbFactory;
    private readonly TenantScopeAccessor?                _scopeAccessor;
    private readonly CustomerStore?                      _customers;

    public BlobCostManagementService(
        ICacheService             cache,
        CostManagementOptions                options,
        DataLoadingStateService              loadingState,
        ILogger<BlobCostManagementService>   logger,
        CostManagementService?               apiService = null,
        IDbContextFactory<CmcspDbContext>?   dbFactory  = null,
        TenantScopeAccessor?                 scopeAccessor = null,
        CustomerStore?                       customers  = null)
    {
        _cache        = cache;
        _options      = options;
        _loadingState = loadingState;
        _logger       = logger;
        _apiService   = apiService;
        _dbFactory    = dbFactory;
        _scopeAccessor = scopeAccessor;
        _customers    = customers;
    }

    // The current request's tenant scope (Unscoped in the single-tenant path or background work).
    private TenantScope Scope => _scopeAccessor?.Current ?? TenantScope.Unscoped;

    // Tenant-namespaced cache key so customers never share cached payloads (empty prefix in the
    // single-tenant path → keys are exactly as before).
    private string Scoped(string baseKey) => Scope.CacheKeyPrefix + baseKey;


    // ── Public interface ───────────────────────────────────────────────────────

    public Task<List<CostRow>> GetMainCostDataAsync(CancellationToken ct = default) =>
        GetOrPopulateAsync(KeyMain, ct);

    public Task<List<CostRow>> GetRgCostDataAsync(CancellationToken ct = default) =>
        GetOrPopulateAsync(KeyRg, ct);

    public Task<List<CostRow>> GetTagCostDataAsync(CancellationToken ct = default) =>
        GetOrPopulateAsync(KeyTag, ct);

    /// <summary>
    /// AmortizedCost data is never available from blob exports (exports use ActualCost).
    /// Delegates to the underlying API service on a best-effort basis — if the API is
    /// unavailable or rate-limited, returns an empty list rather than blocking.
    /// A 90-second timeout prevents a rate-limited API from blocking the warmup cycle.
    /// </summary>
    public async Task<List<CostRow>> GetAmortizedMainCostDataAsync(CancellationToken ct = default)
    {
        if (_apiService is null)
            return [];

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            return await _apiService.GetAmortizedMainCostDataAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "BlobCostManagementService: amortized cost API call timed out (blob exports unaffected). " +
                "Amortized data will be unavailable until the next successful API call.");
            return [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "BlobCostManagementService: amortized cost API call failed (blob exports unaffected). " +
                "Amortized data will be unavailable until the next successful API call.");
            return [];
        }
    }

    public void InvalidateCache()
    {
        // Remove the current scope's payloads (empty prefix in the single-tenant path).
        _cache.Remove(Scoped(KeyMain));
        _cache.Remove(Scoped(KeyRg));
        _cache.Remove(Scoped(KeyTag));
        _cache.Remove(Scoped("cm_main_amort"));
        _cache.Remove(Scoped("cm_budgets"));
        _cache.Remove(Scoped("cm_budgets_subs"));
        _cache.Remove(Scoped("cm_advisor"));
        _cache.Remove(Scoped("cm_advisor_scores"));
        _cache.Remove(Scoped("cm_sub_names"));
        _cache.Remove(Scoped("cm_forecast"));
        _cache.Remove(Scoped("cm_forecast_amort"));
        _cache.Remove(Scoped("cm_publisher"));
        _loadingState.Update(KeyMain, LoadPhase.Idle);
        _loadingState.Update(KeyRg,   LoadPhase.Idle);
        _loadingState.Update(KeyTag,  LoadPhase.Idle);
        _logger.LogInformation("Blob cost cache invalidated.");
    }

    public Task<List<SubscriptionBudget>> GetSubscriptionBudgetsAsync(CancellationToken ct = default) =>
        _apiService is not null
            ? _apiService.GetSubscriptionBudgetsAsync(ct)
            : Task.FromResult(new List<SubscriptionBudget>());

    public Task<List<AdvisorRecommendation>> GetAdvisorRecommendationsAsync(CancellationToken ct = default) =>
        _apiService is not null
            ? _apiService.GetAdvisorRecommendationsAsync(ct)
            : Task.FromResult(new List<AdvisorRecommendation>());

    public Task<List<AdvisorCategoryScore>> GetAdvisorScoresAsync(CancellationToken ct = default) =>
        _apiService is not null
            ? _apiService.GetAdvisorScoresAsync(ct)
            : Task.FromResult(new List<AdvisorCategoryScore>());

    public Task<Dictionary<string, string>> GetSubscriptionDisplayNamesAsync(CancellationToken ct = default) =>
        _apiService is not null
            ? _apiService.GetSubscriptionDisplayNamesAsync(ct)
            : Task.FromResult(new Dictionary<string, string>());

    /// <summary>
    /// Forecast and publisher-type breakdown are ARM Query API features (not available from
    /// blob exports), so they delegate to the underlying API service on a best-effort basis
    /// with a bounded timeout — a rate-limited API never blocks the dashboard.
    /// </summary>
    public Task<List<ForecastPoint>> GetForecastAsync(string metric = "ActualCost", CancellationToken ct = default) =>
        DelegateBestEffortAsync(
            token => _apiService!.GetForecastAsync(metric, token),
            "forecast", ct, fallback: []);

    public Task<List<PublisherTypeCostRow>> GetPublisherBreakdownAsync(CancellationToken ct = default) =>
        DelegateBestEffortAsync(
            token => _apiService!.GetPublisherBreakdownAsync(token),
            "publisher breakdown", ct, fallback: []);

    /// <summary>
    /// Runs an API-service call with a 90-second timeout, returning <paramref name="fallback"/>
    /// (rather than throwing) if the API is unavailable or rate-limited so blob-export pages
    /// keep working. Mirrors <see cref="GetAmortizedMainCostDataAsync"/>.
    /// </summary>
    private async Task<T> DelegateBestEffortAsync<T>(
        Func<CancellationToken, Task<T>> call, string label, CancellationToken ct, T fallback)
    {
        if (_apiService is null) return fallback;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            return await call(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "BlobCostManagementService: {Label} API call timed out (blob exports unaffected).", label);
            return fallback;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "BlobCostManagementService: {Label} API call failed (blob exports unaffected).", label);
            return fallback;
        }
    }

    // ── Internal ───────────────────────────────────────────────────────────────

    private async Task<List<CostRow>> GetOrPopulateAsync(string key, CancellationToken ct)
    {
        // Cache payloads are tenant-namespaced; the loading-state key stays unprefixed so the
        // per-dataset UI banner works the same regardless of scope.
        var cacheKey = Scoped(key);
        var ttl = TimeSpan.FromMinutes(_options.CacheExpirationMinutes);
        if (_cache.TryGetValue<List<CostRow>>(cacheKey, ttl, out var hit) && hit is not null)
        {
            _logger.LogDebug("Cache hit for {Key}.", cacheKey);
            if (_loadingState.For(key)?.Phase != LoadPhase.Ready)
                _loadingState.Update(key, LoadPhase.Ready, $"{hit.Count:N0} rows (cached)");
            return hit;
        }

        // One thread fetches; others wait and then get cache hits.
        await _fetchLock.WaitAsync(ct);
        try
        {
            // Re-check inside the lock — another thread may have just populated it.
            if (_cache.TryGetValue<List<CostRow>>(cacheKey, ttl, out hit) && hit is not null)
                return hit;

            await PopulateAllCachesAsync(ct);

            return _cache.TryGetValue<List<CostRow>>(cacheKey, ttl, out List<CostRow>? result) && result is not null
                ? result
                : [];
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>
    /// Reads the durable cost store (SQL <c>CostFact</c> when the data platform is enabled,
    /// otherwise the blob exports) and populates the three shared cache entries in one pass.
    /// </summary>
    private async Task PopulateAllCachesAsync(CancellationToken ct)
    {
        // Signal all three datasets as loading.
        _loadingState.Update(KeyMain, LoadPhase.Loading);
        _loadingState.Update(KeyRg,   LoadPhase.Loading);
        _loadingState.Update(KeyTag,  LoadPhase.Loading);

        // Read path: SQL is the durable store of parsed rows when the data platform is on.
        if (_dbFactory is not null)
        {
            var (sm, sr, stg) = await LoadFromSqlAsync(ct);
            if (sm.Count > 0 || sr.Count > 0 || stg.Count > 0)
            {
                SetCaches(sm, sr, stg, anyError: false);
                return;
            }
            // SQL empty (no collection yet) – fall back to a one-off blob parse so the
            // dashboard still shows data before the first collector run.
            _logger.LogInformation("CostFact table empty – falling back to a direct blob parse.");
        }

        var (mainList, rgList, tagList, anyError) = await ParseExportsAsync(ct);
        SetCaches(mainList, rgList, tagList, anyError);
    }

    /// <summary>
    /// Re-parses the export CSVs (the source feed) and, when the SQL data platform is enabled,
    /// upserts the aggregated rows into <c>CostFact</c> before warming the shared cache. This is
    /// the collector's write path. Returns the per-dataset row counts.
    /// </summary>
    public async Task<CostCollectionResult> RefreshAsync(CancellationToken ct = default)
    {
        _loadingState.Update(KeyMain, LoadPhase.Loading);
        _loadingState.Update(KeyRg,   LoadPhase.Loading);
        _loadingState.Update(KeyTag,  LoadPhase.Loading);

        var (mainList, rgList, tagList, anyError) = await ParseExportsAsync(ct);

        if (_dbFactory is not null)
            await UpsertFactsAsync(mainList, rgList, tagList, ct);

        SetCaches(mainList, rgList, tagList, anyError);
        return new CostCollectionResult(mainList.Count, rgList.Count, tagList.Count);
    }

    /// <summary>
    /// Lists and parses every relevant export blob into the three aggregated datasets.
    /// Falls back to the Query API when no export blobs exist yet.
    /// </summary>
    private async Task<(List<CostRow> Main, List<CostRow> Rg, List<CostRow> Tag, bool AnyError)> ParseExportsAsync(CancellationToken ct)
    {
        var opts = _options.ExportBlob;

        // Aggregation keys are compared case-insensitively to match both Azure's case-insensitive
        // resource naming (subscription GUID, meter category, resource group, tag key) and the
        // SQL CostFact unique index (case-insensitive collation). Using Ordinal here would let a
        // casing variant (e.g. "rg-devbox" vs "RG-Devbox") survive as two in-memory rows that then
        // collide as a duplicate key on insert, crashing the whole collection run.
        var mainAccum = new Dictionary<string, CostRow>(StringComparer.OrdinalIgnoreCase);
        var rgAccum   = new Dictionary<string, CostRow>(StringComparer.OrdinalIgnoreCase);
        var tagAccum  = new Dictionary<string, CostRow>(StringComparer.OrdinalIgnoreCase);

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

                if (_apiService is not null)
                {
                    _logger.LogInformation(
                        "No export blobs available – falling back to Cost Management Query API.");
                    // Run sequentially to respect the per-subscription rate limit (5 req/min).
                    var apiMain = await _apiService.GetMainCostDataAsync(ct);
                    var apiRg   = await _apiService.GetRgCostDataAsync(ct);
                    var apiTag  = await _apiService.GetTagCostDataAsync(ct);
                    return (apiMain, apiRg, apiTag, false);
                }
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

                    // Parse into a per-blob accumulator (rows with the same key within
                    // one blob are summed as normal). Case-insensitive to match the SQL
                    // unique index so casing variants aggregate instead of colliding.
                    var blobMain = new Dictionary<string, CostRow>(StringComparer.OrdinalIgnoreCase);
                    var blobRg   = new Dictionary<string, CostRow>(StringComparer.OrdinalIgnoreCase);
                    var blobTag  = new Dictionary<string, CostRow>(StringComparer.OrdinalIgnoreCase);

                    await ParseCsvIntoAccumulatorsAsync(
                        reader, blobMain, blobRg, blobTag, blob.Name, ct);

                    // Merge with replacement: if the same (date|sub|meter) key already
                    // exists from an earlier blob, overwrite it with this blob's value.
                    // Azure "MonthToDate" exports write a NEW cumulative CSV each day
                    // (blob path includes the run date), so a given day's cost appears in
                    // every subsequent blob of that month. Blobs are ordered oldest-first,
                    // so the final (newest) blob always wins — giving us the most
                    // up-to-date cost for each day without double-counting.
                    foreach (var (k, v) in blobMain) mainAccum[k] = v;
                    foreach (var (k, v) in blobRg)   rgAccum[k]   = v;
                    foreach (var (k, v) in blobTag)  tagAccum[k]  = v;
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

        return (mainList, rgList, tagList, anyError);
    }

    // ── Durable SQL store (parsed-row persistence) ────────────────────────────

    private void SetCaches(List<CostRow> mainList, List<CostRow> rgList, List<CostRow> tagList, bool anyError)
    {
        var expiry = TimeSpan.FromMinutes(_options.CacheExpirationMinutes);
        _cache.Set(Scoped(KeyMain), mainList, expiry);
        _cache.Set(Scoped(KeyRg),   rgList,   expiry);
        _cache.Set(Scoped(KeyTag),  tagList,  expiry);

        var phase = anyError && mainList.Count == 0 ? LoadPhase.Failed : LoadPhase.Ready;
        _loadingState.Update(KeyMain, phase, anyError && mainList.Count == 0 ? "fetch failed" : $"{mainList.Count:N0} rows");
        _loadingState.Update(KeyRg,   phase, anyError && rgList.Count   == 0 ? "fetch failed" : $"{rgList.Count:N0} rows");
        _loadingState.Update(KeyTag,  phase, anyError && tagList.Count  == 0 ? "fetch failed" : $"{tagList.Count:N0} rows");

        _logger.LogInformation(
            "Blob cost cache populated. Main={Main}, RG={Rg}, Tag={Tag} rows.",
            mainList.Count, rgList.Count, tagList.Count);
    }

    /// <summary>Loads the rolling-window <c>CostFact</c> rows from SQL and maps them to <see cref="CostRow"/>.</summary>
    private async Task<(List<CostRow> Main, List<CostRow> Rg, List<CostRow> Tag)> LoadFromSqlAsync(CancellationToken ct)
    {
        var cutoff = DateOnly.FromDateTime(RowCutoff);
        var scope  = Scope;
        await using var db = await _dbFactory!.CreateDbContextAsync(ct);

        // Tenant isolation (Phase 9): in the single-tenant path the query is unfiltered (identical
        // to before). When scoped, every row must belong to a customer in the resolved scope — a
        // customer can never read another tenant's facts, and a denied scope reads nothing.
        var query = db.CostFacts
            .AsNoTracking()
            .Where(f => f.UsageDate >= cutoff &&
                        (f.Dataset == DatasetMain || f.Dataset == DatasetRg || f.Dataset == DatasetTag));

        if (!scope.IsUnscoped)
        {
            if (scope.CustomerIds.Count == 0)
            {
                _logger.LogWarning("Tenant scope resolved to no customers ({Tenant}); returning no rows.", scope.TenantId);
                return ([], [], []);
            }
            var ids = scope.CustomerIds;
            query = query.Where(f => ids.Contains(f.CustomerId));
        }

        var facts = await query.ToListAsync(ct);

        var main = facts.Where(f => f.Dataset == DatasetMain).Select(Map).ToList();
        var rg   = facts.Where(f => f.Dataset == DatasetRg).Select(Map).ToList();
        var tag  = facts.Where(f => f.Dataset == DatasetTag).Select(Map).ToList();

        _logger.LogInformation(
            "Loaded {Main}/{Rg}/{Tag} CostFact row(s) from SQL (since {Cutoff:yyyy-MM-dd}, scope={Scope}).",
            main.Count, rg.Count, tag.Count, cutoff, scope.IsUnscoped ? "unscoped" : scope.CacheKeyPrefix);

        return (main, rg, tag);
    }

    /// <summary>Upserts the parsed rows into <c>CostFact</c> by natural key (idempotent, latest wins).</summary>
    private async Task UpsertFactsAsync(
        List<CostRow> main, List<CostRow> rg, List<CostRow> tag, CancellationToken ct)
    {
        // Phase 9: stamp the owning customer. A collection run writes for one customer — the
        // scope's single customer when scoped, otherwise the bootstrap "home" customer. In the
        // single-tenant path with no seeded customer this resolves to (0, "") — the schema
        // defaults — so behaviour is unchanged.
        var (ownerId, ownerTenant) = await ResolveWriteOwnerAsync(ct);

        var incoming = new Dictionary<string, CostFact>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in main) { var f = ToFact(DatasetMain, r, ownerId, ownerTenant); incoming[NaturalKey(f)] = f; }
        foreach (var r in rg)   { var f = ToFact(DatasetRg,   r, ownerId, ownerTenant); incoming[NaturalKey(f)] = f; }
        foreach (var r in tag)  { var f = ToFact(DatasetTag,  r, ownerId, ownerTenant); incoming[NaturalKey(f)] = f; }

        if (incoming.Count == 0) return;

        var cutoff = DateOnly.FromDateTime(RowCutoff);
        await using var db = await _dbFactory!.CreateDbContextAsync(ct);

        var existing = await db.CostFacts
            .Where(f => f.UsageDate >= cutoff &&
                        (f.Dataset == DatasetMain || f.Dataset == DatasetRg || f.Dataset == DatasetTag))
            .ToDictionaryAsync(NaturalKey, f => f, StringComparer.OrdinalIgnoreCase, ct);

        int inserted = 0, updated = 0, pending = 0;
        foreach (var (key, inc) in incoming)
        {
            if (existing.TryGetValue(key, out var cur))
            {
                cur.Cost             = inc.Cost;
                cur.NormalizedCost   = inc.NormalizedCost;
                cur.SubscriptionName = inc.SubscriptionName;
                cur.CustomerId       = inc.CustomerId;
                cur.TenantId         = inc.TenantId;
                updated++;
            }
            else
            {
                db.CostFacts.Add(inc);
                inserted++;
            }

            if (++pending >= SaveBatchSize) { await db.SaveChangesAsync(ct); pending = 0; }
        }
        if (pending > 0) await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "CostFact upsert complete — {Inserted} inserted, {Updated} updated.", inserted, updated);
    }

    /// <summary>
    /// Resolves the customer a collection run writes for: the scope's single customer when scoped,
    /// otherwise the bootstrap "home" customer. Returns <c>(0, "")</c> — the schema defaults — when
    /// no customer registry is available (legacy single-tenant deployments).
    /// </summary>
    private async Task<(long CustomerId, string TenantId)> ResolveWriteOwnerAsync(CancellationToken ct)
    {
        var scope = Scope;
        if (!scope.IsUnscoped && scope.CustomerIds.Count == 1)
            return (scope.CustomerIds[0], scope.TenantId);

        if (_customers is { IsEnabled: true })
        {
            var home = await _customers.GetHomeCustomerAsync(ct);
            if (home is not null) return (home.Id, home.TenantId);
        }

        return (0L, string.Empty);
    }

    private static string NaturalKey(CostFact f) =>
        $"{f.Dataset}|{f.UsageDate:yyyyMMdd}|{f.SubscriptionId}|{f.ServiceName}|{f.ResourceGroupName}|{f.Tag}|{f.Currency}";

    private static CostFact ToFact(string dataset, CostRow r, long customerId, string tenantId) => new()
    {
        Dataset           = dataset,
        UsageDate         = DateOnly.FromDateTime(r.Date),
        SubscriptionId    = r.SubscriptionId,
        SubscriptionName  = r.SubscriptionName,
        ServiceName       = dataset == DatasetMain ? r.ServiceName : string.Empty,
        ResourceGroupName = dataset == DatasetRg   ? r.ResourceGroupName : string.Empty,
        Tag               = dataset == DatasetTag  ? r.Tag : string.Empty,
        Cost              = r.Cost,
        Currency          = r.Currency,
        NormalizedCost    = r.NormalizedCost,
        CustomerId        = customerId,
        TenantId          = tenantId
    };

    private static CostRow Map(CostFact f) => new()
    {
        Date              = f.UsageDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        Cost              = f.Cost,
        Currency          = f.Currency,
        NormalizedCost    = f.NormalizedCost,
        SubscriptionId    = f.SubscriptionId,
        SubscriptionName  = f.SubscriptionName,
        ServiceName       = f.ServiceName,
        ResourceGroupName = f.ResourceGroupName,
        Tag               = f.Tag
    };

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
        int idxCurr    = FindCol(colMap, ColCurrency, ColCurrencyAlt, ColCurrencyAlt2);
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
            if (_subscriptionFilter is { Count: > 0 } && !_subscriptionFilter.Contains(subId)) continue;
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
