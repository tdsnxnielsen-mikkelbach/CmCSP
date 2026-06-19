// CmCSP – azd entry-point template (subscription scope).
//
// Composes the existing resource-group-scoped modules into a single deployment:
//   1. app    (./modules/app.bicep)     – ACR, Key Vault, Container Apps env,
//                                          Container App + collector Job.
//   2. storage(./modules/storage.bicep) – Storage Account, containers, table, and
//                                          the role assignments for the app/collect MIs.
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

@description('Base application name (Container App, collector job, resource prefixes).')
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

@description('''Phase 7: when true, grants the Container App + collector managed identities the built-in
'Reader' role on THIS subscription so the app can read live Azure resource inventory (Azure Resource Graph),
orphaned-resource queries and reservation recommendations for the Optimization page. Only covers the
deployment subscription — for other target subscriptions deploy ./modules/reader-sub.bicep per subscription
(see docs/azure-roles.md) or assign Reader once at a management group.''')
param grantReaderOnSubscription bool = true

@description('''Phase 8: when true, grants the Container App + collector managed identities the built-in
'Security Reader' and 'Carbon Optimization Reader' roles on THIS subscription, for the Security Posture
(Defender for Cloud secure score) and Sustainability (Carbon Optimization emissions) pages. NOTE: the
'Reader' grant above already covers both feeds (Microsoft.Security/*/read and emissions viewing), so this
is only needed for least-privilege deployments where grantReaderOnSubscription is false. Default false to
avoid redundant assignments. Only covers the deployment subscription.''')
param grantSecurityCarbonRolesOnSubscription bool = false

@description('''Phase 4: when true, provisions the managed-identity-only data platform — an Azure SQL
serverless database and an Azure Managed Redis (Balanced_B0 / "Basic") cache. Cost-incurring. Requires
the SQL Entra admin params below. The contained-DB users and schema are applied by the postprovision hook.''')
param deployDataPlatform bool = false

@description('Entra admin login (UPN or group name) for the SQL server. Required when deployDataPlatform is true; typically the deployer running azd.')
param sqlAdminLogin string = ''

@description('Entra object ID (SID) of the SQL admin login. Required when deployDataPlatform is true.')
param sqlAdminObjectId string = ''

@description('Principal type of the SQL Entra admin.')
@allowed([
  'User'
  'Group'
  'Application'
])
param sqlAdminPrincipalType string = 'User'

@description('SQL server name. Leave empty to derive a globally-unique name.')
param sqlServerName string = ''

@description('Azure Managed Redis cluster name. Leave empty to derive a globally-unique name.')
param redisName string = ''

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
var resolvedSqlServer = empty(sqlServerName) ? '${appName}-sql-${suffix}' : sqlServerName
var resolvedRedis = empty(redisName) ? '${appName}-redis-${suffix}' : redisName

var allTags = union(tags, { 'azd-env-name': environmentName })

// ── Resource group ────────────────────────────────────────────────────────────

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: rgName
  location: location
  tags: allTags
}

// ── App infrastructure (ACR, Key Vault, Container Apps env, Container App, Job) ──

module app './modules/app.bicep' = {
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
// Consumes the app/collect managed-identity principal IDs → declarative RBAC,
// replacing the old "deploy app, read principalId, re-deploy storage" pass.

module storage './modules/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    storageAccountName: resolvedStorage
    location: location
    appManagedIdentityPrincipalId: app.outputs.containerAppPrincipalId
    collectJobManagedIdentityPrincipalId: app.outputs.collectJobPrincipalId
    tags: allTags
  }
}

// ── Phase 4: data platform (Azure SQL serverless + Azure Managed Redis) ─────────
// Gated behind deployDataPlatform. Authenticates with managed identity only — the
// Container App + collect job MIs get Redis data access here; their SQL contained-DB
// users and the schema are applied by the postprovision hook.

module data './modules/data.bicep' = if (deployDataPlatform) {
  name: 'data'
  scope: rg
  params: {
    location: location
    sqlServerName: resolvedSqlServer
    redisName: resolvedRedis
    sqlAdminLogin: sqlAdminLogin
    sqlAdminObjectId: sqlAdminObjectId
    sqlAdminPrincipalType: sqlAdminPrincipalType
    redisDataAccessPrincipalIds: [
      app.outputs.containerAppPrincipalId
      app.outputs.collectJobPrincipalId
    ]
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
var costMgmtContributorRoleId = '434105ed-43f6-45c7-a02f-909b2ba83430'

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

// ── Phase 7: Reader for live inventory / optimization ──────────────────────────
// Grants the app + collector managed identities the built-in 'Reader' role on this
// subscription so the Optimization page can query Azure Resource Graph (inventory,
// orphaned resources) and Microsoft.Consumption reservation recommendations. Reader is
// purely read-only. Other target subscriptions need ./modules/reader-sub.bicep.

var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7' // Reader (built-in)

resource appReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantReaderOnSubscription) {
  name: guid(subscription().id, appName, 'app', readerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
    principalId: app.outputs.containerAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource collectReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantReaderOnSubscription) {
  name: guid(subscription().id, appName, 'collect', readerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
    principalId: app.outputs.collectJobPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── Phase 8: least-privilege Security Reader + Carbon Optimization Reader ───────
// The Reader grant above already covers Defender for Cloud secure score reads and Carbon
// Optimization emissions viewing. These narrower roles are offered for deployments that prefer
// not to grant full Reader (set grantReaderOnSubscription=false and this param=true). Both roles
// are read-only and scoped to the deployment subscription.

var securityReaderRoleId = '39bc4728-0917-49c7-9d2c-d95423bc2eb4' // Security Reader (built-in)
var carbonReaderRoleId   = 'fa0d39e6-28e5-40cf-8521-1eb320653a4c' // Carbon Optimization Reader (built-in)

resource appSecurityReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantSecurityCarbonRolesOnSubscription) {
  name: guid(subscription().id, appName, 'app', securityReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', securityReaderRoleId)
    principalId: app.outputs.containerAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource collectSecurityReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantSecurityCarbonRolesOnSubscription) {
  name: guid(subscription().id, appName, 'collect', securityReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', securityReaderRoleId)
    principalId: app.outputs.collectJobPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource appCarbonReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantSecurityCarbonRolesOnSubscription) {
  name: guid(subscription().id, appName, 'app', carbonReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', carbonReaderRoleId)
    principalId: app.outputs.containerAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource collectCarbonReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantSecurityCarbonRolesOnSubscription) {
  name: guid(subscription().id, appName, 'collect', carbonReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', carbonReaderRoleId)
    principalId: app.outputs.collectJobPrincipalId
    principalType: 'ServicePrincipal'
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
output COLLECT_JOB_NAME string = app.outputs.collectJobName

// ── Phase 4 data-platform outputs (empty unless deployDataPlatform = true) ──────
output DATA_PLATFORM_ENABLED bool = deployDataPlatform
output SQL_SERVER_NAME string = data.?outputs.sqlServerName ?? ''
output SQL_SERVER_FQDN string = data.?outputs.sqlServerFqdn ?? ''
output SQL_DATABASE_NAME string = data.?outputs.sqlDatabaseName ?? ''
output SQL_CONNECTION_STRING string = data.?outputs.sqlConnectionString ?? ''
output REDIS_NAME string = data.?outputs.redisName ?? ''
output REDIS_HOST_NAME string = data.?outputs.redisHostName ?? ''
output REDIS_PORT int = data.?outputs.redisPort ?? 0
