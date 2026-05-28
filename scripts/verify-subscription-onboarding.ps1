<#
.SYNOPSIS
    Verifies CmCSP onboarding readiness for one subscription.

.DESCRIPTION
    Runs a focused checklist for subscriptions added via the Home page UI:
      - App registration identity resolution from Container App env vars
      - Required RBAC on the target subscription
      - Provider registration state (Microsoft.CostManagement, Microsoft.Advisor)
      - Export resource presence (blob mode)
      - Export managed identity storage role assignment (blob mode)

    Prints PASS/FAIL/WARN lines and a final summary.

.EXAMPLE
    .\scripts\verify-subscription-onboarding.ps1 -SubscriptionId <sub-guid>

.EXAMPLE
    .\scripts\verify-subscription-onboarding.ps1 -SubscriptionId <sub-guid> -AppName cmcsp -AppRg rg-cmcsp-app
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [string]$AppName = 'cmcsp',
    [string]$AppRg = 'rg-cmcsp-app',
    [string]$AppSubscriptionId = '',
    [string]$ClientId = '',
    [string]$StorageAccountResourceId = '',
    [string]$ExportName = 'cmcsp-daily-export',
    [string]$CostManagementApiVersion = '2025-03-01'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$results = [System.Collections.Generic.List[pscustomobject]]::new()

function Add-Result([string]$Status, [string]$Check, [string]$Details) {
    $results.Add([pscustomobject]@{ Status = $Status; Check = $Check; Details = $Details }) | Out-Null
    switch ($Status) {
        'PASS' { Write-Host "[PASS] $Check - $Details" -ForegroundColor Green }
        'FAIL' { Write-Host "[FAIL] $Check - $Details" -ForegroundColor Red }
        default { Write-Host "[WARN] $Check - $Details" -ForegroundColor Yellow }
    }
}

function Run-Az([string[]]$AzArgs) {
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $stdoutLines = [System.Collections.Generic.List[string]]::new()

        az @AzArgs --only-show-errors 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) {
                return
            }

            $line = [string]$_

            # Drop CLI spinner/progress and dynamic-install guidance noise.
            if ($line -match '^\s*[\\/|\-]\s*$') { return }
            if ($line -match '^Preview version of extension is disabled by default') { return }
            if ($line -match '^Please run ''az config set') { return }
            if ($line -match '^The command requires the extension') { return }
            if ($line -match '^Run ''az config set extension\.use_dynamic_install') { return }
            if ($line -match '^Command group .* is experimental and under development') { return }

            $stdoutLines.Add($line)
        }

        if ($LASTEXITCODE -ne 0) {
            throw "az $($AzArgs -join ' ') failed"
        }

        $raw = ($stdoutLines -join "`n")
        if ($raw -match 'Welcome to the cool new Azure CLI') {
            if ($attempt -eq 1) {
                continue
            }
            throw "az returned welcome/help output instead of command result for: az $($AzArgs -join ' ')"
        }

        return $raw
    }

    throw "az command retry exhausted for: az $($AzArgs -join ' ')"
}

