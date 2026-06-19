using 'main.bicep'

param appName = 'cmcsp'

param deployDataPlatform = true

param environmentName = 'cost'

param grantMiRbacAdminOnSubscription = true

param location = 'swedencentral'

param resourceGroupName = 'rg-cmcsp-cost'

param sqlAdminLogin = 'admin@mikkelsaiogspas.onmicrosoft.com'

param sqlAdminObjectId = '614def32-8116-473a-b826-f8d86bd05675'

param sqlAdminPrincipalType = 'User'

param storageAccountName = 'cmcspst5eohwj'
