# Cost Details API – Reservations & Amortized Cost

This guide covers the **Cost Details API** integration added to CmCSP — including the new
**Reservations** page, the **Amortized Cost** toggle on Trend & Forecast and Subscription Breakdown,
and how to configure and deploy at both **billing-account/customer scope** (MCA/CSP) and
**subscription scope**.

---

## What was added

| Area | Change |
|------|--------|
| `src/Models/CostDetailsModels.cs` | New: request/response shapes for `generateCostDetailsReport` + `ReservationRow` model with Used/Unused/Total and `UtilizationPct` |
| `src/Services/ICostDetailsService.cs` | New: interface for billing-account and subscription scope reservation queries |
| `src/Services/CostDetailsService.cs` | New: async POST → 202 → poll → CSV download → parse → cache (4h TTL). Full RFC 4180 CSV parser, currency normalisation, multi-month split |
| `src/Services/ICostManagementService.cs` | `GetAmortizedMainCostDataAsync()` added |
| `src/Services/CostManagementService.cs` | `GetAmortizedMainCostDataAsync` + `cm_main_amort` cache key. `BuildQueryBody` now accepts a `metric` parameter (`ActualCost` / `AmortizedCost`) |
| `src/Services/BlobCostManagementService.cs` | Delegates `GetAmortizedMainCostDataAsync` to the API service (exports use ActualCost only) |
| `src/Components/Pages/Reservations.razor` | New page at `/reservations` |
| `src/Components/Pages/TrendAndForecast.razor` | Chip-toggle for Actual/Amortized cost metric |
| `src/Components/Pages/SubscriptionBreakdown.razor` | Added "Amortized Cost" and "RI Savings" columns |
| `src/Components/Layout/NavMenu.razor` | "Reservations" nav entry added |
| `src/Models/CostManagementOptions.cs` | Two new config sections: `CostDetails` and `BillingAccount` |
| `appsettings.json` | Default values for the new sections |

---

## How the Cost Details API works

The `generateCostDetailsReport` endpoint is **asynchronous**:

```
POST .../generateCostDetailsReport?api-version=2023-11-01
  → 202 Accepted  (Location: <poll-url>, Retry-After: 15)

GET <poll-url>
  → 202 Accepted  (still running – wait Retry-After, then poll again)
  → 200 OK        { status: "Completed", properties: { blobs: [{ blobLink, byteCount }] } }

GET <blobLink>    (pre-authenticated SAS URL – no bearer token required)
  → CSV file
```

`CostDetailsService.TriggerAndPollAsync` implements this loop with configurable timeout and
re-uses `Retry-After` from each poll response. Tokens are refreshed on each poll cycle to
handle long-running reports.

---

## Scopes

| Scope | URL pattern | When to use |
|-------|-------------|-------------|
| **Billing-account / customer** | `.../billingAccounts/{id}/customers/{customerId}/...` | CSP partner — full RI utilisation for a specific customer, independent of which subscription they use |
| **Subscription** | `.../subscriptions/{subscriptionId}/...` | Always available — shows reservations applied to that subscription only |

### Billing-account scope limitations

- Requires **Cost Management Reader** at `billingAccounts/{id}` *and* at each `customers/{id}` scope — this is **separate** from subscription-scope Reader.
- Shared RIs purchased at billing-account level and applied across customers are visible here but not at subscription scope.
- The service principal (or Managed Identity) must be granted access in **Partner Center → Account settings → Manage users**, not just in Azure RBAC.

### Subscription-scope fallback

When billing-account access is not configured (`BillingAccount:BillingAccountId` is empty),
`ICostDetailsService.HasBillingAccountAccess` returns `false`. All billing-scope methods return
empty results gracefully, and the Reservations page shows a warning banner and defaults to
subscription-scope queries automatically.

---

## Configuration

All new settings live under the `AzureCostManagement` section in `appsettings.json` / Key Vault.

### `CostDetails` section

