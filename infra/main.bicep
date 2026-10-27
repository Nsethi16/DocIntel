targetScope = 'resourceGroup'

@description('Location for all resources.')
param location string = resourceGroup().location

@description('Environment name suffix (e.g. dev, uat, prod).')
param environmentName string = 'dev'

@description('Provision shared infrastructure without replacing the app while preparing its image/identity.')
param deployApplication bool = true

@description('Provision runtime resource roles by default. Only skip after this installation has provisioned them successfully.')
param deployRuntimeRoles bool = true

@description('Entra directory ID for verified sign-in. Application memberships use separate tenant slugs.')
param directoryTenantId string

@description('Empty retains legacy sample seeding. Customer installers select their first workspace explicitly.')
param bootstrapTenantId string = ''

@description('Display name of the first customer workspace; never overwrites an existing workspace.')
param bootstrapTenantDisplayName string = ''

@description('Confidential web application registration ID with managed-identity federation configured.')
param webClientId string

@description('Separate API resource application registration ID.')
param apiClientId string

@description('Server-owned Memberships and PlatformAdministrators. No credentials belong in this object.')
@secure()
param identityGrants object = {}

@description('Container image to deploy (e.g. <acr>.azurecr.io/docintel-api:latest).')
param containerImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

@description('Optional Azure Container Registry name for Managed Identity AcrPull.')
param acrName string = ''

@description('Keep the legacy ACR path by default. False pulls an explicitly supplied public image without a registry or pull role.')
param enableContainerRegistry bool = true

@description('Existing registry reuse leaves its SKU, networking, and authentication settings unchanged.')
param reuseRegistry bool = false

@description('Registry pull role GUID. AcrPull is the default; ABAC registries require an explicitly supported role.')
param registryPullRoleDefinitionId string = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

@description('Runtime managed identity name. Empty uses uami-docintel-<environmentName>.')
param runtimeIdentityName string = ''

@description('Use the existing runtime identity without redeploying it.')
param reuseRuntimeIdentity bool = false

@description('Existing Container Apps environment resource ID, including another resource group in this subscription. Skips environment and workspace creation.')
param existingEnvironmentId string = ''

@description('Existing public HTTPS origin. Empty derives the origin from the selected app and environment.')
param publicOrigin string = ''

@description('Container App name. Empty uses ca-docintel-<environmentName>.')
param containerAppName string = ''

@description('Storage account name. Empty uses the generated default.')
param storageAccountName string = ''

@description('Use the existing storage account without changing its root, Blob service, or Queue service configuration.')
param reuseStorageAccount bool = false

@description('Existing required blob container names. Listed containers are not redeployed or reconfigured.')
param existingBlobContainers array = []

@description('Existing required queue names. Listed queues are not redeployed or reconfigured.')
param existingQueues array = []

@description('Cosmos DB account name. Empty uses the generated default.')
param cosmosAccountNameOverride string = ''

@description('Use the existing Cosmos DB account without changing its root configuration.')
param reuseCosmosAccount bool = false

@description('Use the existing DocIntel database without changing its throughput or other database settings.')
param reuseCosmosDatabase bool = false

@description('Existing required Cosmos container names. Listed containers retain indexing, TTL, and all other settings.')
param existingCosmosContainers array = []

@description('Key Vault name. Empty uses the generated default.')
param keyVaultNameOverride string = ''

@description('Use the existing Key Vault without changing its root configuration.')
param reuseKeyVault bool = false

@description('Use the existing docintel-identity key without changing its key material or rotation policy.')
param reuseIdentityKey bool = false

@description('Foundry account name. Empty uses the generated default when an account is required.')
param foundryAccountNameOverride string = ''

@description('Use the existing Foundry account without changing its identity, SKU, networking, or authentication settings.')
param reuseFoundryAccount bool = false

@description('Provision the requested model deployments. False leaves existing deployments unchanged.')
param deployModels bool = true

@description('Optional existing Microsoft Foundry Inference endpoint. When empty, a new Foundry (AIServices) resource is provisioned automatically.')
param foundryInferenceEndpoint string = ''

