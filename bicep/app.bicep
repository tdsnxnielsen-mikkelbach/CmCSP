// CmCSP – Application infrastructure (resource group scope)
// Creates: Storage Account, Container Registry, Key Vault, Log Analytics, Container Apps Environment,
//          Container App (with SystemAssigned managed identity).
//
// All resources share a single resource group (rg-cmcsp-app).
// Storage concerns (export + cache) are also deployed here via main.bicep.

targetScope = 'resourceGroup'

@description('Base name used for the Container App and related resources.')
param appName string = 'cmcsp'

@description('Azure Container Registry name (must be globally unique, alphanumeric, 5–50 chars).')
param acrName string

@description('Key Vault name (globally unique, 3–24 chars, alphanumeric and hyphens).')
param keyVaultName string

@description('Azure region.')
param location string = 'swedencentral'

@description('Container image to deploy, e.g. cmcspacr.azurecr.io/cmcsp:latest. Leave empty for first-time deploy (placeholder image used).')
param containerImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

@description('CPU allocation for the Container App (vCPU).')
param containerCpu string = '0.5'

@description('Memory allocation for the Container App.')
param containerMemory string = '1Gi'

@description('Minimum replica count (0 = scale to zero when idle).')
param minReplicas int = 0

@description('Maximum replica count.')
param maxReplicas int = 2

@description('Tags to apply to all resources.')
param tags object = {}

// ── Log Analytics Workspace ──────────────────────────────────────────────────

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${appName}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// ── Azure Container Registry ─────────────────────────────────────────────────

resource acr 'Microsoft.ContainerRegistry/registries@2023-01-01-preview' = {
  name: acrName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false // use managed identity pull — no username/password
  }
}

// ── Key Vault ────────────────────────────────────────────────────────────────
// Stores secrets referenced as environment variables by the Container App.
// The Container App managed identity is granted 'Key Vault Secrets User'.

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true   // use RBAC (not access policies)
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

// ── Container Apps Environment ───────────────────────────────────────────────

resource caEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${appName}-env'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// ── Container App ────────────────────────────────────────────────────────────

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: caEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      // Only configure ACR registry auth when the image is actually from this ACR.
      // On the initial deploy (MCR placeholder), leave registries empty so Azure
      // does not attempt to validate ACR access before the AcrPull role is assigned.
      registries: contains(containerImage, acr.properties.loginServer) ? [
        {
          server: acr.properties.loginServer
          identity: 'system'   // pull image using the SystemAssigned identity
        }
      ] : []
      secrets: [
        // ClientSecret is stored in Key Vault and fetched at runtime via the
        // Container App's SystemAssigned managed identity (kvSecretsRole below).
        // The Key Vault secret is created by deploy.ps1 Phase 5 before this is used.
        {
          name: 'client-secret'
          keyVaultUrl: '${kv.properties.vaultUri}secrets/CmCSP--ClientSecret'
          identity: 'system'
        }
      ]
    }
    template: {
      containers: [
        {
          name: appName
          image: containerImage
          resources: {
            cpu: json(containerCpu)
            memory: containerMemory
          }
          env: [
            // ── Core configuration ──────────────────────────────────────────
            // All AzureCostManagement settings flow through environment variables.
            // Format: double-underscore maps to colon in ASP.NET Core config.
            // Secret values (ClientSecret, SubscriptionIds, ConnectionStrings)
            // should be stored in Key Vault and referenced here via secretRef.
            {
              name: 'KeyVaultUri'
              value: kv.properties.vaultUri
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://+:8080'
            }
            // ── Cost Management API (Query mode) ────────────────────────────
            // TenantId and ClientId are plain strings; ClientSecret comes from Key Vault.
            {
              name: 'AzureCostManagement__TenantId'
              value: '' // set via az containerapp update (deploy.ps1 Phase 6)
            }
            {
              name: 'AzureCostManagement__ClientId'
              value: '' // set via az containerapp update (deploy.ps1 Phase 6)
            }
            {
              name: 'AzureCostManagement__ClientSecret'
              secretRef: 'client-secret'
            }
            // ── Blob Export mode ────────────────────────────────────────────
            {
              name: 'AzureCostManagement__ExportBlob__Enabled'
              value: 'true'
            }
            {
              name: 'AzureCostManagement__ExportBlob__StorageAccountUri'
              value: '' // set to https://<exportStorageAccount>.blob.core.windows.net
            }
            {
              name: 'AzureCostManagement__ExportBlob__ContainerName'
              value: 'cost-exports'
            }
            {
              name: 'AzureCostManagement__ExportBlob__BlobPrefix'
              value: 'exports'
            }            
            {
              name: 'AzureCostManagement__ExportBlob__StorageAccountResourceId'
              value: '' // set to the ARM resource ID of the export storage account
            }            // ── Azure distributed cache ─────────────────────────────────────
            {
              name: 'AzureCostManagement__AzureCache__Enabled'
              value: 'true'
            }
            {
              name: 'AzureCostManagement__AzureCache__StorageAccountUri'
              value: '' // set to https://<exportStorageAccount>.blob.core.windows.net
            }
            {
              name: 'AzureCostManagement__AzureCache__TableName'
              value: 'cmcspcache'
            }
            {
              name: 'AzureCostManagement__AzureCache__CacheContainerName'
              value: 'cmcspcache'
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

// ── Role: Container App MI → ACR Pull ────────────────────────────────────────

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, containerApp.id, acrPullRoleId)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Role: Container App MI → Key Vault Secrets User ─────────────────────────

var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource kvSecretsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, containerApp.id, kvSecretsUserRoleId)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Role: Container App MI → Key Vault Secrets Officer (write secrets) ───────
// Required so the app can persist user-added subscription IDs to Key Vault.

var kvSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource kvSecretsOfficerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, containerApp.id, kvSecretsOfficerRoleId)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsOfficerRoleId)
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Outputs ──────────────────────────────────────────────────────────────────

output containerAppFqdn string = containerApp.properties.configuration.ingress.fqdn
output containerAppPrincipalId string = containerApp.identity.principalId
output acrLoginServer string = acr.properties.loginServer
output keyVaultUri string = kv.properties.vaultUri
output logAnalyticsWorkspaceId string = logAnalytics.id
