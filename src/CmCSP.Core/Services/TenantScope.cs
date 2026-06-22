namespace CmCSP.Services;

/// <summary>
/// Phase 9: the resolved set of customers a request is allowed to read. Every cost/posture
/// query is funnelled through this so a customer can never reach another tenant's data.
///
/// Lives in CmCSP.Core (rather than the web project) so the singleton cost services can
/// consume it for cache-key prefixing and SQL scoping. The web-only resolver that produces
/// it from the signed-in principal is <c>TenantScopeProvider</c>.
/// </summary>
public sealed record TenantScope
{
    /// <summary>
    /// <c>true</c> when no tenant filtering applies — the legacy single-tenant path
    /// (<c>MultiTenancy:Enabled = false</c>). Consumers must not add a <c>CustomerId</c>
    /// filter in this mode; behaviour is identical to the pre-Phase-9 app.
    /// </summary>
    public bool IsUnscoped { get; init; }

    /// <summary>The signed-in tenant is the CSP home tenant (the partner) — sees all customers.</summary>
    public bool IsPartner { get; init; }

    /// <summary>The sign-in came from an unregistered/suspended tenant — read nothing.</summary>
    public bool IsDenied { get; init; }

    /// <summary>
    /// The customer IDs this request may read. For the partner this is every active customer;
    /// for a customer it is the single owning customer. Empty when denied. Ignored when
    /// <see cref="IsUnscoped"/> is <c>true</c>.
    /// </summary>
    public IReadOnlyList<long> CustomerIds { get; init; } = [];

    /// <summary>The signed-in tenant GUID (<c>tid</c> claim), when known.</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>Legacy single-tenant scope — no filtering.</summary>
    public static TenantScope Unscoped { get; } = new() { IsUnscoped = true };

    /// <summary>Access denied — an unknown or suspended tenant.</summary>
    public static TenantScope Denied { get; } = new() { IsDenied = true };

    /// <summary>
    /// Cache-key namespace for this scope so tenants never share cached payloads. Empty in the
    /// single-tenant (unscoped) path — keys are exactly as before. Otherwise a per-customer (or
    /// partner-aggregate) prefix that partitions the shared L2 (Redis) and per-replica L1.
    /// </summary>
    public string CacheKeyPrefix =>
        IsUnscoped              ? string.Empty
        : IsDenied              ? "mt_denied:"
        : IsPartner             ? "mt_partner:"
        : CustomerIds.Count == 1 ? $"mt_c{CustomerIds[0]}:"
        :                          "mt_denied:";

    /// <summary>
    /// The cache-key prefix for a single customer's partition (e.g. <c>mt_c42:</c>). Used by the
    /// warmup service to rehydrate each active customer's datasets without constructing a full
    /// <see cref="TenantScope"/>. Kept in sync with <see cref="CacheKeyPrefix"/>.
    /// </summary>
    public static string CustomerCacheKeyPrefix(long customerId) => $"mt_c{customerId}:";

    /// <summary>
    /// The cache-key prefix for the partner-aggregate (all-customers) partition. Kept in sync with
    /// <see cref="CacheKeyPrefix"/> so the warmup service and collector can address it directly.
    /// </summary>
    public const string PartnerCacheKeyPrefix = "mt_partner:";
}

/// <summary>
/// Phase 9: ambient holder of the current request's <see cref="TenantScope"/>, so the singleton
/// cost services can scope their cache keys and SQL reads without taking a per-call parameter.
///
/// The scope is published once per circuit by the page base class (which runs in the same async
/// context as the subsequent cost-service calls), and flows down to those calls via
/// <see cref="AsyncLocal{T}"/>. Background work that never sets it (e.g. the cache-warmup service
/// and the collector) reads <see cref="TenantScope.Unscoped"/> by default — identical to the
/// pre-Phase-9 behaviour.
/// </summary>
public sealed class TenantScopeAccessor
{
    private readonly AsyncLocal<TenantScope?> _current = new();

    /// <summary>The current scope, or <see cref="TenantScope.Unscoped"/> when none was published.</summary>
    public TenantScope Current
    {
        get => _current.Value ?? TenantScope.Unscoped;
        set => _current.Value = value;
    }
}
