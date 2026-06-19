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

Group larger efforts into phases so related items ship together.

| Phase | Theme | Target | Status |
|---|---|---|---|
| Phase 1 | azd migration & repo restructure | — | ✅ Shipped |
| Phase 2 | Export provisioning visibility | — | ✅ Shipped |
| Phase 3 | Externalise data collection to a scheduled + on-demand Container Apps Job | — | ✅ Shipped |
| Phase 4 | Storage & cache re-platform: Table/Blob → serverless SQL, in-process/Storage cache → Azure Managed Redis (Basic), all via managed identity | — | ✅ Shipped |
| Phase 5 | Performance & scaling optimization: activate Redis L2, warm the web tier, explicit autoscale rules, collector fan-out | — | ✅ Shipped |

---

## In Progress

| Item | Phase | Priority | Status | Owner | Notes |
|---|---|---|---|---|---|
| _No active items._ | — | — | — | — | — |

---

## Backlog

| Item | Phase | Priority | Status | Notes |
|---|---|---|---|---|
| _Add the next feature idea here_ | Phase 3 | P2 | 📋 Planned | — |

### Phase 3 – Data collection Container Apps Job

**Goal:** Move nightly cost data collection out of the web app into a dedicated
Container Apps **Job** (`cmcsp-collect`) that runs on a `Schedule` trigger at 02:00 UTC
and can also be started **on demand** from the UI. A Job (not a second Container App) is
the right primitive — collection is run-to-completion, isolated from web traffic, and
billed per execution. Mirrors the existing `cmcsp-collect` job pattern.

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
| Update `docs/cache-cleanup.md` (or a new doc) with the collection job design | P3 | ✅ Shipped | [`docs/data-collection.md`](data-collection.md) — schedule, triggers, RBAC, idempotency, audit table |

### Phase 4 – Storage & cache re-platform (serverless SQL + Azure Managed Redis)

**Goal:** Replace the bespoke three-layer hybrid cache and Azure Storage persistence with
standard managed services. Move durable data (cost rows, cache-cleanup state, collection
audit, user-added subscriptions) from **Azure Table + Blob Storage** to an **Azure SQL
Database (serverless tier)**, and move all caching from the in-process `IMemoryCache` +
Storage-backed `AzureStorageCacheService` to an **Azure Managed Redis (Basic SKU)** shared
across replicas and jobs. **Everything authenticates with managed identity** — no
connection strings or access keys in config or Key Vault. This refactor also unblocks the
collector fan-out (per-subscription cache keys become natural with Redis).

> **Status:** ✅ Shipped — SQL + Redis data platform implemented, MI-only, gated behind
> `deployDataPlatform` / `DEPLOY_DATA_PLATFORM`. Collector fan-out enabled via
> `COLLECT_PARTITION_COUNT`/`COLLECT_PARTITION_INDEX`.
>
> **Design doc:** [`docs/phase4-data-platform.md`](phase4-data-platform.md) — schema, MI auth,
> migrations, and the gated rollout sequence.

> **Finding (2026-06-17) — *no* service principal can write Cost Management exports.**
> Azure denies **all** service principals (Entra-app SPs *and* managed identities)
> *write* access to Cost Management exports even with the correct Cost Management
> Contributor role. Reads/list work; writes return `401 RBACAccessDenied`. Confirmed by
> three tests on `af701430`: Entra-app SP `PUT` → `401`; Container-App-style **managed
> identity** `PUT` → `401` (spike, 2026-06-18); identical `PUT` as the deployer **user**
> → `201`. As the supported flow, `infra/hooks/postprovision.ps1` creates exports for
> every configured subscription at deploy-time **as the deployer**. The runtime
> `ExportProvisioningService` SP path remains useful only for detect/reuse.

