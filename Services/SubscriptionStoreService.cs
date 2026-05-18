using System.Text.Json;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Persists user-added subscription IDs to Data/subscriptions.json and merges them
/// into the live <see cref="CostManagementOptions.SubscriptionIds"/> list so that all
/// cost services pick them up without a restart.
///
/// Config-provided IDs (from appsettings / user-secrets / env vars) cannot be removed
/// at runtime; they are only tracked so the UI can distinguish them.
/// </summary>
public sealed class SubscriptionStoreService
{
    private readonly CostManagementOptions _options;
    private readonly string _storePath;
    private readonly ILogger<SubscriptionStoreService> _logger;

    // IDs present in config at startup — not removable at runtime
    private readonly HashSet<string> _configIds;

    // IDs added by the user via the UI — persisted to disk
    private readonly HashSet<string> _userIds = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _lock = new(1, 1);

    public event Action? OnChanged;

    public SubscriptionStoreService(
        CostManagementOptions options,
        IHostEnvironment env,
        ILogger<SubscriptionStoreService> logger)
    {
        _options   = options;
        _logger    = logger;
        // ContentRootPath (/app) is root-owned in the .NET SDK container image;
        // use the OS temp directory (/tmp on Linux) which is always writable.
        _storePath = Path.Combine(Path.GetTempPath(), "cmcsp-data", "subscriptions.json");

        // Snapshot the IDs that came from config so we can mark them as non-removable
        _configIds = new HashSet<string>(options.SubscriptionIds, StringComparer.OrdinalIgnoreCase);

        LoadFromDisk();
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
    public async Task<(int Added, List<string> Invalid)> AddAsync(IEnumerable<string> ids)
    {
        await _lock.WaitAsync();
        try
        {
            int added   = 0;
            var invalid = new List<string>();

            foreach (var raw in ids)
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
                    if (!_options.SubscriptionIds.Any(s => s.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                        _options.SubscriptionIds.Add(normalized);
                }
                // Already present (config or user) — not an error, just a no-op
            }

            if (added > 0)
            {
                await SaveAsync();
                OnChanged?.Invoke();
            }

            return (added, invalid);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes a user-added subscription ID. Config-provided IDs cannot be removed.
    /// </summary>
    public async Task RemoveAsync(string id)
    {
        var normalized = id.Trim().ToLowerInvariant();

        if (_configIds.Contains(normalized))
            return; // config IDs are not removable at runtime

        await _lock.WaitAsync();
        try
        {
            if (_userIds.Remove(normalized))
            {
                _options.SubscriptionIds.RemoveAll(s => s.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                await SaveAsync();
                OnChanged?.Invoke();
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
        try
        {
            var dir = Path.GetDirectoryName(_storePath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(
                _userIds.OrderBy(x => x).ToList(),
                new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(_storePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist subscription IDs to {Path}", _storePath);
        }
    }
}
