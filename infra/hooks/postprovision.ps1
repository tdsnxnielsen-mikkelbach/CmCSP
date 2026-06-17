<#
.SYNOPSIS
    azd postprovision hook for CmCSP.

.DESCRIPTION
    Runs after `azd provision` (infra/main.bicep). Replaces Phases 5-7 of the
    legacy scripts/deploy.ps1:

      5. Seed Key Vault secrets (identity + optional Cost Details settings).
      6. Wire Container App + cache cleanup Job environment variables, including
         the Key Vault reference for the client secret.
      7. Deploy the Cost Management export at the scope selected by EXPORT_SCOPE:
            subscription → bicep/export-sub.bicep    (managed-identity auth)
            billing      → bicep/export-billing.bicep (SAS-token auth, tenant scope)
            none         → skip (default)

    All inputs come from azd: bicep outputs and `azd env set` values are exposed
    to hooks as environment variables.

.NOTES
    Configure with azd before provisioning, e.g.:
      azd env set CMCSP_TENANT_ID        <tenant-guid>
      azd env set CMCSP_CLIENT_ID        <app-client-id>
      azd env set CMCSP_CLIENT_SECRET    <app-client-secret>   # sensitive
      azd env set CMCSP_SUBSCRIPTION_IDS <guid1,guid2,...>

      # Export scope switch (subscription | billing | none)
      azd env set EXPORT_SCOPE           subscription
      azd env set BILLING_ACCOUNT_ID     <billing-account-id>  # billing scope only
      azd env set EXPORT_NAME            cmcsp-daily-export
      azd env set HISTORICAL_MONTHS      0

      # Optional Cost Details API feature
      azd env set ENABLE_COST_DETAILS    true
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Helpers ───────────────────────────────────────────────────────────────────

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "───────────────────────────────────────────────" -ForegroundColor Cyan
    Write-Host "  $msg" -ForegroundColor Cyan
    Write-Host "───────────────────────────────────────────────" -ForegroundColor Cyan
}

function Get-Env([string]$name, [string]$default = '') {
    $val = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($val)) { return $default }
    return $val.Trim()
}

function Require-Env([string]$name) {
    $val = Get-Env $name
    if ([string]::IsNullOrWhiteSpace($val)) {
        Write-Error "Required azd environment variable '$name' is not set. Run: azd env set $name <value>"
    }
    return $val
}

