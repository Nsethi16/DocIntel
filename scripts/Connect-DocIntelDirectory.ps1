#Requires -Version 7.3
<#
.SYNOPSIS
    Preview and activate read-only organizational people lookup after core deployment.
.DESCRIPTION
    Graph User.Read.All is tenant-wide. Only Configure may add that one application permission,
    and only after explicit CONSENT plus approval of the exact directory plan. Existing grants
    are not removed and an enabled connection is never downgraded to hide a failure.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StateDirectory,
    [ValidateSet('Disabled', 'UseExisting', 'Configure', 'PreserveExisting')]
    [string]$DirectoryMode = 'PreserveExisting',
    [switch]$DirectoryRequired,
    [ValidateSet('Preview', 'Apply')][string]$Mode = 'Preview',
    [string]$ExpectedPlanHash = '',
    [string]$Confirmation = '',
    [string]$ConsentConfirmation = ''
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($DirectoryRequired -and $DirectoryMode -eq 'Disabled') {
    throw 'A required directory connection cannot be Disabled.'
}
if ($Mode -eq 'Apply' -and ($Confirmation -cne 'CONNECT' -or
    $ExpectedPlanHash -cnotmatch '^[a-f0-9]{64}$')) {
    throw 'Apply requires CONNECT and the exact reviewed directory plan hash.'
}
Import-Module (Join-Path $PSScriptRoot 'DocIntelRelease.psm1') -Force
$journal = Get-Content -LiteralPath (Join-Path $StateDirectory 'customer-installation.json') -Raw |
    ConvertFrom-Json -AsHashtable -Depth 100
if ($journal.SchemaVersion -ne 1 -or
    (Get-DocIntelObjectHash $journal.Request) -cne $journal.RequestHash) {
    throw 'A valid original customer installation journal is required.'
}
$request = $journal.Request
foreach ($id in @($request.Subscription, $request.Directory, $request.Owner)) {
    $value = [guid]::Empty
    if (-not [guid]::TryParse($id, [ref]$value) -or $value -eq [guid]::Empty) {
        throw 'The journal has invalid customer identity identifiers.'
    }
}
if ($request.Group -notmatch '^[A-Za-z0-9_().-]{1,90}$') { throw 'The customer group is invalid.' }
$subscription = [string]$request.Subscription
$directory = [string]$request.Directory
$group = [string]$request.Group
$privateFiles = [Collections.Generic.List[string]]::new()

function Invoke-DirectoryAzure([string[]]$Arguments) {
    $lines = & az @Arguments --subscription $subscription --only-show-errors --output json 2>&1
    if ($LASTEXITCODE -ne 0) {
        $code = 'AzureOperationFailed'
        foreach ($candidate in @('Authorization_RequestDenied', 'AuthorizationFailed',
            'InvalidAuthenticationToken', 'InsufficientPrivileges', 'Request_ResourceNotFound',
            'Request_BadRequest', 'Forbidden')) {
            if (($lines -join "`n") -match ('\b' + [regex]::Escape($candidate) + '\b')) {
                $code = $candidate; break
            }
        }
        return @{ Succeeded = $false; FailureCode = $code; Value = $null }
    }
    $text = $lines -join "`n"
    return @{ Succeeded = $true; FailureCode = 'None'
        Value = $(if ([String]::IsNullOrWhiteSpace($text)) { $null } else { $text | ConvertFrom-Json -Depth 100 }) }
}

function Read-DirectoryAzure([string[]]$Arguments) {
    $result = Invoke-DirectoryAzure $Arguments
    if (-not $result.Succeeded) {
        throw "Directory setup target inspection failed ($($result.FailureCode)). No activation is confirmed."
    }
    return $result.Value
}

function Invoke-DirectoryGraph([string]$Method, [string]$Path, [object]$Body = $null) {
    $arguments = @('rest', '--method', $Method, '--url', "https://graph.microsoft.com/v1.0/$Path")
    if ($null -ne $Body) {
        $file = Join-Path ([IO.Path]::GetTempPath()) ('docintel-directory-' + [guid]::NewGuid().ToString('N') + '.json')
        $privateFiles.Add($file)
        $Body | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $file -Encoding utf8
        $arguments += @('--body', "@$file")
    }
    return Invoke-DirectoryAzure $arguments
}

