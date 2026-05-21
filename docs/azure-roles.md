# Azure Role Assignments for CmCSP

This document describes every Azure RBAC role required to run CmCSP, covering all three
functional areas: **Query API access**, **Cost Export setup**, and **Application infrastructure**.

See [docs/csp-deployment-guide.md](csp-deployment-guide.md) for the step-by-step deployment
walkthrough that shows exactly when and how to assign each role.

---

## Overview

CmCSP uses three separate Azure identities, each with the minimum permissions needed:

| Identity | Type | Purpose |
|---|---|---|
| **Entra App SP** | Service Principal (app registration) | Call the Cost Management Query API and Azure Advisor API |
| **Export MI** | SystemAssigned MI on the export resource | Write daily CSVs to Blob Storage |
| **Container App MI** | SystemAssigned MI on the Container App | Read blobs/tables + pull from ACR + read Key Vault secrets |

---

## 1 – Entra App Service Principal (Query API mode)

Used when `AzureCostManagement:ExportBlob:Enabled = false`.

### Subscription scope

| Role | Role ID | Scope | Why |
|---|---|---|---|
| **Cost Management Reader** | `72fafed3-fadc-4a7f-a4e1-0c1b7dc0dc57` | Each target subscription | Read cost data via the Query API |

```bash
az role assignment create \
  --assignee "<app-client-id>" \
  --role "Cost Management Reader" \
  --scope "/subscriptions/<subscription-id>"
```

### Billing account scope (CSP – if querying at billing account level)

The Cost Management Query API can also target the billing account scope
(`/providers/Microsoft.Billing/billingAccounts/{id}`). This requires a higher privilege:

| Role | Assigned in | Why |
|---|---|---|
| **Billing Account Reader** | Partner Center / EA Portal | Read all subscriptions' costs under the billing account |

This cannot be assigned via standard RBAC (`az role assignment create`). It must be set in
**Partner Center → Billing → Billing account → Access control** or via the Billing REST API.

### CSP indirect cost visibility (mandatory for CSP subscriptions)

Cost data is hidden for CSP customer subscriptions by default. Regardless of RBAC, a
Partner Center Global Admin must enable it:

