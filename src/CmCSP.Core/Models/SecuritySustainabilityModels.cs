namespace CmCSP.Models;

// ── Phase 8: Azure security posture & sustainability models ───────────────────
// All sourced from ARM with the existing management token:
//   • Microsoft.Security/secureScores(+Controls)  — Defender for Cloud secure score (Reader covers it).
//   • Microsoft.Carbon/carbonEmissionReports       — Carbon Optimization emissions (Reader covers it).

/// <summary>
/// Defender for Cloud secure score for one subscription (the ASC Default <c>ascScore</c> initiative).
/// <see cref="Percentage"/> is 0–100; <see cref="Current"/>/<see cref="Max"/> are raw points.
/// </summary>
public sealed record SecureScoreSummary(
    string SubscriptionId,
    string DisplayName,
    double Current,
    int Max,
    double Percentage);

/// <summary>
/// A single secure-score control (security recommendation group) with its healthy/unhealthy
/// resource counts. Surfaced as the "top findings" list — controls with
/// <see cref="Unhealthy"/> &gt; 0 ordered by <see cref="Weight"/>.
/// </summary>
public sealed record SecurityControlFinding(
    string SubscriptionId,
    string ControlName,
    int Healthy,
    int Unhealthy,
    int NotApplicable,
    double Percentage,
    long Weight)
{
    /// <summary>Total in-scope (applicable) resources for this control.</summary>
    public int Applicable => Healthy + Unhealthy;
}

/// <summary>
/// Overall carbon-emissions summary (kg CO₂e) for the latest available month, with the prior
/// month and month-over-month change ratio. Sourced from the Carbon OverallSummaryReport.
/// </summary>
public sealed record CarbonEmissionSummary(
    double LatestMonthEmissions,
    double PreviousMonthEmissions,
    double MonthOverMonthChangeRatio,
    string LatestMonthLabel)
{
    /// <summary>Month-over-month change as a percentage (e.g. 12.5 for +12.5%).</summary>
    public double MonthOverMonthChangePercent => Math.Round(MonthOverMonthChangeRatio * 100, 1);
}

/// <summary>
/// Carbon emissions (kg CO₂e) for a single month — one point on the sustainability trend chart.
/// </summary>
public sealed record CarbonEmissionMonth(
    DateOnly Month,
    double Emissions,
    double CarbonIntensity);

/// <summary>
/// Carbon emissions (kg CO₂e) attributed to one Azure resource type for the latest month, so the
/// UI can show which services drive the footprint. Sourced from the Carbon TopItemsSummaryReport.
/// </summary>
public sealed record CarbonEmissionByType(
    string ItemName,
    double LatestMonthEmissions,
    double PreviousMonthEmissions,
    double MonthOverMonthChangeRatio);
