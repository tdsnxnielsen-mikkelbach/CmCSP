// CmCSP – Phase 4 data platform (resource group scope)
// Provisions the managed-identity-only data services that replace the Table/Blob
// persistence and the in-process + Storage cache:
//   • Azure SQL Database (serverless tier) – durable cost rows, audit, subscription store.
//   • Azure Managed Redis (Balanced_B0 / "Basic") – shared cache across web + jobs.
//
// Authentication is Entra-only. The SQL server is created with Entra-only auth and an
// Entra admin (the deployer); access keys are disabled on the Redis database and each
// managed identity is granted access via an Entra access-policy assignment.
//
// The contained-database users for the managed identities (CREATE USER ... FROM EXTERNAL
// PROVIDER + db_datareader/db_datawriter) and the schema (infra/sql/schema.sql) are applied
// by the postprovision hook, since T-SQL cannot be expressed in Bicep.

targetScope = 'resourceGroup'

@description('Azure region. Defaults to resource group location.')
param location string = resourceGroup().location

@description('Logical SQL server name (globally unique, lowercase, 1–63 chars).')
param sqlServerName string

@description('SQL database name.')
param sqlDatabaseName string = 'cmcsp'

@description('Azure Managed Redis cluster name (globally unique, alphanumeric + hyphens, 1–60 chars).')
param redisName string

@description('Max vCores for the serverless database (auto-scales down to minCapacity).')
param sqlMaxVCores int = 2

@description('Auto-pause delay in minutes for the serverless database (-1 disables auto-pause).')
param sqlAutoPauseDelayMinutes int = 60

@description('Entra admin for the SQL server. Login name (UPN or group display name) of the deployer.')
param sqlAdminLogin string

@description('Entra object ID (SID) of the SQL admin login above.')
param sqlAdminObjectId string

@description('Principal type of the SQL Entra admin.')
@allowed([
  'User'
  'Group'
  'Application'
])
param sqlAdminPrincipalType string = 'User'

@description('Object IDs of the managed identities that need Redis data access (Container App + collect job).')
param redisDataAccessPrincipalIds string[] = []

@description('Tags to apply to all resources.')
param tags object = {}

// ── Azure SQL (serverless, Entra-only auth) ──────────────────────────────────

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: sqlAdminPrincipalType
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: tenant().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

// Allow other Azure services (the Container App + jobs) to reach the server.
// 0.0.0.0–0.0.0.0 is the special "Allow Azure services" rule, not the public internet.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sqlServer
  name: 'AllowAllAzureIPs'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5_${sqlMaxVCores}'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: sqlMaxVCores
  }
  properties: {
    autoPauseDelay: sqlAutoPauseDelayMinutes
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368 // 32 GB
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

// ── Azure Managed Redis (Balanced_B0 ≈ Basic, Entra-only) ────────────────────

resource redis 'Microsoft.Cache/redisEnterprise@2025-05-01-preview' = {
  name: redisName
  location: location
  tags: tags
  sku: {
    name: 'Balanced_B0'
  }
  properties: {
    minimumTlsVersion: '1.2'
    highAvailability: 'Disabled' // Basic tier – single node, no HA
  }
}

resource redisDatabase 'Microsoft.Cache/redisEnterprise/databases@2025-05-01-preview' = {
  parent: redis
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    clusteringPolicy: 'OSSCluster'
    evictionPolicy: 'VolatileLRU' // TTL-aware LRU – replaces the CacheCleanupJob
    port: 10000
    accessKeysAuthentication: 'Disabled' // Entra-only data access
  }
}

// Grant each managed identity full data access via the built-in 'default' access policy.
resource redisAccess 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-05-01-preview' = [
  for principalId in redisDataAccessPrincipalIds: {
    parent: redisDatabase
    name: 'apa${uniqueString(principalId)}'
    properties: {
      accessPolicyName: 'default'
      user: {
        objectId: principalId
      }
    }
  }
]

// ── Outputs ──────────────────────────────────────────────────────────────────

output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name
@description('Entra-token (no-secret) ADO.NET connection string for Microsoft.Data.SqlClient.')
output sqlConnectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;'
output redisName string = redis.name
output redisHostName string = redis.properties.hostName
output redisPort int = redisDatabase.properties.port