function Get-DirectoryRuntimeProbe {
    $python = Get-Command python3 -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command python -ErrorAction SilentlyContinue }
    if (-not $python) { return @{ status = 'ActionRequired'; failureCode = 'RuntimeProbeUnavailable' } }
    $text = & $python.Name (Join-Path $PSScriptRoot 'Invoke-DocIntelDirectoryProbe.py') `
        --subscription $subscription --resource-group $group --app $appName --image $image `
        --tenant $directory --client $runtimeClient --owner $request.Owner 2>&1
    if ($LASTEXITCODE -ne 0) { return @{ status = 'ActionRequired'; failureCode = 'RuntimeProbeUnavailable' } }
    try { $probe = ($text -join "`n") | ConvertFrom-Json -Depth 10 }
    catch [ArgumentException] { return @{ status = 'ActionRequired'; failureCode = 'RuntimeProbeUnavailable' } }
    $fields = @('schemaVersion', 'status', 'failureCode', 'directoryEnabled',
        'runtimeIdentityVerified', 'ownerEligible', 'searchVerified')
    if ($probe.schemaVersion -ne 1 -or @($probe.PSObject.Properties).Count -ne $fields.Count -or
        @($probe.PSObject.Properties.Name | Where-Object { $_ -notin $fields }).Count -or
        $probe.status -cnotin @('Connected', 'AwaitingAdminApproval', 'ActionRequired', 'TimedOut') -or
        $probe.failureCode -cnotin @('None', 'InvalidInput', 'TenantMismatch', 'RuntimeIdentityMismatch',
            'OwnerNotFound', 'OwnerDisabled', 'OwnerNotMember', 'OwnerNoWorkAddress',
            'DirectoryForbidden', 'DirectoryUnauthorized', 'DirectoryUnavailable', 'TimedOut') -or
        @('directoryEnabled', 'runtimeIdentityVerified', 'ownerEligible', 'searchVerified' |
            Where-Object { $probe.$_ -isnot [bool] }).Count) {
        return @{ status = 'ActionRequired'; failureCode = 'RuntimeProbeUnavailable' }
    }
    if ($probe.status -ceq 'Connected' -and ($probe.failureCode -cne 'None' -or
        -not $probe.runtimeIdentityVerified -or -not $probe.ownerEligible -or -not $probe.searchVerified)) {
        return @{ status = 'ActionRequired'; failureCode = 'RuntimeProbeUnavailable' }
    }
    return $probe
}

function Get-DirectoryGrant {
    $tenant = Invoke-DirectoryAzure @('account', 'get-access-token', '--resource-type', 'ms-graph', '--query', 'tenant')
    if (-not $tenant.Succeeded -or $tenant.Value -ine $directory) {
        return @{ Available = $false; FailureCode = 'AdministratorDirectoryMismatch' }
    }
    $graph = Invoke-DirectoryGraph GET (
        "servicePrincipals?`$filter=appId%20eq%20'00000003-0000-0000-c000-000000000000'&`$select=id,appRoles")
    if (-not $graph.Succeeded) { return @{ Available = $false; FailureCode = 'AdministratorApprovalRequired' } }
    $principals = @($graph.Value.value | Where-Object { $null -ne $_ })
    if ($principals.Count -ne 1 -or $graph.Value.'@odata.nextLink') {
        return @{ Available = $false; FailureCode = 'GraphPermissionDiscoveryFailed' }
    }
    $roles = @($principals[0].appRoles | Where-Object {
        $_.value -ceq 'User.Read.All' -and $_.isEnabled -eq $true -and 'Application' -cin $_.allowedMemberTypes
    })
    $roleId = [guid]::Empty
    $resourceId = [guid]::Empty
    if ($roles.Count -ne 1 -or -not [guid]::TryParse($roles[0].id, [ref]$roleId) -or
        -not [guid]::TryParse($principals[0].id, [ref]$resourceId) -or
        $roleId -eq [guid]::Empty -or $resourceId -eq [guid]::Empty) {
        return @{ Available = $false; FailureCode = 'GraphPermissionDiscoveryFailed' }
    }
    $path = "servicePrincipals/$runtimePrincipal/appRoleAssignments?`$select=id,principalId,resourceId,appRoleId&`$top=100"
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($page = 0; $page -lt 10; $page++) {
        if (-not $seen.Add($path)) { break }
        $assignments = Invoke-DirectoryGraph GET $path
        if (-not $assignments.Succeeded) {
            return @{ Available = $false; FailureCode = 'AdministratorApprovalRequired' }
        }
        if ($null -eq $assignments.Value.value) { break }
        if (@($assignments.Value.value | Where-Object {
            $_.principalId -ieq $runtimePrincipal -and $_.resourceId -ieq $resourceId.ToString() -and
                $_.appRoleId -ieq $roleId.ToString()
        }).Count) {
            return @{ Available = $true; Present = $true
                ResourceId = $resourceId.ToString(); AppRoleId = $roleId.ToString() }
        }
        $next = [string]$assignments.Value.'@odata.nextLink'
        if (-not $next) {
            return @{ Available = $true; Present = $false
                ResourceId = $resourceId.ToString(); AppRoleId = $roleId.ToString() }
        }
        $uri = [uri]$next
        if (-not $uri.IsAbsoluteUri -or $uri.Scheme -cne 'https' -or $uri.Host -cne 'graph.microsoft.com' -or
            $uri.AbsolutePath -cne "/v1.0/servicePrincipals/$runtimePrincipal/appRoleAssignments" -or
            $uri.Port -ne 443 -or $uri.UserInfo -or $uri.Fragment) { break }
        $path = $uri.PathAndQuery.Substring('/v1.0/'.Length)
    }
    return @{ Available = $false; FailureCode = 'GraphPermissionDiscoveryFailed' }
}

