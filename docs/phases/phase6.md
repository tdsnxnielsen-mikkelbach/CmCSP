# Phase 6 – Cost-insight enrichment (same ARM token)

> Part of the [CmCSP roadmap](../todo.md). **Status: ✅ Shipped.**

**Goal:** Squeeze more customer-visible value out of the APIs we already call — every item
below uses the **existing `https://management.azure.com/.default` token** and current RBAC
(Cost Management Reader), so there are **no new permissions or auth surface**. Scope stays
**Azure spend only**. These are the fastest wins.

| Sub-task | Priority | Status | Notes |
|---|---|---|---|
| **Native Cost Management forecast** | P1 | ✅ Shipped | `CostManagementService.GetForecastAsync` calls `POST .../providers/Microsoft.CostManagement/forecast` (api-version 2025-03-01, `includeActualCost=true`) and aggregates per-day across subscriptions. Surfaced in [`TrendAndForecast.razor`](../../src/Components/Pages/TrendAndForecast.razor) as a "Forecast (Microsoft)" line beside the linear extrapolation; the Microsoft month-end total drives the KPI when available, linear stays as fallback. (The current API exposes no confidence-band columns, so none are shown.) |
| **Azure Marketplace spend breakout** | P2 | ✅ Shipped | `CostManagementService.GetPublisherBreakdownAsync` groups MonthToDate spend by `PublisherType` + `MeterCategory` via the Query API. New [`Marketplace.razor`](../../src/Components/Pages/Marketplace.razor) page (`/marketplace`, in nav) shows Azure vs third-party KPIs, an Azure-vs-Marketplace donut, top marketplace services, and a detail grid. Azure spend only |
| **Cost anomaly / spike detection** | P2 | ✅ Shipped | Pure compute via `CostAnomalyDetector.Detect` over the loaded `CostRow` set — per (subscription, service, day) z-score vs a trailing 30-day baseline (z ≥ 2.5, recent days only). Surfaced as a "Cost Anomalies" panel on [`Home.razor`](../../src/Components/Pages/Home.razor). No new API call |
| **Reservation utilization trend** | P3 | ✅ Shipped | [`Reservations.razor`](../../src/Components/Pages/Reservations.razor) adds a 6-month utilization-% line chart, loaded separately (`_trendLoading`) so it doesn't block the month view. Reuses the existing Cost Details reservation fetch (per-month 4h cache) |
