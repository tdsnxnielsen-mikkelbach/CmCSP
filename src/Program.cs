using Azure.Identity;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using CmCSP.Components;
using CmCSP.Data;
using CmCSP.Models;
using CmCSP.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Azure Key Vault configuration ────────────────────────────────────────────
// KeyVaultUri is injected as an environment variable by the Container App Bicep.
// Uses DefaultAzureCredential (managed identity in Azure, az login locally).
// Secret names use '--' as separator, e.g. AzureCostManagement--ExportBlob--StorageAccountResourceId.
var keyVaultUri = builder.Configuration["KeyVaultUri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultUri),
        new DefaultAzureCredential());
}

// ── Razor / Blazor ──────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── MudBlazor ───────────────────────────────────────────────────────────────
builder.Services.AddMudServices();

// ── Authentication (Entra OIDC) ──────────────────────────────────────────────
// Reuses AzureCostManagement:TenantId / ClientId / ClientSecret from the same
// Entra app registration already used for the Cost Management API – no second
// config section or set of credentials needed.
builder.Services.AddMicrosoftIdentityWebAppAuthentication(
    builder.Configuration, configSectionName: CostManagementOptions.SectionName);
// Force pure authorization code flow – avoids needing "ID tokens" implicit grant
// enabled on the app registration (response_type=code only, no id_token fragment).
builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    options.ResponseType = "code";
});
builder.Services.AddAuthorization();

// ── Forwarded headers (Azure Container Apps TLS termination) ─────────────────
// Ensures OIDC redirect URIs use https:// when the app runs behind the
// Container Apps ingress (which terminates TLS and forwards X-Forwarded-Proto).
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                          | ForwardedHeaders.XForwardedProto
                          | ForwardedHeaders.XForwardedHost;
    // Container Apps NLB uses a range of private IPs – trust any proxy.
    opts.KnownIPNetworks.Clear();
    opts.KnownProxies.Clear();
});

// ── Caching ─────────────────────────────────────────────────────────────────
// IMemoryCache is always registered (used as the in-process layer inside the cache service).
builder.Services.AddMemoryCache();

// ── Named HttpClient for Azure Management API ────────────────────────────────
builder.Services.AddHttpClient("AzureMgmt", client =>
{
    client.Timeout = TimeSpan.FromSeconds(180);
});

// ── Azure Cost Management options ────────────────────────────────────────────
var costOptions = builder.Configuration
    .GetSection(CostManagementOptions.SectionName)
    .Get<CostManagementOptions>() ?? new CostManagementOptions();

// Merge exchange rates from config (overrides defaults)
var ratesFromConfig = builder.Configuration
    .GetSection($"{CostManagementOptions.SectionName}:ExchangeRates")
    .Get<Dictionary<string, decimal>>();
if (ratesFromConfig is { Count: > 0 })
    foreach (var (k, v) in ratesFromConfig)
        costOptions.ExchangeRates[k] = v;

builder.Services.AddSingleton(costOptions);

// ── SQL data platform (Phase 4) ──────────────────────────────────────────────
// Register a DbContext factory when a connection string is configured (set by the azd
// postprovision hook as ConnectionStrings__Sql). Singleton stores use the factory to
// create short-lived contexts. When absent, the audit and subscription stores fall back
// to Azure Table Storage / Key Vault so non-SQL deployments keep working unchanged.
var sqlConnectionString = builder.Configuration.GetConnectionString("Sql");
if (!string.IsNullOrWhiteSpace(sqlConnectionString))
    builder.Services.AddDbContextFactory<CmcspDbContext>(opt => opt.UseSqlServer(sqlConnectionString));

// Cache service: Azure Managed Redis (Phase 4) when Redis:Enabled, else Azure Table/Blob.
// Both wrap IMemoryCache as the L1 tier and implement ICacheService.
if (costOptions.Redis.Enabled)
    builder.Services.AddSingleton<ICacheService, RedisCacheService>();
else
    builder.Services.AddSingleton<ICacheService, AzureStorageCacheService>();

// SubscriptionStoreService must be registered immediately after costOptions so that
// user-persisted subscription IDs are merged into costOptions.SubscriptionIds before
// any cost service starts up.
builder.Services.AddSingleton<SubscriptionStoreService>();

// ── Azure services ─────────────────────────────────────────────
// AzureTokenService is Singleton: MSAL manages its own internal token cache.
builder.Services.AddSingleton<AzureTokenService>();