```json
"CostDetails": {
  "Enabled": false,
  "ApiVersion": "2023-11-01",
  "PollingTimeoutSeconds": 600,
  "PollingIntervalSeconds": 15,
  "CacheTtlHours": 4
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `false` | Set to `true` to activate the Reservations page and AmortizedCost features. When `false`, the Reservations page shows an **Enable Now** button that persists the setting to Key Vault and activates the feature immediately without a restart. |
| `ApiVersion` | `2023-11-01` | API version for `generateCostDetailsReport`. Do not change unless Microsoft releases a newer GA version. |
| `PollingTimeoutSeconds` | `600` | How long to poll before giving up (10 min). Large datasets take longer. |
| `PollingIntervalSeconds` | `15` | Minimum gap between poll requests. The `Retry-After` header takes precedence when present. |
| `CacheTtlHours` | `4` | Cache lifetime for reservation results. The API data updates every ~4 hours per Microsoft documentation. Setting lower increases API calls. |

### `BillingAccount` section

```json
"BillingAccount": {
  "BillingAccountId": "",
  "Customers": [
    { "CustomerId": "", "DisplayName": "" }
  ]
}
```

| Key | Description |
|-----|-------------|
| `BillingAccountId` | Billing account number from **Azure portal → Cost Management → Properties**. Usually a numeric string like `"12345678"`. Leave empty to use subscription scope only. |
| `Customers[].CustomerId` | The customer's billing ID shown under **Billing account → Customers** in the portal. This is *not* the Azure AD tenant ID. |
| `Customers[].DisplayName` | Human-readable label shown in the Reservations page customer column. |

---

## Switching from subscription scope to billing-account scope

**Step 1 – Find your billing account ID**

```bash
az billing account list --query '[].{id:id, name:displayName}' -o table
```

The `id` column returns the numeric billing account ID (e.g. `12345678`).

**Step 2 – Find your customer IDs**

```bash
az billing customer list --billing-account-name <billingAccountId> \
  --query '[].{customerId:name, displayName:displayName}' -o table
```

**Step 3 – Grant Cost Management Reader at billing scope**

```bash
# On the billing account
az role assignment create \
  --assignee <clientId-or-principalId> \
  --role "Cost Management Reader" \
  --scope "/providers/Microsoft.Billing/billingAccounts/<billingAccountId>"

# On each customer
az role assignment create \
  --assignee <clientId-or-principalId> \
  --role "Cost Management Reader" \
  --scope "/providers/Microsoft.Billing/billingAccounts/<billingAccountId>/customers/<customerId>"
```

> **Note:** Billing-scope role assignments may also require approval in Partner Center.
> See [docs/azure-roles.md](azure-roles.md) for the full RBAC matrix.

**Step 4 – Update configuration**

Via `az keyvault secret set` (production) or `dotnet user-secrets` (local):

```bash
# Production — Key Vault
az keyvault secret set --vault-name <kv-name> \
  --name "CmCSP--CostDetails--Enabled" --value "true"
az keyvault secret set --vault-name <kv-name> \
  --name "CmCSP--BillingAccount--BillingAccountId" --value "12345678"

# Local — user-secrets
dotnet user-secrets set "AzureCostManagement:CostDetails:Enabled"                   "true"
dotnet user-secrets set "AzureCostManagement:BillingAccount:BillingAccountId"        "12345678"
dotnet user-secrets set "AzureCostManagement:BillingAccount:Customers:0:CustomerId"  "<customerId>"
dotnet user-secrets set "AzureCostManagement:BillingAccount:Customers:0:DisplayName" "Credaris AG"
```

For Container Apps (via the azd `postprovision` hook or `az containerapp update`):

```bash
az containerapp update \
  --name cmcsp --resource-group rg-cmcsp-app \
  --set-env-vars \
    "AzureCostManagement__CostDetails__Enabled=true" \
    "AzureCostManagement__BillingAccount__BillingAccountId=12345678" \
    "AzureCostManagement__BillingAccount__Customers__0__CustomerId=<customerId>" \
    "AzureCostManagement__BillingAccount__Customers__0__DisplayName=Credaris AG"