@description('Optional existing Azure OpenAI endpoint. Defaults to the provisioned Foundry resource when empty.')
param azureOpenAiEndpoint string = ''

@description('Separate Foundry resource root for native Microsoft Decision scoring. Empty uses the provisioned Foundry resource.')
param decisionEndpoint string = ''

@description('Case-sensitive native Microsoft-Decision-1 deployment name.')
param decisionDeploymentName string = 'Decision-1'

@description('Optional existing Azure AI Translator endpoint. Defaults to the provisioned Foundry resource when empty.')
param translatorEndpoint string = ''

@description('Content Understanding Foundry resource root endpoint in a supported region. Empty uses the provisioned Foundry resource.')
param contentUnderstandingEndpoint string = ''

@description('Foundry model deployments to provision automatically under the Foundry account.')
param modelDeployments array = concat([
  {
    name: 'gpt-5-mini'
    format: 'OpenAI'
    modelName: 'gpt-5-mini'
    modelVersion: '2025-08-07'
    skuName: 'GlobalStandard'
    capacity: 10
  }
], empty(decisionEndpoint) ? [
  {
    name: decisionDeploymentName
    format: 'Microsoft'
    modelName: 'Microsoft-Decision-1'
    modelVersion: '1'
    skuName: 'GlobalStandard'
    capacity: 1
  }
] : [])

var uniqueSuffix = uniqueString(resourceGroup().id, environmentName)
var uamiName = !empty(runtimeIdentityName) ? runtimeIdentityName : 'uami-docintel-${environmentName}'
var logAnalyticsName = 'law-docintel-${environmentName}-${uniqueSuffix}'
var caeName = !empty(existingEnvironmentId) ? last(split(existingEnvironmentId, '/')) : 'cae-docintel-${environmentName}'
var caeSubscriptionId = !empty(existingEnvironmentId) ? split(existingEnvironmentId, '/')[2] : subscription().subscriptionId
var caeResourceGroupName = !empty(existingEnvironmentId) ? split(existingEnvironmentId, '/')[4] : resourceGroup().name
var appName = !empty(containerAppName) ? containerAppName : 'ca-docintel-${environmentName}'
var storageName = !empty(storageAccountName) ? storageAccountName : take('sadocintel${environmentName}${uniqueSuffix}', 24)
var cosmosAccountName = !empty(cosmosAccountNameOverride)
  ? cosmosAccountNameOverride
  : 'cosmos-docintel-${environmentName}-${uniqueSuffix}'
var cosmosDatabaseName = 'DocIntel'
var foundryName = !empty(foundryAccountNameOverride)
  ? foundryAccountNameOverride
  : 'aif-docintel-${environmentName}-${uniqueSuffix}'
var keyVaultName = !empty(keyVaultNameOverride) ? keyVaultNameOverride : 'kv-di-${take(environmentName, 3)}-${uniqueSuffix}'
var effectiveAcrName = !empty(acrName) ? acrName : take('acrdocintel${environmentName}${uniqueSuffix}', 50)
var hasFoundryAccount = reuseFoundryAccount || !empty(foundryAccountNameOverride) || empty(foundryInferenceEndpoint)
var createFoundryResource = hasFoundryAccount && !reuseFoundryAccount
var createEnvironment = empty(existingEnvironmentId)

// Existing references select the same roots in both modes; explicit dependencies order fresh creation.
resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: uamiName
}

// 1. User-Assigned Managed Identity (Passwordless Auth for Foundry, Translator, Blob, and ACR)
resource newUami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (!reuseRuntimeIdentity) {
  name: uamiName
  location: location
}

resource foundry 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: foundryName
}

// 1b. Microsoft Foundry (Azure AI Services multi-service account: Models + OpenAI + Translator)
resource newFoundry 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (createFoundryResource) {
  name: foundryName
  location: location
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uami.id}': {}
    }
  }
  properties: {
    customSubDomainName: foundryName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
  dependsOn: [
    newUami
  ]
}