// ExportProvisioningService automatically creates a daily Cost Management export and
// grants its managed identity Storage Blob Data Contributor when a subscription is
// added through the UI.  No-op when ExportBlob.Enabled = false or
// StorageAccountResourceId is not configured.
builder.Services.AddSingleton<ExportProvisioningService>();

// DataLoadingStateService is Singleton: tracks per-dataset load phases so the
// loading banner in the UI can react in real time without polling.
builder.Services.AddSingleton<DataLoadingStateService>();

// CostManagementService is Singleton: IMemoryCache and IHttpClientFactory are
// both Singleton-safe; rate-limit state should survive across requests.
// When ExportBlob.Enabled = true the BlobCostManagementService is used instead,
// which reads pre-built export CSVs from Azure Blob Storage — no Query API rate limits.
if (costOptions.ExportBlob.Enabled)
{
    // Register the concrete API service so BlobCostManagementService can use it
    // as a fallback when no export blobs exist yet (e.g. before the first daily export runs).
    builder.Services.AddSingleton<CostManagementService>();
    builder.Services.AddSingleton<ICostManagementService, BlobCostManagementService>();
    builder.Services.AddHostedService<CacheWarmupService>();
    // Daily refresh now runs externally as the CostCollectorJob Container Apps Job
    // (scheduled nightly + on-demand via the dashboard "Collect now" button), so the
    // in-process DailyApiRefreshService hosted service has been retired.
    builder.Services.AddHostedService<SubscriptionExportReconcileService>();
}
else
{
    builder.Services.AddSingleton<ICostManagementService, CostManagementService>();
    // CacheWarmupService pre-fetches all three datasets in the background after
    // startup so the first user doesn't wait for cold API calls.
    builder.Services.AddHostedService<CacheWarmupService>();
}

// OptimizationService (Phase 7): joins cost data to live Azure inventory via Azure Resource
// Graph (inventory + orphaned-resource finder) and Microsoft.Consumption/Capacity (reservation
// purchase recommendations + expiry). Singleton — stateless ARM reads with an in-process TTL memo.
builder.Services.AddSingleton<OptimizationService>();

// SecurityPostureService (Phase 8): Defender for Cloud secure score + top control findings per
// subscription (Microsoft.Security/secureScores). SustainabilityService (Phase 8): Carbon
// Optimization emissions (Microsoft.Carbon/carbonEmissionReports). Both are read-only ARM reads
// with an in-process TTL memo, covered by the existing Reader grant.
builder.Services.AddSingleton<SecurityPostureService>();
builder.Services.AddSingleton<SustainabilityService>();

// CollectionAuditService reads the cost-collector job's audit trail (last-run status,
// row counts, trigger, duration) from Table Storage so the dashboard can surface it.
builder.Services.AddSingleton<CollectionAuditService>();

// JobControlService starts the CostCollectorJob on demand (ARM jobs/start via the app
// managed identity) and polls its execution status, coalescing onto a running execution.
builder.Services.AddSingleton<JobControlService>();

// DashboardStateService is Scoped: one instance per SignalR circuit so each
// browser tab gets its own date-range filter.
builder.Services.AddScoped<DashboardStateService>();

// CostDetailsService: async report-based API for reservation and amortized-cost data.
// Registered as Singleton — stateless fetch + shared cache; safe for concurrent use.
// Only actively used when CostDetails:Enabled = true but always registered so pages
// can inject ICostDetailsService and check HasBillingAccountAccess at runtime.
builder.Services.AddSingleton<ICostDetailsService, CostDetailsService>();

// ── HTTPS / HSTS ─────────────────────────────────────────────────────────────
builder.Services.AddHsts(opts =>
{
    opts.MaxAge = TimeSpan.FromDays(365);
    opts.IncludeSubDomains = true;
});

// ── App pipeline ─────────────────────────────────────────────────────────────
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ── Login / logout endpoints ──────────────────────────────────────────────────
// These are plain HTTP endpoints so the OIDC challenge can be issued outside
// the Blazor SignalR circuit (where HTTP responses are not available).
app.MapGet("/login", async (HttpContext ctx, string? redirectUri) =>
    await ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new AuthenticationProperties { RedirectUri = redirectUri ?? "/" }))
    .AllowAnonymous();

app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new AuthenticationProperties { RedirectUri = "/" });
}).RequireAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
