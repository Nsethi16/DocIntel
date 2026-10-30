#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Setup-DocIntelGitHub.ps1'
Import-Module (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\DocIntelRelease.psm1') -Force
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('docintel-setup-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$global:DocIntelSetupFixture = @{}
$global:DocIntelSetupFixture.subscription = [guid]::NewGuid().ToString()
$global:DocIntelSetupFixture.directory = [guid]::NewGuid().ToString()
$global:DocIntelSetupFixture.webId = [guid]::NewGuid().ToString()
$global:DocIntelSetupFixture.apiId = [guid]::NewGuid().ToString()
$global:DocIntelSetupFixture.adminId = [guid]::NewGuid().ToString()
$global:DocIntelSetupFixture.runtimeClient = [guid]::NewGuid().ToString()
$global:DocIntelSetupFixture.runtimePrincipal = [guid]::NewGuid().ToString()
$global:DocIntelSetupFixture.prefix = "/subscriptions/$($global:DocIntelSetupFixture.subscription)/resourceGroups/Docintel/providers"
$global:DocIntelSetupFixture.appId = "$($global:DocIntelSetupFixture.prefix)/Microsoft.App/containerApps/ca-docintel-dev"
$global:DocIntelSetupFixture.environmentId = "$($global:DocIntelSetupFixture.prefix)/Microsoft.App/managedEnvironments/keep-this-environment"
$global:DocIntelSetupFixture.runtimeId = "$($global:DocIntelSetupFixture.prefix)/Microsoft.ManagedIdentity/userAssignedIdentities/uami-docintel-dev"
$global:DocIntelSetupFixture.queueUrl = "https://management.azure.com$($global:DocIntelSetupFixture.prefix)/Microsoft.Storage/storageAccounts/sadocinteltest/queueServices/default/queues?api-version=2023-05-01"
$global:DocIntelSetupFixture.nextQueueUrl = $global:DocIntelSetupFixture.queueUrl + '&$skipToken=second'
$global:DocIntelSetupFixture.origin = 'https://ca-docintel-dev.example.azurecontainerapps.io'
$global:DocIntelSetupFixture.calls = [Collections.Generic.List[object]]::new()
$global:DocIntelSetupFixture.mutations = [Collections.Generic.List[object]]::new()
$global:DocIntelSetupFixture.scenario = ''
$global:DocIntelSetupFixture.principals = @{}
$global:DocIntelSetupFixture.app = @{
    id = $global:DocIntelSetupFixture.appId; location = 'westus'
    identity = @{ userAssignedIdentities = @{ $global:DocIntelSetupFixture.runtimeId = @{ clientId = $global:DocIntelSetupFixture.runtimeClient } } }
    properties = @{
        environmentId = $global:DocIntelSetupFixture.environmentId; latestRevisionName = 'existing-revision'
        configuration = @{ ingress = @{ fqdn = 'ca-docintel-dev.example.azurecontainerapps.io'; customDomains = @() } }
        template = @{ containers = @(@{
            name = 'docintel-api'; image = 'registry.azurecr.io/docintel:keep-this-image'
            env = @(
                @{ name = 'AZURE_CLIENT_ID'; value = $global:DocIntelSetupFixture.runtimeClient }
                @{ name = 'Identity__PublicOrigin'; value = $global:DocIntelSetupFixture.origin }
                @{ name = 'Identity__ClientId'; value = $global:DocIntelSetupFixture.webId }
                @{ name = 'Identity__ApiClientId'; value = $global:DocIntelSetupFixture.apiId }
                @{ name = 'AzureAI__BlobEndpoint'; value = 'https://sadocinteltest.blob.core.windows.net/' }
                @{ name = 'AzureAI__QueueEndpoint'; value = 'https://sadocinteltest.queue.core.windows.net/' }
                @{ name = 'AzureAI__CosmosEndpoint'; value = 'https://cosmos-docintel-test.documents.azure.com:443/' }
                @{ name = 'AzureAI__AzureOpenAIEndpoint'; value = 'https://aif-docintel-test.openai.azure.com/' }
                @{ name = 'AzureAI__ContentUnderstandingEndpoint'; value = 'https://aif-docintel-test.services.ai.azure.com/' }
                @{ name = 'AzureAI__TranslatorEndpoint'; value = 'https://tr-docintel-test.cognitiveservices.azure.com/' }
                @{ name = 'DocumentTranslation__StagingBlobEndpoint'; value = 'https://stagingaccount.blob.core.windows.net/' }
                @{ name = 'AzureAI__DecisionEndpoint'; value = 'https://decision-other.services.ai.azure.com/' }
                @{ name = 'AzureAI__DecisionDeploymentName'; value = 'Decision-1' }
                @{ name = 'Identity__DataProtectionKeyIdentifier'; value = 'https://kv-di-test.vault.azure.net/keys/docintel-identity' }
                @{ name = 'UserOwnedSetting'; value = 'must-survive' }
                @{ name = 'UserOwnedSecret'; secretRef = 'existing-secret' }
                @{ name = 'Channels__Email__Enabled'; value = 'true' }
            )
        }) }
    }
}
$global:DocIntelSetupFixture.resourceList = @(
    @{ id = "$($global:DocIntelSetupFixture.prefix)/Microsoft.ContainerRegistry/registries/testregistry"; type = 'Microsoft.ContainerRegistry/registries'; name = 'testregistry' }
    @{ id = $global:DocIntelSetupFixture.runtimeId; type = 'Microsoft.ManagedIdentity/userAssignedIdentities'; name = 'uami-docintel-dev' }
    @{ id = "$($global:DocIntelSetupFixture.prefix)/Microsoft.Storage/storageAccounts/sadocinteltest"; type = 'Microsoft.Storage/storageAccounts'; name = 'sadocinteltest' }
    @{ id = "$($global:DocIntelSetupFixture.prefix)/Microsoft.DocumentDB/databaseAccounts/cosmos-docintel-test"; type = 'Microsoft.DocumentDB/databaseAccounts'; name = 'cosmos-docintel-test' }
    @{ id = "$($global:DocIntelSetupFixture.prefix)/Microsoft.KeyVault/vaults/kv-di-test"; type = 'Microsoft.KeyVault/vaults'; name = 'kv-di-test' }
    @{ id = "$($global:DocIntelSetupFixture.prefix)/Microsoft.CognitiveServices/accounts/aif-docintel-test"; type = 'Microsoft.CognitiveServices/accounts'; name = 'aif-docintel-test' }
)

function az {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $global:DocIntelSetupFixture.calls.Add($Arguments)
    $global:LASTEXITCODE = 0
    $key = ($Arguments | Select-Object -First 4) -join ' '
    $mutation = ($Arguments -contains 'create') -or ($Arguments -contains 'assign') -or
        ($Arguments -contains 'update') -or ($key -like 'containerapp registry set*') -or
        ($Arguments -contains 'PATCH')
    if ($mutation) { $global:DocIntelSetupFixture.mutations.Add($Arguments) }
    $result = switch -Wildcard ($key) {
        'account show --subscription *' { @{ tenantId = $global:DocIntelSetupFixture.directory }; break }
        'resource list --subscription *' { $global:DocIntelSetupFixture.resourceList; break }
        'containerapp show --subscription *' { $global:DocIntelSetupFixture.app; break }
        'acr show --subscription *' { @{ roleAssignmentMode = 'LegacyRegistryPermissions' }; break }
        'identity show --subscription *' {
            @{ id = $global:DocIntelSetupFixture.runtimeId; clientId = $global:DocIntelSetupFixture.runtimeClient; principalId = $global:DocIntelSetupFixture.runtimePrincipal }; break
        }
        'storage container-rm list --subscription' {
            @(@{ name = 'ask-ai-runs'; publicAccess = 'None' }, @{ name = 'identity-keys'; publicAccess = 'None' },
                @{ name = 'translation-source'; publicAccess = 'None' },
                @{ name = 'translation-target'; publicAccess = 'None' }); break
        }
        'storage account management-policy show' {
            if ($Arguments -notcontains 'stagingaccount') { throw 'Original storage policy cannot be inspected.' }
            @{ policy = @{ rules = @((Get-DocIntelTranslationLifecycleRule)) } }; break
        }
        'storage account show --subscription' {
            @{ name = 'stagingaccount'
                id = "$($global:DocIntelSetupFixture.prefix)/Microsoft.Storage/storageAccounts/stagingaccount"
                location = 'westus'; kind = 'StorageV2'; sku = @{ name = 'Standard_LRS' }
                tags = Get-DocIntelTranslationStagingTags $global:DocIntelSetupFixture.appId `
                    $global:DocIntelSetupFixture.runtimeId `
                    "$($global:DocIntelSetupFixture.prefix)/Microsoft.CognitiveServices/accounts/tr-docintel-test"
                minimumTlsVersion = 'TLS1_2'; enableHttpsTrafficOnly = $true
                allowBlobPublicAccess = $false; allowSharedKeyAccess = $false }; break
        }
        'rest --method GET --url' {
            $url = $Arguments[4]
            if ($url -notin @($global:DocIntelSetupFixture.queueUrl, $global:DocIntelSetupFixture.nextQueueUrl)) {
                throw 'Unexpected storage queue management URL.'
            }
            $isNextPage = $url -eq $global:DocIntelSetupFixture.nextQueueUrl
            if ($global:DocIntelSetupFixture.scenario -eq 'queue-read-failure' -or
                ($isNextPage -and $global:DocIntelSetupFixture.scenario -eq 'queue-page-failure')) {
                $global:LASTEXITCODE = 1
                @{ error = @{ code = 'AuthorizationFailed' } }; break
            }
            $items = @(@{ name = 'ask-ai' }, @{ name = 'ask-ai-poison' },
                @{ name = 'document-jobs' }, @{ name = 'document-jobs-poison' })
            $page = @{ value = $items }
            switch ($global:DocIntelSetupFixture.scenario) {
                'empty-queues' { $page.value = @() }
                'invalid-queue-response' { $page = @{ unexpected = @() } }
                'invalid-queue-name' { $page.value = @(@{ name = '' }) }
                'paged-queues' {
                    if ($isNextPage) { $page.value = $items[2..3] }
                    else {
                        $page.value = $items[0..1]
                        $page.nextLink = $global:DocIntelSetupFixture.nextQueueUrl
                    }
                }
                'queue-page-failure' {
                    $page.value = $items[0..1]
                    $page.nextLink = $global:DocIntelSetupFixture.nextQueueUrl
                }
                'foreign-queue-nextlink' { $page.nextLink = 'https://untrusted.example/queues' }
                'wrong-account-nextlink' {
                    $page.nextLink = $global:DocIntelSetupFixture.queueUrl.Replace('sadocinteltest', 'otheraccount')
                }
                'repeated-queue-nextlink' { $page.nextLink = $global:DocIntelSetupFixture.queueUrl }
            }
            $page; break
        }
        'cognitiveservices account list --subscription' {
            @(@{ id = "$($global:DocIntelSetupFixture.prefix)/Microsoft.CognitiveServices/accounts/tr-docintel-test"
                kind = $(if ($global:DocIntelSetupFixture.scenario -eq 'unsupported-translator') {
                    'AIServices'
                } else { 'TextTranslation' }); sku = @{ name = 'S1' }; location = 'westus'
                identity = @{ type = 'SystemAssigned' }
                properties = @{ customSubDomainName = 'tr-docintel-test'; disableLocalAuth = $true } }); break
        }
        'cognitiveservices account show --subscription' {
            @{ kind = 'AIServices'; properties = @{ customSubDomainName = 'aif-docintel-test' } }; break
        }
        'cognitiveservices account deployment list' {
            @(@{ name = 'gpt-5-mini'; sku = @{ capacity = 100 }
                properties = @{ model = @{ name = 'gpt-5-mini' } } }); break
        }
        'cosmosdb sql database list' { @(@{ name = 'DocIntel' }); break }
        'cosmosdb sql container list' {
            @(@{ name = 'harnesses'; resource = @{ partitionKey = @{ paths = @(
                $(if ($global:DocIntelSetupFixture.scenario -eq 'wrong-partition') { '/other' } else { '/tenantId' })
            ) } } }); break
        }
        'ad app show --id' {
            if ($Arguments[4] -eq $global:DocIntelSetupFixture.webId) {
                @{
                    appId = $global:DocIntelSetupFixture.webId; id = $global:DocIntelSetupFixture.webId
                    web = @{ redirectUris = @('https://other.example/signin-oidc') }
                }
            } else {
                @{
                    appId = $global:DocIntelSetupFixture.apiId; id = $global:DocIntelSetupFixture.apiId
                    identifierUris = @("api://$($global:DocIntelSetupFixture.apiId)")
                    api = @{ oauth2PermissionScopes = @(@{
                        id = [guid]::Empty.ToString()
                        value = if ($global:DocIntelSetupFixture.scenario -eq 'disabled-scope') {
                            'DocIntel.Access'
                        } else { 'Existing.Scope' }
                        isEnabled = $global:DocIntelSetupFixture.scenario -ne 'disabled-scope'
                        type = 'Admin'
                    }) }
                    appRoles = @($(if ($global:DocIntelSetupFixture.scenario -eq 'role-collision') {
                        @{ value = 'DocIntel.Access' }
                    }))
                }
            }
            break
        }
        'ad app federated-credential list' { @(); break }
        'ad app federated-credential create' { @{}; break }
        'ad app update --id' { @{}; break }
        'ad sp list --filter' {
            $filter = $Arguments[4]
            ,@($global:DocIntelSetupFixture.principals.Values | Where-Object { $filter -like "*$($_.appId)*" }); break
        }
        'ad sp create --id' {
            $client = $Arguments[4]
            $principal = @{ id = [guid]::NewGuid().ToString(); appId = $client }
            $global:DocIntelSetupFixture.principals[$client] = $principal
            $principal; break
        }
        'rest --method PATCH --url' {
            $index = [Array]::IndexOf($Arguments, '--body')
            $global:DocIntelSetupFixture.apiPatch = Get-Content $Arguments[$index + 1].TrimStart('@') -Raw | ConvertFrom-Json -Depth 100
            @{}; break
        }
        'deployment group what-if --subscription' {
            $parameterIndex = [Array]::IndexOf($Arguments, '--parameters')
            $file = $Arguments[$parameterIndex + 1].TrimStart('@')
            $global:DocIntelSetupFixture.parameters = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -Depth 100
            $changes = @(@{
                resourceId = "$($global:DocIntelSetupFixture.prefix)/Microsoft.Storage/storageAccounts/sadocinteltest/blobServices/default/containers/ask-ai-runs"
                changeType = 'Create'; delta = @(@{ path = 'properties.publicAccess'; propertyChangeType = 'Create'; after = 'None' })
            })
            if ($global:DocIntelSetupFixture.scenario -eq 'delete') { $changes[0].changeType = 'Delete' }
            if ($global:DocIntelSetupFixture.scenario -eq 'replace-app') { $changes[0].resourceId = $global:DocIntelSetupFixture.appId }
            if ($global:DocIntelSetupFixture.scenario -eq 'drift') { $changes[0].delta[0].after = 'Blob' }
            @{ changes = $changes }; break
        }
        'deployment group create --subscription' {
            $outputs = @{}
            foreach ($pair in @{
                managedIdentityResourceId = $global:DocIntelSetupFixture.runtimeId
                managedIdentityClientId = $global:DocIntelSetupFixture.runtimeClient
                managedIdentityPrincipalId = $global:DocIntelSetupFixture.runtimePrincipal
                containerRegistryLoginServer = 'testregistry.azurecr.io'
                storageBlobEndpoint = 'https://sadocinteltest.blob.core.windows.net/'
                storageQueueEndpoint = 'https://sadocinteltest.queue.core.windows.net/'
                cosmosEndpoint = 'https://cosmos-docintel-test.documents.azure.com:443/'
                azureOpenAiEndpoint = 'https://aif-docintel-test.openai.azure.com/'
                contentUnderstandingEndpoint = 'https://aif-docintel-test.services.ai.azure.com/'
                foundryEndpoint = 'https://aif-docintel-test.services.ai.azure.com/models'
                translatorEndpoint = 'https://tr-docintel-test.cognitiveservices.azure.com/'
                translationStagingBlobEndpoint = 'https://stagingaccount.blob.core.windows.net/'
                identityKeyIdentifier = 'https://kv-di-test.vault.azure.net/keys/docintel-identity'
            }.GetEnumerator()) { $outputs[$pair.Key] = @{ value = $pair.Value } }
            @{ properties = @{ outputs = $outputs } }; break
        }
        'containerapp identity assign --subscription' { @{}; break }
        'containerapp registry set --subscription' { @{}; break }
        'containerapp update --subscription *' {
            $index = [Array]::IndexOf($Arguments, '--set-env-vars')
            foreach ($argument in $Arguments[($index + 1)..($Arguments.Length - 1)]) {
                if ($argument -eq '--only-show-errors') { break }
                $name, $value = $argument -split '=', 2
                $global:DocIntelSetupFixture.app.properties.template.containers[0].env = @(
                    $global:DocIntelSetupFixture.app.properties.template.containers[0].env | Where-Object { $_.name -cne $name }
                ) + @(@{ name = $name; value = $value })
            }
            $lostSetting = switch ($global:DocIntelSetupFixture.scenario) {
                'managed-setting-loss' { 'Identity__ClientId' }
                'unrelated-setting-loss' { 'UserOwnedSecret' }
                default { '' }
            }
            if ($lostSetting) {
                $global:DocIntelSetupFixture.app.properties.template.containers[0].env = @(
                    $global:DocIntelSetupFixture.app.properties.template.containers[0].env |
                        Where-Object { $_.name -cne $lostSetting }
                )
            }
            @{}; break
        }
        default { throw "Unexpected Azure operation in offline setup tests: $key" }
    }
    ConvertTo-Json -InputObject $result -Depth 100 -Compress
}

function Reset-Calls { $global:DocIntelSetupFixture.calls.Clear(); $global:DocIntelSetupFixture.mutations.Clear(); $global:DocIntelSetupFixture.scenario = '' }
function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) { throw "$Message Expected '$Expected', actual '$Actual'." }
}
function Assert-Failure([hashtable]$Arguments, [string]$Message) {
    $failed = $false
    try { & $scriptPath @Arguments | Out-Null }
    catch { $failed = $true; if ($_.Exception.Message -notlike "*$Message*") { throw } }
    if (-not $failed) { throw "Expected setup to reject $Message." }
}
$base = @{
    SubscriptionId = $global:DocIntelSetupFixture.subscription; DirectoryTenantId = $global:DocIntelSetupFixture.directory
    RegistryName = 'testregistry'; AdminObjectId = $global:DocIntelSetupFixture.adminId
    OutputDirectory = Join-Path $temporary 'plan'
}
try {
    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count) { throw ($parseErrors | ForEach-Object Message | Out-String) }

    $invalid = $base.Clone(); $invalid.SubscriptionId = 'invalid'
    Assert-Failure $invalid 'SubscriptionId'
    $invalid = $base.Clone(); $invalid.Mode = 'Apply'
    Assert-Failure $invalid 'Confirmation=APPLY'
    $invalid.Confirmation = 'APPLY'
    Assert-Failure $invalid 'plan hash'
    $invalid.Mode = 'Preview'; $invalid.ExpectedPlanHash = 'invalid'
    Assert-Failure $invalid 'ExpectedPlanHash'
    Assert-Equal 0 $global:DocIntelSetupFixture.calls.Count 'Invalid input must be rejected before Azure calls.'

    & $scriptPath @base | Out-Null
    Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'Preview must never mutate Azure or Entra.'
    Assert-Equal $global:DocIntelSetupFixture.environmentId $global:DocIntelSetupFixture.parameters.parameters.existingEnvironmentId.value 'Existing app environment must be reused.'
    Assert-Equal $false $global:DocIntelSetupFixture.parameters.parameters.deployApplication.value 'Infrastructure must not replace the app.'
    foreach ($flag in 'reuseRegistry', 'reuseRuntimeIdentity', 'reuseStorageAccount',
        'reuseCosmosAccount', 'reuseKeyVault', 'reuseIdentityKey', 'reuseFoundryAccount') {
        Assert-Equal $true $global:DocIntelSetupFixture.parameters.parameters.$flag.value "Existing resources must be adopted: $flag."
    }
    Assert-Equal $false $global:DocIntelSetupFixture.parameters.parameters.deployModels.value 'An existing model must not be reset.'
    Assert-Equal $false $global:DocIntelSetupFixture.parameters.parameters.configureTranslation.value 'A reconciled Translator must be preserved independently of Foundry.'
    Assert-Equal $true $global:DocIntelSetupFixture.parameters.parameters.reuseCosmosDatabase.value 'An existing database must retain its configuration.'
    Assert-Equal 'harnesses' $global:DocIntelSetupFixture.parameters.parameters.existingCosmosContainers.value[0] 'Existing container settings must be preserved.'
    $plan = Get-Content (Join-Path $base.OutputDirectory 'setup-plan.json') -Raw | ConvertFrom-Json
    if (($plan | ConvertTo-Json -Depth 100) -match [regex]::Escape($global:DocIntelSetupFixture.adminId)) {
        throw 'The shareable setup plan must not expose the bootstrap administrator identity.'
    }
    Assert-Equal 4 @($global:DocIntelSetupFixture.parameters.parameters.existingQueues.value).Count 'Existing queues must be adopted through the management API.'
    if (@($global:DocIntelSetupFixture.calls | Where-Object {
        ($_ -contains 'queue-rm') -or ($_ -contains '--account-key') -or
        ($_ -join ' ') -match 'storage queue list|storage account keys'
    }).Count) { throw 'Queue discovery must not use unsupported CLI groups, storage keys or data-plane listing.' }
    $apply = $base.Clone(); $apply.Mode = 'Apply'; $apply.Confirmation = 'APPLY'; $apply.ExpectedPlanHash = $plan.planHash
    Reset-Calls
    & $scriptPath @apply | Out-Null
    Assert-Equal 4 $global:DocIntelSetupFixture.mutations.Count 'Apply must provision infra and merge identity/registry/runtime settings only.'
    $update = $global:DocIntelSetupFixture.mutations | Where-Object { $_[0] -eq 'containerapp' -and $_[1] -eq 'update' } | Select-Object -Last 1
    if (-not ($update -contains '--set-env-vars') -or $update -contains '--replace-env-vars' -or $update -contains '--image') {
        throw 'Setup must merge runtime settings and preserve the existing image.'
    }
    if (@($update | Where-Object { $_ -like 'Channels__Email__Enabled=*' }).Count) {
        throw 'Existing channel configuration must not be disabled.'
    }
    if (-not ($update -contains "Identity__PublicOrigin=$($global:DocIntelSetupFixture.origin)") -or
        -not ($update -contains "Identity__ClientId=$($global:DocIntelSetupFixture.webId)")) { throw 'Runtime identity/origin outputs were not wired.' }

    Reset-Calls
    & $scriptPath @base | Out-Null
    $currentPlan = Get-Content (Join-Path $base.OutputDirectory 'setup-plan.json') -Raw | ConvertFrom-Json
    $apply.ExpectedPlanHash = $currentPlan.planHash
    $reviewedPreview = $base.Clone(); $reviewedPreview.ExpectedPlanHash = $currentPlan.planHash
    & $scriptPath @reviewedPreview | Out-Null
    Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'An unchanged reviewed plan must remain a read-only preview.'
    $global:DocIntelSetupFixture.scenario = 'drift'
    Assert-Failure $reviewedPreview 'changed since Preview'
    Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'A different plan from the reviewed run must block the Apply workflow before writes.'

    foreach ($scenario in 'delete', 'replace-app', 'drift') {
        Reset-Calls; $global:DocIntelSetupFixture.scenario = $scenario
        $message = switch ($scenario) { 'delete' { 'includes deletion' } 'replace-app' { 'must not replace' } default { 'changed since Preview' } }
        Assert-Failure $apply $message
        Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'Invalid/changed preview must block every mutation.'
    }
    Reset-Calls; $global:DocIntelSetupFixture.scenario = 'paged-queues'
    & $scriptPath @base | Out-Null
    $discoveredQueues = @($global:DocIntelSetupFixture.parameters.parameters.existingQueues.value)
    Assert-Equal 4 $discoveredQueues.Count 'All queue pages must be read before determining which queues already exist.'
    if ('document-jobs' -notin $discoveredQueues -or 'document-jobs-poison' -notin $discoveredQueues) {
        throw 'Later-page queues must not be treated as missing and redeployed.'
    }
    Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'Paged queue discovery must remain read-only.'
    Reset-Calls; $global:DocIntelSetupFixture.scenario = 'empty-queues'
    & $scriptPath @base | Out-Null
    Assert-Equal 0 @($global:DocIntelSetupFixture.parameters.parameters.existingQueues.value).Count 'An explicitly empty inventory must allow missing queues to be planned.'
    Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'An empty queue inventory must not create queues during Preview.'
    foreach ($case in @(
        @{ scenario = 'queue-read-failure'; message = 'rest --method GET' },
        @{ scenario = 'queue-page-failure'; message = 'rest --method GET' },
        @{ scenario = 'invalid-queue-response'; message = 'valid value array' },
        @{ scenario = 'invalid-queue-name'; message = 'invalid queue name' },
        @{ scenario = 'foreign-queue-nextlink'; message = 'outside the selected account' },
        @{ scenario = 'wrong-account-nextlink'; message = 'outside the selected account' },
        @{ scenario = 'repeated-queue-nextlink'; message = 'repeated continuation URL' }
    )) {
        Reset-Calls; $global:DocIntelSetupFixture.scenario = $case.scenario
        Assert-Failure $base $case.message
        Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'A failed or unsafe queue inventory must not mutate resources.'
        $previews = @($global:DocIntelSetupFixture.calls | Where-Object { $_[0..2] -join ' ' -eq 'deployment group what-if' })
        Assert-Equal 0 $previews.Count 'An incomplete queue inventory must not be used for an infrastructure plan.'
    }
    Reset-Calls; $global:DocIntelSetupFixture.scenario = 'wrong-partition'
    Assert-Failure $base 'different partition key'
    Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'Partition mismatch must not replace Cosmos containers.'
    Reset-Calls
    $invalid = $base.Clone(); $invalid.StorageAccountName = 'differentaccount'
    Assert-Failure $invalid 'Refusing to move'
    Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'Resource repointing must not migrate data.'
    Reset-Calls
    $withoutAdmin = $base.Clone(); $withoutAdmin.AdminObjectId = ''
    $withGrants = $global:DocIntelSetupFixture.app.properties.template.containers[0].env
    $global:DocIntelSetupFixture.app.properties.template.containers[0].env = @(
        $withGrants | Where-Object { $_.name -ne 'DOCINTEL_IDENTITY_GRANTS' }
    )
    & $scriptPath @withoutAdmin | Out-Null
    $incomplete = Get-Content (Join-Path $base.OutputDirectory 'setup-plan.json') -Raw | ConvertFrom-Json
    Assert-Equal $true $incomplete.bootstrapAdminRequired 'Preview must explain the missing bootstrap input.'
    $withoutAdmin.Mode = 'Apply'; $withoutAdmin.Confirmation = 'APPLY'
    $withoutAdmin.ExpectedPlanHash = $incomplete.planHash
    Assert-Failure $withoutAdmin 'DOCINTEL_ADMIN_OBJECT_ID'
    Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'Missing administrator must fail before provisioning.'
    $global:DocIntelSetupFixture.app.properties.template.containers[0].env = $withGrants

    Reset-Calls
    $managedDirectory = $base.Clone(); $managedDirectory.ConfigureDirectoryApplications = $true
    & $scriptPath @managedDirectory | Out-Null
    Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'Directory preview must not change registrations.'
    $directoryPlan = Get-Content (Join-Path $base.OutputDirectory 'setup-plan.json') -Raw | ConvertFrom-Json
    Assert-Equal 2 $directoryPlan.missingServicePrincipals 'Preview must report missing enterprise applications independently of registrations.'
    $managedDirectory.Mode = 'Apply'; $managedDirectory.Confirmation = 'APPLY'
    $managedDirectory.ExpectedPlanHash = $directoryPlan.planHash
    Reset-Calls
    & $scriptPath @managedDirectory | Out-Null
    $scopeValues = @($global:DocIntelSetupFixture.apiPatch.api.oauth2PermissionScopes | ForEach-Object value)
    if ('Existing.Scope' -notin $scopeValues -or 'DocIntel.Access' -notin $scopeValues) {
        throw 'Directory setup must preserve existing scopes and add the delegated scope.'
    }
    if ($global:DocIntelSetupFixture.apiPatch.appRoles) { throw 'Directory setup must not create the conflicting app role.' }
    Assert-Equal 2 $global:DocIntelSetupFixture.principals.Count 'Directory retries must create missing service principals even for existing registrations.'
    $redirectUpdate = $global:DocIntelSetupFixture.mutations | Where-Object { $_[0..2] -join ' ' -eq 'ad app update' } | Select-Object -First 1
    if (-not ($redirectUpdate -contains 'https://other.example/signin-oidc') -or
        -not ($redirectUpdate -contains ($global:DocIntelSetupFixture.origin + '/signin-oidc'))) {
        throw 'Runtime redirect setup must preserve existing redirect URIs.'
    }
    Reset-Calls
    $directoryRetry = $managedDirectory.Clone(); $directoryRetry.Mode = 'Preview'
    $directoryRetry.ExpectedPlanHash = ''
    & $scriptPath @directoryRetry | Out-Null
    $retryPlan = Get-Content (Join-Path $base.OutputDirectory 'setup-plan.json') -Raw | ConvertFrom-Json
    Assert-Equal 0 $retryPlan.missingServicePrincipals 'An existing enterprise application must be reused.'
    $directoryRetry.Mode = 'Apply'; $directoryRetry.ExpectedPlanHash = $retryPlan.planHash
    & $scriptPath @directoryRetry | Out-Null
    $principalCreates = @($global:DocIntelSetupFixture.mutations | Where-Object { $_[0..2] -join ' ' -eq 'ad sp create' })
    Assert-Equal 0 $principalCreates.Count 'An authorized retry must not duplicate existing service principals.'
    Reset-Calls; $global:DocIntelSetupFixture.scenario = 'role-collision'
    Assert-Failure $managedDirectory 'app role conflicts'
    Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'An invalid directory setup must fail before resource provisioning.'
    Reset-Calls; $global:DocIntelSetupFixture.scenario = 'disabled-scope'
    Assert-Failure $managedDirectory 'DocIntel.Access is disabled'
    Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'A deliberately disabled API permission must fail before resource provisioning.'

    Reset-Calls
    $originalResources = $global:DocIntelSetupFixture.resourceList
    $originalEnvironment = $global:DocIntelSetupFixture.app.properties.template.containers[0].env
    try {
        $global:DocIntelSetupFixture.resourceList = @($originalResources | Where-Object {
            $_.type -in @('Microsoft.ContainerRegistry/registries', 'Microsoft.ManagedIdentity/userAssignedIdentities')
        })
        $global:DocIntelSetupFixture.app.properties.template.containers[0].env = @($originalEnvironment | Where-Object {
            $_.name -notlike 'AzureAI__*' -and
                $_.name -cne 'DocumentTranslation__StagingBlobEndpoint' -and
                $_.name -notin @('Identity__DataProtectionKeyIdentifier', 'Identity__DataProtectionBlobUri')
        })
        & $scriptPath @base | Out-Null
        foreach ($flag in 'reuseStorageAccount', 'reuseCosmosAccount', 'reuseKeyVault', 'reuseIdentityKey', 'reuseFoundryAccount') {
            Assert-Equal $false $global:DocIntelSetupFixture.parameters.parameters.$flag.value "Missing resource must be planned for creation: $flag."
        }
        Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'Fresh infrastructure preview must still be read-only.'
    }
    finally {
        $global:DocIntelSetupFixture.resourceList = $originalResources
        $global:DocIntelSetupFixture.app.properties.template.containers[0].env = $originalEnvironment
    }
    foreach ($case in @(
        @{ scenario = 'managed-setting-loss'; message = "Runtime setting 'Identity__ClientId' was not persisted" },
        @{ scenario = 'unrelated-setting-loss'; message = "An unrelated runtime setting 'UserOwnedSecret' changed" }
    )) {
        Reset-Calls
        $baselineEnvironment = $global:DocIntelSetupFixture.app.properties.template.containers[0].env
        try {
            & $scriptPath @base | Out-Null
            $readbackPlan = Get-Content (Join-Path $base.OutputDirectory 'setup-plan.json') -Raw | ConvertFrom-Json
            $readbackApply = $base.Clone(); $readbackApply.Mode = 'Apply'; $readbackApply.Confirmation = 'APPLY'
            $readbackApply.ExpectedPlanHash = $readbackPlan.planHash
            $global:DocIntelSetupFixture.scenario = $case.scenario
            Assert-Failure $readbackApply $case.message
        }
        finally { $global:DocIntelSetupFixture.app.properties.template.containers[0].env = $baselineEnvironment }
    }
    Reset-Calls
    $global:DocIntelSetupFixture.scenario = 'unsupported-translator'
    Assert-Failure $base 'AIServices is insufficient'
    Assert-Equal 0 $global:DocIntelSetupFixture.mutations.Count 'An unsupported PDF binding must require scoped reconciliation, not silent retention.'
    Write-Output 'GitHub setup discovery, preview-only safety, adoption, plan fencing and runtime merge tests passed without Azure calls.'
}
finally {
    Remove-Item Function:\az
    Remove-Variable -Name DocIntelSetupFixture -Scope Global
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse }
}
