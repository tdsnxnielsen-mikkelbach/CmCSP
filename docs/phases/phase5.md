# Phase 5 – Performance & scaling optimization

> Part of the [CmCSP roadmap](../todo.md). **Status: ✅ Shipped (2026-06-19).**

**Goal:** Now that serverless SQL + Azure Managed Redis are deployed, turn them into real
performance wins and make the web tier scale predictably. Review (2026-06-19) of the live
deployment found Redis provisioned but **not wired** (the app ran in-memory-only), the web
Container App scaling to zero with no explicit scale rules, and headroom to fan the
collector out. These items activate what's already paid for and improve tail latency.

> **Status:** ✅ Shipped (2026-06-19). Redis L2 wired live + in the hook, web app warmed
> (`minReplicas: 1`) with an HTTP-concurrency scale rule, collector fan-out runbook
> documented, and the SQL auto-pause trade-off reviewed and accepted.

| Sub-task | Priority | Status | Notes |
|---|---|---|---|
| **Wire Redis L2 into the running app + collector** | P1 | ✅ Shipped | Redis `cmcsp-redis-5eohwj` (`Balanced_B0`) was deployed but unused — live `cmcsp` had **no** `AzureCostManagement__Redis__*` env, so `ICacheService` resolved to `AzureStorageCacheService` with `IsAzureEnabled=false` (in-memory only, per replica). Set `Redis__Enabled=true` + `HostName` + `Port` on the web app **and** collect job (MI connects via `DefaultAzureCredential`); new revision `cmcsp--0000013` logged `RedisCacheService initialised. Host=cmcsp-redis-5eohwj...:10000`. Hardened [`postprovision.ps1`](../../infra/hooks/postprovision.ps1) so Redis wiring is independent of SQL and logs a visible warning if `REDIS_HOST_NAME` is missing, so it can't silently sit unused again after `azd provision` |
| **Set web app `minReplicas: 1` (stop scale-to-zero)** | P1 | ✅ Shipped | This is **Blazor Server** — state lives in server-side SignalR circuits, so scale-to-zero dropped idle circuits and forced a cold container + SQL-serverless resume on the next visit. Changed [`infra/modules/app.bicep`](../../infra/modules/app.bicep) default `minReplicas` 0 → 1 and applied live (`az containerapp update --min-replicas 1`) |
| **Add an explicit HTTP-concurrency scale rule + raise `maxReplicas`** | P2 | ✅ Shipped | `scale.rules` was `null` → default CPU-only autoscale, which a SignalR/IO-bound dashboard rarely triggers. Added an HTTP `concurrentRequests: 50` rule + raised `maxReplicas` 2 → 4 in [`infra/modules/app.bicep`](../../infra/modules/app.bicep) (new param `scaleHttpConcurrentRequests`); applied live. Safe to scale out now that Redis L2 is shared, so extra replicas don't multiply SQL reads. Container Apps provides the session affinity Blazor Server needs |
| **Collector fan-out / dedicated background jobs** | P3 | ✅ Shipped | Partition-safe collection already exists (`COLLECT_PARTITION_COUNT`/`COLLECT_PARTITION_INDEX`; `CostFact` natural key includes `SubscriptionId`). Documented the fan-out runbook (start *N* distinct scheduled executions with fixed partition indexes) and the dedicated-`CostBackfillJob` guidance in [`docs/data-collection.md`](../data-collection.md). Enable fan-out only as subscription count grows; default stays `parallelism: 1` |
| **SQL serverless autopause vs. latency review** | P3 | ✅ Shipped | Reviewed `autoPauseDelay 60 min` / `minCapacity 0.5`. With Redis L2 + a warm replica, SQL is rarely on the request path, so the resume penalty is acceptable. **Decision:** keep auto-pause at 60 min for cost; revisit only if cold-start latency becomes user-visible. Documented in [`docs/phase4-data-platform.md`](../phase4-data-platform.md) |