# Backfill N prior calendar months of subscription-scope cost data (one-time exports).
function Invoke-HistoricalBackfill([string]$PrimarySub) {
    Write-Host ""
    Write-Host "  Backfilling $historicalMonths prior calendar month(s)..." -ForegroundColor Cyan
    $today     = (Get-Date).Date
    $farFuture = '2099-12-31T00:00:00Z'

    for ($m = 1; $m -le $historicalMonths; $m++) {
        $monthStart  = [datetime]::new($today.Year, $today.Month, 1).AddMonths(-$m)
        $monthEnd    = $monthStart.AddMonths(1).AddDays(-1)
        $historyName = "$exportName-hist-$($monthStart.ToString('yyyy-MM'))"

        Write-Host "  [$m/$historicalMonths] $($monthStart.ToString('yyyy-MM')) → $historyName ..."
        az deployment sub create `
            --location $location `
            --template-file (Join-Path $bicepRoot 'export-sub.bicep') `
            --only-show-errors `
            --parameters `
                "exportName=$historyName" `
                "storageAccountResourceId=$storageId" `
                "containerName=$exportContainer" `
                'rootFolderPath=exports' `
                "location=$location" `
                'timeframe=Custom' `
                "timePeriodFrom=$($monthStart.ToString('yyyy-MM-dd'))" `
                "timePeriodTo=$($monthEnd.ToString('yyyy-MM-dd'))" `
                'scheduleStatus=Inactive' `
                "recurrenceFrom=$farFuture" `
            | Out-Null

        az rest --method POST `
            --uri "https://management.azure.com/subscriptions/$PrimarySub/providers/Microsoft.CostManagement/exports/$historyName/run?api-version=2025-03-01" `
            --only-show-errors | Out-Null
        Write-Host "    Triggered." -ForegroundColor Green
    }
}

# Repo root = parent of infra/ (azd runs hooks from the azure.yaml directory,
# but resolve relative to this script so manual invocation also works).
$repoRoot  = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$bicepRoot = Join-Path $repoRoot 'bicep'

# ── Resolve inputs from azd (bicep outputs + `azd env set` values) ─────────────

$subscriptionId   = Require-Env 'AZURE_SUBSCRIPTION_ID'
$location         = Get-Env    'AZURE_LOCATION' 'swedencentral'
$appRg            = Require-Env 'AZURE_RESOURCE_GROUP'
$containerAppName = Require-Env 'CONTAINER_APP_NAME'
$cleanupJobName   = Get-Env    'CLEANUP_JOB_NAME' "$containerAppName-cleanup"
$keyVaultName     = Require-Env 'AZURE_KEY_VAULT_NAME'
$keyVaultUri      = Require-Env 'AZURE_KEY_VAULT_URI'
$storageName      = Require-Env 'STORAGE_ACCOUNT_NAME'
$storageUri       = Require-Env 'STORAGE_ACCOUNT_URI'
$storageId        = Require-Env 'STORAGE_ACCOUNT_RESOURCE_ID'
$exportContainer  = Get-Env    'EXPORT_CONTAINER_NAME' 'cost-exports'

$tenantId         = Require-Env 'CMCSP_TENANT_ID'
$clientId         = Require-Env 'CMCSP_CLIENT_ID'
$clientSecret     = Require-Env 'CMCSP_CLIENT_SECRET'
$subscriptionIds  = (Require-Env 'CMCSP_SUBSCRIPTION_IDS') -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }

$exportScope      = (Get-Env 'EXPORT_SCOPE' 'none').ToLowerInvariant()
$exportName       = Get-Env 'EXPORT_NAME' 'cmcsp-daily-export'
$historicalMonths = [int](Get-Env 'HISTORICAL_MONTHS' '0')
$billingAccountId = Get-Env 'BILLING_ACCOUNT_ID'
$enableCostDetails = (Get-Env 'ENABLE_COST_DETAILS' 'false').ToLowerInvariant() -eq 'true'

az account set --subscription $subscriptionId --only-show-errors | Out-Null

# ──────────────────────────────────────────────────────────────────────────────
# Phase 5 – Key Vault secrets
# ──────────────────────────────────────────────────────────────────────────────

Write-Step "Phase 5 – Key Vault secrets"

# Grant the current principal Key Vault Secrets Officer so we can write secrets.
$currentOid = az ad signed-in-user show --query id -o tsv 2>$null
if (-not $currentOid) { $currentOid = az account show --query 'user.name' -o tsv 2>$null }

$kvId = "/subscriptions/$subscriptionId/resourceGroups/$appRg/providers/Microsoft.KeyVault/vaults/$keyVaultName"
$kvSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
$existingKvRole = az role assignment list --assignee $currentOid --role $kvSecretsOfficerRoleId --scope $kvId --query '[].id' -o tsv 2>$null
if (-not $existingKvRole) {
    Write-Host "  Granting Key Vault Secrets Officer to current principal..."
    az role assignment create --assignee $currentOid --role $kvSecretsOfficerRoleId --scope $kvId --only-show-errors | Out-Null
    Start-Sleep -Seconds 15  # AAD propagation
}

$secrets = [ordered]@{
    'CmCSP--TenantId'        = $tenantId
    'CmCSP--ClientId'        = $clientId
    'CmCSP--ClientSecret'    = $clientSecret
    'CmCSP--SubscriptionIds' = ($subscriptionIds -join ',')
}
if ($enableCostDetails)   { $secrets['CmCSP--CostDetails--Enabled'] = 'true' }
if ($billingAccountId)    { $secrets['CmCSP--BillingAccount--BillingAccountId'] = $billingAccountId }

foreach ($name in $secrets.Keys) {
    Write-Host "  Setting secret: $name"
    az keyvault secret set --vault-name $keyVaultName --name $name --value $secrets[$name] --only-show-errors | Out-Null
}

# ──────────────────────────────────────────────────────────────────────────────
# Phase 6 – Container App + cleanup job environment variables
# ──────────────────────────────────────────────────────────────────────────────

Write-Step "Phase 6 – Container App environment variables"

# Key Vault reference consumed as a Container App secret (resolved via the app MI).
Write-Host "  Adding Key Vault secret reference 'client-secret'..."
az containerapp secret set --name $containerAppName --resource-group $appRg `
    --secrets "client-secret=keyvaultref:${keyVaultUri}secrets/CmCSP--ClientSecret,identityref:system" `
    --only-show-errors | Out-Null

$subIdsEnv = @()
for ($i = 0; $i -lt $subscriptionIds.Count; $i++) {
    $subIdsEnv += "AzureCostManagement__SubscriptionIds__$i=$($subscriptionIds[$i])"
}

$envPairs = @(
    "AzureCostManagement__TenantId=$tenantId"
    "AzureCostManagement__ClientId=$clientId"
    "AzureCostManagement__ClientSecret=secretref:client-secret"
    "AzureCostManagement__ExportBlob__Enabled=true"
    "AzureCostManagement__ExportBlob__StorageAccountUri=$storageUri"
    "AzureCostManagement__ExportBlob__StorageAccountResourceId=$storageId"
    "AzureCostManagement__ExportBlob__ContainerName=$exportContainer"
    "AzureCostManagement__ExportBlob__BlobPrefix=exports"
    "AzureCostManagement__AzureCache__Enabled=true"
    "AzureCostManagement__AzureCache__StorageAccountUri=$storageUri"
    "AzureCostManagement__AzureCache__TableName=cmcspcache"
    "AzureCostManagement__AzureCache__CacheContainerName=cmcspcache"
) + $subIdsEnv

Write-Host "  Updating Container App with $($envPairs.Count) environment variables..."
az containerapp update --name $containerAppName --resource-group $appRg --set-env-vars @envPairs --only-show-errors | Out-Null

# Cleanup job storage endpoints (table endpoint shares the account, different sub-domain).
$tableStorageUri = $storageUri -replace '\.blob\.core\.windows\.net', '.table.core.windows.net'
Write-Host "  Wiring cleanup job storage endpoints ($cleanupJobName)..."
az containerapp job update --name $cleanupJobName --resource-group $appRg `
    --set-env-vars "CACHE_TABLE_ENDPOINT=$tableStorageUri" "CACHE_BLOB_ENDPOINT=$storageUri" `
    --only-show-errors 2>$null | Out-Null

# ──────────────────────────────────────────────────────────────────────────────
# Phase 7 – Cost Management exports (scope switch)
# ──────────────────────────────────────────────────────────────────────────────

Write-Step "Phase 7 – Cost Management exports (scope: $exportScope)"

$recurrenceFrom = (Get-Date).AddDays(1).ToString('yyyy-MM-ddT00:00:00Z')

switch ($exportScope) {

    # ── Subscription scope: managed-identity auth, sub-level deployment ───────
    'subscription' {
        $primarySub = $subscriptionIds[0]
        az account set --subscription $primarySub --only-show-errors | Out-Null

        Write-Host "  Deploying subscription-scope export '$exportName'..."
        $exportOut = az deployment sub create `
            --location $location `
            --template-file (Join-Path $bicepRoot 'export-sub.bicep') `
            --only-show-errors `
            --parameters `
                "exportName=$exportName" `
                "storageAccountResourceId=$storageId" `
                "containerName=$exportContainer" `
                'rootFolderPath=exports' `
                "location=$location" `
                "recurrenceFrom=$recurrenceFrom" `
            -o json | ConvertFrom-Json

        $exportMiId = $exportOut.properties.outputs.managedIdentityPrincipalId.value
        if ($exportMiId) {
            Write-Host "  Granting Storage Blob Data Contributor to export MI ($exportMiId)..."
            # Re-run storage module with the export MI principal → declarative RBAC.
            az deployment group create `
                --resource-group $appRg `
                --template-file (Join-Path $bicepRoot 'main.bicep') `
                --mode Incremental --only-show-errors `
                --parameters `
                    "storageAccountName=$storageName" `
                    "location=$location" `
                    "exportManagedIdentityPrincipalId=$exportMiId" `
                | Out-Null
        }

        if ($historicalMonths -gt 0) {
            Invoke-HistoricalBackfill -PrimarySub $primarySub
        }

        az account set --subscription $subscriptionId --only-show-errors | Out-Null
    }

    # ── Billing account scope: SAS-token auth, tenant-level deployment ────────
    'billing' {
        if (-not $billingAccountId) {
            Write-Error "EXPORT_SCOPE=billing requires BILLING_ACCOUNT_ID. Run: azd env set BILLING_ACCOUNT_ID <id>"
        }

        # Billing-account exports cannot use managed identity – generate a
        # short-lived account-key SAS scoped to the export container (acwl).
        Write-Host "  Generating container SAS for billing-account export delivery..."
        $accountKey = az storage account keys list --account-name $storageName --resource-group $appRg `
            --query '[0].value' -o tsv --only-show-errors
        $sasExpiry = (Get-Date).AddYears(2).ToString('yyyy-MM-ddT00:00:00Z')
        $sasToken  = az storage container generate-sas `
            --account-name $storageName `
            --name $exportContainer `
            --permissions acwl `
            --expiry $sasExpiry `
            --account-key $accountKey `
            --https-only `
            -o tsv --only-show-errors

        Write-Host "  Deploying billing-account-scope export '$exportName' (tenant deployment)..."
        az deployment tenant create `
            --location $location `
            --template-file (Join-Path $bicepRoot 'export-billing.bicep') `
            --only-show-errors `
            --parameters `
                "billingAccountId=$billingAccountId" `
                "exportName=$exportName" `
                "storageAccountResourceId=$storageId" `
                "containerName=$exportContainer" `
                'rootFolderPath=exports' `
                "sasToken=$sasToken" `
                "recurrenceFrom=$recurrenceFrom" `
            | Out-Null

        Write-Host "  Billing-account export deployed." -ForegroundColor Green
    }

    default {
        Write-Host "  EXPORT_SCOPE='$exportScope' – skipping export deployment." -ForegroundColor Yellow
        Write-Host "  Set one of: azd env set EXPORT_SCOPE subscription | billing" -ForegroundColor Yellow
    }
}

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Step "postprovision complete"
$fqdn = Get-Env 'CONTAINER_APP_FQDN'
if ($fqdn) { Write-Host "  App URL:   https://$fqdn" -ForegroundColor Green }
Write-Host "  Key Vault: $keyVaultUri" -ForegroundColor Green
Write-Host "  Storage:   $storageUri" -ForegroundColor Green
Write-Host "  Next:      azd deploy   (builds & rolls the web app image)" -ForegroundColor Yellow