function Save-DirectoryResult([string]$Status, [string]$Code, [bool]$Enabled) {
    $result = [ordered]@{
        SchemaVersion = 1; Status = $Status; FailureCode = $Code; DirectoryEnabled = $Enabled
        DirectoryConnected = $Status -ceq 'Connected'; RequestedMode = $DirectoryMode
        Required = [bool]$DirectoryRequired; UpdatedAtUtc = [DateTime]::UtcNow.ToString('O')
    }
    $file = Join-Path $StateDirectory 'directory-result.json'
    $pending = Join-Path $StateDirectory ('directory-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $pending -Encoding utf8
        Move-Item -LiteralPath $pending -Destination $file -Force
    } finally { if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending } }
    Write-Host "Organization directory: $Status ($Code). No existing permissions were removed."
    if ($Status -notin @('Connected', 'NotRequested')) {
        Write-Warning 'Core deployment is retained, but organization directory setup is not complete.'
        if ($DirectoryRequired) { throw "Required directory connection is not ready ($Code)." }
    }
    return [pscustomobject]$result
}

try {
    $account = Read-DirectoryAzure @('account', 'show')
    if ($account.id -ine $subscription -or $account.tenantId -ine $directory) {
        throw 'The Azure account does not match the saved customer directory/subscription.'
    }
    $key = (Get-DocIntelObjectHash @($directory, $subscription, $group,
        $request.Customer, $request.Environment)).Substring(0, 16)
    $foundation = Read-DirectoryAzure @('deployment', 'sub', 'show', '--name', "docintel-$key-foundation")
    $deployment = Read-DirectoryAzure @('deployment', 'sub', 'show', '--name', "docintel-$key-application")
    foreach ($record in @($foundation, $deployment)) {
        if ($record.properties.provisioningState -cne 'Succeeded' -or
            $record.properties.parameters.installationRequestHash.value -cne $journal.RequestHash -or
            $record.properties.parameters.directoryTenantId.value -ine $directory -or
            $record.properties.parameters.initialOwnerObjectId.value -ine $request.Owner) {
            throw 'The core Azure deployment does not match the original customer installation.'
        }
    }
    $outputs = $foundation.properties.outputs
    $appName = [string]$outputs.containerAppName.value
    $runtimeId = [string]$outputs.managedIdentityResourceId.value
    $runtimeClient = [string]$outputs.managedIdentityClientId.value
    $runtimePrincipal = [string]$outputs.managedIdentityPrincipalId.value
    $image = [string]$deployment.properties.parameters.containerImage.value
    if ($appName -cnotmatch '^[a-z][a-z0-9-]{1,30}[a-z0-9]$' -or
        $image -cnotmatch '^[a-z0-9.-]+/[a-z0-9/-]+@sha256:[a-f0-9]{64}$' -or
        $runtimeId -notlike "/subscriptions/$subscription/resourceGroups/$group/providers/Microsoft.ManagedIdentity/userAssignedIdentities/*") {
        throw 'The core image or runtime identity binding is unsupported.'
    }
    foreach ($id in @($runtimeClient, $runtimePrincipal)) {
        $value = [guid]::Empty
        if (-not [guid]::TryParse($id, [ref]$value) -or $value -eq [guid]::Empty) {
            throw 'The core runtime identity is invalid.'
        }
    }
    $app = Read-DirectoryAzure @('containerapp', 'show', '--resource-group', $group, '--name', $appName)
    $containers = @($app.properties.template.containers | Where-Object name -CEQ 'docintel-api')
    $identity = @($app.identity.userAssignedIdentities.PSObject.Properties | Where-Object Name -IEQ $runtimeId)
    if ($containers.Count -ne 1 -or $containers[0].image -cne $image -or $identity.Count -ne 1 -or
        $identity[0].Value.clientId -ine $runtimeClient -or $identity[0].Value.principalId -ine $runtimePrincipal) {
        throw 'The live app no longer uses the saved image/runtime identity. No connection change was made.'
    }
    $settings = @{}
    foreach ($entry in $containers[0].env) { $settings[$entry.name] = $entry }
    foreach ($entry in @{
        AZURE_CLIENT_ID = $runtimeClient; Identity__ManagedIdentityClientId = $runtimeClient
        Identity__DirectoryTenantId = $directory; Identity__ClientId = $foundation.properties.parameters.webClientId.value
        Identity__ApiClientId = $foundation.properties.parameters.apiClientId.value; Identity__Mode = 'Entra'
    }.GetEnumerator()) {
        if ($settings[$entry.Key].secretRef -or $settings[$entry.Key].value -cne $entry.Value) {
            throw 'The application identity configuration changed. Directory setup will not repoint it.'
        }
    }
    $flag = $settings['Identity__Directory__Enabled']
    if ($flag.secretRef -or ($flag.value -and $flag.value -cnotin @('true', 'false'))) {
        throw 'The directory feature flag cannot be safely inspected.'
    }
    $enabled = $flag.value -ceq 'true'
    if ($DirectoryMode -eq 'Disabled' -and $enabled) {
        throw 'An established directory connection cannot be disabled by this deployment helper.'
    }
    $action = 'NoChange'
    $status = 'NotRequested'
    $code = 'None'
    $probe = $null
    $grant = $null
    $requested = $DirectoryMode -in @('UseExisting', 'Configure') -or $enabled -or $DirectoryRequired
    if ($requested) {
        $probe = Get-DirectoryRuntimeProbe
        if ($probe.status -ceq 'Connected') {
            $enabled = [bool]$probe.directoryEnabled
            $status = 'Connected'
            if (-not $enabled) { $action = 'Enable'; $status = 'ReadyToConnect' }
        } else {
            $status = if ($enabled) { 'ActionRequired' } else { $probe.status }
            $code = $probe.failureCode
            if ($DirectoryMode -eq 'Configure' -and $probe.failureCode -ceq 'DirectoryForbidden') {
                $grant = Get-DirectoryGrant
                if (-not $grant.Available) { $status = 'AwaitingAdminApproval'; $code = $grant.FailureCode }
                elseif ($grant.Present) { $status = 'PendingPropagation'; $code = 'RuntimePermissionNotEffective' }
                else { $action = 'GrantThenVerify'; $status = 'AwaitingAdminApproval' }
            }
        }
    }
    $plan = @{
        SchemaVersion = 1; DirectoryMode = $DirectoryMode; Required = [bool]$DirectoryRequired
        Action = $action; Status = $status; FailureCode = $code; PreviousEnabled = $enabled
        Subscription = $subscription; Group = $group; Directory = $directory; App = $appName; Image = $image
        RuntimeResource = $runtimeId; RuntimeClient = $runtimeClient; RuntimePrincipal = $runtimePrincipal
        Owner = $request.Owner; Permission = 'User.Read.All'; PermissionScope = 'Tenant-wide read-only directory access'
        Grant = $grant; Probe = $probe; CoreRequestHash = $journal.RequestHash
        HelperHash = (Get-FileHash $PSCommandPath -Algorithm SHA256).Hash
        ProbeHash = (Get-FileHash (Join-Path $PSScriptRoot 'Invoke-DocIntelDirectoryProbe.py') -Algorithm SHA256).Hash
        Revision = $app.properties.latestRevisionName
    }
    $planHash = Get-DocIntelObjectHash $plan
    if ($Mode -eq 'Apply' -and $ExpectedPlanHash -cne $planHash) {
        throw 'The directory plan changed. Nothing was changed; review a new directory preview.'
    }
    @{ PlanHash = $planHash; Plan = $plan } | ConvertTo-Json -Depth 50 |
        Set-Content -LiteralPath (Join-Path $StateDirectory 'directory-plan.json') -Encoding utf8
    if ($requested -and $probe.failureCode -ceq 'DirectoryForbidden') {
        $consentRequest = @{ SchemaVersion = 1; DirectoryTenantId = $directory
            RuntimePrincipalId = $runtimePrincipal; RuntimeResourceId = $runtimeId
            Permission = 'User.Read.All'; CoreRequestHash = $journal.RequestHash }
        @{ Request = $consentRequest; RequestHash = Get-DocIntelObjectHash $consentRequest } |
            ConvertTo-Json -Depth 20 |
            Set-Content -LiteralPath (Join-Path $StateDirectory 'directory-consent-request.json') -Encoding utf8
    }
    if ($Mode -eq 'Preview') {
        Write-Host "Directory plan: $action; $status. Permission scope: tenant-wide User.Read.All, no mail or write access."
        return [pscustomobject]@{ PlanHash = $planHash; Action = $action; Status = $status
            NeedsConsent = $action -ceq 'GrantThenVerify'; MutationPlanned = $action -in @('Enable', 'GrantThenVerify') }
    }
    if ($action -eq 'GrantThenVerify') {
        if ($DirectoryMode -ne 'Configure' -or $ConsentConfirmation -cne 'CONSENT') {
            throw 'Assigning tenant-wide User.Read.All requires explicit CONSENT for the reviewed runtime identity.'
        }
        $created = Invoke-DirectoryGraph POST "servicePrincipals/$($grant.ResourceId)/appRoleAssignedTo" @{
            principalId = $runtimePrincipal; resourceId = $grant.ResourceId; appRoleId = $grant.AppRoleId
        }
        if (-not $created.Succeeded) {
            return Save-DirectoryResult 'AwaitingAdminApproval' 'AdministratorApprovalRequired' $enabled
        }
        if ($created.Value.principalId -ine $runtimePrincipal -or
            $created.Value.resourceId -ine $grant.ResourceId -or $created.Value.appRoleId -ine $grant.AppRoleId) {
            return Save-DirectoryResult 'ActionRequired' 'GrantReadbackFailed' $enabled
        }
        $probe = Get-DirectoryRuntimeProbe
        if ($probe.status -cne 'Connected') {
            return Save-DirectoryResult 'PendingPropagation' $probe.failureCode $enabled
        }
        $action = if ($probe.directoryEnabled) { 'NoChange' } else { 'Enable' }
        $status = 'Connected'; $code = 'None'
    }
    if ($action -eq 'Enable') {
        $updated = Invoke-DirectoryAzure @('containerapp', 'update', '--resource-group', $group, '--name', $appName,
            '--container-name', 'docintel-api', '--set-env-vars', 'Identity__Directory__Enabled=true')
        if (-not $updated.Succeeded) { return Save-DirectoryResult 'ActionRequired' 'ActivationNotConfirmed' $enabled }
        $enabled = $true
        try {
            & (Join-Path $PSScriptRoot 'Wait-DocIntelReady.ps1') -SubscriptionId $subscription `
                -ResourceGroup $group -ContainerAppName $appName -ContainerName docintel-api -ExpectedImage $image | Out-Host
        } catch [System.Management.Automation.RuntimeException] {
            return Save-DirectoryResult 'ActionRequired' 'ActivationNotConfirmed' $enabled
        }
        $probe = Get-DirectoryRuntimeProbe
        if ($probe.status -cne 'Connected' -or -not $probe.directoryEnabled) {
            return Save-DirectoryResult 'ActionRequired' 'ActivationNotConfirmed' $enabled
        }
        $status = 'Connected'; $code = 'None'
    }
    return Save-DirectoryResult $status $code $enabled
}
finally {
    foreach ($file in $privateFiles) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
}
