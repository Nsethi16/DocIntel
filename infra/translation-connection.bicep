targetScope = 'resourceGroup'

@description('Dedicated regional single-service Translator, never an AIServices/model account.')
param accountName string
param location string
@description('Dedicated module-owned staging account; never the originals/artifact account.')
param stagingAccountName string
param applicationResourceId string
param runtimePrincipalId string
param runtimeIdentityResourceId string
param provisionAccount bool = true
param provisionStagingAccount bool = true
param provisionSourceContainer bool = true
param provisionTargetContainer bool = true
param assignRuntimeRole bool = true
param assignSourceRole bool = true
param assignTargetRole bool = true
param assignRuntimeSourceRole bool = true
param assignRuntimeTargetRole bool = true

resource translator 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: accountName
}
resource newTranslator 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (provisionAccount) {
  name: accountName
  location: location
  kind: 'TextTranslation'
  sku: {
    name: 'S1'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: accountName
    disableLocalAuth: true
  }
}
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: stagingAccountName
}
resource newStaging 'Microsoft.Storage/storageAccounts@2023-05-01' = if (provisionStagingAccount) {
  name: stagingAccountName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  tags: {
    'docintel-purpose': 'translation-staging-v1'
    'docintel-owner-app': toLower(applicationResourceId)
    'docintel-runtime': toLower(runtimeIdentityResourceId)
    'docintel-translator': toLower(translator.id)
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    accessTier: 'Hot'
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: storage
  name: 'default'
}
resource source 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  parent: blobs
  name: 'translation-source'
}
resource target 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  parent: blobs
  name: 'translation-target'
}
resource newSource 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (provisionSourceContainer) {
  parent: blobs
  name: 'translation-source'
  properties: {
    publicAccess: 'None'
  }
  dependsOn: [
    newStaging
  ]
}
resource newTarget 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (provisionTargetContainer) {
  parent: blobs
  name: 'translation-target'
  properties: {
    publicAccess: 'None'
  }
  dependsOn: [
    newStaging
  ]
}
resource stagingExpiry 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = if (provisionStagingAccount) {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'docintel-translation-staging-v1'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                'translation-source/docintel-translation-v1/'
                'translation-target/docintel-translation-v1/'
              ]
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterModificationGreaterThan: 1
                }
              }
              snapshot: {
                delete: {
                  daysAfterCreationGreaterThan: 1
                }
              }
              version: {
                delete: {
                  daysAfterCreationGreaterThan: 1
                }
              }
            }
          }
        }
      ]
    }
  }
  dependsOn: [
    newStaging
  ]
}
var cognitiveUserRole = 'a97b65f3-24c7-4388-baec-2e87135dc908'
var blobReaderRole = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
var blobContributorRole = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource runtimeRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignRuntimeRole) {
  name: guid(translator.id, runtimeIdentityResourceId, cognitiveUserRole)
  scope: translator
  properties: {
    principalId: runtimePrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveUserRole)
  }
  dependsOn: [
    newTranslator
  ]
}
resource sourceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignSourceRole) {
  name: guid(source.id, translator.id, blobReaderRole)
  scope: source
  properties: {
    principalId: translator.identity.principalId!
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobReaderRole)
  }
  dependsOn: [
    newTranslator
    newSource
  ]
}
resource targetRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignTargetRole) {
  name: guid(target.id, translator.id, blobContributorRole)
  scope: target
  properties: {
    principalId: translator.identity.principalId!
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributorRole)
  }
  dependsOn: [
    newTranslator
    newTarget
  ]
}
resource runtimeSourceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignRuntimeSourceRole) {
  name: guid(source.id, runtimeIdentityResourceId, blobContributorRole)
  scope: source
  properties: {
    principalId: runtimePrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributorRole)
  }
  dependsOn: [
    newSource
  ]
}
resource runtimeTargetRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignRuntimeTargetRole) {
  name: guid(target.id, runtimeIdentityResourceId, blobContributorRole)
  scope: target
  properties: {
    principalId: runtimePrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributorRole)
  }
  dependsOn: [
    newTarget
  ]
}
