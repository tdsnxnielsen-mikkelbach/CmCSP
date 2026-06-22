// CmCSP – Cost Collector Job
//
// Refreshes the shared cost-data cache (Table + Blob Storage) that the dashboard
// reads, so the first user each day doesn't pay the cold-fetch cost and figures stay
// current independently of the web app's lifecycle (which scales to zero when idle).
//
// Runs as an Azure Container Apps Job with two triggers:
//   • Schedule – nightly at 02:00 UTC (cronExpression in bicep/app.bicep)
//   • Manual   – started on demand from the dashboard "Collect now" button
//
// It reuses the exact same cost + cache services as the web app (via CmCSP.Core), so
// cache keys, TTLs and the 60 KB Table/Blob routing stay identical. Collection writes
// the four aggregate datasets (main, resource-group, tag, amortized) that all pages share.
//
// At the end of every run it appends an audit row (status, counts, trigger, duration)
// to Table Storage via CollectionAuditService; the dashboard reads that for last-run status.
//
// Configuration: the same AzureCostManagement__* environment variables the Container App
// uses (set by the azd postprovision hook) plus KeyVaultUri. Authentication is
// DefaultAzureCredential (the job's managed identity on Azure).
//
// Environment variables specific to this job:
//   COLLECT_TRIGGER  – "schedule" | "manual" (recorded in the audit row; default "manual")

