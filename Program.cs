using CmCSP.Components;
using CmCSP.Models;
using CmCSP.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

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
// IMemoryCache is always registered (used as the in-process layer inside AzureStorageCacheService).
builder.Services.AddMemoryCache();

// AzureStorageCacheService wraps IMemoryCache.
// When AzureCache:Enabled = true it also persists to Azure Table + Blob Storage so that
// multiple Container App replicas share the same cache and survive restarts.
builder.Services.AddSingleton<AzureStorageCacheService>();

// ── Named HttpClient for Azure Management API ────────────────────────────────
builder.Services.AddHttpClient("AzureMgmt", client =>
{
    client.Timeout = TimeSpan.FromSeconds(90);
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
    // Daily refresh: call the Query API once per day so dashboards always show
    // up-to-date figures, independent of when the blob export last landed.
    builder.Services.AddHostedService<DailyApiRefreshService>();
    builder.Services.AddHostedService<SubscriptionExportReconcileService>();
}
else
{
    builder.Services.AddSingleton<ICostManagementService, CostManagementService>();
    // CacheWarmupService pre-fetches all three datasets in the background after
    // startup so the first user doesn't wait for cold API calls.
    builder.Services.AddHostedService<CacheWarmupService>();
}

// DashboardStateService is Scoped: one instance per SignalR circuit so each
// browser tab gets its own date-range filter.
builder.Services.AddScoped<DashboardStateService>();

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
