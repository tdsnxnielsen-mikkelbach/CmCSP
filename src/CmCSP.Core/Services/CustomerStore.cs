using CmCSP.Data;
using CmCSP.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CmCSP.Services;

/// <summary>
/// Phase 9 (CSP multi-tenancy): the registry of customers (one per Entra tenant) the partner
/// has delegated access into, and the reverse <c>subscription → customer</c> lookup used during
/// authorization.
///
/// Backed by the <c>Customer</c> / <c>CustomerSubscription</c> SQL tables when the data platform
/// is provisioned (an <see cref="IDbContextFactory{TContext}"/> is registered). When SQL is
/// absent the store reports <see cref="IsEnabled"/> = <c>false</c> and the app stays in its
/// legacy single-tenant behaviour.
///
/// A small in-memory snapshot of valid issuer tenant IDs (home tenant + every <c>active</c>
/// customer tenant) is cached so the OIDC <c>IssuerValidator</c> — which runs synchronously
/// during token validation — can authorise a sign-in without a per-request SQL round-trip.
/// </summary>
public sealed class CustomerStore
{
    private readonly CostManagementOptions _options;
    private readonly IDbContextFactory<CmcspDbContext>? _dbFactory;
    private readonly ILogger<CustomerStore> _logger;

    // Valid issuer tenants = home tenant + active customer tenants. Refreshed from SQL.
    private volatile HashSet<string> _validTenantIds = new(StringComparer.OrdinalIgnoreCase);

