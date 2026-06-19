<#
.SYNOPSIS
    azd postprovision hook for CmCSP.

.DESCRIPTION
    Runs after `azd provision` (infra/main.bicep). Replaces Phases 5-7 of the
    legacy scripts/deploy.ps1:

      5. Seed Key Vault secrets (identity + optional Cost Details settings).
      6. Wire Container App + cost collector Job environment
         variables, including the Key Vault reference for the client secret.
      6b. Assign Cost Management Contributor to the Entra App SP on every
          configured subscription (required for export auto-provisioning).
      7. Deploy the Cost Management export at the scope selected by EXPORT_SCOPE:
            subscription → infra/modules/export-sub.bicep    (managed-identity auth)
            billing      → infra/modules/export-billing.bicep (SAS-token auth, tenant scope)
            none         → skip (default)

         Subscription-scope exports are created here for EVERY configured
         subscription, running as the deployer (the signed-in user). This is the
         reliable path: Azure Cost Management denies *service principals* write
         access to exports even when they hold Cost Management Contributor, so the
         runtime SP path in ExportProvisioningService cannot create them. Doing it
         at deploy-time as the deployer side-steps that restriction.

      8. Phase 4 data platform (only when DATA_PLATFORM_ENABLED=true): apply the
         SQL schema (infra/sql/schema.sql) and create contained-DB users for the
         Container App + collect job managed identities (db_datareader/writer),
         then wire the SQL + Redis connection settings. Runs as the deployer, who
         is the SQL Entra admin (set in infra/modules/data.bicep).

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

# Run a T-SQL script/query against Azure SQL using the deployer's Entra credential.
# Prefers Invoke-Sqlcmd (SqlServer module) with an az-issued access token; falls back
# to go-sqlcmd's ActiveDirectoryAzCli auth. The deployer is the SQL Entra admin.
function Invoke-SqlScript {
    param(
        [Parameter(Mandatory)][string]$ServerFqdn,
        [Parameter(Mandatory)][string]$Database,
        [string]$Query,
        [string]$InputFile
    )

    if (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue) {
        $token = az account get-access-token --resource https://database.windows.net/ `
            --query accessToken -o tsv --only-show-errors
        if (-not $token) { Write-Error 'Could not acquire an Azure SQL access token (az account get-access-token).' }
        $params = @{ ServerInstance = $ServerFqdn; Database = $Database; AccessToken = $token; ErrorAction = 'Stop' }
        if ($InputFile) { $params['InputFile'] = $InputFile } else { $params['Query'] = $Query }
        Invoke-Sqlcmd @params | Out-Null
        return
    }

    $sqlcmd = Get-Command sqlcmd -ErrorAction SilentlyContinue
    if ($sqlcmd) {
        $cmdArgs = @('-S', "tcp:$ServerFqdn,1433", '-d', $Database, '--authentication-method', 'ActiveDirectoryAzCli', '-b')
        if ($InputFile) { $cmdArgs += @('-i', $InputFile) } else { $cmdArgs += @('-Q', $Query) }
        & $sqlcmd.Source @cmdArgs
        if ($LASTEXITCODE -ne 0) { Write-Error "sqlcmd failed (exit $LASTEXITCODE)." }
        return
    }

    Write-Error "No SQL client found. Install the 'SqlServer' PowerShell module (Install-Module SqlServer -Scope CurrentUser) or go-sqlcmd."
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
$bicepRoot = Join-Path (Join-Path $repoRoot 'infra') 'modules'

# ── Resolve inputs from azd (bicep outputs + `azd env set` values) ─────────────

$subscriptionId   = Require-Env 'AZURE_SUBSCRIPTION_ID'
$location         = Get-Env    'AZURE_LOCATION' 'swedencentral'
$appRg            = Require-Env 'AZURE_RESOURCE_GROUP'
$containerAppName = Require-Env 'CONTAINER_APP_NAME'
$collectJobName   = Get-Env    'COLLECT_JOB_NAME' "$containerAppName-collect"
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
# Phase 6 – Container App + collector job environment variables
# ──────────────────────────────────────────────────────────────────────────────

Write-Step "Phase 6 – Container App environment variables"

# When the Phase 4 data platform is provisioned, Redis (L2) + SQL (durable store)
# replace the storage Table/Blob cache, so we must NOT wire the AzureCache section
# (Redis takes precedence and the storage cache stays disabled). Computed here so
# Phase 6 can gate the cache vars; Phase 8 reuses the same value.
$dataPlatformEnabled = (Get-Env 'DATA_PLATFORM_ENABLED' 'false').ToLowerInvariant() -eq 'true'
if (-not $dataPlatformEnabled) {
    $dataPlatformEnabled = (Get-Env 'DEPLOY_DATA_PLATFORM' 'false').ToLowerInvariant() -eq 'true'
}

# Key Vault reference consumed as a Container App secret (resolved via the app MI).
# NOTE: 'client-secret' (CmCSP--ClientSecret) is the Entra App credential used for
# OIDC sign-in + the Cost Management Query API fallback — it is NOT a cache/storage
# secret and is retained under MI-only operation. All cache, storage and SQL access
# is managed-identity based (no connection-string secrets).
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
) + $subIdsEnv

