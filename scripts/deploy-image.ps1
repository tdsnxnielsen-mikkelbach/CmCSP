<#
.SYNOPSIS
    Build, push, and redeploy the CmCSP container image to Azure Container Apps.

.DESCRIPTION
    Uses `dotnet publish /t:PublishContainer` — no Dockerfile or Docker daemon required.
    The .NET SDK builds a container image and pushes it directly to ACR in one step.

    1. Determines a tag (<yyyyMMdd>-<git-short-sha> by default).
    2. Authenticates against ACR via az acr login (feeds the .NET SDK's credential store).
    3. dotnet publish /t:PublishContainer  → builds the app AND pushes the image to ACR.
    4. Resolves the exact sha256 digest of the image that was just pushed.
    5. Updates the Container App with the digest-pinned image reference so ACA always
       pulls the new image even if the tag name did not change.
    6. Polls until the new revision becomes active, then prints the live app URL.

.EXAMPLE
    # Standard deployment from repo root
    .\scripts\deploy-image.ps1 -AcrName cmcspacrXXXXXX -AppName cmcsp -AppRg rg-cmcsp-app

    # Skip build (image already pushed to ACR by CI)
    .\scripts\deploy-image.ps1 -AcrName cmcspacrXXXXXX -AppName cmcsp -AppRg rg-cmcsp-app -SkipBuild -Tag "20260513-abc1234"

    # Override tag (e.g. CI-assigned version string)
    .\scripts\deploy-image.ps1 -AcrName cmcspacrXXXXXX -AppName cmcsp -AppRg rg-cmcsp-app -Tag "1.2.3"

.NOTES
    Requirements:
      - .NET 10 SDK  (dotnet --version)
      - az CLI logged in  (az login)
      - No Dockerfile or Docker daemon required
#>

[CmdletBinding()]
param (
    [Parameter(Mandatory)][string]$AcrName,
    [Parameter(Mandatory)][string]$AppName,
    [Parameter(Mandatory)][string]$AppRg,
    [string]$AppSubscriptionId = '',

    # Override the image tag. Defaults to <yyyyMMdd>-<git-short-sha>
    [string]$Tag = '',

    # Image repository name inside ACR (defaults to AppName lowercased)
    [string]$Repository = '',

    # Name of the cache cleanup Container Apps Job (defaults to <AppName>-cleanup)
    [string]$CleanupJobName = '',

    # Cleanup job image repository inside ACR (defaults to <AppName>-cleanup)
    [string]$CleanupRepository = '',

    # Path to the main app .csproj. Defaults to auto-detection from repo root.
    [string]$ProjectPath = '',

    # Path to the cleanup job .csproj. Defaults to auto-detection from repo root.
    [string]$CleanupProjectPath = '',

    # Skip build+push (image already in ACR); requires -Tag
    [switch]$SkipBuild,

    # Skip building and updating the cleanup job
    [switch]$SkipCleanupJob,

    # Also tag the pushed image as 'latest'
    [switch]$PushLatest,

    # Optionally re-set the identity env vars alongside the image update.
    # Useful when the app registration (TenantId / ClientId) has changed.
    [string]$TenantId = '',
    [string]$ClientId = '',

    # Dry-run: print commands without executing
    [switch]$WhatIf
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

function Invoke-Cmd([string]$exe, [string[]]$argList, [switch]$StreamOutput) {
    Write-Host "  > $exe $($argList -join ' ')" -ForegroundColor DarkGray
    if ($WhatIf) {
        Write-Host "  [WhatIf] skipped" -ForegroundColor Yellow
        return ""
    }
    $stdoutLines = [System.Collections.Generic.List[string]]::new()

    & $exe @argList 2>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) {
            Write-Host $_.ToString() -ForegroundColor DarkGray
        } elseif ($_ -notmatch '^\s*[\\/|\-]\s') {
            $stdoutLines.Add([string]$_)
            if ($StreamOutput) { Write-Host "  $_" }
        }
    }

    if ($LASTEXITCODE -ne 0) {
        # Print captured stdout so the actual error is visible
        if (-not $StreamOutput -and $stdoutLines.Count -gt 0) {
            $stdoutLines | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
        }
        Write-Error "${exe} failed (exit $LASTEXITCODE) — see output above"
    }
    return $stdoutLines -join "`n"
}

function Invoke-AzCli([string[]]$azArgs) {
    return Invoke-Cmd 'az' $azArgs
}

