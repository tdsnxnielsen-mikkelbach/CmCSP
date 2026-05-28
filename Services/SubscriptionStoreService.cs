using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Persists user-added subscription IDs to Key Vault (primary) and a local temp file
/// (fallback/backup) and merges them into the live <see cref="CostManagementOptions.SubscriptionIds"/>
/// list so that all cost services pick them up without a restart.
///
/// Config-provided IDs (from appsettings / user-secrets / env vars) cannot be removed
/// at runtime; they are only tracked so the UI can distinguish them.
/// </summary>
public sealed class SubscriptionStoreService
{
    private const string KvSecretName = "CmCSP--UserSubscriptionIds";

    private readonly CostManagementOptions _options;
    private readonly string _storePath;
    private readonly ILogger<SubscriptionStoreService> _logger;
    private readonly SecretClient? _kvClient;
    private readonly ExportProvisioningService? _provisioner;

    // IDs present in config at startup — not removable at runtime
    private readonly HashSet<string> _configIds;

    // IDs added by the user via the UI — persisted to disk and Key Vault
    private readonly HashSet<string> _userIds = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _lock = new(1, 1);

    public event Action<string>? OnChanged;

    public SubscriptionStoreService(
        CostManagementOptions options,
        IHostEnvironment env,
        IConfiguration configuration,
        ILogger<SubscriptionStoreService> logger,
        ExportProvisioningService? provisioner = null)
    {
        _options   = options;
        _logger    = logger;
        // ContentRootPath (/app) is root-owned in the .NET SDK container image;
        // use the OS temp directory (/tmp on Linux) which is always writable.
        _storePath = Path.Combine(Path.GetTempPath(), "cmcsp-data", "subscriptions.json");

        // Snapshot the IDs that came from config so we can mark them as non-removable
        _configIds = new HashSet<string>(options.SubscriptionIds, StringComparer.OrdinalIgnoreCase);

        var kvUri = configuration["KeyVaultUri"];
        if (!string.IsNullOrWhiteSpace(kvUri))
        {
            _kvClient = new SecretClient(new Uri(kvUri), new DefaultAzureCredential());
        }

        _provisioner = provisioner;

        // Load from disk first (fast, synchronous), then layer KV on top (KV wins on conflict).
        LoadFromDisk();
        LoadFromKeyVault();
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
    }
}
