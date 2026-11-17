<#
.SYNOPSIS
    azd postdeploy hook for CmCSP – build & push the Container Apps Job images.

.DESCRIPTION
    `azd deploy` builds and rolls the main web app (service "web"), but Azure
    Container Apps Jobs are not an azd service host. This hook builds each job
    image with the .NET SDK's container support (no Dockerfile), pushes it to
    ACR, and updates the corresponding Container Apps Job to the new digest.

    Jobs handled:
      • cost collector (src/CostCollectorJob  → cmcsp-collect → COLLECT_JOB_NAME)

    Uses an isolated temporary Docker credential directory, without modifying
    the operator's shared Docker configuration.

.NOTES
    Inputs come from azd outputs:
      AZURE_CONTAINER_REGISTRY_NAME, AZURE_CONTAINER_REGISTRY_ENDPOINT,
      AZURE_RESOURCE_GROUP, COLLECT_JOB_NAME
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Env([string]$name, [string]$default = '') {
    $val = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($val)) { return $default }
    return $val.Trim()
}
function Require-Env([string]$name) {
    $val = Get-Env $name
    if ([string]::IsNullOrWhiteSpace($val)) {
        Write-Error "Required azd environment variable '$name' is not set."
    }
    return $val
}

$repoRoot       = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$acrName        = Require-Env 'AZURE_CONTAINER_REGISTRY_NAME'
$acrLoginServer = Get-Env 'AZURE_CONTAINER_REGISTRY_ENDPOINT' "$acrName.azurecr.io"
$appRg          = Require-Env 'AZURE_RESOURCE_GROUP'

# Jobs to build/push/update. Names come from bicep outputs; project/repository are static.
$jobs = @(
    [PSCustomObject]@{ JobName = (Require-Env 'COLLECT_JOB_NAME'); Project = 'CostCollectorJob';  Repository = 'cmcsp-collect' }
)

# Tag: <yyyyMMdd>-<git-short-sha> (random suffix if no git history).
$datePart = (Get-Date -Format 'yyyyMMdd')
$gitSha   = git -C $repoRoot rev-parse --short HEAD 2>$null
if ($LASTEXITCODE -ne 0 -or -not $gitSha) {
    $gitSha = [System.Guid]::NewGuid().ToString('N').Substring(0, 7)
}
$tag = "$datePart-$($gitSha.Trim())"

Write-Host ""
Write-Host "───────────────────────────────────────────────" -ForegroundColor Cyan
Write-Host "  postdeploy – Container Apps Job images ($tag)"  -ForegroundColor Cyan
Write-Host "───────────────────────────────────────────────" -ForegroundColor Cyan

# ── ACR token (no Docker daemon required) ─────────────────────────────────────
$tokenJson   = az acr login --name $acrName --expose-token --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to obtain ACR token for '$acrName'." }
$acrPassword = ($tokenJson | ConvertFrom-Json).accessToken
if (-not $acrPassword) { Write-Error "Failed to obtain ACR token for '$acrName'." }

$originalDockerConfigDir = $env:DOCKER_CONFIG
$dockerConfigDir = Join-Path ([IO.Path]::GetTempPath()) "cmcsp-publish-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $dockerConfigDir | Out-Null
    $env:DOCKER_CONFIG = $dockerConfigDir
    $authValue = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("00000000-0000-0000-0000-000000000000:$acrPassword"))
    @{ auths = @{ $acrLoginServer = @{ auth = $authValue } } } |
        ConvertTo-Json -Depth 10 | Set-Content (Join-Path $dockerConfigDir 'config.json')

    foreach ($job in $jobs) {
        $projectPath = Join-Path $repoRoot 'src' $job.Project "$($job.Project).csproj"
        if (-not (Test-Path $projectPath)) {
            Write-Error "$($job.Project) project not found at $projectPath."
        }

        $fullImage = "$acrLoginServer/$($job.Repository)`:$tag"
        Write-Host ""
        Write-Host "  dotnet publish /t:PublishContainer → $fullImage"
        dotnet publish $projectPath `
            --configuration Release --os linux --arch x64 `
            /t:PublishContainer `
            "-p:ContainerRegistry=$acrLoginServer" `
            "-p:ContainerRepository=$($job.Repository)" `
            "-p:ContainerImageTags=$tag"
        if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed for $($job.Project) (exit $LASTEXITCODE)." }

        # ── Resolve digest and update the job ─────────────────────────────────
        $digest = az acr manifest show-metadata --registry $acrName --name "$($job.Repository)`:$tag" `
            --query 'digest' -o tsv --only-show-errors
        if ($LASTEXITCODE -ne 0 -or -not $digest) {
            Write-Error "Failed to resolve published digest for $fullImage."
        }
        $imageRef = "$acrLoginServer/$($job.Repository)@$($digest.Trim())"

        Write-Host "  Updating job '$($job.JobName)' → $imageRef"
        az containerapp job update --name $job.JobName --resource-group $appRg `
            --image $imageRef --only-show-errors | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Error "Failed to update job '$($job.JobName)'." }

        Write-Host "  $($job.JobName) image updated." -ForegroundColor Green
    }
} finally {
    $env:DOCKER_CONFIG = $originalDockerConfigDir
    if (Test-Path $dockerConfigDir) { Remove-Item -LiteralPath $dockerConfigDir -Recurse -Force }
}
