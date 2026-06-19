# Phase 4 — SQL + Redis data platform

Phase 4 replaces the bespoke Azure Table/Blob persistence and the in-process +
Storage-backed cache with two standard managed services:

- **Azure SQL Database (serverless tier)** — durable store for parsed cost rows, the
  collection audit trail, and the user-added subscription list.
- **Azure Managed Redis (Basic SKU)** — a shared cache across web replicas and jobs,
  replacing the `IMemoryCache` + `AzureStorageCacheService` (Table/Blob) hybrid.

**Everything authenticates with managed identity.** No SQL logins, no Redis access keys,
no connection-string secrets in config or Key Vault.

> Status: in progress. The data model (this document, the EF Core context, and
> `infra/sql/schema.sql`) is the first increment. Provisioning the live SQL/Redis
> resources and cutting services over are gated, follow-on steps.

## Why this change

The current design works but carries avoidable complexity and cost:

- `AzureStorageCacheService` hand-rolls a 60 KB Table-vs-Blob routing scheme and a
  separate `CacheCleanupJob` to evict expired rows. Redis does TTL eviction natively.
- Cost rows, audit rows, and the subscription list live in three different Table/Blob
  shapes with bespoke (de)serialisation. SQL gives one queryable, durable store.
- The subscription list is dual-written to a Key Vault secret **and** a temp file.

## Data model

EF Core context: `CmCSP.Data.CmcspDbContext` (project `CmCSP.Core`). Raw DDL mirror:
[`infra/sql/schema.sql`](../infra/sql/schema.sql).

### `CostFact` — aggregated cost rows

The durable replacement for the Table/Blob cache of parsed `CostRow`s. One row per
**natural key** `(Dataset, UsageDate, SubscriptionId, ServiceName, ResourceGroupName, Tag, Currency)`,
enforced by a unique index so re-collection and historical backfill **upsert** cleanly
(latest write wins).

| Column | Type | Notes |
|---|---|---|
| `Id` | `bigint` identity | surrogate PK |
| `Dataset` | `nvarchar(16)` | `main`, `rg`, `tag`, `main_amort` |
| `UsageDate` | `date` | daily granularity |
| `SubscriptionId` / `SubscriptionName` | `nvarchar(36)` / `(256)` | |
| `ServiceName` | `nvarchar(256)` | populated for `main` / `main_amort` |
| `ResourceGroupName` | `nvarchar(256)` | populated for `rg` |
| `Tag` | `nvarchar(512)` | populated for `tag` |
| `Cost` / `NormalizedCost` | `decimal(38,18)` | raw + TargetCurrency |
| `Currency` | `nvarchar(8)` | ISO 4217 |

Dimension columns default to `''` (not `NULL`) so the unique natural-key index dedupes
correctly — SQL Server treats `NULL`s as distinct.

### `CollectionAudit` — collection run history

Mirrors `CollectionAuditRecord` (replaces the `cmcspcollectaudit` table). Indexed by
`StartedUtc DESC` for the dashboard's "last run" read.

### `UserSubscription` — runtime UI-added subscriptions

`SubscriptionId` (PK) + `AddedUtc`. Config-provided IDs stay in config; only UI-added
IDs are persisted here, replacing the Key Vault secret + temp-file dual-write.

### `AppSetting` — small runtime flags

`Key`/`Value`/`UpdatedUtc` — e.g. the runtime `CostDetails.Enabled` flag, replacing
one-off Key Vault flag secrets.

## Authentication (managed identity, no secrets)

SQL connection string uses Entra token auth — `Microsoft.Data.SqlClient` acquires the
token via `DefaultAzureCredential`:

```
Server=tcp:<server>.database.windows.net,1433;Database=<db>;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;
```

Each managed identity is provisioned as a **contained database user** with
`db_datareader` + `db_datawriter`:

```sql
CREATE USER [<mi-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<mi-name>];
ALTER ROLE db_datawriter ADD MEMBER [<mi-name>];
```

For system-assigned managed identities, `<mi-name>` is the **resource name** (its Entra
display name) — i.e. the Container App (`cmcsp`) and the collect job (`cmcsp-collect`).
The postprovision hook (Phase 8) runs this T-SQL as the deployer (the SQL Entra admin).

Redis (Managed Redis, Basic) uses Entra data-access policies per MI principal; access-key
auth is disabled. The client (`StackExchange.Redis`) authenticates with a
`DefaultAzureCredential` token.

The cache tier is selected by config: `AzureCostManagement:Redis:Enabled=true` routes through
[`RedisCacheService`](../src/CmCSP.Core/Services/RedisCacheService.cs); otherwise the legacy
[`AzureStorageCacheService`](../src/CmCSP.Core/Services/AzureStorageCacheService.cs) (Table/Blob)
is used. Both implement [`ICacheService`](../src/CmCSP.Core/Services/ICacheService.cs) and wrap
`IMemoryCache` as L1. The postprovision hook sets `AzureCostManagement__Redis__Enabled/HostName/Port`
on the app + collect job when the data platform is provisioned.

## Migrations

The schema is owned by EF Core migrations in `CmCSP.Core`:

```
dotnet ef migrations add <Name> --project src/CmCSP.Core --startup-project src/CmCSP.csproj
dotnet ef database update      --project src/CmCSP.Core --startup-project src/CmCSP.csproj
```

