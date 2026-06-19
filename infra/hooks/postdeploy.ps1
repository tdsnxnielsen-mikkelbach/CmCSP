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

    Mirrors the job portion of scripts/deploy-image.ps1, reusing the ACR token +
    Docker-config credential injection pattern.

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
$acrPassword = ($tokenJson | ConvertFrom-Json).accessToken
if (-not $acrPassword) { Write-Error "Failed to obtain ACR token for '$acrName'." }

# ── Inject credentials into Docker config (credsStore beats auths, so suspend it) ──
$dockerConfigPath = Join-Path $env:USERPROFILE '.docker' 'config.json'
$dockerConfigDir  = Split-Path $dockerConfigPath -Parent
if (-not (Test-Path $dockerConfigDir)) { New-Item -ItemType Directory -Path $dockerConfigDir | Out-Null }
$originalDockerConfig = if (Test-Path $dockerConfigPath) { Get-Content $dockerConfigPath -Raw } else { $null }
$dockerConfig = if ($originalDockerConfig) { $originalDockerConfig | ConvertFrom-Json } else { [PSCustomObject]@{} }
$dockerConfig.PSObject.Properties.Remove('credsStore')
$authValue = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("00000000-0000-0000-0000-000000000000:$acrPassword"))
if (-not $dockerConfig.PSObject.Properties['auths']) {
    $dockerConfig | Add-Member -MemberType NoteProperty -Name 'auths' -Value ([PSCustomObject]@{})
}
$dockerConfig.auths | Add-Member -MemberType NoteProperty -Name $acrLoginServer -Value ([PSCustomObject]@{ auth = $authValue }) -Force
$dockerConfig | ConvertTo-Json -Depth 10 | Set-Content $dockerConfigPath

try {
    foreach ($job in $jobs) {
        $projectPath = Join-Path $repoRoot 'src' $job.Project "$($job.Project).csproj"
        if (-not (Test-Path $projectPath)) {
            Write-Host "  $($job.Project) project not found at $projectPath – skipping." -ForegroundColor Yellow
            continue
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
        $imageRef = if ($digest) { "$acrLoginServer/$($job.Repository)@$digest" } else { $fullImage }

        Write-Host "  Updating job '$($job.JobName)' → $imageRef"
        az containerapp job update --name $job.JobName --resource-group $appRg `
            --image $imageRef --only-show-errors | Out-Null

        Write-Host "  $($job.JobName) image updated." -ForegroundColor Green
    }
} finally {
    if ($null -ne $originalDockerConfig) { Set-Content -Path $dockerConfigPath -Value $originalDockerConfig }
    else { Remove-Item -Path $dockerConfigPath -ErrorAction SilentlyContinue }
}
