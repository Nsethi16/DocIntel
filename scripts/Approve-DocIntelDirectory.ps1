#Requires -Version 7.3
<#
.SYNOPSIS
    Entra-administrator-only, Graph-only approval of the deployment's read-only directory request.
.DESCRIPTION
    Requires no Azure subscription role or container-exec access. Sign in to the customer
    directory (az login --tenant <id> --allow-no-subscriptions). Nothing is granted in Preview.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConsentRequestPath,
    [ValidateSet('Preview', 'Apply')][string]$Mode = 'Preview',
    [string]$ExpectedPlanHash = '',
    [string]$Confirmation = ''
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DocIntelRelease.psm1') -Force
if ($Mode -eq 'Apply' -and ($Confirmation -cne 'CONSENT' -or
    $ExpectedPlanHash -cnotmatch '^[a-f0-9]{64}$')) {
    throw 'Apply requires CONSENT and the exact reviewed administrator plan hash.'
}
$envelope = Get-Content -LiteralPath $ConsentRequestPath -Raw | ConvertFrom-Json -AsHashtable -Depth 20
$request = $envelope.Request
if ($request.SchemaVersion -ne 1 -or $request.Permission -cne 'User.Read.All' -or
    (Get-DocIntelObjectHash $request) -cne $envelope.RequestHash -or
    $request.CoreRequestHash -cnotmatch '^[a-f0-9]{64}$') {
    throw 'The directory consent request is invalid or changed.'
}
foreach ($key in @('DirectoryTenantId', 'RuntimePrincipalId')) {
    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($request[$key], [ref]$parsed) -or $parsed -eq [guid]::Empty) {
        throw 'Consent requires an explicit customer directory and managed-identity principal.'
    }
}
if ($request.RuntimeResourceId -notmatch '^/subscriptions/[a-fA-F0-9-]{36}/resourceGroups/[^/]+/providers/Microsoft.ManagedIdentity/userAssignedIdentities/[^/]+$') {
    throw 'The requested identity must be a customer user-assigned managed identity.'
}
$token = $null
$headers = $null
function Invoke-ConsentGraph([string]$Method, [string]$Path, [object]$Body = $null) {
    $parameters = @{ Uri = "https://graph.microsoft.com/v1.0/$Path"; Method = $Method
        Headers = $headers; MaximumRedirection = 0; TimeoutSec = 30; SkipHttpErrorCheck = $true }
    if ($null -ne $Body) { $parameters.Body = $Body | ConvertTo-Json -Depth 20 -Compress; $parameters.ContentType = 'application/json' }
    $response = Invoke-WebRequest @parameters
    if ($response.StatusCode -notin @(200, 201)) {
        throw "Entra administrator approval was not acknowledged (HTTP $($response.StatusCode)). No activation is confirmed."
    }
    return $response.Content | ConvertFrom-Json -Depth 50
}
try {
    $raw = & az account get-access-token --tenant $request.DirectoryTenantId --resource-type ms-graph `
        --output json --only-show-errors 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Sign in to the customer directory with an authorized Entra administrator.' }
    $token = ($raw -join "`n") | ConvertFrom-Json
    if ($token.tenant -ine $request.DirectoryTenantId -or -not $token.accessToken) {
        throw 'The Graph credential does not belong to the requested customer directory.'
    }
    $headers = @{ Authorization = 'Bearer ' + $token.accessToken }
    $principal = Invoke-ConsentGraph GET (
        "servicePrincipals/$($request.RuntimePrincipalId)?`$select=id,servicePrincipalType,alternativeNames")
    if ($principal.id -ine $request.RuntimePrincipalId -or $principal.servicePrincipalType -cne 'ManagedIdentity' -or
        $request.RuntimeResourceId -inotIn $principal.alternativeNames) {
        throw 'The directory principal is not the exact Azure managed identity in the request.'
    }
    $graph = Invoke-ConsentGraph GET (
        "servicePrincipals?`$filter=appId%20eq%20'00000003-0000-0000-c000-000000000000'&`$select=id,appRoles")
    $resources = @($graph.value | Where-Object { $null -ne $_ })
    if ($resources.Count -ne 1 -or $graph.'@odata.nextLink') { throw 'Microsoft Graph permission discovery is ambiguous.' }
    $roles = @($resources[0].appRoles | Where-Object {
        $_.value -ceq 'User.Read.All' -and $_.isEnabled -eq $true -and 'Application' -cin $_.allowedMemberTypes
    })
    if ($roles.Count -ne 1) { throw 'The exact read-only User.Read.All application permission was not found.' }
    foreach ($value in @($resources[0].id, $roles[0].id)) {
        $id = [guid]::Empty
        if (-not [guid]::TryParse($value, [ref]$id) -or $id -eq [guid]::Empty) {
            throw 'Microsoft Graph returned an invalid permission identity.'
        }
    }
    $assignments = Invoke-ConsentGraph GET (
        "servicePrincipals/$($request.RuntimePrincipalId)/appRoleAssignments?`$select=principalId,resourceId,appRoleId&`$top=100")
    if ($null -eq $assignments.value -or $assignments.'@odata.nextLink') {
        throw 'Permission discovery is incomplete; administrator review is required.'
    }
    $present = @($assignments.value | Where-Object {
        $_.principalId -ieq $request.RuntimePrincipalId -and $_.resourceId -ieq $resources[0].id -and
            $_.appRoleId -ieq $roles[0].id
    }).Count -gt 0
    $plan = @{ RequestHash = $envelope.RequestHash; Directory = $request.DirectoryTenantId
        RuntimePrincipal = $principal.id; RuntimeResource = $request.RuntimeResourceId
        GraphResource = $resources[0].id; AppRole = $roles[0].id; Permission = 'User.Read.All'; AlreadyAssigned = $present }
    $planHash = Get-DocIntelObjectHash $plan
    if ($Mode -eq 'Apply' -and $ExpectedPlanHash -cne $planHash) {
        throw 'The administrator approval plan changed. Preview again before consent.'
    }
    $output = Join-Path (Split-Path $ConsentRequestPath -Parent) 'directory-administrator-plan.json'
    @{ PlanHash = $planHash; Plan = $plan } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $output -Encoding utf8
    Write-Host "Customer directory: $($request.DirectoryTenantId). Runtime identity: $($principal.id)."
    Write-Host 'Permission: tenant-wide User.Read.All application read access; no mail, directory writes or role-granting authority.'
    if ($Mode -eq 'Preview') {
        return [pscustomobject]@{ PlanHash = $planHash; AlreadyAssigned = $present; Status = 'ApprovalPreview' }
    }
    if (-not $present) {
        $created = Invoke-ConsentGraph POST "servicePrincipals/$($resources[0].id)/appRoleAssignedTo" @{
            principalId = $principal.id; resourceId = $resources[0].id; appRoleId = $roles[0].id
        }
        if ($created.principalId -ine $principal.id -or $created.resourceId -ine $resources[0].id -or
            $created.appRoleId -ine $roles[0].id) { throw 'The approved assignment was not confirmed.' }
    }
    Write-Host 'Directory consent is recorded. Re-run deployment verification; lookup is not enabled by this administrator script.'
    return [pscustomobject]@{ Status = 'ConsentRecorded'; DirectoryConnected = $false; RuntimeVerificationRequired = $true }
} finally {
    $raw = $null
    $token = $null
    $headers = $null
}