| Sub-task | Priority | Status | Notes |
|---|---|---|---|
| ~~Spike: can the Container App MI create Cost Management exports?~~ | P1 | ✅ Shipped | **Resolved 2026-06-18 — NO.** A managed identity holding Cost Management Contributor on `af701430` got the same `401 RBACAccessDenied` on `PUT` as the app-registration SP (tested via a throwaway `azure-cli` Container Apps job using its system-assigned MI + raw management token). Switching `CreateExportAsync` to MI auth would **not** help. Decision: keep the deploy-time-as-deployer path in `postprovision.ps1` as the supported provisioning flow; the runtime SP path is detect/reuse only |
| Design the SQL schema for cost rows + audit + cleanup state + subscription store | P1 | ✅ Shipped | `CmCSP.Data.CmcspDbContext` + entities (`CostFact`, `CollectionAudit`, `UserSubscription`, `AppSetting`); natural-key unique index on `CostFact` for upsert. Raw DDL mirror [`infra/sql/schema.sql`](../infra/sql/schema.sql); design [`docs/phase4-data-platform.md`](phase4-data-platform.md). Cleanup-state table dropped (CacheCleanupJob now retired) |
| Provision Azure SQL **serverless** DB in bicep (auto-pause, MI-only auth) | P1 | ✅ Shipped | [`infra/modules/data.bicep`](../infra/modules/data.bicep) — `GP_S_Gen5_2`, auto-pause 60 min, `minCapacity 0.5`; Entra-only auth (deployer as AD admin, `azureADOnlyAuthentication`). Gated behind `deployDataPlatform` in [`infra/main.bicep`](../infra/main.bicep) |
| Provision Azure **Managed Redis (Basic)** in bicep with Entra (MI) auth | P1 | ✅ Shipped | [`infra/modules/data.bicep`](../infra/modules/data.bicep) — `Balanced_B0` (no HA), `accessKeysAuthentication Disabled`, `VolatileLRU` eviction (native TTL → retires CacheCleanupJob); Entra `default` access-policy assignment per MI (Container App + collect job) |
| Introduce a data-access layer (EF Core or Dapper) for SQL persistence | P1 | ✅ Shipped | **EF Core** chosen (migrations + MI token auth via `Authentication=Active Directory Default`). `CmcspDbContext` added to `CmCSP.Core`; wired into DI in both [`src/Program.cs`](../src/Program.cs) and [`src/CostCollectorJob/Program.cs`](../src/CostCollectorJob/Program.cs) via `AddDbContextFactory<CmcspDbContext>` when `ConnectionStrings:Sql` is configured (set by the postprovision hook) |
| Postprovision hook: apply schema + create MI contained-DB users | P1 | ✅ Shipped | Phase 8 in [`infra/hooks/postprovision.ps1`](../infra/hooks/postprovision.ps1) (runs only when `DATA_PLATFORM_ENABLED=true`): applies [`infra/sql/schema.sql`](../infra/sql/schema.sql) and runs `CREATE USER ... FROM EXTERNAL PROVIDER` + `db_datareader`/`db_datawriter` for the Container App + collect job MIs, as the deployer (SQL Entra admin). Prefers `Invoke-Sqlcmd` w/ az token, falls back to go-sqlcmd `ActiveDirectoryAzCli` |
| One-time historical backfill into SQL | P2 | ✅ Shipped | Run-once [`CostBackfillJob`](../src/CostBackfillJob/Program.cs) + [`CostBackfillService`](../src/CmCSP.Core/Services/CostBackfillService.cs) read **every** export CSV (no 365-day window), aggregate into the `main`/`rg`/`tag` datasets and **upsert** into `CostFact` by natural key (latest export wins, idempotent re-runs) in 5k-row batches. Requires `DEPLOY_DATA_PLATFORM=true` (`ConnectionStrings:Sql`) |
| Replace `AzureStorageCacheService` with a Redis-backed `ICacheService` | P1 | ✅ Shipped | New [`ICacheService`](../src/CmCSP.Core/Services/ICacheService.cs) abstraction (same `TryGetValue`/`Set`/`Remove`/`IsAzureEnabled` surface); [`RedisCacheService`](../src/CmCSP.Core/Services/RedisCacheService.cs) uses `StackExchange.Redis` + `Microsoft.Azure.StackExchangeRedis` `DefaultAzureCredential` token auth (no keys), IMemoryCache L1, native TTL. `AzureStorageCacheService` also implements `ICacheService`; DI picks Redis when `AzureCostManagement:Redis:Enabled`, else Table/Blob (web app + collect job). Consumers depend on the interface |
| Migrate `CollectionAuditService` to SQL | P2 | ✅ Shipped | [`CollectionAuditService`](../src/CmCSP.Core/Services/CollectionAuditService.cs) writes/reads the `CollectionAudit` SQL table via the `IDbContextFactory<CmcspDbContext>` when SQL is configured; same `WriteAsync`/`GetRecentAsync`/`GetLatestAsync` surface for the UI. Falls back to the `cmcspcollectaudit` Table Storage table when SQL is absent |
| Migrate `SubscriptionStoreService` off Key Vault/temp-file to SQL | P2 | ✅ Shipped | [`SubscriptionStoreService`](../src/CmCSP.Core/Services/SubscriptionStoreService.cs) uses the `UserSubscription` SQL table as the single source of truth (reconcile on save) + `AppSetting` for the runtime `CostDetails.Enabled` flag when SQL is configured; KV + temp-file dual-write retained only as the non-SQL fallback |
| Retire `CacheCleanupJob` | P2 | ✅ Shipped | Removed the project, its `bicep/app.bicep` job + `bicep/main.bicep` storage RBAC, the `infra/main.bicep` wiring/output, and the `postdeploy`/`postprovision` hooks. Redis does TTL eviction natively and `CostFact` is durable (not pruned on a cadence), so the job is obsolete. Deleted `docs/cache-cleanup.md` |
| Update `BlobCostManagementService` consumption of exports | P2 | ✅ Shipped | Blob exports remain the **source feed**; parsed/aggregated rows now persist to SQL `CostFact` and read back from it. New [`ICostManagementService.RefreshAsync`](../src/CmCSP.Core/Services/ICostManagementService.cs) (write path: parse → `UpsertFactsAsync` by natural key in 5k batches → warm cache); read path `PopulateAllCachesAsync` loads from `CostFact` (`LoadFromSqlAsync`) with a one-off blob-parse fallback when empty. DI injects `IDbContextFactory<CmcspDbContext>` (optional ctor param) when SQL is configured; amortized data stays API-only. [`CostCollectorJob`](../src/CostCollectorJob/Program.cs) now calls `RefreshAsync` |
| Wire all MI role assignments + remove secrets from config/KV | P1 | ✅ Shipped | Already MI-only: SQL `azureADOnlyAuthentication` + contained-DB users; Redis `accessKeysAuthentication Disabled` + per-MI access policies; SQL conn string `Authentication=Active Directory Default` (no secret). [`postprovision.ps1`](../infra/hooks/postprovision.ps1) Phase 6 now **gates the storage Table/Blob cache off** when the data platform is on (`AzureCache__Enabled=false`; Redis takes over). No cache/storage connection-string secrets exist. The Entra `CmCSP--ClientSecret` is **retained** (OIDC sign-in + Query API fallback — not a cache secret) and documented as such |
| Update hooks + `appsettings` + cache instructions doc | P2 | ✅ Shipped | Added top-level `ConnectionStrings:Sql` to [`appsettings.json`](../src/appsettings.json) (Redis section already present); rewrote [`services-cache.instructions.md`](../.github/instructions/services-cache.instructions.md) for the Redis contract (L1 IMemoryCache + L2 Redis native-TTL MI auth, SQL `CostFact` durable store, `RefreshAsync` write path, `_fetchLock` herd protection, no cleanup job). Hook Phase 6 cache gating updated (see row above) |
| **Per-subscription partitioning for the collector (`parallelism > 1`)** | P3 | ✅ Shipped | [`CostCollectorJob`](../src/CostCollectorJob/Program.cs) honours `COLLECT_PARTITION_COUNT` / `COLLECT_PARTITION_INDEX` — each execution collects only `index % count` of the subscription set and restricts blob parsing via [`BlobCostManagementService.SubscriptionFilter`](../src/CmCSP.Core/Services/BlobCostManagementService.cs). Data-safe because `CostFact`'s natural key includes `SubscriptionId` (disjoint writes never conflict). Default `parallelism: 1`; [`infra/modules/app.bicep`](../infra/modules/app.bicep) documents fan-out via distinct scheduled executions (Container Apps Jobs have no native task index) |

