#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$scriptPath = Join-Path $root 'scripts\Install-DocIntelCustomer.ps1'
$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors) | Out-Null
if ($parseErrors.Count) { throw ($parseErrors | ForEach-Object Message | Out-String) }
$compiled = & az bicep build --file (Join-Path $root 'infra\customer.bicep') --stdout --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Customer Bicep compilation failed.' }
$template = ($compiled -join "`n") | ConvertFrom-Json -Depth 100
$module = @($template.resources | Where-Object type -CEQ 'Microsoft.Resources/deployments')[0]
if ($module.properties.parameters.enableContainerRegistry.value -ne $false -or
    $module.properties.parameters.reuseIdentityKey.value -cne "[parameters('infrastructureReady')]" -or
    $module.properties.parameters.deployRuntimeRoles.value -cne "[not(parameters('infrastructureReady'))]" -or
    $module.properties.parameters.deployModels.value -cne "[not(parameters('infrastructureReady'))]") {
    throw 'The customer second phase must preserve data/key/model roots, and public images must not create ACR.'
}
foreach ($role in $module.properties.template.resources | Where-Object {
    $_.type -in @('Microsoft.Authorization/roleAssignments', 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments')
}) {
    if (-not $role.condition.Contains("parameters('deployRuntimeRoles')")) {
        throw 'The second deployment must omit RBAC resources rather than misinterpret unevaluated what-if reference deltas.'
    }
}
foreach ($mapping in @(
    @('existingBlobContainers', 'Microsoft.Storage/storageAccounts/blobServices/containers'),
    @('existingQueues', 'Microsoft.Storage/storageAccounts/queueServices/queues'),
    @('existingCosmosContainers', 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers')
)) {
    $children = @($module.properties.template.resources | Where-Object type -CEQ $mapping[1])
    foreach ($child in $children) {
        $name = [regex]::Match($child.condition, "'([^']+)'\)\)\]$").Groups[1].Value
        if (-not $name -or -not ([string]$module.properties.parameters.($mapping[0])).Contains("'$name'")) {
            throw 'The application phase must preserve every provisioned data child.'
        }
    }
}
foreach ($slug in @('abc', 'abcdefghijklmnop', 'abc-defghijklmno')) {
    foreach ($environment in @('qa', 'longname')) {
        $stem = $slug.Replace('-', '').Substring(0, [Math]::Min(8, $slug.Replace('-', '').Length))
        $suffix = 'abcdefghijklm'
        $appFormat = [regex]::Match($template.variables.appName, "format\('([^']+)'").Groups[1].Value
        $appName = [string]::Format($appFormat, $stem, $environment, $suffix.Substring(0, 6))
        if ($appName.Length -gt 32 -or $appName -notmatch '^[a-z][a-z0-9-]+[a-z0-9]$') {
            throw 'A permitted boundary input generated an invalid Container App name.'
        }
        foreach ($entry in @(
            @{ name = 'storageAccountName'; max = 24; args = @($suffix) },
            @{ name = 'cosmosAccountNameOverride'; max = 44; args = @($stem, $environment, $suffix) },
            @{ name = 'keyVaultNameOverride'; max = 24; args = @($stem.Substring(0, [Math]::Min(4, $stem.Length)), $suffix) },
            @{ name = 'foundryAccountNameOverride'; max = 64; args = @($stem, $environment, $suffix) }
        )) {
            $format = [regex]::Match($module.properties.parameters.($entry.name).value, "format\('([^']+)'").Groups[1].Value
            $name = [string]::Format($format, [object[]]$entry.args)
            if ($name.Length -gt $entry.max -or -not $name.EndsWith($suffix, [StringComparison]::Ordinal)) {
                throw 'Global resource names must fit their limits and retain the complete uniqueness suffix.'
            }
        }
    }
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('docintel-customer-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$global:DocIntelCustomerFixture = @{}
$f = $global:DocIntelCustomerFixture
foreach ($name in @('subscription', 'directory', 'owner', 'webId', 'apiId', 'webObject', 'apiObject', 'principal')) {
    $f[$name] = [guid]::NewGuid().ToString()
}
$f.image = 'ghcr.io/nsethi16/docintel-api@sha256:' + ('a' * 64)
$f.groupId = "/subscriptions/$($f.subscription)/resourceGroups/rg-docintel-acme-prod"
$f.calls = [Collections.Generic.List[object]]::new()
$f.writes = [Collections.Generic.List[object]]::new()

function Reset-CustomerFixture {
    $global:DocIntelCustomerFixture.calls.Clear()
    $global:DocIntelCustomerFixture.writes.Clear()
    $global:DocIntelCustomerFixture.scenario = ''
    $global:DocIntelCustomerFixture.web = $null
    $global:DocIntelCustomerFixture.api = $null
    $global:DocIntelCustomerFixture.federations = @()
    $global:DocIntelCustomerFixture.deployments = 0
}

function az {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $f = $global:DocIntelCustomerFixture
    $f.calls.Add($Arguments)
    $global:LASTEXITCODE = 0
    $key = ($Arguments | Select-Object -First 3) -join ' '
    if ('--subscription' -notin $Arguments -or $Arguments[[Array]::IndexOf($Arguments, '--subscription') + 1] -cne $f.subscription) {
        throw 'Every Azure and Graph operation must be scoped to the customer subscription; never change CLI defaults.'
    }
    $result = switch -Wildcard ($key) {
        'account show *' { @{ id = $f.subscription; tenantId = $f.directory }; break }
        'account get-access-token --resource-type' {
            if ($Arguments[[Array]::IndexOf($Arguments, '--query') + 1] -cne 'tenant') { throw 'Never emit a Graph credential.' }
            if ($f.scenario -eq 'wrong-directory') { [guid]::Empty.ToString() } else { $f.directory }; break
        }
        'provider show --namespace' {
            if ($f.scenario -eq 'azure-error') {
                $global:LASTEXITCODE = 1
                'ERROR: (AuthorizationFailed) do-not-log-private-fixture-payload'
            } elseif ($f.scenario -eq 'provider') { 'NotRegistered' } else { 'Registered' }; break
        }
        'cognitiveservices model list' {
            @(@{ kind = 'AIServices'; model = @{
                format = 'OpenAI'; name = 'gpt-5-mini'
                version = $(if ($f.scenario -eq 'model') { 'wrong' } else { '2025-08-07' })
                skus = @(@{ name = 'GlobalStandard'; usageName = 'OpenAI.GlobalStandard.gpt-5-mini' })
            } }); break
        }
        'cognitiveservices usage list' {
            @(@{ name = @{ value = 'OpenAI.GlobalStandard.gpt-5-mini' }
                currentValue = 0; limit = $(if ($f.scenario -eq 'quota') { 9 } else { 100 }) }); break
        }
        'group exists --name' { $f.scenario -eq 'occupied'; break }
        'group show --name' { @{ location = 'westus'; tags = @{} }; break }
        'resource list --resource-group' { @(@{ name = 'unrelated-existing-data' }); break }
        'rest --method *' {
            $method = $Arguments[[Array]::IndexOf($Arguments, '--method') + 1]
            $url = $Arguments[[Array]::IndexOf($Arguments, '--url') + 1]
            if (-not $url.StartsWith('https://graph.microsoft.com/v1.0/', [StringComparison]::Ordinal)) {
                throw 'Graph requests must stay on the fixed Microsoft Graph origin.'
            }
            $path = [uri]::UnescapeDataString($url.Substring('https://graph.microsoft.com/v1.0/'.Length))
            if ($method -ne 'GET') {
                $f.writes.Add($Arguments)
                $body = Get-Content -LiteralPath $Arguments[[Array]::IndexOf($Arguments, '--body') + 1].TrimStart('@') -Raw |
                    ConvertFrom-Json -Depth 100
            }
            switch -Wildcard ("$method $path") {
                'GET me?*' { @{ id = $f.owner }; break }
                'GET users/*' { @{ id = $f.owner; accountEnabled = $f.scenario -ne 'disabled-owner' }; break }
                'GET applications[?]*' {
                    if ($f.scenario -eq 'ambiguous') { @{ value = @(@{ id = 'one' }, @{ id = 'two' }) }; break }
                    if ($f.scenario -eq 'unowned') {
                        @{ value = @(@{ id = $f.webObject; appId = $f.webId; signInAudience = 'AzureADMyOrg'; tags = @() }) }; break
                    }
                    @{ value = @() }; break
                }
                'POST applications' {
                    $isWeb = $body.displayName.Contains(' Web ')
                    $app = @{ id = $(if ($isWeb) { $f.webObject } else { $f.apiObject })
                        appId = $(if ($isWeb) { $f.webId } else { $f.apiId })
                        displayName = $body.displayName; signInAudience = $body.signInAudience; tags = $body.tags
                        web = @{ redirectUris = @('https://preserve.example/signin-oidc') }
                        api = @{ oauth2PermissionScopes = @(); preAuthorizedApplications = @() }; identifierUris = @() }
                    if ($isWeb) { $f.web = $app } else { $f.api = $app }
                    $app; break
                }
                'GET servicePrincipals[?]*' { @{ value = @() }; break }
                'POST servicePrincipals' { @{ id = [guid]::NewGuid().ToString() }; break }
                'PATCH applications/*' {
                    $app = if ($path -eq "applications/$($f.webObject)") { $f.web } else { $f.api }
                    if ($f.scenario -ne 'lost-api-patch') {
                        foreach ($property in $body.PSObject.Properties) { $app[$property.Name] = $property.Value }
                    }
                    @{}; break
                }
                'GET applications/*/federatedIdentityCredentials' { @{ value = @($f.federations) }; break }
                'POST applications/*/federatedIdentityCredentials' { $f.federations += $body; $body; break }
                'GET applications/*' {
                    if ($path.StartsWith("applications/$($f.webObject)?", [StringComparison]::Ordinal)) {
                        $f.web
                    } else { $f.api }; break
                }
                default { throw "Unexpected Graph operation: $method $path" }
            }
            break
        }
        'deployment sub what-if' {
            $parameters = Get-Content -LiteralPath $Arguments[[Array]::IndexOf($Arguments, '--parameters') + 1].TrimStart('@') -Raw |
                ConvertFrom-Json -Depth 100
            $changes = @()
            if (-not $parameters.parameters.infrastructureReady.value) {
                $changes += @{ resourceId = $f.groupId; changeType = 'Create' }
            }
            $change = @{ resourceId = "$($f.groupId)/providers/Microsoft.App/containerApps/di-acme-prod-fixture"; changeType = 'Create' }
            switch ($f.scenario) {
                'delete' { $change.changeType = 'Delete' }
                'outside' { $change.resourceId = $change.resourceId.Replace('rg-docintel-acme-prod', 'another-customer') }
                'drift' { $change.delta = @(@{ path = 'properties.changed'; after = 'unexpected' }) }
                'shared-modify' {
                    if ($parameters.parameters.infrastructureReady.value) {
                        $change.resourceId = "$($f.groupId)/providers/Microsoft.Storage/storageAccounts/shared"
                        $change.changeType = 'Modify'
                    }
                }
            }
            $changes += $change
            $analysis = @{ status = 'Succeeded'; changes = $changes }
            switch ($f.scenario) {
                'diagnostics' { $analysis.diagnostics = @(@{ message = 'Nested deployment short-circuited' }) }
                'potential' { $analysis.potentialChanges = @(@{ changeType = 'Modify' }) }
                'empty-plan' { $analysis.changes = @() }
                'ignored-nested' { $analysis.changes = @(@{
                    changeType = 'Ignore'; resourceId = "$($f.groupId)/providers/Microsoft.Resources/deployments/nested"
                }) }
            }
            if ($parameters.parameters.infrastructureReady.value) {
                $analysis.changes += @{ changeType = 'Ignore'
                    resourceId = "$($f.groupId)/providers/Microsoft.Storage/storageAccounts/shared" }
            }
            $analysis; break
        }
        'deployment sub create' {
            $f.writes.Add($Arguments)
            $f.deployments++
            $parameters = Get-Content -LiteralPath $Arguments[[Array]::IndexOf($Arguments, '--parameters') + 1].TrimStart('@') -Raw |
                ConvertFrom-Json -Depth 100
            if ($f.deployments -eq 1 -and $parameters.parameters.deployApplication.value) {
                throw 'Never start the application before runtime federation.'
            }
            if ($f.deployments -eq 2 -and (-not $parameters.parameters.infrastructureReady.value -or -not $f.federations.Count)) {
                throw 'Application deployment must preserve the shared infrastructure and follow federation.'
            }
            @{ properties = @{ provisioningState = 'Succeeded'; outputs = @{
                publicOrigin = @{ value = 'https://fixture.azurecontainerapps.io' }
                requiredWebRedirectUri = @{ value = 'https://fixture.azurecontainerapps.io/signin-oidc' }
                managedIdentityPrincipalId = @{ value = $f.principal }
                containerAppName = @{ value = 'di-acme-prod-fixture' }
            } } }; break
        }
        'containerapp show --resource-group' {
            $settings = @{
                AZURE_CLIENT_ID = 'runtime'; Identity__DirectoryTenantId = $f.directory
                Identity__ClientId = $f.webId; Identity__ApiClientId = $f.apiId
                Identity__PublicOrigin = 'https://fixture.azurecontainerapps.io'
                Identity__Mode = 'Entra'; Identity__UseManagedIdentityAssertion = 'true'
                Identity__DataProtectionBlobUri = 'https://storage.example/identity-keys/keyring.xml'
                Identity__DataProtectionKeyIdentifier = 'https://vault.example/keys/key'
                AzureAI__BlobEndpoint = 'https://storage.example'; AzureAI__QueueEndpoint = 'https://queue.example'
                AzureAI__CosmosEndpoint = 'https://cosmos.example'; AzureAI__AzureOpenAIEndpoint = 'https://ai.example'
                AzureAI__ContentUnderstandingEndpoint = 'https://ai.example'; DOCINTEL_IDENTITY_GRANTS = '{"PlatformAdministrators":[]}'
            }
            @{ properties = @{ latestRevisionName = 'new'; latestReadyRevisionName = 'new'
                configuration = @{ activeRevisionsMode = 'Single'; ingress = @{ fqdn = 'fixture.azurecontainerapps.io'; transport = 'auto' } }
                template = @{ containers = @(@{ name = 'docintel-api'
                    image = $(if ($f.scenario -eq 'wrong-image') { 'wrong' } else { $f.image })
                    env = @($settings.GetEnumerator() | ForEach-Object { @{ name = $_.Key; value = $_.Value } })
                }) }
            } }; break
        }
        default { throw "Unexpected Azure operation: $key" }
    }
    $result | ConvertTo-Json -Depth 100 -Compress
}

function Invoke-WebRequest {
    param([string]$Uri, [string]$Method = 'Get', [int]$MaximumRedirection, [int]$TimeoutSec,
        [switch]$SkipHttpErrorCheck, [hashtable]$Headers)
    $f = $global:DocIntelCustomerFixture
    if ($Uri -ceq 'https://fixture.azurecontainerapps.io/health/ready') { return @{ StatusCode = 200 } }
    if ($MaximumRedirection -ne 0 -or $TimeoutSec -ne 30 -or -not $SkipHttpErrorCheck) {
        throw 'Registry verification must be bounded and must not follow redirects with credentials.'
    }
    if ($Uri -ceq 'https://ghcr.io/token?service=ghcr.io&scope=repository:nsethi16/docintel-api:pull') {
        return @{ StatusCode = 200; Content = '{"token":"public-anonymous-fixture"}' }
    }
    if ($Uri -ceq ('https://ghcr.io/v2/nsethi16/docintel-api/manifests/sha256:' + ('a' * 64)) -and
        $Method -ceq 'Head' -and $Headers.Authorization -ceq 'Bearer public-anonymous-fixture') {
        return @{ StatusCode = $(if ($f.scenario -eq 'private-image') { 403 } else { 200 })
            Headers = @{ 'Docker-Content-Digest' = 'sha256:' + ('a' * 64) } }
    }
    throw 'Unexpected HTTP request; customer setup must not access arbitrary origins.'
}

function Assert-CustomerFailure([hashtable]$Arguments, [string]$Message, [switch]$AllowWrites) {
    $writes = $global:DocIntelCustomerFixture.writes.Count
    $failed = $false
    try { & $scriptPath @Arguments | Out-Null }
    catch {
        $failed = $true
        if ($_.Exception.Message.Contains('do-not-log-private-fixture-payload')) {
            throw 'Azure failure reporting must not echo raw provider output.'
        }
        if ($_.Exception.Message -notlike "*$Message*") { throw }
    }
    if (-not $failed) { throw "Expected setup failure: $Message." }
    if (-not $AllowWrites -and $writes -ne $global:DocIntelCustomerFixture.writes.Count) {
        throw 'A rejected plan/input must not mutate Azure or Entra.'
    }
}

try {
    $arguments = @{ CustomerSlug = 'acme'; Image = $f.image; SubscriptionId = $f.subscription; OutputDirectory = $temporary }
    Reset-CustomerFixture
    foreach ($invalid in @(
        @{ CustomerSlug = 'ACME'; message = 'CustomerSlug' },
        @{ CustomerSlug = 'ab--cd'; message = 'CustomerSlug' },
        @{ Image = 'ghcr.io/nsethi16/docintel-api:latest'; message = 'SHA-256' },
        @{ Location = 'westus2'; message = 'Content Understanding' }
    )) {
        $bad = $arguments.Clone()
        foreach ($entry in $invalid.GetEnumerator()) { if ($entry.Key -ne 'message') { $bad[$entry.Key] = $entry.Value } }
        Assert-CustomerFailure $bad $invalid.message
        if ($f.calls.Count) { throw 'Invalid inputs must fail before Azure is called.' }
    }
    foreach ($scenario in @(
        @('wrong-directory', 'same directory'), @('disabled-owner', 'enabled user'), @('provider', 'register'),
        @('azure-error', 'AuthorizationFailed'), @('model', 'unavailable'), @('quota', 'quota'),
        @('private-image', 'anonymously pullable'),
        @('occupied', 'not empty'), @('ambiguous', 'Multiple matching'), @('unowned', 'not owned'),
        @('delete', 'deletion'), @('outside', 'outside'), @('diagnostics', 'incomplete'),
        @('potential', 'incomplete'), @('empty-plan', 'incomplete'), @('ignored-nested', 'ignored')
    )) {
        Reset-CustomerFixture
        $f.scenario = $scenario[0]
        Assert-CustomerFailure $arguments $scenario[1]
    }
    Reset-CustomerFixture
    & $scriptPath @arguments | Out-Null
    if ($f.writes.Count) { throw 'Preview mutated Azure/Entra.' }
    $plan = Get-Content -LiteralPath (Join-Path $temporary 'customer-plan.json') -Raw | ConvertFrom-Json -Depth 100
    $apply = $arguments.Clone()
    $apply.Mode = 'Apply'; $apply.Confirmation = 'APPLY'; $apply.ExpectedPlanHash = $plan.PlanHash
    $f.scenario = 'drift'
    Assert-CustomerFailure $apply 'plan changed'
    Reset-CustomerFixture
    & $scriptPath @apply | Out-Null
    $receipt = Get-Content -LiteralPath (Join-Path $temporary 'customer-result.json') -Raw | ConvertFrom-Json
    if ($receipt.Status -cne 'InfrastructureReady' -or $f.deployments -ne 2 -or
        $f.federations[0].subject -cne $f.principal -or
        $f.federations[0].issuer -cne "https://login.microsoftonline.com/$($f.directory)/v2.0" -or
        'api://AzureADTokenExchange' -cnotin $f.federations[0].audiences -or
        'https://preserve.example/signin-oidc' -cnotin $f.web.web.redirectUris -or
        "api://$($f.apiId)" -cnotin $f.api.identifierUris -or
        $f.api.api.preAuthorizedApplications[0].appId -cne $f.webId) {
        throw 'A successful fresh install must wire the exact runtime identity, preserve redirects, and preauthorize the separate API.'
    }
    foreach ($scenario in @(
        @('lost-api-patch', 'preauthorization readback'), @('shared-modify', 'must not modify'),
        @('wrong-image', 'does not match')
    )) {
        Reset-CustomerFixture
        $f.scenario = $scenario[0]
        Assert-CustomerFailure $apply $scenario[1] -AllowWrites
        $receipt = Get-Content -LiteralPath (Join-Path $temporary 'customer-result.json') -Raw | ConvertFrom-Json
        if ($receipt.Status -cne 'Incomplete') { throw 'Failed apply must not leave a success-shaped receipt.' }
    }
    Write-Output 'Customer installer execution and infrastructure contract tests passed.'
}
finally {
    Remove-Item Function:\az, Function:\Invoke-WebRequest -ErrorAction SilentlyContinue
    Remove-Variable DocIntelCustomerFixture -Scope Global
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
