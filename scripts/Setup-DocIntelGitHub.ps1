#Requires -Version 7.3
<#
.SYNOPSIS
    Discover an existing DocIntel deployment and preview/apply its supporting infrastructure.
.DESCRIPTION
    Preview never mutates Azure or Entra. Apply always produces a fresh what-if first, requires
    an explicit confirmation, preserves the existing Container App environment/origin/image,
    and merges managed runtime settings without replacing unrelated settings or identities.
#>
[CmdletBinding()]
param(
    [ValidateSet('Preview', 'Apply')][string]$Mode = 'Preview',
    [string]$Confirmation = '',
    [string]$ExpectedPlanHash = '',
    [string]$SubscriptionId = $env:SETUP_SUBSCRIPTION_ID,
    [string]$DirectoryTenantId = $env:SETUP_DIRECTORY_TENANT_ID,
    [string]$ResourceGroup = 'Docintel',
    [string]$ContainerAppName = 'ca-docintel-dev',
    [string]$ContainerName = '',
    [string]$RegistryName = 'acrdocinteldevokjs3fevazuto',
    [string]$RuntimeIdentityName = '',
    [string]$WebClientId = $env:DOCINTEL_WEB_CLIENT_ID,
    [string]$ApiClientId = $env:DOCINTEL_API_CLIENT_ID,
    [string]$AdminObjectId = $env:DOCINTEL_ADMIN_OBJECT_ID,
    [string]$ApplicationTenantId = 'contoso-legal',
    [string]$StorageAccountName = '',
    [string]$CosmosAccountName = '',
    [string]$KeyVaultName = '',
    [string]$FoundryAccountName = '',
    [string]$EnvironmentName = 'dev',
    [switch]$ConfigureDirectoryApplications,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Test-GuidValue([string]$Value) {
    $parsed = [guid]::Empty
    return [guid]::TryParse($Value, [ref]$parsed) -and $parsed -ne [guid]::Empty
}

foreach ($entry in @{ SubscriptionId = $SubscriptionId; DirectoryTenantId = $DirectoryTenantId }.GetEnumerator()) {
    if (-not (Test-GuidValue $entry.Value)) { throw "$($entry.Key) must be a nonempty GUID." }
}
foreach ($entry in @{ WebClientId = $WebClientId; ApiClientId = $ApiClientId; AdminObjectId = $AdminObjectId }.GetEnumerator()) {
    if ($entry.Value -and -not (Test-GuidValue $entry.Value)) { throw "$($entry.Key) must be a GUID when supplied." }
}
if ($WebClientId -and $WebClientId -eq $ApiClientId) { throw 'The web and API registrations must be separate.' }
if ($Mode -eq 'Apply' -and $Confirmation -cne 'APPLY') {
    throw 'Apply requires Confirmation=APPLY. Run Preview and review its resource plan first.'
}
if ($Mode -eq 'Apply' -and $ExpectedPlanHash -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'Apply requires the SHA-256 plan hash from the preceding Preview job.'
}
if ($ExpectedPlanHash -and $ExpectedPlanHash -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'ExpectedPlanHash must be the SHA-256 hash from the reviewed Preview.'
}
if ($ResourceGroup -notmatch '^[A-Za-z0-9_.()-]{1,90}$' -or
    $ContainerAppName -notmatch '^[a-z][a-z0-9-]{1,30}[a-z0-9]$' -or
    $RegistryName -notmatch '^[a-zA-Z0-9]{5,50}$' -or
    $ApplicationTenantId -notmatch '^[a-z0-9][a-z0-9-]{0,127}$' -or
    $EnvironmentName -notmatch '^[a-z0-9]{2,8}$') {
    throw 'The resource group, app, registry, application tenant or environment name is invalid.'
}
$root = Split-Path $PSScriptRoot -Parent
$template = Join-Path $root 'infra\main.bicep'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('docintel-setup-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$privateFiles = [Collections.Generic.List[string]]::new()

function Invoke-SetupAzure {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $result = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) {
        throw "Azure operation '$($Arguments[0..([Math]::Min(2, $Arguments.Length - 1))] -join ' ')' failed. No success is assumed."
    }
    $text = $result -join "`n"
    if ([String]::IsNullOrWhiteSpace($text)) { return $null }
    return $text | ConvertFrom-Json -Depth 100
}

function Write-PrivateJson([object]$Value) {
    $file = Join-Path ([IO.Path]::GetTempPath()) ('docintel-setup-' + [guid]::NewGuid().ToString('N') + '.json')
    $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $file -Encoding utf8
    $privateFiles.Add($file)
    return $file
}

function ConvertTo-PlanJson([object]$Value) {
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [Collections.IDictionary]) {
        $fields = foreach ($key in $Value.Keys | Sort-Object) {
            ($key | ConvertTo-Json -Compress) + ':' + (ConvertTo-PlanJson $Value[$key])
        }
        return '{' + ($fields -join ',') + '}'
    }
    if ($Value -is [pscustomobject]) {
        $fields = foreach ($property in $Value.PSObject.Properties | Sort-Object Name) {
            ($property.Name | ConvertTo-Json -Compress) + ':' + (ConvertTo-PlanJson $property.Value)
        }
        return '{' + ($fields -join ',') + '}'
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        return '[' + (@($Value | ForEach-Object { ConvertTo-PlanJson $_ }) -join ',') + ']'
    }
    return $Value | ConvertTo-Json -Compress
}

