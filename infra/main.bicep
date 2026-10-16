targetScope = 'resourceGroup'

@description('Location for all resources.')
param location string = resourceGroup().location

@description('Environment name suffix (e.g. dev, uat, prod).')
param environmentName string = 'dev'

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

var uniqueSuffix = uniqueString(resourceGroup().id, environmentName)
var uamiName = 'uami-docintel-${environmentName}'
var logAnalyticsName = 'law-docintel-${environmentName}-${uniqueSuffix}'
var caeName = 'cae-docintel-${environmentName}'
var appName = 'ca-docintel-${environmentName}'
var storageName = take('sadocintel${environmentName}${uniqueSuffix}', 24)
var foundryName = 'aif-docintel-${environmentName}-${uniqueSuffix}'
var createFoundryResource = empty(foundryInferenceEndpoint)

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
  properties: {
    customSubDomainName: foundryName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
}

// Grant UAMI "Cognitive Services User" and "Cognitive Services OpenAI User" on the provisioned Foundry resource
var cognitiveServicesUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
var cognitiveServicesOpenAiUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')

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

// Grant UAMI "AcrPull" when an ACR in the same resource group is provided
var acrPullRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
resource existingAcr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = if (!empty(acrName)) {
  name: !empty(acrName) ? acrName : 'placeholder'
}

resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(acrName)) {
  name: guid(resourceGroup().id, uami.id, acrPullRoleId, acrName)
  scope: existingAcr
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
resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uami.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: cae.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: !empty(acrName) ? [
        {
          server: '${acrName}.azurecr.io'
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
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

output containerAppFqdn string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
output managedIdentityClientId string = uami.properties.clientId
output managedIdentityPrincipalId string = uami.properties.principalId
output storageAccountName string = storage.name
output foundryEndpoint string = effectiveFoundryEndpoint
