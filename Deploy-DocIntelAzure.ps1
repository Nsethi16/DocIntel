#Requires -Version 7.3
<#
.SYNOPSIS
    Preview or apply a parameterized, managed-identity DocIntel deployment.
.DESCRIPTION
    Preview is the default. -Apply provisions shared infrastructure and publishes a unique image.
    -ConfigureWebRegistration additionally adds the app redirect URI and a UAMI federated credential;
    it requires ownership/permission on the named Entra web application. No app registrations,
    directory consent grants, or default privileged users are created.
.EXAMPLE
    .\Deploy-DocIntelAzure.ps1 -SubscriptionId <id> -DirectoryTenantId <id> -WebClientId <id> `
        -ApiClientId <id> -IdentityGrantsPath .\identity-grants.json -Apply -ConfigureWebRegistration
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$SubscriptionId = "",

    [string]$DirectoryTenantId = "",
    [string]$WebClientId = "",
    [string]$ApiClientId = "",
    [string]$IdentityGrantsPath = "",
    [switch]$Apply,
    [switch]$ConfigureWebRegistration,

    [Parameter(Mandatory = $false)]
    [string]$ResourceGroup = "rg-docintel-dev-westus2",

    [Parameter(Mandatory = $false)]
    [string]$Location = "westus2",

    [Parameter(Mandatory = $false)]
    [string]$EnvironmentName = "dev",

    [Parameter(Mandatory = $false)]
    [string]$AcrName = "",

    [Parameter(Mandatory = $false)]
    [string]$FoundryEndpoint = "",

    [Parameter(Mandatory = $false)]
    [string]$AzureOpenAiEndpoint = "",

    [Parameter(Mandatory = $false)]
    [string]$TranslatorEndpoint = "",
    [string]$ContentUnderstandingEndpoint = ""
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

foreach ($entry in @{
    SubscriptionId = $SubscriptionId; DirectoryTenantId = $DirectoryTenantId
    WebClientId = $WebClientId; ApiClientId = $ApiClientId
}.GetEnumerator()) {
    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($entry.Value, [ref]$parsed) -or $parsed -eq [guid]::Empty) {
        throw "$($entry.Key) must be an explicit, nonempty GUID. No Azure resources were changed."
    }
}
if ($WebClientId -eq $ApiClientId) { throw "Web and API registrations must be separate." }
if ($TranslatorEndpoint) {
    throw 'Existing translation endpoints require scripts\Sync-DocIntelTranslation.ps1 preview/reconciliation. Fresh deployments leave TranslatorEndpoint empty to provision a dedicated regional S1 resource.'
}
if ($EnvironmentName -notmatch '^[a-z0-9]{2,8}$') { throw "EnvironmentName must contain 2-8 lowercase letters/digits." }
if ([string]::IsNullOrWhiteSpace($IdentityGrantsPath) -or
    -not (Test-Path -LiteralPath $IdentityGrantsPath -PathType Leaf)) {
    throw "Provide a server-owned IdentityGrantsPath containing Memberships and/or PlatformAdministrators."
}
$grants = Get-Content -LiteralPath $IdentityGrantsPath -Raw | ConvertFrom-Json
if (@($grants.PSObject.Properties.Name | Where-Object { $_ -notin @('Memberships', 'PlatformAdministrators') }).Count -gt 0 -or
    (@($grants.Memberships | Where-Object { $null -ne $_ }).Count -eq 0 -and
     @($grants.PlatformAdministrators | Where-Object { $null -ne $_ }).Count -eq 0)) {
    throw "Identity grants must contain only nonempty Memberships and/or PlatformAdministrators."
}

$parameterPath = Join-Path ([IO.Path]::GetTempPath()) ("docintel-" + [guid]::NewGuid().ToString('N') + ".parameters.json")
$federationPath = Join-Path ([IO.Path]::GetTempPath()) ("docintel-" + [guid]::NewGuid().ToString('N') + ".federation.json")
$previousSubscription = $null
$oldRegistryUser = $env:DOTNET_CONTAINER_REGISTRY_UNAME
$oldRegistryPassword = $env:DOTNET_CONTAINER_REGISTRY_PWORD
Push-Location $PSScriptRoot
try {
    az bicep build --file "$PSScriptRoot\infra\main.bicep" --stdout --only-show-errors | Out-Null
    $previousSubscription = az account show --query id --output tsv --only-show-errors
    $account = az account show --subscription $SubscriptionId --output json --only-show-errors | ConvertFrom-Json
    if ([guid]$account.tenantId -ne [guid]$DirectoryTenantId) {
        throw "The subscription directory does not match DirectoryTenantId."
    }
    az account set --subscription $SubscriptionId
    $web = az ad app show --id $WebClientId --output json --only-show-errors | ConvertFrom-Json
    $api = az ad app show --id $ApiClientId --output json --only-show-errors | ConvertFrom-Json
    if (-not @($api.api.oauth2PermissionScopes | Where-Object { $_.value -eq 'DocIntel.Access' -and $_.isEnabled }).Count) {
        throw "The API registration must expose the enabled DocIntel.Access delegated scope. An app role is not required for browser deployment."
    }
    if ([string]::IsNullOrWhiteSpace($AcrName)) {
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes("$SubscriptionId/$ResourceGroup"))).ToLowerInvariant()
        $AcrName = "acrdocintel$EnvironmentName$($hash.Substring(0, 12))"
    }
    $tag = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
    $parameters = @{
        '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters = @{
            location = @{ value = $Location }; environmentName = @{ value = $EnvironmentName }
            deployApplication = @{ value = $true }; acrName = @{ value = $AcrName }
            containerImage = @{ value = "$AcrName.azurecr.io/docintel-api:$tag" }
            directoryTenantId = @{ value = $DirectoryTenantId }
            webClientId = @{ value = $WebClientId }; apiClientId = @{ value = $ApiClientId }
            identityGrants = @{ value = $grants }
            foundryInferenceEndpoint = @{ value = $FoundryEndpoint }
            azureOpenAiEndpoint = @{ value = $AzureOpenAiEndpoint }
            translatorEndpoint = @{ value = $TranslatorEndpoint }
            contentUnderstandingEndpoint = @{ value = $ContentUnderstandingEndpoint }
        }
    }
    $parameters | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $parameterPath -Encoding utf8
    $exists = az group exists --subscription $SubscriptionId --name $ResourceGroup --output tsv --only-show-errors
    if ($exists -eq 'true') {
        $existingStorage = @(az storage account list --subscription $SubscriptionId `
            --resource-group $ResourceGroup --output json --only-show-errors | ConvertFrom-Json)
        if ($existingStorage.Count) {
            throw 'This fresh-deployment helper never initializes lifecycle policies on existing accounts. Use scripts\Sync-DocIntelTranslation.ps1 for reviewed adoption and expiry verification.'
        }
    }
    if ($exists -ne 'true') {
        if (-not $Apply) {
            Write-Host "Bicep and identity preflight passed. The resource group is absent; no what-if or resource changes were performed."
            return
        }
        az group create --subscription $SubscriptionId --name $ResourceGroup --location $Location --output none
    }
    az deployment group what-if --subscription $SubscriptionId --resource-group $ResourceGroup `
        --template-file "$PSScriptRoot\infra\main.bicep" --parameters "@$parameterPath" `
        --result-format ResourceIdOnly --only-show-errors
    if (-not $Apply) { Write-Host "Preview only. Use -Apply after reviewing the proposed resources."; return }

    dotnet test DocIntel.slnx -c Release --nologo --verbosity quiet
    node --test tests\DocIntel.Ui.Tests\identity-http.test.cjs tests\DocIntel.Ui.Tests\identity-client.test.cjs `
        tests\DocIntel.Ui.Tests\ask-ai.test.cjs tests\DocIntel.Ui.Tests\ask-ai-restart.test.cjs
    $parameters.parameters.deployApplication.value = $false
    $parameters | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $parameterPath -Encoding utf8
    $bootstrap = az deployment group create --subscription $SubscriptionId --resource-group $ResourceGroup `
        --template-file "$PSScriptRoot\infra\main.bicep" --parameters "@$parameterPath" `
        --query properties.outputs --output json --only-show-errors | ConvertFrom-Json

    $redirect = $bootstrap.requiredWebRedirectUri.value
    $subject = $bootstrap.requiredFederationSubject.value
    $issuer = "https://login.microsoftonline.com/$DirectoryTenantId/v2.0"
    $federations = az ad app federated-credential list --id $WebClientId --output json --only-show-errors | ConvertFrom-Json
    $federated = @($federations | Where-Object {
        $_.issuer -eq $issuer -and $_.subject -eq $subject -and 'api://AzureADTokenExchange' -in $_.audiences
    }).Count -gt 0
    if (-not $federated -and $ConfigureWebRegistration) {
        @{ name = "docintel-$EnvironmentName-$($subject.Substring(0, 8))"; issuer = $issuer; subject = $subject
           audiences = @('api://AzureADTokenExchange') } |
            ConvertTo-Json | Set-Content -LiteralPath $federationPath -Encoding utf8
        az ad app federated-credential create --id $WebClientId --parameters "@$federationPath" --output none
        $federated = $true
    }
    $redirects = @($web.web.redirectUris)
    if ($redirect -notin $redirects -and $ConfigureWebRegistration) {
        $redirects += $redirect
        az ad app update --id $WebClientId --web-redirect-uris @redirects --output none
    }
    if (-not $federated -or $redirect -notin $redirects) {
        throw "Shared infrastructure is ready, but web registration federation/redirect is missing. Configure it or use -ConfigureWebRegistration; the running app was not replaced."
    }
    $login = az acr login --name $AcrName --expose-token --output json --only-show-errors | ConvertFrom-Json
    $env:DOTNET_CONTAINER_REGISTRY_UNAME = '00000000-0000-0000-0000-000000000000'
    $env:DOTNET_CONTAINER_REGISTRY_PWORD = $login.accessToken
    dotnet publish "$PSScriptRoot\src\DocIntel.Api\DocIntel.Api.csproj" -c Release --os linux --arch x64 `
        /t:PublishContainer "-p:ContainerRegistry=$AcrName.azurecr.io" `
        -p:ContainerRepository=docintel-api "-p:ContainerImageTag=$tag" --verbosity minimal
    $env:DOTNET_CONTAINER_REGISTRY_PWORD = $oldRegistryPassword
    $login = $null

    $parameters.parameters.deployApplication.value = $true
    $parameters.parameters.configureTranslation = @{ value = $false }
    $parameters | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $parameterPath -Encoding utf8
    $deployed = az deployment group create --subscription $SubscriptionId --resource-group $ResourceGroup `
        --template-file "$PSScriptRoot\infra\main.bicep" --parameters "@$parameterPath" `
        --query properties.outputs --output json --only-show-errors | ConvertFrom-Json
    $url = $deployed.containerAppFqdn.value
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri "$url/health" -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        }
        catch [System.Net.Http.HttpRequestException] {
            Write-Warning "Readiness endpoint is not reachable yet."
        }
        Start-Sleep -Seconds 10
    }
    if (-not $ready) { throw "The new revision is not ready. Inspect health and role propagation; deployment success is not claimed." }
    Write-Host "Deployment is ready: $url"
}
finally {
    $env:DOTNET_CONTAINER_REGISTRY_UNAME = $oldRegistryUser
    $env:DOTNET_CONTAINER_REGISTRY_PWORD = $oldRegistryPassword
    if ($previousSubscription) { az account set --subscription $previousSubscription }
    if (Test-Path -LiteralPath $parameterPath) { Remove-Item -LiteralPath $parameterPath }
    if (Test-Path -LiteralPath $federationPath) { Remove-Item -LiteralPath $federationPath }
    Pop-Location
}
