# Phase 8 – Azure security & sustainability

> Part of the [CmCSP roadmap](../todo.md). **Status: 📋 Planned.**

**Goal:** Round out the customer-facing report with **Azure security posture** and **carbon
emissions** — both strong reseller upsells. Azure-only; needs Reader / Security Reader on the
targets. **Advisor stays cost-only** (no expansion to other Advisor categories).

| Sub-task | Priority | Status | Notes |
|---|---|---|---|
| **Defender for Cloud Secure Score (Azure only)** | P1 | 📋 Planned | `Microsoft.Security/secureScores` + `secureScoreControls` / `assessments` for the Azure secure-score % and top findings per subscription. Pairs with the existing Advisor (cost) page as a separate "Security" page. **Azure security posture only — not M365 Secure Score.** Needs **Security Reader** |
| **Carbon Optimization emissions** | P2 | 📋 Planned | `Microsoft.Carbon` (Carbon Optimization API) for emissions (scope 1/2/3 estimates) per subscription/service over time. New "Sustainability" page; increasingly requested in customer reports. Same managed-identity pattern; honor its own throttling/availability |