// 1c. Model Deployments inside the Microsoft Foundry resource (serialized with @batchSize(1))
@batchSize(1)
resource foundryModelDeployments 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = [for m in modelDeployments: if (hasFoundryAccount && deployModels) {
  parent: foundry
  name: m.name
  sku: {
    name: m.?skuName ?? 'GlobalStandard'
    capacity: int(m.?capacity ?? 10)
  }
  properties: {
    model: {
      format: m.?format ?? 'OpenAI'
      name: m.modelName
      version: m.modelVersion
    }
  }
  dependsOn: [
    newFoundry
  ]
}]

// Grant UAMI "Cognitive Services User", "Cognitive Services OpenAI User", and "Azure AI User (Foundry)" on the provisioned Foundry resource
var cognitiveServicesUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
var cognitiveServicesOpenAiUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
var foundryAiUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')

resource foundryCogUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (hasFoundryAccount && deployRuntimeRoles) {
  name: guid(resourceGroup().id, uami.id, cognitiveServicesUserRoleId)
  scope: foundry
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: cognitiveServicesUserRoleId
  }
  dependsOn: [
    newFoundry
    newUami
  ]
}

resource foundryOpenAiUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (hasFoundryAccount && deployRuntimeRoles) {
  name: guid(resourceGroup().id, uami.id, cognitiveServicesOpenAiUserRoleId)
  scope: foundry
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: cognitiveServicesOpenAiUserRoleId
  }
  dependsOn: [
    newFoundry
    newUami
  ]
}

resource foundryAiUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (hasFoundryAccount && deployRuntimeRoles) {
  name: guid(resourceGroup().id, uami.id, foundryAiUserRoleId)
  scope: foundry
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: foundryAiUserRoleId
  }
  dependsOn: [
    newFoundry
    newUami
  ]
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: effectiveAcrName
}

// 1d. Azure Container Registry (Provisioned automatically + AcrPull RBAC for UAMI)
resource newAcr 'Microsoft.ContainerRegistry/registries@2023-07-01' = if (enableContainerRegistry && !reuseRegistry) {
  name: effectiveAcrName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

var acrPullRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', registryPullRoleDefinitionId)
resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableContainerRegistry && deployRuntimeRoles) {
  name: guid(acr.id, uami.id, acrPullRoleId)
  scope: acr
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRoleId
  }
  dependsOn: [
    newAcr
    newUami
  ]
}

var foundrySubdomain = hasFoundryAccount
  ? (reuseFoundryAccount ? (foundry.properties.?customSubDomainName ?? '') : foundryName)
  : ''
var foundryRootEndpoint = hasFoundryAccount
  ? (!empty(foundrySubdomain) ? 'https://${foundrySubdomain}.services.ai.azure.com/' : foundry.properties.endpoint)
  : ''
var effectiveFoundryEndpoint = !empty(foundryInferenceEndpoint)
  ? foundryInferenceEndpoint
  : (!empty(foundryRootEndpoint)
    ? '${foundryRootEndpoint}${endsWith(foundryRootEndpoint, '/') ? '' : '/'}models'
    : '')
var effectiveOpenAiEndpoint = !empty(azureOpenAiEndpoint)
  ? azureOpenAiEndpoint
  : (!empty(foundrySubdomain) ? 'https://${foundrySubdomain}.openai.azure.com/' : foundryRootEndpoint)
var effectiveTranslatorEndpoint = !empty(translatorEndpoint)
  ? translatorEndpoint
  : (!empty(foundrySubdomain) ? 'https://${foundrySubdomain}.cognitiveservices.azure.com/' : foundryRootEndpoint)
var effectiveContentUnderstandingEndpoint = !empty(contentUnderstandingEndpoint)
  ? contentUnderstandingEndpoint
  : foundryRootEndpoint
var effectiveDecisionEndpoint = !empty(decisionEndpoint) ? decisionEndpoint : foundryRootEndpoint
var effectivePublicOrigin = !empty(publicOrigin) ? publicOrigin : 'https://${appName}.${cae.properties.defaultDomain}'
var effectiveEnvironmentId = !empty(existingEnvironmentId) ? existingEnvironmentId : cae.id

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageName
}

// 2. Storage Account for Harness Profiles & Split PDF Artifacts
resource newStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = if (!reuseStorageAccount) {
  name: storageName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: storage
  name: 'default'
}

