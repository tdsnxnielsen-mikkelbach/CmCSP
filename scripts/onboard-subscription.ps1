<#
.SYNOPSIS
    Grants the CmCSP Entra App service principal the role it needs to auto-provision
    Cost Management exports on a target subscription, then verifies the assignment.

.DESCRIPTION
    The dashboard's "Add subscription" / "Re-provision Export" flow creates a
    `cmcsp-daily-export` Cost Management export by calling ARM as the Entra App SP.
    That call fails with RBACAccessDenied unless the SP holds **Cost Management
    Contributor** on the subscription. Assigning that role is a subscription-scope
    role assignment (requires User Access Administrator / Owner), so it is a one-time
    onboarding step the app cannot perform for itself.

    This script:
      1. Resolves the SP's client (application) ID — from -ClientId, the azd env
         (CMCSP_CLIENT_ID), or Key Vault (CmCSP--ClientId).
      2. Assigns Cost Management Contributor on the target subscription (idempotent).
      3. Verifies the assignment is present.

    After it completes (allow a few minutes for AAD propagation), use the
    "Re-provision Export" button on the Home page for that subscription.

.PARAMETER SubscriptionId
    The target subscription GUID to onboard.

.PARAMETER ClientId
    Optional. The Entra App (client/application) ID of the CmCSP service principal.
    When omitted, it is resolved from the azd environment, then Key Vault.

.PARAMETER KeyVaultName
    Optional. Key Vault holding the CmCSP--ClientId secret. When omitted, it is
    resolved from the azd environment (AZURE_KEY_VAULT_NAME).

.EXAMPLE
    ./scripts/onboard-subscription.ps1 -SubscriptionId af701430-fcf8-4be3-ac8a-4252d6b1960d

.EXAMPLE
    ./scripts/onboard-subscription.ps1 `
        -SubscriptionId af701430-fcf8-4be3-ac8a-4252d6b1960d `
        -ClientId 11111111-2222-3333-4444-555555555555

.NOTES
    Requires the Azure CLI, signed in as a principal with User Access Administrator
    or Owner on the target subscription.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string]$SubscriptionId,

    [string]$ClientId,

    [string]$KeyVaultName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Cost Management Contributor – built-in role definition ID (stable across tenants).
$CostManagementContributorRoleId = '434105ed-43f6-45c7-a02f-909b2ba83430'

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "───────────────────────────────────────────────" -ForegroundColor Cyan
    Write-Host "  $msg" -ForegroundColor Cyan
    Write-Host "───────────────────────────────────────────────" -ForegroundColor Cyan
}

# Best-effort lookup of an azd environment value; returns '' when azd or the key is absent.
function Get-AzdValue([string]$name) {
    try {
        $val = azd env get-value $name 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($val)) { return $val.Trim() }
    } catch { }
    return ''
}

# ── Resolve the service principal client ID ───────────────────────────────────

Write-Step "Resolving CmCSP service principal client ID"

if ([string]::IsNullOrWhiteSpace($ClientId)) {
    $ClientId = Get-AzdValue 'CMCSP_CLIENT_ID'
    if ($ClientId) { Write-Host "  Using CMCSP_CLIENT_ID from azd environment." }
}

if ([string]::IsNullOrWhiteSpace($ClientId)) {
    if ([string]::IsNullOrWhiteSpace($KeyVaultName)) {
        $KeyVaultName = Get-AzdValue 'AZURE_KEY_VAULT_NAME'
    }
    if ($KeyVaultName) {
        Write-Host "  Reading CmCSP--ClientId from Key Vault '$KeyVaultName'..."
        $ClientId = az keyvault secret show --vault-name $KeyVaultName --name 'CmCSP--ClientId' `
            --query value -o tsv --only-show-errors 2>$null
    }
}

if ([string]::IsNullOrWhiteSpace($ClientId)) {
    Write-Error "Could not resolve the SP client ID. Pass -ClientId explicitly, or run from a folder with an azd environment, or pass -KeyVaultName."
}

$ClientId = $ClientId.Trim()
Write-Host "  Service principal client ID: $ClientId" -ForegroundColor Green

# Resolve the SP object so the role assignment targets the correct principal.
$spObjectId = az ad sp show --id $ClientId --query id -o tsv --only-show-errors 2>$null
if ([string]::IsNullOrWhiteSpace($spObjectId)) {
    Write-Error "No service principal found for client ID '$ClientId' in this tenant. Verify the app registration and that you are signed into the right tenant (az login --tenant <id>)."
}

# ── Assign Cost Management Contributor on the subscription ─────────────────────

Write-Step "Assigning Cost Management Contributor on subscription $SubscriptionId"

$scope = "/subscriptions/$SubscriptionId"

$existing = az role assignment list `
    --assignee $ClientId `
    --role $CostManagementContributorRoleId `
    --scope $scope `
    --query '[].id' -o tsv --only-show-errors 2>$null

if (-not [string]::IsNullOrWhiteSpace($existing)) {
    Write-Host "  Already assigned — nothing to do." -ForegroundColor Green
}
else {
    Write-Host "  Creating role assignment..."
    az role assignment create `
        --assignee-object-id $spObjectId `
        --assignee-principal-type ServicePrincipal `
        --role $CostManagementContributorRoleId `
        --scope $scope `
        --only-show-errors | Out-Null
    Write-Host "  Role assigned." -ForegroundColor Green
}

# ── Verify ────────────────────────────────────────────────────────────────────

Write-Step "Verifying assignment"

$verify = az role assignment list `
    --assignee $ClientId `
    --role $CostManagementContributorRoleId `
    --scope $scope `
    --query "[?scope=='$scope'] | length(@)" -o tsv --only-show-errors 2>$null

if ($verify -as [int] -ge 1) {
    Write-Host "  Verified: Cost Management Contributor is present on the subscription." -ForegroundColor Green
    Write-Host ""
    Write-Host "  Next:" -ForegroundColor Yellow
    Write-Host "    • Allow 1–5 minutes for AAD propagation." -ForegroundColor Yellow
    Write-Host "    • On the Home page, click 'Re-provision Export' for this subscription." -ForegroundColor Yellow
}
else {
    Write-Error "Verification failed — the role assignment was not found at scope $scope. Check your permissions (User Access Administrator / Owner)."
}
