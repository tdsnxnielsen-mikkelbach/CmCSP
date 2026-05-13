using CmCSP.Components;
using CmCSP.Models;
using CmCSP.Services;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Razor / Blazor ──────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── MudBlazor ───────────────────────────────────────────────────────────────
builder.Services.AddMudServices();

// ── Caching ─────────────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();

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

// ── Azure services ───────────────────────────────────────────────────────────
// AzureTokenService is Singleton: MSAL manages its own internal token cache.
builder.Services.AddSingleton<AzureTokenService>();

// CostManagementService is Singleton: IMemoryCache and IHttpClientFactory are
// both Singleton-safe; rate-limit state should survive across requests.
builder.Services.AddSingleton<ICostManagementService, CostManagementService>();

// DashboardStateService is Scoped: one instance per SignalR circuit so each
// browser tab gets its own date-range filter.
builder.Services.AddScoped<DashboardStateService>();

// ── App pipeline ─────────────────────────────────────────────────────────────
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
