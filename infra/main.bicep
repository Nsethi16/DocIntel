targetScope = 'resourceGroup'

@description('Location for all resources.')
param location string = resourceGroup().location

@description('Environment name suffix (e.g. dev, uat, prod).')
param environmentName string = 'dev'

@description('Provision shared infrastructure without replacing the app while preparing its image/identity.')
param deployApplication bool = true

@description('Entra directory ID for verified sign-in. Application memberships use separate tenant slugs.')
param directoryTenantId string

@description('Confidential web application registration ID with managed-identity federation configured.')
param webClientId string

@description('Separate API resource application registration ID.')
param apiClientId string

@description('Server-owned Memberships and PlatformAdministrators. No credentials belong in this object.')
param identityGrants object = {
  Memberships: []
  PlatformAdministrators: []
}

@description('Container image to deploy (e.g. <acr>.azurecr.io/docintel-api:latest).')
param containerImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

@description('Optional Azure Container Registry name for Managed Identity AcrPull.')
param acrName string = ''

@description('Optional existing Microsoft Foundry Inference endpoint. When empty, a new Foundry (AIServices) resource is provisioned automatically.')
param foundryInferenceEndpoint string = ''

@description('Optional existing Azure OpenAI endpoint. Defaults to the provisioned Foundry resource when empty.')
param azureOpenAiEndpoint string = ''

@description('Optional existing Azure AI Translator endpoint. Defaults to the provisioned Foundry resource when empty.')
param translatorEndpoint string = ''

@description('Optional existing Azure Document Intelligence endpoint. Empty provisions managed-identity prebuilt-read access.')
param documentIntelligenceEndpoint string = ''

@description('Foundry model deployments to provision automatically under the Foundry account.')
param modelDeployments array = [
  {
    name: 'gpt-5-mini'
    format: 'OpenAI'
    modelName: 'gpt-5-mini'
    modelVersion: '2025-08-07'
    skuName: 'GlobalStandard'
    capacity: 10
  }
]

var uniqueSuffix = uniqueString(resourceGroup().id, environmentName)
var uamiName = 'uami-docintel-${environmentName}'
var logAnalyticsName = 'law-docintel-${environmentName}-${uniqueSuffix}'
var caeName = 'cae-docintel-${environmentName}'
var appName = 'ca-docintel-${environmentName}'
var storageName = take('sadocintel${environmentName}${uniqueSuffix}', 24)
var cosmosAccountName = 'cosmos-docintel-${environmentName}-${uniqueSuffix}'
var cosmosDatabaseName = 'DocIntel'
var foundryName = 'aif-docintel-${environmentName}-${uniqueSuffix}'
var keyVaultName = 'kv-di-${take(environmentName, 3)}-${uniqueSuffix}'
var effectiveAcrName = !empty(acrName) ? acrName : take('acrdocintel${environmentName}${uniqueSuffix}', 50)
var createFoundryResource = empty(foundryInferenceEndpoint)
var documentIntelligenceName = 'di-docintel-${environmentName}-${uniqueSuffix}'

// 1. User-Assigned Managed Identity (Passwordless Auth for Foundry, Translator, Blob, and ACR)
resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: uamiName
  location: location
}

// 1b. Microsoft Foundry (Azure AI Services multi-service account: Models + OpenAI + Translator)
resource foundry 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (createFoundryResource) {
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
}

// 1c. Model Deployments inside the Microsoft Foundry resource (serialized with @batchSize(1))
@batchSize(1)
resource foundryModelDeployments 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = [for m in modelDeployments: if (createFoundryResource) {
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
}]

// Grant UAMI "Cognitive Services User", "Cognitive Services OpenAI User", and "Azure AI User (Foundry)" on the provisioned Foundry resource
var cognitiveServicesUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
var cognitiveServicesOpenAiUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
var foundryAiUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')

resource documentIntelligence 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (empty(documentIntelligenceEndpoint)) {
  name: documentIntelligenceName
  location: location
  kind: 'FormRecognizer'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: documentIntelligenceName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
}

resource documentIntelligenceUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (empty(documentIntelligenceEndpoint)) {
  name: guid(resourceGroup().id, uami.id, documentIntelligenceName, cognitiveServicesUserRoleId)
  scope: documentIntelligence
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: cognitiveServicesUserRoleId
  }
}

resource foundryCogUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (createFoundryResource) {
  name: guid(resourceGroup().id, uami.id, cognitiveServicesUserRoleId)
  scope: foundry
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: cognitiveServicesUserRoleId
  }
}

