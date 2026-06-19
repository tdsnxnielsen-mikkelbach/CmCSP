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

@description('Container image for the cost collector job. Defaults to placeholder on first deploy; updated by the azd postdeploy hook.')
param collectJobImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

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

@description('When set, stamps the Container App with the azd-service-name tag so `azd deploy` can locate it. Leave empty for script-based deploys.')
param azdServiceName string = ''

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

// ── User-assigned identity for ACR pulls ─────────────────────────────────────
// A dedicated pull identity (rather than each resource's SystemAssigned identity)
// is required to break a chicken-and-egg on first deploy: the Container App / Job
// declare this ACR in `registries`, and Container Apps validates registry access
// while provisioning the very first revision. A SystemAssigned identity does not
// exist until the resource is created, so its AcrPull role can only be assigned
// *after* provisioning has already started — the validation then hangs until it
// times out. This identity is created and granted AcrPull up-front, and the app
// and job take an explicit dependency on that grant (see `dependsOn` below).

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acrPullIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${appName}-acrpull'
  location: location
  tags: tags
}

resource acrPullUamiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, acrPullIdentity.id, acrPullRoleId)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: acrPullIdentity.properties.principalId
    principalType: 'ServicePrincipal'
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
  tags: union(tags, empty(azdServiceName) ? {} : { 'azd-service-name': azdServiceName })
  identity: {
    // SystemAssigned: used at runtime for Key Vault + storage + ARM calls.
    // UserAssigned (acrPullIdentity): used only to pull the image from ACR.
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${acrPullIdentity.id}': {}
    }
  }
  // Ensure the pull identity already holds AcrPull before the first revision is
  // provisioned, otherwise registry validation hangs until it times out.
  dependsOn: [
    acrPullUamiRole
  ]
  properties: {
    managedEnvironmentId: caEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 80
        transport: 'http'
        allowInsecure: false
      }
      // Register this ACR for image pulls using the dedicated user-assigned
      // identity (granted AcrPull above). `azd provision` runs with the public
      // MCR placeholder image; the registry entry must already be present so the
      // later `azd deploy` image swap to <acr>/cmcsp/web-csp-cost can authenticate.
      registries: [
        {
          server: acr.properties.loginServer
          identity: acrPullIdentity.id
        }
      ]
      secrets: [
        // ClientSecret is stored in Key Vault and fetched at runtime via the
        // Container App's SystemAssigned managed identity (kvSecretsRole below).
        // The Key Vault secret is created by the azd postprovision hook before this is used.
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
              value: 'http://+:80'
            }
            // ── Cost Management API (Query mode) ────────────────────────────
            // TenantId and ClientId are plain strings; ClientSecret comes from Key Vault.
            {
              name: 'AzureCostManagement__TenantId'
              value: '' // set via az containerapp update (postprovision hook)
            }
            {
              name: 'AzureCostManagement__ClientId'
              value: '' // set via az containerapp update (postprovision hook)
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
            // ── Cost collector job coordinates (for the "Collect now" button) ─
            // The app's managed identity starts this job via ARM jobs/start
            // (granted by collectJobOperatorAssignment) and polls its status.
            {
              name: 'CollectorJob__SubscriptionId'
              value: subscription().subscriptionId
            }
            {
              name: 'CollectorJob__ResourceGroup'
              value: resourceGroup().name
            }
            {
              name: 'CollectorJob__JobName'
              value: '${appName}-collect'
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

// ── Cost Collector Job ───────────────────────────────────────────────────────
// Refreshes the shared cost-data cache (the four aggregate datasets every page reads).
// Two ways to run:
//   • Schedule – nightly at 02:00 UTC (cronExpression below).
//   • Manual   – started on demand from the dashboard "Collect now" button, which calls
//                the ARM `jobs/start` action using the Container App's managed identity
//                (granted via collectJobOperatorAssignment below).
// A Schedule-triggered job can still be started manually via the start API, so a single
// job resource serves both paths. Runtime storage/Key Vault access uses its own
// SystemAssigned identity (storage roles granted in main.bicep, KV role below).

resource collectJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${appName}-collect'
  location: location
  tags: tags
  identity: {
    // SystemAssigned: runtime access to storage (cache + audit), Key Vault and Cost Management.
    // UserAssigned (acrPullIdentity): used only to pull the image from ACR.
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${acrPullIdentity.id}': {}
    }
  }
  dependsOn: [
    acrPullUamiRole
  ]
  properties: {
    environmentId: caEnv.id
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 1800     // 30 min – cost collection across many subscriptions can be slow
      replicaRetryLimit: 1
      scheduleTriggerConfig: {
        cronExpression: '0 2 * * *'  // nightly at 02:00 UTC
        // Default parallelism: 1 — one replica collects every subscription. Per-subscription
        // partitioning is now data-safe (CostFact's natural key includes SubscriptionId, so
        // disjoint partitions never conflict). To fan out, set COLLECT_PARTITION_COUNT > 1 and
        // give each scheduled execution a distinct COLLECT_PARTITION_INDEX (0..count-1); the
        // collector then handles only `index % count` of the subscription set. Container Apps
        // Jobs provide no native per-replica task index, so use separate executions (or bump
        // parallelism only with an index source) rather than relying on replica identity.
        parallelism: 1
        replicaCompletionCount: 1
      }
      // Register this ACR using the dedicated pull identity (see Container App note above).
      // The job image is swapped to <acr>/cmcsp-collect by the postdeploy hook.
      registries: [
        {
          server: acr.properties.loginServer
          identity: acrPullIdentity.id
        }
      ]
      secrets: [
        // ClientSecret fetched from Key Vault via the job's SystemAssigned identity
        // (collectJobKvSecretsRole below). Used for the Cost Management Query API fallback.
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
          name: '${appName}-collect'
          image: collectJobImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'KeyVaultUri'
              value: kv.properties.vaultUri
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'COLLECT_TRIGGER'
              value: 'schedule' // overridden to 'manual' on UI-started executions
            }
            // ── Cost Management API (Query mode fallback) ───────────────────
            {
              name: 'AzureCostManagement__TenantId'
              value: '' // set via az containerapp job update (postprovision hook)
            }
            {
              name: 'AzureCostManagement__ClientId'
              value: '' // set via az containerapp job update (postprovision hook)
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
              value: '' // set via postprovision hook
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
              value: '' // set via postprovision hook
            }
            // ── Azure distributed cache (also hosts the audit table) ────────
            {
              name: 'AzureCostManagement__AzureCache__Enabled'
              value: 'true'
            }
            {
              name: 'AzureCostManagement__AzureCache__StorageAccountUri'
              value: '' // set via postprovision hook
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
    }
  }
}

