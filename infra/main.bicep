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

@description('''When true, grants the Container App managed identity 'Role Based Access Control Administrator'
on THIS subscription, constrained (via an ABAC condition) to assigning only the Cost Management Contributor
role. This makes UI-added subscriptions self-onboard without a manual role grant — but only covers the
deployment subscription. For multi-subscription / future-subscription coverage, assign the role once at a
management group instead. Leave false to keep onboarding a manual step (scripts/onboard-subscription.ps1).''')
param grantMiRbacAdminOnSubscription bool = false

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
    collectJobManagedIdentityPrincipalId: app.outputs.collectJobPrincipalId
    tags: allTags
  }
}

// ── Optional: let the Container App MI self-onboard subscriptions ───────────────
// Grants 'Role Based Access Control Administrator' on this subscription, constrained
// by an ABAC condition so the MI can ONLY assign the Cost Management Contributor role
// (and nothing else). This is what enables ExportProvisioningService to grant the
// Entra App SP its export-creation role at runtime when a subscription is added via
// the UI. Scoped to the deployment subscription only — see param description.

var rbacAdminRoleId = 'f58310d9-a9f6-439a-9e8d-f62e7b41a168' // Role Based Access Control Administrator
var costMgmtContributorRoleId = '1e7ca9b1-60d1-4db8-a914-f2ca1ff27c40'

// Condition: permit roleAssignments write/delete ONLY when the role being assigned is
// Cost Management Contributor. All other assignment attempts by this MI are denied.
var rbacAdminCondition = '((!(ActionMatches{\'Microsoft.Authorization/roleAssignments/write\'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${costMgmtContributorRoleId}})) AND ((!(ActionMatches{\'Microsoft.Authorization/roleAssignments/delete\'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${costMgmtContributorRoleId}}))'

resource miRbacAdmin 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantMiRbacAdminOnSubscription) {
  name: guid(subscription().id, appName, rbacAdminRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', rbacAdminRoleId)
    principalId: app.outputs.containerAppPrincipalId
    principalType: 'ServicePrincipal'
    conditionVersion: '2.0'
    condition: rbacAdminCondition
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
output COLLECT_JOB_NAME string = app.outputs.collectJobName