resource newBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = if (!reuseStorageAccount) {
  parent: storage
  name: 'default'
  dependsOn: [
    newStorage
  ]
}

resource runContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (!contains(existingBlobContainers, 'ask-ai-runs')) {
  parent: blobService
  name: 'ask-ai-runs'
  properties: {
    publicAccess: 'None'
  }
  dependsOn: [
    newBlobService
  ]
}

resource identityKeys 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (!contains(existingBlobContainers, 'identity-keys')) {
  parent: blobService
  name: 'identity-keys'
  properties: {
    publicAccess: 'None'
  }
  dependsOn: [
    newBlobService
  ]
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' existing = {
  parent: storage
  name: 'default'
}

resource newQueueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = if (!reuseStorageAccount) {
  parent: storage
  name: 'default'
  dependsOn: [
    newStorage
  ]
}

resource dispatchQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' existing = {
  parent: queueService
  name: 'ask-ai'
}

resource newDispatchQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = if (!contains(existingQueues, 'ask-ai')) {
  parent: queueService
  name: 'ask-ai'
  dependsOn: [
    newQueueService
  ]
}

resource poisonQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = if (!contains(existingQueues, 'ask-ai-poison')) {
  parent: queueService
  name: 'ask-ai-poison'
  dependsOn: [
    newQueueService
  ]
}

resource documentQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' existing = {
  parent: queueService
  name: 'document-jobs'
}

resource newDocumentQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = if (!contains(existingQueues, 'document-jobs')) {
  parent: queueService
  name: 'document-jobs'
  dependsOn: [
    newQueueService
  ]
}

resource documentPoisonQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = if (!contains(existingQueues, 'document-jobs-poison')) {
  parent: queueService
  name: 'document-jobs-poison'
  dependsOn: [
    newQueueService
  ]
}

resource storageQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRuntimeRoles) {
  name: guid(storage.id, uami.id, '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  scope: storage
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  }
  dependsOn: [
    newStorage
    newUami
  ]
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource newKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' = if (!reuseKeyVault) {
  name: keyVaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 90
  }
}

resource identityEncryptionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' existing = {
  parent: keyVault
  name: 'docintel-identity'
}

resource newIdentityEncryptionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = if (!reuseIdentityKey) {
  parent: keyVault
  name: 'docintel-identity'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [
      'wrapKey'
      'unwrapKey'
    ]
    rotationPolicy: {
      attributes: {
        expiryTime: 'P180D'
      }
      lifetimeActions: [
        {
          trigger: {
            timeAfterCreate: 'P90D'
          }
          action: {
            type: 'rotate'
          }
        }
      ]
    }
  }
  dependsOn: [
    newKeyVault
  ]
}

resource identityKeyRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRuntimeRoles) {
  name: guid(identityEncryptionKey.id, uami.id, 'e147488a-f6f5-4113-8e2d-b22465e65bf6')
  scope: identityEncryptionKey
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e147488a-f6f5-4113-8e2d-b22465e65bf6')
  }
  dependsOn: [
    newIdentityEncryptionKey
    newKeyVault
    newUami
  ]
}

// Storage Blob Data Contributor role for UAMI
var storageBlobDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
resource storageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRuntimeRoles) {
  name: guid(storage.id, uami.id, storageBlobDataContributorRoleId)
  scope: storage
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRoleId
  }
  dependsOn: [
    newStorage
    newUami
  ]
}

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = {
  name: cosmosAccountName
}

// 2b. Serverless Azure Cosmos DB (Shared 'harnesses' and 'jobs' containers partitioned by /tenantId)
resource newCosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = if (!reuseCosmosAccount) {
  name: cosmosAccountName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
  }
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' existing = {
  parent: cosmosAccount
  name: cosmosDatabaseName
}

resource newCosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = if (!reuseCosmosDatabase) {
  parent: cosmosAccount
  name: cosmosDatabaseName
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
  }
  dependsOn: [
    newCosmosAccount
  ]
}

