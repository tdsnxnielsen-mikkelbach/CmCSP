using System.Text.Json;

namespace CmCSP.Models;

// ─── API response shapes ───────────────────────────────────────────────────────

public sealed record CostApiResponse(CostApiProperties? Properties);

public sealed record CostApiProperties(
    List<CostApiColumn> Columns,
    List<List<JsonElement>> Rows,
    string? NextLink);

public sealed record CostApiColumn(string Name, string Type);

// ─── Budget API response shapes ────────────────────────────────────────────────────────
// GET /subscriptions/{id}/providers/Microsoft.Consumption/budgets

public sealed record BudgetListResponse(List<BudgetResource>? Value, string? NextLink);
public sealed record BudgetResource(string Name, BudgetResourceProperties? Properties);
public sealed record BudgetResourceProperties(
    decimal Amount,
    string TimeGrain,
    BudgetCurrentSpend? CurrentSpend);
public sealed record BudgetCurrentSpend(decimal Amount, string Unit);

// ─── Advisor Recommendations API response shapes ───────────────────────────────
// GET /subscriptions/{id}/providers/Microsoft.Advisor/recommendations?$filter=Category eq 'Cost'

public sealed record AdvisorListResponse(List<AdvisorResource>? Value, string? NextLink);
public sealed record AdvisorResource(string? Name, AdvisorProperties? Properties);
public sealed record AdvisorProperties(
    string? Category,
    string? Impact,
    string? ImpactedField,
    string? ImpactedValue,
    AdvisorShortDescription? ShortDescription,
    Dictionary<string, string>? ExtendedProperties,
    AdvisorResourceMetadata? ResourceMetadata);
public sealed record AdvisorShortDescription(string? Problem, string? Solution);
public sealed record AdvisorResourceMetadata(string? ResourceId);

// ─── Advisor Score API response shapes ────────────────────────────────────────
// GET /subscriptions/{id}/providers/Microsoft.Advisor/advisorScore

public sealed record AdvisorScoreListResponse(List<AdvisorScoreResource>? Value);
public sealed record AdvisorScoreResource(string? Name, AdvisorScoreProperties? Properties);
public sealed record AdvisorScoreProperties(AdvisorScoreDetail? Score);
public sealed record AdvisorScoreDetail(double? Current, double? ConsumptionUnits);

// ─── Subscriptions API response shapes ────────────────────────────────────────
// GET /subscriptions/{id}?api-version=2022-12-01

public sealed record SubscriptionInfoResponse(string? DisplayName);