using System.Diagnostics;
using Azure.Identity;
using CmCSP.Data;
using CmCSP.Models;
using CmCSP.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// ── Configuration: environment variables + Key Vault ─────────────────────────
var keyVaultUri = builder.Configuration["KeyVaultUri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

// ── Cost Management options (mirrors the web app's binding) ───────────────────
var costOptions = builder.Configuration
    .GetSection(CostManagementOptions.SectionName)
    .Get<CostManagementOptions>() ?? new CostManagementOptions();

var ratesFromConfig = builder.Configuration
    .GetSection($"{CostManagementOptions.SectionName}:ExchangeRates")
    .Get<Dictionary<string, decimal>>();
if (ratesFromConfig is { Count: > 0 })
    foreach (var (k, v) in ratesFromConfig)
        costOptions.ExchangeRates[k] = v;

builder.Services.AddSingleton(costOptions);

// ── SQL data platform (Phase 4) ───────────────────────────────────────────────
// DbContext factory for the audit store when a connection string is configured; falls
// back to Table Storage when absent (mirrors the web app registration).
var sqlConnectionString = builder.Configuration.GetConnectionString("Sql");
if (!string.IsNullOrWhiteSpace(sqlConnectionString))
    builder.Services.AddDbContextFactory<CmcspDbContext>(opt => opt.UseSqlServer(sqlConnectionString));

// ── Cost + cache pipeline (same registrations as the web app) ────────────────
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("AzureMgmt", client => client.Timeout = TimeSpan.FromSeconds(180));
if (costOptions.Redis.Enabled)
    builder.Services.AddSingleton<ICacheService, RedisCacheService>();
else
    builder.Services.AddSingleton<ICacheService, AzureStorageCacheService>();
builder.Services.AddSingleton<AzureTokenService>();
builder.Services.AddSingleton<DataLoadingStateService>();
builder.Services.AddSingleton<ExportProvisioningService>();
builder.Services.AddSingleton<SubscriptionStoreService>();
builder.Services.AddSingleton<CollectionAuditService>();

// Phase 9: customer registry + ambient tenant-scope holder. In the collector the scope stays
// Unscoped (no circuit), but CustomerStore lets the write path stamp CostFact rows with the
// bootstrap "home" customer so per-customer scoping works once multi-tenancy is enabled.
builder.Services.AddSingleton<CustomerStore>();
builder.Services.AddSingleton<TenantScopeAccessor>();

if (costOptions.ExportBlob.Enabled)
{
    builder.Services.AddSingleton<CostManagementService>();
    builder.Services.AddSingleton<ICostManagementService, BlobCostManagementService>();
}
else
{
    builder.Services.AddSingleton<ICostManagementService, CostManagementService>();
}

using var host = builder.Build();

var logger     = host.Services.GetRequiredService<ILogger<Program>>();
var audit      = host.Services.GetRequiredService<CollectionAuditService>();
var costService = host.Services.GetRequiredService<ICostManagementService>();

// Force SubscriptionStoreService to construct so user-added subscription IDs (Key Vault)
// are merged into costOptions.SubscriptionIds before collection starts.
_ = host.Services.GetRequiredService<SubscriptionStoreService>();

// ── Partitioning + optional multi-tenant fan-out ───────────────────────────────
// Single-tenant: COLLECT_PARTITION_COUNT/INDEX splits the *subscription* set across parallel
// executions (CostFact's natural key is per-subscription, so disjoint partitions never conflict).
// Multi-tenant (MultiTenancy:Enabled + customer registry present): the collector iterates active
// *customers*, collecting each under its own tenant scope so CostFact rows are stamped with the
// owning CustomerId and (in service-principal mode) read with a per-tenant GDAP token. The same
// COLLECT_PARTITION_COUNT/INDEX then splits the *customer* set across executions for scale-out.
var customerStore = host.Services.GetRequiredService<CustomerStore>();
var scopeAccessor = host.Services.GetRequiredService<TenantScopeAccessor>();

var partitionCount = Math.Max(1, ParseIntEnv("COLLECT_PARTITION_COUNT", 1));
var partitionIndex = Math.Clamp(ParseIntEnv("COLLECT_PARTITION_INDEX", 0), 0, partitionCount - 1);

var multiTenant = costOptions.MultiTenancy.Enabled && customerStore.IsEnabled;

var trigger       = (Environment.GetEnvironmentVariable("COLLECT_TRIGGER") ?? "manual").Trim().ToLowerInvariant();
var correlationId = Guid.NewGuid().ToString("N");
var replicaName   = Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME") ?? Environment.MachineName;
var startedUtc    = DateTimeOffset.UtcNow;
var sw            = Stopwatch.StartNew();

long mainRows = 0, rgRows = 0, tagRows = 0, amortRows = 0;
var subscriptionCount = 0;

var auditRecord = new CollectionAuditRecord
{
    Trigger       = trigger,
    StartedUtc    = startedUtc,
    ReplicaName   = replicaName,
    CorrelationId = correlationId
};

try
{
    if (multiTenant)
    {
        var allCustomers = (await customerStore.GetActiveCustomersAsync())
            .OrderBy(c => c.Id)
            .ToList();
        var customers = partitionCount > 1
            ? allCustomers.Where((_, i) => i % partitionCount == partitionIndex).ToList()
            : allCustomers;

        logger.LogInformation(
            "CostCollector[{CorrelationId}]: multi-tenant fan-out — partition {Index}/{Count} handling {Subset} of {Total} customer(s).",
            correlationId, partitionIndex, partitionCount, customers.Count, allCustomers.Count);

        foreach (var customer in customers)
        {
            var subs = (await customerStore.GetSubscriptionIdsAsync(customer.Id)).ToList();
            if (subs.Count == 0)
            {
                logger.LogInformation(
                    "CostCollector[{CorrelationId}]: customer {Customer} has no mapped subscriptions — skipping.",
                    correlationId, customer.DisplayName);
                continue;
            }

            // Publish the customer's scope: CostFact writes are stamped with this CustomerId and
            // (service-principal mode) AzureTokenService acquires a per-tenant GDAP token.
            scopeAccessor.Current = new TenantScope
            {
                IsUnscoped  = false,
                IsPartner   = false,
                CustomerIds = [customer.Id],
                TenantId    = customer.TenantId
            };

            // Restrict export parsing/persistence + API queries to this customer's subscriptions.
            costOptions.SubscriptionIds.Clear();
            costOptions.SubscriptionIds.AddRange(subs);
            if (costService is BlobCostManagementService blobMt)
                blobMt.SubscriptionFilter = new HashSet<string>(subs, StringComparer.OrdinalIgnoreCase);

            subscriptionCount += subs.Count;

            logger.LogInformation(
                "CostCollector[{CorrelationId}]: collecting customer {Customer} ({Subs} subscription(s)).",
                correlationId, customer.DisplayName, subs.Count);

            var result = await costService.RefreshAsync();
            var amort  = await costService.GetAmortizedMainCostDataAsync();
            mainRows += result.Main; rgRows += result.Rg; tagRows += result.Tag; amortRows += amort.Count;
        }

        // Refresh the partner-aggregate (mt_partner:) cache from SQL. Collection above only writes
        // per-customer (mt_c{id}:) caches, so without this rebuild the partner's all-customers view
        // is populated lazily once and then served stale — a newly-collected customer's data would
        // never appear for the partner. Reading under the partner scope re-caches all active
        // customers' rows fresh in both tiers. Only partition 0 does it (the SQL read already spans
        // every customer regardless of this execution's customer subset).
        if (partitionIndex == 0 && costService is BlobCostManagementService blobAgg)
        {
            try
            {
                blobAgg.SubscriptionFilter = null;
                scopeAccessor.Current = new TenantScope
                {
                    IsUnscoped  = false,
                    IsPartner   = true,
                    CustomerIds = allCustomers.Select(c => c.Id).ToList(),
                    TenantId    = costOptions.MultiTenancy.HomeTenantId
                };
                await blobAgg.RefreshScopedCacheFromStoreAsync();
                logger.LogInformation(
                    "CostCollector[{CorrelationId}]: refreshed partner-aggregate cache for {Count} customer(s).",
                    correlationId, allCustomers.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "CostCollector[{CorrelationId}]: partner-aggregate cache refresh failed (will rebuild lazily).",
                    correlationId);
            }
        }

        scopeAccessor.Current = TenantScope.Unscoped;
    }
    else
    {
        // Single-tenant path: optional per-subscription partitioning across executions.
        if (partitionCount > 1)
        {
            var all = costOptions.SubscriptionIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var subset = all.Where((_, i) => i % partitionCount == partitionIndex).ToList();

            costOptions.SubscriptionIds.Clear();
            costOptions.SubscriptionIds.AddRange(subset);

            if (costService is BlobCostManagementService blob)
                blob.SubscriptionFilter = new HashSet<string>(subset, StringComparer.OrdinalIgnoreCase);

            logger.LogInformation(
                "CostCollector[{CorrelationId}]: partition {Index}/{Count} handling {Subset} of {Total} subscription(s).",
                correlationId, partitionIndex, partitionCount, subset.Count, all.Count);
        }

        subscriptionCount = costOptions.SubscriptionIds.Count;

        logger.LogInformation(
            "CostCollector[{CorrelationId}]: starting {Trigger} collection for {SubCount} subscription(s) on replica {Replica}.",
            correlationId, trigger, subscriptionCount, replicaName);

        // RefreshAsync re-parses the export CSVs (the source feed) and, when the SQL data platform
        // is enabled, upserts the aggregated rows into CostFact before warming the shared Redis cache.
        // Amortized data is API-only (exports use ActualCost), so it is fetched separately.
        var result = await costService.RefreshAsync();
        var amort  = await costService.GetAmortizedMainCostDataAsync();
        mainRows += result.Main; rgRows += result.Rg; tagRows += result.Tag; amortRows += amort.Count;
    }

    auditRecord.Status = "Success";

    logger.LogInformation(
        "CostCollector[{CorrelationId}]: collection complete. main={Main}, rg={Rg}, tag={Tag}, amort={Amort}.",
        correlationId, mainRows, rgRows, tagRows, amortRows);
}
catch (Exception ex)
{
    auditRecord.Status = "Failed";
    auditRecord.Error  = ex.Message;
    logger.LogError(ex, "CostCollector[{CorrelationId}]: collection failed.", correlationId);
}

sw.Stop();
auditRecord.SubscriptionCount = subscriptionCount;
auditRecord.MainRows          = (int)mainRows;
auditRecord.RgRows            = (int)rgRows;
auditRecord.TagRows           = (int)tagRows;
auditRecord.AmortRows         = (int)amortRows;
auditRecord.FinishedUtc       = DateTimeOffset.UtcNow;
auditRecord.DurationMs        = sw.ElapsedMilliseconds;

await audit.WriteAsync(auditRecord);

logger.LogInformation(
    "CostCollector[{CorrelationId}]: {Status} in {DurationMs} ms (trigger={Trigger}).",
    correlationId, auditRecord.Status, auditRecord.DurationMs, trigger);

return auditRecord.Status == "Success" ? 0 : 1;

static int ParseIntEnv(string name, int fallback)
{
    var raw = Environment.GetEnvironmentVariable(name);
    return int.TryParse(raw, out var v) ? v : fallback;
}
