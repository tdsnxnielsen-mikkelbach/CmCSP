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

public sealed record BudgetListResponse(List<BudgetResource>? Value);
public sealed record BudgetResource(string Name, BudgetResourceProperties? Properties);
public sealed record BudgetResourceProperties(
    decimal Amount,
    string TimeGrain,
    BudgetCurrentSpend? CurrentSpend);
public sealed record BudgetCurrentSpend(decimal Amount, string Unit);
