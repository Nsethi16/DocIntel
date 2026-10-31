#Requires -Version 7.3
<#
.SYNOPSIS
    Preview or reconcile isolated, managed-identity PDF batch translation.
.DESCRIPTION
    Creates a dedicated S1 Translator and translation-owned Standard_LRS staging account when needed.
    Never writes the original-artifact account or its policy, changes model deployments, or redeploys
    application image/identity/ingress. Existing staging requires verified ownership and expiry.
#>
[CmdletBinding()]
param(
    [ValidateSet('Preview', 'Apply')][string]$Mode = 'Preview',
    [string]$ExpectedPlanHash = '',
    [string]$Confirmation = '',
    [string]$SubscriptionId = '',
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$ContainerAppName,
    [string]$ContainerName = '',
    [string]$TranslatorResourceId = $env:DOCINTEL_TRANSLATOR_RESOURCE_ID,
    [string]$TranslatorEndpoint = $env:DOCINTEL_TRANSLATOR_ENDPOINT,
    [switch]$ReplaceExistingEndpoint,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Import-Module (Join-Path $PSScriptRoot 'DocIntelRelease.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$isRelease = Test-Path -LiteralPath (Join-Path $root 'release.json')
if ($isRelease) { Read-DocIntelRelease -Root $root | Out-Null }
if ($Mode -eq 'Apply' -and ($Confirmation -cne 'TRANSLATE' -or
    $ExpectedPlanHash -cnotmatch '^[a-f0-9]{64}$')) {
    throw 'Apply requires TRANSLATE and the exact preceding Preview plan hash.'
}
if ($ExpectedPlanHash -and $ExpectedPlanHash -cnotmatch '^[a-f0-9]{64}$') {
    throw 'ExpectedPlanHash must be the exact preceding Preview plan hash.'
}
if ($ResourceGroup -notmatch '^[A-Za-z0-9_.()-]{1,90}$' -or
    $ContainerAppName -notmatch '^[a-z][a-z0-9-]{1,30}[a-z0-9]$') { throw 'Invalid translation setup target.' }

function Invoke-TranslationAzure {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $result = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) { throw "Translation Azure operation '$($Arguments[0..1] -join ' ')' failed." }
    if (-not [String]::IsNullOrWhiteSpace(($result -join "`n"))) {
        return ($result -join "`n") | ConvertFrom-Json -Depth 100
    }
}
function Read-TranslationApp {
    Invoke-TranslationAzure @('containerapp', 'show', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--name', $ContainerAppName)
}
function Get-TranslationSetting([string]$Name) {
    $entries = @($container.env | Where-Object name -IEQ $Name)
    if ($entries.Count -gt 1 -or $entries.secretRef -or ($entries.Count -and $entries[0].name -cne $Name)) {
        throw "Resolve duplicate, noncanonical or secret-backed '$Name'; secret values are never read."
    }
    return [string]$entries.value
}
function Assert-TranslationRoot([string]$Endpoint) {
    $uri = $null
    if (-not [uri]::TryCreate($Endpoint, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne 'https' -or $uri.Port -ne 443 -or $uri.AbsolutePath -cne '/' -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
        throw 'Translator endpoint must be a plain HTTPS resource root; credential-bearing URLs are never logged.'
    }
    return $uri.AbsoluteUri
}
function Get-TranslationRoot([object]$Account) {
    $domain = [string]$Account.properties.customSubDomainName
    if ($Account.kind -cne 'TextTranslation' -or $Account.sku.name -cne 'S1' -or
        $Account.location -ieq 'global' -or -not $Account.location -or
        $domain -cnotmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,62}$' -or
        $Account.properties.disableLocalAuth -ne $true -or
        $Account.properties.provisioningState -cne 'Succeeded' -or
        $Account.identity.type -notmatch '(^|, ?)(SystemAssigned)(,|$)' -or
        $Account.identity.principalId -notmatch '^[a-fA-F0-9-]{36}$') {
        throw 'Existing Translator must be regional TextTranslation S1 with custom subdomain, local auth disabled and a provisioned system identity.'
    }
    return "https://$domain.cognitiveservices.azure.com/"
}
function Read-TranslationRoles([string]$Scope, [string]$Principal) {
    @(Invoke-TranslationAzure @('role', 'assignment', 'list', '--subscription', $SubscriptionId,
        '--scope', $Scope, '--assignee-object-id', $Principal, '--include-inherited', '--fill-principal-name', 'false'))
}
function Test-TranslationGrant([object[]]$Roles, [string]$Principal, [string]$Role, [string]$Scope) {
    if ([String]::IsNullOrWhiteSpace($Principal)) { return $false }
    Test-DocIntelTranslationPermission -Assignments $Roles -PrincipalId $Principal -RequiredRoleId $Role `
        -RequiredScope $Scope -StagingAccountScope $stagingId
}
function Read-StagingInventory {
    @(Invoke-TranslationAzure @('storage', 'account', 'list', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup))
}
function Test-StagingTags([object]$Account) {
    if (-not $Account.tags) { return $false }
    foreach ($key in $ownershipTags.Keys) {
        if ($Account.tags.PSObject.Properties[$key].Value -cne $ownershipTags[$key]) { return $false }
    }
    return $true
}
function Read-StagingAccount {
    Invoke-TranslationAzure @('storage', 'account', 'show', '--subscription', $SubscriptionId, '--ids', $stagingId)
}
function Read-StagingContainers {
    $items = @(Invoke-TranslationAzure @('storage', 'container-rm', 'list', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--storage-account', $stagingName))
    foreach ($name in 'translation-source', 'translation-target') {
        $matches = @($items | Where-Object { ($_.name -split '/')[-1] -ceq $name })
        if ($matches.Count -gt 1) { throw 'Staging container discovery is ambiguous.' }
        if ($matches.Count) {
            $access = if ($matches[0].publicAccess) { $matches[0].publicAccess } else { $matches[0].properties.publicAccess }
            if ($access -and $access -cne 'None') { throw "Staging container '$name' is not private." }
            $name
        }
    }
}
function Read-StagingLifecycle {
    $result = & az storage account management-policy show --subscription $SubscriptionId `
        --resource-group $ResourceGroup --account-name $stagingName --only-show-errors --output json 2>$null
    $policy = if ($LASTEXITCODE -eq 0 -and -not [String]::IsNullOrWhiteSpace(($result -join "`n"))) {
        ($result -join "`n") | ConvertFrom-Json -Depth 100
    } else { $null }
    Get-DocIntelTranslationLifecycleState -StorageResourceId $stagingId -Policy $policy
}
function Assert-NarrowTranslatorRoles([string]$Principal) {
    $all = @(Invoke-TranslationAzure @('role', 'assignment', 'list', '--subscription', $SubscriptionId, '--all',
        '--assignee-object-id', $Principal, '--fill-principal-name', 'false'))
    $all += @(Read-TranslationRoles $stagingId $Principal)
    $roles = @($all | Where-Object principalId -IEQ $Principal)
    foreach ($role in $roles) {
        $allowed = $role.scope.TrimEnd('/') -in @($stagingId, $sourceScope, $targetScope) -and
            ($role.roleDefinitionId -split '/')[-1] -in @(
                $readerRole, $contributorRole, 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
        if (-not $allowed) {
            throw 'Translator has an unrelated scope or non-Blob-data role; no removal, expansion or bypass is automatic.'
        }
    }
}
function Get-TranslationChangeSummary([object]$Change) {
    $resourceId = if ($Change.resourceId -is [string]) { $Change.resourceId } else { '<unavailable>' }
    $changeType = if ($Change.changeType -is [string]) { $Change.changeType } else { '<unavailable>' }
    return [ordered]@{ resourceId = $resourceId; changeType = $changeType } | ConvertTo-Json -Compress
}
function Test-SpecPrincipal([object]$Spec, [string]$Actual) {
    if ($Spec.Principal) {
        if ($Actual -ieq $Spec.Principal) { return $true }
        if ($Mode -cne 'Preview' -or $provisionAccount -or $provisionStaging -or
            $lifecycle.Status -cne 'Ready' -or $Spec.Key -cnotin @('translator-source', 'translator-target')) {
            return $false
        }
        $normalized = [regex]::Replace($Actual, '\s+', '')
        $references = @(
            "[reference(resourceId('Microsoft.CognitiveServices/accounts',parameters('accountName')),'2024-10-01','full').identity.principalId]"
            "[reference(resourceId('Microsoft.CognitiveServices/accounts','$accountName'),'2024-10-01','full').identity.principalId]"
            "[reference('$armTranslatorId','2024-10-01','full').identity.principalId]"
        )
        return @($references | Where-Object { $_ -ieq $normalized }).Count -eq 1
    }
    return $provisionAccount -and (-not $Actual -or
        ($Actual.StartsWith('[reference(', [StringComparison]::Ordinal) -and
            ($Actual.Contains("parameters('accountName')", [StringComparison]::Ordinal) -or
                $Actual.Contains($translatorId, [StringComparison]::OrdinalIgnoreCase))))
}

$login = Invoke-TranslationAzure @('account', 'show')
if (-not $SubscriptionId) { $SubscriptionId = [string]$login.id }
if ($SubscriptionId -notmatch '^[a-fA-F0-9-]{36}$' -or $login.id -ine $SubscriptionId) {
    throw 'Sign in to the selected subscription before translation setup.'
}
$groupScope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup"
$app = Read-TranslationApp
if ($app.id -ine "$groupScope/providers/Microsoft.App/containerApps/$ContainerAppName") {
    throw 'The application does not belong to the selected setup target.'
}
$selected = @($app.properties.template.containers | Where-Object { -not $ContainerName -or $_.name -ceq $ContainerName })
if ($selected.Count -ne 1) { throw 'Select exactly one application container.' }
$container = $selected[0]
foreach ($entry in @{ DocumentTranslation__SourceContainerName = 'translation-source'
    DocumentTranslation__TargetContainerName = 'translation-target' }.GetEnumerator()) {
    $configured = Get-TranslationSetting $entry.Key
    if ($configured -and $configured -cne $entry.Value) { throw 'Nonstandard translation staging names require operator review.' }
}
$clientId = Get-TranslationSetting 'AZURE_CLIENT_ID'
$assigned = @($app.identity.userAssignedIdentities.PSObject.Properties | Where-Object {
    $_.Value.clientId -ieq $clientId -and $clientId
})
if ($assigned.Count -ne 1) { throw 'The configured runtime identity is not unambiguously assigned to the app.' }
$identity = Invoke-TranslationAzure @('identity', 'show', '--subscription', $SubscriptionId, '--ids', $assigned[0].Name)
if ($identity.id -ine $assigned[0].Name -or $identity.clientId -ine $clientId -or
    $identity.principalId -ine $assigned[0].Value.principalId -or $identity.tenantId -ine $login.tenantId -or
    $identity.principalId -notmatch '^[a-fA-F0-9-]{36}$') { throw 'Runtime identity metadata does not match the app and directory.' }
$primaryEndpoint = Get-TranslationSetting 'AzureAI__BlobEndpoint'
if ($primaryEndpoint -notmatch '^https://(?<account>[a-z0-9]{3,24})\.blob\.core\.windows\.net/?$') {
    throw 'The original-artifact account must have a canonical HTTPS Blob binding.'
}
$primaryId = "$groupScope/providers/Microsoft.Storage/storageAccounts/$($Matches.account)"
$primary = Invoke-TranslationAzure @('storage', 'account', 'show', '--subscription', $SubscriptionId, '--ids', $primaryId)
if ($primary.id -ine $primaryId -or -not $primary.location) { throw 'The original-artifact binding cannot be verified.' }
$savedEndpoint = Get-TranslationSetting 'AzureAI__TranslatorEndpoint'
if ($savedEndpoint) { $null = Assert-TranslationRoot $savedEndpoint }
if ($TranslatorEndpoint) { $TranslatorEndpoint = Assert-TranslationRoot $TranslatorEndpoint }
$accounts = @(Invoke-TranslationAzure @('cognitiveservices', 'account', 'list',
    '--subscription', $SubscriptionId, '--resource-group', $ResourceGroup))
if ($TranslatorResourceId -and $TranslatorResourceId -notmatch "^$([regex]::Escape($groupScope))/providers/Microsoft.CognitiveServices/accounts/[^/]+$") {
    throw 'Explicit Translator must be an existing account in the application resource group.'
}
$candidates = @($accounts | Where-Object {
    $candidateRoot = "https://$($_.properties.customSubDomainName).cognitiveservices.azure.com/"
    if ($TranslatorResourceId) { $_.id -ieq $TranslatorResourceId }
    elseif ($TranslatorEndpoint) { $candidateRoot -ieq $TranslatorEndpoint }
    else { $_.kind -ceq 'TextTranslation' -and $candidateRoot.TrimEnd('/') -ieq $savedEndpoint.TrimEnd('/') }
})
if ($candidates.Count -gt 1 -or (($TranslatorResourceId -or $TranslatorEndpoint) -and $candidates.Count -ne 1)) {
    throw 'Explicit Translator selection is missing or ambiguous; unsupported endpoints are not silently retained.'
}
$suffix = (Get-DocIntelObjectHash @($SubscriptionId.ToLowerInvariant(), $ResourceGroup.ToLowerInvariant(),
    $ContainerAppName.ToLowerInvariant())).Substring(0, 16)
$provisionAccount = $candidates.Count -eq 0
if ($provisionAccount) {
    $accountName = "tr-docintel-$suffix"
    $collision = @($accounts | Where-Object name -IEQ $accountName)
    if ($collision.Count) { $candidates = $collision; $provisionAccount = $false }
}
if ($provisionAccount) {
    $translator = $null; $region = $primary.location
    $translatorId = "$groupScope/providers/Microsoft.CognitiveServices/accounts/$accountName"
    $endpoint = "https://$accountName.cognitiveservices.azure.com/"
} else {
    $translator = $candidates[0]; $accountName = $translator.name; $translatorId = $translator.id
    $endpoint = Get-TranslationRoot $translator; $region = $translator.location
    if ($TranslatorEndpoint -and $endpoint -ine $TranslatorEndpoint) { throw 'Explicit endpoint and Translator resource do not match.' }
}
$ownershipTags = Get-DocIntelTranslationStagingTags $app.id $identity.id $translatorId
$savedStaging = Get-TranslationSetting 'DocumentTranslation__StagingBlobEndpoint'
if ($savedStaging -and $savedStaging -notmatch '^https://[a-z0-9]{3,24}\.blob\.core\.windows\.net/?$') {
    throw 'Staging must be a canonical HTTPS Blob root, without credentials or SAS.'
}
$stagingName = if ($savedStaging) { ([uri]$savedStaging).Host.Split('.')[0] } else { "stditr$suffix" }
$inventory = @(Read-StagingInventory)
$named = @($inventory | Where-Object name -CEQ $stagingName)
if ($named.Count -gt 1) { throw 'Staging account discovery is ambiguous.' }
if (-not $savedStaging -and -not $named.Count) {
    $owned = @($inventory | Where-Object { Test-StagingTags $_ })
    if ($owned.Count -gt 1) { throw 'Multiple owned staging accounts exist; an unambiguous binding is required.' }
    if ($owned.Count) { $named = $owned; $stagingName = $owned[0].name }
}
$stagingId = "$groupScope/providers/Microsoft.Storage/storageAccounts/$stagingName"
if ($stagingId -ieq $primaryId) { throw 'Original-artifact storage is forbidden as translation staging; no fallback is allowed.' }
$provisionStaging = $named.Count -eq 0
if ($provisionStaging -and $savedStaging) { throw 'The configured staging account is missing; it will not be silently repointed.' }
if ($provisionStaging) {
    $availability = Invoke-TranslationAzure @('storage', 'account', 'check-name', '--name', $stagingName)
    if ($availability.nameAvailable -ne $true) { throw 'Deterministic staging-account name collision; no account will be adopted.' }
    $staging = $null; $stagingEndpoint = "https://$stagingName.blob.core.windows.net/"
    $existingContainers = @()
    $lifecycle = Get-DocIntelTranslationLifecycleState -StorageResourceId $stagingId -Policy $null
    $lifecycle.Status = 'CreateWithNewStagingAccount'; $lifecycle.Blocker = ''
} else {
    $staging = Read-StagingAccount
    $stagingEndpoint = Assert-DocIntelTranslationStagingAccount $staging $ownershipTags $stagingId $region
    $existingContainers = @(Read-StagingContainers)
    $lifecycle = Read-StagingLifecycle
}
$network = if ($staging.networkRuleSet) { $staging.networkRuleSet } else { $staging.properties.networkAcls }
$publicNetwork = if ($staging.publicNetworkAccess) { $staging.publicNetworkAccess } else { $staging.properties.publicNetworkAccess }
$networkReviewRequired = $publicNetwork -ieq 'Disabled' -or $network.defaultAction -ieq 'Deny'
$sourceScope = "$stagingId/blobServices/default/containers/translation-source"
$targetScope = "$stagingId/blobServices/default/containers/translation-target"
$cognitiveRole = 'a97b65f3-24c7-4388-baec-2e87135dc908'
$readerRole = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
$contributorRole = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
if (-not $provisionAccount) { Assert-NarrowTranslatorRoles $translator.identity.principalId }
$roleSpecs = @(
    @{ Key = 'runtime-cognitive'; Scope = $translatorId; Role = $cognitiveRole; Principal = $identity.principalId }
    @{ Key = 'translator-source'; Scope = $sourceScope; Role = $readerRole; Principal = $translator.identity.principalId }
    @{ Key = 'translator-target'; Scope = $targetScope; Role = $contributorRole; Principal = $translator.identity.principalId }
    @{ Key = 'runtime-source'; Scope = $sourceScope; Role = $contributorRole; Principal = $identity.principalId }
    @{ Key = 'runtime-target'; Scope = $targetScope; Role = $contributorRole; Principal = $identity.principalId }
)
$armGroupScope = "/subscriptions/$(([guid]$SubscriptionId).ToString())/resourceGroups/$(($app.id -split '/')[4])"
$armTranslatorId = "$armGroupScope/providers/Microsoft.CognitiveServices/accounts/$accountName"
$armStagingId = "$armGroupScope/providers/Microsoft.Storage/storageAccounts/$stagingName"
foreach ($spec in $roleSpecs) {
    $armScope = if ($spec.Key -ceq 'runtime-cognitive') { $armTranslatorId } else {
        "$armStagingId/blobServices/default/containers/$(($spec.Scope -split '/')[-1])"
    }
    $seedIdentity = if ($spec.Key -like 'translator-*') { $armTranslatorId } else { $identity.id }
    $assignmentGuid = Get-DocIntelArmGuid @($armScope, $seedIdentity, $spec.Role)
    $spec.AssignmentId = "$armScope/providers/Microsoft.Authorization/roleAssignments/$assignmentGuid"
    $roles = if (($spec.Scope -ieq $translatorId -and $provisionAccount) -or
        ($spec.Scope -ine $translatorId -and $provisionStaging) -or -not $spec.Principal) { @() }
    else { @(Read-TranslationRoles $spec.Scope $spec.Principal) }
    $spec.Assign = -not (Test-TranslationGrant $roles $spec.Principal $spec.Role $spec.Scope)
}
$values = @{
    accountName = $accountName; location = $region; stagingAccountName = $stagingName; applicationResourceId = $app.id
    runtimePrincipalId = $identity.principalId; runtimeIdentityResourceId = $identity.id
    provisionAccount = $provisionAccount; provisionStagingAccount = $provisionStaging
    provisionSourceContainer = 'translation-source' -cnotin $existingContainers
    provisionTargetContainer = 'translation-target' -cnotin $existingContainers
    assignRuntimeRole = $roleSpecs[0].Assign; assignSourceRole = $roleSpecs[1].Assign
    assignTargetRole = $roleSpecs[2].Assign; assignRuntimeSourceRole = $roleSpecs[3].Assign
    assignRuntimeTargetRole = $roleSpecs[4].Assign
}
$parameters = @{ parameters = @{} }
foreach ($key in $values.Keys) { $parameters.parameters[$key] = @{ value = $values[$key] } }
$template = Join-Path $root $(if ($isRelease) { 'infra\translation-connection.json' } else { 'infra\translation-connection.bicep' })
$library = Join-Path $PSScriptRoot 'DocIntelRelease.psm1'
$codeHashes = @{ Template = (Get-FileHash $template -Algorithm SHA256).Hash
    Script = (Get-FileHash $PSCommandPath -Algorithm SHA256).Hash; Library = (Get-FileHash $library -Algorithm SHA256).Hash }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'TestOutput\translation-connection' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$parameterFile = Join-Path $OutputDirectory ('.translation-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    $parameters | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $parameterFile -Encoding utf8
    $expected = @{}
    if ($provisionAccount) { $expected[$translatorId] = 'Translator' }
    if ($provisionStaging) { $expected[$stagingId] = 'Staging'; $expected["$stagingId/managementPolicies/default"] = 'Policy' }
    if ($values.provisionSourceContainer) { $expected[$sourceScope] = 'Container' }
    if ($values.provisionTargetContainer) { $expected[$targetScope] = 'Container' }
    $changes = @()
    if ($expected.Count -or @($roleSpecs | Where-Object Assign).Count) {
        $whatIf = Invoke-TranslationAzure @('deployment', 'group', 'what-if', '--subscription', $SubscriptionId,
            '--resource-group', $ResourceGroup, '--template-file', $template, '--parameters', "@$parameterFile",
            '--result-format', 'FullResourcePayloads', '--no-pretty-print')
        if ($whatIf.status -cne 'Succeeded' -or $whatIf.error -or
            @($whatIf.diagnostics | Where-Object { $null -ne $_ }).Count -or
            @($whatIf.potentialChanges | Where-Object { $null -ne $_ }).Count) {
            throw 'Translation ARM preview is incomplete. No apply is allowed.'
        }
        $changes = @($whatIf.changes | Where-Object { $null -ne $_ })
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $seenRoles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($change in $changes) {
            if ($change.changeType -ceq 'NoChange') { continue }
            if ($change.changeType -ceq 'Ignore' -and
                $change.resourceId.StartsWith($groupScope + '/', [StringComparison]::OrdinalIgnoreCase) -and
                $change.resourceId -notmatch '/providers/Microsoft.Resources/deployments/' -and
                -not $expected.ContainsKey($change.resourceId) -and
                -not @($roleSpecs | Where-Object {
                    $_.Assign -and $change.resourceId -ieq $_.AssignmentId
                }).Count) { continue }
            if ($change.changeType -cne 'Create' -or -not $seen.Add($change.resourceId)) {
                throw ('Translation preview must only create exact missing resources, never modify/delete existing resources. ' +
                    (Get-TranslationChangeSummary $change))
            }
            $kind = $expected[$change.resourceId]
            if ($kind -ceq 'Translator' -and $change.after.kind -ceq 'TextTranslation' -and
                $change.after.sku.name -ceq 'S1' -and $change.after.location -ieq $region -and
                $change.after.identity.type -ceq 'SystemAssigned' -and
                $change.after.properties.customSubDomainName -ceq $accountName -and
                $change.after.properties.disableLocalAuth -eq $true) { continue }
            if ($kind -ceq 'Staging') {
                $null = Assert-DocIntelTranslationStagingAccount $change.after $ownershipTags $stagingId $region
                continue
            }
            if ($kind -ceq 'Container' -and $change.after.properties.publicAccess -ceq 'None') { continue }
            if ($kind -ceq 'Policy' -and @($change.after.properties.policy.rules).Count -eq 1 -and
                (Get-DocIntelObjectHash $change.after.properties.policy.rules[0]) -ceq
                    (Get-DocIntelObjectHash (Get-DocIntelTranslationLifecycleRule))) { continue }
            $roleMatches = @($roleSpecs | Where-Object {
                $_.Assign -and $change.resourceId -ieq $_.AssignmentId -and
                    ($change.after.properties.roleDefinitionId -ieq "/subscriptions/$SubscriptionId/providers/Microsoft.Authorization/roleDefinitions/$($_.Role)" -or
                        $change.after.properties.roleDefinitionId -ieq "/providers/Microsoft.Authorization/roleDefinitions/$($_.Role)") -and
                    (Test-SpecPrincipal $_ ([string]$change.after.properties.principalId))
            })
            if ($roleMatches.Count -eq 1 -and $seenRoles.Add($roleMatches[0].Key)) { continue }
            throw ('Translation preview escaped exact isolated staging/Translator/container-role scope. ' +
                (Get-TranslationChangeSummary $change))
        }
        if ($seen.Count -ne $expected.Count + @($roleSpecs | Where-Object Assign).Count) {
            throw 'Translation preview omitted required resource/role creations.'
        }
    }
    $plan = @{
        Application = $app.id; Container = $container.name; SnapshotHash = Get-DocIntelObjectHash $app
        OriginalStorage = $primaryId; OriginalStorageSnapshotHash = Get-DocIntelObjectHash $primary
        OriginalStoragePolicy = 'Not read or written'
        Resource = $translatorId; Endpoint = $endpoint; PreviousEndpoint = $savedEndpoint
        AccountSnapshotHash = Get-DocIntelObjectHash $translator
        StagingResource = $stagingId; StagingBlobEndpoint = $stagingEndpoint; PreviousStagingEndpoint = $savedStaging
        StagingSnapshotHash = Get-DocIntelObjectHash $staging; StagingOwnership = $ownershipTags
        ProvisionAccount = $provisionAccount; ProvisionStagingAccount = $provisionStaging
        Sku = 'S1'; StagingSku = 'Standard_LRS'; Region = $region; Lifecycle = $lifecycle
        RuntimeIdentity = $identity.id; RuntimePrincipal = $identity.principalId
        ReplaceExistingEndpoint = [bool]$ReplaceExistingEndpoint
        EndpointReplacementRequired = [bool]($savedEndpoint -and $savedEndpoint.TrimEnd('/') -ine $endpoint.TrimEnd('/'))
        NetworkReviewRequired = [bool]$networkReviewRequired
        RequiresApply = [bool]($expected.Count -or @($roleSpecs | Where-Object Assign).Count -or
            $savedEndpoint -cne $endpoint -or $savedStaging -cne $stagingEndpoint -or $lifecycle.Status -cne 'Ready')
        Source = @{ Scope = $sourceScope; Role = $readerRole; Private = $true }
        Target = @{ Scope = $targetScope; Role = $contributorRole; Private = $true }
        Permissions = @($roleSpecs | ForEach-Object {
            @{ Scope = $_.Scope; Role = $_.Role; Assign = $_.Assign; AssignmentId = $_.AssignmentId
                Principal = $(if ($_.Principal) { $_.Principal } else { "SystemIdentity:$translatorId" }) }
        })
        CodeHashes = $codeHashes; WhatIfSnapshotHash = Get-DocIntelObjectHash @($changes | Sort-Object resourceId)
        Changes = @($changes | Sort-Object resourceId | ForEach-Object {
            @{ resourceId = $_.resourceId; changeType = $_.changeType }
        })
        Warning = 'Usage-billed S1 + isolated Standard_LRS staging and five narrow roles require approval. No original-data account, policy, model, networking, auth, image or identity changes.'
    }
    $planHash = Get-DocIntelObjectHash $plan
    if ($ExpectedPlanHash -and $ExpectedPlanHash -cne $planHash) {
        throw 'Translation targets or plan changed since Preview. No writes were performed.'
    }
    @{ PlanHash = $planHash; Plan = $plan } | ConvertTo-Json -Depth 100 |
        Set-Content -LiteralPath (Join-Path $OutputDirectory 'translation-plan.json') -Encoding utf8
    if ($env:GITHUB_OUTPUT) { "plan_hash=$planHash" | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
    if ($Mode -eq 'Preview') { return [pscustomobject]@{ PlanHash = $planHash; Plan = $plan } }
    if ($plan.EndpointReplacementRequired -and -not $ReplaceExistingEndpoint) {
        throw 'Existing endpoint replacement requires an explicit -ReplaceExistingEndpoint and a new reviewed Preview.'
    }
    if (-not $provisionStaging -and $lifecycle.Status -cne 'Ready') { throw $lifecycle.Blocker }
    if ($networkReviewRequired) { throw 'Owned staging networking needs separate operator review; it is never changed automatically.' }
    $currentPrimary = Invoke-TranslationAzure @('storage', 'account', 'show', '--subscription', $SubscriptionId, '--ids', $primaryId)
    $stageDrift = if ($provisionStaging) {
        $current = @(Read-StagingInventory)
        @($current | Where-Object { $_.id -ieq $stagingId -or (Test-StagingTags $_) }).Count -gt 0 -or
            (Invoke-TranslationAzure @('storage', 'account', 'check-name', '--name', $stagingName)).nameAvailable -ne $true
    } else {
        (Get-DocIntelObjectHash (Read-StagingAccount)) -cne $plan.StagingSnapshotHash -or
            (Get-DocIntelObjectHash (Read-StagingLifecycle)) -cne (Get-DocIntelObjectHash $lifecycle)
    }
    if ($stageDrift -or (Get-DocIntelObjectHash (Read-TranslationApp)) -cne $plan.SnapshotHash -or
        (Get-DocIntelObjectHash $currentPrimary) -cne $plan.OriginalStorageSnapshotHash -or
        (Get-FileHash $template -Algorithm SHA256).Hash -cne $codeHashes.Template -or
        (Get-FileHash $PSCommandPath -Algorithm SHA256).Hash -cne $codeHashes.Script -or
        (Get-FileHash $library -Algorithm SHA256).Hash -cne $codeHashes.Library) {
        throw 'Application, storage, lifecycle, ownership or setup code changed during setup. No writes were performed.'
    }
    if ($expected.Count -or @($roleSpecs | Where-Object Assign).Count) {
        $result = Invoke-TranslationAzure @('deployment', 'group', 'create', '--subscription', $SubscriptionId,
            '--resource-group', $ResourceGroup, '--name', 'docintel-batch-translation',
            '--template-file', $template, '--parameters', "@$parameterFile", '--mode', 'Incremental')
        if ($result.properties.provisioningState -cne 'Succeeded') { throw 'Translation infrastructure did not succeed.' }
    }
    $liveTranslator = Invoke-TranslationAzure @('cognitiveservices', 'account', 'show', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--name', $accountName)
    $liveStaging = Read-StagingAccount
    $liveLifecycle = Read-StagingLifecycle
    if ($liveTranslator.id -ine $translatorId -or (Get-TranslationRoot $liveTranslator) -ine $endpoint -or
        (Assert-DocIntelTranslationStagingAccount $liveStaging $ownershipTags $stagingId $region) -cne $stagingEndpoint -or
        @(Read-StagingContainers).Count -ne 2 -or $liveLifecycle.Status -cne 'Ready' -or
        (-not $provisionStaging -and (Get-DocIntelObjectHash $liveLifecycle) -cne (Get-DocIntelObjectHash $lifecycle))) {
        throw 'Owned isolated staging, privacy expiry or Translator could not be verified; app settings were not changed.'
    }
    Assert-NarrowTranslatorRoles $liveTranslator.identity.principalId
    foreach ($spec in $roleSpecs) {
        $principal = if ($spec.Key -like 'translator-*') { $liveTranslator.identity.principalId } else { $identity.principalId }
        if (-not (Test-TranslationGrant @(Read-TranslationRoles $spec.Scope $principal) $principal $spec.Role $spec.Scope)) {
            throw 'A scoped translation grant is not visible yet. Wait for propagation and preview again; the app was not repointed.'
        }
    }
    if ((Get-DocIntelObjectHash (Read-TranslationApp)) -cne $plan.SnapshotHash) {
        throw 'Application changed while infrastructure was prepared. Runtime settings were not overwritten.'
    }
    if ($savedEndpoint -cne $endpoint -or $savedStaging -cne $stagingEndpoint) {
        Invoke-TranslationAzure @('containerapp', 'update', '--subscription', $SubscriptionId,
            '--resource-group', $ResourceGroup, '--name', $ContainerAppName, '--container-name', $container.name,
            '--set-env-vars', "AzureAI__TranslatorEndpoint=$endpoint",
            "DocumentTranslation__StagingBlobEndpoint=$stagingEndpoint") | Out-Null
    }
    $verified = Read-TranslationApp
    $expectedTemplate = $app.properties.template | ConvertTo-Json -Depth 100 | ConvertFrom-Json -Depth 100
    $expectedContainer = @($expectedTemplate.containers | Where-Object name -CEQ $container.name)[0]
    $expectedContainer.env = @($expectedContainer.env | Where-Object {
        $_.name -cnotin @('AzureAI__TranslatorEndpoint', 'DocumentTranslation__StagingBlobEndpoint')
    }) + @([pscustomobject]@{ name = 'AzureAI__TranslatorEndpoint'; value = $endpoint }
        [pscustomobject]@{ name = 'DocumentTranslation__StagingBlobEndpoint'; value = $stagingEndpoint })
    $actualTemplate = $verified.properties.template | ConvertTo-Json -Depth 100 | ConvertFrom-Json -Depth 100
    $expectedTemplate.PSObject.Properties.Remove('revisionSuffix')
    $actualTemplate.PSObject.Properties.Remove('revisionSuffix')
    foreach ($item in @($expectedTemplate.containers) + @($actualTemplate.containers)) {
        $item.env = @($item.env | Sort-Object name)
    }
    if ((Get-DocIntelObjectHash $actualTemplate) -cne (Get-DocIntelObjectHash $expectedTemplate) -or
        (Get-DocIntelObjectHash $verified.identity) -cne (Get-DocIntelObjectHash $app.identity) -or
        (Get-DocIntelObjectHash $verified.properties.configuration) -cne (Get-DocIntelObjectHash $app.properties.configuration) -or
        $verified.properties.environmentId -cne $app.properties.environmentId) {
        throw 'Unrelated app settings, image, identity, ingress or model bindings changed; connection success is not claimed.'
    }
    return [pscustomobject]@{ Status = 'Configured'; Resource = $translatorId; Endpoint = $endpoint
        StagingResource = $stagingId; StagingBlobEndpoint = $stagingEndpoint
        Readiness = 'Five scoped grants and owned one-day staging expiry verified; runtime PDF validation is still required.' }
}
finally { Remove-Item -LiteralPath $parameterFile -Force -ErrorAction SilentlyContinue }
