#Requires -Version 7.3
<#
.SYNOPSIS
    Preview, install or safely resume a customer installation using a public, digest-pinned image.
.DESCRIPTION
    No mutation occurs in Preview. Apply requires the reviewed plan hash and APPLY confirmation.
    Existing resources require the original journal and matching completed Azure foundation.
    This is not an arbitrary-resource adoption or upgrade tool.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CustomerSlug,
    [string]$Image = '',
    [ValidateSet('PublicImage', 'CustomerRegistryBuild')][string]$ImageSource = 'PublicImage',
    [string]$SourceCommit = '',
    [string]$CustomerName = '',
    [string]$Location = 'westus',
    [string]$EnvironmentName = 'prod',
    [string]$SubscriptionId = '',
    [string]$ResourceGroup = '',
    [string]$InitialOwnerObjectId = '',
    [ValidateSet('Preview', 'Apply')][string]$Mode = 'Preview',
    [string]$Confirmation = '',
    [string]$ExpectedPlanHash = '',
    [string]$OutputDirectory = '',
    [switch]$Resume,
    [switch]$RetryFailedBuild
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Test-CustomerGuid([string]$Value) {
    $parsed = [guid]::Empty
    return [guid]::TryParse($Value, [ref]$parsed) -and $parsed -ne [guid]::Empty
}

if ($CustomerSlug.Length -lt 3 -or $CustomerSlug.Length -gt 16 -or
    $CustomerSlug -cnotmatch '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$') {
    throw 'CustomerSlug must be 3-16 lowercase letters/digits with single, internal hyphens; start with a letter.'
}
if ($EnvironmentName -cnotmatch '^[a-z0-9]{2,8}$') { throw 'EnvironmentName must contain 2-8 lowercase letters/digits.' }
if ($ImageSource -eq 'PublicImage') {
    if ($Image -cnotmatch '^ghcr\.io/nsethi16/docintel-api@(?<digest>sha256:[a-f0-9]{64})$') {
        throw 'Image must be the reviewed public ghcr.io/nsethi16/docintel-api image pinned by SHA-256 digest, not a tag.'
    }
    $digest = $Matches.digest
} elseif ($Image -or $SourceCommit -cnotmatch '^[a-f0-9]{40}$') {
    throw 'CustomerRegistryBuild requires a full SourceCommit and no preselected image.'
}
$resolvedImage = $Image
if ($RetryFailedBuild -and (-not $Resume -or $ImageSource -ne 'CustomerRegistryBuild')) {
    throw 'RetryFailedBuild requires Resume of a customer registry build and a new reviewed APPLY.'
}
if (-not $CustomerName) { $CustomerName = $CustomerSlug }
if ($CustomerName.Length -gt 120 -or [String]::IsNullOrWhiteSpace($CustomerName) -or
    $CustomerName -match '[\x00-\x1f\x7f]') { throw 'CustomerName must be 1-120 printable characters.' }
# Published CU region list, reviewed 2026-10-26; model availability/quota are separately checked below.
$regions = @('australiaeast', 'canadacentral', 'eastus', 'eastus2', 'japaneast', 'southcentralus',
    'southeastasia', 'swedencentral', 'uksouth', 'westeurope', 'westus', 'westus3')
if ($Location -cnotin $regions) { throw 'Location is not in the reviewed Content Understanding region list.' }
foreach ($entry in @{ SubscriptionId = $SubscriptionId; InitialOwnerObjectId = $InitialOwnerObjectId }.GetEnumerator()) {
    if ($entry.Value -and -not (Test-CustomerGuid $entry.Value)) { throw "$($entry.Key) must be a nonempty GUID." }
}
if (-not $ResourceGroup) { $ResourceGroup = "rg-docintel-$CustomerSlug-$EnvironmentName" }
if ($ResourceGroup -notmatch '^[A-Za-z0-9_().-]{1,90}$' -or $ResourceGroup.EndsWith('.')) {
    throw 'ResourceGroup is invalid.'
}
if ($Mode -eq 'Apply' -and ($Confirmation -cne 'APPLY' -or $ExpectedPlanHash -cnotmatch '^[a-f0-9]{64}$')) {
    throw 'Apply requires Confirmation=APPLY and the exact ExpectedPlanHash from the reviewed Preview.'
}
$root = Split-Path $PSScriptRoot -Parent
$template = Join-Path $root 'infra\customer.bicep'
if (Test-Path -LiteralPath (Join-Path $root 'infra\customer.json')) { $template = Join-Path $root 'infra\customer.json' }
$privateFiles = [Collections.Generic.List[string]]::new()
$receipt = @{ Status = 'Incomplete'; Stage = 'Preflight'; Customer = $CustomerSlug; ResourceGroup = $ResourceGroup }
$writeReceipt = $false
$journal = $null
if ($Resume) {
    if (-not $OutputDirectory -or
        -not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'customer-installation.json'))) {
        throw 'Resume requires the original setup state directory and customer-installation.json.'
    }
    $journal = Get-Content -LiteralPath (Join-Path $OutputDirectory 'customer-installation.json') -Raw |
        ConvertFrom-Json -AsHashtable -Depth 100
    if ($journal.SchemaVersion -ne 1) { throw 'The installation journal format is unsupported.' }
    if (-not $InitialOwnerObjectId) { $InitialOwnerObjectId = $journal.Request.Owner }
}

