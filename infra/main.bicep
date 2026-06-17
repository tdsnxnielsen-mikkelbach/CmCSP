// CmCSP – azd entry-point template (subscription scope).
//
// Composes the existing resource-group-scoped modules into a single deployment:
//   1. app    (../bicep/app.bicep)   – ACR, Key Vault, Container Apps env,
//                                        Container App + cache cleanup Job.
//   2. storage(../bicep/main.bicep)  – Storage Account, containers, table, and
//                                        the role assignments for the app/cleanup MIs.
//
// Module ordering replaces the old multi-pass PowerShell flow: `app` is deployed
// first, then `storage` consumes its managed-identity principal IDs to create the
// RBAC role assignments – no second deployment pass required.
//
// Cost Management exports (subscription vs. billing account scope), Key Vault
// secret seeding, and Container App env-var wiring are handled by the
// postprovision hook (see infra/hooks/postprovision.ps1).

targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the azd environment – drives resource naming and the azd-env-name tag.')
param environmentName string

@minLength(1)
@description('Primary location for all resources.')
param location string

@description('Base application name (Container App, cleanup job, resource prefixes).')
param appName string = 'cmcsp'

@description('Resource group name. Defaults to rg-<appName>-<environmentName>.')
param resourceGroupName string = ''

@description('Azure Container Registry name. Leave empty to derive a globally-unique name.')
param acrName string = ''

@description('Key Vault name. Leave empty to derive a globally-unique name.')
param keyVaultName string = ''

@description('Storage account name. Leave empty to derive a globally-unique name.')
param storageAccountName string = ''

@description('Tags applied to every resource.')
param tags object = {
  project: 'cmcsp'
  application: 'csp-cost-dashboard'
  'managed-by': 'azd'
}

// ── Derived names ─────────────────────────────────────────────────────────────
// A stable suffix per (subscription, environment) keeps globally-unique names
// reproducible across re-runs. Override the *Name params to adopt existing resources.
var suffix = take(uniqueString(subscription().id, environmentName), 6)
var rgName = empty(resourceGroupName) ? 'rg-${appName}-${environmentName}' : resourceGroupName
var resolvedAcr = empty(acrName) ? '${appName}acr${suffix}' : acrName
var resolvedKv = empty(keyVaultName) ? 'kv-${appName}-${suffix}' : keyVaultName
var resolvedStorage = empty(storageAccountName) ? '${appName}st${suffix}' : storageAccountName

var allTags = union(tags, { 'azd-env-name': environmentName })

// ── Resource group ────────────────────────────────────────────────────────────

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: rgName
  location: location
  tags: allTags
}

// ── App infrastructure (ACR, Key Vault, Container Apps env, Container App, Job) ──

module app '../bicep/app.bicep' = {
  name: 'app'
  scope: rg
  params: {
    appName: appName
    acrName: resolvedAcr
    keyVaultName: resolvedKv
    location: location
    tags: allTags
    azdServiceName: 'web'
  }
}

// ── Export + cache storage (account, containers, table, RBAC role assignments) ──
// Consumes the app/cleanup managed-identity principal IDs → declarative RBAC,
// replacing the old "deploy app, read principalId, re-deploy storage" pass.

module storage '../bicep/main.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    storageAccountName: resolvedStorage
    location: location
    appManagedIdentityPrincipalId: app.outputs.containerAppPrincipalId
    cleanupJobManagedIdentityPrincipalId: app.outputs.cleanupJobPrincipalId
    tags: allTags
  }
}

// ── Outputs (surfaced to azd as environment variables for the hooks) ───────────

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name

output AZURE_CONTAINER_REGISTRY_NAME string = resolvedAcr
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = app.outputs.acrLoginServer

output AZURE_KEY_VAULT_NAME string = resolvedKv
output AZURE_KEY_VAULT_URI string = app.outputs.keyVaultUri

output STORAGE_ACCOUNT_NAME string = storage.outputs.storageAccountName
output STORAGE_ACCOUNT_URI string = storage.outputs.storageAccountUri
output STORAGE_ACCOUNT_RESOURCE_ID string = storage.outputs.storageAccountResourceId
output EXPORT_CONTAINER_NAME string = storage.outputs.exportContainerName

output CONTAINER_APP_NAME string = appName
output CONTAINER_APP_FQDN string = app.outputs.containerAppFqdn
output CLEANUP_JOB_NAME string = '${appName}-cleanup'
