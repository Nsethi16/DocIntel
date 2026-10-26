#!/usr/bin/env pwsh
# DocIntel azd post-provision hook.
#
# Completes the Entra configuration now that provisioning has produced the
# public origin and the runtime managed identity:
#   * exposes the enabled DocIntel.Access delegated scope on docintel-api
#     (without deleting or overriding a deliberate existing scope/role),
#   * adds the <origin>/signin-oidc redirect URI to docintel-web,
#   * adds the managed-identity federated credential docintel-web uses for
#     passwordless (secret-free) sign-in token exchange,
#   * prints a verification checklist.
#
# It only adds what is missing and is safe to re-run. It does not grant
# directory consent or privileged roles.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$message) { Write-Error $message; exit 1 }

function New-TempJson([object]$value) {
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("docintel-" + [guid]::NewGuid().ToString('n') + '.json')
    $value | ConvertTo-Json -Depth 20 | Set-Content -Path $path -Encoding utf8
    return $path
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Fail 'The Azure CLI (az) is required to finish DocIntel identity setup.'
}

$web = $env:WEB_CLIENT_ID
$api = $env:API_CLIENT_ID
$tenant = $env:DIRECTORY_TENANT_ID
$redirect = $env:REQUIRED_WEB_REDIRECT_URI
$subject = $env:REQUIRED_FEDERATION_SUBJECT
$origin = $env:CONTAINER_APP_FQDN

foreach ($pair in @(@('WEB_CLIENT_ID', $web), @('API_CLIENT_ID', $api), @('DIRECTORY_TENANT_ID', $tenant),
                    @('REQUIRED_WEB_REDIRECT_URI', $redirect), @('REQUIRED_FEDERATION_SUBJECT', $subject))) {
    if ([string]::IsNullOrWhiteSpace($pair[1])) { Fail "Missing provisioning value $($pair[0]); cannot finish identity setup." }
}

# --- docintel-api: expose the DocIntel.Access delegated scope ---------------
$apiApp = az ad app show --id $api -o json | ConvertFrom-Json
if (-not $apiApp) { Fail "docintel-api registration $api was not found." }

$existingRole = @($apiApp.appRoles | Where-Object { $_.value -ceq 'DocIntel.Access' })
if ($existingRole.Count) {
    Fail 'An app role named DocIntel.Access conflicts with the delegated scope. Resolve it explicitly; setup will not delete roles.'
}
$scopes = @($apiApp.api.oauth2PermissionScopes | Where-Object { $null -ne $_ })
$accessScope = $scopes | Where-Object { $_.value -ceq 'DocIntel.Access' } | Select-Object -First 1
if ($accessScope) {
    if (-not $accessScope.isEnabled) { Fail 'DocIntel.Access is present but disabled; setup will not override a deliberate disablement.' }
    Write-Host 'DocIntel: DocIntel.Access scope already present.'
} else {
    Write-Host 'DocIntel: adding DocIntel.Access delegated scope to docintel-api.'
    $scopes += [pscustomobject]@{
        id                      = [guid]::NewGuid().ToString()
        value                   = 'DocIntel.Access'
        isEnabled               = $true
        type                    = 'Admin'
        adminConsentDisplayName = 'Access DocIntel API'
        adminConsentDescription = 'Access DocIntel on behalf of an explicitly assigned signed-in user.'
    }
    $apiPatch = @{ api = @{ oauth2PermissionScopes = $scopes } }
    if (-not $apiApp.api.requestedAccessTokenVersion) { $apiPatch.api.requestedAccessTokenVersion = 2 }
    if (-not @($apiApp.identifierUris).Count) { $apiPatch.identifierUris = @("api://$api") }
    $patchFile = New-TempJson $apiPatch
    az rest --method PATCH --url "https://graph.microsoft.com/v1.0/applications/$($apiApp.id)" --body "@$patchFile" | Out-Null
    Remove-Item $patchFile -ErrorAction SilentlyContinue
}

# --- docintel-web: redirect URI ---------------------------------------------
$webApp = az ad app show --id $web -o json | ConvertFrom-Json
if (-not $webApp) { Fail "docintel-web registration $web was not found." }
$redirects = @($webApp.web.redirectUris)
if ($redirect -notin $redirects) {
    Write-Host "DocIntel: adding redirect URI $redirect to docintel-web."
    $redirects += $redirect
    az ad app update --id $web --web-redirect-uris @redirects | Out-Null
} else {
    Write-Host 'DocIntel: redirect URI already present on docintel-web.'
}

# --- docintel-web: managed-identity federated credential --------------------
$issuer = "https://login.microsoftonline.com/$tenant/v2.0"
$creds = @(az ad app federated-credential list --id $web -o json | ConvertFrom-Json)
$match = @($creds | Where-Object {
    $_.issuer -ceq $issuer -and $_.subject -ceq $subject -and ('api://AzureADTokenExchange' -in $_.audiences)
})
if ($match.Count) {
    Write-Host 'DocIntel: managed-identity federated credential already present.'
} else {
    Write-Host 'DocIntel: adding managed-identity federated credential to docintel-web.'
    $fedFile = New-TempJson @{
        name      = "docintel-runtime-$($subject.Substring(0, [Math]::Min(8, $subject.Length)))"
        issuer    = $issuer
        subject   = $subject
        audiences = @('api://AzureADTokenExchange')
    }
    az ad app federated-credential create --id $web --parameters "@$fedFile" | Out-Null
    Remove-Item $fedFile -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host '============================================================'
Write-Host ' DocIntel provisioning + identity setup complete.'
Write-Host "   Public origin : $origin"
Write-Host "   Web client    : $web"
Write-Host "   API client    : $api"
Write-Host ''
Write-Host ' Next: `azd deploy` (or the end of `azd up`) builds and pushes the'
Write-Host ' image and updates the Container App. Then verify:'
Write-Host "   * $origin/health/live and /health/ready return healthy"
Write-Host '   * admin-consent the docintel-api / Microsoft Graph permissions'
Write-Host '     the directory admin intends to grant'
Write-Host '   * sign in, upload a real PDF, and confirm a cited Ask AI answer'
Write-Host '============================================================'
