using System.Text.Json;

namespace CmCSP.Models;

// ─── API response shapes ───────────────────────────────────────────────────────

public sealed record CostApiResponse(CostApiProperties? Properties);

public sealed record CostApiProperties(
    List<CostApiColumn> Columns,
    List<List<JsonElement>> Rows,
    string? NextLink);

public sealed record CostApiColumn(string Name, string Type);
