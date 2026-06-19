using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Detects day-over-day cost spikes from cost rows already held in the durable store / cache.
/// Pure in-process computation — issues no API calls. For each subscription + service it builds
/// a daily total series and flags recent days whose cost is a statistical outlier (high z-score)
/// versus a trailing baseline window.
/// </summary>
public static class CostAnomalyDetector
{
    /// <summary>
    /// Scans the most recent <paramref name="lookbackDays"/> days of data for spikes.
    /// </summary>
    /// <param name="rows">Daily cost rows grouped by service (the <c>cm_main</c> dataset).</param>
    /// <param name="resolveSubName">Optional subscription-ID → display-name resolver.</param>
    /// <param name="trailingDays">Size of the baseline window preceding each evaluated day.</param>
    /// <param name="zThreshold">Minimum z-score for a day to count as an anomaly.</param>
    /// <param name="minDailyCost">Ignore series/days below this cost to suppress noise on tiny meters.</param>
    /// <param name="lookbackDays">How many of the most recent data days to evaluate.</param>
    public static IReadOnlyList<CostAnomaly> Detect(
        IEnumerable<CostRow> rows,
        Func<string, string>? resolveSubName = null,
        int trailingDays = 30,
        double zThreshold = 2.5,
        decimal minDailyCost = 50m,
        int lookbackDays = 3)
    {
        // Aggregate to one cost value per (subscription, service, day).
        var byDay = rows
            .Where(r => r.Date != default)
            .GroupBy(r => (
                r.SubscriptionId,
                Service: string.IsNullOrWhiteSpace(r.ServiceName) ? "(unassigned)" : r.ServiceName,
                Day: r.Date.Date))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.NormalizedCost));

        if (byDay.Count == 0) return [];

        var latest = byDay.Keys.Max(k => k.Day);
        var evalCutoff = latest.AddDays(-(lookbackDays - 1));

        var nameBySub = rows
            .GroupBy(r => r.SubscriptionId)
            .ToDictionary(g => g.Key, g => g.First().SubscriptionName, StringComparer.OrdinalIgnoreCase);

        var anomalies = new List<CostAnomaly>();

        foreach (var series in byDay
            .GroupBy(kv => (kv.Key.SubscriptionId, kv.Key.Service)))
        {
            var costByDay = series.ToDictionary(kv => kv.Key.Day, kv => kv.Value);

            foreach (var (day, cost) in costByDay
                .Where(kv => kv.Key >= evalCutoff)
                .OrderBy(kv => kv.Key))
            {
                if (cost < minDailyCost) continue;

                // Baseline: the trailingDays calendar days immediately before this day that have data.
                var windowStart = day.AddDays(-trailingDays);
                var baseline = costByDay
                    .Where(kv => kv.Key >= windowStart && kv.Key < day)
                    .Select(kv => kv.Value)
                    .ToList();

                if (baseline.Count < 7) continue; // too little history to judge

                var mean = baseline.Average();
                if (mean <= 0) continue;

                var variance = baseline.Sum(c => (double)((c - mean) * (c - mean))) / baseline.Count;
                var stdDev = Math.Sqrt(variance);
                if (stdDev <= 0) continue;

                var z = (double)(cost - mean) / stdDev;
                if (z < zThreshold || cost <= mean) continue;

                var (subId, service) = series.Key;
                var subName = resolveSubName?.Invoke(subId)
                    ?? (nameBySub.TryGetValue(subId, out var n) && !string.IsNullOrWhiteSpace(n) ? n : subId);

                anomalies.Add(new CostAnomaly(
                    SubscriptionId:   subId,
                    SubscriptionName: subName,
                    ServiceName:      service,
                    Date:             day,
                    Cost:             cost,
                    Baseline:         Math.Round(mean, 2),
                    DeltaPct:         mean > 0 ? Math.Round((cost - mean) / mean * 100m, 1) : 0m,
                    ZScore:           Math.Round(z, 2)));
            }
        }

        return anomalies
            .OrderByDescending(a => a.ZScore)
            .ThenByDescending(a => a.Cost - a.Baseline)
            .ToList();
    }
}