resource foundryOpenAiUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (createFoundryResource) {
  name: guid(resourceGroup().id, uami.id, cognitiveServicesOpenAiUserRoleId)
  scope: foundry
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: cognitiveServicesOpenAiUserRoleId
  }
}

resource foundryAiUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (createFoundryResource) {
  name: guid(resourceGroup().id, uami.id, foundryAiUserRoleId)
  scope: foundry
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: foundryAiUserRoleId
  }
}

// 1d. Azure Container Registry (Provisioned automatically + AcrPull RBAC for UAMI)
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: effectiveAcrName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
}

var acrPullRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, uami.id, acrPullRoleId)
  scope: acr
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRoleId
  }
}

var effectiveFoundryEndpoint = !empty(foundryInferenceEndpoint)
  ? foundryInferenceEndpoint
  : 'https://${foundryName}.services.ai.azure.com/models'
var effectiveOpenAiEndpoint = !empty(azureOpenAiEndpoint)
  ? azureOpenAiEndpoint
  : 'https://${foundryName}.openai.azure.com/'
var effectiveTranslatorEndpoint = !empty(translatorEndpoint)
  ? translatorEndpoint
  : 'https://${foundryName}.cognitiveservices.azure.com/'

// 2. Storage Account for Harness Profiles & Split PDF Artifacts
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
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

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource runContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'ask-ai-runs'
  properties: {
    publicAccess: 'None'
  }
}

resource identityKeys 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'identity-keys'
  properties: {
    publicAccess: 'None'
  }
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource dispatchQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: 'ask-ai'
}

resource poisonQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: 'ask-ai-poison'
}

resource documentQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: 'document-jobs'
}

resource documentPoisonQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: 'document-jobs-poison'
}

resource storageQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, uami.id, '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  scope: storage
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
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

resource identityEncryptionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
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
}

resource identityKeyRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(identityEncryptionKey.id, uami.id, 'e147488a-f6f5-4113-8e2d-b22465e65bf6')
  scope: identityEncryptionKey
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e147488a-f6f5-4113-8e2d-b22465e65bf6')
  }
}

// Storage Blob Data Contributor role for UAMI
var storageBlobDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
resource storageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, uami.id, storageBlobDataContributorRoleId)
  scope: storage
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRoleId
  }
}

// 2b. Serverless Azure Cosmos DB (Shared 'harnesses' and 'jobs' containers partitioned by /tenantId)
resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
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

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: cosmosDatabaseName
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
  }
}

resource harnessesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
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
}

resource jobsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
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
}

// Cosmos DB Built-in Data Contributor Role Assignment for UAMI
var cosmosDataContributorRoleId = '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
resource cosmosSqlRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, uami.id, '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: cosmosDataContributorRoleId
    principalId: uami.properties.principalId
    scope: cosmosAccount.id
  }
}

// 3. Log Analytics + Container Apps Environment
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource cae 'Microsoft.App/managedEnvironments@2024-03-01' = {
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
    environmentId: cae.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        allowInsecure: false
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: acr.properties.loginServer
          identity: uami.id
        }
      ]
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
              name: 'AzureAI__DocumentIntelligenceEndpoint'
              value: !empty(documentIntelligenceEndpoint)
                ? documentIntelligenceEndpoint
                : 'https://${documentIntelligenceName}.cognitiveservices.azure.com/'
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
              value: 'https://${appName}.${cae.properties.defaultDomain}'
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
    runContainer
    identityKeys
    poisonQueue
    documentPoisonQueue
    storageRoleAssignment
    storageQueueRole
    identityKeyRole
    cosmosSqlRoleAssignment
    documentIntelligenceUserRole
  ]
}

output containerAppFqdn string = 'https://${appName}.${cae.properties.defaultDomain}'
output containerRegistryName string = acr.name
output containerRegistryLoginServer string = acr.properties.loginServer
output managedIdentityClientId string = uami.properties.clientId
output managedIdentityPrincipalId string = uami.properties.principalId
output storageAccountName string = storage.name
output cosmosAccountName string = cosmosAccount.name
output foundryEndpoint string = effectiveFoundryEndpoint
output identityKeyIdentifier string = '${keyVault.properties.vaultUri}keys/${identityEncryptionKey.name}'
output requiredWebRedirectUri string = 'https://${appName}.${cae.properties.defaultDomain}/signin-oidc'
output requiredFederationSubject string = uami.properties.principalId