if ($dataPlatformEnabled) {
    # Redis + SQL are the cache/durable store; keep the storage cache off.
    Write-Host "  Data platform enabled – Redis/SQL cache; storage Table/Blob cache disabled." -ForegroundColor DarkGray
    $envPairs += "AzureCostManagement__AzureCache__Enabled=false"
}
else {
    # No data platform: fall back to the managed-identity storage Table/Blob cache.
    $envPairs += "AzureCostManagement__AzureCache__Enabled=true"
    $envPairs += "AzureCostManagement__AzureCache__StorageAccountUri=$storageUri"
    $envPairs += "AzureCostManagement__AzureCache__TableName=cmcspcache"
    $envPairs += "AzureCostManagement__AzureCache__CacheContainerName=cmcspcache"
}

Write-Host "  Updating Container App with $($envPairs.Count) environment variables..."
az containerapp update --name $containerAppName --resource-group $appRg --set-env-vars @envPairs --only-show-errors | Out-Null

# Collect job runs the same data pipeline as the web app, so it takes the same
# cost + cache env vars (its 'client-secret' secret is defined in infra/modules/app.bicep).
Write-Host "  Wiring collect job environment variables ($collectJobName)..."
az containerapp job update --name $collectJobName --resource-group $appRg `
    --set-env-vars @envPairs --only-show-errors 2>$null | Out-Null

# ──────────────────────────────────────────────────────────────────────────────
# Phase 6b – Cost Management Contributor for the Entra App SP
# ──────────────────────────────────────────────────────────────────────────────
# Grants the Entra App SP 'Cost Management Contributor' on each target
# subscription. NOTE: Azure denies service principals *write* access to Cost
# Management exports even with this role, so the SP cannot create exports — that
# is done at deploy-time as the deployer (Phase 7). This grant still lets the SP
# READ/list and reuse exports at runtime (ExportProvisioningService.DetectAsync).

Write-Step "Phase 6b – Cost Management role for service principal"

$costMgmtContributorRoleId = '434105ed-43f6-45c7-a02f-909b2ba83430'
$spObjectId = az ad sp show --id $clientId --query id -o tsv --only-show-errors 2>$null

if (-not $spObjectId) {
    Write-Host "  Could not resolve SP object id for client $clientId — skipping role assignment." -ForegroundColor Yellow
}
else {
    foreach ($sub in $subscriptionIds) {
        $scope    = "/subscriptions/$sub"
        $assigned = az role assignment list --assignee $clientId --role $costMgmtContributorRoleId `
            --scope $scope --query "[?scope=='$scope'] | length(@)" -o tsv --only-show-errors 2>$null
        if (($assigned -as [int]) -ge 1) {
            Write-Host "  $sub — already assigned." -ForegroundColor Green
        }
        else {
            Write-Host "  $sub — assigning Cost Management Contributor..."
            az role assignment create --assignee-object-id $spObjectId --assignee-principal-type ServicePrincipal `
                --role $costMgmtContributorRoleId --scope $scope --only-show-errors | Out-Null
            Write-Host "    Assigned." -ForegroundColor Green
        }
    }
}

# ──────────────────────────────────────────────────────────────────────────────
# Phase 7 – Cost Management exports (scope switch)
# ──────────────────────────────────────────────────────────────────────────────

Write-Step "Phase 7 – Cost Management exports (scope: $exportScope)"

$recurrenceFrom = (Get-Date).AddDays(1).ToString('yyyy-MM-ddT00:00:00Z')

switch ($exportScope) {

    # ── Subscription scope: managed-identity auth, sub-level deployment ───────
    # Created for every configured subscription as the deployer (signed-in user),
    # because service principals are denied Cost Management export writes.
    'subscription' {
        foreach ($sub in $subscriptionIds) {
            az account set --subscription $sub --only-show-errors | Out-Null

            Write-Host "  [$sub] Deploying subscription-scope export '$exportName'..."
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
                Write-Host "  [$sub] Granting Storage Blob Data Contributor to export MI ($exportMiId)..."
                # Re-run storage module with the export MI principal → declarative RBAC.
                az deployment group create `
                    --resource-group $appRg `
                    --template-file (Join-Path $bicepRoot 'storage.bicep') `
                    --mode Incremental --only-show-errors `
                    --parameters `
                        "storageAccountName=$storageName" `
                        "location=$location" `
                        "exportManagedIdentityPrincipalId=$exportMiId" `
                    | Out-Null
            }

            if ($historicalMonths -gt 0) {
                Invoke-HistoricalBackfill -PrimarySub $sub
            }
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

# ──────────────────────────────────────────────────────────────
# Phase 8 – Data platform (Azure SQL contained-DB users + schema)
# ──────────────────────────────────────────────────────────────
# Only when the data platform was provisioned (bicep deployDataPlatform=true).
# Applies the schema and grants each managed identity db_datareader/db_datawriter
# as a contained-database user. Runs as the deployer (the SQL Entra admin).
# $dataPlatformEnabled was computed before Phase 6 (used there to gate the storage cache).

if ($dataPlatformEnabled) {
    Write-Step "Phase 8 – Data platform (SQL users + schema)"

    $sqlServerFqdn = Get-Env 'SQL_SERVER_FQDN'
    $sqlDatabase   = Get-Env 'SQL_DATABASE_NAME' 'cmcsp'
    $schemaFile    = Join-Path (Join-Path (Join-Path $repoRoot 'infra') 'sql') 'schema.sql'

    if (-not $sqlServerFqdn) {
        Write-Host "  DATA_PLATFORM_ENABLED set but SQL_SERVER_FQDN is empty – skipping." -ForegroundColor Yellow
    }
    elseif (-not (Test-Path $schemaFile)) {
        Write-Host "  Schema file not found: $schemaFile – skipping." -ForegroundColor Yellow
    }
    else {
        # Wire the no-secret SQL + Redis settings into the app + collect job FIRST, independently
        # of schema application. The container apps need ConnectionStrings__Sql to use SQL as the
        # cache/durable/audit store; if this step is skipped (e.g. because the schema apply below
        # fails) the apps fall back to NO backend — Phase 6 already disabled the storage cache —
        # which silently breaks the collection-audit trail and shared cache. Keep it in its own
        # try/catch so a SQL-client/permission problem during schema apply can't strand the config.
        try {
            $sqlConn   = Get-Env 'SQL_CONNECTION_STRING'
            $redisHost = Get-Env 'REDIS_HOST_NAME'
            $redisPort = Get-Env 'REDIS_PORT' '10000'
            $dataEnv = @()
            if ($sqlConn)   { $dataEnv += "ConnectionStrings__Sql=$sqlConn" }
            if ($redisHost) {
                # Redis options bind under the AzureCostManagement section.
                $dataEnv += "AzureCostManagement__Redis__Enabled=true"
                $dataEnv += "AzureCostManagement__Redis__HostName=$redisHost"
                $dataEnv += "AzureCostManagement__Redis__Port=$redisPort"
            }

            if ($dataEnv.Count -gt 0) {
                Write-Host "  Wiring SQL + Redis connection settings into the app + collect job..."
                az containerapp update --name $containerAppName --resource-group $appRg `
                    --set-env-vars @dataEnv --only-show-errors | Out-Null
                az containerapp job update --name $collectJobName --resource-group $appRg `
                    --set-env-vars @dataEnv --only-show-errors 2>$null | Out-Null
            }
            else {
                Write-Host "  SQL_CONNECTION_STRING/REDIS_HOST_NAME not available – skipping connection-string wiring." -ForegroundColor Yellow
            }
        }
        catch {
            Write-Host "  Wiring SQL/Redis connection settings failed: $($_.Exception.Message)" -ForegroundColor Yellow
        }

        # Apply the schema + create contained-DB users. A failure here does NOT undo the
        # connection-string wiring above; the schema can be re-applied later by re-running azd provision.
        try {
            Write-Host "  Applying schema (infra/sql/schema.sql) to $sqlServerFqdn/$sqlDatabase..."
            Invoke-SqlScript -ServerFqdn $sqlServerFqdn -Database $sqlDatabase -InputFile $schemaFile

            # Each system-assigned MI's Entra display name equals its resource name.
            $miNames = @($containerAppName, $collectJobName) | Where-Object { $_ } | Select-Object -Unique
            $userTsql = ($miNames | ForEach-Object {
@"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$_')
BEGIN
    CREATE USER [$_] FROM EXTERNAL PROVIDER;
    ALTER ROLE db_datareader ADD MEMBER [$_];
    ALTER ROLE db_datawriter ADD MEMBER [$_];
END
"@
            }) -join "`nGO`n"

            Write-Host "  Creating contained-DB users: $($miNames -join ', ')..."
            Invoke-SqlScript -ServerFqdn $sqlServerFqdn -Database $sqlDatabase -Query $userTsql

            Write-Host "  Data platform configured." -ForegroundColor Green
        }
        catch {
            Write-Host "  Data-platform schema setup failed: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host "  Ensure a SQL client is available (Install-Module SqlServer -Scope CurrentUser) and that" -ForegroundColor Yellow
            Write-Host "  the deployer is the SQL Entra admin, then re-run: azd provision" -ForegroundColor Yellow
        }
    }
}

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Step "postprovision complete"
$fqdn = Get-Env 'CONTAINER_APP_FQDN'
if ($fqdn) { Write-Host "  App URL:   https://$fqdn" -ForegroundColor Green }
Write-Host "  Key Vault: $keyVaultUri" -ForegroundColor Green
Write-Host "  Storage:   $storageUri" -ForegroundColor Green
Write-Host "  Next:      azd deploy   (builds & rolls the web app image)" -ForegroundColor Yellow
