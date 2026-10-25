#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'Deploy-DocIntelAzure.ps1'
$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath, [ref]$tokens, [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -ne 0) {
    throw ($parseErrors | ForEach-Object Message | Out-String)
}

$script:AzureCalls = 0
function az {
    $script:AzureCalls++
    throw 'Azure must not be invoked by offline preflight tests.'
}

function Assert-PreflightFailure {
    param([hashtable]$Arguments, [string]$Expected)
    $failed = $false
    try {
        & $scriptPath @Arguments
    }
    catch {
        $failed = $true
        if ($_.Exception.Message -notlike "*$Expected*") { throw }
    }
    if (-not $failed) { throw 'Invalid deployment inputs were not rejected.' }
    if ($script:AzureCalls -ne 0) { throw 'A preflight failure invoked Azure.' }
}

$ids = @{
    SubscriptionId = [guid]::NewGuid().ToString()
    DirectoryTenantId = [guid]::NewGuid().ToString()
    WebClientId = [guid]::NewGuid().ToString()
    ApiClientId = [guid]::NewGuid().ToString()
}
$invalid = $ids.Clone()
$invalid.SubscriptionId = 'invalid'
Assert-PreflightFailure $invalid 'SubscriptionId'
Assert-PreflightFailure $ids 'IdentityGrantsPath'
$same = $ids.Clone()
$same.ApiClientId = $same.WebClientId
Assert-PreflightFailure $same 'separate'

$temporary = Join-Path $PSScriptRoot ('.docintel-grants-test-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    '{}' | Set-Content -LiteralPath $temporary
    (Get-Item -LiteralPath $temporary -Force).IsReadOnly = $true
    $empty = $ids.Clone()
    $empty.IdentityGrantsPath = $temporary
    Assert-PreflightFailure $empty 'nonempty'
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
$templatePath = Join-Path (Split-Path $PSScriptRoot -Parent) 'infra\azuredeploy.json'
$template = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json -Depth 100
$applications = @($template.resources | Where-Object { $_.type -eq 'Microsoft.App/containerApps' })
if ($applications.Count -ne 1) { throw 'Expected exactly one conditional Container App resource.' }
$application = $applications[0]
if ($application.properties.template.PSObject.Properties['dependsOn']) {
    throw 'Container App dependencies must not be emitted as ARM resource lookups inside its API properties.'
}
if (-not @($application.properties.template.containers[0].env | Where-Object {
    $_.name -eq 'AzureAI__ContentUnderstandingEndpoint'
})) {
    throw 'The application must use the configured Content Understanding resource root endpoint.'
}
if ($application.apiVersion -ne '2025-07-01') {
    throw 'Container Apps must use the verified API schema supporting managed-identity scale rules.'
}
$scalers = @($application.properties.template.scale.rules | Where-Object { $_.custom.type -eq 'azure-queue' })
if ($scalers.Count -ne 2 -or @($scalers | Where-Object { -not $_.custom.identity }).Count -ne 0) {
    throw 'Both document and Ask AI queues require managed-identity-backed scale rules.'
}
$keys = @($template.resources | Where-Object { $_.type -eq 'Microsoft.KeyVault/vaults/keys' })
if ($keys.Count -ne 1 -or $keys[0].properties.rotationPolicy.lifetimeActions[0].action.type -cne 'rotate') {
    throw 'The identity key must retain its schema-valid automatic rotation action.'
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Context)
    if ($Actual -cne $Expected) {
        throw "${Context}: expected '$Expected', received '$Actual'."
    }
}

function Get-ArmResource {
    param([string]$Type)
    $resources = @($template.resources | Where-Object type -CEQ $Type)
    Assert-Equal $resources.Count 1 "Writable resource count for $Type"
    return $resources[0]
}

function Assert-Dependency {
    param($Resource, [string]$Dependency)
    if ($Dependency -cnotin $Resource.dependsOn) {
        throw "$($Resource.type) must wait for $Dependency in fresh and reused modes."
    }
}

