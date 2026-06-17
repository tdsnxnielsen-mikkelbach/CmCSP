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
using CmCSP.Models;
using CmCSP.Services;
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

// ── Cost + cache pipeline (same registrations as the web app) ────────────────
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("AzureMgmt", client => client.Timeout = TimeSpan.FromSeconds(180));
builder.Services.AddSingleton<AzureStorageCacheService>();
builder.Services.AddSingleton<AzureTokenService>();
builder.Services.AddSingleton<DataLoadingStateService>();
builder.Services.AddSingleton<ExportProvisioningService>();
builder.Services.AddSingleton<SubscriptionStoreService>();
builder.Services.AddSingleton<CollectionAuditService>();

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

var trigger       = (Environment.GetEnvironmentVariable("COLLECT_TRIGGER") ?? "manual").Trim().ToLowerInvariant();
var correlationId = Guid.NewGuid().ToString("N");
var replicaName   = Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME") ?? Environment.MachineName;
var startedUtc    = DateTimeOffset.UtcNow;
var sw            = Stopwatch.StartNew();

logger.LogInformation(
    "CostCollector[{CorrelationId}]: starting {Trigger} collection for {SubCount} subscription(s) on replica {Replica}.",
    correlationId, trigger, costOptions.SubscriptionIds.Count, replicaName);

var auditRecord = new CollectionAuditRecord
{
    Trigger           = trigger,
    StartedUtc        = startedUtc,
    SubscriptionCount = costOptions.SubscriptionIds.Count,
    ReplicaName       = replicaName,
    CorrelationId     = correlationId
};

try
{
    // Invalidate so the next fetch re-reads exports / re-queries instead of returning stale cache.
    costService.InvalidateCache();

    // Sequential dataset fetches: each method already iterates all subscriptions internally
    // (and the Query-API fallback honours the 5-req/min per-subscription rate limit). In the
    // production blob-export path there is no API rate limit, so a single run is efficient.
    var main  = await costService.GetMainCostDataAsync();
    var rg    = await costService.GetRgCostDataAsync();
    var tag   = await costService.GetTagCostDataAsync();
    var amort = await costService.GetAmortizedMainCostDataAsync();

    auditRecord.MainRows  = main.Count;
    auditRecord.RgRows    = rg.Count;
    auditRecord.TagRows   = tag.Count;
    auditRecord.AmortRows = amort.Count;
    auditRecord.Status    = "Success";

    logger.LogInformation(
        "CostCollector[{CorrelationId}]: collection complete. main={Main}, rg={Rg}, tag={Tag}, amort={Amort}.",
        correlationId, main.Count, rg.Count, tag.Count, amort.Count);
}
catch (Exception ex)
{
    auditRecord.Status = "Failed";
    auditRecord.Error  = ex.Message;
    logger.LogError(ex, "CostCollector[{CorrelationId}]: collection failed.", correlationId);
}

sw.Stop();
auditRecord.FinishedUtc = DateTimeOffset.UtcNow;
auditRecord.DurationMs  = sw.ElapsedMilliseconds;

await audit.WriteAsync(auditRecord);

logger.LogInformation(
    "CostCollector[{CorrelationId}]: {Status} in {DurationMs} ms (trigger={Trigger}).",
    correlationId, auditRecord.Status, auditRecord.DurationMs, trigger);

return auditRecord.Status == "Success" ? 0 : 1;
