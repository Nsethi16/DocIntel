targetScope = 'subscription'

@minLength(3)
@maxLength(16)
param customerSlug string

@minLength(1)
@maxLength(120)
param customerDisplayName string = customerSlug

@minLength(2)
@maxLength(8)
param environmentName string = 'prod'

param resourceGroupName string
param location string
param resourceGroupTags object = {}
param installationRequestHash string = ''
param directoryTenantId string
param webClientId string
param apiClientId string

@description('Explicitly selected directory user who administers all workspaces in this installation.')
param initialOwnerObjectId string

@description('Public container image pinned by SHA-256 digest; the installer validates its registry and availability.')
param containerImage string

@description('Explicitly build the pinned public source in the customer registry instead of requiring a public binary.')
param buildImageInCustomerRegistry bool = false

param sourceCommit string = ''

@description('Provision dependencies first; sign-in federation is configured before the app is created.')
param deployApplication bool = true

@description('Only the second phase of the same fresh installation may reuse its completed infrastructure.')
param infrastructureReady bool = false

var suffix = uniqueString(subscription().subscriptionId, resourceGroupName, customerSlug, environmentName)
var stem = take(replace(customerSlug, '-', ''), 8)
var environmentKey = take(suffix, 8)
var appName = 'di-${stem}-${environmentName}-${take(suffix, 6)}'
var environmentResourceName = 'cae-docintel-${environmentKey}'
var registryName = 'acrdi${suffix}'
var effectiveImage = !empty(containerImage) ? containerImage : (buildImageInCustomerRegistry
  ? '${registryName}.azurecr.io/docintel-api:source-${take(sourceCommit, 12)}-${take(installationRequestHash, 12)}'
  : fail('A pinned image or explicit customer-registry build is required.'))

resource customerGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: union(resourceGroupTags, {
    'docintel-customer': customerSlug
    'docintel-environment': environmentName
    'docintel-request': installationRequestHash
  })
}

module installation './main.bicep' = {
  name: deployApplication ? 'docintel-application' : 'docintel-infrastructure'
  scope: customerGroup
  params: {
    location: location
    environmentName: environmentKey
    containerAppName: appName
    runtimeIdentityName: 'id-${appName}'
    storageAccountName: 'sadi${suffix}'
    cosmosAccountNameOverride: 'cosmos-di-${stem}-${environmentName}-${suffix}'
    foundryAccountNameOverride: 'ai-di-${stem}-${environmentName}-${suffix}'
    keyVaultNameOverride: 'kv-di-${take(stem, 4)}-${suffix}'
    enableContainerRegistry: buildImageInCustomerRegistry
    acrName: buildImageInCustomerRegistry ? registryName : ''
    reuseRegistry: infrastructureReady
    containerImage: effectiveImage
    directoryTenantId: directoryTenantId
    bootstrapTenantId: customerSlug
    bootstrapTenantDisplayName: customerDisplayName
    webClientId: webClientId
    apiClientId: apiClientId
    identityGrants: {
      PlatformAdministrators: [
        {
          DirectoryTenantId: directoryTenantId
          ObjectId: initialOwnerObjectId
        }
      ]
    }
    deployApplication: deployApplication
    deployRuntimeRoles: !infrastructureReady
    reuseRuntimeIdentity: infrastructureReady
    existingEnvironmentId: infrastructureReady
      ? resourceId(resourceGroupName, 'Microsoft.App/managedEnvironments', environmentResourceName)
      : ''
    reuseStorageAccount: infrastructureReady
    reuseCosmosAccount: infrastructureReady
    reuseCosmosDatabase: infrastructureReady
    reuseFoundryAccount: infrastructureReady
    reuseKeyVault: infrastructureReady
    reuseIdentityKey: infrastructureReady
    deployModels: !infrastructureReady
    existingBlobContainers: infrastructureReady ? ['ask-ai-runs', 'identity-keys'] : []
    existingQueues: infrastructureReady
      ? ['ask-ai', 'ask-ai-poison', 'document-jobs', 'document-jobs-poison']
      : []
    existingCosmosContainers: infrastructureReady ? ['harnesses', 'jobs', 'ocr-usage', 'review-sessions'] : []
  }
}

output containerAppName string = appName
output publicOrigin string = installation.outputs.containerAppFqdn
output requiredWebRedirectUri string = installation.outputs.requiredWebRedirectUri
output managedIdentityPrincipalId string = installation.outputs.managedIdentityPrincipalId
output managedIdentityClientId string = installation.outputs.managedIdentityClientId
output managedIdentityResourceId string = installation.outputs.managedIdentityResourceId
output workspaceUrl string = '${installation.outputs.containerAppFqdn}/?tenant=${uriComponent(customerSlug)}'
output containerRegistryName string = installation.outputs.containerRegistryName
output containerRegistryLoginServer string = installation.outputs.containerRegistryLoginServer
output containerRegistryResourceId string = buildImageInCustomerRegistry
  ? resourceId(resourceGroupName, 'Microsoft.ContainerRegistry/registries', registryName)
  : ''
