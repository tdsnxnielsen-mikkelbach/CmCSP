# CmCSP – Roadmap & Development Backlog

A living backlog of features, improvements, and technical work for CmCSP. Use it to plan
phases, track in-progress work, and record what has shipped.

## How to use this document

- Add new items under **Backlog** with a status of `📋 Planned`.
- Move items to **In Progress** when work starts (`🚧 In Progress`).
- Move items to **Shipped** once deployed/merged (`✅ Shipped`) and fill in the date.
- Keep each item to a single row; link to a design note or issue for detail when needed.

### Status legend

| Status | Meaning |
|---|---|
| 📋 Planned | Agreed/likely, not yet started |
| 🚧 In Progress | Actively being worked on |
| 🔬 Spike | Exploratory / proof-of-concept |
| ⏸️ Blocked | Waiting on a dependency or decision |
| ✅ Shipped | Merged and deployed |
| ❌ Dropped | Decided against (keep for history) |

### Priority legend

`P0` critical · `P1` high · `P2` normal · `P3` nice-to-have

---

## Phases

Group larger efforts into phases so related items ship together. Each phase has its own
detail page (goal + sub-task breakdown) under [`docs/phases/`](phases/).

| Phase | Theme | Target | Status | Detail |
|---|---|---|---|---|
| Phase 1 | azd migration & repo restructure | — | ✅ Shipped | _(see Shipped)_ |
| Phase 2 | Export provisioning visibility | — | ✅ Shipped | _(see Shipped)_ |
| Phase 3 | Externalise data collection to a scheduled + on-demand Container Apps Job | — | ✅ Shipped | [phase3.md](phases/phase3.md) |
| Phase 4 | Storage & cache re-platform: Table/Blob → serverless SQL, in-process/Storage cache → Azure Managed Redis (Basic), all via managed identity | — | ✅ Shipped | [phase4.md](phases/phase4.md) |
| Phase 5 | Performance & scaling optimization: activate Redis L2, warm the web tier, explicit autoscale rules, collector fan-out | — | ✅ Shipped | [phase5.md](phases/phase5.md) |
| Phase 6 | Cost-insight enrichment (same ARM token): native forecast, Marketplace spend split, anomaly detection, reservation utilization trend | — | ✅ Shipped | [phase6.md](phases/phase6.md) |
| Phase 7 | Azure inventory & optimization: Resource Graph enrichment, orphaned-resource finder, reservation/savings-plan purchase recommendations | — | ✅ Shipped | [phase7.md](phases/phase7.md) |
| Phase 8 | Azure security & sustainability: Defender for Cloud Secure Score (Azure only), Carbon Optimization emissions | — | ✅ Shipped | [phase8.md](phases/phase8.md) |
| Phase 9 | CSP multi-tenancy: customer→tenant→subscription model, per-tenant tokens (GDAP + multi-tenant Entra app), tenant-isolated cache/data | — | ✅ Shipped | [phase9.md](phases/phase9.md) |

---

## In Progress

