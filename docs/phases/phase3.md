# Phase 3 – Data collection Container Apps Job

> Part of the [CmCSP roadmap](../todo.md). **Status: ✅ Shipped.**

**Goal:** Move nightly cost data collection out of the web app into a dedicated
Container Apps **Job** (`cmcsp-collect`) that runs on a `Schedule` trigger at 02:00 UTC
and can also be started **on demand** from the UI. A Job (not a second Container App) is
the right primitive — collection is run-to-completion, isolated from web traffic, and
billed per execution. Mirrors the existing `cmcsp-collect` job pattern.

See also: [`docs/data-collection.md`](../data-collection.md) — schedule, triggers, RBAC,
idempotency, audit table, and the collector fan-out runbook.

| Sub-task | Priority | Status | Notes |
|---|---|---|---|
| Create `CostCollectorJob` console project that reuses `CostManagementService` + cache services | P1 | ✅ Shipped | Extracted shared `CmCSP.Core` lib; job reuses the exact cost + cache pipeline (cache keys/TTLs/60 KB routing identical) |
| Collect all four datasets **sequentially** (`cm_main`, `cm_rg`, `cm_tag`, `cm_main_amort`) | P1 | ✅ Shipped | `InvalidateCache()` then the four `Get*` methods; Query-API fallback honours the 5-req/min limit, blob-export path has none |
| Add `Microsoft.App/jobs` resource `cmcsp-collect` in `bicep/app.bicep` | P1 | ✅ Shipped | `Schedule` trigger `0 2 * * *`; System+UserAssigned identity; KV Secrets User + storage RBAC (`main.bicep`) |
| Build/push/update `cmcsp-collect` image in `infra/hooks/postdeploy.ps1` | P1 | ✅ Shipped | Hook builds the collect job image, sharing the ACR token + docker-config setup (cleanup job since retired) |
| Wire collect job env vars in `infra/hooks/postprovision.ps1` | P1 | ✅ Shipped | Same cost + cache env as the Container App; `client-secret` defined in bicep |
| Retire `DailyApiRefreshService` from the web app | P1 | ✅ Shipped | Hosted service + file removed; the job now owns nightly refresh |
| Decide fate of `CacheWarmupService` | P2 | ✅ Shipped | Repurposed as a **rehydrator-only** service: repopulates in-memory cache from persistent Table/Blob storage on startup; no longer issues live API calls (collection owned by `CostCollectorJob`) |
| Grant Container App MI permission to start the job | P1 | ✅ Shipped | Custom "Collect Job Operator" role (start + executions read) scoped to the job |
| Add “Collect now” UI button → ARM `jobs/.../start` via MI | P2 | ✅ Shipped | `JobControlService` starts + polls; Home.razor "Data Collection" panel with bounded polling |
| Guard against concurrent executions | P2 | ✅ Shipped | `StartOrCoalesceAsync` coalesces onto an in-flight execution; parallelism=1 (aggregate datasets) |
| Surface last-run status / success in UI | P2 | ✅ Shipped | `CollectionAuditService` writes/reads an audit row (status/counts/trigger/duration) in Table Storage; UI shows it + portal log link |
| Update `docs/cache-cleanup.md` (or a new doc) with the collection job design | P3 | ✅ Shipped | [`docs/data-collection.md`](../data-collection.md) — schedule, triggers, RBAC, idempotency, audit table |
