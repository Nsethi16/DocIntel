#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$file = Join-Path $root 'scripts\Approve-DocIntelDirectory.ps1'
Import-Module (Join-Path $root 'scripts\DocIntelRelease.psm1') -Force
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('docintel-consent-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$global:DocIntelConsentFixture = @{}
$f = $global:DocIntelConsentFixture
foreach ($key in @('directory', 'principal', 'resource', 'role', 'subscription')) { $f[$key] = [guid]::NewGuid().ToString() }
$f.runtimeId = "/subscriptions/$($f.subscription)/resourceGroups/customer/providers/Microsoft.ManagedIdentity/userAssignedIdentities/runtime"
$request = @{ SchemaVersion = 1; DirectoryTenantId = $f.directory; RuntimePrincipalId = $f.principal
    RuntimeResourceId = $f.runtimeId; Permission = 'User.Read.All'; CoreRequestHash = 'a' * 64 }
$requestPath = Join-Path $temporary 'directory-consent-request.json'
@{ Request = $request; RequestHash = Get-DocIntelObjectHash $request } |
    ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $requestPath
$f.present = $false; $f.posts = 0; $f.badIdentity = $false; $f.wrongTenant = $false

function az {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $f = $global:DocIntelConsentFixture
    if (($Arguments[0..1] -join ' ') -cne 'account get-access-token' -or
        '--subscription' -in $Arguments -or
        $Arguments[[Array]::IndexOf($Arguments, '--tenant') + 1] -cne $f.directory) {
        throw 'Entra-only consent must not require Azure subscription roles or inspect Azure resources.'
    }
    $global:LASTEXITCODE = 0
    @{ tenant = $(if ($f.wrongTenant) { [guid]::Empty.ToString() } else { $f.directory })
        accessToken = 'private-admin-fixture' } | ConvertTo-Json -Compress
}

function Invoke-WebRequest {
    param([string]$Uri, [string]$Method, [hashtable]$Headers, [int]$MaximumRedirection,
        [int]$TimeoutSec, [switch]$SkipHttpErrorCheck, [string]$Body, [string]$ContentType)
    $f = $global:DocIntelConsentFixture
    if (-not $Uri.StartsWith('https://graph.microsoft.com/v1.0/', [StringComparison]::Ordinal) -or
        $Headers.Authorization -cne 'Bearer private-admin-fixture' -or $MaximumRedirection -ne 0 -or
        $TimeoutSec -ne 30 -or -not $SkipHttpErrorCheck) { throw 'Consent must remain on the verified Graph origin.' }
    $response = if ($Method -ceq 'POST') {
        $f.posts++
        $value = $Body | ConvertFrom-Json
        if ($value.principalId -cne $f.principal -or $value.resourceId -cne $f.resource -or
            $value.appRoleId -cne $f.role -or
            $Uri -cne "https://graph.microsoft.com/v1.0/servicePrincipals/$($f.resource)/appRoleAssignedTo") {
            throw 'The administrator may grant only the reviewed runtime User.Read.All assignment.'
        }
        $f.present = $true
        $value
    } elseif ($Uri.Contains('/appRoleAssignments?')) {
        @{ value = @($(if ($f.present) { @{ principalId = $f.principal; resourceId = $f.resource; appRoleId = $f.role } })) }
    } elseif ($Uri.Contains('servicePrincipals?')) {
        @{ value = @(@{ id = $f.resource; appRoles = @(@{
            id = $f.role; value = 'User.Read.All'; isEnabled = $true; allowedMemberTypes = @('Application')
        }) }) }
    } else {
        @{ id = $f.principal; servicePrincipalType = $(if ($f.badIdentity) { 'Application' } else { 'ManagedIdentity' })
            alternativeNames = @($f.runtimeId) }
    }
    return @{ StatusCode = $(if ($Method -eq 'POST') { 201 } else { 200 })
        Content = $response | ConvertTo-Json -Depth 30 -Compress }
}

function Expect-ConsentFailure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch {
        $failed = $true
        if ($_.Exception.Message.Contains('private-admin-fixture') -or $_.Exception.Message -notlike "*$Message*") { throw }
    }
    if (-not $failed) { throw "Expected administrator approval failure: $Message." }
}

try {
    $plan = & $file -ConsentRequestPath $requestPath
    if ($f.posts -or $plan.AlreadyAssigned) { throw 'Administrator Preview must not grant anything.' }
    Expect-ConsentFailure { & $file -ConsentRequestPath $requestPath -Mode Apply -ExpectedPlanHash $plan.PlanHash } 'CONSENT'
    if ($f.posts) { throw 'Missing approval must not grant a role.' }
    $result = & $file -ConsentRequestPath $requestPath -Mode Apply -Confirmation CONSENT -ExpectedPlanHash $plan.PlanHash
    if ($f.posts -ne 1 -or $result.Status -cne 'ConsentRecorded' -or $result.DirectoryConnected -or
        -not $result.RuntimeVerificationRequired) { throw 'Consent alone must not claim directory activation.' }
    $plan = & $file -ConsentRequestPath $requestPath
    & $file -ConsentRequestPath $requestPath -Mode Apply -Confirmation CONSENT -ExpectedPlanHash $plan.PlanHash | Out-Null
    if ($f.posts -ne 1) { throw 'Existing permission must not be assigned twice.' }
    $f.badIdentity = $true
    Expect-ConsentFailure { & $file -ConsentRequestPath $requestPath } 'exact Azure managed identity'
    $f.badIdentity = $false; $f.wrongTenant = $true
    Expect-ConsentFailure { & $file -ConsentRequestPath $requestPath } 'customer directory'
    if ($f.posts -ne 1) { throw 'Invalid target/credential must never receive permissions.' }
    Write-Output 'Graph-only administrator consent, identity binding and idempotency tests passed.'
} finally {
    Remove-Item Function:\az, Function:\Invoke-WebRequest -ErrorAction SilentlyContinue
    Remove-Variable DocIntelConsentFixture -Scope Global
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