function Invoke-CustomerAzure([string[]]$Arguments, [switch]$AllowMissingDeployment) {
    $scope = if ($SubscriptionId) { @('--subscription', $SubscriptionId) } else { @() }
    $lines = & az @Arguments @scope --only-show-errors --output json 2>&1
    if ($LASTEXITCODE -ne 0) {
        $code = 'UnclassifiedAzureError'
        $errorText = $lines -join "`n"
        if ($AllowMissingDeployment -and ($Arguments[0..2] -join ' ') -ceq 'deployment sub show' -and
            $errorText -match '\bDeploymentNotFound\b') {
            Write-Verbose 'The foundation command was not admitted; the group must still pass the empty-group guard.'
            return $null
        }
        foreach ($candidate in @('Authorization_RequestDenied', 'AuthorizationFailed',
            'InvalidAuthenticationToken', 'Request_BadRequest', 'InvalidResourceName',
            'InvalidTemplateDeployment', 'InvalidTemplate', 'InsufficientQuota',
            'MissingSubscriptionRegistration', 'RoleAssignmentUpdateNotPermitted', 'Forbidden', 'NotFound')) {
            if ($errorText -match ('\b' + [regex]::Escape($candidate) + '\b')) { $code = $candidate; break }
        }
        $operation = $Arguments[0..([Math]::Min(2, $Arguments.Length - 1))] -join ' '
        throw "Azure operation '$operation' failed ($code). Setup is incomplete; no raw credential/provider payload is logged."
    }
    $text = $lines -join "`n"
    if ([String]::IsNullOrWhiteSpace($text)) { return $null }
    return $text | ConvertFrom-Json -Depth 100
}

function Write-CustomerParameterFile([object]$Value) {
    $file = Join-Path ([IO.Path]::GetTempPath()) ('docintel-customer-' + [guid]::NewGuid().ToString('N') + '.json')
    $privateFiles.Add($file)
    $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $file -Encoding utf8
    return $file
}

function ConvertTo-CustomerCanonicalJson([object]$Value) {
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [Collections.IDictionary]) {
        $fields = foreach ($key in $Value.Keys | Sort-Object -CaseSensitive) {
            ($key | ConvertTo-Json -Compress) + ':' + (ConvertTo-CustomerCanonicalJson $Value[$key])
        }
        return '{' + ($fields -join ',') + '}'
    }
    if ($Value -is [pscustomobject]) {
        $fields = foreach ($property in $Value.PSObject.Properties | Sort-Object Name -CaseSensitive) {
            ($property.Name | ConvertTo-Json -Compress) + ':' + (ConvertTo-CustomerCanonicalJson $property.Value)
        }
        return '{' + ($fields -join ',') + '}'
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        return '[' + (@($Value | ForEach-Object { ConvertTo-CustomerCanonicalJson $_ }) -join ',') + ']'
    }
    return $Value | ConvertTo-Json -Compress
}

function Get-CustomerHash([object]$Value) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes((ConvertTo-CustomerCanonicalJson $Value)))).ToLowerInvariant()
}

function Save-CustomerJournal {
    $path = Join-Path $OutputDirectory 'customer-installation.json'
    $pending = Join-Path $OutputDirectory ('journal-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $journal | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $pending -Encoding utf8
        Move-Item -LiteralPath $pending -Destination $path -Force
    } finally {
        if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending }
    }
}

function Get-CustomerRegistry {
    $outputs = $foundation.properties.outputs
    $name = [string]$outputs.containerRegistryName.value
    $server = [string]$outputs.containerRegistryLoginServer.value
    $id = [string]$outputs.containerRegistryResourceId.value
    $expected = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.ContainerRegistry/registries/$name"
    if ($name -cnotmatch '^[a-z0-9]{5,50}$' -or $server -cne "$name.azurecr.io" -or $id -ine $expected) {
        throw 'The build registry is not the validated customer foundation registry.'
    }
    return @{ Name = $name; Server = $server; Id = $id
        Tag = "source-$($SourceCommit.Substring(0, 12))-$($requestHash.Substring(0, 12))" }
}

function Read-CustomerBuildRun([hashtable]$Registry, [string]$RunId) {
    if ($RunId -cnotmatch '^[A-Za-z0-9_-]{1,80}$') { throw 'The saved registry build run ID is invalid.' }
    $run = Invoke-CustomerAzure @('rest', '--method', 'GET', '--url',
        "https://management.azure.com$($Registry.Id)/runs/${RunId}?api-version=2019-04-01")
    if ($run.id -ine "$($Registry.Id)/runs/$RunId" -or $run.properties.runId -cne $RunId -or
        $run.properties.runType -cne 'QuickBuild') { throw 'The registry run is not the saved customer build.' }
    return $run
}

function Read-CustomerBuild([hashtable]$Registry, [string]$RunId) {
    $run = Read-CustomerBuildRun $Registry $RunId
    $status = [string]$run.properties.status
    if ($status -in @('Queued', 'Started', 'Running')) { return $null }
    if ($status -cnotin @('Succeeded', 'Failed', 'Canceled', 'Error', 'Timeout')) {
        throw 'The registry returned an unknown build status. No application was deployed.'
    }
    if ($status -cne 'Succeeded') {
        throw "The customer image build finished with status '$status'. Inspect the scoped ACR run; no app was deployed."
    }
    $images = @($run.properties.outputImages | Where-Object { $null -ne $_ })
    if ($images.Count -ne 1 -or $images[0].registry -ine $Registry.Server -or
        $images[0].repository -cne 'docintel-api' -or $images[0].tag -cne $Registry.Tag -or
        $images[0].digest -cnotmatch '^sha256:[a-f0-9]{64}$' -or
        $run.properties.platform.os -cne 'Linux' -or $run.properties.platform.architecture -cne 'amd64') {
        throw 'The build did not return exactly the approved Linux AMD64 repository/tag and SHA-256 digest.'
    }
    return "$($Registry.Server)/docintel-api@$($images[0].digest)"
}

