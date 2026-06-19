using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using CmCSP.Data;
using CmCSP.Models;
using Microsoft.EntityFrameworkCore;

namespace CmCSP.Services;

/// <summary>
/// Persists user-added subscription IDs and merges them into the live
/// <see cref="CostManagementOptions.SubscriptionIds"/> list so that all cost services pick
/// them up without a restart.
///
/// Phase 4: when a <see cref="CmcspDbContext"/> factory is registered (i.e. the SQL data
/// platform is provisioned), the <c>UserSubscription</c> SQL table is the single source of
/// truth and the runtime <c>CostDetails.Enabled</c> flag is stored in the <c>AppSetting</c>
/// table. When SQL is not configured the service falls back to the legacy Key Vault secret
/// (primary) + local temp file (backup) so existing deployments keep working unchanged.
///
/// Config-provided IDs (from appsettings / user-secrets / env vars) cannot be removed
/// at runtime; they are only tracked so the UI can distinguish them.
/// </summary>
public sealed class SubscriptionStoreService
{
    private const string KvSecretName                  = "CmCSP--UserSubscriptionIds";
    private const string KvCostDetailsEnabledSecretName = "CmCSP--CostDetails--Enabled";
    private const string CostDetailsEnabledSettingKey   = "CostDetails.Enabled";

    private readonly CostManagementOptions _options;
    private readonly string _storePath;
    private readonly ILogger<SubscriptionStoreService> _logger;
    private readonly SecretClient? _kvClient;
    private readonly ExportProvisioningService? _provisioner;
    private readonly IDbContextFactory<CmcspDbContext>? _dbFactory;

    // IDs present in config at startup — not removable at runtime
    private readonly HashSet<string> _configIds;

    // IDs added by the user via the UI — persisted to SQL (or Key Vault/disk when SQL absent)
    private readonly HashSet<string> _userIds = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _lock = new(1, 1);

    public event Action<string>? OnChanged;

    public SubscriptionStoreService(
        CostManagementOptions options,
        IHostEnvironment env,
        IConfiguration configuration,
        ILogger<SubscriptionStoreService> logger,
        ExportProvisioningService? provisioner = null,
        IDbContextFactory<CmcspDbContext>? dbFactory = null)
    {
        _options   = options;
        _logger    = logger;
        _dbFactory = dbFactory;
        // ContentRootPath (/app) is root-owned in the .NET SDK container image;
        // use the OS temp directory (/tmp on Linux) which is always writable.
        _storePath = Path.Combine(Path.GetTempPath(), "cmcsp-data", "subscriptions.json");

        // Snapshot the IDs that came from config so we can mark them as non-removable
        _configIds = new HashSet<string>(options.SubscriptionIds, StringComparer.OrdinalIgnoreCase);

        // Key Vault is only used for the legacy fallback path (no SQL data platform).
        var kvUri = configuration["KeyVaultUri"];
        if (_dbFactory is null && !string.IsNullOrWhiteSpace(kvUri))
        {
            _kvClient = new SecretClient(new Uri(kvUri), new DefaultAzureCredential());
        }

        _provisioner = provisioner;

        if (_dbFactory is not null)
        {
            // SQL is the single source of truth.
            LoadFromSql();
        }
        else
        {
            // Load from disk first (fast, synchronous), then layer KV on top (KV wins on conflict).
            LoadFromDisk();
            LoadFromKeyVault();
        }
    }

    // ── Public read surfaces ─────────────────────────────────────────────────

    /// <summary>IDs that came from appsettings / secrets / env vars (read-only).</summary>
    public IReadOnlyList<string> ConfiguredIds => _configIds.ToList();

    /// <summary>IDs added at runtime via the UI.</summary>
    public IReadOnlyList<string> UserAddedIds => _userIds.ToList();

    /// <summary>All active subscription IDs (config + user-added).</summary>
    public IReadOnlyList<string> AllIds => _options.SubscriptionIds.AsReadOnly();

