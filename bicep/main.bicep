//  CSP Cost Reporting – export storage (resource group scope)
// Creates: Storage Account, Blob Container for cost exports, Table + Blob container for app cache
// and Role Assignments for the export managed identity and (optionally) the Container App MI.
//
// This resource group hosts ONLY export / storage concerns.
// The Container App, Key Vault, and Container Registry live in a separate RG (see app.bicep).

targetScope = 'resourceGroup'

@description('Storage account name (must be globally unique, lowercase, 3–24 chars).')
param storageAccountName string

@description('Azure region. Defaults to resource group location.')
param location string = resourceGroup().location

@description('Blob container name for cost exports.')
param exportContainerName string = 'cost-exports'

@description('Blob container name used by the app for large cache payloads.')
param cacheContainerName string = 'cmcspcache'

@description('Azure Table name used by the app for small cache entries.')
param cacheTableName string = 'cmcspcache'

@description('Allow public access on blobs (false for security).')
param allowBlobPublicAccess bool = false

@description('Principal ID of the Cost Management export managed identity (for write access). Leave empty to skip.')
param exportManagedIdentityPrincipalId string = ''

@description('Principal ID of the Container App managed identity (for read cache access). Leave empty to skip.')
param appManagedIdentityPrincipalId string = ''

@description('Principal ID of the cache cleanup job managed identity (for delete access to expired cache entries). Leave empty to skip.')
param cleanupJobManagedIdentityPrincipalId string = ''

@description('Principal ID of the cost collector job managed identity (cache read/write + export read + audit write). Leave empty to skip.')
param collectJobManagedIdentityPrincipalId string = ''

@description('Tags to apply to all resources.')
param tags object = {}

// ── Storage Account ──────────────────────────────────────────────────────────

resource sa 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  tags: tags
  properties: {
    allowBlobPublicAccess: allowBlobPublicAccess
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    allowSharedKeyAccess: true // required for Cost Management SAS fallback
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' existing = {
  name: 'default'
  parent: sa
}

// ── Containers ───────────────────────────────────────────────────────────────

resource exportContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: exportContainerName
  parent: blobService
  properties: {
    publicAccess: 'None'
    metadata: { purpose: 'azure-cost-management-exports' }
  }
}

resource cacheContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: cacheContainerName
  parent: blobService
  properties: {
    publicAccess: 'None'
    metadata: { purpose: 'cmcsp-app-cache-large-payloads' }
  }
}

// ── Table Storage (small cache entries) ─────────────────────────────────────

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-01-01' existing = {
  name: 'default'
  parent: sa
}

resource cacheTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-01-01' = {
  name: cacheTableName
  parent: tableService
}

// ── Role definitions ─────────────────────────────────────────────────────────

var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageBlobDataReaderRoleId      = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

// ── Role: export MI → Storage Blob Data Contributor ─────────────────────────

resource exportWriteRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(exportManagedIdentityPrincipalId)) {
  name: guid(sa.id, exportManagedIdentityPrincipalId, storageBlobDataContributorRoleId)
  scope: sa
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: exportManagedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── Roles: Container App MI → Blob Data Reader + Blob Data Contributor (cache) + Table Data Contributor ──

// Read access to all blobs (cost export CSVs + cache blob pointers).
resource appBlobReadRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(appManagedIdentityPrincipalId)) {
  name: guid(sa.id, appManagedIdentityPrincipalId, storageBlobDataReaderRoleId)
  scope: sa
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReaderRoleId)
    principalId: appManagedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Write access scoped to the cache container only.
// Required so AzureStorageCacheService can persist large cache payloads (> 60 KB) to blob storage,
// ensuring the cache survives Container App restarts and redeploys.
// The export container intentionally keeps read-only access (appBlobReadRole above).
resource appCacheBlobWriteRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(appManagedIdentityPrincipalId)) {
  name: guid(sa.id, appManagedIdentityPrincipalId, storageBlobDataContributorRoleId, 'cache')
  scope: cacheContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: appManagedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource appTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(appManagedIdentityPrincipalId)) {
  name: guid(sa.id, appManagedIdentityPrincipalId, storageTableDataContributorRoleId)
  scope: sa
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: appManagedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── Role: Container App MI → User Access Administrator (export provisioning) ───────
// Scoped to this storage account only. Required so ExportProvisioningService can
// dynamically grant Storage Blob Data Contributor to each new export resource MI.

var userAccessAdminRoleId = '18d7d88d-d35e-4fb5-a5c3-7773c20a72d9'

resource appUserAccessAdminRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(appManagedIdentityPrincipalId)) {
  name: guid(sa.id, appManagedIdentityPrincipalId, userAccessAdminRoleId)
  scope: sa
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', userAccessAdminRoleId)
    principalId: appManagedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── Outputs ──────────────────────────────────────────────────────────────────

output storageAccountResourceId string = sa.id
output storageAccountName string = sa.name
output storageAccountUri string = sa.properties.primaryEndpoints.blob
output exportContainerName string = exportContainer.name
output cacheContainerName string = cacheContainer.name
output cacheTableName string = cacheTable.name

// ── Roles: Cleanup Job MI → Table Data Contributor + Cache Blob Contributor ──
// Scoped to the storage account (table) and to the cache container (blob) so the
// cleanup job can delete expired rows and their associated large-payload blobs.

resource cleanupJobTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(cleanupJobManagedIdentityPrincipalId)) {
  name: guid(sa.id, cleanupJobManagedIdentityPrincipalId, storageTableDataContributorRoleId)
  scope: sa
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: cleanupJobManagedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource cleanupJobCacheBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(cleanupJobManagedIdentityPrincipalId)) {
  name: guid(sa.id, cleanupJobManagedIdentityPrincipalId, storageBlobDataContributorRoleId, 'cleanup')
  scope: cacheContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: cleanupJobManagedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── Roles: Collect Job MI → Blob Data Reader + Cache Blob Contributor + Table Data Contributor ──
// The collector runs the same data path as the app: reads cost-export blobs, writes large
// cache payloads to the cache container, and writes cache + audit rows to Table Storage.

resource collectJobBlobReadRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(collectJobManagedIdentityPrincipalId)) {
  name: guid(sa.id, collectJobManagedIdentityPrincipalId, storageBlobDataReaderRoleId)
  scope: sa
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReaderRoleId)
    principalId: collectJobManagedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource collectJobCacheBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(collectJobManagedIdentityPrincipalId)) {
  name: guid(sa.id, collectJobManagedIdentityPrincipalId, storageBlobDataContributorRoleId, 'collect')
  scope: cacheContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: collectJobManagedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource collectJobTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(collectJobManagedIdentityPrincipalId)) {
  name: guid(sa.id, collectJobManagedIdentityPrincipalId, storageTableDataContributorRoleId)
  scope: sa
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: collectJobManagedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}
