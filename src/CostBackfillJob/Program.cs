// CmCSP – Cost Backfill Job (run once)
//
// One-time historical backfill of Azure Cost Management blob exports into the SQL
// CostFact table. Reads EVERY export CSV (no rolling 365-day window), aggregates the
// rows into the four dashboard datasets, and upserts them into SQL keyed by the
// CostFact natural key so re-runs are idempotent and the latest export always wins.
//
// Unlike CostCollectorJob (nightly cache refresh) this job is intended to be run a
// single time after the SQL data platform is first provisioned, to seed the fact table
// with all accumulated history before the durable nightly collection takes over.
//
// Configuration: the same AzureCostManagement__* environment variables the web app and
// collector use (ExportBlob:StorageAccountUri + ConnectionStrings:Sql), plus KeyVaultUri.
// Authentication is DefaultAzureCredential (managed identity in Azure, az login locally).
//
// Exit codes: 0 = success, 1 = failure (e.g. SQL not configured or blob read error).

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

// ── SQL data platform (required for this job) ────────────────────────────────
var sqlConnectionString = builder.Configuration.GetConnectionString("Sql");
if (string.IsNullOrWhiteSpace(sqlConnectionString))
{
    Console.Error.WriteLine(
        "CostBackfill: ConnectionStrings:Sql is not configured. The SQL data platform must be " +
        "provisioned (DEPLOY_DATA_PLATFORM=true) before running the backfill.");
    return 1;
}

builder.Services.AddDbContext<CmcspDbContext>(opt => opt.UseSqlServer(sqlConnectionString));
builder.Services.AddScoped<CostBackfillService>();

using var host = builder.Build();

var logger   = host.Services.GetRequiredService<ILogger<Program>>();
var backfill = host.Services.GetRequiredService<CostBackfillService>();

logger.LogInformation("CostBackfill: starting one-time historical backfill into SQL CostFact.");

try
{
    var result = await backfill.RunAsync();

    logger.LogInformation(
        "CostBackfill: done. Blobs read={Blobs}, facts upserted={Upserted} (inserted={Inserted}, updated={Updated}).",
        result.BlobsRead, result.FactsUpserted, result.FactsInserted, result.FactsUpdated);

    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "CostBackfill: backfill failed.");
    return 1;
}
