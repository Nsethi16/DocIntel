targetScope = 'resourceGroup'

@description('Existing, validated AIServices account. This template never changes the account itself.')
param accountName string

param deploymentName string = 'Decision-1'
param runtimePrincipalId string
param runtimeIdentityResourceId string
param provisionModel bool = false
param assignRuntimeRole bool = false

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: accountName
}

resource decision 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = if (provisionModel) {
  parent: account
  name: deploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: 1
  }
  properties: {
    model: {
      format: 'Microsoft'
      name: 'Microsoft-Decision-1'
      version: '1'
    }
  }
}

var roleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
resource runtimeRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignRuntimeRole) {
  name: guid(account.id, runtimeIdentityResourceId, roleId)
  scope: account
  properties: {
    principalId: runtimePrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
  }
}
