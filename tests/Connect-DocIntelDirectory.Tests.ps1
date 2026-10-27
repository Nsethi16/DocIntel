#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
$previousSummary = $env:GITHUB_STEP_SUMMARY
$env:GITHUB_STEP_SUMMARY = $null
$root = Split-Path $PSScriptRoot -Parent
$file = Join-Path $root 'scripts\Connect-DocIntelDirectory.ps1'
Import-Module (Join-Path $root 'scripts\DocIntelRelease.psm1') -Force
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('docintel-directory-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$global:DocIntelDirectoryFixture = @{ calls = [Collections.Generic.List[object]]::new()
    writes = [Collections.Generic.List[object]]::new() }
$f = $global:DocIntelDirectoryFixture
foreach ($key in @('subscription', 'directory', 'owner', 'client', 'principal', 'web', 'api', 'graph', 'role')) {
    $f[$key] = [guid]::NewGuid().ToString()
}
$f.group = 'rg-customer'
$f.runtimeId = "/subscriptions/$($f.subscription)/resourceGroups/$($f.group)/providers/Microsoft.ManagedIdentity/userAssignedIdentities/runtime"
$f.image = 'ghcr.io/nsethi16/docintel-api@sha256:' + ('a' * 64)
$request = @{ Subscription = $f.subscription; Directory = $f.directory; Owner = $f.owner
    Group = $f.group; Customer = 'acme'; Environment = 'prod'; Image = $f.image }
$f.requestHash = Get-DocIntelObjectHash $request
@{ SchemaVersion = 1; Request = $request; RequestHash = $f.requestHash } |
    ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $temporary 'customer-installation.json')

function Reset-DirectoryFixture {
    $f = $global:DocIntelDirectoryFixture
    $f.calls.Clear(); $f.writes.Clear()
    $f.enabled = $false; $f.access = $false; $f.present = $false; $f.ownerEligible = $true
    $f.graphDenied = $false; $f.grantDenied = $false; $f.propagating = $false
    $f.invalidProbe = $false; $f.probes = 0; $f.grants = 0; $f.updates = 0
}