`infra/sql/schema.sql` is the idempotent raw-DDL equivalent for script-based application
(e.g. from the provisioning hook) and as a review reference.

## Rollout sequence (gated)

1. ✅ Data model: EF Core context + `infra/sql/schema.sql` + this doc.
2. ✅ Provision Azure SQL serverless + Managed Redis in bicep (MI-only auth) — **cost-incurring**.
   Authored in [`infra/modules/data.bicep`](../infra/modules/data.bicep), gated behind the `deployDataPlatform`
   flag in [`infra/main.bicep`](../infra/main.bicep). Enable with
   `azd env set DEPLOY_DATA_PLATFORM true` plus the SQL Entra admin (see below), then `azd provision`.
3. ✅ Postprovision hook: create MI contained-DB users + apply `infra/sql/schema.sql`.
   Implemented as Phase 8 in [`infra/hooks/postprovision.ps1`](../infra/hooks/postprovision.ps1)
   (runs only when `DATA_PLATFORM_ENABLED=true`; uses the deployer's Entra credential).
4. ✅ Redis-backed `ICacheService`; migrate `CollectionAuditService` and
   `SubscriptionStoreService` to SQL. Both services take an optional
   `IDbContextFactory<CmcspDbContext>` (registered in `Program.cs` when
   `ConnectionStrings:Sql` is set) and use SQL as the system of record, falling back to
   Table Storage / Key Vault when SQL is absent.
5. ✅ One-time historical backfill of existing blob exports into `CostFact`. Run-once
   `CostBackfillJob` ([`src/CostBackfillJob`](../src/CostBackfillJob/Program.cs)) reads every
   export CSV and upserts aggregated rows by natural key (idempotent, latest export wins).
6. ✅ Retire `CacheCleanupJob`. Removed the project, its `bicep/app.bicep` job + `bicep/main.bicep`
   storage RBAC, and the `postdeploy`/`postprovision` wiring. Redis does TTL eviction natively and the
   `CostFact` table is durable (not pruned on a cadence), so the job is obsolete. Blob exports remain the
   source feed for the collector and backfill.

## Enabling the data platform

The data platform is **off by default**. To provision it:

```pwsh
# Entra admin for the SQL server — typically the deployer (interactive user)
azd env set SQL_ADMIN_LOGIN  (az ad signed-in-user show --query userPrincipalName -o tsv)
azd env set SQL_ADMIN_OBJECT_ID (az ad signed-in-user show --query id -o tsv)
azd env set DEPLOY_DATA_PLATFORM true
azd provision
azd deploy   # always re-deploy the real image after provision
```

The bicep creates the SQL server (Entra-only auth, deployer as AD admin), a serverless
database, an Azure Managed Redis (`Balanced_B0`) cluster with key-auth disabled, and a
`default` access-policy assignment for the Container App + collect job managed identities.
The contained-DB users and schema are applied by the postprovision hook.

When the data platform is enabled, the postprovision hook (Phase 6) **disables** the legacy
storage Table/Blob cache (`AzureCostManagement__AzureCache__Enabled=false`) so Redis is the
sole L2 cache; SQL `CostFact` is the durable store. With the platform off, the app falls back
to the storage cache and parses exports directly (no SQL).

## Cost data flow (source feed → durable store → cache)

`BlobCostManagementService` separates three concerns:

1. **Source feed** — Cost Management blob exports (CSV). Parsed, never cached.
2. **Durable store** — SQL `CostFact`. Survives cache eviction and restarts.
3. **Cache** — `ICacheService` (L1 `IMemoryCache` + L2 Redis) serving `cm_main`/`cm_rg`/`cm_tag`.

- **Write path (collector):** `ICostManagementService.RefreshAsync(ct)` parses the exports and,
  when SQL is configured, upserts the aggregated rows into `CostFact` (`UpsertFactsAsync`, keyed
  by the natural key, batched at 5k) before warming the cache. Amortized data stays API-only.
- **Read path (web app):** `PopulateAllCachesAsync` loads from `CostFact` (`LoadFromSqlAsync`)
  and warms the cache; only an empty `CostFact` (pre-first-collection) triggers a one-off blob
  parse. The optional `IDbContextFactory<CmcspDbContext>` ctor param is injected by DI when
  `ConnectionStrings:Sql` is set.

## Collector fan-out (per-subscription partitioning)

`CostCollectorJob` supports splitting the subscription set across executions:

| Env var | Default | Meaning |
|---------|---------|---------|
| `COLLECT_PARTITION_COUNT` | `1` | Number of partitions. `>1` enables fan-out. |
| `COLLECT_PARTITION_INDEX` | `0` | This execution's partition (`0 .. count-1`). |

Each execution collects only the subscriptions where `index == position % count` and restricts
blob parsing/persistence via `BlobCostManagementService.SubscriptionFilter`. This is **data-safe**
because `CostFact`'s natural key includes `SubscriptionId`, so disjoint partitions never conflict.

Container Apps Jobs run identical replicas with no native task index, so fan-out is achieved with
**separate scheduled executions** each pinned to a distinct `COLLECT_PARTITION_INDEX`, rather than
by bumping `parallelism` (which the bicep keeps at `1` by default).