function Find-Resource {
    param([string]$Type, [string]$ExplicitName, [string]$BoundName, [string]$RecognizedPattern)
    $candidates = @($script:resources | Where-Object { $_.type -ieq $Type })
    if ($ExplicitName -and $BoundName -and $ExplicitName -ine $BoundName) {
        throw "Refusing to move $Type from the app's existing '$BoundName' binding to '$ExplicitName'. Data migration is not automatic."
    }
    $selected = if ($ExplicitName) { $ExplicitName } else { $BoundName }
    if ($selected) {
        $found = @($candidates | Where-Object { $_.name -ieq $selected })
        if ($found.Count -gt 1) { throw "Ambiguous resource name for $Type." }
        if (-not $found.Count -and $BoundName) {
            throw "The configured $Type '$BoundName' is not in the target resource group. Cross-group data adoption requires an explicit reviewed migration."
        }
        return @{ Name = $selected; Resource = $found | Select-Object -First 1 }
    }
    $recognized = @($candidates | Where-Object { $_.name -match $RecognizedPattern })
    if ($recognized.Count -eq 1) { return @{ Name = $recognized[0].name; Resource = $recognized[0] } }
    if ($candidates.Count) {
        throw "Existing $Type resources are not unambiguously bound to this app. Supply its resource name; setup will not guess or create a duplicate."
    }
    return @{ Name = ''; Resource = $null }
}

function Get-BoundAccount([string]$Setting, [string]$Pattern) {
    $entry = $script:environment | Where-Object { $_.name -ceq $Setting } | Select-Object -First 1
    if ($entry.secretRef) {
        throw "Existing '$Setting' is a secret reference. Resolve its resource binding with the operator before automatic adoption; secret values are not read."
    }
    if (-not $entry.value) { return '' }
    if ($entry.value -notmatch $Pattern) { throw "The existing '$Setting' endpoint is not supported for automatic same-group discovery." }
    return $Matches.account
}

function Get-ExistingSetting([string]$Name) {
    $entry = $script:environment | Where-Object { $_.name -ceq $Name } | Select-Object -First 1
    if ($entry.secretRef) { throw "Existing '$Name' must be resolved by the operator; setup never reads secret values." }
    return [string]$entry.value
}

function Resolve-Registration([string]$ClientId, [string]$DisplayName) {
    if (-not $ConfigureDirectoryApplications) {
        if (-not (Test-GuidValue $ClientId)) {
            throw "Supply $DisplayName's client ID as a GitHub variable, or explicitly enable directory application configuration."
        }
        return @{ appId = $ClientId; id = $null; web = @{ redirectUris = @() }; Planned = $false }
    }
    if ($ClientId) { return Invoke-SetupAzure @('ad', 'app', 'show', '--id', $ClientId) }
    $found = @(Invoke-SetupAzure @('ad', 'app', 'list', '--filter', "displayName eq '$DisplayName'"))
    if ($found.Count -gt 1) { throw "Multiple '$DisplayName' registrations exist. Supply its explicit client ID." }
    if ($found.Count -eq 1) { return $found[0] }
    return @{ appId = [guid]::Empty.ToString(); id = $null; web = @{ redirectUris = @() }; Planned = $true }
}

function Get-DirectoryPlanState([object]$Web, [object]$Api) {
    return @{
        web = @{
            clientId = $Web.appId; redirects = @($Web.web.redirectUris | Sort-Object)
            signInAudience = $Web.signInAudience
        }
        api = @{
            clientId = $Api.appId; identifierUris = @($Api.identifierUris | Sort-Object)
            scopes = @($Api.api.oauth2PermissionScopes | Sort-Object id)
            roles = @($Api.appRoles | Sort-Object id); accessTokenVersion = $Api.api.requestedAccessTokenVersion
        }
    }
}

function Test-RegistrationPrincipal([object]$Registration) {
    if ($Registration.Planned) { return $false }
    $principals = @(Invoke-SetupAzure @('ad', 'sp', 'list', '--filter', "appId eq '$($Registration.appId)'"))
    if ($principals.Count -gt 1) { throw 'The application has ambiguous directory service principals.' }
    return $principals.Count -eq 1
}

