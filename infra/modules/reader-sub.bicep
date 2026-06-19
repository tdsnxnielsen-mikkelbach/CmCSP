// CmCSP – Reader role assignment (subscription scope) — Phase 7
//
// Grants the CmCSP app and/or collector managed identities the built-in 'Reader' role on a
// TARGET subscription so the Optimization page can read live Azure resource inventory via Azure
// Resource Graph (inventory, orphaned/idle resources) and Microsoft.Consumption reservation
// recommendations. Reader is purely read-only — no write, no cost-management write.
//
// The deployment subscription is handled by main.bicep (grantReaderOnSubscription). Use THIS
// module to extend coverage to every OTHER subscription you have configured in
// AzureCostManagement:SubscriptionIds. Deploy it once per target subscription:
//
//   az deployment sub create \
//     --subscription <targetSubscriptionId> \
//     --location <region> \
//     --template-file infra/modules/reader-sub.bicep \
//     --parameters appPrincipalId=<containerAppPrincipalId> collectPrincipalId=<collectJobPrincipalId>
//
// The principal IDs are surfaced by main.bicep / the postprovision hook (the Container App and
// collector job system-assigned identities). Pass an empty string to skip either assignment.

targetScope = 'subscription'

@description('Object (principal) ID of the Container App system-assigned managed identity. Empty to skip.')
param appPrincipalId string = ''

@description('Object (principal) ID of the collector job system-assigned managed identity. Empty to skip.')
param collectPrincipalId string = ''

@description('Stable name discriminator so repeat deployments produce deterministic assignment names.')
param nameSeed string = 'cmcsp'

var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7' // Reader (built-in)

resource appReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(appPrincipalId)) {
  name: guid(subscription().id, nameSeed, 'app', readerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
    principalId: appPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource collectReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(collectPrincipalId)) {
  name: guid(subscription().id, nameSeed, 'collect', readerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
    principalId: collectPrincipalId
    principalType: 'ServicePrincipal'
  }
}