### Phase 5 – Performance & scaling optimization

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
| **Wire Redis L2 into the running app + collector** | P1 | ✅ Shipped | Redis `cmcsp-redis-5eohwj` (`Balanced_B0`) was deployed but unused — live `cmcsp` had **no** `AzureCostManagement__Redis__*` env, so `ICacheService` resolved to `AzureStorageCacheService` with `IsAzureEnabled=false` (in-memory only, per replica). Set `Redis__Enabled=true` + `HostName` + `Port` on the web app **and** collect job (MI connects via `DefaultAzureCredential`); new revision `cmcsp--0000013` logged `RedisCacheService initialised. Host=cmcsp-redis-5eohwj...:10000`. Hardened [`postprovision.ps1`](../infra/hooks/postprovision.ps1) so Redis wiring is independent of SQL and logs a visible warning if `REDIS_HOST_NAME` is missing, so it can't silently sit unused again after `azd provision` |
| **Set web app `minReplicas: 1` (stop scale-to-zero)** | P1 | ✅ Shipped | This is **Blazor Server** — state lives in server-side SignalR circuits, so scale-to-zero dropped idle circuits and forced a cold container + SQL-serverless resume on the next visit. Changed [`infra/modules/app.bicep`](../infra/modules/app.bicep) default `minReplicas` 0 → 1 and applied live (`az containerapp update --min-replicas 1`) |
| **Add an explicit HTTP-concurrency scale rule + raise `maxReplicas`** | P2 | ✅ Shipped | `scale.rules` was `null` → default CPU-only autoscale, which a SignalR/IO-bound dashboard rarely triggers. Added an HTTP `concurrentRequests: 50` rule + raised `maxReplicas` 2 → 4 in [`infra/modules/app.bicep`](../infra/modules/app.bicep) (new param `scaleHttpConcurrentRequests`); applied live. Safe to scale out now that Redis L2 is shared, so extra replicas don't multiply SQL reads. Container Apps provides the session affinity Blazor Server needs |
| **Collector fan-out / dedicated background jobs** | P3 | ✅ Shipped | Partition-safe collection already exists (`COLLECT_PARTITION_COUNT`/`COLLECT_PARTITION_INDEX`; `CostFact` natural key includes `SubscriptionId`). Documented the fan-out runbook (start *N* distinct scheduled executions with fixed partition indexes) and the dedicated-`CostBackfillJob` guidance in [`docs/data-collection.md`](data-collection.md). Enable fan-out only as subscription count grows; default stays `parallelism: 1` |
| **SQL serverless autopause vs. latency review** | P3 | ✅ Shipped | Reviewed `autoPauseDelay 60 min` / `minCapacity 0.5`. With Redis L2 + a warm replica, SQL is rarely on the request path, so the resume penalty is acceptable. **Decision:** keep auto-pause at 60 min for cost; revisit only if cold-start latency becomes user-visible. Documented in [`docs/phase4-data-platform.md`](phase4-data-platform.md) |