    // ── Mutations ────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables the Cost Details API at runtime and persists the setting to Key Vault
    /// so it survives container restarts.
    /// </summary>
    public async Task EnableCostDetailsAsync()
    {
        _options.CostDetails.Enabled = true;

        if (_dbFactory is not null)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var setting = await db.AppSettings.FindAsync(CostDetailsEnabledSettingKey);
                if (setting is null)
                    db.AppSettings.Add(new AppSettingEntity { Key = CostDetailsEnabledSettingKey, Value = "true", UpdatedUtc = DateTimeOffset.UtcNow });
                else
                {
                    setting.Value = "true";
                    setting.UpdatedUtc = DateTimeOffset.UtcNow;
                }
                await db.SaveChangesAsync();
                _logger.LogInformation("Cost Details API enabled and persisted to SQL.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist CostDetails.Enabled to SQL.");
            }
            return;
        }

        if (_kvClient is null)
            return;

        try
        {
            await _kvClient.SetSecretAsync(KvCostDetailsEnabledSecretName, "true");
            _logger.LogInformation("Cost Details API enabled and persisted to Key Vault.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist CostDetails.Enabled to Key Vault.");
        }
    }

    /// <summary>
    /// Adds one or more subscription IDs. Each entry is validated as a GUID.
    /// Returns the count of newly-added IDs and a list of any that failed validation.
    /// </summary>
    public async Task<(int Added, List<string> Invalid)> AddAsync(
        IEnumerable<string> ids,
        string? correlationId = null)
    {
        correlationId ??= Guid.NewGuid().ToString("N");

        int added          = 0;
        var invalid        = new List<string>();
        var newlyAdded     = new List<string>();
        var inputList      = ids.ToList();

        await _lock.WaitAsync();
        try
        {
            foreach (var raw in inputList)
            {
                var trimmed = raw.Trim();
                if (!Guid.TryParse(trimmed, out _))
                {
                    invalid.Add(trimmed);
                    continue;
                }

                var normalized = trimmed.ToLowerInvariant();
                if (_userIds.Add(normalized))
                {
                    added++;
                    newlyAdded.Add(normalized);
                    if (!_options.SubscriptionIds.Any(s => s.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                        _options.SubscriptionIds.Add(normalized);
                }
                // Already present (config or user) — not an error, just a no-op
            }

            if (added > 0)
            {
                await SaveAsync();
                _logger.LogInformation(
                    "SubscriptionStore[{CorrelationId}]: added {Added} subscription(s). Active total now {Total}.",
                    correlationId, added, _options.SubscriptionIds.Count);
                OnChanged?.Invoke(correlationId);
            }
            else
            {
                _logger.LogInformation(
                    "SubscriptionStore[{CorrelationId}]: no new subscriptions added (input entries: {InputCount}, invalid: {InvalidCount}).",
                    correlationId, inputList.Count, invalid.Count);
            }
        }
        finally
        {
            _lock.Release();
        }

        // Fire-and-forget export provisioning — runs after the lock is released so
        // the subscription is immediately active for Query API while the export is created.
        if (_provisioner is not null)
            foreach (var id in newlyAdded)
                _ = Task.Run(async () =>
                {
                    try   { await _provisioner.ProvisionAsync(id, correlationId); }
                    catch (Exception ex) { _logger.LogError(ex, "SubscriptionStore[{CorrelationId}]: export provisioning failed for {SubId}", correlationId, id); }
                });

        return (added, invalid);
    }

    /// <summary>
    /// Removes a user-added subscription ID. Config-provided IDs cannot be removed.
    /// </summary>
    public async Task RemoveAsync(string id, string? correlationId = null)
    {
        correlationId ??= Guid.NewGuid().ToString("N");
        var normalized = id.Trim().ToLowerInvariant();

        if (_configIds.Contains(normalized))
        {
            _logger.LogInformation(
                "SubscriptionStore[{CorrelationId}]: skipped removing configured subscription {SubId} (non-removable at runtime).",
                correlationId, normalized);
            return; // config IDs are not removable at runtime
        }

        await _lock.WaitAsync();
        try
        {
            if (_userIds.Remove(normalized))
            {
                _options.SubscriptionIds.RemoveAll(s => s.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                await SaveAsync();
                _logger.LogInformation(
                    "SubscriptionStore[{CorrelationId}]: removed subscription {SubId}. Active total now {Total}.",
                    correlationId, normalized, _options.SubscriptionIds.Count);
                OnChanged?.Invoke(correlationId);
            }
            else
            {
                _logger.LogInformation(
                    "SubscriptionStore[{CorrelationId}]: remove requested for {SubId} but it was not present in user-managed list.",
                    correlationId, normalized);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Disk persistence ─────────────────────────────────────────────────────

    private void LoadFromDisk()
    {
        if (!File.Exists(_storePath))
            return;

        try
        {
            var json = File.ReadAllText(_storePath);
            var ids  = JsonSerializer.Deserialize<List<string>>(json) ?? [];

            foreach (var raw in ids)
            {
                var trimmed    = raw.Trim();
                var normalized = trimmed.ToLowerInvariant();

                if (!Guid.TryParse(normalized, out _))
                    continue;

                _userIds.Add(normalized);

                if (!_options.SubscriptionIds.Any(s => s.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                    _options.SubscriptionIds.Add(normalized);
            }

            _logger.LogInformation("Loaded {Count} user-added subscription IDs from {Path}", _userIds.Count, _storePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load subscriptions from {Path} — starting with config IDs only", _storePath);
        }
    }

    private async Task SaveAsync()
    {
        // SQL is the single source of truth when the data platform is provisioned.
        if (_dbFactory is not null)
        {
            await SaveToSqlAsync();
            return;
        }

        var json = JsonSerializer.Serialize(
            _userIds.OrderBy(x => x).ToList(),
            new JsonSerializerOptions { WriteIndented = true });

        // Primary: Key Vault (survives container restarts and scale-out)
        if (_kvClient is not null)
        {
            try
            {
                await _kvClient.SetSecretAsync(KvSecretName, json);
                _logger.LogDebug("Persisted {Count} subscription IDs to Key Vault", _userIds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist subscription IDs to Key Vault secret '{Secret}'", KvSecretName);
            }
        }

        // Backup: local temp file
        try
        {
            var dir = Path.GetDirectoryName(_storePath)!;
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_storePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist subscription IDs to {Path}", _storePath);
        }
    }

    // ── SQL persistence (Phase 4 system of record) ───────────────────────────

    private void LoadFromSql()
    {
        try
        {
            using var db = _dbFactory!.CreateDbContext();

            foreach (var row in db.UserSubscriptions.AsNoTracking().ToList())
            {
                var normalized = row.SubscriptionId.Trim().ToLowerInvariant();
                if (!Guid.TryParse(normalized, out _))
                    continue;

                _userIds.Add(normalized);
                if (!_options.SubscriptionIds.Any(s => s.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                    _options.SubscriptionIds.Add(normalized);
            }

            _logger.LogInformation("Loaded {Count} user-added subscription IDs from SQL", _userIds.Count);

            // Restore the persisted CostDetails.Enabled flag.
            var setting = db.AppSettings.AsNoTracking().FirstOrDefault(s => s.Key == CostDetailsEnabledSettingKey);
            if (setting is not null && bool.TryParse(setting.Value, out var enabled) && enabled)
            {
                _options.CostDetails.Enabled = true;
                _logger.LogInformation("CostDetails.Enabled restored to true from SQL.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load subscription state from SQL — starting with config IDs only");
        }
    }

    /// <summary>Reconciles the <c>UserSubscription</c> table to match the in-memory user set.</summary>
    private async Task SaveToSqlAsync()
    {
        try
        {
            await using var db = await _dbFactory!.CreateDbContextAsync();

            var existing = await db.UserSubscriptions.ToListAsync();
            var existingIds = existing.Select(e => e.SubscriptionId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Add new IDs.
            foreach (var id in _userIds)
                if (!existingIds.Contains(id))
                    db.UserSubscriptions.Add(new UserSubscriptionEntity { SubscriptionId = id, AddedUtc = DateTimeOffset.UtcNow });

            // Remove IDs no longer present in the user set.
            foreach (var row in existing)
                if (!_userIds.Contains(row.SubscriptionId))
                    db.UserSubscriptions.Remove(row);

            await db.SaveChangesAsync();
            _logger.LogDebug("Persisted {Count} subscription IDs to SQL", _userIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist subscription IDs to SQL");
        }
    }

    // ── Key Vault load ────────────────────────────────────────────────────────

    private void LoadFromKeyVault()
    {
        if (_kvClient is null)
            return;

        try
        {
            var response = _kvClient.GetSecret(KvSecretName);
            var ids = JsonSerializer.Deserialize<List<string>>(response.Value.Value) ?? [];

            int merged = 0;
            foreach (var raw in ids)
            {
                var normalized = raw.Trim().ToLowerInvariant();
                if (!Guid.TryParse(normalized, out _))
                    continue;

                if (_userIds.Add(normalized))
                {
                    merged++;
                    if (!_options.SubscriptionIds.Any(s => s.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                        _options.SubscriptionIds.Add(normalized);
                }
            }

            _logger.LogInformation("Loaded {Count} subscription IDs from Key Vault ({Merged} new)", ids.Count, merged);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Secret doesn't exist yet — first run or KV was reset; disk data (if any) will be saved on next mutation.
            _logger.LogDebug("Key Vault secret '{Secret}' not found — starting from disk state", KvSecretName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load subscription IDs from Key Vault — using disk state only");
        }

        // Apply persisted CostDetails.Enabled flag (written by EnableCostDetailsAsync).
        try
        {
            var enabledResponse = _kvClient.GetSecret(KvCostDetailsEnabledSecretName);
            if (bool.TryParse(enabledResponse.Value.Value, out var enabled) && enabled)
            {
                _options.CostDetails.Enabled = true;
                _logger.LogInformation("CostDetails.Enabled restored to true from Key Vault.");
            }
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Not yet set — nothing to restore.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read CostDetails.Enabled from Key Vault.");
        }
    }
}