function Resolve-CustomerBuild([switch]$ApplyBuild) {
    $registry = Get-CustomerRegistry
    if ($groupTags['docintel-image-digest']) {
        if ($RetryFailedBuild) { throw 'A completed customer image must not be rebuilt during resume.' }
        $builtDigest = [string]$groupTags['docintel-image-digest']
        if ($builtDigest -cnotmatch '^sha256:[a-f0-9]{64}$') { throw 'The customer image receipt is invalid.' }
        $bound = "$($registry.Server)/docintel-api@$builtDigest"
        if ($journal.ResolvedImage -and $journal.ResolvedImage -cne $bound) {
            throw 'The customer image receipt changed. Resume never replaces its approved build.'
        }
        return $bound
    }
    $runId = [string]$journal.BuildRunId
    if ($groupTags['docintel-build-run'] -and $groupTags['docintel-build-run'] -cne $runId) {
        throw 'The customer registry run binding changed.'
    }
    if ($RetryFailedBuild) {
        if (-not $runId) { throw 'There is no recorded failed build to retry.' }
        $previous = Read-CustomerBuildRun $registry $runId
        if ($previous.properties.status -cnotin @('Failed', 'Canceled', 'Error', 'Timeout')) {
            throw 'RetryFailedBuild only supports a confirmed terminal failed run, never an active or successful build.'
        }
        if (-not $ApplyBuild) { return $null }
        $journal.PreviousBuildRunIds = @($journal.PreviousBuildRunIds | Where-Object { $_ }) + @($runId)
        $journal.BuildRunId = ''
        Save-CustomerJournal
        $runId = ''
    }
    if (-not $runId) {
        if (-not $ApplyBuild) { return $null }
        $runId = [string](Invoke-CustomerAzure @('acr', 'build', '--registry', $registry.Name,
            '--resource-group', $ResourceGroup, '--file', 'Dockerfile', '--image', "docintel-api:$($registry.Tag)",
            '--platform', 'linux/amd64', '--timeout', '1200', '--no-logs', '--no-wait', '--query', 'runId',
            "https://github.com/Nsethi16/DocIntel.git#$SourceCommit"))
        if ($runId -cnotmatch '^[A-Za-z0-9_-]{1,80}$') { throw 'ACR did not return a valid build run ID.' }
        $journal.BuildRunId = $runId
        Save-CustomerJournal
    }
    $attempts = if ($ApplyBuild) { 60 } else { 1 }
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        $built = Read-CustomerBuild $registry $runId
        if ($built) {
            if ($ApplyBuild) {
                $builtDigest = ($built -split '@', 2)[1]
                $tagged = Invoke-CustomerAzure @('tag', 'update', '--resource-id',
                    "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup", '--operation', 'Merge',
                    '--tags', "docintel-build-run=$runId", "docintel-image-digest=$builtDigest")
                if ($tagged.properties.tags.'docintel-image-digest' -cne $builtDigest -or
                    $tagged.properties.tags.'docintel-build-run' -cne $runId) {
                    throw 'The customer build receipt was not persisted in Azure.'
                }
                $groupTags['docintel-build-run'] = $runId
                $groupTags['docintel-image-digest'] = $builtDigest
                $journal.ResolvedImage = $built
                Save-CustomerJournal
            }
            return $built
        }
        if ($ApplyBuild -and $attempt + 1 -lt $attempts) { Start-Sleep -Seconds 10 }
    }
    if ($ApplyBuild) {
        throw 'The customer build is still running. Re-run the same setup bundle to resume; no duplicate build is queued.'
    }
    return $null
}

function Assert-CustomerRuntime([object]$App) {
    $containers = @($App.properties.template.containers | Where-Object name -CEQ 'docintel-api')
    if ($containers.Count -ne 1 -or -not $resolvedImage -or $containers[0].image -cne $resolvedImage) {
        throw 'The existing application does not match the saved image. Resume never upgrades or overwrites it.'
    }
    $settings = @{}
    foreach ($setting in $containers[0].env) { $settings[$setting.name] = $setting }
    $runtimeId = [string]$foundation.properties.outputs.managedIdentityResourceId.value
    $runtimeClient = [string]$foundation.properties.outputs.managedIdentityClientId.value
    $runtimePrincipal = [string]$foundation.properties.outputs.managedIdentityPrincipalId.value
    $identityScope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.ManagedIdentity/userAssignedIdentities/"
    $assigned = @($App.identity.userAssignedIdentities.PSObject.Properties | Where-Object Name -IEQ $runtimeId)
    if (-not $runtimeId.StartsWith($identityScope, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-CustomerGuid $runtimeClient) -or -not (Test-CustomerGuid $runtimePrincipal) -or
        $assigned.Count -ne 1 -or $assigned[0].Value.clientId -ine $runtimeClient -or
        $assigned[0].Value.principalId -ine $runtimePrincipal) {
        throw 'The assigned runtime identity does not match the saved Azure foundation.'
    }
    if ($ImageSource -eq 'CustomerRegistryBuild') {
        $registry = Get-CustomerRegistry
        $pullBindings = @($App.properties.configuration.registries | Where-Object server -CEQ $registry.Server)
        if ($pullBindings.Count -ne 1 -or $pullBindings[0].identity -ine $runtimeId -or
            $pullBindings[0].username -or $pullBindings[0].passwordSecretRef) {
            throw 'The customer registry pull binding must select the validated runtime managed identity.'
        }
    }
    foreach ($entry in @{
        Identity__DirectoryTenantId = $directoryId; Identity__ClientId = $web.appId
        Identity__ApiClientId = $api.appId; Identity__PublicOrigin = $foundation.properties.outputs.publicOrigin.value
        HarnessBootstrap__TenantId = $CustomerSlug
        AZURE_CLIENT_ID = $runtimeClient; Identity__ManagedIdentityClientId = $runtimeClient
    }.GetEnumerator()) {
        if ($settings[$entry.Key].secretRef -or $settings[$entry.Key].value -cne $entry.Value) {
            throw 'The application identity/workspace binding changed. Resume will not repoint an existing application.'
        }
    }
    if ($settings.DOCINTEL_IDENTITY_GRANTS.secretRef) { throw 'Resume cannot inspect secret-backed owner configuration.' }
    $grants = $settings.DOCINTEL_IDENTITY_GRANTS.value | ConvertFrom-Json -Depth 20
    if (-not @($grants.PlatformAdministrators | Where-Object {
        $_.DirectoryTenantId -ieq $directoryId -and $_.ObjectId -ieq $InitialOwnerObjectId
    }).Count) { throw 'The selected owner no longer has the saved platform grant. Explicit operator recovery is required.' }
    return @{ Image = $containers[0].image; Directory = $directoryId; Web = $web.appId; Api = $api.appId
        RuntimeResource = $runtimeId; RuntimeClient = $runtimeClient; RuntimePrincipal = $runtimePrincipal
        OwnerGrantsHash = Get-CustomerHash $grants; Revision = $App.properties.latestRevisionName }
}

