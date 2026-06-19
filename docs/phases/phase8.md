# Phase 8 – Azure security & sustainability

> Part of the [CmCSP roadmap](../todo.md). **Status: ✅ Shipped.**

**Goal:** Round out the customer-facing report with **Azure security posture** and **carbon
emissions** — both strong reseller upsells. Azure-only; both feeds are covered by the existing
Phase 7 **Reader** grant (Security Reader / Carbon Optimization Reader are least-privilege
alternatives). **Advisor stays cost-only** (no expansion to other Advisor categories).

| Sub-task | Priority | Status | Notes |
|---|---|---|---|
| **Defender for Cloud Secure Score (Azure only)** | P1 | ✅ Shipped | `SecurityPostureService` reads `Microsoft.Security/secureScores` (ASC Default `ascScore` %) + `secureScoreControls` (per-control healthy/unhealthy counts → top findings) per subscription, api-version `2020-01-01`. New **Security Posture** page (`/security`): avg-score KPIs, per-sub secure-score bars, top-findings grid. **Azure security posture only — not M365 Secure Score.** Covered by Reader; Security Reader is the least-privilege alternative |
| **Carbon Optimization emissions** | P2 | ✅ Shipped | `SustainabilityService` posts `Microsoft.Carbon/carbonEmissionReports` (api-version `2025-04-01`) for Overall / Monthly / TopItems summary reports (scopes 1–3, kg CO₂e). New **Sustainability** page (`/sustainability`): latest-month + MoM KPIs, monthly emissions trend, emissions-by-resource-type chart. Auto-discovers the rolling ~12-month window from the service; degrades gracefully on missing access |

---

## What shipped

**New services** (both registered Singleton in `src/Program.cs`), using the existing `AzureMgmt`
HttpClient + `AzureTokenService` bearer token (scope `https://management.azure.com/.default`). All
reads are best-effort: on `403`/`404`/empty they log a warning and set `LastAccessDenied`, so the UI
shows a "needs role" banner and an empty state instead of an error. Results are memoised in-process
for `CacheExpirationMinutes` — deliberately *outside* the cost-cache contract in
[services-cache.instructions.md](../../.github/instructions/services-cache.instructions.md) (on-demand
read-only ARM reads, not the CSV-export → SQL → `ICacheService` cost pipeline).

- **`src/CmCSP.Core/Services/SecurityPostureService.cs`**
  - `GetSecureScoresAsync()` — per-subscription Defender for Cloud secure score (% + points).
  - `GetTopFindingsAsync()` — controls with unhealthy resources, ordered by weight then count.
- **`src/CmCSP.Core/Services/SustainabilityService.cs`**
  - `GetEmissionSummaryAsync()` — latest-month total + MoM change (OverallSummaryReport).
  - `GetMonthlyEmissionsAsync()` — per-month series for the trend chart (MonthlySummaryReport).
  - `GetEmissionsByTypeAsync()` — emissions by Azure resource type (TopItemsSummaryReport).
  - Discovers the API's rolling window from its own validation error and memoises it.

**New models — `src/CmCSP.Core/Models/SecuritySustainabilityModels.cs`:** `SecureScoreSummary`,
`SecurityControlFinding`, `CarbonEmissionSummary`, `CarbonEmissionMonth`, `CarbonEmissionByType`.

**UI:**
- New page `src/Components/Pages/Security.razor` (`/security`) + NavMenu link (Security icon).
- New page `src/Components/Pages/Sustainability.razor` (`/sustainability`) + NavMenu link
  (EnergySavingsLeaf icon).

## RBAC

Both feeds are **already covered by the Phase 7 `Reader` grant** — `Microsoft.Security/*/read` is part
of `*/read`, and a subscription **Reader** can view Carbon emissions data. So no new mandatory role
assignment is required.

For **least-privilege** deployments that prefer not to grant full Reader, `infra/main.bicep` adds an
optional, default-off `grantSecurityCarbonRolesOnSubscription` parameter that assigns:

- **Security Reader** (`39bc4728-0917-49c7-9d2c-d95423bc2eb4`) — secure score.
- **Carbon Optimization Reader** (`fa0d39e6-28e5-40cf-8521-1eb320653a4c`) — emissions.

to both managed identities on the deployment subscription. See
[docs/azure-roles.md](../azure-roles.md) § 4c. Carbon data covers a rolling ~12-month window with
roughly a one-month lag.