function Resolve-AppSubscriptionId() {
    if ($AppSubscriptionId) {
        return $AppSubscriptionId
    }

    function Try-ResolveAppInSubscription([string]$SubscriptionId) {
        $probeArgs = @(
            'resource', 'show',
            '--resource-group', $AppRg,
            '--name', $AppName,
            '--resource-type', 'Microsoft.App/containerApps',
            '--query', 'id',
            '-o', 'tsv',
            '--only-show-errors',
            '--subscription', $SubscriptionId
        )

        Write-Host "  > az $($probeArgs -join ' ')" -ForegroundColor DarkGray
        if ($WhatIf) {
            Write-Host "  [WhatIf] skipped" -ForegroundColor Yellow
            return $null
        }

        $probe = & az @probeArgs 2>$null
        if ($LASTEXITCODE -eq 0 -and $probe) {
            return ($probe -join "`n").Trim()
        }

        return $null
    }

    $currentSubscriptionId = (Invoke-AzCli @('account', 'show', '--query', 'id', '-o', 'tsv', '--only-show-errors')).Trim()
    $currentProbe = Try-ResolveAppInSubscription $currentSubscriptionId
    if ($currentProbe) {
        return $currentSubscriptionId
    }

    $subscriptionIds = (Invoke-AzCli @('account', 'list', '--query', '[].id', '-o', 'tsv', '--only-show-errors')) -split "`r?`n"
    foreach ($candidateSubscriptionId in ($subscriptionIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $candidateProbe = Try-ResolveAppInSubscription $candidateSubscriptionId
        if ($candidateProbe) {
            return $candidateSubscriptionId
        }
    }

    Write-Error "Unable to locate Container App '$AppName' in resource group '$AppRg' across accessible subscriptions. Pass -AppSubscriptionId explicitly."
}

# ─────────────────────────────────────────────────────────────────────────────
# Defaults
# ─────────────────────────────────────────────────────────────────────────────

# Repo root is one level above scripts/
$repoRoot = Split-Path $PSScriptRoot -Parent

if (-not $Repository) { $Repository = $AppName.ToLowerInvariant() }
if (-not $CleanupJobName) { $CleanupJobName = "$($AppName.ToLowerInvariant())-cleanup" }
if (-not $CleanupRepository) { $CleanupRepository = "$($AppName.ToLowerInvariant())-cleanup" }

$acrLoginServer = "$AcrName.azurecr.io"

# Locate main app .csproj automatically
if (-not $ProjectPath) {
    $found = Get-ChildItem -Path $repoRoot -Filter '*.csproj' -Depth 1 | Select-Object -First 1
    if (-not $found) { Write-Error "No .csproj found under '$repoRoot'. Pass -ProjectPath explicitly." }
    $ProjectPath = $found.FullName
}
Write-Host "  Project: $ProjectPath"

# Locate cleanup job .csproj automatically
if (-not $CleanupProjectPath) {
    $found = Get-ChildItem -Path (Join-Path $repoRoot 'CacheCleanupJob') -Filter '*.csproj' -Depth 1 `
             -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $CleanupProjectPath = $found.FullName }
}
if (-not $SkipCleanupJob -and -not $CleanupProjectPath) {
    Write-Host "  No cleanup job .csproj found – skipping cleanup job build." -ForegroundColor Yellow
    $SkipCleanupJob = $true
} elseif (-not $SkipCleanupJob) {
    Write-Host "  Cleanup project: $CleanupProjectPath"
}

$resolvedAppSubscriptionId = Resolve-AppSubscriptionId
Write-Host "  App subscription: $resolvedAppSubscriptionId"

# ─────────────────────────────────────────────────────────────────────────────
# Step 1 – Determine tag
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Step 1 – Determine image tag"

if (-not $Tag) {
    $datePart = (Get-Date -Format 'yyyyMMdd')
    $gitSha   = git -C $repoRoot rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $gitSha) {
        $gitSha = [System.Guid]::NewGuid().ToString('N').Substring(0, 7)
        Write-Host "  No git history found – using random suffix: $gitSha" -ForegroundColor Yellow
    }
    $Tag = "$datePart-$($gitSha.Trim())"
}

if ($SkipBuild -and -not $Tag) {
    Write-Error "-SkipBuild requires -Tag to be specified so the existing image can be located in ACR."
}

$fullImage = "$acrLoginServer/$Repository`:$Tag"
Write-Host "  Tag:   $Tag"
Write-Host "  Image: $fullImage"

# ─────────────────────────────────────────────────────────────────────────────
# Step 2 – ACR login (feeds credentials into the .NET SDK's credential store)
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Step 2 – ACR token"

# --expose-token returns a short-lived bearer token without touching the Docker
# daemon. The token is passed directly to dotnet publish as MSBuild properties.
$tokenJson = Invoke-AzCli @('acr', 'login', '--name', $AcrName, '--expose-token', '--output', 'json', '--only-show-errors')
if (-not $WhatIf) {
    $acrToken    = ($tokenJson | ConvertFrom-Json).accessToken
    $acrPassword = $acrToken
    if (-not $acrPassword) {
        Write-Error "Failed to obtain ACR token for '$AcrName'. Ensure you are logged in: az login"
    }
}
Write-Host "  ACR token acquired for $acrLoginServer"

# ─────────────────────────────────────────────────────────────────────────────
# Step 3 – dotnet publish /t:PublishContainer  (build + push in one step)
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Step 3 – dotnet publish /t:PublishContainer"

if ($SkipBuild) {
    Write-Host "  -SkipBuild set – verifying image exists in ACR..."
    $manifest = Invoke-AzCli @(
        'acr', 'manifest', 'show-metadata',
        '--registry', $AcrName,
        '--name', "$Repository`:$Tag",
        '--only-show-errors'
    )
    if (-not $manifest) {
        Write-Error "Image '$fullImage' not found in ACR. Push it first or remove -SkipBuild."
    }
    Write-Host "  Image confirmed in ACR."
} else {
    $containerTags = @($Tag)
    if ($PushLatest) { $containerTags += 'latest' }
    $tagsArg = $containerTags -join ';'

    # The .NET SDK reads Docker's credsStore config and calls docker-credential-desktop,
    # ignoring MSBuild env-var credentials when a credsStore is configured globally.
    # Per-registry entries in the 'auths' section take precedence over credsStore,
    # so we temporarily inject the ACR token there and restore on exit.
    $dockerConfigPath = Join-Path $env:USERPROFILE '.docker' 'config.json'
    $dockerConfigDir  = Split-Path $dockerConfigPath -Parent
    if (-not (Test-Path $dockerConfigDir)) {
        New-Item -ItemType Directory -Path $dockerConfigDir | Out-Null
    }
    $originalDockerConfig = if (Test-Path $dockerConfigPath) { Get-Content $dockerConfigPath -Raw } else { $null }
    $dockerConfig = if ($originalDockerConfig) { $originalDockerConfig | ConvertFrom-Json } else { [PSCustomObject]@{} }

    # Docker's lookup order: credHelpers (per-registry) → credsStore (global) → auths (inline).
    # credsStore beats auths, so we must remove it temporarily; auths then wins.
    $dockerConfig.PSObject.Properties.Remove('credsStore')

    $authValue = [Convert]::ToBase64String(
        [Text.Encoding]::ASCII.GetBytes("00000000-0000-0000-0000-000000000000:$acrPassword"))
    if (-not $dockerConfig.PSObject.Properties['auths']) {
        $dockerConfig | Add-Member -MemberType NoteProperty -Name 'auths' -Value ([PSCustomObject]@{})
    }
    $dockerConfig.auths | Add-Member -MemberType NoteProperty -Name $acrLoginServer `
        -Value ([PSCustomObject]@{ auth = $authValue }) -Force

    if (-not $WhatIf) { $dockerConfig | ConvertTo-Json -Depth 10 | Set-Content $dockerConfigPath }
    Write-Host "  Credentials injected into Docker config for $acrLoginServer (credsStore suspended)"

    try {
        Invoke-Cmd 'dotnet' @(
            'publish', $ProjectPath,
            '--configuration', 'Release',
            '--os', 'linux',
            '--arch', 'x64',
            '/t:PublishContainer',
            "-p:ContainerRegistry=$acrLoginServer",
            "-p:ContainerRepository=$Repository",
            "-p:ContainerImageTags=$tagsArg"
        ) -StreamOutput
    } finally {
        # Restore Docker config exactly as it was
        if (-not $WhatIf) {
            if ($null -ne $originalDockerConfig) {
                Set-Content -Path $dockerConfigPath -Value $originalDockerConfig
            } else {
                Remove-Item -Path $dockerConfigPath -ErrorAction SilentlyContinue
            }
        }
        Write-Host "  Docker config restored."
    }

    Write-Host "  Published and pushed: $fullImage" -ForegroundColor Green
    if ($PushLatest) { Write-Host "  Also pushed: $acrLoginServer/$Repository`:latest" -ForegroundColor Green }
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 3b – Build + push the cache cleanup job image
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Step 3b – Cache cleanup job image"

$cleanupDigest = $null
$cleanupFullImage = "$acrLoginServer/$CleanupRepository`:$Tag"

if ($SkipCleanupJob) {
    Write-Host "  -SkipCleanupJob set – skipping cleanup job build."
} elseif ($SkipBuild) {
    # Verify the cleanup image already exists when -SkipBuild is passed.
    Write-Host "  -SkipBuild set – verifying cleanup image exists in ACR..."
    $cleanupManifest = Invoke-AzCli @(
        'acr', 'manifest', 'show-metadata',
        '--registry', $AcrName,
        '--name', "$CleanupRepository`:$Tag",
        '--only-show-errors'
    )
    if (-not $cleanupManifest -and -not $WhatIf) {
        Write-Host "  Cleanup image '$cleanupFullImage' not found – skipping job update." -ForegroundColor Yellow
        $SkipCleanupJob = $true
    } else {
        Write-Host "  Cleanup image confirmed in ACR."
    }
} else {
    $containerTags = @($Tag)
    if ($PushLatest) { $containerTags += 'latest' }
    $tagsArg = $containerTags -join ';'

    # Docker config was restored after the main image push — re-inject credentials.
    $dockerConfig2 = if ($originalDockerConfig) { $originalDockerConfig | ConvertFrom-Json } else { [PSCustomObject]@{} }
    $dockerConfig2.PSObject.Properties.Remove('credsStore')
    if (-not $dockerConfig2.PSObject.Properties['auths']) {
        $dockerConfig2 | Add-Member -MemberType NoteProperty -Name 'auths' -Value ([PSCustomObject]@{})
    }
    $dockerConfig2.auths | Add-Member -MemberType NoteProperty -Name $acrLoginServer `
        -Value ([PSCustomObject]@{ auth = $authValue }) -Force
    if (-not $WhatIf) { $dockerConfig2 | ConvertTo-Json -Depth 10 | Set-Content $dockerConfigPath }
    Write-Host "  Credentials re-injected for cleanup job push"

    try {
        Invoke-Cmd 'dotnet' @(
            'publish', $CleanupProjectPath,
            '--configuration', 'Release',
            '--os', 'linux',
            '--arch', 'x64',
            '/t:PublishContainer',
            "-p:ContainerRegistry=$acrLoginServer",
            "-p:ContainerRepository=$CleanupRepository",
            "-p:ContainerImageTags=$tagsArg"
        ) -StreamOutput
    } finally {
        if (-not $WhatIf) {
            if ($null -ne $originalDockerConfig) {
                Set-Content -Path $dockerConfigPath -Value $originalDockerConfig
            } else {
                Remove-Item -Path $dockerConfigPath -ErrorAction SilentlyContinue
            }
        }
        Write-Host "  Docker config restored."
    }

    Write-Host "  Published and pushed: $cleanupFullImage" -ForegroundColor Green
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 5 – Resolve exact SHA digest
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Step 4 – Resolve image digest (SHA)"

$digestRaw = Invoke-AzCli @(
    'acr', 'manifest', 'show-metadata',
    '--registry', $AcrName,
    '--name', "$Repository`:$Tag",
    '--query', 'digest',
    '-o', 'tsv',
    '--only-show-errors'
)

$digest = $digestRaw.Trim()

if (-not $WhatIf) {
    if (-not $digest -or -not $digest.StartsWith('sha256:')) {
        Write-Error "Could not resolve digest for '$fullImage'. Got: '$digest'"
    }
}

# Pin by digest: <registry>/<repo>@sha256:<hash>
$pinnedImage = "$acrLoginServer/$Repository@$digest"
Write-Host "  Digest: $digest"
Write-Host "  Pinned: $pinnedImage"

# Resolve cleanup job digest (when job build was not skipped).
$pinnedCleanupImage = $null
if (-not $SkipCleanupJob) {
    $cleanupDigestRaw = Invoke-AzCli @(
        'acr', 'manifest', 'show-metadata',
        '--registry', $AcrName,
        '--name', "$CleanupRepository`:$Tag",
        '--query', 'digest',
        '-o', 'tsv',
        '--only-show-errors'
    )
    $cleanupDigest = $cleanupDigestRaw.Trim()
    if (-not $WhatIf -and $cleanupDigest -and $cleanupDigest.StartsWith('sha256:')) {
        $pinnedCleanupImage = "$acrLoginServer/$CleanupRepository@$cleanupDigest"
        Write-Host "  Cleanup digest: $cleanupDigest"
        Write-Host "  Cleanup pinned: $pinnedCleanupImage"
    } else {
        Write-Host "  Could not resolve cleanup job digest – skipping job update." -ForegroundColor Yellow
        $SkipCleanupJob = $true
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 6 – Update Container App with pinned digest
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Step 5 – Update Container App"

Write-Host "  Configuring ACR registry auth on '$AppName'..."
Invoke-AzCli @(
    'containerapp', 'registry', 'set',
    '--name', $AppName,
    '--resource-group', $AppRg,
    '--subscription', $resolvedAppSubscriptionId,
    '--server', $acrLoginServer,
    '--identity', 'system',
    '--only-show-errors'
) | Out-Null

Write-Host "  Setting image on '$AppName' in '$AppRg'..."
$updateArgs = @(
    'containerapp', 'update',
    '--name', $AppName,
    '--resource-group', $AppRg,
    '--subscription', $resolvedAppSubscriptionId,
    '--image', $pinnedImage,
    '--only-show-errors'
)
if ($TenantId -or $ClientId) {
    $idEnvPairs = @()
    if ($TenantId) { $idEnvPairs += "AzureCostManagement__TenantId=$TenantId" }
    if ($ClientId) { $idEnvPairs += "AzureCostManagement__ClientId=$ClientId" }
    $updateArgs += @('--set-env-vars') + $idEnvPairs
    Write-Host "  Also updating identity env vars (TenantId/ClientId)..."
}
Invoke-AzCli $updateArgs | Out-Null

# ── Update the cache cleanup job image ───────────────────────────────────────

if (-not $SkipCleanupJob -and $pinnedCleanupImage) {
    Write-Host "  Configuring ACR registry auth on cleanup job '$CleanupJobName'..."
    Invoke-AzCli @(
        'containerapp', 'job', 'registry', 'set',
        '--name', $CleanupJobName,
        '--resource-group', $AppRg,
        '--subscription', $resolvedAppSubscriptionId,
        '--server', $acrLoginServer,
        '--identity', 'system',
        '--only-show-errors'
    ) | Out-Null
    Write-Host "  Setting image on cleanup job '$CleanupJobName'..."
    Invoke-AzCli @(
        'containerapp', 'job', 'update',
        '--name', $CleanupJobName,
        '--resource-group', $AppRg,
        '--subscription', $resolvedAppSubscriptionId,
        '--image', $pinnedCleanupImage,
        '--only-show-errors'
    ) | Out-Null
    Write-Host "  Cleanup job updated: $pinnedCleanupImage" -ForegroundColor Green
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 7 – Wait for new revision to be active
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Step 6 – Wait for active revision"

$maxWait = 120   # seconds
$interval = 5
$elapsed  = 0
$activeRevision = $null

while ($elapsed -lt $maxWait) {
    if ($WhatIf) { Write-Host "  [WhatIf] skipping revision poll"; break }

    $revJson = az containerapp revision list `
        --name $AppName `
        --resource-group $AppRg `
        --subscription $resolvedAppSubscriptionId `
        --query '[?properties.active == `true`] | [-1].{name:name, image:properties.template.containers[0].image, traffic:properties.trafficWeight}' `
        -o json 2>$null | ConvertFrom-Json

    if ($revJson -and $revJson.image -and $revJson.image -match [regex]::Escape($digest)) {
        $activeRevision = $revJson
        break
    }

    Write-Host "  Waiting for revision with new digest... ($elapsed/$maxWait s)"
    Start-Sleep -Seconds $interval
    $elapsed += $interval
}

if ($activeRevision) {
    Write-Host ""
    Write-Host "  Active revision: $($activeRevision.name)"  -ForegroundColor Green
    Write-Host "  Image:           $($activeRevision.image)" -ForegroundColor Green
    Write-Host "  Traffic:         $($activeRevision.traffic)%" -ForegroundColor Green
} elseif (-not $WhatIf) {
    Write-Host ""
    Write-Host "  Timed out waiting – check revision status manually:" -ForegroundColor Yellow
    Write-Host "    az containerapp revision list -n $AppName -g $AppRg --subscription $resolvedAppSubscriptionId -o table" -ForegroundColor Yellow
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 8 – Print app URL
# ─────────────────────────────────────────────────────────────────────────────

Write-Step "Done"

$fqdn = Invoke-AzCli @(
    'containerapp', 'show',
    '--name', $AppName,
    '--resource-group', $AppRg,
    '--subscription', $resolvedAppSubscriptionId,
    '--query', 'properties.configuration.ingress.fqdn',
    '-o', 'tsv',
    '--only-show-errors'
)

Write-Host ""
Write-Host "  App URL:  https://$($fqdn.Trim())" -ForegroundColor Green
Write-Host "  Image:    $fullImage"              -ForegroundColor Green
Write-Host "  Digest:   $digest"                 -ForegroundColor Green
if (-not $SkipCleanupJob -and $pinnedCleanupImage) {
    Write-Host "  Cleanup:  $pinnedCleanupImage"   -ForegroundColor Green
}
Write-Host ""