```

**Step 5 – Verify**

Navigate to `/reservations`. The scope banner should read:
> **Fetching data at billing-account / customer scope** — full Used/Unused breakdown per customer is available.

If it still shows the subscription-scope warning, check that `BillingAccountId` is non-empty
and that the service principal has billing-scope Reader access.

---

## Deploying via azd

### `azd provision` — optional settings

The two new configuration sections are **not** enabled by default (they require
billing-account access which may not be available during initial setup). To add them post-deploy,
re-run `azd provision` with the new settings, or set them manually via `az containerapp update`
as shown above.

If you want to include them in a fresh `azd provision` run, set:

```pwsh
azd env set ENABLE_COST_DETAILS true
azd env set BILLING_ACCOUNT_ID  "12345678"
azd provision
```

> Per-customer mappings (`CustomerIds` / `CustomerDisplayNames`) can be stored as
> Key Vault secrets (`CmCSP--BillingAccount--Customers--<i>--CustomerId` /
> `--DisplayName`) via `az keyvault secret set` after provisioning.

| Setting | Description |
|-----------|-------------|
| `ENABLE_COST_DETAILS` | `azd env set ENABLE_COST_DETAILS true` — sets `CmCSP--CostDetails--Enabled = true` in Key Vault |
| `BILLING_ACCOUNT_ID` | Numeric billing account ID — sets `CmCSP--BillingAccount--BillingAccountId` |
| Customer mappings | Stored as `CmCSP--BillingAccount--Customers--<i>--CustomerId` / `--DisplayName` Key Vault secrets |

All settings are optional. Omitting them means the Cost Details feature stays disabled until you set the secrets manually.

### `azd deploy`

Image deploys do **not** change environment variables — they only update the container image
digest. The Cost Details and BillingAccount settings you set once via `azd provision` or
`az containerapp update` are preserved across image updates.

---

## Reservations page (`/reservations`)

| Feature | Detail |
|---------|--------|
| **Enable Now button** | When `CostDetails:Enabled = false`, a warning banner with an **Enable Now** button is shown. Clicking it calls `SubscriptionStoreService.EnableCostDetailsAsync()`, which sets `Enabled = true` in-process (immediate effect) and writes the Key Vault secret `CmCSP--CostDetails--Enabled = true` so the setting survives container restarts. |
| **Scope toggle** | "Billing Account (all customers)" or "Subscriptions" — only shown when billing-account access is configured |
| **Month picker** | Selects the billing period; defaults to current month |
| **KPI cards** | Total RI cost, Used cost, Unused cost, Overall utilisation % |
| **Used vs Unused chart** | Stacked horizontal bar — top 20 reservations by used cost |
| **Detail table** | Per-reservation rows with Customer (billing scope only), Service, Term, Subscription, Used/Unused/Total costs, utilisation progress bar |

### Reservation support at subscription scope

Yes — `generateCostDetailsReport` at subscription scope returns reservation rows where
`ChargeType` is `Usage` (used portion) or `UnusedReservation` (wasted portion). The
limitation is that **shared reservations** purchased at billing-account level may only appear
for the subscriptions they were actually applied to. To see cross-customer RI coverage,
billing-account scope is required.

---

## Amortized Cost

`AmortizedCost` distributes the upfront reservation purchase cost evenly across all days in
the reservation term. For example, a 1-year RI purchased on 1 Jan for 12,000 USD appears as
~32.88 USD/day in amortized view instead of a single 12,000 spike in January.

### Trend & Forecast page

A chip-set toggle at the top of the page lets the user switch between:
- **Actual Cost** — as billed (reservation purchase appears on purchase date)
- **Amortized Cost** — cost spread over the term for smoother trend analysis

The selected metric is local to the page and does not affect other pages or the cache.
Both datasets are cached independently (`cm_main` and `cm_main_amort`).

### Subscription Breakdown page

Two new columns in the reference table:
- **Amortized Cost** — total amortized cost for the selected period (only populated when `CostDetails:Enabled = true`)
- **RI Savings** — `ActualCost − AmortizedCost`. A positive value (red) means the subscription paid more than its amortized share (e.g. it made a large reservation purchase this period). A negative value (green) means the subscription is benefiting from reservations purchased in prior periods.

---

## Cache keys

| Key pattern | Dataset | TTL |
|-------------|---------|-----|
| `cm_main_amort` | Amortized cost by service, all subscriptions | `CacheExpirationMinutes` (default 60 min) |
| `cd_cust_{customerId}_{fromYYYYMM}_{toYYYYMM}` | Reservation rows for a billing customer | `CacheTtlHours` (default 4 h) |
| `cd_sub_{subscriptionId}_{fromYYYYMM}_{toYYYYMM}` | Reservation rows for a subscription | `CacheTtlHours` (default 4 h) |

Cache entries are cleared by `ICostDetailsService.InvalidateCache()` which is not wired to
the global **Refresh Data** button yet. To force a refresh, either wait for TTL expiry or
restart the Container App revision.