1. Sign in to [Partner Center](https://partner.microsoft.com)
2. Navigate to **Customers → {customer} → Service management → Azure subscriptions**
3. Enable **Cost visibility for customer** (IndirectCostEnabled)

Without this step the Query API returns HTTP 400 `IndirectCostDisabled`.

---

## 2 – Cost Management Export Setup

Applies to both `export-sub.bicep` (subscription scope) and `export-billing.bicep`
(billing account scope).

### 2a – Subscription scope export

The export resource (`Microsoft.CostManagement/exports`) uses a **SystemAssigned managed identity**
which needs write access to the storage account where CSVs are dropped.

| Role | Role ID | Scope | Who |
|---|---|---|---|
| **Storage Blob Data Contributor** | `ba92f5b4-2d11-453d-a403-e96b0029c9fe` | Storage account (app RG) | Export resource MI |

This role assignment is created automatically by `bicep/main.bicep` when you pass
`exportManagedIdentityPrincipalId`. The value is available as the `managedIdentityPrincipalId`
output from `bicep/export-sub.bicep`.

The person **deploying** the export must have at minimum:

| Role | Scope | Why |
|---|---|---|
| **Cost Management Contributor** or **Owner** | The subscription | Create the `Microsoft.CostManagement/exports` resource |
| **Contributor** | Export storage RG | Deploy `main.bicep` and create role assignments |

```bash
# Assign Cost Management Contributor on a subscription
az role assignment create \
  --assignee "<deployer-user-or-sp>" \
  --role "Cost Management Contributor" \
  --scope "/subscriptions/<subscription-id>"
```

### 2b – Billing account scope export

The billing-account export (`export-billing.bicep`) uses a SAS token instead of a managed
identity (managed identity is not supported at billing account scope).

The person deploying must have:

| Role | Where | Why |
|---|---|---|
| **Billing Account Owner** or **Contributor** | Partner Center / EA Portal | Create the export resource at billing account scope |
| **Contributor** | Export storage RG | Deploy storage + generate SAS token |

The SAS token requires the following permissions on the target container:
`acwl` — **A**dd, **C**reate, **W**rite, **L**ist.

Generate the SAS token:
```bash
END=$(date -u -d "+2 years" '+%Y-%m-%dT%H:%MZ')
SAS=$(az storage container generate-sas \
  --account-name <storageAccountName> \
  --name cost-exports \
  --permissions acwl \
  --expiry "$END" \
  --auth-mode login \
  --as-user \
  --output tsv)
```

---

## 3 – Container App Managed Identity (Application infrastructure)

The Container App runs with a **SystemAssigned managed identity**. It needs four roles:

### 3a – Read cost export blobs and write/read the app cache

| Role | Role ID | Scope | Why |
|---|---|---|---|
| **Storage Blob Data Reader** | `2a2b9908-6ea1-4ae2-8e65-a410df84e7d1` | Export storage account | Read cost export CSVs + read large cache blobs |
| **Storage Table Data Contributor** | `0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3` | Export storage account | Read and write small cache entries in Table Storage |

Both assignments are created automatically by `bicep/main.bicep` when `appManagedIdentityPrincipalId` is provided.

```bash
APP_MI=$(az deployment group show -g rg-cmcsp-app -n app \
  --query "properties.outputs.containerAppPrincipalId.value" -o tsv)

STORAGE_ID=$(az storage account show -n <storageAccountName> -g rg-cmcsp-app \
  --query id -o tsv)

az role assignment create --assignee "$APP_MI" \
  --role "Storage Blob Data Reader" --scope "$STORAGE_ID"

az role assignment create --assignee "$APP_MI" \
  --role "Storage Table Data Contributor" --scope "$STORAGE_ID"
```

### 3b – Pull images from ACR

| Role | Role ID | Scope | Why |
|---|---|---|---|
| **AcrPull** | `7f951dda-4ed3-4680-a7ca-43fe172d538d` | Azure Container Registry | Pull the CmCSP container image without admin credentials |

Created automatically by `bicep/app.bicep`.

### 3c – Read secrets from Key Vault

| Role | Role ID | Scope | Why |
|---|---|---|---|
| **Key Vault Secrets User** | `4633458b-17de-408a-b874-0445c86b69e6` | Key Vault | Read `ClientSecret`, connection strings, and other runtime secrets |

Created automatically by `bicep/app.bicep`.

### 3d – (Optional) Call the Cost Management Query API

If running in Query API mode (`ExportBlob:Enabled = false`) and you want to use managed
identity instead of a client secret, assign **Cost Management Reader** to the Container App MI
on each target subscription instead of (or in addition to) the Entra App SP.

```bash
az role assignment create \
  --assignee "$APP_MI" \
  --role "Cost Management Reader" \
  --scope "/subscriptions/<subscription-id>"
```

---

## 4 – Azure Advisor Recommendations (Entra App SP)

The Advisor Cost Savings page (`/advisor`) fetches recommendations from the Azure Advisor REST API.
This requires the **Reader** role on each subscription — the existing `Cost Management Reader` role
does **not** cover the `Microsoft.Advisor/recommendations/read` action.

| Role | Role ID | Scope | Why |
|---|---|---|---|
| **Reader** | `acdd72a7-3385-48ef-bd42-f606fba81ae7` | Each target subscription | Grants `*/read`, which includes `Microsoft.Advisor/recommendations/read` |

> **Scope note:** `Reader` is a broader grant than `Cost Management Reader` — it provides read
> access to all resource types in the subscription. Consider informing customers before assigning
> it to the service principal.

```bash
az role assignment create \
  --assignee "<app-client-id>" \
  --role "Reader" \
  --scope "/subscriptions/<subscription-id>"
```

Repeat for every subscription in `SubscriptionIds`.

If the Advisor page shows an empty state with no recommendations, confirm that:
1. The **Reader** role assignment has propagated (allow up to 5 minutes).
2. The subscription has resources that Advisor has analysed (new subscriptions may show no recommendations for up to 24 hours).

---

## 5 – Summary table

| Identity | Role | Scope | Assigned by |
|---|---|---|---|
| Entra App SP | Cost Management Reader | Each subscription | `az role assignment create` |
| Entra App SP | Reader | Each subscription | `az role assignment create` (Advisor page) |
| Entra App SP | Billing Account Reader | Billing account | Partner Center / Billing API |
| *(CSP admin)* | IndirectCostEnabled | Customer subscription | Partner Center UI |
| Export resource MI | Storage Blob Data Contributor | Export storage account | `bicep/main.bicep` |
| Container App MI | Storage Blob Data Reader | Export storage account | `bicep/main.bicep` |
| Container App MI | Storage Table Data Contributor | Export storage account | `bicep/main.bicep` |
| Container App MI | AcrPull | Container Registry | `bicep/app.bicep` |
| Container App MI | Key Vault Secrets User | Key Vault | `bicep/app.bicep` |
| Container App MI | Cost Management Reader | Each subscription | Manual (Query API mode only) |
| Deployer | Cost Management Contributor | Subscription | Pre-requisite |
| Deployer | Contributor | Resource groups | Pre-requisite |

---

## Mode 1 – Query API (Entra App Registration)

This is the development / out-of-the-box mode. A service principal (Entra app registration) calls the Cost Management REST API directly.

### Required permission

| Scope | Role | Role ID | Who needs it |
|---|---|---|---|
| Each target subscription | **Cost Management Reader** | `72fafed3-fadc-4a7f-a4e1-0c1b7dc0dc57` | The Entra app service principal |

### How to assign in the Azure Portal

1. Open **Azure Portal → Subscriptions → {subscription name}**
2. Click **Access control (IAM) → Add → Add role assignment**
3. Role: **Cost Management Reader**
4. Members: select **User, group, or service principal** → search for your app registration name
5. Save. Repeat for every subscription in `SubscriptionIds`.

### How to assign with Azure CLI

```bash
# Run once per subscription
az role assignment create \
  --assignee "<application-client-id>" \
  --role "Cost Management Reader" \
  --scope "/subscriptions/<subscription-id>"
```

### CSP-specific requirement

For Cloud Solution Provider (CSP) subscriptions, the cost data is hidden by default. A **Partner Center** administrator must enable it before any reader can see costs:

1. Sign in to [Partner Center](https://partner.microsoft.com)
2. Navigate to **Customers → {customer name} → Service management → Azure subscriptions**
3. Enable **Cost visibility for customer** (also called **Indirect cost visibility**)

Without this, the Query API returns HTTP 400 with `"IndirectCostDisabled"` regardless of the RBAC role assigned.

---

## Mode 2 – Blob Exports

This mode involves two separate identities with different roles: one that **writes** export files (the export managed identity), and one that **reads** them (the application identity).

### Identity 1 – Export Managed Identity (writer)

The `Microsoft.CostManagement/exports` resource in `bicep/export-sub.bicep` is deployed with a `SystemAssigned` managed identity. This identity needs write access to the storage account.

| Scope | Role | Role ID | Who needs it |
|---|---|---|---|
| Storage account | **Storage Blob Data Contributor** | `ba92f5b4-2d11-453d-a403-e96b0029c9fe` | The export resource's managed identity |

This role assignment is handled automatically by `bicep/main.bicep` when you pass `exportManagedIdentityPrincipalId`. The principal ID is available as an output from `export-sub.bicep` (`managedIdentityPrincipalId`).

**Typical two-step Bicep deploy:**

```bash
# Step 1: Deploy storage account (no principal ID yet)
az deployment group create \
  --resource-group rg-cmcsp \
  --template-file bicep/main.bicep \
  --parameters storageAccountName=cmcspcostexports

# Step 2: Deploy the export (this creates the managed identity)
az deployment sub create \
  --location swedencentral \
  --template-file bicep/export-sub.bicep \
  --parameters \
    exportName=daily-cost-export \
    storageAccountResourceId="<storage-account-resource-id>" \
    recurrenceFrom="2026-01-01T02:00:00Z"

# Step 3: Re-deploy main.bicep with the principal ID to grant the write role
PRINCIPAL_ID=$(az deployment sub show \
  --name export-sub \
  --query "properties.outputs.managedIdentityPrincipalId.value" -o tsv)

az deployment group create \
  --resource-group rg-cmcsp \
  --template-file bicep/main.bicep \
  --parameters \
    storageAccountName=cmcspcostexports \
    exportManagedIdentityPrincipalId="$PRINCIPAL_ID"
```

### Identity 2 – Application identity (reader)

The CmCSP Blazor application reads blobs from the same storage account. It needs read-only access.

| Scope | Role | Role ID | Who needs it |
|---|---|---|---|
| Storage account | **Storage Blob Data Reader** | `2a2b9908-6ea1-4ae2-8e65-a410df84e7d1` | The identity the app authenticates with |

The identity used depends on where the app runs:

#### Option A – Azure App Service / Container Apps (recommended for production)

Enable the **System-assigned managed identity** on the App Service, then assign `Storage Blob Data Reader` to it.

```bash
# Enable managed identity on the App Service
az webapp identity assign \
  --name cmcsp-dashboard \
  --resource-group rg-cmcsp

# Get the principal ID
PRINCIPAL_ID=$(az webapp identity show \
  --name cmcsp-dashboard \
  --resource-group rg-cmcsp \
  --query principalId -o tsv)

# Assign Storage Blob Data Reader
az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Storage Blob Data Reader" \
  --scope "<storage-account-resource-id>"
```

Set `AzureCostManagement:ExportBlob:StorageAccountUri` to `https://<account>.blob.core.windows.net`. The app uses `DefaultAzureCredential` which picks up the managed identity automatically. No `ClientSecret` or `ConnectionString` is needed.

#### Option B – Local development with `az login`

Run `az login` in a terminal. `DefaultAzureCredential` will pick up your signed-in account. Your Azure user account needs `Storage Blob Data Reader` on the storage account.

```bash
az role assignment create \
  --assignee "<your-azure-user-object-id>" \
  --role "Storage Blob Data Reader" \
  --scope "<storage-account-resource-id>"
```

Set `AzureCostManagement:ExportBlob:StorageAccountUri` via `dotnet user-secrets`.

#### Option C – Local development with a connection string

If you don't have `az login` available, set `AzureCostManagement:ExportBlob:ConnectionString` via `dotnet user-secrets`:

```bash
dotnet user-secrets set "AzureCostManagement:ExportBlob:ConnectionString" "<connection-string>"
```

Get the connection string from **Azure Portal → Storage account → Access keys → Connection string**. Never commit this value.

### Billing scope export (export-billing.bicep)

The billing scope export (`bicep/export-billing.bicep`) targets a CSP Billing Account and requires:

- **Billing Account Owner** or **Contributor** to deploy the export resource (tenant-level Bicep deployment requires elevated permissions)
- The SAS token passed at deploy time must grant `acwl` (add, create, write, list) on the container
- The application still only needs `Storage Blob Data Reader` to read the resulting files — the same as above

---

## Summary table

| Identity | Role | Scope | Required for |
|---|---|---|---|
| Entra App SP | Cost Management Reader | Per subscription | Query API mode |
| Export managed identity | Storage Blob Data Contributor | Storage account | Writing export files (blob mode) |
| App Service MI / developer | Storage Blob Data Reader | Storage account | Reading export files (blob mode) |
| *(CSP Partner Center admin)* | Indirect cost visibility | Customer subscription | Either mode — unlocks CSP billing data |

---

## Principle of least privilege in production

The recommended production setup eliminates the Entra App client secret entirely:

```
App Service (SystemAssigned MI)
  ├── Cost Management Reader    → each subscription   [for Query API fallback]
  └── Storage Blob Data Reader  → storage account     [for Blob Export mode]

Export resource (SystemAssigned MI)
  └── Storage Blob Data Contributor → storage account [for writing export files]
```

With this setup no secrets are stored anywhere — only managed identity object IDs and resource IDs in configuration.
