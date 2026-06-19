# Phase 6 – Cost-insight enrichment (same ARM token)

> Part of the [CmCSP roadmap](../todo.md). **Status: 📋 Planned.**

**Goal:** Squeeze more customer-visible value out of the APIs we already call — every item
below uses the **existing `https://management.azure.com/.default` token** and current RBAC
(Cost Management Reader), so there are **no new permissions or auth surface**. Scope stays
**Azure spend only**. These are the fastest wins.

| Sub-task | Priority | Status | Notes |
|---|---|---|---|
| **Native Cost Management forecast** | P1 | 📋 Planned | Call `POST .../providers/Microsoft.CostManagement/forecast` for Microsoft's own forecast (with confidence bands) and surface it in [`TrendAndForecast.razor`](../../src/Components/Pages/TrendAndForecast.razor) alongside (or replacing) the current linear extrapolation. More credible than a self-computed trend; same token + `CostManagementService` pattern |
| **Azure Marketplace spend breakout** | P2 | 📋 Planned | Split out third-party **Azure Marketplace** charges (`ChargeType eq 'Marketplace'`) via the Cost Details API / export rows as its own KPI + chart series. Still Azure spend (not M365). Helps resellers show ISV/SaaS-on-Azure cost separately |
| **Cost anomaly / spike detection** | P2 | 📋 Planned | Pure compute on data already in SQL `CostFact` — no new API call. Compute day-over-day / week-over-week deltas per subscription + service, flag statistically significant spikes, and surface an "anomalies" panel on Home. Anchor windows to wall-clock like the forecast fix |
| **Reservation utilization trend** | P3 | 📋 Planned | Today [`Reservations.razor`](../../src/Components/Pages/Reservations.razor) shows point-in-time used/unused. Add `Microsoft.Consumption/reservationSummaries` (+ `reservationDetails`) for utilization-% over time and underutilized-RI flags. Same token; 60-min cache like budgets/advisor |
