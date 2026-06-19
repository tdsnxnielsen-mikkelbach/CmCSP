---
applyTo: "src/**/Services/**/*.cs"
---
## Cache & Data Platform Architecture — Always Read Before Editing

Cost data flows through three concerns that must be kept distinct. Any change to a
service file must preserve this contract:

1. **Source feed** — Cost Management **blob exports** (CSV) written daily to the
   `cost-exports` container. This is the authoritative input; it is parsed, never cached.
2. **Durable store** — Azure **SQL** `CostFact` table (Phase 4). Parsed/aggregated rows are
   upserted here so data survives cache eviction and restarts.
3. **Cache** — `ICacheService` (in-memory **L1** + distributed **L2**) holding the
   ready-to-serve dataset lists for fast page loads.

### Cache Tiers (`ICacheService`)
Consumers depend on the `ICacheService` abstraction; the backing store is chosen by config/DI:

| Implementation | L1 | L2 | When |
|----------------|----|----|------|
| `RedisCacheService` | `IMemoryCache` | Azure Managed **Redis** (SSL :10000, MI auth) | `AzureCostManagement:Redis:Enabled=true` (data platform) |
| `AzureStorageCacheService` | `IMemoryCache` | Azure Table/Blob (legacy fallback) | Redis disabled |

- **Redis is preferred** and uses native TTL eviction (no cleanup job). Auth is
  managed-identity only (`DefaultAzureCredential` → `ConfigureForAzureWithTokenCredentialAsync`);
  there are no connection-string/access-key secrets. It degrades to L1-only on connect failure.
- Never call Redis, Table or Blob Storage directly from a service — always go through
  `ICacheService` (`TryGetValue` / `Set` / `Remove`).

### Cache Keys
| Key | Dataset | Owner |
|-----|---------|-------|
| `cm_main` | Cost by service/meter | `BlobCostManagementService` |
| `cm_rg` | Cost by resource group | `BlobCostManagementService` |
| `cm_tag` | Cost by tag | `BlobCostManagementService` |

### Read / Write Paths (`BlobCostManagementService`)
- **Read (web app):** when `IDbContextFactory<CmcspDbContext>` is injected (SQL enabled),
  `PopulateAllCachesAsync` loads rows from `CostFact` (`LoadFromSqlAsync`) and warms the cache.
  Only on an empty `CostFact` (before the first collection) does it fall back to a one-off
  blob parse. With no SQL it parses the exports directly.
- **Write (collector):** `RefreshAsync(ct)` parses the exports and, when SQL is enabled,
  upserts the aggregated rows into `CostFact` (`UpsertFactsAsync`, batched at `SaveBatchSize`,
  keyed by `NaturalKey`) before warming the cache. Amortized data stays API-only.
- The `CostFact` natural key includes `SubscriptionId`, so disjoint per-subscription writes
  never conflict — this is what makes the collector's `parallelism > 1` partitioning safe.

### Concurrency Rules
- `BlobCostManagementService` uses a `SemaphoreSlim(1,1)` (`_fetchLock`) to prevent a
  thundering herd on cold-start — do not remove or bypass it. When adding `await` inside the
  double-check pattern, re-check the cache after acquiring the lock.
- `CacheWarmupService` is a **rehydrator only**: on startup it repopulates L1 from the shared
  tier via `ICacheService.TryGetValue`; it never issues live API calls. A shared-tier miss is
  skipped, not fetched.
- Fresh collection is owned by **`CostCollectorJob`** (nightly + on-demand), which calls
  `ICostManagementService.RefreshAsync()`. Any new cache key must also be produced there.

### Configuration
TTL and store settings live in `CostManagementOptions` (`appsettings.json` → `AzureCostManagement`):
- `CacheExpirationMinutes` — L1 TTL.
- `Redis.Enabled`, `Redis.HostName`, `Redis.Port` (10000), `Redis.KeyPrefix` (`cmcsp:`).
- `AzureCache.*` — legacy Table/Blob cache; only wired when Redis is disabled.
- SQL durable store: top-level `ConnectionStrings:Sql` (Entra-token, no secret). When present,
  `AddDbContextFactory<CmcspDbContext>` is registered and DI injects it into `BlobCostManagementService`.

### Do Not
- Do not store new cache keys without producing them in `CostCollectorJob` via `RefreshAsync`.
- Do not add connection-string or access-key secrets for Redis/SQL/Storage — all are MI-only.
- Do not call the L2 store directly; always go through `ICacheService`.
- Do not reintroduce a cache-cleanup job — Redis native TTL handles eviction.

