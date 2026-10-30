#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
$file = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Wait-DocIntelReady.ps1'
Import-Module (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\DocIntelRelease.psm1') -Force
$global:DocIntelReadinessFixture = @{
    image = 'fixture.azurecr.io/docintel:commit'
    revision = 'new-revision'
    latestReady = 'new-revision'
    calls = 0
    requests = 0
    status = 200
    mode = 'Single'
    traffic = @()
    environment = @()
    transport = 'auto'
    additionalPorts = @()
    policyReady = $true
    appId = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/fixture/providers/Microsoft.App/containerApps/fixture'
    runtimeId = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/fixture/providers/Microsoft.ManagedIdentity/userAssignedIdentities/runtime'
    translatorId = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/fixture/providers/Microsoft.CognitiveServices/accounts/translator'
}
function az {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    if (($Arguments | Select-Object -First 4) -join ' ' -ceq 'storage account management-policy show') {
        if ($Arguments -notcontains 'stagingaccount') { throw 'Readiness must never inspect the originals policy.' }
        $global:LASTEXITCODE = 0
        @{ policy = @{ rules = @($(if ($global:DocIntelReadinessFixture.policyReady) {
            Get-DocIntelTranslationLifecycleRule
        })) } } | ConvertTo-Json -Depth 30 -Compress
        return
    }
    if (($Arguments | Select-Object -First 3) -join ' ' -ceq 'cognitiveservices account list') {
        $global:LASTEXITCODE = 0
        @(@{ id = $global:DocIntelReadinessFixture.translatorId
            kind = 'TextTranslation'; sku = @{ name = 'S1' }; location = 'westus'
            properties = @{ customSubDomainName = 'translator' } }) | ConvertTo-Json -Depth 20 -Compress
        return
    }
    if (($Arguments | Select-Object -First 3) -join ' ' -ceq 'storage account show') {
        $global:LASTEXITCODE = 0
        @{ id = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/fixture/providers/Microsoft.Storage/storageAccounts/stagingaccount'
            name = 'stagingaccount'; kind = 'StorageV2'; sku = @{ name = 'Standard_LRS' }; location = 'westus'
            tags = Get-DocIntelTranslationStagingTags $global:DocIntelReadinessFixture.appId `
                $global:DocIntelReadinessFixture.runtimeId $global:DocIntelReadinessFixture.translatorId
            minimumTlsVersion = 'TLS1_2'; enableHttpsTrafficOnly = $true
            allowBlobPublicAccess = $false; allowSharedKeyAccess = $false } |
            ConvertTo-Json -Depth 20 -Compress
        return
    }
    if (($Arguments | Select-Object -First 2) -join ' ' -ne 'containerapp show') {
        throw 'Readiness must never mutate Azure.'
    }
    $global:DocIntelReadinessFixture.calls++
    $global:LASTEXITCODE = 0
    @{
        id = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/fixture/providers/Microsoft.App/containerApps/fixture'
        identity = @{ userAssignedIdentities = @{
            $global:DocIntelReadinessFixture.runtimeId = @{ clientId = 'runtime-client' }
        } }
        properties = @{
            latestRevisionName = $global:DocIntelReadinessFixture.revision
            latestReadyRevisionName = $global:DocIntelReadinessFixture.latestReady
            template = @{ containers = @(@{
                name = 'docintel-api'; image = $global:DocIntelReadinessFixture.image
                env = $global:DocIntelReadinessFixture.environment
            }) }
            configuration = @{
                activeRevisionsMode = $global:DocIntelReadinessFixture.mode
                ingress = @{
                    fqdn = 'fixture.azurecontainerapps.io'; traffic = $global:DocIntelReadinessFixture.traffic
                    transport = $global:DocIntelReadinessFixture.transport
                    additionalPortMappings = $global:DocIntelReadinessFixture.additionalPorts
                }
            }
        }
    } | ConvertTo-Json -Depth 20 -Compress
}
function Invoke-WebRequest {
    param([string]$Uri, [switch]$SkipHttpErrorCheck, [int]$TimeoutSec)
    if ($Uri -cne 'https://fixture.azurecontainerapps.io/health/ready' -or
        -not $SkipHttpErrorCheck -or $TimeoutSec -ne 10) {
        throw 'Readiness must use the bounded HTTPS server-origin check.'
    }
    $global:DocIntelReadinessFixture.requests++
    return @{ StatusCode = $global:DocIntelReadinessFixture.status }
}
function Assert-ReadinessFailure([string]$Message) {
    $failed = $false
    try {
        & $file -ResourceGroup fixture -ContainerAppName fixture -ExpectedImage 'fixture.azurecr.io/docintel:commit' `
            -Attempts 1 -PollSeconds 1 | Out-Null
    } catch {
        $failed = $true
        if ($_.Exception.Message -notlike "*$Message*") { throw }
    }
    if (-not $failed) { throw "Expected readiness to fail: $Message." }
}
try {
    & $file -ResourceGroup fixture -ContainerAppName fixture -ExpectedImage 'fixture.azurecr.io/docintel:commit' `
        -Attempts 1 -PollSeconds 1 | Out-Null
    if ($global:DocIntelReadinessFixture.requests -ne 1) { throw 'Ready release must check the actual endpoint.' }
    $global:DocIntelReadinessFixture.latestReady = 'old-revision'
    $global:DocIntelReadinessFixture.requests = 0
    Assert-ReadinessFailure 'did not become ready'
    if ($global:DocIntelReadinessFixture.requests) { throw 'An old healthy revision must not count as the new release.' }
    $global:DocIntelReadinessFixture.latestReady = 'new-revision'
    $global:DocIntelReadinessFixture.image = 'fixture.azurecr.io/docintel:other'
    Assert-ReadinessFailure 'does not match'
    $global:DocIntelReadinessFixture.image = 'fixture.azurecr.io/docintel:commit'
    $global:DocIntelReadinessFixture.status = 503
    Assert-ReadinessFailure 'did not become ready'
    $global:DocIntelReadinessFixture.status = 200
    $global:DocIntelReadinessFixture.mode = 'Multiple'
    $global:DocIntelReadinessFixture.traffic = @(@{ revisionName = 'old-revision'; weight = 100 })
    Assert-ReadinessFailure 'traffic is not assigned'
    $global:DocIntelReadinessFixture.traffic = @(@{ latestRevision = $true; weight = 100 })
    & $file -ResourceGroup fixture -ContainerAppName fixture -ExpectedImage 'fixture.azurecr.io/docintel:commit' `
        -Attempts 1 -PollSeconds 1 | Out-Null
    $failed = $false
    try { & $file -ConfigurationOnly -ResourceGroup fixture -ContainerAppName fixture | Out-Null }
    catch { $failed = $true; if ($_.Exception.Message -notlike '*Missing runtime settings*') { throw } }
    if (-not $failed) { throw 'Release must stop before publishing when infrastructure/runtime setup is incomplete.' }
    $global:DocIntelReadinessFixture.environment = @(@{
        AZURE_CLIENT_ID = 'runtime-client'; Identity__DirectoryTenantId = 'directory'
        Identity__ClientId = 'web'; Identity__ApiClientId = 'api'; Identity__PublicOrigin = 'https://app.example'
        Identity__Mode = 'Entra'; Identity__UseManagedIdentityAssertion = 'true'
        Identity__DataProtectionBlobUri = 'https://storage.example/keyring.xml'
        Identity__DataProtectionKeyIdentifier = 'https://vault.example/keys/key'
        AzureAI__BlobEndpoint = 'https://primaryaccount.blob.core.windows.net/'; AzureAI__QueueEndpoint = 'https://queue.example'
        DocumentTranslation__StagingBlobEndpoint = 'https://stagingaccount.blob.core.windows.net/'
        AzureAI__CosmosEndpoint = 'https://cosmos.example'; AzureAI__AzureOpenAIEndpoint = 'https://ai.example'
        AzureAI__ContentUnderstandingEndpoint = 'https://layout.example'
        AzureAI__TranslatorEndpoint = 'https://translator.cognitiveservices.azure.com/'
        AzureAI__DecisionEndpoint = 'https://decision.example'; AzureAI__DecisionDeploymentName = 'Decision-1'
        DOCINTEL_IDENTITY_GRANTS = '{"Memberships":[{"ObjectId":"fixture"}]}'
    }.GetEnumerator() | ForEach-Object { @{ name = $_.Key; value = $_.Value } })
    & $file -ConfigurationOnly -ResourceGroup fixture -ContainerAppName fixture | Out-Null
    $global:DocIntelReadinessFixture.policyReady = $false
    $failed = $false
    try { & $file -ConfigurationOnly -ResourceGroup fixture -ContainerAppName fixture | Out-Null }
    catch {
        if ($_.Exception.Message -notlike '*lifecycle expiry is not verified*') { throw }
        $failed = $true
    }
    if (-not $failed) { throw 'Readiness must require the exact deferred-cleanup policy.' }
    $global:DocIntelReadinessFixture.policyReady = $true
    $completeEnvironment = $global:DocIntelReadinessFixture.environment
    foreach ($case in 'missing-staging', 'originals-fallback') {
        $global:DocIntelReadinessFixture.environment = @($completeEnvironment |
            Where-Object name -CNE 'DocumentTranslation__StagingBlobEndpoint')
        if ($case -eq 'originals-fallback') {
            $global:DocIntelReadinessFixture.environment += @{
                name = 'DocumentTranslation__StagingBlobEndpoint'; value = 'https://primaryaccount.blob.core.windows.net/'
            }
        }
        $failed = $false
        try { & $file -ConfigurationOnly -ResourceGroup fixture -ContainerAppName fixture | Out-Null }
        catch {
            if ($_.Exception.Message -notmatch 'StagingBlobEndpoint|Original-artifact storage is forbidden') { throw }
            $failed = $true
        }
        if (-not $failed) { throw 'Readiness must never fall back to original-artifact storage.' }
    }
    $global:DocIntelReadinessFixture.environment = $completeEnvironment
    $global:DocIntelReadinessFixture.environment = @($completeEnvironment |
        Where-Object { $_.name -notlike 'AzureAI__Decision*' })
    & $file -ConfigurationOnly -BeforeDecisionSetup -ResourceGroup fixture -ContainerAppName fixture | Out-Null
    $failed = $false
    try { & $file -ConfigurationOnly -ResourceGroup fixture -ContainerAppName fixture | Out-Null }
    catch {
        if ($_.Exception.Message -notlike '*AzureAI__DecisionEndpoint*') { throw }
        $failed = $true
    }
    if (-not $failed) { throw 'Final preflight must still require the reconciled native connection.' }
    $global:DocIntelReadinessFixture.environment = $completeEnvironment
    $failed = $false
    try { & $file -BeforeDecisionSetup -ResourceGroup fixture -ContainerAppName fixture -ExpectedImage fixture | Out-Null }
    catch {
        if ($_.Exception.Message -notlike '*never release readiness*') { throw }
        $failed = $true
    }
    if (-not $failed) { throw 'Native preparation cannot bypass final release readiness.' }
    foreach ($scenario in 'tcp', 'additional-port') {
        $global:DocIntelReadinessFixture.transport = if ($scenario -eq 'tcp') { 'tcp' } else { 'auto' }
        $global:DocIntelReadinessFixture.additionalPorts = if ($scenario -eq 'additional-port') {
            @(@{ external = $true; targetPort = 8080; exposedPort = 8080 })
        } else { @() }
        $failed = $false
        try { & $file -ConfigurationOnly -ResourceGroup fixture -ContainerAppName fixture | Out-Null }
        catch { $failed = $true; if ($_.Exception.Message -notlike '*HTTP ingress without additional TCP ports*') { throw } }
        if (-not $failed) { throw 'Release must refuse TCP ingress bypasses of protocol-header verification.' }
    }
    Write-Output 'Release image, latest-revision, traffic and bounded health checks passed without Azure or network calls.'
}
finally {
    Remove-Item Function:\az
    Remove-Item Function:\Invoke-WebRequest
    Remove-Variable -Name DocIntelReadinessFixture -Scope Global
}
