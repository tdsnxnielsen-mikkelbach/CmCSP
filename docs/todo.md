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
| Phase 4 | Storage & cache re-platform: Table/Blob → serverless SQL, in-process/Storage cache → Azure Managed Redis (Basic), all via managed identity | — | 📋 Planned |

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
billed per execution. Mirrors the existing `cmcsp-cleanup` pattern.

| Sub-task | Priority | Status | Notes |
|---|---|---|---|
| Create `CostCollectorJob` console project that reuses `CostManagementService` + cache services | P1 | ✅ Shipped | Extracted shared `CmCSP.Core` lib; job reuses the exact cost + cache pipeline (cache keys/TTLs/60 KB routing identical) |
| Collect all four datasets **sequentially** (`cm_main`, `cm_rg`, `cm_tag`, `cm_main_amort`) | P1 | ✅ Shipped | `InvalidateCache()` then the four `Get*` methods; Query-API fallback honours the 5-req/min limit, blob-export path has none |
| Add `Microsoft.App/jobs` resource `cmcsp-collect` in `bicep/app.bicep` | P1 | ✅ Shipped | `Schedule` trigger `0 2 * * *`; System+UserAssigned identity; KV Secrets User + storage RBAC (`main.bicep`) |
| Build/push/update `cmcsp-collect` image in `infra/hooks/postdeploy.ps1` | P1 | ✅ Shipped | Hook now loops over both cleanup + collect jobs, sharing the ACR token + docker-config setup |
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

> **Status:** Planned — design only, nothing implemented yet. Sequence the data-model and
> Redis abstraction work before touching the collector partitioning.

| Sub-task | Priority | Status | Notes |
|---|---|---|---|
| Design the SQL schema for cost rows + audit + cleanup state + subscription store | P1 | 📋 Planned | Decide tables/indexes; map `CostRow`, `CollectionAuditRecord`, subscription list; plan for 365-day rolling window |
| Provision Azure SQL **serverless** DB in bicep (auto-pause, MI-only auth) | P1 | 📋 Planned | Serverless compute tier; disable SQL auth; add EntraID admin; grant Container App + jobs MI `db_datareader`/`db_datawriter` |
| Provision Azure **Managed Redis (Basic)** in bicep with Entra (MI) auth | P1 | 📋 Planned | Basic SKU; access-key auth disabled; data-access policy for each MI principal |
| Introduce a data-access layer (EF Core or Dapper) for SQL persistence | P1 | 📋 Planned | Replace direct `TableClient`/`BlobContainerClient` calls; MI auth via `Microsoft.Data.SqlClient` access token |
| One-time historical backfill into SQL | P2 | 📋 Planned | Ingest pre-365-day blobs already in `cost-exports` **+** a one-time `Custom`-timeframe export run for the pre-export gap (Azure retains ~13 months at subscription scope); dedupe by natural key `(Date, SubscriptionId, ChargeType, grouping, Currency)` processing blobs oldest→newest so the **latest export wins**. Run-once `CostBackfillJob`; depends on the SQL schema/data-access layer |
| Replace `AzureStorageCacheService` with a Redis-backed `ICacheService` | P1 | 📋 Planned | `StackExchange.Redis` with `DefaultAzureCredential` token auth; preserve TTL semantics; drop the 60 KB table/blob routing |
| Migrate `CollectionAuditService` to SQL | P2 | 📋 Planned | Audit rows become a table; keep the same read/write API surface for the UI |
| Migrate `SubscriptionStoreService` off Key Vault/temp-file to SQL | P2 | 📋 Planned | Single source of truth in SQL; remove the disk + KV dual-write |
| Retire `CacheCleanupJob` | P2 | 📋 Planned | Its sole job is deleting expired Table/Blob cache rows — obsolete once Redis handles TTL eviction natively and the SQL fact table is durable (not pruned on a cadence). Remove the project, its `bicep/app.bicep` job + RBAC, and `postdeploy`/`postprovision` wiring |
| Update `BlobCostManagementService` consumption of exports | P2 | 📋 Planned | Cost Management **blob exports** stay (that's the source feed); only the *cache/persistence* of parsed rows moves to SQL/Redis |
| Wire all MI role assignments + remove secrets from config/KV | P1 | 📋 Planned | SQL Entra roles + Redis data-access policies; delete `client-secret`-style cache/storage secrets; keep MI-only |
| Update hooks + `appsettings` + cache instructions doc | P2 | 📋 Planned | New `Redis`/`Sql` config sections; rewrite `services-cache.instructions.md` for the Redis contract |
| **Per-replica per-subscription partitioning for the collector (`parallelism > 1`)** | P3 | 📋 Planned | Now unblocked by per-subscription Redis keys; fan the collect job out across subscriptions for faster runs. (Moved from Phase 3.) |

---

## Shipped

| Item | Phase | Shipped | Notes |
|---|---|---|---|
| Migrate deployment from PowerShell scripts to `azd` | Phase 1 | 2026-06 | `azure.yaml` + `infra/` hooks; subscription & billing export scopes |
| Move application code under `src/` | Phase 1 | 2026-06 | Aligns with standard project layout |
| Always register ACR for system-identity pulls | Phase 1 | 2026-06 | Fixes `azd deploy` image pull `UNAUTHORIZED` |
| Show detected export provisioning path next to each subscription | Phase 2 | 2026-06 | Short status chip on Home page; read-only `DetectAsync` |
| Externalise data collection to a scheduled + on-demand Container Apps Job | Phase 3 | 2026-06 | `cmcsp-collect` job (cron `0 2 * * *` + on-demand); audit trail; Collect-now UI; `CacheWarmupService` repurposed as rehydrator. See [`docs/data-collection.md`](data-collection.md) |

---

## Ideas / Parking lot

Unprioritised thoughts that may become backlog items later.

- _Capture rough ideas here before they're formally planned._
