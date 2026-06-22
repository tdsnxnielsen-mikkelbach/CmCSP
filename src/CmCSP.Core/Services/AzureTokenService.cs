using Azure.Core;
using Azure.Identity;
using Microsoft.Identity.Client;
using System.Text.Json;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Acquires OAuth2 bearer tokens for the Azure Management API.
///
/// Two authentication modes:
///  • ClientSecret set  → MSAL client-credentials flow (service principal).
///  • ClientSecret null → DefaultAzureCredential (Managed Identity in Azure,
///                        az login / VS credential locally).  This is the
///                        preferred path when the app runs as a Container App
///                        with a SystemAssigned identity.
///
/// Phase 9 (multi-tenancy): when a per-customer <see cref="TenantScope"/> is ambient and the
/// service is running in service-principal mode, ARM tokens are acquired against the customer's
/// own tenant authority (GDAP delegated access). MSAL keeps a per-tenant token cache, so tenants
/// never share cached tokens. In the single-tenant path the scope is Unscoped and behaviour is
/// identical to before. Managed-identity mode cannot cross tenants, so it always uses the home
/// identity regardless of scope.
/// </summary>
public sealed class AzureTokenService
{
    private static readonly string[]    MsalScopes  = ["https://management.azure.com/.default"];
    private static readonly string[]    AzureScopes = ["https://management.azure.com/.default"];

    private readonly IConfidentialClientApplication? _app;
    private readonly TokenCredential?                _credential;
    private readonly TenantScopeAccessor?            _scopeAccessor;
    private readonly string                          _homeTenantId;

    /// <summary>
    /// <c>true</c> when using MSAL client-credentials (Entra App SP);
    /// <c>false</c> when falling back to DefaultAzureCredential (Managed Identity / az login).
    /// Export provisioning requires the SP path — call this before attempting provisioning.
    /// </summary>
    public bool UsingServicePrincipal => _app is not null;

    public AzureTokenService(CostManagementOptions options, TenantScopeAccessor? scopeAccessor = null)
    {
        _scopeAccessor = scopeAccessor;
        _homeTenantId  = string.IsNullOrWhiteSpace(options.MultiTenancy.HomeTenantId)
            ? options.TenantId
            : options.MultiTenancy.HomeTenantId;

        if (!string.IsNullOrEmpty(options.ClientSecret))
        {
            _app = ConfidentialClientApplicationBuilder
                .Create(options.ClientId)
                .WithClientSecret(options.ClientSecret)
                .WithAuthority(AzureCloudInstance.AzurePublic, options.TenantId)
                .Build();
        }
        else
        {
            // No client secret configured – fall back to DefaultAzureCredential.
            // In Azure Container Apps this uses the SystemAssigned managed identity.
            // Locally it tries az login / Visual Studio / environment credentials.
            _credential = new DefaultAzureCredential();
        }
    }

    /// <summary>
    /// Acquires an ARM token for the tenant resolved from the ambient <see cref="TenantScope"/>
    /// (the home tenant in the single-tenant path).
    /// </summary>
    public Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
        GetAccessTokenAsync(ResolveCustomerTenantId(), ct);

    /// <summary>
    /// Acquires an ARM token for a specific customer tenant (GDAP delegated access). When
    /// <paramref name="customerTenantId"/> is null/empty or the home tenant, the home authority
    /// is used. Cross-tenant acquisition requires service-principal mode; managed-identity mode
    /// ignores the tenant and uses the home identity.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(string? customerTenantId, CancellationToken ct = default)
    {
        if (_app is not null)
        {
            var builder = _app.AcquireTokenForClient(MsalScopes);

            // Per-tenant authority override for cross-tenant (GDAP) reads. MSAL caches the
            // resulting token per tenant, so customers never share a cached token.
            if (!string.IsNullOrWhiteSpace(customerTenantId) &&
                !string.Equals(customerTenantId, _homeTenantId, StringComparison.OrdinalIgnoreCase))
            {
                builder = builder.WithTenantId(customerTenantId);
            }

            var result = await builder.ExecuteAsync(ct);
            return result.AccessToken;
        }

        // Managed-identity / DefaultAzureCredential cannot acquire cross-tenant tokens with a
        // single identity, so the customer tenant is intentionally ignored here.
        var token = await _credential!.GetTokenAsync(
            new TokenRequestContext(AzureScopes), ct);
        return token.Token;
    }

    /// <summary>
    /// The customer tenant to acquire tokens for, taken from the ambient scope. Returns null in
    /// the single-tenant/unscoped/partner-aggregate path so the home authority is used.
    /// </summary>
    private string? ResolveCustomerTenantId()
    {
        var scope = _scopeAccessor?.Current;
        if (scope is null || scope.IsUnscoped || scope.IsPartner || scope.IsDenied)
            return null;

        return string.IsNullOrWhiteSpace(scope.TenantId) ? null : scope.TenantId;
    }

    /// <summary>
    /// Returns the Entra App service principal's directory object id (the <c>oid</c> claim
    /// from its own access token), or <c>null</c> when not running in service-principal mode.
    /// Used as the <c>principalId</c> when assigning roles to the SP — avoids a Microsoft
    /// Graph lookup, since the SP's token already contains its object id.
    /// </summary>
    public async Task<string?> GetServicePrincipalObjectIdAsync(CancellationToken ct = default)
    {
        if (_app is null) return null;

        var token = await GetAccessTokenAsync(ct);
        var parts = token.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += (payload.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("oid", out var oid) ? oid.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