$emptyStringParameters = @(
    'runtimeIdentityName', 'existingEnvironmentId', 'publicOrigin', 'containerAppName',
    'storageAccountName', 'cosmosAccountNameOverride', 'keyVaultNameOverride', 'foundryAccountNameOverride'
)
$reuseParameters = @(
    'reuseRuntimeIdentity', 'reuseRegistry', 'reuseStorageAccount', 'reuseCosmosAccount',
    'reuseKeyVault', 'reuseIdentityKey', 'reuseFoundryAccount', 'reuseCosmosDatabase'
)
foreach ($name in $emptyStringParameters) {
    Assert-Equal $template.parameters.$name.type 'string' "$name type"
    Assert-Equal $template.parameters.$name.defaultValue '' "$name default"
}
foreach ($name in $reuseParameters) {
    Assert-Equal $template.parameters.$name.type 'bool' "$name type"
    Assert-Equal $template.parameters.$name.defaultValue $false "$name default"
}
foreach ($name in @('existingCosmosContainers', 'existingBlobContainers', 'existingQueues')) {
    Assert-Equal $template.parameters.$name.type 'array' "$name type"
    Assert-Equal @($template.parameters.$name.defaultValue).Count 0 "$name empty default for fresh provisioning"
}
foreach ($name in @('deployApplication', 'deployModels')) {
    Assert-Equal $template.parameters.$name.type 'bool' "$name type"
    Assert-Equal $template.parameters.$name.defaultValue $true "$name legacy default"
}
Assert-Equal $template.parameters.acrName.defaultValue '' 'Legacy registry name default'
Assert-Equal $template.parameters.registryPullRoleDefinitionId.type 'string' 'Registry pull role type'
Assert-Equal $template.parameters.registryPullRoleDefinitionId.defaultValue `
    '7f951dda-4ed3-4680-a7ca-43fe172d538d' 'Registry pull role least-privilege default'
Assert-Equal $template.parameters.identityGrants.type 'secureObject' 'Bootstrap grants parameter protection'
if (($template.outputs | ConvertTo-Json -Depth 100) -match
    'identityGrants|Memberships|PlatformAdministrators|DOCINTEL_IDENTITY_GRANTS') {
    throw 'Deployment outputs must not expose bootstrap grants.'
}

$conditionalResources = @{
    'Microsoft.ManagedIdentity/userAssignedIdentities' = 'reuseRuntimeIdentity'
    'Microsoft.ContainerRegistry/registries' = 'reuseRegistry'
    'Microsoft.Storage/storageAccounts' = 'reuseStorageAccount'
    'Microsoft.Storage/storageAccounts/blobServices' = 'reuseStorageAccount'
    'Microsoft.Storage/storageAccounts/queueServices' = 'reuseStorageAccount'
    'Microsoft.DocumentDB/databaseAccounts' = 'reuseCosmosAccount'
    'Microsoft.DocumentDB/databaseAccounts/sqlDatabases' = 'reuseCosmosDatabase'
    'Microsoft.KeyVault/vaults' = 'reuseKeyVault'
    'Microsoft.KeyVault/vaults/keys' = 'reuseIdentityKey'
}
foreach ($entry in $conditionalResources.GetEnumerator()) {
    $resource = Get-ArmResource $entry.Key
    Assert-Equal $resource.condition "[not(parameters('$($entry.Value)'))]" `
        "$($entry.Key) must never be PUT when its reuse flag is true"
}
$registry = Get-ArmResource 'Microsoft.ContainerRegistry/registries'
Assert-Equal $registry.properties.adminUserEnabled $false 'New registry admin authentication'
Assert-Equal $template.variables.acrPullRoleId `
    "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', parameters('registryPullRoleDefinitionId'))]" `
    'Registry pull role must not silently use a broader role'

