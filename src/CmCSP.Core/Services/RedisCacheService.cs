using System.Text.Json;
using Azure.Identity;
using Microsoft.Azure.StackExchangeRedis;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// <see cref="ICacheService"/> backed by Azure Managed Redis (Phase 4). Replaces the
/// Table/Blob routing of <see cref="AzureStorageCacheService"/> with a single shared
/// Redis tier that does TTL eviction natively (so no <c>CacheCleanupJob</c> is needed).
///
/// Tiers:
///   • L1 — <see cref="IMemoryCache"/> (per-replica, checked first).
///   • L2 — Azure Managed Redis (shared across replicas + jobs).
///
/// Authentication: DefaultAzureCredential via the Microsoft.Azure.StackExchangeRedis
/// extension — no access keys. The managed identity needs a Redis data-access policy.
/// If the connection cannot be established the service degrades to in-memory only
/// (<see cref="IsAzureEnabled"/> = false), mirroring the storage cache's failure mode.
/// </summary>
public sealed class RedisCacheService : ICacheService, IDisposable
{
    private readonly IMemoryCache _memory;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly IDatabase? _db;
    private readonly string _keyPrefix;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public RedisCacheService(
        IMemoryCache memory,
        CostManagementOptions options,
        ILogger<RedisCacheService> logger)
    {
        _memory = memory;
        _logger = logger;

        var cfg = options.Redis;
        _keyPrefix = cfg.KeyPrefix ?? string.Empty;

        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.HostName)) return;

        try
        {
            var configOptions = new ConfigurationOptions
            {
                EndPoints = { { cfg.HostName, cfg.Port } },
                Ssl = true,
                AbortOnConnectFail = false,
                ConnectRetry = 3,
                ConnectTimeout = 15_000
            };

            // Entra (managed identity) auth — no access keys.
            configOptions
                .ConfigureForAzureWithTokenCredentialAsync(new DefaultAzureCredential())
                .GetAwaiter().GetResult();

            _redis = ConnectionMultiplexer.Connect(configOptions);
            _db = _redis.GetDatabase();

            _logger.LogInformation(
                "RedisCacheService initialised. Host={Host}:{Port}", cfg.HostName, cfg.Port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "RedisCacheService: failed to connect to Azure Managed Redis. " +
                "Falling back to in-memory cache only.");
            _redis = null;
            _db = null;
        }
    }

    public bool IsAzureEnabled => _db is not null;

    private string Qualify(string key) => $"{_keyPrefix}{key}";

    public async ValueTask<(bool Found, T? Value)> TryGetValueAsync<T>(
        string key, TimeSpan memoryCacheTtl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_memory.TryGetValue(key, out T? memVal))
            return (true, memVal);

        if (_db is null) return (false, default);

        try
        {
            var payload = await _db.StringGetAsync(Qualify(key)).WaitAsync(ct);
            if (payload.IsNullOrEmpty) return (false, default);

            var value = CachePayloadCodec.Deserialize<T>((byte[])payload!, JsonOpts);
            if (value is not null) _memory.Set(key, value, memoryCacheTtl);

            return (value is not null, value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RedisCacheService: read failed for key {Key}.", key);
            return (false, default);
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _memory.Set(key, value, ttl);

        if (_db is null) return;

        try
        {
            var payload = CachePayloadCodec.Serialize(value, JsonOpts);
            await _db.StringSetAsync(Qualify(key), payload, ttl).WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RedisCacheService: write failed for key {Key}. In-memory entry retained.", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _memory.Remove(key);

        if (_db is null) return;
        try
        {
            await _db.KeyDeleteAsync(Qualify(key)).WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RedisCacheService: delete failed for key {Key}.", key);
        }
    }

    public void Dispose() => _redis?.Dispose();
}
