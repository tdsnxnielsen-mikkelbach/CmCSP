# GitHub Copilot Agents & Instructions

This project includes custom GitHub Copilot agents and coding instructions to speed up common tasks. They live in `.github/agents/` and `.github/instructions/`.

---

## Agents

### `@Azure Cache Optimizer`

**File:** `.github/agents/azure-cache-optimizer.agent.md`

Analyzes and recommends improvements to the hybrid caching pipeline. It understands the three-layer architecture (in-memory → Azure Table → Azure Blob) and the constraints of a multi-replica Container App environment.

**When to use it:**
- Cold-start latency is high after a restart or deployment
- You want to tune `CacheExpirationMinutes` or `ApiDailyRefreshHourUtc`
- You are investigating a thundering-herd problem on the `BlobCostManagementService`
- You want to reduce Azure Table/Blob Storage transaction costs
- You are adding a new dataset and need to know where it fits in the cache flow

**Example prompts:**
```
@Azure Cache Optimizer Why does the first page load slowly after a new deployment?
@Azure Cache Optimizer Is the 60 KB Table/Blob threshold still appropriate for our payload sizes?
@Azure Cache Optimizer What would happen if we scaled to 4 Container App replicas?
@Azure Cache Optimizer How can we reduce the number of Azure Table Storage transactions?
```

**Tools available:** `read`, `search`, `todo` (read-only — it will never modify code).

---

### `@Bicep Reviewer`

**File:** `.github/agents/bicep-reviewer.agent.md`

Audits Bicep templates for IAM correctness, export configuration, and Azure best practices. It knows the exact role IDs required by the application and will flag missing, over-privileged, or misconfigured role assignments.

**When to use it:**
- After modifying any file in `bicep/`
- Before deploying a new environment to verify role assignments are complete
- When adding a new managed identity principal
- When changing storage container names or export settings

**Required role assignments it checks for:**

| Principal | Role | Scope |
|-----------|------|-------|
| Export managed identity | Storage Blob Data Contributor | Storage Account |
| Container App managed identity | Storage Blob Data Reader | Storage Account |
| Container App managed identity | Storage Table Data Contributor | Storage Account |

**Example prompts:**
```
@Bicep Reviewer Audit infra/modules/storage.bicep for IAM completeness.
@Bicep Reviewer Are there any security issues with the storage account config in main.bicep?
@Bicep Reviewer Check export-sub.bicep against current Bicep best practices.
@Bicep Reviewer Does app.bicep assign all the roles the Container App needs?
```

**Tools available:** `read`, `search`, `mcp_bicep/*` (build, lint, best-practices checks — read-only).

---

## Coding Instructions

### Services Cache Rules

**File:** `.github/instructions/services-cache.instructions.md`  
**Applies to:** `src/Services/**/*.cs`

This instruction file is **loaded automatically** whenever Copilot edits any file under `src/Services/`. It ensures the agent never violates the hybrid cache contract when modifying service code.

It enforces:

- **Layer order** — memory first, then Table (≤ 60 KB), then Blob (> 60 KB)
- **Cache key registry** — the three keys (`cm_main`, `cm_rg`, `cm_tag`) and which service owns them
- **Concurrency guard** — the `SemaphoreSlim` in `BlobCostManagementService` must not be bypassed
- **Sequential warmup** — `CacheWarmupService` fetches datasets sequentially to respect the 5 req/min API rate limit; do not parallelise
- **Invalidation contract** — any new cache key must also be cleared in `DailyApiRefreshService`
- **Encapsulation** — all Table/Blob reads and writes must go through `AzureStorageCacheService`, never directly

You do not need to invoke this manually — it is applied automatically by Copilot when the file path matches `src/Services/**/*.cs`.

---

## File Locations

```
.github/
  agents/
    azure-cache-optimizer.agent.md   ← @Azure Cache Optimizer
    bicep-reviewer.agent.md          ← @Bicep Reviewer
  instructions/
    services-cache.instructions.md   ← auto-applied to src/Services/**/*.cs
```
