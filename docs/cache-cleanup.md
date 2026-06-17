# Cache Cleanup – Design & Operations

## Overview

CmCSP uses a two-tier distributed cache backed by **Azure Table Storage** (small payloads ≤ 60 KB) and **Azure Blob Storage** (large payloads > 60 KB). Cache entries are written with an `ExpiresAt` timestamp and are subject to eviction by two independent mechanisms:

| Mechanism | Where | Trigger | Purpose |
|---|---|---|---|
| **Read-path TTL check** | `AzureStorageCacheService.TryGetValue` | Every cache read | Correctness – never return stale data |
| **Scheduled cleanup job** | `cmcsp-cleanup` Container Apps Job | Every 30 minutes | Storage hygiene – remove entries that are never read again |

---

## Read-Path TTL Enforcement

Every call to `AzureStorageCacheService.TryGetValue` checks the `ExpiresAt` field **before** downloading any blob payload. If the entry has expired:

1. If the table row contains a `__blob:` pointer, the referenced blob is deleted from `cmcspcache` container first.
2. The table row is then deleted.
3. A cache-miss is returned so the caller re-fetches live data.

This ensures correctness is not dependent on the scheduled job having run recently.

---

## Scheduled Cleanup Job

### Resource

An **Azure Container Apps Job** named `<appName>-cleanup` is provisioned alongside the main Container App in `app.bicep`. It runs on a cron schedule independently of the main app.

### Schedule

```
*/30 * * * *
```

Runs every 30 minutes. Cleanup of a typical cache (< 20 entries, < 5 expired) completes in under 5 seconds. The replica timeout is 5 minutes.

### What it does

1. Lists all entities in the `cmcsp` partition of Table Storage.
2. For each entity where `ExpiresAt <= UTC now`:
   - Deletes the blob if the `Payload` field starts with `__blob:`.
   - Deletes the table row.
3. Exits `0` on success, `1` if any rows could not be processed.

### Identity & RBAC

The job runs with its own **SystemAssigned Managed Identity**. The following roles are granted by `main.bicep`:

| Role | Scope | Purpose |
|---|---|---|
| Storage Table Data Contributor | Storage account | Read + delete table rows |
| Storage Blob Data Contributor | `cmcspcache` blob container | Delete large-payload blobs |

The main app's Managed Identity is unchanged and retains `Storage Blob Data Reader` (read-only for export CSVs).

---

## Deployment

### First-time (`azd provision`)

`azd provision` provisions the `cmcsp-cleanup` Container Apps Job automatically via `app.bicep`. The job starts with a placeholder image.

In the same deployment, `main.bicep` grants the cleanup job MI the required storage roles using its output principal ID.

The `postprovision` hook wires the storage endpoints as environment variables on the job.

### Image update (`azd deploy`)

`azd deploy` builds and rolls the main app image, and the `postdeploy` hook
(`infra/hooks/postdeploy.ps1`) builds and updates the cleanup job image:

1. Builds `CmCSP.csproj` → `<acr>/cmcsp:<tag>` → updates Container App (`azd deploy`).
2. Builds `src/CacheCleanupJob/CacheCleanupJob.csproj` → `<acr>/cmcsp-cleanup:<tag>` → updates the Container Apps Job (postdeploy hook).

```pwsh
azd deploy
```

---

## Configuration Reference

The cleanup job reads the following environment variables:

| Variable | Default | Description |
|---|---|---|
| `CACHE_TABLE_ENDPOINT` | *(required)* | Table Storage service endpoint, e.g. `https://<account>.table.core.windows.net` |
| `CACHE_BLOB_ENDPOINT` | *(required)* | Blob Storage service endpoint, e.g. `https://<account>.blob.core.windows.net` |
| `CACHE_TABLE_NAME` | `cmcspcache` | Table name |
| `CACHE_CONTAINER_NAME` | `cmcspcache` | Blob container name for large payloads |
| `CACHE_PARTITION_KEY` | `cmcsp` | Partition key used by the app cache |

---

## Cache TTL Values

| Environment | `CacheExpirationMinutes` | Source |
|---|---|---|
| Development | 5 | `appsettings.Development.json` |
| Production | 60 | `appsettings.json` |

The TTL is configurable via `AzureCostManagement:CacheExpirationMinutes` in `appsettings.json` or as an environment variable on the Container App.

---

## Monitoring

Cleanup job executions are visible in **Log Analytics** under the shared `<appName>-logs` workspace. Each run logs:

```
[<timestamp>] Cache cleanup starting. Table=cmcspcache, Container=cmcspcache, Partition=cmcsp
[<timestamp>]   Deleted blob: cm_main-20260528123456.json
[<timestamp>]   Deleted expired entry: key=cm_main, expired=2026-05-28T11:00:00+00:00
[<timestamp>] Cleanup complete. Scanned=8, Expired=3, Errors=0
```

To query recent executions:

```kusto
ContainerAppConsoleLogs_CL
| where ContainerName_s == "cmcsp-cleanup"
| order by TimeGenerated desc
| project TimeGenerated, Log_s
```
