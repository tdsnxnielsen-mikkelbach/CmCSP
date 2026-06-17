---
applyTo: "src/**/Services/**/*.cs"
---
## Hybrid Cache Architecture — Always Read Before Editing

This project uses a **three-layer hybrid cache**. Any change to a service file must preserve this contract:

### Layer Order (fastest → slowest)
1. **`IMemoryCache`** — per-replica, lost on restart, always checked first
2. **Azure Table Storage** — shared across replicas, for payloads **≤ 60 KB** (`TableSizeLimit` constant)
3. **Azure Blob Storage** — shared across replicas, for payloads **> 60 KB** (pointer stored in Table row as `__blob:<blobName>`)

### Cache Keys
| Key | Dataset | Owner |
|-----|---------|-------|
| `cm_main` | Cost by service/meter | `BlobCostManagementService` |
| `cm_rg` | Cost by resource group | `BlobCostManagementService` |
| `cm_tag` | Cost by tag | `BlobCostManagementService` |

### Concurrency Rules
- `BlobCostManagementService` uses a `SemaphoreSlim(1,1)` (`_fetchLock`) to prevent thundering herd on cold-start — do not remove or bypass this.
- `CacheWarmupService` is a **rehydrator only**: on startup it repopulates the in-memory tier from the persistent (Table/Blob) cache via `AzureStorageCacheService.TryGetValue` and never issues live API calls. A persistent-cache miss is skipped, not fetched.
- Fresh data collection is owned by `CostCollectorJob` (nightly + on-demand), which calls `InvalidateCache()` before re-fetching — any new cache key must also be cleared there.

### Configuration
All TTL and storage settings live in `CostManagementOptions` (`appsettings.json` section `AzureCostManagement`):
- `CacheExpirationMinutes` — in-memory TTL
- `AzureCache.Enabled`, `AzureCache.StorageAccountUri`, `AzureCache.TableName`, `AzureCache.CacheContainerName`

### Do Not
- Do not add `await` inside the `SemaphoreSlim` double-check pattern in `BlobCostManagementService` without also re-checking the cache after acquiring.
- Do not store new cache keys without registering them in `DailyApiRefreshService.InvalidateCache()`.
- Do not call Table or Blob Storage directly from a service — always go through `AzureStorageCacheService`.