// ── Role: Collect Job MI → Key Vault Secrets User ───────────────────────────

resource collectJobKvSecretsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, collectJob.id, kvSecretsUserRoleId)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: collectJob.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Custom role: start the collect job + read its execution status ──────────
// No built-in role grants only `Microsoft.App/jobs/start`, so a tightly-scoped
// custom role is defined and assigned to the Container App MI on the collect job.
// This lets the dashboard "Collect now" button start the job and poll execution
// status without granting broad Container Apps management rights.

resource collectJobOperatorRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, appName, 'collect-job-operator')
  properties: {
    roleName: 'CmCSP Collect Job Operator (${appName})'
    description: 'Start the cost collector job and read its execution status.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.App/jobs/read'
          'Microsoft.App/jobs/start/action'
          'Microsoft.App/jobs/executions/read'
        ]
        notActions: []
      }
    ]
    assignableScopes: [
      resourceGroup().id
    ]
  }
}

resource collectJobOperatorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(collectJob.id, containerApp.id, collectJobOperatorRole.id)
  scope: collectJob
  properties: {
    roleDefinitionId: collectJobOperatorRole.id
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Outputs ──────────────────────────────────────────────────────────────────

output containerAppFqdn string = containerApp.properties.configuration.ingress.fqdn
output containerAppName string = containerApp.name
output containerAppPrincipalId string = containerApp.identity.principalId
output collectJobName string = collectJob.name
output collectJobPrincipalId string = collectJob.identity.principalId
output acrLoginServer string = acr.properties.loginServer
output keyVaultUri string = kv.properties.vaultUri
output logAnalyticsWorkspaceId string = logAnalytics.id
