#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
$file = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Wait-DocIntelReady.ps1'
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
}
function az {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    if (($Arguments | Select-Object -First 2) -join ' ' -ne 'containerapp show') {
        throw 'Readiness must never mutate Azure.'
    }
    $global:DocIntelReadinessFixture.calls++
    $global:LASTEXITCODE = 0
    @{
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
        AzureAI__BlobEndpoint = 'https://storage.example'; AzureAI__QueueEndpoint = 'https://queue.example'
        AzureAI__CosmosEndpoint = 'https://cosmos.example'; AzureAI__AzureOpenAIEndpoint = 'https://ai.example'
        AzureAI__ContentUnderstandingEndpoint = 'https://layout.example'
        DOCINTEL_IDENTITY_GRANTS = '{"Memberships":[{"ObjectId":"fixture"}]}'
    }.GetEnumerator() | ForEach-Object { @{ name = $_.Key; value = $_.Value } })
    & $file -ConfigurationOnly -ResourceGroup fixture -ContainerAppName fixture | Out-Null
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
