# Phase 7 – Azure inventory & optimization (new ARM RBAC: Reader)

> Part of the [CmCSP roadmap](../todo.md). **Status: ✅ Shipped.**

**Goal:** Move from "what did it cost" to "what is running and where can we save," by joining
cost data to **live Azure resource inventory**. Same managed-identity auth, but the app/collect
identity needs **Reader** (and `Microsoft.Consumption` recommendation read) on the target
subscriptions. Azure-only scope.

| Sub-task | Priority | Status | Notes |
|---|---|---|---|
| **Azure Resource Graph inventory enrichment** | P1 | ✅ Shipped | `OptimizationService.GetInventoryAsync` runs one KQL query via `POST .../providers/Microsoft.ResourceGraph/resources` (objectArray, `$skipToken` paged) returning every resource with tags/region/type. `GetTagCoverageAsync` rolls it up (total / untagged / per-tag-key) and enriches the **Tag Chargeback** page with real resource counts. In-process TTL memo (deliberately outside the cost-cache contract — read-only ARM enrichment, not a collected dataset) |
| **Orphaned / untagged resource finder** | P2 | ✅ Shipped | `GetOrphanedResourcesAsync` Resource Graph query flags unattached managed disks, unassociated public IPs, orphaned NICs, empty App Service plans and stopped-but-allocated VMs, each with a human reason. New **Optimization** page (`/optimization`) + NavMenu link: KPI summary, orphaned-resource grid, untagged-by-type chart |
| **Reservation / Savings Plan purchase recommendations + expiry** | P2 | ✅ Shipped | `GetReservationRecommendationsAsync` (`Microsoft.Consumption/reservationRecommendations`, legacy + modern amount shapes, normalised to TargetCurrency) and `GetReservationOrdersAsync` (`Microsoft.Capacity/reservationOrders` → expiry + days-left chip). Both surfaced on the **Reservations** page |

---

## What shipped

**New service — `src/CmCSP.Core/Services/OptimizationService.cs`** (registered Singleton in
`src/Program.cs`). Uses the existing `AzureMgmt` HttpClient + `AzureTokenService` bearer token.
All reads are best-effort: on `403`/`404`/empty it logs a warning and sets `LastAccessDenied`, so the
UI shows a "needs Reader role" banner and an empty state instead of an error.

- `GetInventoryAsync()` / `GetTagCoverageAsync()` — Azure Resource Graph inventory + tag rollup.
- `GetOrphanedResourcesAsync()` — orphaned/idle resource finder.
- `GetReservationRecommendationsAsync()` — Consumption purchase recommendations (currency-normalised).
- `GetReservationOrdersAsync()` — Capacity reservation orders + expiry dates.

**New models — `src/CmCSP.Core/Models/InventoryModels.cs`:** `ResourceInventoryItem`,
`InventoryTagCoverage`, `OrphanedResource`, `ReservationPurchaseRecommendation`, `ReservationOrderInfo`.

**UI:**
- New page `src/Components/Pages/Optimization.razor` (`/optimization`) + NavMenu link (Savings icon).
- `TagChargeback.razor` — live "Resource Inventory — Tag Coverage" panel (resources / untagged / tagged %).
- `Reservations.razor` — "Purchase Recommendations" + "Reservation Expiry" sections.

**Caching note:** inventory/orphan/recommendation results are memoised in-process for
`CacheExpirationMinutes`. This is deliberately *outside* the cost-cache contract in
[services-cache.instructions.md](../../.github/instructions/services-cache.instructions.md): these are
on-demand, read-only ARM reads, not the CSV-export → SQL `CostFact` → `ICacheService` cost pipeline, so
they are **not** produced by `CostCollectorJob.RefreshAsync`.

## RBAC (new requirement)

The app/collector managed identities need the built-in **Reader** role on every target subscription
(covers Resource Graph + `Microsoft.Consumption/.../read`). See
[docs/azure-roles.md](../azure-roles.md) § 4b.

- **Deployment subscription:** `infra/main.bicep` assigns Reader to both identities when
  `grantReaderOnSubscription = true` (default).
- **Other subscriptions:** deploy `infra/modules/reader-sub.bicep` once per subscription.
- Reservation **expiry** (`Microsoft.Capacity`) may additionally need Reservations Reader at the
  tenant root; without it the expiry table is simply hidden.