    public CustomerStore(
        CostManagementOptions options,
        ILogger<CustomerStore> logger,
        IDbContextFactory<CmcspDbContext>? dbFactory = null)
    {
        _options   = options;
        _logger    = logger;
        _dbFactory = dbFactory;

        // Always trust the configured home tenant, even before the first SQL refresh.
        _validTenantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { HomeTenantId };

        if (_dbFactory is not null)
        {
            try { RefreshValidTenants(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Initial customer-tenant refresh failed; using home tenant only."); }
        }
    }

    /// <summary><c>true</c> when the SQL data platform backs the customer registry.</summary>
    public bool IsEnabled => _dbFactory is not null;

    /// <summary>
    /// The CSP's own (home) tenant GUID — <c>MultiTenancy:HomeTenantId</c> when set, otherwise the
    /// configured <see cref="CostManagementOptions.TenantId"/>.
    /// </summary>
    public string HomeTenantId =>
        string.IsNullOrWhiteSpace(_options.MultiTenancy.HomeTenantId)
            ? _options.TenantId
            : _options.MultiTenancy.HomeTenantId;

    /// <summary>
    /// <c>true</c> when the given token <c>tid</c> claim belongs to the home tenant or an
    /// <c>active</c> registered customer. Used by the OIDC issuer validator (synchronous).
    /// </summary>
    public bool IsValidTenant(string? tenantId) =>
        !string.IsNullOrWhiteSpace(tenantId) && _validTenantIds.Contains(tenantId);

    /// <summary>The home tenant ID equals the signed-in tenant ID (i.e. this is the partner).</summary>
    public bool IsHomeTenant(string? tenantId) =>
        !string.IsNullOrWhiteSpace(tenantId) &&
        string.Equals(tenantId, HomeTenantId, StringComparison.OrdinalIgnoreCase);

    /// <summary>All <c>active</c> customers (partner view). Empty when SQL is not configured.</summary>
    public async Task<IReadOnlyList<CustomerEntity>> GetActiveCustomersAsync(CancellationToken ct = default)
    {
        if (_dbFactory is null) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Customers
            .Where(c => c.Status == "active")
            .OrderBy(c => c.Id)
            .ToListAsync(ct);
    }

    /// <summary>Every customer (active + suspended), for the partner's management view.</summary>
    public async Task<IReadOnlyList<CustomerEntity>> GetAllCustomersAsync(CancellationToken ct = default)
    {
        if (_dbFactory is null) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Customers.OrderBy(c => c.Id).ToListAsync(ct);
    }

    /// <summary>
    /// The bootstrap "home" customer (lowest Id) — the owner of the single-tenant deployment's
    /// existing data. <c>null</c> when SQL is not configured or no customer has been seeded.
    /// </summary>
    public async Task<CustomerEntity?> GetHomeCustomerAsync(CancellationToken ct = default)
    {
        if (_dbFactory is null) return null;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Customers.OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>The <c>active</c> customer for a sign-in <c>tid</c>, or <c>null</c> if none.</summary>
    public async Task<CustomerEntity?> GetByTenantAsync(string tenantId, CancellationToken ct = default)
    {
        if (_dbFactory is null || string.IsNullOrWhiteSpace(tenantId)) return null;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Customers
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Status == "active", ct);
    }

    /// <summary>The customer with the given id, or <c>null</c>.</summary>
    public async Task<CustomerEntity?> GetByIdAsync(long customerId, CancellationToken ct = default)
    {
        if (_dbFactory is null) return null;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Customers.FindAsync([customerId], ct);
    }

    /// <summary>Reverse lookup: the customer that owns a subscription, or <c>null</c>.</summary>
    public async Task<CustomerEntity?> GetBySubscriptionAsync(string subscriptionId, CancellationToken ct = default)
    {
        if (_dbFactory is null || string.IsNullOrWhiteSpace(subscriptionId)) return null;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var map = await db.CustomerSubscriptions
            .FirstOrDefaultAsync(s => s.SubscriptionId == subscriptionId, ct);
        return map is null ? null : await db.Customers.FindAsync([map.CustomerId], ct);
    }

    /// <summary>
    /// Bulk reverse lookup: every mapped subscription id → its owning customer (id + tenant GUID),
    /// in a single query. Empty when the SQL data platform is not configured. Used to build the
    /// subscription directory without a per-subscription round-trip.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, (long CustomerId, string TenantId)>> GetSubscriptionOwnersAsync(
        CancellationToken ct = default)
    {
        if (_dbFactory is null)
            return new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.CustomerSubscriptions
            .Join(db.Customers,
                s => s.CustomerId,
                c => c.Id,
                (s, c) => new { s.SubscriptionId, c.Id, c.TenantId })
            .ToListAsync(ct);

        var result = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            result[r.SubscriptionId] = (r.Id, r.TenantId);
        return result;
    }

    /// <summary>The subscription IDs mapped to a customer.</summary>
    public async Task<IReadOnlyList<string>> GetSubscriptionIdsAsync(long customerId, CancellationToken ct = default)
    {
        if (_dbFactory is null) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.CustomerSubscriptions
            .Where(s => s.CustomerId == customerId)
            .Select(s => s.SubscriptionId)
            .ToListAsync(ct);
    }

    /// <summary>The subscription mappings for a customer (id + cached display name).</summary>
    public async Task<IReadOnlyList<CustomerSubscriptionEntity>> GetSubscriptionsAsync(long customerId, CancellationToken ct = default)
    {
        if (_dbFactory is null) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.CustomerSubscriptions
            .Where(s => s.CustomerId == customerId)
            .OrderBy(s => s.SubscriptionId)
            .ToListAsync(ct);
    }

    // ── Onboarding / mutations (partner only) ─────────────────────────────────

    /// <summary>
    /// Onboards a new customer (one Entra tenant). Idempotent on <c>TenantId</c> — re-onboarding an
    /// existing tenant updates its display name and reactivates it. Refreshes the issuer-validation
    /// cache so a user from the new tenant can sign in immediately.
    /// </summary>
    public async Task<CustomerEntity> OnboardCustomerAsync(
        string tenantId, string displayName, string? gdapRelationshipId = null, CancellationToken ct = default)
    {
        if (_dbFactory is null)
            throw new InvalidOperationException("Customer onboarding requires the SQL data platform.");
        if (string.IsNullOrWhiteSpace(tenantId) || !Guid.TryParse(tenantId.Trim(), out _))
            throw new ArgumentException("A valid Entra tenant GUID is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A display name is required.", nameof(displayName));

        tenantId = tenantId.Trim();
        displayName = displayName.Trim();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Customers.FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);
        if (existing is not null)
        {
            existing.DisplayName = displayName;
            existing.Status = "active";
            if (gdapRelationshipId is not null) existing.GdapRelationshipId = gdapRelationshipId;
            await db.SaveChangesAsync(ct);
            RefreshValidTenants();
            _logger.LogInformation("Re-onboarded existing customer {Tenant} ({Name}).", tenantId, displayName);
            return existing;
        }

        var customer = new CustomerEntity
        {
            TenantId           = tenantId,
            DisplayName        = displayName,
            Status             = "active",
            GdapRelationshipId = gdapRelationshipId,
            CreatedUtc         = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        RefreshValidTenants();
        _logger.LogInformation("Onboarded customer {Tenant} ({Name}) as #{Id}.", tenantId, displayName, customer.Id);
        return customer;
    }

    /// <summary>
    /// Imports (or updates) a customer discovered in the Ion Gateway directory. Records
    /// <see cref="CustomerEntity.Source"/> = <c>ion</c> and the primary domain so the UI can tell an
    /// Ion pricing-only customer from a natively onboarded (GDAP) one. Idempotent on <c>TenantId</c>:
    /// an existing customer keeps its native <c>Source</c>/status and GDAP link (it is only enriched
    /// with a domain if one was missing), while a new one is created as an active Ion customer.
    /// </summary>
    public async Task<CustomerEntity?> ImportIonCustomerAsync(
        string tenantId, string displayName, string? domain = null, CancellationToken ct = default)
    {
        if (_dbFactory is null)
            throw new InvalidOperationException("Customer import requires the SQL data platform.");
        if (string.IsNullOrWhiteSpace(tenantId) || !Guid.TryParse(tenantId.Trim(), out _))
            return null; // PCT directory rows can lack a tenant GUID — skip, cannot key a customer.

        tenantId = tenantId.Trim();
        displayName = string.IsNullOrWhiteSpace(displayName) ? tenantId : displayName.Trim();
        domain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Customers.FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);
        if (existing is not null)
        {
            // Never downgrade a native/GDAP customer to Ion; just backfill the domain if missing.
            if (string.IsNullOrWhiteSpace(existing.Domain) && domain is not null)
            {
                existing.Domain = domain;
                await db.SaveChangesAsync(ct);
            }
            return existing;
        }

        var customer = new CustomerEntity
        {
            TenantId    = tenantId,
            DisplayName = displayName,
            Status      = "active",
            Source      = "ion",
            Domain      = domain,
            CreatedUtc  = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        RefreshValidTenants();
        _logger.LogInformation("Imported Ion customer {Tenant} ({Name}) as #{Id}.", tenantId, displayName, customer.Id);
        return customer;
    }

    /// <summary>
    /// Bulk-imports customers discovered in the Ion Gateway directory in one pass. Loads every
    /// existing tenant id once so already-present customers (native or previously imported) are
    /// skipped without a per-row query, inserts new ones in batches, and rebuilds the
    /// issuer-validation cache a single time at the end. Reports progress via
    /// <paramref name="progress"/> so a long import can drive a load bar. Returns the imported and
    /// skipped counts. Idempotent: re-running skips everything already present.
    /// </summary>
    public async Task<(int Imported, int Skipped)> ImportIonCustomersBulkAsync(
        IReadOnlyList<(string TenantId, string DisplayName, string? Domain)> items,
        IProgress<CustomerImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (_dbFactory is null)
            throw new InvalidOperationException("Customer import requires the SQL data platform.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Snapshot every known tenant id up front; HashSet.Add doubles as the "already present"
        // test and de-dupes repeated tenants within the same directory page.
        var seen = new HashSet<string>(
            await db.Customers.Select(c => c.TenantId).ToListAsync(ct),
            StringComparer.OrdinalIgnoreCase);

        int imported = 0, skipped = 0, processed = 0, pendingSaves = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            processed++;

            var tid = item.TenantId?.Trim();
            if (string.IsNullOrWhiteSpace(tid) || !Guid.TryParse(tid, out _) || !seen.Add(tid))
            {
                skipped++;
            }
            else
            {
                db.Customers.Add(new CustomerEntity
                {
                    TenantId    = tid,
                    DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? tid : item.DisplayName.Trim(),
                    Status      = "active",
                    Source      = "ion",
                    Domain      = string.IsNullOrWhiteSpace(item.Domain) ? null : item.Domain.Trim(),
                    CreatedUtc  = now
                });
                imported++;
                pendingSaves++;
            }

            if (pendingSaves >= 100)
            {
                await db.SaveChangesAsync(ct);
                pendingSaves = 0;
            }
            if (processed % 10 == 0 || processed == items.Count)
                progress?.Report(new CustomerImportProgress(processed, items.Count, imported, skipped));
        }

        if (pendingSaves > 0)
            await db.SaveChangesAsync(ct);

        if (imported > 0)
        {
            RefreshValidTenants();
            _logger.LogInformation("Bulk-imported {Imported} Ion customer(s); skipped {Skipped} already-present.", imported, skipped);
        }

        return (imported, skipped);
    }

    /// <summary>
    /// Sets a customer's status (<c>active</c> / <c>suspended</c>). A suspended customer can no
    /// longer sign in or be collected. Refreshes the issuer-validation cache.
    /// </summary>
    public async Task SetCustomerStatusAsync(long customerId, string status, CancellationToken ct = default)
    {
        if (_dbFactory is null) return;
        if (status is not ("active" or "suspended"))
            throw new ArgumentException("Status must be 'active' or 'suspended'.", nameof(status));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var customer = await db.Customers.FindAsync([customerId], ct);
        if (customer is null) return;
        customer.Status = status;
        await db.SaveChangesAsync(ct);
        RefreshValidTenants();
        _logger.LogInformation("Customer #{Id} status set to {Status}.", customerId, status);
    }

    /// <summary>Maps a subscription to a customer. Idempotent on <c>(CustomerId, SubscriptionId)</c>.</summary>
    public async Task AddSubscriptionAsync(
        long customerId, string subscriptionId, string subscriptionName = "", CancellationToken ct = default)
    {
        if (_dbFactory is null)
            throw new InvalidOperationException("Mapping subscriptions requires the SQL data platform.");
        if (string.IsNullOrWhiteSpace(subscriptionId) || !Guid.TryParse(subscriptionId.Trim(), out _))
            throw new ArgumentException("A valid subscription GUID is required.", nameof(subscriptionId));

        subscriptionId = subscriptionId.Trim();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var exists = await db.CustomerSubscriptions
            .AnyAsync(s => s.CustomerId == customerId && s.SubscriptionId == subscriptionId, ct);
        if (exists)
        {
            _logger.LogDebug("Subscription {Sub} already mapped to customer #{Id}.", subscriptionId, customerId);
            return;
        }

        db.CustomerSubscriptions.Add(new CustomerSubscriptionEntity
        {
            CustomerId       = customerId,
            SubscriptionId   = subscriptionId,
            SubscriptionName = subscriptionName?.Trim() ?? string.Empty,
            AddedUtc         = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Mapped subscription {Sub} to customer #{Id}.", subscriptionId, customerId);
    }

    /// <summary>Removes a subscription mapping from a customer.</summary>
    public async Task RemoveSubscriptionAsync(long customerId, string subscriptionId, CancellationToken ct = default)
    {
        if (_dbFactory is null) return;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var map = await db.CustomerSubscriptions
            .FirstOrDefaultAsync(s => s.CustomerId == customerId && s.SubscriptionId == subscriptionId, ct);
        if (map is null) return;
        db.CustomerSubscriptions.Remove(map);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Unmapped subscription {Sub} from customer #{Id}.", subscriptionId, customerId);
    }

    /// <summary>
    /// Records (or clears) the GDAP relationship id on a customer. The relationship itself is
    /// created out-of-band in the Partner Center portal; this stores its id for reference/auditing.
    /// </summary>
    public async Task UpdateGdapRelationshipAsync(
        long customerId, string? gdapRelationshipId, CancellationToken ct = default)
    {
        if (_dbFactory is null) return;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var customer = await db.Customers.FindAsync([customerId], ct);
        if (customer is null) return;
        customer.GdapRelationshipId = string.IsNullOrWhiteSpace(gdapRelationshipId) ? null : gdapRelationshipId.Trim();
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Customer #{Id} GDAP relationship id updated.", customerId);
    }

    /// <summary>
    /// Reloads the cached set of valid issuer tenant IDs (home + active customers) from SQL.
    /// Call after onboarding/suspending a customer so new sign-ins are authorised immediately.
    /// </summary>
    public void RefreshValidTenants()
    {
        if (_dbFactory is null) return;

        using var db = _dbFactory.CreateDbContext();
        var tenants = db.Customers
            .Where(c => c.Status == "active")
            .Select(c => c.TenantId)
            .ToList();

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { HomeTenantId };
        foreach (var t in tenants)
            if (!string.IsNullOrWhiteSpace(t)) set.Add(t);

        _validTenantIds = set;
        _logger.LogInformation("Refreshed valid issuer tenants: {Count} active (incl. home).", set.Count);
    }
}

/// <summary>Progress snapshot for a bulk customer import, for driving a UI load bar.</summary>
public sealed record CustomerImportProgress(int Processed, int Total, int Imported, int Skipped);