function az {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $f = $global:DocIntelDirectoryFixture
    $f.calls.Add($Arguments); $global:LASTEXITCODE = 0
    if ($Arguments[[Array]::IndexOf($Arguments, '--subscription') + 1] -cne $f.subscription) {
        throw 'Every directory setup operation must explicitly target the customer subscription.'
    }
    $key = ($Arguments | Select-Object -First 3) -join ' '
    $value = switch -Wildcard ($key) {
        'account show *' { @{ id = $f.subscription; tenantId = $f.directory }; break }
        'account get-access-token *' {
            if ($Arguments[[Array]::IndexOf($Arguments, '--query') + 1] -cne 'tenant') { throw 'Never expose Graph credentials.' }
            $f.directory; break
        }
        'deployment sub show' {
            @{ properties = @{ provisioningState = 'Succeeded'; parameters = @{
                installationRequestHash = @{ value = $f.requestHash }; directoryTenantId = @{ value = $f.directory }
                initialOwnerObjectId = @{ value = $f.owner }; containerImage = @{ value = $f.image }
                webClientId = @{ value = $f.web }; apiClientId = @{ value = $f.api }
            }; outputs = @{
                containerAppName = @{ value = 'customer-app' }; managedIdentityResourceId = @{ value = $f.runtimeId }
                managedIdentityClientId = @{ value = $f.client }; managedIdentityPrincipalId = @{ value = $f.principal }
            } } }; break
        }
        'containerapp show --resource-group' {
            $settings = @{
                AZURE_CLIENT_ID = $f.client; Identity__ManagedIdentityClientId = $f.client
                Identity__DirectoryTenantId = $f.directory; Identity__ClientId = $f.web; Identity__ApiClientId = $f.api
                Identity__Mode = 'Entra'; Identity__UseManagedIdentityAssertion = 'true'
                Identity__Directory__Enabled = $f.enabled.ToString().ToLowerInvariant()
                Identity__PublicOrigin = 'https://customer.azurecontainerapps.io'
                Identity__DataProtectionBlobUri = 'https://storage.example/keys'
                Identity__DataProtectionKeyIdentifier = 'https://vault.example/keys/key'
                AzureAI__BlobEndpoint = 'https://storage.example'; AzureAI__QueueEndpoint = 'https://queue.example'
                AzureAI__CosmosEndpoint = 'https://cosmos.example'; AzureAI__AzureOpenAIEndpoint = 'https://ai.example'
                AzureAI__ContentUnderstandingEndpoint = 'https://ai.example'; DOCINTEL_IDENTITY_GRANTS = '{}'
            }
            @{ identity = @{ userAssignedIdentities = @{ $f.runtimeId = @{ clientId = $f.client; principalId = $f.principal } } }
                properties = @{
                    latestRevisionName = $(if ($f.enabled) { 'connected' } else { 'core' })
                    latestReadyRevisionName = $(if ($f.enabled) { 'connected' } else { 'core' })
                    configuration = @{ activeRevisionsMode = 'Single'; ingress = @{ fqdn = 'customer.azurecontainerapps.io' } }
                    template = @{ containers = @(@{ name = 'docintel-api'; image = $f.image
                        env = @($settings.GetEnumerator() | ForEach-Object { @{ name = $_.Key; value = $_.Value } })
                    }) }
                } }; break
        }
        'containerapp update --resource-group' {
            $f.writes.Add($Arguments); $f.updates++
            $index = [Array]::IndexOf($Arguments, '--set-env-vars')
            if ($index -lt 0 -or $Arguments[$index + 1] -cne 'Identity__Directory__Enabled=true') {
                throw 'Directory activation must change only the opt-in flag and never disable it.'
            }
            if (-not $f.access -or -not $f.ownerEligible) { throw 'Activation must follow actual runtime proof.' }
            $f.enabled = $true; @{}; break
        }
        'rest --method *' {
            $method = $Arguments[2]
            $url = $Arguments[[Array]::IndexOf($Arguments, '--url') + 1]
            if (-not $url.StartsWith('https://graph.microsoft.com/v1.0/', [StringComparison]::Ordinal)) {
                throw 'Only the fixed Graph origin is supported.'
            }
            if ($f.graphDenied) { $global:LASTEXITCODE = 1; 'Authorization_RequestDenied'; break }
            if ($method -ceq 'POST') {
                $f.writes.Add($Arguments); $f.grants++
                if ($url -cne "https://graph.microsoft.com/v1.0/servicePrincipals/$($f.graph)/appRoleAssignedTo") {
                    throw 'Only the discovered Microsoft Graph User.Read.All assignment may be created.'
                }
                $body = Get-Content -LiteralPath $Arguments[[Array]::IndexOf($Arguments, '--body') + 1].TrimStart('@') -Raw |
                    ConvertFrom-Json
                if ($body.principalId -cne $f.principal -or $body.resourceId -cne $f.graph -or $body.appRoleId -cne $f.role) {
                    throw 'Consent must target only the reviewed runtime identity and read-only role.'
                }
                if ($f.grantDenied) { $global:LASTEXITCODE = 1; 'Authorization_RequestDenied'; break }
                $f.present = $true
                if (-not $f.propagating) { $f.access = $true }
                $body; break
            }
            if ($method -cne 'GET') { throw 'No permission revocation or directory writes are allowed.' }
            if ($url.Contains('/appRoleAssignments?')) {
                @{ value = @($(if ($f.present) { @{ principalId = $f.principal; resourceId = $f.graph; appRoleId = $f.role } })) }
            } else {
                @{ value = @(@{ id = $f.graph; appRoles = @(@{
                    id = $f.role; value = 'User.Read.All'; isEnabled = $true; allowedMemberTypes = @('Application')
                }) }) }
            }
            break
        }
        default { throw "Unexpected directory setup operation: $key" }
    }
    $value | ConvertTo-Json -Depth 100 -Compress
}

function python3 {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $f = $global:DocIntelDirectoryFixture
    $global:LASTEXITCODE = 0; $f.probes++
    foreach ($pair in @(@('--client', $f.client), @('--tenant', $f.directory), @('--owner', $f.owner), @('--image', $f.image))) {
        if ($Arguments[[Array]::IndexOf($Arguments, $pair[0]) + 1] -cne $pair[1]) { throw 'Runtime proof must use the core identity.' }
    }
    $status = if ($f.access -and $f.ownerEligible) { 'Connected' } elseif ($f.access) { 'ActionRequired' } else { 'AwaitingAdminApproval' }
    $code = if ($status -eq 'Connected') { 'None' } elseif ($f.access) { 'OwnerNotMember' } else { 'DirectoryForbidden' }
    @{ schemaVersion = 1; status = $status; failureCode = $code
        directoryEnabled = $f.enabled; runtimeIdentityVerified = $true
        ownerEligible = $f.ownerEligible -and -not $f.invalidProbe; searchVerified = $f.access } |
        ConvertTo-Json -Compress
}

function Invoke-WebRequest { return @{ StatusCode = 200 } }

function Preview-Directory([string]$DirectoryMode, [switch]$Required) {
    return & $file -StateDirectory $temporary -DirectoryMode $DirectoryMode -DirectoryRequired:$Required
}