function Resolve-ContainerAppSubscriptionId() {
    if (-not [string]::IsNullOrWhiteSpace($AppSubscriptionId)) {
        return $AppSubscriptionId
    }

    $currentSubscriptionId = (Run-Az -AzArgs @('account', 'show', '--query', 'id', '-o', 'tsv')).Trim()
    try {
        $currentAppId = (Run-Az -AzArgs @(
            'resource', 'show',
            '--subscription', $currentSubscriptionId,
            '--resource-group', $AppRg,
            '--name', $AppName,
            '--resource-type', 'Microsoft.App/containerApps',
            '--query', 'id',
            '-o', 'tsv'
        )).Trim()
        if (-not [string]::IsNullOrWhiteSpace($currentAppId)) {
            return $currentSubscriptionId
        }
    }
    catch {
    }

    $subscriptionIds = (Run-Az -AzArgs @('account', 'list', '--query', '[].id', '-o', 'tsv')) -split "`r?`n"
    foreach ($candidateSubscriptionId in ($subscriptionIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        try {
            $candidateAppId = (Run-Az -AzArgs @(
                'resource', 'show',
                '--subscription', $candidateSubscriptionId,
                '--resource-group', $AppRg,
                '--name', $AppName,
                '--resource-type', 'Microsoft.App/containerApps',
                '--query', 'id',
                '-o', 'tsv'
            )).Trim()
            if (-not [string]::IsNullOrWhiteSpace($candidateAppId)) {
                return $candidateSubscriptionId
            }
        }
        catch {
        }
    }

    throw "Unable to locate Container App '$AppName' in resource group '$AppRg' across accessible subscriptions. Provide -AppSubscriptionId explicitly."
}

Write-Host ""
Write-Host "CmCSP subscription onboarding verifier" -ForegroundColor Cyan
Write-Host "Subscription: $SubscriptionId" -ForegroundColor Cyan
Write-Host "Container App: $AppName (RG: $AppRg)" -ForegroundColor Cyan
Write-Host ""

$resolvedAppSubscriptionId = Resolve-ContainerAppSubscriptionId

# Resolve required app env vars from Container App (tsv queries are resilient).
$resolvedClientId = (Run-Az -AzArgs @(
    'containerapp', 'show',
    '-n', $AppName,
    '-g', $AppRg,
    '--subscription', $resolvedAppSubscriptionId,
    '--query', "properties.template.containers[0].env[?name=='AzureCostManagement__ClientId'].value | [0]",
    '-o', 'tsv'
)).Trim()

$resolvedBlobMode = (Run-Az -AzArgs @(
    'containerapp', 'show',
    '-n', $AppName,
    '-g', $AppRg,
    '--subscription', $resolvedAppSubscriptionId,
    '--query', "properties.template.containers[0].env[?name=='AzureCostManagement__ExportBlob__Enabled'].value | [0]",
    '-o', 'tsv'
)).Trim()

$resolvedStorageId = (Run-Az -AzArgs @(
    'containerapp', 'show',
    '-n', $AppName,
    '-g', $AppRg,
    '--subscription', $resolvedAppSubscriptionId,
    '--query', "properties.template.containers[0].env[?name=='AzureCostManagement__ExportBlob__StorageAccountResourceId'].value | [0]",
    '-o', 'tsv'
)).Trim()

if ([string]::IsNullOrWhiteSpace($ClientId)) {
    $ClientId = $resolvedClientId
}
if ([string]::IsNullOrWhiteSpace($StorageAccountResourceId)) {
    $StorageAccountResourceId = $resolvedStorageId
}
$blobModeEnabled = ($resolvedBlobMode -eq 'true')

if ([string]::IsNullOrWhiteSpace($ClientId) -or ($ClientId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$')) {
    Add-Result 'FAIL' 'ClientId resolution' 'AzureCostManagement__ClientId not found on Container App env vars.'
} else {
    Add-Result 'PASS' 'ClientId resolution' "Using app registration ClientId $ClientId"
}

# RBAC checks
$rolesTsv = Run-Az -AzArgs @(
    'role', 'assignment', 'list',
    '--scope', "/subscriptions/$SubscriptionId",
    '--assignee', $ClientId,
    '--query', '[].roleDefinitionName',
    '-o', 'tsv'
)
$roleSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($r in ($rolesTsv -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
    [void]$roleSet.Add([string]$r)
}

if ($roleSet.Contains('Cost Management Contributor')) {
    Add-Result 'PASS' 'RBAC: Cost Management Contributor' 'Assigned on subscription scope.'
} else {
    Add-Result 'FAIL' 'RBAC: Cost Management Contributor' 'Missing on subscription scope.'
}

if ($roleSet.Contains('Reader')) {
    Add-Result 'PASS' 'RBAC: Reader' 'Assigned on subscription scope.'
} else {
    Add-Result 'FAIL' 'RBAC: Reader' 'Missing on subscription scope (required for Advisor APIs).'
}

# Provider registration checks
foreach ($ns in @('Microsoft.CostManagement', 'Microsoft.Advisor')) {
    try {
        $state = (Run-Az -AzArgs @(
            'provider', 'show',
            '--namespace', $ns,
            '--subscription', $SubscriptionId,
            '--query', 'registrationState',
            '-o', 'tsv'
        )).Trim()

        if ($state -eq 'Registered') {
            Add-Result 'PASS' "Provider: $ns" 'Registered'
        } elseif ($state -eq 'Registering') {
            Add-Result 'WARN' "Provider: $ns" 'Registering (wait and re-run verification)'
        } else {
            Add-Result 'FAIL' "Provider: $ns" "State is '$state'"
        }
    }
    catch {
        Add-Result 'FAIL' "Provider: $ns" $_.Exception.Message
    }
}

# Blob mode export checks
if (-not $blobModeEnabled) {
    Add-Result 'WARN' 'Blob export checks' 'Blob mode is disabled on Container App; skipping export resource checks.'
}
else {
    if ([string]::IsNullOrWhiteSpace($StorageAccountResourceId)) {
        Add-Result 'FAIL' 'Export storage resource ID' 'Blob mode enabled but AzureCostManagement__ExportBlob__StorageAccountResourceId is missing.'
    } else {
        Add-Result 'PASS' 'Export storage resource ID' $StorageAccountResourceId
    }

    $exportFound = $false
    $exportPrincipalId = ''
    try {
        $exportsJson = Run-Az -AzArgs @(
            'rest',
            '--method', 'get',
            '--url', "https://management.azure.com/subscriptions/$SubscriptionId/providers/Microsoft.CostManagement/exports?api-version=$CostManagementApiVersion",
            '--query', 'value',
            '-o', 'json'
        )

        $exports = @()
        if (-not [string]::IsNullOrWhiteSpace($exportsJson)) {
            $exports = @(ConvertFrom-Json $exportsJson)
        }

        $selectedExport = $null
        if (-not [string]::IsNullOrWhiteSpace($ExportName)) {
            $selectedExport = $exports | Where-Object { $_.name -eq $ExportName } | Select-Object -First 1
        }

        if ($null -eq $selectedExport -and -not [string]::IsNullOrWhiteSpace($StorageAccountResourceId)) {
            $selectedExport = $exports | Where-Object {
                [string]::Equals(
                    [string]$_.properties.deliveryInfo.destination.resourceId,
                    $StorageAccountResourceId,
                    [System.StringComparison]::OrdinalIgnoreCase)
            } | Select-Object -First 1
        }

        if ($null -eq $selectedExport -and $exports.Count -eq 1) {
            $selectedExport = $exports[0]
        }

        if ($null -ne $selectedExport) {
            $exportFound = $true
            $exportPrincipalId = [string]$selectedExport.identity.principalId
            Add-Result 'PASS' 'Export resource' "Found '$($selectedExport.name)' ($($selectedExport.id))"
        }
        elseif ($exports.Count -gt 0) {
            $exportNames = ($exports | ForEach-Object { $_.name }) -join ', '
            Add-Result 'WARN' 'Export resource' "Export(s) exist but none matched the expected name/storage. Found: $exportNames"
        }
        else {
            Add-Result 'WARN' 'Export resource' 'No Cost Management exports were found at subscription scope yet.'
        }
    }
    catch {
        Add-Result 'WARN' 'Export resource' "Unable to enumerate subscription exports: $($_.Exception.Message)"
    }

    if ($exportFound -and -not [string]::IsNullOrWhiteSpace($StorageAccountResourceId)) {
        if (-not [string]::IsNullOrWhiteSpace($exportPrincipalId)) {
            Add-Result 'PASS' 'Export managed identity principal' $exportPrincipalId
        } else {
            Add-Result 'FAIL' 'Export managed identity principal' 'principalId not found on export resource.'
        }

        if ($exportPrincipalId) {
            $assignmentsTsv = Run-Az -AzArgs @(
                'role', 'assignment', 'list',
                '--scope', $StorageAccountResourceId,
                '--assignee-object-id', $exportPrincipalId,
                '--query', '[].roleDefinitionName',
                '-o', 'tsv'
            )

            $hasBlobContributor = $false
            foreach ($sr in ($assignmentsTsv -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
                if ([string]::Equals([string]$sr, 'Storage Blob Data Contributor', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $hasBlobContributor = $true
                    break
                }
            }

            if ($hasBlobContributor) {
                Add-Result 'PASS' 'Export MI storage role' 'Storage Blob Data Contributor assigned on export storage account.'
            } else {
                Add-Result 'FAIL' 'Export MI storage role' 'Storage Blob Data Contributor missing on export storage account.'
            }
        }
    }
}

Write-Host ""
Write-Host "Summary" -ForegroundColor Cyan
Write-Host "-------" -ForegroundColor Cyan
$passCount = ($results | Where-Object { $_.Status -eq 'PASS' } | Measure-Object).Count
$warnCount = ($results | Where-Object { $_.Status -eq 'WARN' } | Measure-Object).Count
$failCount = ($results | Where-Object { $_.Status -eq 'FAIL' } | Measure-Object).Count
Write-Host "PASS: $passCount"
Write-Host "WARN: $warnCount"
Write-Host "FAIL: $failCount"

if ($failCount -gt 0) {
    exit 2
}
if ($warnCount -gt 0) {
    exit 1
}
exit 0
