#!/usr/bin/env pwsh
# DocIntel azd pre-provision hook.
#
# Prepares the identity inputs the hardened Bicep template needs before any
# resource is created, so a customer can run `azd up` against their own Azure:
#   * ensures the docintel-web (confidential) and docintel-api app
#     registrations and their service principals exist (created only if
#     missing; an unambiguous existing registration is reused, never mutated
#     destructively here),
#   * captures the deploying admin's directory object ID to seed the first
#     tenant admin, and the directory tenant ID,
#   * writes those values into the azd environment for main.parameters.json.
#
# This runs with the credentials of the person running `azd up`. It uses the
# Azure CLI for directory operations, so `az login` into the same tenant is a
# prerequisite (azd and az keep separate sign-in state). It never grants itself
# privileges, creates a conflicting app role, or configures Graph consent.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$message) {
    Write-Error $message
    exit 1
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Fail 'The Azure CLI (az) is required for DocIntel identity setup. Install it and run `az login --tenant <your-tenant>`.'
}

# Confirm az is signed in and aligned with the tenant azd is deploying into.
$account = az account show -o json 2>$null | ConvertFrom-Json
if (-not $account) {
    Fail 'Azure CLI is not signed in. Run `az login --tenant <your-directory-tenant-id>` and re-run `azd up`.'
}
$directoryTenantId = $account.tenantId
if ($env:AZURE_TENANT_ID -and $env:AZURE_TENANT_ID -ne $directoryTenantId) {
    Fail "Azure CLI is signed into tenant $directoryTenantId but azd targets $($env:AZURE_TENANT_ID). Run `az login --tenant $($env:AZURE_TENANT_ID)`."
}

Write-Host "DocIntel: preparing Entra identity in directory $directoryTenantId"

# Resolve the deploying admin's object ID for the first tenant-admin grant.
# Prefer the interactively signed-in user; fall back to azd's principal or an
# explicitly supplied object ID. An empty result seeds no user (no default
# account is invented), matching DocIntel's bootstrap policy.
$adminObjectId = ''
try {
    $adminObjectId = (az ad signed-in-user show --query id -o tsv 2>$null)
} catch { $adminObjectId = '' }
if ([string]::IsNullOrWhiteSpace($adminObjectId)) {
    if ($env:DOCINTEL_ADMIN_OBJECT_ID) { $adminObjectId = $env:DOCINTEL_ADMIN_OBJECT_ID }
    elseif ($env:AZURE_PRINCIPAL_ID) { $adminObjectId = $env:AZURE_PRINCIPAL_ID }
}
if ([string]::IsNullOrWhiteSpace($adminObjectId)) {
    Write-Warning 'No deploying-admin object ID could be resolved (non-interactive sign-in?). No first tenant admin will be seeded. Set DOCINTEL_ADMIN_OBJECT_ID (azd env set) to seed one.'
    $adminObjectId = ''
}

# Ensure an application registration exists, reusing an unambiguous existing one.
function Resolve-Application([string]$displayName, [string]$existingClientId) {
    if ($existingClientId) {
        $app = az ad app show --id $existingClientId -o json 2>$null | ConvertFrom-Json
        if (-not $app) { Fail "Configured client ID '$existingClientId' for '$displayName' was not found in this directory." }
        return $app.appId
    }
    $found = @(az ad app list --display-name $displayName --query "[].appId" -o json 2>$null | ConvertFrom-Json)
    if ($found.Count -gt 1) {
        Fail "Multiple '$displayName' registrations exist. Set its client ID explicitly (azd env set) and re-run."
    }
    if ($found.Count -eq 1) {
        Write-Host "DocIntel: reusing existing '$displayName' registration ($($found[0]))"
        return $found[0]
    }
    Write-Host "DocIntel: creating '$displayName' registration"
    $created = az ad app create --display-name $displayName --sign-in-audience AzureADMyOrg -o json | ConvertFrom-Json
    if (-not $created) { Fail "Failed to create the '$displayName' registration." }
    return $created.appId
}

$webClientId = Resolve-Application 'docintel-web' $env:WEB_CLIENT_ID
$apiClientId = Resolve-Application 'docintel-api' $env:API_CLIENT_ID
if ($webClientId -eq $apiClientId) { Fail 'The web and API registrations must be two separate applications.' }

# Ensure a service principal exists for each registration (idempotent).
foreach ($clientId in @($webClientId, $apiClientId)) {
    $sp = az ad sp list --filter "appId eq '$clientId'" --query "[0].id" -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($sp)) {
        Write-Host "DocIntel: creating service principal for $clientId"
        az ad sp create --id $clientId -o none 2>$null
    }
}

$appTenant = if ($env:DOCINTEL_APP_TENANT) { $env:DOCINTEL_APP_TENANT } else { 'default' }
if ($appTenant -notmatch '^[a-z0-9][a-z0-9-]{0,127}$') {
    Fail "DOCINTEL_APP_TENANT ('$appTenant') must be lowercase, start alphanumeric, and be 1-128 chars."
}

# Publish the resolved values into the azd environment for the Bicep provision.
az account set --subscription $account.id -o none 2>$null
azd env set DIRECTORY_TENANT_ID $directoryTenantId | Out-Null
azd env set WEB_CLIENT_ID $webClientId | Out-Null
azd env set API_CLIENT_ID $apiClientId | Out-Null
azd env set DOCINTEL_ADMIN_OBJECT_ID $adminObjectId | Out-Null
azd env set DOCINTEL_APP_TENANT $appTenant | Out-Null

Write-Host "DocIntel: identity inputs ready (web=$webClientId, api=$apiClientId, appTenant=$appTenant)."