| Item | Phase | Priority | Status | Owner | Notes |
|---|---|---|---|---|---|
| Data model: customer → tenant → subscriptions | Phase 9 | P1 | ✅ Shipped | — | `CustomerEntity`/`CustomerSubscriptionEntity` + `CostFact` tenant columns + index; `schema.sql` idempotent + bootstrap home customer; gated by `MultiTenancy` flag |
| Multi-tenant sign-in + tenant-scope resolver | Phase 9 | P1 | ✅ Shipped | — | `CustomerStore` registry + issuer-validated multi-tenant OIDC (gated); `ITenantScopeProvider` resolves `tid`→customer scope |
| Tenant-isolated cache + SQL reads (P0 boundary) | Phase 9 | P0 | ✅ Shipped | — | `TenantScope.CacheKeyPrefix` + `TenantScopeAccessor` (ambient); `BlobCostManagementService` scopes SQL reads (`WHERE CustomerId IN`), prefixes cache keys, stamps writes; `CostPageBase` publishes scope; API-mode `CostManagementService` keys prefixed via `Scoped()`; `CacheWarmupService` warms per-customer partitions |
| Per-tenant ARM token acquisition | Phase 9 | P1 | ✅ Shipped | — | `AzureTokenService` reads ambient scope → client-secret mode acquires tokens with `.WithTenantId(customerTenantId)` (MSAL per-tenant cache); MI mode stays home-tenant (can't cross tenants) |
| Per-tenant collector fan-out | Phase 9 | P2 | ✅ Shipped | — | `CostCollectorJob` iterates active customers partitioned by `COLLECT_PARTITION_INDEX/COUNT`, sets ambient scope per slice (per-tenant token + scoped cache/SQL), accumulates audit totals; single-tenant unchanged |
| Scale-out collection (large estates) | Phase 9 | P2 | ✅ Shipped | — | `JobControlService.StartScaledAsync` POSTs N `jobs/start` executions with per-partition `COLLECT_PARTITION_*` overrides (no native task index); count from `CollectorJob:PartitionCount`, wired via `collectorPartitionCount` in bicep |
| Customer picker + onboarding UI | Phase 9 | P1 | ✅ Shipped | — | Partner-only `/customers` admin page (onboard/suspend/map subs) via `CustomerStore` write methods; gated partner picker in `MainLayout` + nav link drive `SelectedCustomerId`; `CostPageBase` narrows partner scope to one customer |
| GDAP delegated-access onboarding | Phase 9 | P1 | ✅ Shipped | — | `GdapOnboardingService`: per-customer admin-consent link + ARM subscription auto-discovery (per-tenant GDAP token) auto-maps subscriptions, replacing manual GUID entry; GDAP relationship id stored on the customer. Partner Center API stays out of scope (relationship created in the portal) |

---

## Backlog

Planned phases, newest first. Open the detail page for each phase's goal and sub-task table.

| Phase | Theme | Priority | Status | Detail |
|---|---|---|---|---|
| Phase 9 | CSP multi-tenancy | P0 | ✅ Shipped | [phase9.md](phases/phase9.md) |

---

## Shipped

| Item | Phase | Shipped | Detail |
|---|---|---|---|
| Migrate deployment from PowerShell scripts to `azd` | Phase 1 | 2026-06 | `azure.yaml` + `infra/` hooks; subscription & billing export scopes |
| Move application code under `src/` | Phase 1 | 2026-06 | Aligns with standard project layout |
| Always register ACR for system-identity pulls | Phase 1 | 2026-06 | Fixes `azd deploy` image pull `UNAUTHORIZED` |
| Show detected export provisioning path next to each subscription | Phase 2 | 2026-06 | Short status chip on Home page; read-only `DetectAsync` |
| Data collection Container Apps Job | Phase 3 | 2026-06 | `cmcsp-collect` job (cron `0 2 * * *` + on-demand); audit trail; Collect-now UI. [phase3.md](phases/phase3.md) · [data-collection.md](data-collection.md) |
| Storage & cache re-platform (serverless SQL + Managed Redis) | Phase 4 | 2026-06 | MI-only SQL `CostFact` durable store + shared Redis L2 cache. [phase4.md](phases/phase4.md) · [phase4-data-platform.md](phase4-data-platform.md) |
| Performance & scaling optimization | Phase 5 | 2026-06 | Redis L2 wired, `minReplicas: 1` + HTTP scale rule, collector fan-out runbook, SQL auto-pause review. [phase5.md](phases/phase5.md) |
| Cost-insight enrichment (same ARM token) | Phase 6 | 2026-06 | Native Microsoft forecast in Trend page, `/marketplace` first-party-vs-third-party split, anomaly panel on Home, 6-month reservation utilization trend — all on the existing ARM token. [phase6.md](phases/phase6.md) |
| Azure inventory & optimization | Phase 7 | 2026-06 | `OptimizationService` (Resource Graph inventory + orphaned/idle finder, Consumption purchase recommendations, Capacity reservation expiry); new `/optimization` page, live tag-coverage on Tag Chargeback, recommendations + expiry on Reservations; Reader RBAC via `infra/main.bicep` + `infra/modules/reader-sub.bicep`. [phase7.md](phases/phase7.md) |
| Azure security & sustainability | Phase 8 | 2026-06 | `SecurityPostureService` (Defender for Cloud secure score + top control findings) on new `/security` page; `SustainabilityService` (Carbon Optimization emissions — overall/monthly/by-type, kg CO₂e) on new `/sustainability` page; both covered by the Phase 7 Reader grant, with optional Security Reader + Carbon Optimization Reader least-privilege roles in `infra/main.bicep`. [phase8.md](phases/phase8.md) |

---

## Ideas / Parking lot

Unprioritised thoughts that may become backlog items later.

- _Capture rough ideas here before they're formally planned._

<!-- Phase detail pages live in docs/phases/. Keep this file as the index only. -->