function Complete-CustomerReadiness([object]$Outputs) {
    $arguments = @{
        SubscriptionId = $SubscriptionId; ResourceGroup = $ResourceGroup
        ContainerAppName = $Outputs.containerAppName.value; ContainerName = 'docintel-api'
    }
    & (Join-Path $PSScriptRoot 'Wait-DocIntelReady.ps1') @arguments -ConfigurationOnly
    & (Join-Path $PSScriptRoot 'Wait-DocIntelReady.ps1') @arguments -ExpectedImage $resolvedImage
    $receipt.Status = 'InfrastructureReady'
    $receipt.WorkspaceUrl = [string]$Outputs.workspaceUrl.value
    $receipt.PublicOrigin = [string]$Outputs.publicOrigin.value
    $receipt.Image = $resolvedImage
    Write-Host "Customer workspace ready for sign-in: $($receipt.WorkspaceUrl)"
    Write-Host 'Complete acceptance with a representative PDF and an Ask AI answer with source citations.'
}

function Invoke-CustomerGraph([string]$Method, [string]$Path, [object]$Body = $null) {
    $arguments = @('rest', '--method', $Method, '--url', ('https://graph.microsoft.com/v1.0/' + $Path))
    if ($null -ne $Body) { $arguments += @('--body', ('@' + (Write-CustomerParameterFile $Body))) }
    return Invoke-CustomerAzure $arguments
}

function Get-CustomerApplication([string]$Name) {
    $filter = [uri]::EscapeDataString("displayName eq '$Name'")
    $response = Invoke-CustomerGraph GET ('applications?$filter=' + $filter +
        '&$select=id,appId,displayName,tags,signInAudience,web,api,identifierUris,requiredResourceAccess,appRoles')
    if ($null -eq $response.value -or $response.'@odata.nextLink') { throw 'Application discovery is incomplete or ambiguous.' }
    $apps = @($response.value | Where-Object { $null -ne $_ })
    if ($apps.Count -gt 1) { throw 'Multiple matching applications exist. Operator review is required; setup will not guess.' }
    if (-not $apps.Count) { return $null }
    $app = $apps[0]
    if (-not (Test-CustomerGuid $app.id) -or -not (Test-CustomerGuid $app.appId) -or
        $app.signInAudience -cne 'AzureADMyOrg' -or $installationTag -cnotin $app.tags) {
        throw 'A matching application is not owned by this installation. It will not be adopted or overwritten.'
    }
    return $app
}

function New-CustomerApplication([string]$Name) {
    $app = Invoke-CustomerGraph POST 'applications' @{
        displayName = $Name; signInAudience = 'AzureADMyOrg'; tags = @($installationTag)
    }
    if (-not (Test-CustomerGuid $app.id) -or -not (Test-CustomerGuid $app.appId)) {
        throw 'Application creation did not return valid identifiers.'
    }
    return $app
}