$nameOverrides = @{
    uamiName = 'runtimeIdentityName'; appName = 'containerAppName'; storageName = 'storageAccountName'
    cosmosAccountName = 'cosmosAccountNameOverride'; keyVaultName = 'keyVaultNameOverride'
    foundryName = 'foundryAccountNameOverride'; effectiveAcrName = 'acrName'
}
foreach ($entry in $nameOverrides.GetEnumerator()) {
    $prefix = "[if(not(empty(parameters('$($entry.Value)'))), parameters('$($entry.Value)'), "
    if (-not $template.variables.($entry.Key).StartsWith($prefix, [StringComparison]::Ordinal)) {
        throw "$($entry.Key) must use the explicit name before generating a default."
    }
}
if (-not $template.variables.uamiName.Contains("format('uami-docintel-{0}', parameters('environmentName'))") -or
    -not $template.variables.appName.Contains("format('ca-docintel-{0}', parameters('environmentName'))")) {
    throw 'The default runtime identity and app names must remain backward-compatible.'
}

$identityId = "[resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', variables('uamiName'))]"
$storageId = "[resourceId('Microsoft.Storage/storageAccounts', variables('storageName'))]"
$cosmosId = "[resourceId('Microsoft.DocumentDB/databaseAccounts', variables('cosmosAccountName'))]"
$vaultId = "[resourceId('Microsoft.KeyVault/vaults', variables('keyVaultName'))]"
$keyId = "[resourceId('Microsoft.KeyVault/vaults/keys', variables('keyVaultName'), 'docintel-identity')]"
$registryId = "[resourceId('Microsoft.ContainerRegistry/registries', variables('effectiveAcrName'))]"
$foundryId = "[resourceId('Microsoft.CognitiveServices/accounts', variables('foundryName'))]"
$environmentId = "[resourceId('Microsoft.App/managedEnvironments', variables('caeName'))]"

$foundry = Get-ArmResource 'Microsoft.CognitiveServices/accounts'
Assert-Equal $template.variables.hasFoundryAccount `
    "[or(or(parameters('reuseFoundryAccount'), not(empty(parameters('foundryAccountNameOverride')))), empty(parameters('foundryInferenceEndpoint')))]" `
    'Foundry account selection must support new, reused, and external-endpoint-only modes'
Assert-Equal $template.variables.createFoundryResource `
    "[and(variables('hasFoundryAccount'), not(parameters('reuseFoundryAccount')))]" `
    'Existing Foundry root configuration must not be reapplied'
Assert-Equal $foundry.condition "[variables('createFoundryResource')]" 'Foundry root creation condition'
Assert-Dependency $foundry $identityId
$models = Get-ArmResource 'Microsoft.CognitiveServices/accounts/deployments'
Assert-Equal $models.condition "[and(variables('hasFoundryAccount'), parameters('deployModels'))]" `
    'Model deployments must be independently disabled for adopted Foundry accounts'
Assert-Dependency $models $foundryId
Assert-Equal $models.copy.mode 'serial' 'Foundry deployments must remain serialized'

$workspace = Get-ArmResource 'Microsoft.OperationalInsights/workspaces'
$environment = Get-ArmResource 'Microsoft.App/managedEnvironments'
Assert-Equal $template.variables.createEnvironment "[empty(parameters('existingEnvironmentId'))]" `
    'Existing environment must skip workspace and environment creation'
Assert-Equal $workspace.condition "[variables('createEnvironment')]" 'Workspace creation condition'
Assert-Equal $environment.condition "[variables('createEnvironment')]" 'Environment creation condition'
Assert-Equal $template.variables.caeName `
    "[if(not(empty(parameters('existingEnvironmentId'))), last(split(parameters('existingEnvironmentId'), '/')), format('cae-docintel-{0}', parameters('environmentName')))]" `
    'The selected environment reference must retain the existing environment name'
Assert-Equal $template.variables.caeSubscriptionId `
    "[if(not(empty(parameters('existingEnvironmentId'))), split(parameters('existingEnvironmentId'), '/')[2], subscription().subscriptionId)]" `
    'The selected environment lookup must use the subscription from its resource ID'
