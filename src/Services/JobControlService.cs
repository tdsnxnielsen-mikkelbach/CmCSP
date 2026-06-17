using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace CmCSP.Services;

/// <summary>
/// Starts the cost-collector Container Apps Job on demand (the dashboard "Collect now"
/// button) and reports its execution status, so users can refresh figures without
/// waiting for the nightly schedule.
///
/// Uses the Container App's managed identity (DefaultAzureCredential) to call the ARM
/// <c>Microsoft.App/jobs/start</c> action and to list job executions. The custom
/// "Collect Job Operator" role (assigned in bicep/app.bicep) scopes that identity to
/// exactly start + read on the collect job.
///
/// Concurrency: if an execution is already in progress, <see cref="StartOrCoalesceAsync"/>
/// coalesces onto it instead of starting another. A single replica refreshes the
/// aggregate datasets (cache keys span all subscriptions), so a second concurrent run
/// would only duplicate work.
///
/// Configuration (set in bicep as Container App env vars):
///   CollectorJob:SubscriptionId, CollectorJob:ResourceGroup, CollectorJob:JobName
/// </summary>
public sealed class JobControlService
{
    private const string ArmBase = "https://management.azure.com";
    private const string ApiVersion = "2024-03-01";
    private static readonly string[] InProgressStatuses = ["Running", "Processing", "Unknown"];

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<JobControlService> _logger;
    private readonly TokenCredential _credential = new DefaultAzureCredential();

    private readonly string? _subscriptionId;
    private readonly string? _resourceGroup;
    private readonly string? _jobName;

    public JobControlService(
        IConfiguration configuration,
        IHttpClientFactory httpFactory,
        ILogger<JobControlService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _subscriptionId = configuration["CollectorJob:SubscriptionId"];
        _resourceGroup = configuration["CollectorJob:ResourceGroup"];
        _jobName = configuration["CollectorJob:JobName"];
    }

    /// <summary><c>true</c> when the collect-job coordinates are configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_subscriptionId) &&
        !string.IsNullOrWhiteSpace(_resourceGroup) &&
        !string.IsNullOrWhiteSpace(_jobName);

    private string JobUri =>
        $"{ArmBase}/subscriptions/{_subscriptionId}/resourceGroups/{_resourceGroup}" +
        $"/providers/Microsoft.App/jobs/{_jobName}";

    /// <summary>Azure portal deep link to the collect job's execution history (raw logs).</summary>
    public string? PortalExecutionHistoryUrl => IsConfigured
        ? $"https://portal.azure.com/#@/resource/subscriptions/{_subscriptionId}/resourceGroups/{_resourceGroup}" +
          $"/providers/Microsoft.App/jobs/{_jobName}/executionHistory"
        : null;

    /// <summary>
    /// Starts a collector run, or returns the in-progress run when one already exists.
    /// </summary>
    public async Task<JobRunStatus> StartOrCoalesceAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return JobRunStatus.NotConfigured;

        // Coalesce: don't start a second run if one is already executing.
        var running = await GetLatestAsync(ct);
        if (running is { IsInProgress: true })
        {
            _logger.LogInformation("JobControl: coalescing onto in-progress execution {Execution}.", running.ExecutionName);
            return running with { Coalesced = true };
        }

        var client = _httpFactory.CreateClient("AzureMgmt");
        await AuthorizeAsync(client, ct);

        using var resp = await client.PostAsync($"{JobUri}/start?api-version={ApiVersion}", content: null, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("JobControl: failed to start collect job ({Status}): {Body}", (int)resp.StatusCode, body);
            return new JobRunStatus { Status = "StartFailed", IsInProgress = false };
        }

        // The start action returns the new execution; fall back to a fresh list if not.
        try
        {
            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("name", out var nameEl))
            {
                return new JobRunStatus
                {
                    ExecutionName = nameEl.GetString(),
                    Status = "Running",
                    IsInProgress = true,
                    StartTimeUtc = DateTimeOffset.UtcNow
                };
            }
        }
        catch (JsonException)
        {
            // No JSON body (202 Accepted) — query the executions list below.
        }

        return await GetLatestAsync(ct) ?? new JobRunStatus { Status = "Running", IsInProgress = true };
    }

    /// <summary>Returns the most recent execution's status, or null if there are none.</summary>
    public async Task<JobRunStatus?> GetLatestAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        var client = _httpFactory.CreateClient("AzureMgmt");
        await AuthorizeAsync(client, ct);

        using var resp = await client.GetAsync($"{JobUri}/executions?api-version={ApiVersion}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("JobControl: failed to list executions ({Status}): {Body}", (int)resp.StatusCode, body);
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("value", out var values) || values.GetArrayLength() == 0)
            return null;

        JobRunStatus? latest = null;
        foreach (var item in values.EnumerateArray())
        {
            var status = Map(item);
            if (latest is null || (status.StartTimeUtc ?? DateTimeOffset.MinValue) > (latest.StartTimeUtc ?? DateTimeOffset.MinValue))
                latest = status;
        }
        return latest;
    }

    private async Task AuthorizeAsync(HttpClient client, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static JobRunStatus Map(JsonElement execution)
    {
        var name = execution.TryGetProperty("name", out var n) ? n.GetString() : null;
        string status = "Unknown";
        DateTimeOffset? start = null;

        if (execution.TryGetProperty("properties", out var props))
        {
            if (props.TryGetProperty("status", out var s)) status = s.GetString() ?? "Unknown";
            if (props.TryGetProperty("startTime", out var st) && st.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(st.GetString(), out var parsed))
                start = parsed;
        }

        return new JobRunStatus
        {
            ExecutionName = name,
            Status = status,
            StartTimeUtc = start,
            IsInProgress = InProgressStatuses.Contains(status, StringComparer.OrdinalIgnoreCase)
        };
    }
}

/// <summary>Snapshot of a collector job execution surfaced to the dashboard.</summary>
public sealed record JobRunStatus
{
    public string? ExecutionName { get; init; }
    public string Status { get; init; } = "Unknown";
    public DateTimeOffset? StartTimeUtc { get; init; }
    public bool IsInProgress { get; init; }

    /// <summary>True when a start request joined an already-running execution.</summary>
    public bool Coalesced { get; init; }

    public static JobRunStatus NotConfigured { get; } = new() { Status = "NotConfigured" };
}
