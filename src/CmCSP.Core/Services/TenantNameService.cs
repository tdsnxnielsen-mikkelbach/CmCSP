using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CmCSP.Services;

/// <summary>
/// Phase 9: a process-wide, best-effort cache of Entra <b>tenant id → display name</b> so the
/// partner UI can label customers by name instead of GUID without every page issuing its own
/// Microsoft Graph call. Resolution order: Graph
/// (<see cref="GdapOnboardingService.ResolveTenantDisplayNameAsync"/>) → the customer registry's
/// stored <c>DisplayName</c> → the raw tenant id. Resolved names are memoised for the lifetime of
/// the process (tenant names change rarely).
/// </summary>
public sealed class TenantNameService
{
    private readonly GdapOnboardingService          _gdap;
    private readonly CustomerStore                   _customers;
    private readonly ILogger<TenantNameService>      _logger;
    private readonly ConcurrentDictionary<string, string> _names =
        new(StringComparer.OrdinalIgnoreCase);

    public TenantNameService(
        GdapOnboardingService          gdap,
        CustomerStore                  customers,
        ILogger<TenantNameService>     logger)
    {
        _gdap      = gdap;
        _customers = customers;
        _logger    = logger;
    }

    /// <summary>
    /// Returns a human-friendly label for <paramref name="tenantId"/>, resolving and caching it on
    /// first use. Never throws and never blocks indefinitely; falls back to the tenant id.
    /// </summary>
    public async Task<string> GetDisplayNameAsync(string? tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return string.Empty;

        var key = tenantId.Trim();
        if (_names.TryGetValue(key, out var cached))
            return cached;

        string resolved;
        try
        {
            resolved = await _gdap.ResolveTenantDisplayNameAsync(key, ct) ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TenantNameService: Graph lookup for {Tenant} failed.", key);
            resolved = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            try
            {
                var customer = await _customers.GetByTenantAsync(key, ct);
                if (!string.IsNullOrWhiteSpace(customer?.DisplayName))
                    resolved = customer!.DisplayName;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TenantNameService: registry lookup for {Tenant} failed.", key);
            }
        }

        if (string.IsNullOrWhiteSpace(resolved))
            resolved = key; // last resort — show the GUID rather than nothing

        _names[key] = resolved;
        return resolved;
    }

    /// <summary>
    /// Synchronous best-effort lookup that returns a previously-resolved name or the tenant id if
    /// it has not been resolved yet. Useful inside render paths where awaiting is not possible;
    /// call <see cref="GetDisplayNameAsync"/> during load to warm the cache first.
    /// </summary>
    public string GetCachedOrId(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return string.Empty;
        var key = tenantId.Trim();
        return _names.TryGetValue(key, out var name) ? name : key;
    }
}