Assert-Equal $template.variables.caeResourceGroupName `
    "[if(not(empty(parameters('existingEnvironmentId'))), split(parameters('existingEnvironmentId'), '/')[4], resourceGroup().name)]" `
    'The selected environment lookup must support another resource group'
$scopedEnvironmentId = "extensionResourceId(format('/subscriptions/{0}/resourceGroups/{1}', variables('caeSubscriptionId'), variables('caeResourceGroupName')), 'Microsoft.App/managedEnvironments', variables('caeName'))"
Assert-Equal $template.variables.effectiveEnvironmentId `
    "[if(not(empty(parameters('existingEnvironmentId'))), parameters('existingEnvironmentId'), $scopedEnvironmentId)]" `
    'The app must retain the exact supplied environment ID'
Assert-Equal $application.properties.environmentId "[variables('effectiveEnvironmentId')]" 'App environment selection'
Assert-Dependency $environment "[resourceId('Microsoft.OperationalInsights/workspaces', variables('logAnalyticsName'))]"
Assert-Dependency $application $environmentId
Assert-Equal $application.condition "[parameters('deployApplication')]" 'Supporting-infrastructure-only mode'

foreach ($type in @('Microsoft.Storage/storageAccounts/blobServices', 'Microsoft.Storage/storageAccounts/queueServices')) {
    $resource = Get-ArmResource $type
    Assert-Dependency $resource $storageId
    Assert-Equal $resource.condition "[not(parameters('reuseStorageAccount'))]" `
        'Adopted storage must preserve Blob/Queue service retention, CORS, and other settings'
}
$database = Get-ArmResource 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases'
Assert-Dependency $database $cosmosId
Assert-Dependency $keys[0] $vaultId
Assert-Equal $database.condition "[not(parameters('reuseCosmosDatabase'))]" `
    'The existing DocIntel database must not receive any settings PUT'
$childGroups = @(
    @{
        Type = 'Microsoft.Storage/storageAccounts/blobServices/containers'
        Parameter = 'existingBlobContainers'; Names = @('ask-ai-runs', 'identity-keys')
        Parent = "[resourceId('Microsoft.Storage/storageAccounts/blobServices', variables('storageName'), 'default')]"
    }
    @{
        Type = 'Microsoft.Storage/storageAccounts/queueServices/queues'
        Parameter = 'existingQueues'; Names = @('ask-ai', 'ask-ai-poison', 'document-jobs', 'document-jobs-poison')
        Parent = "[resourceId('Microsoft.Storage/storageAccounts/queueServices', variables('storageName'), 'default')]"
    }
    @{
        Type = 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers'
        Parameter = 'existingCosmosContainers'; Names = @('harnesses', 'jobs', 'ocr-usage', 'review-sessions')
        Parent = "[resourceId('Microsoft.DocumentDB/databaseAccounts/sqlDatabases', variables('cosmosAccountName'), variables('cosmosDatabaseName'))]"
    }
)
foreach ($group in $childGroups) {
    $children = @($template.resources | Where-Object type -CEQ $group.Type)
    Assert-Equal $children.Count $group.Names.Count "$($group.Type) required child count"
    foreach ($name in $group.Names) {
        $condition = "[not(contains(parameters('$($group.Parameter)'), '$name'))]"
        $child = @($children | Where-Object condition -CEQ $condition)
        Assert-Equal $child.Count 1 "Only missing child '$name' may receive a configuration PUT"
        if (-not $child[0].name.Contains("'$name'", [StringComparison]::Ordinal)) {
            throw "The $($group.Parameter) skip condition must match the child resource name '$name'."
        }
        Assert-Dependency $child[0] $group.Parent
    }
}
$containers = @($template.resources | Where-Object type -CEQ 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers')
Assert-Equal $containers.Count 4 'Shared DocIntel Cosmos container count'
foreach ($container in $containers) {
    Assert-Equal ($container.properties.resource.partitionKey.paths -join ',') '/tenantId' `
        "$($container.name) tenant isolation"
    Assert-Equal $container.properties.resource.partitionKey.kind 'Hash' "$($container.name) partition kind"
}
$blobContainers = @($template.resources | Where-Object type -CEQ 'Microsoft.Storage/storageAccounts/blobServices/containers')
foreach ($container in $blobContainers) {
    Assert-Equal $container.properties.publicAccess 'None' 'Missing blob containers must be private'
}

$roles = @($template.resources | Where-Object type -CEQ 'Microsoft.Authorization/roleAssignments')
Assert-Equal $roles.Count 7 'Runtime role assignment count'
$expectedScopes = @($foundryId, $registryId, $storageId, $keyId)
foreach ($role in $roles) {
    Assert-Equal $role.properties.principalId `
        "[reference(resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', variables('uamiName')), '2023-01-31').principalId]" `
        'Runtime role assignment must target the selected managed identity'
    if ($role.scope -cnotin $expectedScopes) { throw "Unexpected runtime RBAC scope: $($role.scope)" }
    Assert-Equal $role.properties.principalType 'ServicePrincipal' 'Managed identity role principal type'
    Assert-Dependency $role $identityId
    Assert-Dependency $role $role.scope
    if ($role.scope -ceq $foundryId) {
        Assert-Equal $role.condition "[variables('hasFoundryAccount')]" 'Foundry RBAC must include adopted accounts'
    }
    elseif ($role.condition) {
        throw 'Storage, registry, and key runtime roles must not be skipped when their roots are reused.'
    }
}
Assert-Equal @($roles | Where-Object scope -CEQ $foundryId).Count 3 'Foundry runtime role count'
Assert-Equal @($roles | Where-Object scope -CEQ $storageId).Count 2 'Storage runtime role count'
$keyRole = @($roles | Where-Object scope -CEQ $keyId)
Assert-Equal $keyRole.Count 1 'Identity key runtime role count'
Assert-Dependency $keyRole[0] $vaultId
Assert-Dependency $keyRole[0] $keyId
$cosmosRole = Get-ArmResource 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments'
Assert-Equal $cosmosRole.properties.scope $cosmosId 'Cosmos runtime data-plane RBAC scope'
Assert-Equal $cosmosRole.properties.principalId $roles[0].properties.principalId 'Cosmos runtime role principal'
Assert-Dependency $cosmosRole $cosmosId
Assert-Dependency $cosmosRole $identityId
if ($cosmosRole.condition) { throw 'Cosmos runtime RBAC must include adopted accounts.' }
foreach ($dependencyToken in @(
    "variables('acrPullRoleId')", "variables('cognitiveServicesUserRoleId')",
    "variables('cognitiveServicesOpenAiUserRoleId')", "variables('foundryAiUserRoleId')",
    "variables('storageBlobDataContributorRoleId')", '974c5e8b-45b9-4653-ba55-5f855dd0fb88',
    'e147488a-f6f5-4113-8e2d-b22465e65bf6', 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments',
    "'harnesses'", "'jobs'", "'ocr-usage'", "'review-sessions'",
    "'ask-ai'", "'ask-ai-poison'", "'document-jobs'", "'document-jobs-poison'",
    "'ask-ai-runs'", "'identity-keys'"
)) {
    if (-not ($application.dependsOn | Where-Object { $_.Contains($dependencyToken) })) {
        throw "The app must wait for runtime readiness: $dependencyToken."
    }
}
Assert-Dependency $application 'foundryModelDeployments'

$requiredOutputs = @(
    'containerAppFqdn', 'containerRegistryName', 'containerRegistryLoginServer',
    'managedIdentityResourceId', 'managedIdentityClientId', 'managedIdentityPrincipalId',
    'storageAccountName', 'storageBlobEndpoint', 'storageQueueEndpoint', 'cosmosAccountName', 'cosmosEndpoint',
    'foundryEndpoint', 'foundryResourceId', 'azureOpenAiEndpoint', 'contentUnderstandingEndpoint',
    'translatorEndpoint', 'containerEnvironmentId', 'identityKeyIdentifier',
    'requiredWebRedirectUri', 'requiredFederationSubject'
)
foreach ($name in $requiredOutputs) {
    Assert-Equal $template.outputs.$name.type 'string' "Runner output $name"
}
Assert-Equal $template.outputs.managedIdentityResourceId.value $identityId 'Managed identity resource ID output'
Assert-Equal $template.outputs.requiredFederationSubject.value $template.outputs.managedIdentityPrincipalId.value `
    'Federation must target the runtime managed identity'
Assert-Equal $template.outputs.containerEnvironmentId.value "[variables('effectiveEnvironmentId')]" `
    'Runner selected environment output'
Assert-Equal $template.outputs.foundryResourceId.value `
    "[if(variables('hasFoundryAccount'), resourceId('Microsoft.CognitiveServices/accounts', variables('foundryName')), '')]" `
    'External inference endpoints without an account must not invent a Foundry resource ID'
Assert-Equal $template.outputs.containerAppFqdn.value `
    "[if(not(empty(parameters('publicOrigin'))), parameters('publicOrigin'), format('https://{0}.{1}', variables('appName'), reference($scopedEnvironmentId, '2024-03-01').defaultDomain))]" `
    'HTTPS origin must be retained even with deployApplication=false'
Assert-Equal $template.outputs.requiredWebRedirectUri.value `
    ("[format('{0}/signin-oidc', " + $template.outputs.containerAppFqdn.value.Trim('[', ']') + ')]') `
    'Web redirect must use the same selected public origin'

$runtimeEnvironment = @{}
foreach ($entry in $application.properties.template.containers[0].env) {
    $runtimeEnvironment[$entry.name] = $entry.value
}
$runtimeOutputs = @{
    'Identity__PublicOrigin' = 'containerAppFqdn'
    'Identity__DataProtectionKeyIdentifier' = 'identityKeyIdentifier'
    'AzureAI__CosmosEndpoint' = 'cosmosEndpoint'; 'AzureAI__BlobEndpoint' = 'storageBlobEndpoint'
    'AzureAI__QueueEndpoint' = 'storageQueueEndpoint'; 'AzureAI__FoundryInferenceEndpoint' = 'foundryEndpoint'
    'AzureAI__AzureOpenAIEndpoint' = 'azureOpenAiEndpoint'; 'AzureAI__TranslatorEndpoint' = 'translatorEndpoint'
    'AzureAI__ContentUnderstandingEndpoint' = 'contentUnderstandingEndpoint'
}
foreach ($entry in $runtimeOutputs.GetEnumerator()) {
    Assert-Equal $runtimeEnvironment[$entry.Key] $template.outputs.($entry.Value).value `
        "Legacy app and setup runner must use the same $($entry.Key)"
}
foreach ($name in @('azureOpenAiEndpoint', 'translatorEndpoint', 'contentUnderstandingEndpoint')) {
    if (-not $template.outputs.$name.value.StartsWith(
        "[if(not(empty(parameters('$name'))), parameters('$name'), ", [StringComparison]::Ordinal)) {
        throw "The explicit $name must take precedence over generated endpoints."
    }
}
if ($template.outputs.contentUnderstandingEndpoint.value -notmatch 'customSubDomainName' -or
    $template.outputs.contentUnderstandingEndpoint.value -notmatch '\.endpoint') {
    throw 'Adopted Foundry endpoint defaults must resolve the actual custom subdomain or account endpoint.'
}
if ($template.outputs.identityKeyIdentifier.value -notmatch "'docintel-identity'" -or
    $template.outputs.identityKeyIdentifier.value -match 'keyUriWithVersion') {
    throw 'The identity key identifier must retain the fixed existing key name and be versionless.'
}
Assert-Equal $script:AzureCalls 0 'All deployment tests must remain offline'
Write-Output 'Deployment parser, preflight, adoption, secure grants, runtime RBAC, and ARM-shape tests passed without Azure calls.'
