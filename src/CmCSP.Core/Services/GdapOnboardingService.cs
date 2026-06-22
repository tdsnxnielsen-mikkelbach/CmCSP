using System.Net.Http.Headers;
using System.Text.Json;
using CmCSP.Models;
using Microsoft.Extensions.Logging;

namespace CmCSP.Services;

/// <summary>
/// Phase 9 (CSP multi-tenancy): GDAP-driven onboarding helpers that replace manual
/// subscription-ID entry for a customer.
///
/// Two responsibilities, both deliberately <b>Azure-only</b> (no Partner Center API — the GDAP
/// relationship itself is created out-of-band in the Partner Center portal and its id is recorded
/// on the <c>Customer</c> row):
///
///  1. <see cref="BuildAdminConsentUrl"/> — produces the per-customer admin-consent link the
///     partner sends to the customer's Entra admin. Consent grants this multi-tenant app delegated
///     access in the customer tenant (alongside the GDAP relationship).
///  2. <see cref="DiscoverSubscriptionsAsync"/> / <see cref="SyncSubscriptionsAsync"/> — once
///     delegated access exists, enumerates the customer's Azure subscriptions via ARM using a
///     <b>per-tenant</b> token (GDAP) and maps them onto the customer, so the partner never has to
///     type subscription GUIDs by hand.
///
/// Cross-tenant token acquisition requires service-principal mode (a client secret); managed-identity
/// mode cannot cross tenants, so <see cref="CanAcquireCrossTenantTokens"/> reports whether discovery
/// is available.
/// </summary>
public sealed class GdapOnboardingService
{
    private const string SubscriptionsApiVersion = "2022-12-01";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory     _httpFactory;
    private readonly AzureTokenService      _tokenService;
    private readonly CustomerStore          _customers;
    private readonly CostManagementOptions  _options;
    private readonly ILogger<GdapOnboardingService> _logger;

    public GdapOnboardingService(
        IHttpClientFactory             httpFactory,
        AzureTokenService              tokenService,
        CustomerStore                  customers,
        CostManagementOptions          options,
        ILogger<GdapOnboardingService> logger)
    {
        _httpFactory  = httpFactory;
        _tokenService = tokenService;
        _customers    = customers;
        _options      = options;
        _logger       = logger;
    }

    /// <summary>
    /// <c>true</c> when the app runs in service-principal mode and can therefore acquire per-tenant
    /// (GDAP) tokens to read a customer tenant. Managed-identity mode cannot cross tenants, so
    /// subscription discovery is unavailable and the partner must map subscriptions manually.
    /// </summary>
    public bool CanAcquireCrossTenantTokens => _tokenService.UsingServicePrincipal;

    /// <summary>
    /// Builds the admin-consent URL a customer's Entra admin opens to grant this multi-tenant app
    /// delegated access in their tenant. The customer admin signs in to <b>their</b> tenant and
    /// consents on behalf of the organisation.
    /// </summary>
    /// <param name="customerTenantId">The customer's Entra tenant GUID.</param>
    /// <param name="redirectUri">
    /// Where Entra returns the admin after consent (must be a registered redirect URI on the app).
    /// Typically the dashboard's <c>/customers</c> page.
    /// </param>
    public string BuildAdminConsentUrl(string customerTenantId, string redirectUri)
    {
        if (string.IsNullOrWhiteSpace(customerTenantId) || !Guid.TryParse(customerTenantId.Trim(), out _))
            throw new ArgumentException("A valid customer tenant GUID is required.", nameof(customerTenantId));
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new InvalidOperationException("AzureCostManagement:ClientId is not configured.");

        var query = new Dictionary<string, string?>
        {
            ["client_id"]    = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["state"]        = customerTenantId.Trim(),
            // Force an account picker so Entra does not silently reuse the partner/home-tenant
            // session. The admin MUST consent with a Global Administrator native to the customer
            // tenant; reusing a foreign account triggers AADSTS50020 ("account does not exist in
            // this tenant"). prompt=select_account makes that choice explicit.
            ["prompt"]       = "select_account"
        };

        var qs = string.Join('&', query
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));

