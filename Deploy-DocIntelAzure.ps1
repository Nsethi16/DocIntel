<#
.SYNOPSIS
    One-command deployment script for DocIntel Agent Harness Studio to Azure Container Apps.
.EXAMPLE
    .\Deploy-DocIntelAzure.ps1 -SubscriptionId "<your-subscription-id>" -ResourceGroup "rg-docintel-dev-westus2" -Location "westus2" -FoundryEndpoint "https://<your-foundry>.services.ai.azure.com/models"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$SubscriptionId,

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
    [string]$TranslatorEndpoint = ""
)

$ErrorActionPreference = "Stop"

Write-Host "1. Setting Azure subscription to $SubscriptionId..." -ForegroundColor Cyan
az account set --subscription $SubscriptionId

Write-Host "2. Ensuring Resource Group '$ResourceGroup' exists in '$Location'..." -ForegroundColor Cyan
az group create --name $ResourceGroup --location $Location --output none

if ([string]::IsNullOrWhiteSpace($AcrName)) {
    $suffix = (Get-Random -Minimum 1000 -Maximum 9999)
    $AcrName = "acrdocintel$EnvironmentName$suffix"
    Write-Host "3. Creating Azure Container Registry '$AcrName'..." -ForegroundColor Cyan
    az acr create --name $AcrName --resource-group $ResourceGroup --location $Location --sku Basic --output none
}

$imageTag = "$AcrName.azurecr.io/docintel-api:latest"
Write-Host "4. Building and pushing container image '$imageTag' via .NET SDK container publish..." -ForegroundColor Cyan
az acr login --name $AcrName
dotnet publish "$PSScriptRoot\src\DocIntel.Api\DocIntel.Api.csproj" `
    -c Release `
    --os linux `
    --arch x64 `
    /t:PublishContainer `
    -p:ContainerRegistry="$AcrName.azurecr.io" `
    -p:ContainerRepository="docintel-api" `
    -p:ContainerImageTag="latest"

Write-Host "5. Deploying Bicep infrastructure (UAMI + Storage + Container App)..." -ForegroundColor Cyan
$deployResult = az deployment group create `
    --resource-group $ResourceGroup `
    --template-file "$PSScriptRoot\infra\main.bicep" `
    --parameters `
        location=$Location `
        environmentName=$EnvironmentName `
        containerImage=$imageTag `
        acrName=$AcrName `
        foundryInferenceEndpoint=$FoundryEndpoint `
        azureOpenAiEndpoint=$AzureOpenAiEndpoint `
        translatorEndpoint=$TranslatorEndpoint `
    --query "properties.outputs" `
    -o json | ConvertFrom-Json

Write-Host "`nDeployment Complete!" -ForegroundColor Green
Write-Host "  Agent Harness Studio URL : $($deployResult.containerAppFqdn.value)" -ForegroundColor Green
Write-Host "  UAMI Client ID           : $($deployResult.managedIdentityClientId.value)"
Write-Host "  UAMI Principal ID        : $($deployResult.managedIdentityPrincipalId.value)"
Write-Host "  Storage Account          : $($deployResult.storageAccountName.value)"
