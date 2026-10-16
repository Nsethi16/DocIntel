targetScope = 'resourceGroup'

@description('Location for all resources.')
param location string = resourceGroup().location

@description('Environment name suffix (e.g. dev, uat, prod).')
param environmentName string = 'dev'

@description('Container image to deploy (e.g. <acr>.azurecr.io/docintel-api:latest).')
param containerImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

@description('Optional Azure Container Registry name for Managed Identity AcrPull.')
param acrName string = ''

@description('Microsoft Foundry Inference endpoint (e.g. https://<foundry>.services.ai.azure.com/models).')
param foundryInferenceEndpoint string = ''

@description('Azure OpenAI endpoint (e.g. https://<foundry>.openai.azure.com/).')
param azureOpenAiEndpoint string = ''

@description('Azure AI Translator endpoint (e.g. https://<translator>.cognitiveservices.azure.com/).')
param translatorEndpoint string = ''

var uniqueSuffix = uniqueString(resourceGroup().id, environmentName)
var uamiName = 'uami-docintel-${environmentName}'
var logAnalyticsName = 'law-docintel-${environmentName}-${uniqueSuffix}'
var caeName = 'cae-docintel-${environmentName}'
var appName = 'ca-docintel-${environmentName}'
var storageName = take('sadocintel${environmentName}${uniqueSuffix}', 24)

// 1. User-Assigned Managed Identity (Passwordless Auth for Foundry, Translator, Blob, and ACR)
resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: uamiName
  location: location
}

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
              value: foundryInferenceEndpoint
            }
            {
              name: 'AzureAI__AzureOpenAIEndpoint'
              value: azureOpenAiEndpoint
            }
            {
              name: 'AzureAI__TranslatorEndpoint'
              value: translatorEndpoint
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
