# CmCSP – CSP Deployment Guide (from scratch)

This guide walks a **Cloud Solution Provider (CSP)** through deploying CmCSP to Azure Container Apps
from a clean starting point. Follow the steps in order.

---

## Pre-requisites

| Requirement | Notes |
|---|---|
| Azure CLI ≥ 2.60 | `az --version` |
| .NET 10 SDK | `dotnet --version` — also handles container build and push (no Docker required) |
| Azure subscription | Where the app infrastructure lives (your own Partner subscription, not a customer subscription) |
| Partner Center access | Global Admin or Billing Admin role — needed to enable customer cost visibility |
| Bicep CLI | Installed automatically with Azure CLI; verify with `az bicep version` |

---

## Resource group layout

| Resource group | Location | What lives here |
|---|---|---|
| `rg-cmcsp-app` | swedencentral | Storage account, cost export containers, Table Storage (cache), cost export schedules, Container Registry, Key Vault, Log Analytics, Container Apps Environment, Container App |

---

## Automated deployment (recommended)

Deployment is driven by the **Azure Developer CLI (`azd`)**. A single `azd up`
provisions all infrastructure (via the Bicep modules in `bicep/`), seeds Key Vault,
wires Container App environment variables, deploys the Cost Management exports, and
builds + pushes both container images.

```pwsh
azd auth login
azd env new cmcsp-prod
azd env set CMCSP_TENANT_ID        "<entra-tenant-id>"
azd env set CMCSP_CLIENT_ID        "<app-client-id>"
azd env set CMCSP_CLIENT_SECRET    "<client-secret>"
azd env set CMCSP_SUBSCRIPTION_IDS "<sub-id-1>,<sub-id-2>"
azd env set EXPORT_SCOPE           subscription   # or: billing
azd up
```

**See [azd-deployment-guide.md](azd-deployment-guide.md) for the complete walkthrough**, including
the subscription ↔ billing-account export scope switch, optional settings
(`ENABLE_COST_DETAILS`, `HISTORICAL_MONTHS`), adopting existing resources, and day-2
operations (`azd deploy`, `azd provision`, `azd down`).

After deployment, the app performs a one-time export reconciliation pass at startup for all active subscriptions when `AzureCostManagement:ExportBlob:Enabled=true`. If a subscription already has a compatible Cost Management export targeting the configured storage account, the app reuses it. If not, it creates the canonical `cmcsp-daily-export`. Operators can also open the **Home** page and use **Re-provision Export** for any active subscription to retry that flow manually.

> The manual steps below explain **what `azd` does under the hood** and remain useful
> for troubleshooting or for environments where `azd` is not available.

---

## Step 1 – Clone the repository and configure local secrets

```bash
git clone <your-repo-url>
cd CmCSP
dotnet restore
```

For local development only (not needed for the Azure deployment):
```bash
dotnet user-secrets init
dotnet user-secrets set "AzureCostManagement:TenantId"          "<entra-tenant-id>"
dotnet user-secrets set "AzureCostManagement:ClientId"          "<app-client-id>"
dotnet user-secrets set "AzureCostManagement:ClientSecret"      "<client-secret>"
dotnet user-secrets set "AzureCostManagement:SubscriptionIds:0" "<subscription-id>"
```

---

## Step 2 – Enable cost visibility in Partner Center

> **This must be done before any API call or export will return data.**

