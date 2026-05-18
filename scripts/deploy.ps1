<#
.SYNOPSIS
    Full CmCSP deployment from scratch – provisions all Azure resources and wires
    them together.

.DESCRIPTION
    Phase 1 – App resource group  (main.bicep + app.bicep)
        Storage Account, export container, cache container, Table Storage,
        Container Registry, Key Vault, Log Analytics, Container Apps Environment,
        Container App (placeholder image; real image deployed by deploy-image.ps1)

    Phase 2 – Role assignments
        Container App MI → storage account (Blob Reader + Table Contributor)

    Phase 4 – Key Vault secrets
        ClientSecret, TenantId, ClientId, SubscriptionIds stored as KV secrets

    Phase 5 – Container App env-var wiring
        Patches the Container App with storage URIs, export-blob mode toggle, etc.

    Phase 6 – Cost Management exports
        Subscription-scope export (export-sub.bicep) if -DeployExports is set

.NOTES
    Requires: az CLI logged in, Bicep CLI (az bicep install), Docker optional
    Script is re-runnable (idempotent via --mode Incremental / existing resource checks)
#>

[CmdletBinding()]
param (
    # ── Identity ──────────────────────────────────────────────────────────────
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$ClientId,
    [Parameter(Mandatory)][string]$ClientSecret,

    # ── Subscriptions ─────────────────────────────────────────────────────────
    # Comma-separated list of subscription GUIDs the app will query.
    # The FIRST one is also used as the "primary" subscription for billing exports.
    [Parameter(Mandatory)][string[]]$SubscriptionIds,

    # ── Resource names (tune as needed) ──────────────────────────────────────
    [string]$AppRg            = 'rg-cmcsp-app',
    [string]$Location         = 'swedencentral',
    [string]$AppName          = 'cmcsp',

    # Globally-unique names – suffix with a short random string if needed
    [string]$AcrName          = '',        # defaults to "${AppName}acr<6-char suffix>"
    [string]$KeyVaultName     = '',        # defaults to "kv-${AppName}-<6-char suffix>"
    [string]$StorageAccount   = '',        # defaults to "${AppName}exports<6-char suffix>"

    # ── Export options ────────────────────────────────────────────────────────
    [switch]$DeployExports,               # pass to also deploy the cost-export schedule
    [string]$ExportName       = 'cmcsp-daily-export',
    [int]$HistoricalMonths    = 0,        # backfill N prior calendar months of export data

    # ── Misc ──────────────────────────────────────────────────────────────────
    [switch]$WhatIf,
    [hashtable]$Tags          = @{
        'project'      = 'cmcsp'
        'application'  = 'csp-cost-dashboard'
        'environment'  = 'production'
        'managed-by'   = 'bicep'
        'owner'        = 'platform-engineering'
        'cost-center'  = 'cloud-ops'
    }
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ─────────────────────────────────────────────────────────────────────────────
# Helpers
# ─────────────────────────────────────────────────────────────────────────────

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "─────────────────────────────────────────────────────" -ForegroundColor Cyan
    Write-Host "  $msg" -ForegroundColor Cyan
    Write-Host "─────────────────────────────────────────────────────" -ForegroundColor Cyan
}

function Invoke-AzCli([string[]]$azArgs) {
    if ($WhatIf) {
        Write-Host "[WhatIf] az $($azArgs -join ' ')" -ForegroundColor Yellow
        return $null
    }
    # 2>&1 merges stderr into the pipeline as ErrorRecord objects.
    # We separate them: ErrorRecords (stderr) go to the console; strings (stdout)
    # are filtered to remove az progress spinner lines (\, /, |, -) then joined
    # into a single string that ConvertFrom-Json can safely consume.
    $stdoutLines = [System.Collections.Generic.List[string]]::new()

    az @azArgs 2>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) {
            Write-Host $_.ToString() -ForegroundColor DarkGray
        } elseif ($_ -notmatch '^\s*[\\/|\-]\s') {
            $stdoutLines.Add([string]$_)
        }
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "az CLI failed (exit $LASTEXITCODE) — see output above"
    }

    return $stdoutLines -join "`n"
}