        return $"https://login.microsoftonline.com/{customerTenantId.Trim()}/adminconsent?{qs}";
    }

    /// <summary>
    /// Enumerates the Azure subscriptions visible to this app in a customer tenant (GDAP delegated
    /// access) via ARM <c>GET /subscriptions</c>, acquiring a per-tenant token for that tenant.
    /// Returns an empty list (and logs a warning) when access has not been granted yet.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredSubscription>> DiscoverSubscriptionsAsync(
        string customerTenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customerTenantId) || !Guid.TryParse(customerTenantId.Trim(), out _))
            throw new ArgumentException("A valid customer tenant GUID is required.", nameof(customerTenantId));

        if (!CanAcquireCrossTenantTokens)
        {
            _logger.LogWarning(
                "Subscription discovery for tenant {Tenant} skipped: app is not in service-principal " +
                "mode, so cross-tenant (GDAP) tokens cannot be acquired.", customerTenantId);
            return [];
        }

        var token  = await _tokenService.GetAccessTokenAsync(customerTenantId.Trim(), ct);
        var client = _httpFactory.CreateClient("AzureMgmt");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var results = new List<DiscoveredSubscription>();
        var url = $"https://management.azure.com/subscriptions?api-version={SubscriptionsApiVersion}";

        // ARM paginates with nextLink; follow it until exhausted.
        while (!string.IsNullOrEmpty(url))
        {
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Subscription discovery for tenant {Tenant} returned {Status}. Has the customer " +
                    "consented and the GDAP relationship been established?",
                    customerTenantId, (int)response.StatusCode);
                return results;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in value.EnumerateArray())
                {
                    var subId = el.TryGetProperty("subscriptionId", out var s) ? s.GetString() : null;
                    if (string.IsNullOrWhiteSpace(subId)) continue;

                    results.Add(new DiscoveredSubscription(
                        SubscriptionId: subId!,
                        DisplayName:    el.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : "",
                        State:          el.TryGetProperty("state", out var st) ? st.GetString() ?? "" : ""));
                }
            }

            url = root.TryGetProperty("nextLink", out var next) ? next.GetString() ?? string.Empty : string.Empty;
        }

        _logger.LogInformation(
            "Discovered {Count} subscription(s) in customer tenant {Tenant}.", results.Count, customerTenantId);
        return results;
    }

    /// <summary>
    /// Discovers the customer's subscriptions (GDAP) and maps any not already mapped onto the
    /// customer, caching each subscription's display name. Enabled subscriptions only. Idempotent —
    /// re-running only adds newly-found subscriptions.
    /// </summary>
    public async Task<SubscriptionSyncResult> SyncSubscriptionsAsync(
        long customerId, string customerTenantId, CancellationToken ct = default)
    {
        var discovered = await DiscoverSubscriptionsAsync(customerTenantId, ct);

        var existing = new HashSet<string>(
            await _customers.GetSubscriptionIdsAsync(customerId, ct), StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var sub in discovered)
        {
            // Skip disabled/warned/deleted subscriptions and ones already mapped.
            if (!sub.State.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) continue;
            if (existing.Contains(sub.SubscriptionId)) continue;

            await _customers.AddSubscriptionAsync(customerId, sub.SubscriptionId, sub.DisplayName, ct);
            added++;
        }

        _logger.LogInformation(
            "Synced subscriptions for customer #{Id}: {Discovered} discovered, {Added} newly mapped.",
            customerId, discovered.Count, added);

        return new SubscriptionSyncResult(discovered.Count, added, discovered);
    }
}

/// <summary>A subscription found in a customer tenant during GDAP discovery.</summary>
public sealed record DiscoveredSubscription(string SubscriptionId, string DisplayName, string State);

/// <summary>Outcome of <see cref="GdapOnboardingService.SyncSubscriptionsAsync"/>.</summary>
public sealed record SubscriptionSyncResult(
    int Discovered, int Added, IReadOnlyList<DiscoveredSubscription> Subscriptions);