1. Sign in to [Partner Center](https://partner.microsoft.com) with a Global Admin account.
2. Go to **Customers → {customer name} → Service management**.
3. Under **Azure subscriptions**, find each subscription you want to report on.
4. Toggle **Cost visibility for customer** → **On**.

Repeat for every subscription you will add to `SubscriptionIds`.

---

## Step 3 – Create an Entra app registration (Query API / fallback mode)

> Skip this step if you intend to run only in Blob Export mode with no Query API fallback.

```bash
# Create the app registration
APP_ID=$(az ad app create --display-name "cmcsp-dashboard" \
  --query appId -o tsv)

# Create a service principal
SP_OID=$(az ad sp create --id "$APP_ID" --query id -o tsv)

# Create a client secret (copy the value — you will store it in Key Vault in Step 8)
SECRET=$(az ad app credential reset --id "$APP_ID" \
  --display-name "cmcsp-prod" \
  --years 2 \
  --query password -o tsv)

echo "TenantId:     $(az account show --query tenantId -o tsv)"
echo "ClientId:     $APP_ID"
echo "ClientSecret: $SECRET   ← copy now, store in Key Vault (Step 8)"
```

### 3b – Configure redirect URIs for user authentication

The same app registration handles both the Cost Management API client credentials flow and the browser-based OIDC login flow. You need to register the redirect URIs for each environment you will use.

```bash
# Replace <fqdn> with the Container App FQDN from Step 8 output.
# Run this once after you know the FQDN (re-running is safe – it overwrites).
APP_ID="<your-app-client-id>"

az ad app update --id "$APP_ID" \
  --web-redirect-uris \
    "https://localhost:7105/signin-oidc" \
    "http://localhost:5106/signin-oidc" \
    "https://<fqdn>/signin-oidc"
```

You can also add these manually in **Azure Portal → App registrations → {app} → Authentication → Platform configurations → Web → Redirect URIs**.

> **Required platform type:** Make sure the platform is **Web** (not SPA or Mobile). Under  
> **Implicit grant and hybrid flows**, leave both token checkboxes **unchecked** — the app uses  
> the authorization code flow, not implicit flow.

---

## Step 4 – Assign roles to the app registration

### 4a – Cost Management Contributor (required for all modes)

Run once for every customer subscription you want to include in the dashboard.
`Cost Management Contributor` is a superset of `Cost Management Reader` — it covers the Query API and additionally allows the app to automatically create the daily export when a subscription is added via the UI.

```bash
az role assignment create \
  --assignee "$APP_ID" \
  --role "Cost Management Contributor" \
  --scope "/subscriptions/<customer-subscription-id>"
```

For billing-account level access (Partner Center Billing Admin required):
- Go to **Partner Center → Billing → Billing account → Access control (IAM)**
- Add **Billing Account Reader** for the service principal.

### 4b – Reader (required for the Advisor Cost Savings page)

The Advisor recommendations API requires the **Reader** role, which is broader than `Cost Management Reader`.
Run once for every subscription you added in Step 4a:

```bash
az role assignment create \
  --assignee "$APP_ID" \
  --role "Reader" \
  --scope "/subscriptions/<customer-subscription-id>"
```

> **Note:** `Reader` grants `*/read` across all resource types. For CSP resellers this is applied to
> customer subscriptions via the same cross-tenant Entra app credentials. Inform customers that
> the service principal will be able to read (but not modify) any resource in their subscription.
> If the Advisor page is not required, this role assignment can be omitted.

---

## Step 5 – Create resource group

```bash
az group create -n rg-cmcsp-app -l swedencentral
```

---

## Step 6 – Deploy export storage (rg-cmcsp-app)

Choose a globally unique storage account name (lowercase, 3–24 chars, no hyphens):

```bash
STORAGE_NAME="cmcspexports$(openssl rand -hex 3)"

az deployment group create \
  -g rg-cmcsp-app \
  --template-file infra/modules/storage.bicep \
  --parameters \
    storageAccountName="$STORAGE_NAME" \
    location=swedencentral \
    tags='{"project":"cmcsp","application":"csp-cost-dashboard","environment":"production","managed-by":"bicep","owner":"platform-engineering","cost-center":"cloud-ops"}'

# Save the storage account resource ID for later steps
STORAGE_ID=$(az deployment group show -g rg-cmcsp-app -n storage \
  --query "properties.outputs.storageAccountResourceId.value" -o tsv)

STORAGE_URI=$(az deployment group show -g rg-cmcsp-app -n storage \
  --query "properties.outputs.storageAccountUri.value" -o tsv)

echo "Storage account ID:  $STORAGE_ID"
echo "Storage account URI: $STORAGE_URI"
```

---

## Step 7 – Set up Cost Management Exports

### 7a – Subscription scope export (one per customer subscription)

```bash
# Future date at least 1 minute from now
EXPORT_START=$(date -u -d "+5 minutes" '+%Y-%m-%dT%H:%M:%SZ')

az deployment sub create \
  --location swedencentral \
  --template-file infra/modules/export-sub.bicep \
  --parameters \
    exportName="daily-cost-export" \
    storageAccountResourceId="$STORAGE_ID" \
    recurrenceFrom="$EXPORT_START" \
    location=swedencentral

# Get the export managed identity's principal ID
EXPORT_MI=$(az deployment sub show \
  --name export-sub \
  --query "properties.outputs.managedIdentityPrincipalId.value" -o tsv)

echo "Export MI principal ID: $EXPORT_MI"
```

### 7b – Grant export MI write access to storage

```bash
az deployment group create \
  -g rg-cmcsp-app \
  --template-file infra/modules/storage.bicep \
  --parameters \
    storageAccountName="$STORAGE_NAME" \
    exportManagedIdentityPrincipalId="$EXPORT_MI"
```

### 7c – (Optional) Billing account scope export

Requires Billing Account Owner / Contributor in Partner Center.

```bash
BILLING_ACCOUNT_ID="<your-billing-account-id>"   # from: az billing account list
EXPORT_START=$(date -u -d "+5 minutes" '+%Y-%m-%dT%H:%M:%SZ')

# Generate SAS token (2-year expiry)
SAS_EXPIRY=$(date -u -d "+2 years" '+%Y-%m-%dT%H:%MZ')
SAS=$(az storage container generate-sas \
  --account-name "$STORAGE_NAME" \
  --name cost-exports \
  --permissions acwl \
  --expiry "$SAS_EXPIRY" \
  --auth-mode login --as-user --output tsv)

az deployment tenant create \
  --location swedencentral \
  --template-file infra/modules/export-billing.bicep \
  --parameters \
    billingAccountId="$BILLING_ACCOUNT_ID" \
    exportName="daily-billing-export" \
    storageAccountResourceId="$STORAGE_ID" \
    sasToken="$SAS" \
    recurrenceFrom="$EXPORT_START"
```

> **Note:** The billing export will produce its first file after the scheduled run time.
> You can trigger it manually in **Azure Portal → Cost Management → Exports → Run now**.
>
> After clicking **Run now**, expect:
> - **1–10 minutes** for the export job to complete and CSVs to appear in blob storage
> - **Up to 60 minutes** before the app serves the new data (cache TTL)  
>   To skip the wait, restart the container revision:
>   ```bash
>   az containerapp revision restart -n cmcsp -g rg-cmcsp-app --revision "$(az containerapp show -n cmcsp -g rg-cmcsp-app --query 'properties.latestRevisionName' -o tsv)"
>   ```
>   This triggers `CacheWarmupService` which reads the fresh blobs within seconds of startup.

---

## Step 8 – Deploy application infrastructure (rg-cmcsp-app)

Choose globally unique names for ACR (alphanumeric only) and Key Vault:

```bash
ACR_NAME="cmcspacr$(openssl rand -hex 3)"
KV_NAME="kv-cmcsp-$(openssl rand -hex 3)"

az deployment group create \
  -g rg-cmcsp-app \
  --template-file infra/modules/app.bicep \
  --parameters \
    appName=cmcsp \
    acrName="$ACR_NAME" \
    keyVaultName="$KV_NAME" \
    location=swedencentral \
    tags='{"project":"cmcsp","application":"csp-cost-dashboard","environment":"production","managed-by":"bicep","owner":"platform-engineering","cost-center":"cloud-ops"}'

# Save outputs
APP_MI=$(az deployment group show -g rg-cmcsp-app -n app \
  --query "properties.outputs.containerAppPrincipalId.value" -o tsv)

ACR_SERVER=$(az deployment group show -g rg-cmcsp-app -n app \
  --query "properties.outputs.acrLoginServer.value" -o tsv)

KV_URI=$(az deployment group show -g rg-cmcsp-app -n app \
  --query "properties.outputs.keyVaultUri.value" -o tsv)

APP_FQDN=$(az deployment group show -g rg-cmcsp-app -n app \
  --query "properties.outputs.containerAppFqdn.value" -o tsv)

echo "Container App MI:  $APP_MI"
echo "ACR login server:  $ACR_SERVER"
echo "Key Vault URI:     $KV_URI"
echo "App FQDN:          $APP_FQDN"
```

---

## Step 9 – Grant Container App MI access to storage and subscriptions

```bash
# Storage Blob Data Reader (read export CSVs + large cache blobs)
az role assignment create --assignee "$APP_MI" \
  --role "Storage Blob Data Reader" --scope "$STORAGE_ID"

# Storage Table Data Contributor (read/write small cache entries)
az role assignment create --assignee "$APP_MI" \
  --role "Storage Table Data Contributor" --scope "$STORAGE_ID"

# User Access Administrator scoped to the storage account
# Required so ExportProvisioningService can automatically grant Storage Blob Data Contributor
# to each new export resource MI when a subscription is added through the UI.
az role assignment create --assignee "$APP_MI" \
  --role "User Access Administrator" --scope "$STORAGE_ID"

# (Optional) Cost Management Contributor on each subscription — only needed for Query API mode
# when NOT using automated export provisioning through the UI.
az role assignment create --assignee "$APP_MI" \
  --role "Cost Management Contributor" \
  --scope "/subscriptions/<subscription-id>"
```

Also re-deploy `storage.bicep` with the Container App MI principal ID so Bicep manages the
role assignments declaratively (avoids drift):

```bash
az deployment group create \
  -g rg-cmcsp-app \
  --template-file infra/modules/storage.bicep \
  --parameters \
    storageAccountName="$STORAGE_NAME" \
    exportManagedIdentityPrincipalId="$EXPORT_MI" \
    appManagedIdentityPrincipalId="$APP_MI"
```

---

## Step 10 – Store secrets in Key Vault

```bash
TENANT_ID=$(az account show --query tenantId -o tsv)

# Store the Entra App secrets (for Query API mode)
az keyvault secret set --vault-name "$KV_NAME" \
  --name "AzureCostManagement--TenantId"     --value "$TENANT_ID"

az keyvault secret set --vault-name "$KV_NAME" \
  --name "AzureCostManagement--ClientId"     --value "$APP_ID"

az keyvault secret set --vault-name "$KV_NAME" \
  --name "AzureCostManagement--ClientSecret" --value "$SECRET"

# Store subscription IDs (colon not allowed in KV names — use double dash)
az keyvault secret set --vault-name "$KV_NAME" \
  --name "AzureCostManagement--SubscriptionIds--0" \
  --value "<customer-subscription-id-1>"
```

> **Note:** The Container App is granted **Key Vault Secrets User** and **Key Vault Secrets Officer** by `app.bicep`.
> `Secrets User` allows reading the `ClientSecret` at runtime.
> `Secrets Officer` allows `SubscriptionStoreService` to write the `CmCSP--UserSubscriptionIds` secret
> whenever subscriptions are added or removed via the UI, so they survive container restarts and scale-out.

---

## Step 11 – Build and push the container image

> **Using `azd` (recommended):** `azd deploy` handles Steps 11 and 12 together —
> build, push (no Docker required), digest resolution, and Container App update with SHA-pinned image.
> See the [Automated deployment](#automated-deployment-recommended) section above.

The `.NET 10 SDK` can build and push a container image directly — no Dockerfile or Docker daemon needed.
This uses the `ContainerBaseImage`, `ContainerImageName`, and `ContainerPort` properties already set in `CmCSP.csproj`.

```bash
# Log in to ACR first
az acr login --name "$ACR_NAME"

# Build the app and push the container image to ACR in one step
dotnet publish CmCSP.csproj \
  --configuration Release \
  --os linux --arch x64 \
  /t:PublishContainer \
  -p:ContainerRegistry="$ACR_SERVER" \
  -p:ContainerRepository=cmcsp \
  -p:ContainerImageTags=latest
```

To also resolve the digest and pin it (what `azd deploy` does automatically):

```bash
DIGEST=$(az acr manifest show-metadata \
  --registry "$ACR_NAME" \
  --name "cmcsp:latest" \
  --query digest -o tsv)

PINNED_IMAGE="$ACR_SERVER/cmcsp@$DIGEST"
echo "Pinned image: $PINNED_IMAGE"
```

---

## Step 12 – Update the Container App with the real image and configuration

```bash
TENANT_ID=$(az account show --query tenantId -o tsv)

# 12a. Set non-sensitive environment variables
az containerapp update \
  -n cmcsp -g rg-cmcsp-app \
  --image "$ACR_SERVER/cmcsp:latest" \
  --set-env-vars \
    "ASPNETCORE_ENVIRONMENT=Production" \
    "ASPNETCORE_URLS=http://+:8080" \
    "AzureCostManagement__TenantId=$TENANT_ID" \
    "AzureCostManagement__ClientId=$APP_ID" \
    "AzureCostManagement__ExportBlob__Enabled=true" \
    "AzureCostManagement__ExportBlob__StorageAccountUri=$STORAGE_URI" \
    "AzureCostManagement__ExportBlob__StorageAccountResourceId=$STORAGE_ID" \
    "AzureCostManagement__ExportBlob__ContainerName=cost-exports" \
    "AzureCostManagement__ExportBlob__BlobPrefix=exports" \
    "AzureCostManagement__AzureCache__Enabled=true" \
    "AzureCostManagement__AzureCache__StorageAccountUri=$STORAGE_URI" \
    "AzureCostManagement__AzureCache__TableName=cmcspcache" \
    "AzureCostManagement__AzureCache__CacheContainerName=cmcspcache" \
    "AzureCostManagement__ApiDailyRefreshHourUtc=1" \
    "KeyVaultUri=$KV_URI"

# NOTE (Phase 4 data platform): when SQL + Azure Managed Redis are provisioned, the
# `azd` postprovision hook DISABLES the storage cache above
# (`AzureCostManagement__AzureCache__Enabled=false`) and instead wires Redis + SQL:
#     "AzureCostManagement__Redis__Enabled=true" \
#     "AzureCostManagement__Redis__HostName=<name>.<region>.redis.azure.net" \
#     "AzureCostManagement__Redis__Port=10000" \
#     "ConnectionStrings__Sql=Server=tcp:<srv>.database.windows.net,1433;Database=cmcsp;Authentication=Active Directory Default;Encrypt=True;"
# Both Redis and SQL authenticate with the Container App managed identity (no keys/secrets).

# 12b. Wire the ClientSecret from Key Vault as a Container App secret
#      The Container App's Managed Identity must have Key Vault Secrets User (granted by app.bicep).
#      The KV secret name must be: CmCSP--ClientSecret
az containerapp secret set -n cmcsp -g rg-cmcsp-app \
  --secrets "client-secret=keyvaultref:$KV_URI/secrets/CmCSP--ClientSecret,identityref:system"

az containerapp update -n cmcsp -g rg-cmcsp-app \
  --set-env-vars "AzureCostManagement__ClientSecret=secretref:client-secret"
```

> **Why is `ClientSecret` required in blob mode?**  
> The Container App's Managed Identity can read blob storage and Key Vault directly. However, for CSP resellers to query customer subscriptions via the Azure Cost Management Query API (used on first startup before any blobs exist, and for the daily background refresh), the request must be made with an Entra app credential (`ClientSecret`) scoped to the CSP reseller tenant. Managed Identity alone does not have cross-tenant Cost Management rights.

---

## Step 13 – Verify the deployment

```bash
echo "Dashboard URL: https://$APP_FQDN"
```

Open the URL in a browser. You should see:

1. The **loading banner** appear (Cost by Service / Resource Groups / Tag Chargeback chips showing ⟳).
2. The banner update to ✓ for each dataset as blobs are read and cached.
3. Charts and KPI cards populate on the dashboard pages.
4. A **subscription scope badge** on each analytics page showing `selected` vs `with data` subscriptions.

If the loading banner shows **✗ fetch failed**, check:
- Application logs: `az containerapp logs show -n cmcsp -g rg-cmcsp-app --follow`
- Cost export has run at least once (Portal → Cost Management → Exports → check last run time)
- Container App MI has `Storage Blob Data Reader` on the storage account (Step 9)

---

## Step 14 – Add more subscriptions

For each additional customer subscription:

1. **Enable cost visibility** in Partner Center (Step 2).
2. **Assign Cost Management Contributor** to the Entra App SP (enables both the Query API and automated export creation):
   ```bash
   az role assignment create --assignee "$APP_ID" \
     --role "Cost Management Contributor" \
     --scope "/subscriptions/<new-subscription-id>"
   ```
3. **Assign Reader** to the Entra App SP (required for the Advisor page):
   ```bash
   az role assignment create --assignee "$APP_ID" \
     --role "Reader" \
     --scope "/subscriptions/<new-subscription-id>"
   ```
4. **Add the subscription ID** via the **Dashboard UI (recommended)**:
   Open the app, go to the **Home** page, expand **Manage Subscriptions**, and paste or type the GUID.
   The subscription display name is resolved automatically and shown in the chip.
   The ID is persisted to **Key Vault** (secret `CmCSP--UserSubscriptionIds`) so it survives container restarts.

   When running in blob export mode (`ExportBlob:Enabled = true`), the app will **automatically**:
   - Create the `cmcsp-daily-export` Cost Management export on the new subscription.
   - Grant `Storage Blob Data Contributor` to the export's managed identity on the storage account.

  The action is traced end-to-end with a single correlation ID across log lines from:
  - `Home[...]`
  - `SubscriptionStore[...]`
  - `MainLayout[...]`
  - `ExportProvisioning[...]`

     Example trace (same correlation ID across all steps):

     ```text
     2026-05-27T08:14:02.100Z info: CmCSP.Components.Pages.Home[0]
       Home[9f9b4e8f2e8a4f58ac9cc2b8f70c5a7a]: invalidating cost cache and broadcasting refresh after subscription change (active count 2).
     2026-05-27T08:14:02.121Z info: CmCSP.Services.SubscriptionStoreService[0]
       SubscriptionStore[9f9b4e8f2e8a4f58ac9cc2b8f70c5a7a]: added 1 subscription(s). Active total now 2.
     2026-05-27T08:14:02.145Z info: CmCSP.Components.Layout.MainLayout[0]
       MainLayout[9f9b4e8f2e8a4f58ac9cc2b8f70c5a7a]: subscriptions changed (active count 2). Invalidating cost cache and broadcasting state refresh.
     2026-05-27T08:14:02.502Z info: CmCSP.Services.ExportProvisioningService[0]
       ExportProvisioning[9f9b4e8f2e8a4f58ac9cc2b8f70c5a7a]: provisioning cost export for subscription 11111111-2222-3333-4444-555555555555
     2026-05-27T08:14:03.983Z info: CmCSP.Services.ExportProvisioningService[0]
       ExportProvisioning[9f9b4e8f2e8a4f58ac9cc2b8f70c5a7a]: created/updated export 'cmcsp-daily-export' on 11111111-2222-3333-4444-555555555555; export MI principal: 66666666-7777-8888-9999-aaaaaaaaaaaa
     2026-05-27T08:14:04.427Z info: CmCSP.Services.ExportProvisioningService[0]
       ExportProvisioning[9f9b4e8f2e8a4f58ac9cc2b8f70c5a7a]: granted Storage Blob Data Contributor to export MI 66666666-7777-8888-9999-aaaaaaaaaaaa on /subscriptions/.../resourceGroups/rg-cmcsp-app/providers/Microsoft.Storage/storageAccounts/cmcspexportsabc123
     ```

   No CLI commands are needed for the export setup. The first export blob will appear the following day.

   You can also import a `.csv` or `.txt` file — any GUIDs found are extracted automatically.

   **Alternative – Re-run `azd provision`** (makes the IDs permanent in config):
   ```pwsh
   azd env set CMCSP_SUBSCRIPTION_IDS "<sub-id-1>,<sub-id-2>,<new-sub-id>"
   azd provision
   ```

   **Alternative – Direct `az` command**:
   ```bash
   az containerapp update -n cmcsp -g rg-cmcsp-app \
     --set-env-vars "AzureCostManagement__SubscriptionIds__1=<new-subscription-id>"
   ```

---

## Architecture diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│  rg-cmcsp-exports (swedencentral)                                       │
│                                                                         │
│  ┌──────────────────────────────────┐                                   │
│  │  Storage Account                │◄── Export MI writes daily CSVs    │
│  │  ├── blob: cost-exports/        │◄── Container App reads CSVs       │
│  │  ├── blob: cmcspcache/          │◄── Container App writes/reads     │
│  │                              │    large cache blobs               │
│  │                              │◄── Collect Job writes cost facts  │
│  └── table: cmcspcache         │    + cache (nightly 02:00 UTC)     │
│  └──────────────────────────────────┘                                   │
│                                                                         │
│  Microsoft.CostManagement/exports  ──► (subscription scope, per sub)   │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│  rg-cmcsp-app (swedencentral)                                           │
│                                                                         │
│  ┌─────────────┐   ┌──────────────┐   ┌──────────────────────────┐    │
│  │  ACR        │   │  Key Vault   │   │  Container Apps Env      │    │
│  │  cmcsp:*    │   │  secrets     │   │  ┌────────────────────┐  │    │
│  │  cmcsp-     │   └──────┬───────┘   │  │  Container App     │  │    │
│  │  collect:* │          │  KV       │  │  cmcsp             │  │    │
│  └──────┬──────┘          │  Secrets  │  │  SystemAssigned MI │  │    │
│         │  AcrPull        │  User     │  └────────────────────┘  │    │
│         └────────────┬────┘           │  ┌────────────────────┐  │    │
│                      └──────────────► │  │  Job (0 2 cron)    │  │    │
│                                       │  │  cmcsp-collect     │  │    │
│                                       │  │  SystemAssigned MI │  │    │
│                                       │  └────────────────────┘  │    │
│                                       └──────────────────────────┘    │
│                                                                         │
│  Log Analytics ──► Container App + Job logs                            │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## Troubleshooting

For runtime subscription add/remove diagnostics, tail logs and filter on one correlation ID:

```bash
az containerapp logs show -n cmcsp -g rg-cmcsp-app --follow | grep -E "Home\[|SubscriptionStore\[|MainLayout\[|ExportProvisioning\["
```

| Symptom | Likely cause | Fix |
|---|---|---|
| HTTP 400 `IndirectCostDisabled` | Cost visibility not enabled | Step 2 – Partner Center |
| HTTP 403 on Cost Management API | Entra app SP missing Cost Management Reader | Step 4 – assign role |
| HTTP 403 on blob read | Container App MI missing Storage Blob Data Reader | Step 9 |
| HTTP 403 on table read/write | Container App MI missing Storage Table Data Contributor | Step 9 |
| No CSV files in container | Export hasn't run yet | Portal → Cost Management → Exports → Run now |
| Data appears stale after forced export | Cache hasn't expired yet | Restart revision to trigger `CacheWarmupService` (see Step 7 note) |
| Image pull failure | ACR pull role not yet propagated | Wait 2 min; AcrPull assigned in app.bicep |
| Key Vault 403 | Container App MI missing Key Vault Secrets User | Check app.bicep deployment |
| All chips show ✗ immediately | `ClientSecret` not wired; Query API auth failing | Check Step 12b; confirm KV secret `CmCSP--ClientSecret` exists |
| All chips show ✗ on startup only | No blobs yet AND `ClientSecret` missing | Either add `ClientSecret` (Step 12b) or wait for first export run |
| Collector job not running | Job not provisioned | Run `azd provision` to provision the `cmcsp-collect` job |
| Collector job fails (403) | MI missing storage roles | Re-run `azd provision`; `main.bicep` grants Table Contributor + Blob Contributor |
| Daily refresh not updating data | `ApiDailyRefreshHourUtc` set but `ClientSecret` absent | Set `ClientSecret` via KV ref; without it, `CostManagementService` cannot auth |
| Budgets page shows “no budgets found” | No subscription-scope budgets exist in Azure | Create a budget in Portal: Cost Management → Budgets, per subscription |
| Budgets page shows 403 error | Entra app SP missing Cost Management Reader | Same role used for cost data – confirm Step 4 || Budgets page shows 0 current spend | CSP `currentSpend` API field returned null/0 | Expected for CSP — spend is automatically computed from `cm_main` cost rows instead; no action needed |
| Login redirect loop | Redirect URI not registered in Entra app | Add `https://<fqdn>/signin-oidc` to the app's Web platform redirect URIs (Step 3b) |
| `AADSTS50011` error on sign-in | Redirect URI mismatch | Check the exact URI (including trailing slash) matches what was registered in Step 3b |
| `AADSTS700054` error on sign-in | `response_type 'id_token' is not enabled` | The app uses pure authorization code flow — **do not** enable ID tokens under Implicit grant in the app registration. This error appears only if ID token implicit flow is explicitly requested; the app overrides `ResponseType = "code"` in `Program.cs` to prevent it. If you see this after a code change, verify the `Configure<OpenIdConnectOptions>` call is still present. |
| `AADSTS700016` error on sign-in | Wrong `ClientId` configured | Verify `AzureCostManagement:ClientId` matches the Application ID in Entra |
| Login page shown before app loads | `TenantId` or `ClientId` not set | Set them via user-secrets (local) or Container App env vars / Key Vault (production) || Tag Chargeback shows no tagged data | No tagged resources, or CSP tag API limitation | Verify tags exist on resources; blob exports are required for reliable tag data |
| Date range picker shows wrong range | `cm_main` cache empty on first render | Click the “Fit to data” (⊡) button after the loading chips turn ✓ |
| `SubscriptionStoreService` startup error | DI misconfiguration | Ensure `CostManagementOptions` is registered before hosted services |
| Dashboard shows data only for current month; older months blank | Export uses `MonthToDate` — no historical blobs exist | Set `azd env set HISTORICAL_MONTHS 12` (with `EXPORT_SCOPE=subscription`) and re-run `azd provision` to backfill prior months |
| Currency not normalised correctly in blob mode | CSP export uses `billingCurrency` column instead of `billingCurrencyCode` | Handled automatically — parser checks `billingcurrencycode`, `currency`, `billingcurrency` in order |
| MTD / YTD figures are inflated (e.g. ~11× expected) | Azure `MonthToDate` exports are cumulative — a new blob is written each day containing all data from day 1; the old code summed across all blobs, counting early days many times | Fixed in `BlobCostManagementService` (merge-with-replacement across blobs). Deploy the latest image; then click **Refresh Data** in the nav sidebar (or restart the revision) to clear the cached inflated values |
| Need to force-refresh data without waiting 60 min | In-memory cache hasn't expired | Click **Refresh Data** in the nav sidebar — it invalidates the cache and triggers an immediate re-fetch on all open pages |
| Subscription added in UI but export not created | Provisioning failed for that subscription | Use correlation-ID trace (`Home[...]`, `SubscriptionStore[...]`, `MainLayout[...]`, `ExportProvisioning[...]`) to find the failing step and HTTP error body |
| Advisor page shows "no recommendations found" | Reader role not yet assigned, or no actionable recommendations exist | Assign **Reader** to the Entra App SP on each subscription (Step 4c); allow 5 min for role propagation. New subscriptions may have no Advisor data for up to 24 h |
| Advisor page shows 403 error | Entra App SP missing Reader role | Confirm Step 4a — `Cost Management Contributor` supersedes Reader; does not cover the Advisor API |
| Advisor savings are shown in wrong currency | Exchange rate for subscription billing currency not configured | Add the missing currency code and rate to `ExchangeRates` in `appsettings.json` |