resource harnessesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = if (!contains(existingCosmosContainers, 'harnesses')) {
  parent: cosmosDatabase
  name: 'harnesses'
  properties: {
    resource: {
      id: 'harnesses'
      partitionKey: {
        paths: [
          '/tenantId'
        ]
        kind: 'Hash'
      }
    }
  }
  dependsOn: [
    newCosmosDatabase
  ]
}

resource jobsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = if (!contains(existingCosmosContainers, 'jobs')) {
  parent: cosmosDatabase
  name: 'jobs'
  properties: {
    resource: {
      id: 'jobs'
      partitionKey: {
        paths: [
          '/tenantId'
        ]
        kind: 'Hash'
      }
    }
  }
  dependsOn: [
    newCosmosDatabase
  ]
}

// Cosmos DB Built-in Data Contributor Role Assignment for UAMI
resource ocrUsageContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = if (!contains(existingCosmosContainers, 'ocr-usage')) {
  parent: cosmosDatabase
  name: 'ocr-usage'
  properties: {
    resource: {
      id: 'ocr-usage'
      partitionKey: {
        paths: [
          '/tenantId'
        ]
        kind: 'Hash'
      }
    }
  }
  dependsOn: [
    newCosmosDatabase
  ]
}

var cosmosDataContributorRoleId = '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
resource reviewsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = if (!contains(existingCosmosContainers, 'review-sessions')) {
  parent: cosmosDatabase
  name: 'review-sessions'
  properties: {
    resource: {
      id: 'review-sessions'
      partitionKey: {
        paths: [
          '/tenantId'
        ]
        kind: 'Hash'
      }
    }
  }
  dependsOn: [
    newCosmosDatabase
  ]
}

resource cosmosSqlRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = if (deployRuntimeRoles) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, uami.id, '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: cosmosDataContributorRoleId
    principalId: uami.properties.principalId
    scope: cosmosAccount.id
  }
  dependsOn: [
    newCosmosAccount
    newUami
  ]
}

// 3. Log Analytics + Container Apps Environment
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsName
}

resource newLogAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (createEnvironment) {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource cae 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  scope: resourceGroup(caeSubscriptionId, caeResourceGroupName)
  name: caeName
}

resource newCae 'Microsoft.App/managedEnvironments@2024-03-01' = if (createEnvironment) {
  name: caeName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
  dependsOn: [
    newLogAnalytics
  ]
}

