using CmCSP.Models;

namespace CmCSP.Services;

public static class CostDataAggregation
{
    public static List<SubscriptionDailyCost> BySubscriptionDay(IEnumerable<CostRow> rows) =>
        rows.GroupBy(r => new
            {
                Date = r.Date.Date,
                SubscriptionId = r.SubscriptionId.ToUpperInvariant(),
                r.CustomerId
            })
            .Select(g =>
            {
                var name = g.Select(r => r.SubscriptionName)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                var tenantId = g.Select(r => r.TenantId)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                var first = g.First();
                return new SubscriptionDailyCost(
                    DateTime.SpecifyKind(g.Key.Date, first.Date.Kind),
                    first.SubscriptionId,
                    name,
                    g.Key.CustomerId,
                    tenantId,
                    g.Sum(r => r.NormalizedCost));
            })
            .ToList();
}
