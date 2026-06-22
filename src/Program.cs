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
using Microsoft.IdentityModel.Tokens;
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

// ── Phase 9: multi-tenant sign-in (gated) ────────────────────────────────────
// When AzureCostManagement:MultiTenancy:Enabled = true the Entra app registration is
// multi-tenant: sign-in is accepted from any tenant, but the issuer is validated against
// the home tenant + every active registered customer (CustomerStore). Off by default, so
// the single-tenant deployment is unchanged (authority stays bound to the home tenant).
var multiTenancyEnabled = builder.Configuration.GetValue<bool>(
    $"{CostManagementOptions.SectionName}:MultiTenancy:Enabled");
if (multiTenancyEnabled)
{
    builder.Services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
        .PostConfigure<CustomerStore>((options, customerStore) =>
        {
            // Accept sign-in from any organisation; restrict via the issuer validator below.
            options.Authority = "https://login.microsoftonline.com/organizations/v2.0";
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
            {
                var tid = ExtractTenantIdFromIssuer(issuer);
                if (customerStore.IsValidTenant(tid))
                    return issuer;
                throw new SecurityTokenInvalidIssuerException(
                    $"Issuer tenant '{tid}' is not the home tenant or an active registered customer.");
            };
        });
}

// Pulls the tenant GUID out of an Entra issuer URL — v2 (login.microsoftonline.com/{tid}/v2.0)
// or v1 (sts.windows.net/{tid}/). Returns the first path segment that parses as a GUID.
static string? ExtractTenantIdFromIssuer(string? issuer)
{
    if (string.IsNullOrWhiteSpace(issuer) || !Uri.TryCreate(issuer, UriKind.Absolute, out var uri))
        return null;
    foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        if (Guid.TryParse(segment, out _))
            return segment;
    return null;
}

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

// CustomerStore (Phase 9): the SQL-backed registry of customers (one per tenant) and the
// reverse subscription→customer lookup. Reports IsEnabled=false when SQL is absent, keeping
// the app in its legacy single-tenant behaviour. Also feeds the multi-tenant OIDC issuer
// validator above (resolved at runtime).
builder.Services.AddSingleton<CustomerStore>();

// GdapOnboardingService (Phase 9): GDAP-driven onboarding — builds the per-customer admin-consent
// link and auto-discovers a customer tenant's subscriptions via a per-tenant ARM token, replacing
// manual subscription-ID entry. Cross-tenant discovery requires service-principal mode.
builder.Services.AddSingleton<GdapOnboardingService>();

// TenantNameService (Phase 9): process-wide cache of tenant id → display name (Graph, then the
// customer registry, then the raw GUID) so the partner UI can label tenants by name everywhere
// without each page re-issuing a Graph call.
builder.Services.AddSingleton<TenantNameService>();

// TenantScopeAccessor (Phase 9): ambient holder of the current circuit's tenant scope, read by
// the singleton cost service for cache-key prefixing + SQL scoping. Singleton because it wraps
// an AsyncLocal whose value is per-async-flow (published by CostPageBase before each load).
builder.Services.AddSingleton<TenantScopeAccessor>();

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

// ITenantScopeProvider (Phase 9): resolves the signed-in user's tenant (tid claim) into the
// set of customers the request may read. Scoped to the circuit so it's resolved once per
// session. Returns TenantScope.Unscoped (no filtering) when MultiTenancy is disabled.
builder.Services.AddScoped<ITenantScopeProvider, TenantScopeProvider>();

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

// ── GDAP admin-consent callback ────────────────────────────────────────────────
// Where Entra returns a CUSTOMER's Global Admin after they grant this multi-tenant
// app delegated access (see GdapOnboardingService.BuildAdminConsentUrl). This MUST
// be anonymous: the customer admin is not a dashboard user, so it must NOT trigger
// an OIDC sign-in challenge (that would bounce them to the home tenant and fail with
// AADSTS50020). It just renders a terminal "you can close this window" page.
// Entra appends: admin_consent=True & tenant={customerTid} & state={customerTid} on
// success, or error & error_description on failure.
app.MapGet("/gdap/consent-callback", (HttpContext ctx) =>
{
    static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

    var consented = string.Equals(ctx.Request.Query["admin_consent"], "True", StringComparison.OrdinalIgnoreCase);
    var tenant = Enc(ctx.Request.Query["tenant"]);
    var error = Enc(ctx.Request.Query["error"]);
    var errorDescription = Enc(ctx.Request.Query["error_description"]);

    string title, message, colour;
    if (consented)
    {
        title = "Access granted";
        message = $"Delegated access was granted for tenant <code>{tenant}</code>. " +
                  "You can close this window and return to your partner. " +
                  "They can now discover your subscriptions in the dashboard.";
        colour = "#107c10";
    }
    else
    {
        title = "Consent not completed";
        message = string.IsNullOrEmpty(error)
            ? "Consent was cancelled or did not complete. You can close this window and try the link again."
            : $"Consent failed: <code>{error}</code> – {errorDescription}. You can close this window and try again.";
        colour = "#a4262c";
    }

    var html = $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{Enc(title)}}</title>
        <style>
          body{font-family:Segoe UI,system-ui,sans-serif;background:#faf9f8;color:#201f1e;
               display:flex;min-height:100vh;align-items:center;justify-content:center;margin:0}
          .card{background:#fff;border:1px solid #edebe9;border-radius:8px;padding:2rem 2.5rem;
                max-width:30rem;box-shadow:0 1.6px 3.6px rgba(0,0,0,.13)}
          h1{font-size:1.25rem;margin:0 0 .75rem;color:{{colour}}}
          p{font-size:.95rem;line-height:1.5;margin:0}
          code{background:#f3f2f1;padding:.1rem .3rem;border-radius:3px;font-size:.85em}
        </style></head>
        <body><div class="card"><h1>{{Enc(title)}}</h1><p>{{message}}</p></div></body></html>
        """;

    return Results.Content(html, "text/html");
}).AllowAnonymous();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