---

## Shipped

| Item | Phase | Shipped | Notes |
|---|---|---|---|
| Migrate deployment from PowerShell scripts to `azd` | Phase 1 | 2026-06 | `azure.yaml` + `infra/` hooks; subscription & billing export scopes |
| Move application code under `src/` | Phase 1 | 2026-06 | Aligns with standard project layout |
| Always register ACR for system-identity pulls | Phase 1 | 2026-06 | Fixes `azd deploy` image pull `UNAUTHORIZED` |
| Show detected export provisioning path next to each subscription | Phase 2 | 2026-06 | Short status chip on Home page; read-only `DetectAsync` |
| Externalise data collection to a scheduled + on-demand Container Apps Job | Phase 3 | 2026-06 | `cmcsp-collect` job (cron `0 2 * * *` + on-demand); audit trail; Collect-now UI; `CacheWarmupService` repurposed as rehydrator. See [`docs/data-collection.md`](data-collection.md) |
| Performance & scaling optimization (Redis L2 wired, `minReplicas: 1` + HTTP scale rule, collector fan-out runbook, SQL auto-pause review) | Phase 5 | 2026-06 | Activated the deployed-but-unused Redis L2, warmed the Blazor Server web tier, added concurrency autoscaling. See Phase 5 backlog rows |

---

## Ideas / Parking lot

Unprioritised thoughts that may become backlog items later.

- _Capture rough ideas here before they're formally planned._
