using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly int     _partitionCount;

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
        _partitionCount = int.TryParse(configuration["CollectorJob:PartitionCount"], out var pc)
            ? Math.Clamp(pc, 1, 20)
            : 1;
    }

    /// <summary><c>true</c> when the collect-job coordinates are configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_subscriptionId) &&
        !string.IsNullOrWhiteSpace(_resourceGroup) &&
        !string.IsNullOrWhiteSpace(_jobName);

    /// <summary>
    /// The number of parallel collector executions a single "Collect now" fans out to
    /// (<c>CollectorJob:PartitionCount</c>, default 1). Each execution handles a disjoint slice via
    /// <c>COLLECT_PARTITION_INDEX</c>/<c>COLLECT_PARTITION_COUNT</c> — used to scale collection
    /// across many customers/subscriptions for larger CSP estates.
    /// </summary>
    public int DefaultPartitionCount => _partitionCount;

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

    /// <summary>
    /// Starts <paramref name="partitions"/> parallel collector executions, each handling a disjoint
    /// slice of the work via <c>COLLECT_PARTITION_INDEX</c>/<c>COLLECT_PARTITION_COUNT</c>. Used to
    /// scale collection across many customers/subscriptions for larger CSP estates. With
    /// <paramref name="partitions"/> &lt;= 1 this is equivalent to <see cref="StartOrCoalesceAsync"/>
    /// (coalescing onto any in-flight run).
    /// </summary>
    public async Task<JobRunStatus> StartScaledAsync(int partitions, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return JobRunStatus.NotConfigured;

        partitions = Math.Clamp(partitions, 1, 20);
        if (partitions == 1)
            return await StartOrCoalesceAsync(ct);

        // Read the job's container template once so each partition override preserves the image,
        // resources and existing env (including secretRefs) and only adds the partition vars.
        var container = await GetContainerTemplateAsync(ct);
        if (container is null)
        {
            _logger.LogWarning("JobControl: could not read the job template for a scaled start — falling back to one execution.");
            return await StartOrCoalesceAsync(ct);
        }

        var client = _httpFactory.CreateClient("AzureMgmt");
        await AuthorizeAsync(client, ct);

        string? firstExecution = null;
        var started = 0;
        for (var i = 0; i < partitions; i++)
        {
            var body = BuildStartOverride(container, i, partitions);
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync($"{JobUri}/start?api-version={ApiVersion}", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError("JobControl: scaled start partition {Index}/{Count} failed ({Status}): {Body}",
                    i, partitions, (int)resp.StatusCode, errBody);
                continue;
            }

            started++;
            try
            {
                var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (firstExecution is null && doc.RootElement.TryGetProperty("name", out var nameEl))
                    firstExecution = nameEl.GetString();
            }
            catch (JsonException) { /* 202 with no body — execution name surfaces via the list */ }
        }

        _logger.LogInformation("JobControl: scaled start launched {Started}/{Requested} partition execution(s).",
            started, partitions);

        return new JobRunStatus
        {
            ExecutionName = firstExecution,
            Status        = started == 0 ? "StartFailed" : "Running",
            IsInProgress  = started > 0,
            StartTimeUtc  = DateTimeOffset.UtcNow
        };
    }

    // Reads the collect job's first container template (image/resources/env) as a mutable JSON
    // object, so a scaled start can override only the partition env without losing other settings.
    private async Task<JsonObject?> GetContainerTemplateAsync(CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("AzureMgmt");
        await AuthorizeAsync(client, ct);

        using var resp = await client.GetAsync($"{JobUri}?api-version={ApiVersion}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("JobControl: failed to read job template ({Status}): {Body}", (int)resp.StatusCode, body);
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        var container = JsonNode.Parse(json)?["properties"]?["template"]?["containers"]?.AsArray()?.FirstOrDefault()?.AsObject();
        // Deep-clone so the returned node has no parent and can be reused per partition.
        return container is null ? null : JsonNode.Parse(container.ToJsonString())!.AsObject();
    }

    // Builds the jobs/start body (a JobExecutionTemplate) for one partition: the container template
    // with COLLECT_PARTITION_INDEX/COUNT + COLLECT_TRIGGER=manual upserted into its env.
    private static JsonObject BuildStartOverride(JsonObject containerTemplate, int index, int count)
    {
        var container = JsonNode.Parse(containerTemplate.ToJsonString())!.AsObject();

        var env = container["env"]?.AsArray();
        if (env is null)
        {
            env = new JsonArray();
            container["env"] = env;
        }

        void Upsert(string name, string value)
        {
            foreach (var item in env)
            {
                if (item is JsonObject o && (string?)o["name"] == name)
                {
                    o["value"] = value;
                    o.Remove("secretRef");
                    return;
                }
            }
            env.Add(new JsonObject { ["name"] = name, ["value"] = value });
        }

        Upsert("COLLECT_PARTITION_COUNT", count.ToString());
        Upsert("COLLECT_PARTITION_INDEX", index.ToString());
        Upsert("COLLECT_TRIGGER", "manual");

        return new JsonObject { ["containers"] = new JsonArray { container } };
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
