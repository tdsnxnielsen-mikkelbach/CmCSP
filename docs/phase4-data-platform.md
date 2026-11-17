# SQL and Managed Redis data platform

The optional data platform separates the export source feed, durable facts, and
disposable cache. It serves the .NET 10 Blazor + Radzen + ApexCharts dashboard and
the collector through the shared [Core project](../src/CmCSP.Core/CmCSP.Core.csproj).

## Storage responsibilities

| Store | Responsibility |
|---|---|
| Azure Blob cost exports | Authoritative CSV input; not a cache |
| Azure SQL `CostFact` | Durable aggregated cost facts |
| SQL `CollectionAudit`, `UserSubscription`, `AppSetting` | Audit and runtime state |
| SQL `Customer`, `CustomerSubscription` | Optional multi-tenant registry |
| `ICacheService` | Ready-to-serve datasets, memory L1 plus configured L2 |

EF Core entities/context live under [Data](../src/CmCSP.Core/Data);
[schema.sql](../infra/sql/schema.sql) is the idempotent provisioning DDL.
The SQL fact natural key contains dataset, usage date, subscription, dimensions,
and currency. Preserve subscription disambiguation and idempotent upsert behavior.

## Read and write paths

- Collector `RefreshAsync`: parse exports, upsert facts in batches, publish caches.
- Web reads: use the existing dataset/date indexes for detailed facts; summary
  pages use a scoped SQL `GROUP BY` for daily subscription totals. An empty initial
  SQL store can fall back to a one-off export parse.
- Amortized cost remains API-backed.
- Backfill: explicitly import all historical export CSVs into SQL.
- Startup warmup: rehydrate existing shared entries without live collection.

## Cache selection

`AzureCostManagement:Redis:Enabled=true` selects `RedisCacheService`.
It wraps `IMemoryCache` with Managed Redis and native TTL eviction. Redis
authentication uses `DefaultAzureCredential` and the Azure Redis token extension.
TLS port defaults to 10000.

Otherwise `AzureStorageCacheService` supplies memory L1 and optional Table/Blob L2.
The legacy implementation routes small payloads to Table Storage and large
payloads to Blob Storage. Consumers depend on `ICacheService`, not on its backing store.

`CacheExpirationMinutes` and the Redis/AzureCache sections control cache settings.
Scoped deployments prefix cost keys by tenant/customer. The principal datasets are `cm_main`, `cm_main_daily_sub`, `cm_rg`, `cm_tag`,
and API-backed `cm_main_amort`.

## Enablement

The platform is gated by `DEPLOY_DATA_PLATFORM` / `deployDataPlatform`.

```powershell
azd env set SQL_ADMIN_LOGIN "<entra-admin-login>"
azd env set SQL_ADMIN_OBJECT_ID "<entra-admin-object-id>"
azd env set DEPLOY_DATA_PLATFORM true
azd provision
azd deploy
```

Provisioning incurs Azure charges. The hook applies schema, creates contained
database users, and wires connection/cache settings to the web app and collector.
It disables legacy Storage L2 when Redis is selected.

## Authentication

SQL uses an Entra-authenticated connection string:

```text
Server=tcp:<server>.database.windows.net,1433;Database=<database>;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;
```

Runtime identities need contained users with the configured data-reader/writer
roles. Redis has per-identity Entra access policies and disabled access-key auth.
These are not SQL-password or Redis-key secrets. The Entra app credential for
browser sign-in and service-principal Azure calls is a separate concern.

## Capacity and failure behavior

The data module defines SQL serverless with 60-minute auto-pause and minimum
capacity 0.5, plus Managed Redis `Balanced_B0`. SQL resume can delay a cold read;
memory/Redis normally shield interactive requests. Redis connection failures
are logged and can degrade to L1-only operation; this is not durable storage.

Use the repository's `-chiseled-extra` .NET container bases: SqlClient requires
globalization support. Do not switch to invariant-globalization images.

See [collection](data-collection.md), [roles](azure-roles.md), and
[service instructions](../.github/instructions/services-cache.instructions.md).