# Start a resource-group deployment asynchronously, print elapsed-time ticks while
# it runs, and return the completed deployment object (same JSON shape as
# 'az deployment group create' / 'az deployment group show').
# Retries the initial launch if ContainerAppOperationInProgress is returned.
function Invoke-AzDeploymentAsync {
    param(
        [string]   $Rg,
        [string]   $Name,
        [string[]] $TemplateArgs,
        [int]      $StartMaxAttempts  = 6,
        [int]      $StartRetrySeconds = 30,
        [int]      $PollSeconds       = 15,
        [int]      $TimeoutSeconds    = 1800
    )
    if ($WhatIf) {
        Write-Host "[WhatIf] az deployment group create -g $Rg -n $Name --no-wait ..." -ForegroundColor Yellow
        return $null
    }

    $launchArgs = @(
        'deployment', 'group', 'create',
        '--resource-group', $Rg,
        '--name', $Name,
        '--mode', 'Incremental',
        '--only-show-errors',
        '--no-wait'
    ) + $TemplateArgs

    for ($attempt = 1; $attempt -le $StartMaxAttempts; $attempt++) {
        try {
            Invoke-AzCli $launchArgs | Out-Null
            break
        } catch {
            if ($attempt -lt $StartMaxAttempts -and $_.ToString() -match 'ContainerAppOperationInProgress') {
                Write-Host "  Container App busy – retrying in ${StartRetrySeconds}s ($attempt/$($StartMaxAttempts - 1))..." -ForegroundColor Yellow
                Start-Sleep -Seconds $StartRetrySeconds
            } else { throw }
        }
    }

    $start     = Get-Date
    $deadline  = $start.AddSeconds($TimeoutSeconds)
    $pollRetry = 0
    do {
        Start-Sleep -Seconds $PollSeconds
        $dep     = az deployment group show -g $Rg -n $Name -o json 2>$null | ConvertFrom-Json
        $state   = $dep.properties.provisioningState
        $elapsed = [int]((Get-Date) - $start).TotalSeconds

        if ($state -eq 'Running') {
            Write-Host "  [${elapsed}s]  still provisioning..." -ForegroundColor Yellow
            if ((Get-Date) -ge $deadline) {
                Write-Error "Deployment '$Name' timed out after ${TimeoutSeconds}s (still Running). Cancel it in the portal or run: az deployment group cancel -g $Rg -n $Name"
            }
        } elseif ($state -eq 'Failed') {
            $errCode = $dep.properties.error.details[0].code
            if ($errCode -eq 'ContainerAppOperationInProgress' -and $pollRetry -lt $StartMaxAttempts) {
                $pollRetry++
                Write-Host "  Container App busy – re-launching deployment in ${StartRetrySeconds}s (poll-retry $pollRetry/$StartMaxAttempts)..." -ForegroundColor Yellow
                Start-Sleep -Seconds $StartRetrySeconds
                Invoke-AzCli $launchArgs | Out-Null
                $state = 'Running'   # keep the loop going
            } else {
                Write-Error "Deployment '$Name' failed after ${elapsed}s: $($dep.properties.error | ConvertTo-Json -Compress)"
            }
        } elseif ($state -eq 'Canceled') {
            Write-Error "Deployment '$Name' was canceled after ${elapsed}s."
        }
    } while ($state -eq 'Running')

    Write-Host "  Done ($([int]((Get-Date) - $start).TotalSeconds)s)." -ForegroundColor Green
    return $dep
}

