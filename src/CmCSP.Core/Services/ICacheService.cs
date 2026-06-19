namespace CmCSP.Services;

/// <summary>
/// Shared cache abstraction over the in-process + distributed cache tiers.
///
/// Two implementations exist:
///   • <see cref="AzureStorageCacheService"/> — IMemoryCache + Azure Table/Blob (legacy).
///   • <see cref="RedisCacheService"/> — IMemoryCache + Azure Managed Redis (Phase 4).
///
/// Consumers depend on this interface so the backing store can be swapped via DI/config
/// without code changes. The method surface mirrors the original concrete service so the
/// migration is behaviour-preserving.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// True when a shared (cross-replica) tier is configured and reachable. False means the
    /// service is degraded to in-memory only. <see cref="CacheWarmupService"/> uses this to
    /// decide whether a persistent rehydrate is worthwhile.
    /// </summary>
    bool IsAzureEnabled { get; }

    /// <summary>
    /// Try to get a value. Checks in-memory first, then the shared tier. On a shared-tier hit
    /// the in-memory entry is re-populated with <paramref name="memoryCacheTtl"/>.
    /// </summary>
    bool TryGetValue<T>(string key, TimeSpan memoryCacheTtl, out T? value);

    /// <summary>Stores a value in both the in-memory and shared tiers with the given TTL.</summary>
    void Set<T>(string key, T value, TimeSpan ttl);

    /// <summary>Removes the key from both the in-memory and shared tiers.</summary>
    void Remove(string key);
}
