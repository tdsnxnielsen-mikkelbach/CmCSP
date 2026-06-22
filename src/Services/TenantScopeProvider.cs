using System.Security.Claims;
using CmCSP.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace CmCSP.Services;

/// <summary>
/// Resolves the signed-in user's tenant (<c>tid</c> claim) into the <see cref="TenantScope"/>
/// the request may read. Scoped to the Blazor circuit so the result is resolved once per user
/// session. The <see cref="TenantScope"/> record itself lives in CmCSP.Core so the singleton
/// cost services can consume it.
/// </summary>
public interface ITenantScopeProvider
{
    /// <summary>Resolves (and memoises) the scope for the current signed-in user.</summary>
    ValueTask<TenantScope> GetScopeAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class TenantScopeProvider(
    CostManagementOptions options,
    CustomerStore customers,
    AuthenticationStateProvider authState,
    ILogger<TenantScopeProvider> logger) : ITenantScopeProvider
{
    // Microsoft.Identity.Web surfaces the tenant id under either claim type.
    private static readonly string[] TenantClaimTypes =
        ["http://schemas.microsoft.com/identity/claims/tenantid", "tid"];

    private TenantScope? _cached;

    public async ValueTask<TenantScope> GetScopeAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        // Feature off, or SQL/customer registry absent → behave exactly as the single-tenant app.
        if (!options.MultiTenancy.Enabled || !customers.IsEnabled)
            return _cached = TenantScope.Unscoped;

        var state = await authState.GetAuthenticationStateAsync();
        var tid = ResolveTenantId(state.User);

        if (string.IsNullOrWhiteSpace(tid))
        {
            logger.LogWarning("No tenant (tid) claim on the signed-in principal; denying scope.");
            return _cached = TenantScope.Denied;
        }

        // Partner (home tenant) → every active customer.
        if (customers.IsHomeTenant(tid))
        {
            var active = await customers.GetActiveCustomersAsync(ct);
            return _cached = new TenantScope
            {
                IsPartner   = true,
                TenantId    = tid,
                CustomerIds = active.Select(c => c.Id).ToList()
            };
        }

        // Customer tenant → only their own customer.
        var customer = await customers.GetByTenantAsync(tid, ct);
        if (customer is null)
        {
            logger.LogWarning("Sign-in from unregistered/suspended tenant {Tid}; denying scope.", tid);
            return _cached = TenantScope.Denied;
        }

        return _cached = new TenantScope
        {
            TenantId    = tid,
            CustomerIds = [customer.Id]
        };
    }

    private static string? ResolveTenantId(ClaimsPrincipal user)
    {
        foreach (var type in TenantClaimTypes)
        {
            var value = user.FindFirst(type)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }
}
