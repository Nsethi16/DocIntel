#Requires -Version 7.3
[CmdletBinding()]
param(
    [string]$SubscriptionId = '',
    [string]$ResourceGroup,
    [string]$ContainerAppName,
    [string]$ExpectedImage,
    [switch]$ConfigurationOnly,
    [switch]$BeforeDecisionSetup,
    [string]$ContainerName = '',
    [ValidateRange(1, 60)][int]$Attempts = 30,
    [ValidateRange(1, 30)][int]$PollSeconds = 10
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DocIntelRelease.psm1') -Force
if ($BeforeDecisionSetup -and -not $ConfigurationOnly) {
    throw 'BeforeDecisionSetup is only valid for the preliminary configuration check, never release readiness.'
}
$subscriptionArguments = if ($SubscriptionId) { @('--subscription', $SubscriptionId) } else { @() }
if (-not $ResourceGroup -or -not $ContainerAppName -or (-not $ExpectedImage -and -not $ConfigurationOnly)) {
    throw 'The release target and exact expected image are required.'
}
for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
    $json = & az containerapp show --resource-group $ResourceGroup --name $ContainerAppName @subscriptionArguments --output json --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the released Container App.' }
    $app = ($json -join "`n") | ConvertFrom-Json -Depth 100
    $containers = @($app.properties.template.containers)
    $selected = if ($ContainerName) { @($containers | Where-Object { $_.name -ceq $ContainerName }) } else { $containers }
    if ($selected.Count -ne 1) { throw 'Select exactly one application container before releasing.' }
    if ($ConfigurationOnly) {
        if ($app.properties.configuration.ingress.transport -eq 'tcp' -or
            @($app.properties.configuration.ingress.additionalPortMappings |
                Where-Object { $null -ne $_ }).Count) {
            throw 'DocIntel requires HTTP ingress without additional TCP ports so ingress protocol headers cannot be bypassed.'
        }
        $settings = @{}
        foreach ($entry in $selected[0].env) { $settings[$entry.name] = $entry }
        $missing = @(@(
            'AZURE_CLIENT_ID', 'Identity__DirectoryTenantId', 'Identity__ClientId', 'Identity__ApiClientId',
            'Identity__PublicOrigin', 'Identity__DataProtectionBlobUri', 'Identity__DataProtectionKeyIdentifier',
            'AzureAI__BlobEndpoint', 'AzureAI__QueueEndpoint', 'AzureAI__CosmosEndpoint',
            'AzureAI__AzureOpenAIEndpoint', 'AzureAI__ContentUnderstandingEndpoint', 'AzureAI__TranslatorEndpoint',
            'DOCINTEL_IDENTITY_GRANTS', 'DocumentTranslation__StagingBlobEndpoint'
        ) | Where-Object { -not $settings[$_] -or (-not $settings[$_].value -and -not $settings[$_].secretRef) })
        if (-not $BeforeDecisionSetup) {
            $missing += @('AzureAI__DecisionEndpoint', 'AzureAI__DecisionDeploymentName') | Where-Object {
                -not $settings[$_] -or (-not $settings[$_].value -and -not $settings[$_].secretRef)
            }
        }
        if ($missing.Count) {
            throw "Run 'Set up DocIntel Azure' before releasing. Missing runtime settings: $($missing -join ', ')."
        }
        if ($settings.Identity__Mode.value -ne 'Entra' -or
            $settings.Identity__UseManagedIdentityAssertion.value -ne 'true' -or
            $settings.Models__AllowDevelopmentSimulation.value -eq 'true' -or
            $settings.DOTNET_ENVIRONMENT.value -eq 'Development' -or
            $settings.ASPNETCORE_ENVIRONMENT.value -eq 'Development') {
            throw "The runtime is not configured for production managed-identity sign-in. Run 'Set up DocIntel Azure' first."
        }
        if ($settings.Identity__ClientId.value -eq $settings.Identity__ApiClientId.value) {
            throw 'The web and API registrations must be different.'
        }
        if ($app.id -notmatch '^/subscriptions/(?<subscription>[a-fA-F0-9-]{36})/resourceGroups/(?<group>[^/]+)/providers/Microsoft.App/containerApps/[^/]+$') {
            throw 'Cannot verify translation lifecycle expiry without an unambiguous application resource ID.'
        }
        $storageSubscription = $Matches.subscription
        $storageGroup = $Matches.group
        $stagingEndpoint = $settings.DocumentTranslation__StagingBlobEndpoint.value
        if ($stagingEndpoint -notmatch '^https://(?<account>[a-z0-9]{3,24})\.blob\.core\.windows\.net/?$') {
            throw 'Cannot verify isolated staging without its canonical Blob binding; there is no fallback.'
        }
        $storageName = $Matches.account
        if ($stagingEndpoint.TrimEnd('/') -ieq $settings.AzureAI__BlobEndpoint.value.TrimEnd('/')) {
            throw 'Original-artifact storage is forbidden as translation staging; there is no fallback.'
        }
        $storageId = "/subscriptions/$storageSubscription/resourceGroups/$storageGroup/providers/Microsoft.Storage/storageAccounts/$storageName"
        $runtime = @($app.identity.userAssignedIdentities.PSObject.Properties | Where-Object {
            $_.Value.clientId -ieq $settings.AZURE_CLIENT_ID.value
        })
        if ($runtime.Count -ne 1) { throw 'Cannot verify staging ownership without its assigned runtime identity.' }
        $accountsJson = & az cognitiveservices account list --subscription $storageSubscription `
            --resource-group $storageGroup --output json --only-show-errors
        if ($LASTEXITCODE -ne 0) { throw 'Unable to verify the dedicated Translator binding.' }
        $accounts = @((($accountsJson -join "`n") | ConvertFrom-Json -Depth 100) | Where-Object {
            $_.kind -ceq 'TextTranslation' -and $_.sku.name -ceq 'S1' -and
                "https://$($_.properties.customSubDomainName).cognitiveservices.azure.com/".TrimEnd('/') -ieq
                    $settings.AzureAI__TranslatorEndpoint.value.TrimEnd('/')
        })
        if ($accounts.Count -ne 1) { throw 'A verified dedicated Translator is required for staging ownership.' }
        $accountJson = & az storage account show --subscription $storageSubscription --ids $storageId `
            --output json --only-show-errors
        if ($LASTEXITCODE -ne 0) { throw 'Unable to verify isolated staging; no account is silently adopted.' }
        $account = ($accountJson -join "`n") | ConvertFrom-Json -Depth 100
        $tags = Get-DocIntelTranslationStagingTags $app.id $runtime[0].Name $accounts[0].id
        $null = Assert-DocIntelTranslationStagingAccount $account $tags $storageId $accounts[0].location
        $PSNativeCommandUseErrorActionPreference = $false
        $policyJson = & az storage account management-policy show --subscription $storageSubscription `
            --resource-group $storageGroup --account-name $storageName --output json --only-show-errors 2>$null
        $policy = if ($LASTEXITCODE -eq 0 -and -not [String]::IsNullOrWhiteSpace(($policyJson -join "`n"))) {
            ($policyJson -join "`n") | ConvertFrom-Json -Depth 100
        } else { $null }
        $expiry = Get-DocIntelTranslationLifecycleState -StorageResourceId $storageId -Policy $policy
        if ($expiry.Status -cne 'Ready') {
            throw "Translation staging lifecycle expiry is not verified. $($expiry.Blocker)"
        }
        $runtimePrincipal = [string]$runtime[0].Value.principalId
        $translatorPrincipal = [string]$accounts[0].identity.principalId
        if ($runtimePrincipal -notmatch '^[a-fA-F0-9-]{36}$' -or
            $translatorPrincipal -notmatch '^[a-fA-F0-9-]{36}$') {
            throw 'Translation permission readiness requires verified runtime and Translator principals.'
        }
        $sourceScope = "$storageId/blobServices/default/containers/translation-source"
        $targetScope = "$storageId/blobServices/default/containers/translation-target"
        foreach ($permission in @(
            @{ Principal = $runtimePrincipal; Scope = $accounts[0].id; Role = 'a97b65f3-24c7-4388-baec-2e87135dc908' }
            @{ Principal = $translatorPrincipal; Scope = $sourceScope; Role = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1' }
            @{ Principal = $translatorPrincipal; Scope = $targetScope; Role = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' }
            @{ Principal = $runtimePrincipal; Scope = $sourceScope; Role = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' }
            @{ Principal = $runtimePrincipal; Scope = $targetScope; Role = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' }
        )) {
            $rolesJson = & az role assignment list --subscription $storageSubscription `
                --scope $permission.Scope --assignee-object-id $permission.Principal `
                --include-inherited --fill-principal-name false --output json --only-show-errors
            if ($LASTEXITCODE -ne 0) { throw 'Translation permission readiness could not read scoped assignments.' }
            $assignments = @(($rolesJson -join "`n") | ConvertFrom-Json -Depth 100)
            if (-not (Test-DocIntelTranslationPermission -Assignments $assignments `
                -PrincipalId $permission.Principal -RequiredRoleId $permission.Role `
                -RequiredScope $permission.Scope -StagingAccountScope $storageId)) {
                throw 'Translation data/inference permission readiness is incomplete; no grant is attempted.'
            }
        }
        Write-Output 'Production runtime settings are present. Their live provider access is verified after rollout.'
        return
    }
    if ($selected[0].image -cne $ExpectedImage) {
        throw 'The application container/image does not match this release. Readiness is not claimed.'
    }
    $revision = $app.properties.latestRevisionName
    $ready = $revision -and $app.properties.latestReadyRevisionName -ceq $revision
    if ($app.properties.configuration.activeRevisionsMode -eq 'Multiple') {
        $traffic = @($app.properties.configuration.ingress.traffic | Where-Object {
            $_.weight -eq 100 -and ($_.revisionName -ceq $revision -or $_.latestRevision -eq $true)
        })
        if ($traffic.Count -ne 1) {
            throw 'Multiple-revision traffic is not assigned entirely to this release. Configure an explicit reviewed rollout.'
        }
    }
    $hostName = $app.properties.configuration.ingress.fqdn
    if (-not $hostName -or $hostName -notmatch '^[A-Za-z0-9.-]+$') {
        throw 'The application has no valid HTTPS ingress hostname.'
    }
    if ($ready) {
        try {
            $response = Invoke-WebRequest -Uri "https://$hostName/health/ready" -SkipHttpErrorCheck -TimeoutSec 10
            if ($response.StatusCode -eq 200) {
                Write-Output "Released revision $revision is ready: https://$hostName"
                if ($env:GITHUB_STEP_SUMMARY) {
                    "### Released revision is ready`nhttps://$hostName" | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
                }
                return
            }
            Write-Warning "The released revision readiness endpoint returned HTTP $($response.StatusCode)."
        }
        catch [System.Net.Http.HttpRequestException] {
            Write-Warning 'The released revision readiness endpoint is not reachable yet.'
        }
        catch [System.Threading.Tasks.TaskCanceledException] {
            Write-Warning 'The released revision readiness check timed out.'
        }
    }
    if ($attempt + 1 -lt $Attempts) { Start-Sleep -Seconds $PollSeconds }
}
throw 'The released revision did not become ready. Inspect provider/storage configuration and logs; deployment success is not claimed.'