function Assert-CustomerChanges([object]$WhatIf, [switch]$ApplicationOnly) {
    if ($WhatIf.status -cne 'Succeeded' -or -not @($WhatIf.changes | Where-Object { $null -ne $_ }).Count -or
        @($WhatIf.diagnostics | Where-Object { $null -ne $_ }).Count -or
        @($WhatIf.potentialChanges | Where-Object { $null -ne $_ }).Count) {
        throw 'ARM what-if analysis is incomplete or contains unvalidated potential changes. No approval is inferred.'
    }
    $groupId = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup"
    foreach ($change in $WhatIf.changes) {
        if ($change.changeType -notin @('Create', 'Modify', 'NoChange', 'Ignore', 'Deploy')) {
            throw 'The plan contains a deletion or unsupported change. Operator review is required.'
        }
        if ($change.changeType -eq 'Ignore') {
            if (-not $ApplicationOnly -or $change.resourceId -match '/providers/Microsoft.Resources/deployments/') {
                throw 'ARM ignored a new resource or nested deployment. Complete analysis is required.'
            }
            continue
        }
        if ($change.changeType -eq 'NoChange') { continue }
        if ($change.resourceId -ieq $groupId) {
            if ($ApplicationOnly -and $change.changeType -ne 'NoChange') {
                throw 'The second phase must not change the resource group.'
            }
            continue
        }
        if (-not $change.resourceId.StartsWith($groupId + '/', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'ARM proposed a change outside the selected customer resource group.'
        }
        if ($change.resourceId -match '/providers/Microsoft.Resources/deployments/') { continue }
        if ($change.changeType -eq 'Modify') { throw 'Fresh installation must not modify existing customer resources.' }
        if ($ApplicationOnly -and $change.resourceId -notmatch '/providers/Microsoft.App/containerApps/[^/]+$') {
            throw 'The second phase may only create the application, not replace missing shared infrastructure.'
        }
    }
}

try {
    $account = Invoke-CustomerAzure @('account', 'show')
    if (-not (Test-CustomerGuid $account.id) -or -not (Test-CustomerGuid $account.tenantId)) {
        throw 'Sign in to the customer Azure subscription before running setup.'
    }
    $SubscriptionId = $account.id
    $directoryId = $account.tenantId
    $files = @('infra\main.bicep', 'infra\customer.bicep', 'scripts\Install-DocIntelCustomer.ps1',
        'scripts\Wait-DocIntelReady.ps1')
    if ($template.EndsWith('.json', [StringComparison]::Ordinal)) { $files += 'infra\customer.json' }
    $fileHashes = @{}
    foreach ($file in $files) { $fileHashes[$file] = (Get-FileHash (Join-Path $root $file) -Algorithm SHA256).Hash }
    $graphTenant = Invoke-CustomerAzure @('account', 'get-access-token', '--resource-type', 'ms-graph', '--query', 'tenant')
    if ($graphTenant -ine $directoryId) { throw 'The Graph credential and customer subscription must use the same directory.' }
    if (-not $InitialOwnerObjectId) {
        $me = Invoke-CustomerGraph GET 'me?$select=id'
        $InitialOwnerObjectId = $me.id
    }
    if (-not (Test-CustomerGuid $InitialOwnerObjectId)) { throw 'Select an explicit directory user as initial owner.' }
    $owner = Invoke-CustomerGraph GET ("users/$InitialOwnerObjectId" + '?$select=id,accountEnabled')
    if ($owner.id -ine $InitialOwnerObjectId -or $owner.accountEnabled -ne $true) {
        throw 'The initial owner must be an enabled user in the customer directory.'
    }
    $request = @{
        Subscription = $SubscriptionId; Directory = $directoryId; Group = $ResourceGroup; Location = $Location
        Customer = $CustomerSlug; Name = $CustomerName; Environment = $EnvironmentName
        Owner = $InitialOwnerObjectId; Image = $Image; ImageSource = $ImageSource
        SourceCommit = $SourceCommit; Files = $fileHashes
    }
    $requestHash = Get-CustomerHash $request
    $key = (Get-CustomerHash @($directoryId, $SubscriptionId, $ResourceGroup, $CustomerSlug, $EnvironmentName)).Substring(0, 16)
    $foundation = $null
    if ($Resume) {
        if ($journal.RequestHash -cne $requestHash -or (Get-CustomerHash $journal.Request) -cne $requestHash) {
            throw 'Resume requires the original customer, owner, region, image and installer version. Use the original release bundle.'
        }
        if ($journal.FoundationStarted) {
            $foundation = Invoke-CustomerAzure @('deployment', 'sub', 'show', '--name', "docintel-$key-foundation") `
                -AllowMissingDeployment
            if ($foundation) {
            if ($foundation.properties.provisioningState -cne 'Succeeded') {
                throw 'The foundation is still running or partially failed. An operator must resolve that Azure deployment before resume.'
            }
            foreach ($entry in $journal.FoundationParameters.GetEnumerator()) {
                if ((Get-CustomerHash $foundation.properties.parameters.($entry.Key).value) -cne
                    (Get-CustomerHash $entry.Value.value)) {
                    throw 'The saved foundation does not match the live Azure deployment. No existing resources will be adopted.'
                }
            }
            if ($foundation.properties.parameters.installationRequestHash.value -cne $requestHash) {
                throw 'The Azure foundation belongs to a different installation request.'
            }
            }
        }
    }
    $providers = @('Microsoft.App', 'Microsoft.OperationalInsights', 'Microsoft.ManagedIdentity',
        'Microsoft.Storage', 'Microsoft.DocumentDB', 'Microsoft.KeyVault', 'Microsoft.CognitiveServices')
    if ($ImageSource -eq 'CustomerRegistryBuild') { $providers += 'Microsoft.ContainerRegistry' }
    foreach ($provider in $providers) {
        $state = Invoke-CustomerAzure @('provider', 'show', '--namespace', $provider, '--query', 'registrationState')
        if ($state -cne 'Registered') { throw "Have the Azure administrator register $provider before previewing setup." }
    }
    if (-not $foundation) {
    $models = @(Invoke-CustomerAzure @('cognitiveservices', 'model', 'list', '--location', $Location))
    $model = @($models | Where-Object {
        $_.model.format -ceq 'OpenAI' -and $_.model.name -ceq 'gpt-5-mini' -and $_.model.version -ceq '2025-08-07'
    })
    if (@($model | Where-Object kind -CEQ 'AIServices').Count) {
        $model = @($model | Where-Object kind -CEQ 'AIServices')
    } else {
        $model = @($model | Where-Object kind -CEQ 'OpenAI')
    }
    $sku = @($model.model.skus | Where-Object name -CEQ 'GlobalStandard')
    if ($model.Count -ne 1 -or $sku.Count -ne 1 -or -not $sku[0].usageName) {
        throw 'The pinned GPT-5 Mini model/version/GlobalStandard SKU is unavailable in this subscription/region.'
    }
    $usage = @(Invoke-CustomerAzure @('cognitiveservices', 'usage', 'list', '--location', $Location))
    $quota = @($usage | Where-Object { $_.name.value -ceq $sku[0].usageName })
    if ($quota.Count -ne 1 -or $null -eq $quota[0].limit -or $null -eq $quota[0].currentValue -or
        [double]$quota[0].limit - [double]$quota[0].currentValue -lt 10) {
        throw 'At least 10 deployment-capacity units of the model quota are required. Obtain quota before applying.'
    }
    }
    if ($ImageSource -eq 'CustomerRegistryBuild') {
        $source = Invoke-WebRequest -Uri "https://github.com/Nsethi16/DocIntel/archive/$SourceCommit.tar.gz" `
            -Method Head -MaximumRedirection 5 -TimeoutSec 30 -SkipHttpErrorCheck
        if ($source.StatusCode -ne 200 -or
            $source.BaseResponse.RequestMessage.RequestUri.Host -cne 'codeload.github.com' -or
            [string]$source.Headers['Content-Type'] -notmatch '^application/(x-gzip|gzip|octet-stream)(;|$)') {
            throw 'The pinned source commit is not publicly available. No credentials or private source are sent to ACR.'
        }
    } else {
    $tokenResponse = Invoke-WebRequest -Uri (
        'https://ghcr.io/token?service=ghcr.io&scope=repository:nsethi16/docintel-api:pull'
    ) -MaximumRedirection 0 -TimeoutSec 30 -SkipHttpErrorCheck
    if ($tokenResponse.StatusCode -ne 200) { throw 'Unable to verify anonymous public-image access.' }
    $publicToken = ($tokenResponse.Content | ConvertFrom-Json).token
    if (-not $publicToken) { throw 'The public registry did not provide an anonymous pull token.' }
    $manifest = Invoke-WebRequest -Uri "https://ghcr.io/v2/nsethi16/docintel-api/manifests/$digest" `
        -Method Head -MaximumRedirection 0 -TimeoutSec 30 -SkipHttpErrorCheck -Headers @{
            Authorization = "Bearer $publicToken"
            Accept = 'application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.docker.distribution.manifest.v2+json'
        }
    $publicToken = $null
    if ($manifest.StatusCode -ne 200 -or [string]$manifest.Headers['Docker-Content-Digest'] -cne $digest) {
        throw 'The exact image digest is not anonymously pullable. No image tag or private-registry fallback is used.'
    }
    }
    $groupExists = Invoke-CustomerAzure @('group', 'exists', '--name', $ResourceGroup)
    $groupTags = @{}
    if ($groupExists -eq $true) {
        $group = Invoke-CustomerAzure @('group', 'show', '--name', $ResourceGroup)
        if ($group.location -ine $Location) { throw 'The existing empty resource group has a different location.' }
        $inventory = @(Invoke-CustomerAzure @('resource', 'list', '--resource-group', $ResourceGroup))
        if ($inventory.Count -and -not $foundation) {
            throw 'The resource group is not empty. Use the existing-deployment setup path; nothing will be adopted.'
        }
        foreach ($property in $group.tags.PSObject.Properties) { $groupTags[$property.Name] = $property.Value }
    } elseif ($groupExists -ne $false) { throw 'Unable to establish whether the customer resource group exists.' }
    if (($groupTags.ContainsKey('docintel-customer') -and $groupTags['docintel-customer'] -cne $CustomerSlug) -or
        ($groupTags.ContainsKey('docintel-environment') -and $groupTags['docintel-environment'] -cne $EnvironmentName)) {
        throw 'The resource group is tagged for a different customer/environment.'
    }
    if ($foundation -and ($groupExists -ne $true -or $groupTags['docintel-request'] -cne $requestHash)) {
        throw 'The customer group is missing or its installation binding changed.'
    }
    $installationTag = "docintel-installation:$key"
    $webName = "DocIntel $CustomerSlug $EnvironmentName Web $key"
    $apiName = "DocIntel $CustomerSlug $EnvironmentName API $key"
    $web = Get-CustomerApplication $webName
    $api = Get-CustomerApplication $apiName
    if ($Resume -and (($journal.WebClientId -and $web.appId -cne $journal.WebClientId) -or
        ($journal.ApiClientId -and $api.appId -cne $journal.ApiClientId))) {
        throw 'The installation applications changed or were deleted. Resume will not create replacement identities.'
    }
    $parameters = @{
        customerSlug = @{ value = $CustomerSlug }; environmentName = @{ value = $EnvironmentName }
        location = @{ value = $Location }; resourceGroupName = @{ value = $ResourceGroup }
        resourceGroupTags = @{ value = $groupTags }; directoryTenantId = @{ value = $directoryId }
        initialOwnerObjectId = @{ value = $InitialOwnerObjectId }; containerImage = @{ value = $Image }
        customerDisplayName = @{ value = $CustomerName }; installationRequestHash = @{ value = $requestHash }
        buildImageInCustomerRegistry = @{ value = $ImageSource -eq 'CustomerRegistryBuild' }
        sourceCommit = @{ value = $SourceCommit }
        webClientId = @{ value = $(if ($web) { $web.appId } else { '11111111-1111-1111-1111-111111111111' }) }
        apiClientId = @{ value = $(if ($api) { $api.appId } else { '22222222-2222-2222-2222-222222222222' }) }
        deployApplication = @{ value = $true }; infrastructureReady = @{ value = [bool]$foundation }
    }
    if ($foundation) {
        foreach ($entry in $parameters.GetEnumerator() | Where-Object {
            $_.Key -notin @('resourceGroupTags', 'deployApplication', 'infrastructureReady')
        }) {
            if ((Get-CustomerHash $foundation.properties.parameters.($entry.Key).value) -cne
                (Get-CustomerHash $entry.Value.value)) {
                throw 'The live foundation identity/request parameters do not match this setup. No resources will be repointed.'
            }
        }
        if ($foundation.properties.parameters.deployApplication.value -ne $false -or
            $foundation.properties.parameters.infrastructureReady.value -ne $false) {
            throw 'The recorded deployment is not the original infrastructure-only foundation.'
        }
    }
    $parameterFile = Write-CustomerParameterFile @{ parameters = $parameters }
    $whatIfArguments = @('deployment', 'sub', 'what-if', '--location', $Location,
        '--name', "docintel-$key-application", '--template-file', $template, '--parameters', "@$parameterFile",
        '--result-format', 'FullResourcePayloads', '--no-pretty-print')
    $existingApp = $null
    $runtimeSnapshot = $null
    $operation = if ($foundation) { 'ResumeAfterFoundation' } else { 'FreshInstall' }
    if ($foundation) {
        if ($ImageSource -eq 'CustomerRegistryBuild') { $resolvedImage = Resolve-CustomerBuild }
        $appResources = @($inventory | Where-Object {
            $_.type -ieq 'Microsoft.App/containerApps' -and $_.name -ceq $foundation.properties.outputs.containerAppName.value
        })
        if ($appResources.Count -gt 1) { throw 'The application inventory is ambiguous.' }
        if ($appResources.Count) {
            $existingApp = Invoke-CustomerAzure @('containerapp', 'show', '--resource-group', $ResourceGroup,
                '--name', $foundation.properties.outputs.containerAppName.value)
            $runtimeSnapshot = Assert-CustomerRuntime $existingApp
            $operation = 'VerifyExistingApplication'
        }
    }
    $whatIf = if ($existingApp) { @{ changes = @() } } else { Invoke-CustomerAzure $whatIfArguments }
    if (-not $existingApp) { Assert-CustomerChanges $whatIf -ApplicationOnly:([bool]$foundation) }
    $plan = @{
        Version = 1; Operation = $operation; Parameters = $parameters; Web = $web; Api = $api
        Runtime = $runtimeSnapshot; RequestHash = $requestHash
        ResourceGroupExists = $groupExists; Files = $fileHashes; Changes = @($whatIf.changes)
        Diagnostics = @($whatIf.diagnostics | Where-Object { $null -ne $_ })
        PotentialChanges = @($whatIf.potentialChanges | Where-Object { $null -ne $_ })
        DirectoryOperations = @('Create/reuse installation-tagged applications and service principals',
            'Expose and preauthorize DocIntel.Access; configure the final web redirect and runtime federation')
        Boundaries = @('No Graph admin consent or deployer self-elevation', 'No existing-resource adoption',
            'No email/Teams/private networking', 'Readiness is not a PDF/Ask AI acceptance test')
        ImageSource = $ImageSource; SourceCommit = $SourceCommit
        RetryFailedBuild = [bool]$RetryFailedBuild
        BuildCosts = $(if ($ImageSource -eq 'CustomerRegistryBuild') {
            'Customer Basic ACR plus ACR Tasks build compute; maximum build duration 1200 seconds.'
        } else { 'No customer registry or build task.' })
    }
    $planHash = Get-CustomerHash $plan
    if ($Mode -eq 'Apply' -and $ExpectedPlanHash -cne $planHash) {
        throw 'The plan changed since Preview. Nothing was changed; review a new Preview before applying.'
    }
    if (-not $OutputDirectory) {
        $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('docintel-customer-plan-' + [guid]::NewGuid().ToString('N'))
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $planPath = Join-Path $OutputDirectory 'customer-plan.json'
    @{ PlanHash = $planHash; Plan = $plan } | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $planPath -Encoding utf8
    Write-Host "Customer plan: $planPath"
    Write-Host "PlanHash: $planHash"
    if ($Mode -eq 'Preview') {
        Write-Host 'Preview only. Review the saved resource/directory plan and costs before APPLY.'
        return
    }

    $writeReceipt = $true
    if (-not $journal) { $journal = @{ SchemaVersion = 1; Request = $request; RequestHash = $requestHash } }
    Save-CustomerJournal
    if ($existingApp) {
        $receipt.Stage = 'Readiness'
        Complete-CustomerReadiness $foundation.properties.outputs
        return
    }
    $receipt.Stage = 'DirectoryApplications'
    if (-not $web) { $web = New-CustomerApplication $webName }
    $journal.WebClientId = $web.appId
    Save-CustomerJournal
    if (-not $api) { $api = New-CustomerApplication $apiName }
    $journal.ApiClientId = $api.appId
    Save-CustomerJournal
    if ($web.appId -ieq $api.appId) { throw 'Web and API applications must be separate.' }
    foreach ($app in @($web, $api)) {
        $principals = Invoke-CustomerGraph GET ('servicePrincipals?$filter=' +
            [uri]::EscapeDataString("appId eq '$($app.appId)'") + '&$select=id')
        if ($null -eq $principals.value -or $principals.'@odata.nextLink' -or @($principals.value).Count -gt 1) {
            throw 'Service-principal discovery is incomplete or ambiguous.'
        }
        if (-not @($principals.value).Count) { Invoke-CustomerGraph POST 'servicePrincipals' @{ appId = $app.appId } | Out-Null }
    }
    $scopes = @($api.api.oauth2PermissionScopes | Where-Object { $null -ne $_ })
    $scope = @($scopes | Where-Object value -CEQ 'DocIntel.Access')
    if ($scope.Count -gt 1 -or @($api.appRoles | Where-Object value -CEQ 'DocIntel.Access').Count -or
        ($scope.Count -eq 1 -and -not $scope[0].isEnabled)) { throw 'The existing DocIntel.Access definition is conflicting or disabled.' }
    if (-not $scope.Count) {
        $scope = @(@{ id = [guid]::NewGuid().ToString(); value = 'DocIntel.Access'; isEnabled = $true; type = 'Admin'
            adminConsentDisplayName = 'Access DocIntel'
            adminConsentDescription = 'Access this DocIntel installation as the signed-in, explicitly authorized user.' })
        $scopes += $scope
    }
    $preauthorized = @($api.api.preAuthorizedApplications | Where-Object { $null -ne $_ -and $_.appId -ine $web.appId })
    $previousWeb = @($api.api.preAuthorizedApplications | Where-Object { $_.appId -ieq $web.appId })
    $preauthorized += @{ appId = $web.appId
        delegatedPermissionIds = @(@($previousWeb.delegatedPermissionIds) + @($scope[0].id) |
            Where-Object { $_ } | Sort-Object -Unique) }
    $identifier = "api://$($api.appId)"
    $identifiers = @($api.identifierUris | Where-Object { $_ })
    if ($identifiers.Count -and $identifier -cnotin $identifiers) { throw 'The installation API identifier URI was changed; operator review is required.' }
    if ($api.api.requestedAccessTokenVersion -and $api.api.requestedAccessTokenVersion -ne 2) {
        throw 'The installation API token version was changed; operator review is required.'
    }
    Invoke-CustomerGraph PATCH "applications/$($api.id)" @{
        identifierUris = @(@($identifiers) + @($identifier) | Sort-Object -Unique)
        api = @{ requestedAccessTokenVersion = 2; oauth2PermissionScopes = $scopes; preAuthorizedApplications = $preauthorized }
    } | Out-Null
    $apiReadback = Invoke-CustomerGraph GET ("applications/$($api.id)" + '?$select=identifierUris,api')
    if ($identifier -cnotin $apiReadback.identifierUris -or $apiReadback.api.requestedAccessTokenVersion -ne 2 -or
        -not @($apiReadback.api.oauth2PermissionScopes | Where-Object {
            $_.id -ceq $scope[0].id -and $_.value -ceq 'DocIntel.Access' -and $_.isEnabled -eq $true
        }).Count -or -not @($apiReadback.api.preAuthorizedApplications | Where-Object {
            $_.appId -ceq $web.appId -and $scope[0].id -cin $_.delegatedPermissionIds
        }).Count) { throw 'API scope/preauthorization readback failed. Infrastructure was not provisioned.' }
    $parameters.webClientId.value = $web.appId
    $parameters.apiClientId.value = $api.appId
    $deploymentArguments = @('deployment', 'sub', 'create', '--location', $Location,
        '--name', "docintel-$key-foundation", '--template-file', $template)
    if (-not $foundation) {
        $parameters.deployApplication.value = $false
        $parameters.infrastructureReady.value = $false
        $receipt.Stage = 'SharedInfrastructure'
        $parameterFile = Write-CustomerParameterFile @{ parameters = $parameters }
        $journal.FoundationParameters = (@{ parameters = $parameters } | ConvertTo-Json -Depth 100 |
            ConvertFrom-Json -AsHashtable -Depth 100).parameters
        $journal.FoundationStarted = $true
        Save-CustomerJournal
        $foundation = Invoke-CustomerAzure ($deploymentArguments + @('--parameters', "@$parameterFile"))
        if ($foundation.properties.provisioningState -cne 'Succeeded') { throw 'Shared infrastructure did not finish successfully.' }
    }
    $outputs = $foundation.properties.outputs
    $redirect = [string]$outputs.requiredWebRedirectUri.value
    $principalId = [string]$outputs.managedIdentityPrincipalId.value
    $origin = [string]$outputs.publicOrigin.value
    if (-not (Test-CustomerGuid $principalId) -or $origin -cnotmatch '^https://[a-z0-9.-]+\.azurecontainerapps\.io$' -or
        $redirect -cne ($origin + '/signin-oidc') -or -not $outputs.containerAppName.value) {
        throw 'Infrastructure did not return the expected HTTPS origin, redirect and runtime principal.'
    }
    $receipt.Stage = 'RuntimeFederation'
    $receipt.PublicOrigin = $origin
    $receipt.WebClientId = $web.appId
    $receipt.ApiClientId = $api.appId
    if ($ImageSource -eq 'CustomerRegistryBuild') {
        $receipt.Stage = 'CustomerImageBuild'
        $resolvedImage = Resolve-CustomerBuild -ApplyBuild
    }
    $redirects = @(@($web.web.redirectUris) + @($redirect) | Where-Object { $_ } | Sort-Object -Unique)
    Invoke-CustomerGraph PATCH "applications/$($web.id)" @{ web = @{ redirectUris = $redirects } } | Out-Null
    $issuer = "https://login.microsoftonline.com/$directoryId/v2.0"
    $federations = Invoke-CustomerGraph GET "applications/$($web.id)/federatedIdentityCredentials"
    if ($null -eq $federations.value -or $federations.'@odata.nextLink') { throw 'Federation discovery is incomplete.' }
    $matching = @($federations.value | Where-Object {
        $_.issuer -ceq $issuer -and $_.subject -ceq $principalId -and 'api://AzureADTokenExchange' -cin $_.audiences
    })
    if (-not $matching.Count) {
        Invoke-CustomerGraph POST "applications/$($web.id)/federatedIdentityCredentials" @{
            name = "docintel-runtime-$key"; issuer = $issuer; subject = $principalId
            audiences = @('api://AzureADTokenExchange')
        } | Out-Null
    }
    $webReadback = Invoke-CustomerGraph GET ("applications/$($web.id)" + '?$select=web')
    $federationReadback = Invoke-CustomerGraph GET "applications/$($web.id)/federatedIdentityCredentials"
    if ($redirect -cnotin $webReadback.web.redirectUris -or
        -not @($federationReadback.value | Where-Object {
            $_.issuer -ceq $issuer -and $_.subject -ceq $principalId -and 'api://AzureADTokenExchange' -cin $_.audiences
        }).Count) { throw 'Runtime federation/redirect readback failed. The application was not started.' }
    $receipt.Stage = 'Application'
    $parameters.deployApplication.value = $true
    $parameters.infrastructureReady.value = $true
    $parameters.containerImage.value = $resolvedImage
    $parameterFile = Write-CustomerParameterFile @{ parameters = $parameters }
    $whatIfArguments[[Array]::IndexOf($whatIfArguments, '--parameters') + 1] = "@$parameterFile"
    $finalPlan = Invoke-CustomerAzure $whatIfArguments
    Assert-CustomerChanges $finalPlan -ApplicationOnly
    $deploymentArguments[[Array]::IndexOf($deploymentArguments, '--name') + 1] = "docintel-$key-application"
    $deployed = Invoke-CustomerAzure ($deploymentArguments + @('--parameters', "@$parameterFile"))
    if ($deployed.properties.provisioningState -cne 'Succeeded') { throw 'Application deployment did not finish successfully.' }
    $liveApp = Invoke-CustomerAzure @('containerapp', 'show', '--resource-group', $ResourceGroup,
        '--name', $outputs.containerAppName.value)
    Assert-CustomerRuntime $liveApp | Out-Null
    $receipt.Stage = 'Readiness'
    Complete-CustomerReadiness $outputs
}
finally {
    $publicToken = $null
    foreach ($file in $privateFiles) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
    if ($writeReceipt) {
        $receipt | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'customer-result.json') -Encoding utf8
    }
}
