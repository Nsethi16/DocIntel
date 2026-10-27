#Requires -Version 7.3
[CmdletBinding()]
param(
    [string]$SubscriptionId = '',
    [string]$ResourceGroup,
    [string]$ContainerAppName,
    [string]$ExpectedImage,
    [switch]$ConfigurationOnly,
    [string]$ContainerName = '',
    [ValidateRange(1, 60)][int]$Attempts = 30,
    [ValidateRange(1, 30)][int]$PollSeconds = 10
)
$ErrorActionPreference = 'Stop'
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
            'AzureAI__AzureOpenAIEndpoint', 'AzureAI__ContentUnderstandingEndpoint',
            'AzureAI__DecisionEndpoint', 'AzureAI__DecisionDeploymentName', 'DOCINTEL_IDENTITY_GRANTS'
        ) | Where-Object { -not $settings[$_] -or (-not $settings[$_].value -and -not $settings[$_].secretRef) })
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