function Apply-Directory([string]$DirectoryMode, [object]$Plan, [switch]$Consent, [switch]$Required) {
    return & $file -StateDirectory $temporary -DirectoryMode $DirectoryMode -DirectoryRequired:$Required `
        -Mode Apply -Confirmation CONNECT -ExpectedPlanHash $Plan.PlanHash `
        -ConsentConfirmation $(if ($Consent) { 'CONSENT' } else { '' })
}

function Assert-DirectoryFailure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch {
        $failed = $true
        if ($_.Exception.Message -notlike "*$Message*") { throw }
    }
    if (-not $failed) { throw "Expected directory setup failure: $Message." }
}

try {
    Reset-DirectoryFixture
    $plan = Preview-Directory Disabled
    $result = Apply-Directory Disabled $plan
    if ($result.Status -cne 'NotRequested' -or $f.writes.Count -or $f.probes) {
        throw 'Disabled setup must not read the directory, grant permissions or change the feature.'
    }
    Reset-DirectoryFixture
    $f.access = $true
    $plan = Preview-Directory UseExisting
    if ($plan.NeedsConsent -or $f.writes.Count) { throw 'Existing permission must not require new consent.' }
    $result = Apply-Directory UseExisting $plan
    if ($result.Status -cne 'Connected' -or $f.grants -or $f.updates -ne 1 -or -not $f.enabled) {
        throw 'Existing runtime access must activate only after successful proof.'
    }
    $f.writes.Clear()
    $plan = Preview-Directory PreserveExisting
    $result = Apply-Directory PreserveExisting $plan
    if ($f.writes.Count -or $result.Status -cne 'Connected') { throw 'Established connections must be preserved without mutation.' }
    Assert-DirectoryFailure { Preview-Directory Disabled } 'cannot be disabled'

    foreach ($scenario in @('grant', 'propagation', 'denied', 'graph-denied', 'required')) {
        Reset-DirectoryFixture
        $f.propagating = $scenario -eq 'propagation'; $f.grantDenied = $scenario -eq 'denied'
        $f.graphDenied = $scenario -eq 'graph-denied'
        $required = $scenario -eq 'required'
        $plan = Preview-Directory Configure -Required:$required
        if ($f.writes.Count) { throw 'Directory Preview must never mutate anything.' }
        if ($plan.NeedsConsent) {
            Assert-DirectoryFailure { Apply-Directory Configure $plan -Required:$required } 'CONSENT'
            if ($f.writes.Count) { throw 'Missing consent must prevent all mutations.' }
        }
        if ($required -and $f.graphDenied) { throw 'Invalid required scenario.' }
        $result = Apply-Directory Configure $plan -Consent -Required:$required
        if ($scenario -in @('grant', 'required')) {
            if ($result.Status -cne 'Connected' -or $f.grants -ne 1 -or $f.updates -ne 1) {
                throw 'Explicit consent must add only one read role, prove access and then activate.'
            }
        } elseif ($f.enabled -or $f.updates) {
            throw 'Pending/denied directory access must not enable the feature.'
        }
    }
    Reset-DirectoryFixture
    $f.present = $true
    $plan = Preview-Directory Configure
    $result = Apply-Directory Configure $plan
    if ($result.Status -cne 'PendingPropagation' -or $f.grants -or $f.enabled) {
        throw 'An existing but ineffective grant must be reported without duplication or premature activation.'
    }
    $plan = Preview-Directory UseExisting -Required
    Assert-DirectoryFailure { Apply-Directory UseExisting $plan -Required } 'Required directory'
    if ($f.enabled -or $f.grants) { throw 'Required failure must preserve the core app and permission state.' }
    Reset-DirectoryFixture
    $f.access = $true; $f.ownerEligible = $false
    $plan = Preview-Directory UseExisting
    $result = Apply-Directory UseExisting $plan
    if ($result.Status -cne 'ActionRequired' -or $f.writes.Count) { throw 'Ineligible initial owner must never be locked out by activation.' }
    Reset-DirectoryFixture
    $f.access = $true; $f.invalidProbe = $true
    $plan = Preview-Directory UseExisting
    $result = Apply-Directory UseExisting $plan
    if ($result.Status -cne 'ActionRequired' -or $f.writes.Count) { throw 'Invalid runtime proof must never activate.' }
    Reset-DirectoryFixture
    $f.access = $true
    $plan = Preview-Directory UseExisting
    $f.enabled = $true
    Assert-DirectoryFailure { Apply-Directory UseExisting $plan } 'plan changed'
    if ($f.writes.Count) { throw 'Stale directory approval must not mutate permissions or settings.' }
    Write-Output 'Directory preview, consent, runtime verification, activation and preservation tests passed.'
} finally {
    $env:GITHUB_STEP_SUMMARY = $previousSummary
    Remove-Item Function:\az, Function:\python3, Function:\Invoke-WebRequest -ErrorAction SilentlyContinue
    Remove-Variable DocIntelDirectoryFixture -Scope Global
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