try {
    $account = Invoke-SetupAzure @('account', 'show', '--subscription', $SubscriptionId)
    if ([guid]$account.tenantId -ne [guid]$DirectoryTenantId) { throw 'The Azure subscription belongs to a different Entra directory.' }
    $script:resources = @(Invoke-SetupAzure @('resource', 'list', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroup))
    $app = Invoke-SetupAzure @('containerapp', 'show', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--name', $ContainerAppName)
    $containers = @($app.properties.template.containers)
    if ($ContainerName) { $container = $containers | Where-Object { $_.name -ceq $ContainerName } | Select-Object -First 1 }
    elseif ($containers.Count -eq 1) { $container = $containers[0] }
    else { throw 'The app has multiple containers. Supply the DocIntel container name explicitly.' }
    if (-not $container) { throw 'The named DocIntel application container is missing.' }
    $script:environment = @($container.env)
    $configuredDirectory = Get-ExistingSetting 'Identity__DirectoryTenantId'
    if ($configuredDirectory -and $configuredDirectory -ine $DirectoryTenantId) {
        throw 'The configured application directory differs from the selected subscription directory. Membership migration is not automatic.'
    }
    foreach ($localSetting in 'AskAi__RunStorageDirectory', 'DocumentJobs__DatabasePath',
        'Identity__DataProtectionDirectory', 'AzureAI__DevelopmentStorageConnectionString', 'AzureAI__LocalOpenAIEndpoint') {
        if (Get-ExistingSetting $localSetting) {
            throw "The app has a local-only '$localSetting' override. Preserve/migrate its data explicitly before shared infrastructure setup."
        }
    }
    foreach ($entry in @{
        AzureAI__CosmosDatabaseName = 'DocIntel'; AzureAI__CosmosHarnessesContainer = 'harnesses'
        AzureAI__CosmosJobsContainer = 'jobs'; AskAi__RunContainerName = 'ask-ai-runs'
        AskAi__DispatchQueueName = 'ask-ai'; AskAi__PoisonQueueName = 'ask-ai-poison'
        DocumentJobs__QueueName = 'document-jobs'; DocumentJobs__PoisonQueueName = 'document-jobs-poison'
    }.GetEnumerator()) {
        $configured = Get-ExistingSetting $entry.Key
        if ($configured -and $configured -cne $entry.Value) {
            throw "Existing '$($entry.Key)' uses a nonstandard data namespace. Setup will not repoint it; use an explicit reviewed template."
        }
    }
    if (-not $app.properties.configuration.ingress.fqdn) { throw 'The existing app must have an HTTPS ingress hostname.' }
    $origin = 'https://' + $app.properties.configuration.ingress.fqdn
    $configuredOrigin = Get-ExistingSetting 'Identity__PublicOrigin'
    if ($configuredOrigin) {
        $uri = $null
        if (-not [uri]::TryCreate($configuredOrigin, [UriKind]::Absolute, [ref]$uri) -or
            $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/') {
            throw 'The existing identity public origin is invalid; setup will not overwrite it.'
        }
        $acceptedHosts = @($app.properties.configuration.ingress.fqdn) +
            @($app.properties.configuration.ingress.customDomains | ForEach-Object name)
        if ($uri.Host -notin $acceptedHosts) { throw 'The identity origin is not an existing ingress/custom-domain binding.' }
        $origin = $configuredOrigin.TrimEnd('/')
    }
    $environmentId = $app.properties.environmentId
    if (-not $environmentId) { $environmentId = $app.properties.managedEnvironmentId }
    if (-not $environmentId -or $environmentId -notlike "/subscriptions/$SubscriptionId/*") {
        throw 'The existing Container Apps environment cannot be safely identified in the target subscription.'
    }
    $registry = Find-Resource 'Microsoft.ContainerRegistry/registries' $RegistryName '' '^acrdocintel'
    if (-not $registry.Resource) { throw 'The selected existing container registry was not found; verify the GitHub target settings.' }
    $registryDetails = Invoke-SetupAzure @('acr', 'show', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--name', $registry.Name)
    if ($registryDetails.roleAssignmentMode -eq 'AbacRepositoryPermissions') {
        throw 'This registry uses repository ABAC. Configure reviewed repository-scoped image roles before using automatic setup; AcrPull/AcrPush assumptions are not applied.'
    }
    $runtimeId = Get-ExistingSetting 'AZURE_CLIENT_ID'
    $identityName = $RuntimeIdentityName
    if (-not $identityName -and $runtimeId) {
        foreach ($property in @($app.identity.userAssignedIdentities.PSObject.Properties)) {
            if ($property.Value.clientId -eq $runtimeId) { $identityName = ($property.Name -split '/')[-1] }
        }
        if (-not $identityName) { throw 'The configured runtime client ID does not match an attached user-assigned identity.' }
    }
    if (-not $identityName) { $identityName = "uami-docintel-$EnvironmentName" }
    $identity = Find-Resource 'Microsoft.ManagedIdentity/userAssignedIdentities' $identityName '' '^uami-docintel-'
    $runtime = if ($identity.Resource) {
        Invoke-SetupAzure @('identity', 'show', '--subscription', $SubscriptionId, '--resource-group', $ResourceGroup,
            '--name', $identity.Name)
    } else { $null }
    if ($runtimeId -and (-not $runtime -or $runtime.clientId -ne $runtimeId)) {
        throw 'Refusing to replace the existing runtime identity. Verify its configured client ID first.'
    }
    $storageBound = Get-BoundAccount 'AzureAI__BlobEndpoint' '^https://(?<account>[a-z0-9]+)\.blob\.core\.windows\.net/?$'
    $queueBound = Get-BoundAccount 'AzureAI__QueueEndpoint' '^https://(?<account>[a-z0-9]+)\.queue\.core\.windows\.net/?$'
    if ($storageBound -and $queueBound -and $storageBound -ne $queueBound) { throw 'Existing Blob/Queue accounts differ; setup will not migrate execution state.' }
    if (-not $storageBound) { $storageBound = $queueBound }
    $storage = Find-Resource 'Microsoft.Storage/storageAccounts' $StorageAccountName $storageBound '^sadocintel'
    $blobContainers = @()
    $queues = @()
    if ($storage.Resource) {
        $existingBlobs = @(Invoke-SetupAzure @('storage', 'container-rm', 'list', '--subscription', $SubscriptionId,
            '--resource-group', $ResourceGroup, '--storage-account', $storage.Name))
        foreach ($blob in $existingBlobs) {
            $name = ($blob.name -split '/')[-1]
            if ($name -in @('ask-ai-runs', 'identity-keys')) {
                $access = if ($blob.publicAccess) { $blob.publicAccess } else { $blob.properties.publicAccess }
                if ($access -and $access -ine 'None') {
                    throw "Required Blob container '$name' is publicly accessible. Resolve it explicitly before automatic adoption."
                }
                $blobContainers += $name
            }
        }
        $queues = @(Invoke-SetupAzure @('storage', 'queue-rm', 'list', '--subscription', $SubscriptionId,
            '--resource-group', $ResourceGroup, '--storage-account', $storage.Name) |
            ForEach-Object { ($_.name -split '/')[-1] })
    }
    $keyRing = Get-ExistingSetting 'Identity__DataProtectionBlobUri'
    if ($keyRing) {
        $keyRingUri = $null
        if (-not [uri]::TryCreate($keyRing, [UriKind]::Absolute, [ref]$keyRingUri) -or
            $keyRingUri.Scheme -ne 'https' -or $keyRingUri.UserInfo -or $keyRingUri.Query -or $keyRingUri.Fragment -or
            -not $storage.Name -or $keyRingUri.Host -ine "$($storage.Name).blob.core.windows.net") {
            throw 'The existing browser key ring belongs to a different/unsupported storage binding. Setup will not rotate or migrate it silently.'
        }
    }
    $cosmos = Find-Resource 'Microsoft.DocumentDB/databaseAccounts' $CosmosAccountName (
        Get-BoundAccount 'AzureAI__CosmosEndpoint' '^https://(?<account>[a-z0-9-]+)\.documents\.azure\.com(?::443)?/?$') '^cosmos-docintel-'
    $keyBinding = Get-ExistingSetting 'Identity__DataProtectionKeyIdentifier'
    $vaultBound = ''
    $keyExists = $false
    if ($keyBinding) {
        if ($keyBinding -notmatch '^https://(?<account>[a-zA-Z0-9-]+)\.vault\.azure\.net/keys/docintel-identity/?$') {
            throw 'The existing key binding is not the supported versionless docintel-identity key. Preserve it through an explicit reviewed deployment.'
        }
        $vaultBound = $Matches.account
        $keyExists = $true
    }
    $vault = Find-Resource 'Microsoft.KeyVault/vaults' $KeyVaultName $vaultBound '^(kv-di-|kv-docintel)'
    if ($vault.Resource -and -not $keyExists) {
        $keys = Invoke-SetupAzure @('rest', '--method', 'GET', '--url',
            "https://management.azure.com$($vault.Resource.id)/keys?api-version=2023-07-01")
        $keyExists = @($keys.value | Where-Object { ($_.name -split '/')[-1] -eq 'docintel-identity' }).Count -eq 1
    }
    $aiBound = ''
    foreach ($setting in @('AzureAI__AzureOpenAIEndpoint', 'AzureAI__ContentUnderstandingEndpoint',
        'AzureAI__FoundryInferenceEndpoint', 'AzureAI__TranslatorEndpoint')) {
        $bound = Get-BoundAccount $setting '^https://(?<account>[a-zA-Z0-9-]+)\.(?:openai\.azure\.com|services\.ai\.azure\.com|cognitiveservices\.azure\.com)(?:/models)?/?$'
        if ($bound -and $aiBound -and $bound -ne $aiBound) {
            throw 'AI features currently use different accounts. Setup will not collapse them or move model/data processing; use an explicit reviewed multi-resource configuration.'
        }
        if ($bound) { $aiBound = $bound }
    }
    $foundry = Find-Resource 'Microsoft.CognitiveServices/accounts' $FoundryAccountName $aiBound '^(aif-|ai-docintel)'
    $aiEndpoints = @{}
    $modelExists = $false
    if ($foundry.Resource) {
        $ai = Invoke-SetupAzure @('cognitiveservices', 'account', 'show', '--subscription', $SubscriptionId,
            '--resource-group', $ResourceGroup, '--name', $foundry.Name)
        if ($ai.kind -ne 'AIServices') { throw 'Content Understanding setup requires a Foundry AIServices account; the existing resource is not silently replaced.' }
        $subdomain = $ai.properties.customSubDomainName
        if (-not $subdomain) { throw 'The existing Foundry account has no configured custom subdomain.' }
        $aiEndpoints = @{
            foundryInferenceEndpoint = "https://$subdomain.services.ai.azure.com/models"
            azureOpenAiEndpoint = "https://$subdomain.openai.azure.com/"
            contentUnderstandingEndpoint = "https://$subdomain.services.ai.azure.com/"
            translatorEndpoint = "https://$subdomain.cognitiveservices.azure.com/"
        }
        $models = @(Invoke-SetupAzure @('cognitiveservices', 'account', 'deployment', 'list',
            '--subscription', $SubscriptionId, '--resource-group', $ResourceGroup, '--name', $foundry.Name))
        $selected = $models | Where-Object { $_.name -eq 'gpt-5-mini' } | Select-Object -First 1
        if ($selected -and $selected.properties.model.name -ne 'gpt-5-mini') {
            throw 'The deployment named gpt-5-mini contains a different model; setup will not replace it.'
        }
        $modelExists = $null -ne $selected
    }
    $reuseDatabase = $false
    $cosmosContainers = @()
    if ($cosmos.Resource) {
        $databases = @(Invoke-SetupAzure @('cosmosdb', 'sql', 'database', 'list', '--subscription', $SubscriptionId,
            '--resource-group', $ResourceGroup, '--account-name', $cosmos.Name))
        if (@($databases | Where-Object { ($_.name -split '/')[-1] -ceq 'DocIntel' }).Count) {
            $reuseDatabase = $true
            $existingContainers = @(Invoke-SetupAzure @('cosmosdb', 'sql', 'container', 'list',
                '--subscription', $SubscriptionId, '--resource-group', $ResourceGroup,
                '--account-name', $cosmos.Name, '--database-name', 'DocIntel'))
            foreach ($item in $existingContainers | Where-Object { ($_.name -split '/')[-1] -in @('harnesses', 'jobs', 'ocr-usage', 'review-sessions') }) {
                if (@($item.resource.partitionKey.paths).Count -ne 1 -or $item.resource.partitionKey.paths[0] -cne '/tenantId') {
                    throw "Existing Cosmos container '$($item.name)' has a different partition key; no replacement is attempted."
                }
                $cosmosContainers += ($item.name -split '/')[-1]
            }
        }
    }
    if (-not $WebClientId) { $WebClientId = Get-ExistingSetting 'Identity__ClientId' }
    if (-not $ApiClientId) { $ApiClientId = Get-ExistingSetting 'Identity__ApiClientId' }
    $web = Resolve-Registration $WebClientId 'docintel-web'
    $api = Resolve-Registration $ApiClientId 'docintel-api'
    if ($web.appId -eq $api.appId -and $web.appId -ne [guid]::Empty.ToString()) {
        throw 'The resolved web/API registrations must be different.'
    }
    if ($ConfigureDirectoryApplications -and -not $api.Planned -and
        @($api.appRoles | Where-Object { $_.value -ceq 'DocIntel.Access' }).Count) {
        throw 'An app role conflicts with delegated DocIntel.Access. Resolve it explicitly; setup will not delete roles or scopes.'
    }
    if ($ConfigureDirectoryApplications -and -not $api.Planned -and
        @($api.api.oauth2PermissionScopes |
            Where-Object { $_.value -ceq 'DocIntel.Access' -and -not $_.isEnabled }).Count) {
        throw 'DocIntel.Access is disabled; setup will not override a deliberate permission disablement.'
    }
    $webWasPlanned = [bool]$web.Planned
    $apiWasPlanned = [bool]$api.Planned
    $directoryState = if ($ConfigureDirectoryApplications) { Get-DirectoryPlanState $web $api } else { $null }
    $webHasPrincipal = $ConfigureDirectoryApplications -and (Test-RegistrationPrincipal $web)
    $apiHasPrincipal = $ConfigureDirectoryApplications -and (Test-RegistrationPrincipal $api)
    $grantsEntry = $script:environment | Where-Object { $_.name -ceq 'DOCINTEL_IDENTITY_GRANTS' } | Select-Object -First 1
    $bootstrapGrants = $null
    $hasGrants = [bool]$grantsEntry.secretRef
    if ($grantsEntry.value) {
        $existingGrants = $grantsEntry.value | ConvertFrom-Json -Depth 30
        $hasGrants = @($existingGrants.Memberships | Where-Object { $null -ne $_ }).Count -gt 0 -or
            @($existingGrants.PlatformAdministrators | Where-Object { $null -ne $_ }).Count -gt 0
    }
    if (-not $hasGrants) {
        if (Test-GuidValue $AdminObjectId) {
            $bootstrapGrants = @{ Memberships = @(@{
                DirectoryTenantId = $DirectoryTenantId; ObjectId = $AdminObjectId
                ApplicationTenantId = $ApplicationTenantId; Role = 'Admin'; AllDocuments = $true
            }) }
        } elseif ($Mode -eq 'Apply') {
            throw 'Supply DOCINTEL_ADMIN_OBJECT_ID once to bootstrap your tenant admin. It cannot be inferred from the GitHub actor.'
        }
    }
    $parameters = @{
        location = $app.location; environmentName = $EnvironmentName; deployApplication = $false
        containerAppName = $ContainerAppName; publicOrigin = $origin
        existingEnvironmentId = $environmentId
        acrName = $registry.Name; reuseRegistry = $true
        runtimeIdentityName = $identity.Name; reuseRuntimeIdentity = [bool]$runtime
        storageAccountName = $storage.Name; reuseStorageAccount = [bool]$storage.Resource
        existingBlobContainers = $blobContainers; existingQueues = $queues
        cosmosAccountNameOverride = $cosmos.Name; reuseCosmosAccount = [bool]$cosmos.Resource
        reuseCosmosDatabase = $reuseDatabase; existingCosmosContainers = $cosmosContainers
        keyVaultNameOverride = $vault.Name; reuseKeyVault = [bool]$vault.Resource; reuseIdentityKey = $keyExists
        foundryAccountNameOverride = $foundry.Name; reuseFoundryAccount = [bool]$foundry.Resource
        deployModels = -not $modelExists
        directoryTenantId = $DirectoryTenantId; webClientId = $web.appId; apiClientId = $api.appId
        identityGrants = @{}
    }
    foreach ($key in $aiEndpoints.Keys) { $parameters[$key] = $aiEndpoints[$key] }
    $wrapped = @{ parameters = @{} }
    foreach ($key in $parameters.Keys) { $wrapped.parameters[$key] = @{ value = $parameters[$key] } }
    $parameterFile = Write-PrivateJson $wrapped
    $deploymentName = 'docintel-setup-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
    $plan = Invoke-SetupAzure @('deployment', 'group', 'what-if', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--template-file', $template, '--parameters', "@$parameterFile",
        '--result-format', 'FullResourcePayloads', '--no-pretty-print')
    if ($plan.error -or ($plan.status -and $plan.status -ne 'Succeeded')) {
        throw 'Azure did not produce a successful infrastructure preview. No apply is attempted.'
    }
    if (@($plan.changes | Where-Object { $_.changeType -eq 'Delete' }).Count) {
        throw 'The infrastructure preview includes deletion. Setup refuses to apply it.'
    }
    $appChanges = @($plan.changes | Where-Object { $_.resourceId -ieq $app.id -and $_.changeType -notin @('NoChange', 'Ignore') })
    if ($appChanges.Count) { throw 'Infrastructure preparation must not replace the existing Container App.' }
    $resourceChanges = @($plan.changes | ForEach-Object {
        @{ resourceId = $_.resourceId; changeType = $_.changeType }
    } | Sort-Object { $_.resourceId })
    $planIdentity = [ordered]@{
        subscription = $SubscriptionId; resourceGroup = $ResourceGroup; app = $ContainerAppName
        environment = $environmentId; origin = $origin; image = $container.image
        runtimeIdentity = $identity.Name; storage = $storage.Name; cosmos = $cosmos.Name
        vault = $vault.Name; foundry = $foundry.Name; configureDirectory = [bool]$ConfigureDirectoryApplications
        webClientId = $web.appId; apiClientId = $api.appId
        directoryState = $directoryState
        webHasPrincipal = $webHasPrincipal; apiHasPrincipal = $apiHasPrincipal
        bootstrapAdmin = if ($bootstrapGrants) { $AdminObjectId } else { '' }
        changes = @($plan.changes | Sort-Object resourceId)
        appVersion = $app.properties.latestRevisionName
        templateHash = (Get-FileHash -LiteralPath $template -Algorithm SHA256).Hash
    }
    $planHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes((ConvertTo-PlanJson $planIdentity))))
    if ($ExpectedPlanHash -and $planHash -cne $ExpectedPlanHash.ToUpperInvariant()) {
        throw 'The discovered targets or infrastructure plan changed since Preview. Apply was stopped before any mutation; review a new preview.'
    }
    $summary = @{
        mode = $Mode; subscriptionId = $SubscriptionId; resourceGroup = $ResourceGroup
        application = $ContainerAppName; publicOrigin = $origin; existingEnvironmentId = $environmentId
        existingImagePreserved = $container.image; runtimeIdentity = $identity.Name
        resourceChanges = $resourceChanges; bootstrapAdminWillBeCreated = [bool]$bootstrapGrants
        bootstrapAdminRequired = -not $hasGrants -and -not $bootstrapGrants
        directoryApplicationChanges = [bool]$ConfigureDirectoryApplications
        planHash = $planHash
        plannedWebRegistration = [bool]$web.Planned; plannedApiRegistration = [bool]$api.Planned
        missingServicePrincipals = if ($ConfigureDirectoryApplications) {
            @(@($webHasPrincipal, $apiHasPrincipal) | Where-Object { -not $_ }).Count
        } else { 0 }
        warning = 'Resource provisioning can incur charges. Review names, regions, quotas and role assignments before Apply.'
    }
    $summary | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $OutputDirectory 'setup-plan.json') -Encoding utf8
    $plan | ConvertTo-Json -Depth 100 | Set-Content (Join-Path $OutputDirectory 'what-if.json') -Encoding utf8
    if ($env:GITHUB_OUTPUT) { "plan_hash=$planHash" | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
    $summary | ConvertTo-Json -Depth 20
    if ($Mode -eq 'Preview') {
        Write-Output 'Preview only: no Azure resources, identities, roles, registration settings or app configuration were changed.'
        if (-not $hasGrants -and -not $bootstrapGrants) {
            Write-Warning 'Set the DOCINTEL_ADMIN_OBJECT_ID GitHub secret before Apply. Your administrator cannot be inferred safely.'
        }
        return
    }
    if ($web.Planned) {
        $web = Invoke-SetupAzure @('ad', 'app', 'create', '--display-name', 'docintel-web',
            '--sign-in-audience', 'AzureADMyOrg')
    }
    if ($api.Planned) {
        $api = Invoke-SetupAzure @('ad', 'app', 'create', '--display-name', 'docintel-api',
            '--sign-in-audience', 'AzureADMyOrg')
    }
    if ($ConfigureDirectoryApplications) {
        foreach ($registration in @($web, $api)) {
            if (-not (Test-RegistrationPrincipal $registration)) {
                Invoke-SetupAzure @('ad', 'sp', 'create', '--id', $registration.appId) | Out-Null
            }
        }
    }
    $wrapped.parameters.webClientId.value = $web.appId
    $wrapped.parameters.apiClientId.value = $api.appId
    $parameterFile = Write-PrivateJson $wrapped
    $deployed = Invoke-SetupAzure @('deployment', 'group', 'create', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--name', $deploymentName, '--template-file', $template,
        '--parameters', "@$parameterFile")
    $outputs = $deployed.properties.outputs
    if (-not $outputs.managedIdentityResourceId.value -or -not $outputs.storageBlobEndpoint.value -or
        -not $outputs.cosmosEndpoint.value -or -not $outputs.contentUnderstandingEndpoint.value) {
        throw 'Infrastructure returned incomplete outputs; the existing app configuration was not changed.'
    }
    if ($ConfigureDirectoryApplications) {
        $currentWeb = if ($webWasPlanned) { $web } else { Invoke-SetupAzure @('ad', 'app', 'show', '--id', $web.appId) }
        $currentApi = if ($apiWasPlanned) { $api } else { Invoke-SetupAzure @('ad', 'app', 'show', '--id', $api.appId) }
        if ((ConvertTo-PlanJson (Get-DirectoryPlanState $currentWeb $currentApi)) -cne
            (ConvertTo-PlanJson (Get-DirectoryPlanState $web $api))) {
            throw 'Directory registration settings changed during provisioning. They were not overwritten; preview again.'
        }
        $scopes = @($api.api.oauth2PermissionScopes | Where-Object { $null -ne $_ })
        if (@($api.appRoles | Where-Object { $_.value -ceq 'DocIntel.Access' }).Count) {
            throw 'An app role conflicts with delegated DocIntel.Access. Resolve it explicitly; setup will not delete roles or scopes.'
        }
        $scope = $scopes | Where-Object { $_.value -ceq 'DocIntel.Access' } | Select-Object -First 1
        if (-not $scope) {
            $scopes += @{
                id = [guid]::NewGuid().ToString(); value = 'DocIntel.Access'; isEnabled = $true; type = 'Admin'
                adminConsentDisplayName = 'Access DocIntel API'
                adminConsentDescription = 'Access DocIntel on behalf of an explicitly assigned signed-in user.'
            }
        } elseif (-not $scope.isEnabled) { throw 'DocIntel.Access is disabled; setup will not override a deliberate permission disablement.' }
        $apiSettings = @{ oauth2PermissionScopes = $scopes }
        if (-not $api.api.requestedAccessTokenVersion) { $apiSettings.requestedAccessTokenVersion = 2 }
        $apiValues = @{ api = $apiSettings }
        if (-not @($api.identifierUris).Count) { $apiValues.identifierUris = @("api://$($api.appId)") }
        $apiPatch = Write-PrivateJson $apiValues
        Invoke-SetupAzure @('rest', '--method', 'PATCH', '--url', "https://graph.microsoft.com/v1.0/applications/$($api.id)",
            '--body', "@$apiPatch") | Out-Null
        $redirect = $origin + '/signin-oidc'
        $redirects = @($web.web.redirectUris)
        if ($redirect -notin $redirects) {
            $redirects += $redirect
            Invoke-SetupAzure (@('ad', 'app', 'update', '--id', $web.appId, '--web-redirect-uris') + $redirects) | Out-Null
        }
        $issuer = "https://login.microsoftonline.com/$DirectoryTenantId/v2.0"
        $subject = $outputs.managedIdentityPrincipalId.value
        $credentials = @(Invoke-SetupAzure @('ad', 'app', 'federated-credential', 'list', '--id', $web.appId))
        if (-not @($credentials | Where-Object {
            $_.issuer -ceq $issuer -and $_.subject -ceq $subject -and 'api://AzureADTokenExchange' -in $_.audiences
        }).Count) {
            $federation = Write-PrivateJson @{
                name = "docintel-runtime-$($subject.Substring(0, 8))"; issuer = $issuer; subject = $subject
                audiences = @('api://AzureADTokenExchange')
            }
            Invoke-SetupAzure @('ad', 'app', 'federated-credential', 'create', '--id', $web.appId,
                '--parameters', "@$federation") | Out-Null
        }
    }
    $current = Invoke-SetupAzure @('containerapp', 'show', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--name', $ContainerAppName)
    $currentContainer = $current.properties.template.containers | Where-Object { $_.name -ceq $container.name } | Select-Object -First 1
    if (($currentContainer | ConvertTo-Json -Depth 100 -Compress) -cne
        ($container | ConvertTo-Json -Depth 100 -Compress) -or
        $current.properties.configuration.ingress.fqdn -ne $app.properties.configuration.ingress.fqdn) {
        throw 'The app changed during setup. Infrastructure is provisioned, but runtime settings were not overwritten; preview again.'
    }
    Invoke-SetupAzure @('containerapp', 'identity', 'assign', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--name', $ContainerAppName,
        '--user-assigned', $outputs.managedIdentityResourceId.value) | Out-Null
    Invoke-SetupAzure @('containerapp', 'registry', 'set', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--name', $ContainerAppName,
        '--server', $outputs.containerRegistryLoginServer.value, '--identity', $outputs.managedIdentityResourceId.value) | Out-Null
    $settings = @{
        DOTNET_ENVIRONMENT = 'Production'; ASPNETCORE_ENVIRONMENT = 'Production'
        Models__AllowDevelopmentSimulation = 'false'
        AZURE_CLIENT_ID = $outputs.managedIdentityClientId.value
        Identity__Mode = 'Entra'; Identity__DirectoryTenantId = $DirectoryTenantId
        Identity__ClientId = $web.appId; Identity__ApiClientId = $api.appId
        Identity__UseManagedIdentityAssertion = 'true'
        Identity__ManagedIdentityClientId = $outputs.managedIdentityClientId.value
        Identity__PublicOrigin = $origin
        Identity__DataProtectionBlobUri = if ($keyRing) { $keyRing } else {
            $outputs.storageBlobEndpoint.value.TrimEnd('/') + '/identity-keys/keyring.xml'
        }
        Identity__DataProtectionKeyIdentifier = $outputs.identityKeyIdentifier.value
        AzureAI__BlobEndpoint = $outputs.storageBlobEndpoint.value; AzureAI__QueueEndpoint = $outputs.storageQueueEndpoint.value
        AzureAI__CosmosEndpoint = $outputs.cosmosEndpoint.value
        AzureAI__AzureOpenAIEndpoint = $outputs.azureOpenAiEndpoint.value
        AzureAI__ContentUnderstandingEndpoint = $outputs.contentUnderstandingEndpoint.value
        AzureAI__FoundryInferenceEndpoint = $outputs.foundryEndpoint.value
        AzureAI__TranslatorEndpoint = $outputs.translatorEndpoint.value
    }
    foreach ($name in 'Identity__Directory__Enabled', 'Channels__Email__Enabled', 'Channels__Teams__Enabled') {
        if (-not @($script:environment | Where-Object { $_.name -ceq $name }).Count) { $settings[$name] = 'false' }
    }
    if ($bootstrapGrants) { $settings.DOCINTEL_IDENTITY_GRANTS = $bootstrapGrants | ConvertTo-Json -Depth 20 -Compress }
    $arguments = @('containerapp', 'update', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--name', $ContainerAppName,
        '--container-name', $container.name, '--set-env-vars')
    foreach ($name in $settings.Keys | Sort-Object) { $arguments += "$name=$($settings[$name])" }
    Invoke-SetupAzure $arguments | Out-Null
    $verified = Invoke-SetupAzure @('containerapp', 'show', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--name', $ContainerAppName)
    $verifiedContainer = $verified.properties.template.containers |
        Where-Object { $_.name -ceq $container.name } | Select-Object -First 1
    if ($verifiedContainer.image -cne $container.image -or
        $verified.properties.configuration.ingress.fqdn -cne $app.properties.configuration.ingress.fqdn) {
        throw 'The post-setup app image/origin no longer matches the accepted target. Setup success is not claimed.'
    }
    foreach ($name in $settings.Keys) {
        $saved = $verifiedContainer.env | Where-Object { $_.name -ceq $name } | Select-Object -First 1
        if ($saved.value -cne $settings[$name]) {
            throw "Runtime setting '$name' was not persisted as intended. Setup success is not claimed."
        }
    }
    foreach ($entry in $script:environment | Where-Object { -not $settings.ContainsKey($_.name) }) {
        $saved = $verifiedContainer.env | Where-Object { $_.name -ceq $entry.name } | Select-Object -First 1
        if ($saved.value -cne $entry.value -or $saved.secretRef -cne $entry.secretRef) {
            throw "An unrelated runtime setting '$($entry.name)' changed. Setup success is not claimed."
        }
    }
    Write-Output "Infrastructure and runtime settings applied. Existing image and ingress were preserved: $origin"
    Write-Output 'Run Validate and deploy DocIntel on main to publish the tested image. Verify readiness, sign-in and a real document/Ask AI request.'
}
finally {
    foreach ($file in $privateFiles) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
}
