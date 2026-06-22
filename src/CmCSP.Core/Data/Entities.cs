namespace CmCSP.Data;

/// <summary>
/// One aggregated cost row persisted in SQL — the durable replacement for the
/// Table/Blob cache of parsed cost rows. One row per
/// (Dataset, UsageDate, SubscriptionId, ServiceName, ResourceGroupName, Tag, Currency).
/// </summary>
public sealed class CostFact
{
    public long Id { get; set; }

    /// <summary>Which dataset this row belongs to: <c>main</c>, <c>rg</c>, <c>tag</c>, or <c>main_amort</c>.</summary>
    public string Dataset { get; set; } = string.Empty;

    /// <summary>UTC date the cost was incurred (granularity = Daily).</summary>
    public DateOnly UsageDate { get; set; }

    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;

    /// <summary>Service dimension — populated for <c>main</c> / <c>main_amort</c>, empty otherwise.</summary>
    public string ServiceName { get; set; } = string.Empty;
    /// <summary>Resource-group dimension — populated for <c>rg</c>, empty otherwise.</summary>
    public string ResourceGroupName { get; set; } = string.Empty;

    /// <summary>Tag dimension — populated for <c>tag</c>, empty otherwise.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Original cost in the subscription's billing currency.</summary>
    public decimal Cost { get; set; }

    /// <summary>ISO 4217 billing currency returned by the API (e.g. "USD").</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Cost converted to the configured TargetCurrency.</summary>
    public decimal NormalizedCost { get; set; }

    /// <summary>
    /// Phase 9: owning customer (FK → <see cref="Customer"/>), denormalised onto the fact for
    /// fast per-customer filtered reads. The natural key is unchanged — a subscription belongs
    /// to exactly one customer, so this is carried for query scoping, not added to the key.
    /// In single-tenant deployments this is the bootstrap "home" customer.
    /// </summary>
    public long CustomerId { get; set; }

    /// <summary>
    /// Phase 9: the owning customer's Entra tenant GUID — redundant with <see cref="Customer"/>
    /// but indexed for direct tenant scoping during authorization.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>
/// One row of the cost-collection audit trail (durable replacement for the
/// <c>cmcspcollectaudit</c> Table Storage table). Written by the CostCollectorJob.
/// </summary>
public sealed class CollectionAuditEntity
{
    public long Id { get; set; }

    public string Status { get; set; } = "Unknown";
    public string Trigger { get; set; } = "manual";

    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }
    public long DurationMs { get; set; }

    public int SubscriptionCount { get; set; }
    public int MainRows { get; set; }
    public int RgRows { get; set; }
    public int TagRows { get; set; }
    public int AmortRows { get; set; }

    public string? Error { get; set; }
    public string? ReplicaName { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}

/// <summary>
/// A subscription ID added at runtime through the UI — the durable replacement for the
/// Key Vault secret + temp-file store. Config-provided IDs are not stored here.
/// </summary>
public sealed class UserSubscriptionEntity
{
    public string SubscriptionId { get; set; } = string.Empty;
    public DateTimeOffset AddedUtc { get; set; }
}

/// <summary>
/// A simple key/value application setting persisted in SQL (e.g. the runtime
/// <c>CostDetails.Enabled</c> flag), replacing one-off Key Vault flag secrets.
/// </summary>
public sealed class AppSettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>
/// Phase 9 (CSP multi-tenancy): a reseller's customer — one Entra tenant the partner has
/// delegated (GDAP) access into. Existing single-tenant data maps to a single bootstrap
/// "home" customer during migration, so nothing is orphaned.
/// </summary>
public sealed class CustomerEntity
{
    public long Id { get; set; }

    /// <summary>The customer's Entra tenant GUID — the sign-in <c>tid</c> claim and token authority.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Friendly customer name shown in the partner's customer picker.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary><c>active</c> or <c>suspended</c> — gates sign-in and collection.</summary>
    public string Status { get; set; } = "active";

    /// <summary>The GDAP relationship granting delegated access (nullable until established).</summary>
    public string? GdapRelationshipId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>
/// Phase 9: which subscriptions belong to a customer. Generalises the flat
/// <see cref="UserSubscriptionEntity"/> list into a tenant-scoped mapping.
/// </summary>
public sealed class CustomerSubscriptionEntity
{
    public long Id { get; set; }

    /// <summary>Owning customer (FK → <see cref="CustomerEntity"/>).</summary>
    public long CustomerId { get; set; }

    /// <summary>Azure subscription GUID.</summary>
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>Cached subscription display name.</summary>
    public string SubscriptionName { get; set; } = string.Empty;

    public DateTimeOffset AddedUtc { get; set; }
}