# Poll until the Container App reaches Succeeded (or a terminal failure state).
# Keeps subsequent phases from queuing operations on a still-settling app.
function Wait-ContainerAppReady([string]$Name, [string]$Rg, [int]$TimeoutSeconds = 600) {
    if ($WhatIf) { return }
    $start    = Get-Date
    $deadline = $start.AddSeconds($TimeoutSeconds)
    $first    = $true
    do {
        $state = az containerapp show -n $Name -g $Rg `
            --query 'properties.provisioningState' -o tsv 2>$null
        $elapsed = [int]((Get-Date) - $start).TotalSeconds

        if ($state -eq 'Succeeded') {
            if (-not $first) {
                Write-Host "  Container App ready after ${elapsed}s." -ForegroundColor Green
            }
            return
        }

        if ($state -in @('Failed', 'Canceled')) {
            Write-Error "Container App '$Name' reached terminal state: $state"
        }

        if ($first) {
            Write-Host "  Container App is $state – waiting for ACA control plane to settle (up to ${TimeoutSeconds}s)..." -ForegroundColor Yellow
            $first = $false
        }

        Write-Host "  [${elapsed}s]  provisioningState: $state" -ForegroundColor Yellow
        Start-Sleep -Seconds 15
    } while ((Get-Date) -lt $deadline)
    Write-Warning "Timed out after ${TimeoutSeconds}s waiting for '$Name' (current: $state). Continuing anyway."
}

# Convert hashtable to JSON string for bicep --parameters
# Inner quotes must be escaped with \" so Windows argument parsing preserves them
# when PowerShell passes the value to the external az process.
function ConvertTo-BicepTags([hashtable]$t) {
    return ($t | ConvertTo-Json -Compress) -replace '"', '\"'
}

# Generate a short alphanumeric suffix based on subscription + app name so the
# same run always produces the same names (stable re-runs).
function Get-Suffix([string]$seed) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($seed)
    $hash  = [System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
    return ([System.Convert]::ToBase64String($hash).ToLower() -replace '[^a-z0-9]', '')[0..5] -join ''
}

# ─────────────────────────────────────────────────────────────────────────────
# Pre-flight checks
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Pre-flight checks"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error "az CLI not found. Install from https://aka.ms/install-az"; exit 1
}
$azVersion = (az --version 2>$null | Select-String '^azure-cli' | ForEach-Object { $_ -replace 'azure-cli\s+', '' }).Trim()
if (-not $azVersion) { $azVersion = 'unknown' }
Write-Host "az CLI version: $azVersion"

# Make sure Bicep is available
az bicep install --only-show-errors | Out-Null

# Ensure we are logged in
$account = az account show --query '{sub:id,tenant:tenantId}' -o json 2>$null | ConvertFrom-Json
if (-not $account) { Write-Error "Not logged in to az CLI. Run: az login"; exit 1 }
Write-Host "Logged in – tenant $($account.tenant)"

# Default globally-unique names (stable suffix per tenant+app)
$suffix = Get-Suffix "$TenantId-$AppName"
if (-not $AcrName)      { $AcrName      = "$($AppName)acr$suffix"     }
if (-not $KeyVaultName) { $KeyVaultName = "kv-$AppName-$suffix"       }
if (-not $StorageAccount) { $StorageAccount = "$($AppName)st$suffix"  }

# Bicep root (relative to this script's location)
$BicepRoot = Join-Path $PSScriptRoot ".." "bicep"

Write-Host "AppRg:          $AppRg"
Write-Host "Location:       $Location"
Write-Host "AcrName:        $AcrName"
Write-Host "KeyVaultName:   $KeyVaultName"
Write-Host "StorageAccount: $StorageAccount"
Write-Host "SubscriptionIds: $($SubscriptionIds -join ', ')"

# ─────────────────────────────────────────────────────────────────────────────
# Phase 1 – Resource groups
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Phase 1 – Resource groups"

$exists = az group exists -n $AppRg
if ($exists -eq 'true') {
    Write-Host "  RG '$AppRg' already exists – skipping"
} else {
    Write-Host "  Creating RG '$AppRg' in $Location"
    Invoke-AzCli @('group', 'create', '-n', $AppRg, '-l', $Location, '--only-show-errors') | Out-Null
}

# ─────────────────────────────────────────────────────────────────────────────
# Phase 2 – Export storage (main.bicep)
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Phase 2 – Export storage (main.bicep)"

$tagsJson = ConvertTo-BicepTags $Tags

Write-Host "  Starting deployment..."
$storageOut = Invoke-AzDeploymentAsync -Rg $AppRg -Name 'main' -TemplateArgs @(
    '--template-file', "$BicepRoot\main.bicep",
    '--parameters',
        "storageAccountName=$StorageAccount",
        "location=$Location",
        "tags=$tagsJson"
)

$storageUri = $storageOut.properties.outputs.storageAccountUri.value
if (-not $storageUri) {
    Write-Error "main.bicep deployment succeeded but 'storageAccountUri' output is empty. Cannot continue without a storage URI."
}
Write-Host "  Storage Account URI: $storageUri"

# ─────────────────────────────────────────────────────────────────────────────
# Phase 3 – App infrastructure (app.bicep)
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Phase 3 – App infrastructure (app.bicep)"

# If the Container App already exists from a previous run, wait for any in-progress
# operation to settle before launching the deployment – otherwise it will immediately
# fail with ContainerAppOperationInProgress.
$existingCaState = az containerapp show -n $AppName -g $AppRg `
    --query 'properties.provisioningState' -o tsv 2>$null
if ($existingCaState -and $existingCaState -notin @('Succeeded', 'Failed', 'Canceled')) {
    Write-Host "  Container App '$AppName' is currently '$existingCaState' – waiting before deploying..."
    Wait-ContainerAppReady -Name $AppName -Rg $AppRg
}

Write-Host "  Starting deployment (ACR, Key Vault, Container Apps env, Container App)..."
$appOut = Invoke-AzDeploymentAsync -Rg $AppRg -Name 'app' -TemplateArgs @(
    '--template-file', "$BicepRoot\app.bicep",
    '--parameters',
        "appName=$AppName",
        "acrName=$AcrName",
        "keyVaultName=$KeyVaultName",
        "location=$Location",
        "tags=$tagsJson"
)

$containerAppFqdn    = $appOut.properties.outputs.containerAppFqdn.value
$containerAppMiId    = $appOut.properties.outputs.containerAppPrincipalId.value
$acrLoginServer      = $appOut.properties.outputs.acrLoginServer.value
$keyVaultUri         = $appOut.properties.outputs.keyVaultUri.value

Write-Host "  Container App FQDN:  $containerAppFqdn"
Write-Host "  Container App MI:    $containerAppMiId"
Write-Host "  ACR Login Server:    $acrLoginServer"
Write-Host "  Key Vault URI:       $keyVaultUri"

Write-Step "Phase 3 (settling) – waiting for Container App to reach Succeeded"
Wait-ContainerAppReady -Name $AppName -Rg $AppRg

# ─────────────────────────────────────────────────────────────────────────────
# Phase 4 – Cross-RG role assignments (app MI → export storage)
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Phase 4 – Role assignments (app MI → storage + ACR)"

$storageId = "/subscriptions/$($account.sub)/resourceGroups/$AppRg/providers/Microsoft.Storage/storageAccounts/$StorageAccount"
$acrId     = "/subscriptions/$($account.sub)/resourceGroups/$AppRg/providers/Microsoft.ContainerRegistry/registries/$AcrName"

# AcrPull is checked here as a safety net; app.bicep manages it with a stable GUID.
# Storage roles (Blob Reader, Table Contributor) are intentionally NOT created via CLI –
# main.bicep owns them with deterministic guid() names.  Creating them here would
# generate random GUIDs that conflict with Bicep on re-runs.
$acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
$existingAcrPull = az role assignment list `
    --assignee $containerAppMiId `
    --role $acrPullRoleId `
    --scope $acrId `
    --query '[].id' -o tsv 2>$null
if ($existingAcrPull) {
    Write-Host "  'AcrPull' already assigned – skipping"
} else {
    Write-Host "  Assigning 'AcrPull' to app MI"
    Invoke-AzCli @(
        'role', 'assignment', 'create',
        '--assignee-object-id', $containerAppMiId,
        '--assignee-principal-type', 'ServicePrincipal',
        '--role', $acrPullRoleId,
        '--scope', $acrId,
        '--only-show-errors'
    ) | Out-Null
}

# Remove any storage role assignments that were previously created via CLI with
# random GUIDs. main.bicep will re-create them with stable deterministic GUIDs,
# which is idempotent on every subsequent run (Incremental mode sees same name).
$storageRoleIds = @(
    '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'  # Storage Blob Data Reader
    '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'  # Storage Table Data Contributor
)
foreach ($rId in $storageRoleIds) {
    $toDelete = az role assignment list `
        --assignee $containerAppMiId --role $rId --scope $storageId `
        --query '[].id' -o tsv 2>$null
    foreach ($raId in ($toDelete -split "`n" | Where-Object { $_ })) {
        Write-Host "  Removing stale storage role assignment (Bicep will re-create with stable GUID): $raId"
        Invoke-AzCli @('role', 'assignment', 'delete', '--ids', $raId, '--only-show-errors') | Out-Null
    }
}

# Deploy main.bicep with the app MI principal ID so Bicep creates the storage
# role assignments with deterministic GUIDs – idempotent on re-runs.
Write-Host "  Updating storage with app MI principal ID..."
Invoke-AzCli @(
    'deployment', 'group', 'create',
    '--resource-group', $AppRg,
    '--template-file', "$BicepRoot\main.bicep",
    '--mode', 'Incremental',
    '--only-show-errors',
    '--parameters',
        "storageAccountName=$StorageAccount",
        "location=$Location",
        "appManagedIdentityPrincipalId=$containerAppMiId",
        "tags=$tagsJson"
) | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
# Phase 5 – Key Vault secrets
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Phase 5 – Key Vault secrets"

# Key Vault name extracted from URI: https://<name>.vault.azure.net/
$kvName = ($keyVaultUri -replace 'https://', '' -replace '\.vault\.azure\.net/?', '')

# Grant the CURRENT principal (operator) Key Vault Secrets Officer so we can write secrets
$currentOid = az ad signed-in-user show --query id -o tsv 2>$null
if (-not $currentOid) {
    # Service principal login – use account object id
    $currentOid = az account show --query 'user.name' -o tsv 2>$null
}

$kvId = "/subscriptions/$($account.sub)/resourceGroups/$AppRg/providers/Microsoft.KeyVault/vaults/$kvName"
$kvSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
$existingKvRole = az role assignment list `
    --assignee $currentOid `
    --role $kvSecretsOfficerRoleId `
    --scope $kvId `
    --query '[].id' -o tsv 2>$null
if (-not $existingKvRole) {
    Write-Host "  Granting Key Vault Secrets Officer to current principal..."
    Invoke-AzCli @(
        'role', 'assignment', 'create',
        '--assignee', $currentOid,
        '--role', $kvSecretsOfficerRoleId,
        '--scope', $kvId,
        '--only-show-errors'
    ) | Out-Null
    # Short wait for AAD propagation
    if (-not $WhatIf) { Start-Sleep -Seconds 15 }
}

$secrets = @{
    'CmCSP--TenantId'           = $TenantId
    'CmCSP--ClientId'           = $ClientId
    'CmCSP--ClientSecret'       = $ClientSecret
    'CmCSP--SubscriptionIds'    = ($SubscriptionIds -join ',')
}

foreach ($name in $secrets.Keys) {
    Write-Host "  Setting secret: $name"
    Invoke-AzCli @(
        'keyvault', 'secret', 'set',
        '--vault-name', $kvName,
        '--name', $name,
        '--value', $secrets[$name],
        '--only-show-errors'
    ) | Out-Null
}

# ─────────────────────────────────────────────────────────────────────────────
# Phase 6 – Wire Container App environment variables
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Phase 6 – Container App environment variable patch"

# Build the env-var list as JSON. Sensitive values come from Key Vault references.
$subIdsEnv = ($SubscriptionIds | ForEach-Object { $i = 0 } {
    $i; "AzureCostManagement__SubscriptionIds__$i=$_"; $i++
}) | Where-Object { $_ -is [string] }

# Wire the ClientSecret Key Vault reference as a Container App secret first,
# so it can be consumed as secretref in the env vars below.
Write-Host "  Adding Key Vault secret reference 'client-secret'..."
Invoke-AzCli @(
    'containerapp', 'secret', 'set',
    '--name', $AppName,
    '--resource-group', $AppRg,
    '--secrets', "client-secret=keyvaultref:${keyVaultUri}secrets/CmCSP--ClientSecret,identityref:system",
    '--only-show-errors'
) | Out-Null

# Core env vars (non-secret); ClientSecret exposed via secretRef
$envPairs = @(
    "AzureCostManagement__TenantId=$TenantId"
    "AzureCostManagement__ClientId=$ClientId"
    "AzureCostManagement__ClientSecret=secretref:client-secret"
    "AzureCostManagement__ExportBlob__Enabled=true"
    "AzureCostManagement__ExportBlob__StorageAccountUri=$storageUri"
    "AzureCostManagement__ExportBlob__ContainerName=cost-exports"
    "AzureCostManagement__ExportBlob__BlobPrefix=exports"
    "AzureCostManagement__AzureCache__Enabled=true"
    "AzureCostManagement__AzureCache__StorageAccountUri=$storageUri"
    "AzureCostManagement__AzureCache__TableName=cmcspcache"
    "AzureCostManagement__AzureCache__CacheContainerName=cmcspcache"
) + $subIdsEnv

Write-Host "  Updating Container App with $($envPairs.Count) environment variables..."

Invoke-AzCli (@(
    'containerapp', 'update',
    '--name', $AppName,
    '--resource-group', $AppRg,
    '--set-env-vars'
) + $envPairs + @('--only-show-errors')) | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
# Phase 7 – Cost Management exports (optional)
# ─────────────────────────────────────────────────────────────────────────────

if ($DeployExports) {
    Write-Step "Phase 7 – Cost Management exports (export-sub.bicep)"

    $primarySub = $SubscriptionIds[0]
    $recurrenceFrom = (Get-Date).AddDays(1).ToString('yyyy-MM-ddT00:00:00Z')

    # Set the primary subscription as the deployment target
    Invoke-AzCli @('account', 'set', '--subscription', $primarySub) | Out-Null

    $exportOut = Invoke-AzCli @(
        'deployment', 'sub', 'create',
        '--location', $Location,
        '--template-file', "$BicepRoot\export-sub.bicep",
        '--only-show-errors',
        '--parameters',
            "exportName=$ExportName",
            "storageAccountResourceId=$storageId",
            "containerName=cost-exports",
            "rootFolderPath=exports",
            "location=$Location",
            "recurrenceFrom=$recurrenceFrom"
    ) | ConvertFrom-Json

    $exportMiId = $exportOut.properties.outputs.managedIdentityPrincipalId.value
    if ($exportMiId) {
        Write-Host "  Export MI principal ID: $exportMiId"
        Write-Host "  Granting Storage Blob Data Contributor to export MI..."
        Invoke-AzCli @(
            'deployment', 'group', 'create',
            '--resource-group', $AppRg,
            '--template-file', "$BicepRoot\main.bicep",
            '--mode', 'Incremental',
            '--only-show-errors',
            '--parameters',
                "storageAccountName=$StorageAccount",
                "location=$Location",
                "exportManagedIdentityPrincipalId=$exportMiId",
                "appManagedIdentityPrincipalId=$containerAppMiId",
                "tags=$tagsJson"
        ) | Out-Null
    }

    # ── Optional: backfill historical months ─────────────────────────────────
    if ($HistoricalMonths -gt 0) {
        Write-Host ""
        Write-Host "  Backfilling $HistoricalMonths prior calendar month(s) of cost data..." -ForegroundColor Cyan
        $today  = (Get-Date).Date
        $farFuture = '2099-12-31T00:00:00Z'

        for ($m = 1; $m -le $HistoricalMonths; $m++) {
            $monthStart  = [datetime]::new($today.Year, $today.Month, 1).AddMonths(-$m)
            $monthEnd    = $monthStart.AddMonths(1).AddDays(-1)
            $historyName = "$ExportName-hist-$($monthStart.ToString('yyyy-MM'))"

            Write-Host "  [$m/$HistoricalMonths] $($monthStart.ToString('yyyy-MM')) → deploying $historyName ..."

            Invoke-AzCli @(
                'deployment', 'sub', 'create',
                '--location', $Location,
                '--template-file', "$BicepRoot\export-sub.bicep",
                '--only-show-errors',
                '--parameters',
                    "exportName=$historyName",
                    "storageAccountResourceId=$storageId",
                    'containerName=cost-exports',
                    'rootFolderPath=exports',
                    "location=$Location",
                    'timeframe=Custom',
                    "timePeriodFrom=$($monthStart.ToString('yyyy-MM-dd'))",
                    "timePeriodTo=$($monthEnd.ToString('yyyy-MM-dd'))",
                    'scheduleStatus=Inactive',
                    "recurrenceFrom=$farFuture"
            ) | Out-Null

            # Trigger the one-time export immediately.
            $scope = "/subscriptions/$primarySub"
            az rest --method POST `
                --uri "https://management.azure.com$scope/providers/Microsoft.CostManagement/exports/$historyName/run?api-version=2025-03-01" `
                --only-show-errors | Out-Null

            Write-Host "    Triggered." -ForegroundColor Green
        }
    }

    # Switch back to whichever subscription the user had active
    Invoke-AzCli @('account', 'set', '--subscription', $account.sub) | Out-Null
} else {
    Write-Host ""
    Write-Host "  Skipping export schedule deployment (pass -DeployExports to enable)." -ForegroundColor Yellow
    if ($HistoricalMonths -gt 0) {
        Write-Host "  Note: -HistoricalMonths requires -DeployExports to also be set." -ForegroundColor Yellow
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Deployment complete"

Write-Host ""
Write-Host "  App URL:       https://$containerAppFqdn" -ForegroundColor Green
Write-Host "  ACR:           $acrLoginServer"           -ForegroundColor Green
Write-Host "  Key Vault:     $keyVaultUri"              -ForegroundColor Green
Write-Host "  Storage:       $storageUri"               -ForegroundColor Green
Write-Host ""
Write-Host "  Next step: build and push the container image, then run:" -ForegroundColor Yellow
Write-Host "    .\scripts\deploy-image.ps1 -AcrName $AcrName -AppName $AppName -AppRg $AppRg" -ForegroundColor Yellow
Write-Host ""
