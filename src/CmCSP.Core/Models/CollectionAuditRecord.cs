namespace CmCSP.Models;

/// <summary>
/// One row of the cost-collection audit trail, written by the CostCollectorJob at the
/// end of every run (scheduled or on-demand) and read by the dashboard to show the
/// last-run status. Persisted to Azure Table Storage by <c>CollectionAuditService</c>.
/// </summary>
public sealed class CollectionAuditRecord
{
    /// <summary>Outcome of the run: "Success" or "Failed".</summary>
    public string Status { get; set; } = "Unknown";

    /// <summary>What started the run: "schedule" (nightly cron) or "manual" (UI button).</summary>
    public string Trigger { get; set; } = "manual";

    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }
    public long DurationMs { get; set; }

    /// <summary>Number of subscriptions in scope for this run.</summary>
    public int SubscriptionCount { get; set; }

    public int MainRows { get; set; }
    public int RgRows { get; set; }
    public int TagRows { get; set; }
    public int AmortRows { get; set; }

    /// <summary>Error detail when <see cref="Status"/> is "Failed"; otherwise null.</summary>
    public string? Error { get; set; }

    /// <summary>Container Apps replica that ran the job (for cross-referencing logs).</summary>
    public string? ReplicaName { get; set; }

    /// <summary>Correlation id that ties this audit row to the job's log lines.</summary>
    public string CorrelationId { get; set; } = string.Empty;
}