// 4. Azure Container App (DocIntel Agent Harness Studio + Async Worker)
resource containerApp 'Microsoft.App/containerApps@2025-07-01' = if (deployApplication) {
  name: appName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uami.id}': {}
    }
  }
  properties: {
    environmentId: effectiveEnvironmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        allowInsecure: false
        targetPort: 8080
        transport: 'auto'
      }
      registries: enableContainerRegistry ? [
        {
          server: acr.properties.loginServer
          identity: uami.id
        }
      ] : []
    }
    template: {
      containers: [
        {
          name: 'docintel-api'
          image: containerImage
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
          env: [
            {
              name: 'DOTNET_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: uami.properties.clientId
            }
            {
              name: 'AzureAI__FoundryInferenceEndpoint'
              value: effectiveFoundryEndpoint
            }
            {
              name: 'AzureAI__AzureOpenAIEndpoint'
              value: effectiveOpenAiEndpoint
            }
            {
              name: 'AzureAI__TranslatorEndpoint'
              value: effectiveTranslatorEndpoint
            }
            {
              name: 'AzureAI__ContentUnderstandingEndpoint'
              value: effectiveContentUnderstandingEndpoint
            }
            {
              name: 'AzureAI__DecisionEndpoint'
              value: effectiveDecisionEndpoint
            }
            {
              name: 'AzureAI__DecisionDeploymentName'
              value: decisionDeploymentName
            }
            {
              name: 'AzureAI__CosmosEndpoint'
              value: cosmosAccount.properties.documentEndpoint
            }
            {
              name: 'AzureAI__BlobEndpoint'
              value: storage.properties.primaryEndpoints.blob
            }
            {
              name: 'AzureAI__QueueEndpoint'
              value: storage.properties.primaryEndpoints.queue
            }
            {
              name: 'Identity__Mode'
              value: 'Entra'
            }
            {
              name: 'Identity__DirectoryTenantId'
              value: directoryTenantId
            }
            {
              name: 'Identity__ClientId'
              value: webClientId
            }
            {
              name: 'Identity__ApiClientId'
              value: apiClientId
            }
            {
              name: 'Identity__UseManagedIdentityAssertion'
              value: 'true'
            }
            {
              name: 'Identity__ManagedIdentityClientId'
              value: uami.properties.clientId
            }
            {
              name: 'Identity__PublicOrigin'
              value: effectivePublicOrigin
            }
            {
              name: 'Identity__DataProtectionBlobUri'
              value: '${storage.properties.primaryEndpoints.blob}identity-keys/keyring.xml'
            }
            {
              name: 'Identity__DataProtectionKeyIdentifier'
              value: '${keyVault.properties.vaultUri}keys/${identityEncryptionKey.name}'
            }
            {
              name: 'DOCINTEL_IDENTITY_GRANTS'
              value: string(identityGrants)
            }
            {
              name: 'HarnessBootstrap__TenantId'
              value: bootstrapTenantId
            }
            {
              name: 'HarnessBootstrap__DisplayName'
              value: bootstrapTenantDisplayName
            }
            {
              name: 'AskAi__RecoveryPollSeconds'
              value: '30'
            }
            {
              name: 'DocumentJobs__RecoveryPollSeconds'
              value: '30'
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/alive'
                port: 8080
              }
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              periodSeconds: 10
              failureThreshold: 3
              timeoutSeconds: 5
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
        rules: [
          {
            name: 'ask-ai-backlog'
            custom: {
              type: 'azure-queue'
              identity: uami.id
              metadata: {
                accountName: storage.name
                queueName: dispatchQueue.name
                queueLength: '5'
              }
            }
          }
          {
            name: 'document-backlog'
            custom: {
              type: 'azure-queue'
              identity: uami.id
              metadata: {
                accountName: storage.name
                queueName: documentQueue.name
                queueLength: '5'
              }
            }
          }
        ]
      }
    }
  }
  dependsOn: [
    newUami
    newCae
    acrPullRole
    foundryCogUserRole
    foundryOpenAiUserRole
    foundryAiUserRole
    foundryModelDeployments
    newDispatchQueue
    newDocumentQueue
    runContainer
    identityKeys
    poisonQueue
    documentPoisonQueue
    storageRoleAssignment
    storageQueueRole
    identityKeyRole
    cosmosSqlRoleAssignment
    harnessesContainer
    jobsContainer
    ocrUsageContainer
    reviewsContainer
  ]
}

output containerAppFqdn string = effectivePublicOrigin
output containerRegistryName string = enableContainerRegistry ? acr.name : ''
output containerRegistryLoginServer string = enableContainerRegistry ? acr.properties.loginServer : ''
output managedIdentityClientId string = uami.properties.clientId
output managedIdentityPrincipalId string = uami.properties.principalId
output managedIdentityResourceId string = uami.id
output storageAccountName string = storage.name
output storageBlobEndpoint string = storage.properties.primaryEndpoints.blob
output storageQueueEndpoint string = storage.properties.primaryEndpoints.queue
output cosmosAccountName string = cosmosAccount.name
output cosmosEndpoint string = cosmosAccount.properties.documentEndpoint
output foundryEndpoint string = effectiveFoundryEndpoint
output foundryResourceId string = hasFoundryAccount ? foundry.id : ''
output azureOpenAiEndpoint string = effectiveOpenAiEndpoint
output contentUnderstandingEndpoint string = effectiveContentUnderstandingEndpoint
output decisionEndpoint string = effectiveDecisionEndpoint
output decisionDeploymentName string = decisionDeploymentName
output translatorEndpoint string = effectiveTranslatorEndpoint
output containerEnvironmentId string = effectiveEnvironmentId
output identityKeyIdentifier string = '${keyVault.properties.vaultUri}keys/${identityEncryptionKey.name}'
output requiredWebRedirectUri string = '${effectivePublicOrigin}/signin-oidc'
output requiredFederationSubject string = uami.properties.principalId
