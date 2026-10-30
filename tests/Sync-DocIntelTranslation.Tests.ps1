#Requires -Version 7.3
param([string]$ReleaseRoot = '')
$ErrorActionPreference = 'Stop'
$root = if ($ReleaseRoot) { $ReleaseRoot } else { Split-Path $PSScriptRoot -Parent }
Import-Module (Join-Path $root 'scripts\DocIntelRelease.psm1') -Force
$file = Join-Path $root 'scripts\Sync-DocIntelTranslation.ps1'
$output = Join-Path $root ('TestOutput\translation-tests-' + [guid]::NewGuid().ToString('N'))
$compiled = & az bicep build --file (Join-Path $root 'infra\translation-connection.bicep') --stdout --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Isolated staging template must compile without Azure access.' }
$template = ($compiled -join "`n") | ConvertFrom-Json -Depth 100
$storage = @($template.resources | Where-Object type -CEQ 'Microsoft.Storage/storageAccounts')
$policy = @($template.resources | Where-Object type -CEQ 'Microsoft.Storage/storageAccounts/managementPolicies')
$roles = @($template.resources | Where-Object type -CEQ 'Microsoft.Authorization/roleAssignments')
if ($template.resources.Count -ne 10 -or $storage.Count -ne 1 -or $roles.Count -ne 5 -or
    $storage[0].sku.name -cne 'Standard_LRS' -or $storage[0].properties.allowSharedKeyAccess -ne $false -or
    $storage[0].properties.allowBlobPublicAccess -ne $false -or
    $storage[0].condition -cne "[parameters('provisionStagingAccount')]" -or
    $policy[0].condition -cne "[parameters('provisionStagingAccount')]" -or
    (Get-DocIntelObjectHash $policy[0].properties.policy.rules[0]) -cne
        (Get-DocIntelObjectHash (Get-DocIntelTranslationLifecycleRule))) {
    throw 'Only new dedicated staging may initialize the exact expiry policy; five narrowly scoped grants are required.'
}
foreach ($role in $roles) {
    if ($role.properties.roleDefinitionId -notmatch 'cognitiveUserRole' -and
        $role.scope -notmatch "translation-(source|target)") {
        throw 'Blob grants must be container-scoped, never account-wide.'
    }
}
if (($template | ConvertTo-Json -Depth 100) -match 'listKeys|listAccountSas|publicNetworkAccess|accounts/deployments') {
    throw 'Translation setup must not access keys, model deployments or network settings.'
}
function New-Fixture {
    $sub = [guid]::NewGuid().ToString()
    $tenant = [guid]::NewGuid().ToString()
    $prefix = "/subscriptions/$sub/resourceGroups/fixture/providers"
    $identity = @{ id = "$prefix/Microsoft.ManagedIdentity/userAssignedIdentities/runtime"
        clientId = [guid]::NewGuid().ToString(); principalId = [guid]::NewGuid().ToString(); tenantId = $tenant }
    $global:TranslationFixture = @{
        sub = $sub; tenant = $tenant; prefix = $prefix; identity = $identity
        writes = [Collections.Generic.List[object]]::new(); calls = [Collections.Generic.List[object]]::new()
        translator = $null; stage = $null; stagePolicy = $null; containers = @(); roles = @(); scenario = ''
        primaryPolicy = @{ policy = @{ rules = @(@{ name = 'customer-retention'; definition = 'never-touch' }) } }
        primary = @{ id = "$prefix/Microsoft.Storage/storageAccounts/primaryaccount"; name = 'primaryaccount'
            location = 'westus'; networkRuleSet = @{ defaultAction = 'Deny' }; publicNetworkAccess = 'Disabled' }
        app = @{
            id = "$prefix/Microsoft.App/containerApps/fixture-app"
            identity = @{ userAssignedIdentities = @{ $identity.id = @{
                clientId = $identity.clientId; principalId = $identity.principalId } } }
            properties = @{
                latestRevisionName = 'existing'; environmentId = "$prefix/Microsoft.App/managedEnvironments/keep"
                configuration = @{ ingress = @{ fqdn = 'fixture.example'; external = $true }
                    secrets = @(@{ name = 'keep-secret' }) }
                template = @{ revisionSuffix = 'prior'; scale = @{ minReplicas = 1 }; containers = @(@{
                    name = 'docintel-api'; image = 'fixture.azurecr.io/app:keep'
                    env = @(
                        @{ name = 'AZURE_CLIENT_ID'; value = $identity.clientId }
                        @{ name = 'AzureAI__BlobEndpoint'; value = 'https://primaryaccount.blob.core.windows.net/' }
                        @{ name = 'AzureAI__TranslatorEndpoint'; value = 'https://language.cognitiveservices.azure.com/' }
                        @{ name = 'AzureAI__AzureOpenAIEndpoint'; value = 'https://language.openai.azure.com/' }
                        @{ name = 'AzureAI__DecisionEndpoint'; value = 'https://native.services.ai.azure.com/' }
                        @{ name = 'UserOwned'; secretRef = 'keep-secret' }
                    )
                }) }
            }
        }
    }
}
function New-Translator([string]$Name) {
    $f = $global:TranslationFixture
    @{ name = $Name; id = "$($f.prefix)/Microsoft.CognitiveServices/accounts/$Name"
        location = 'westus'; kind = 'TextTranslation'; sku = @{ name = 'S1' }
        identity = @{ type = 'SystemAssigned'; principalId = '22222222-2222-2222-2222-222222222222' }
        properties = @{ customSubDomainName = $Name; disableLocalAuth = $true; provisioningState = 'Succeeded' } }
}
function New-Stage([string]$Name, [string]$TranslatorId) {
    $f = $global:TranslationFixture
    @{ name = $Name; id = "$($f.prefix)/Microsoft.Storage/storageAccounts/$Name"
        location = 'westus'; kind = 'StorageV2'; sku = @{ name = 'Standard_LRS' }
        tags = Get-DocIntelTranslationStagingTags $f.app.id $f.identity.id $TranslatorId
        minimumTlsVersion = 'TLS1_2'; enableHttpsTrafficOnly = $true
        allowBlobPublicAccess = $false; allowSharedKeyAccess = $false
        networkRuleSet = @{ defaultAction = 'Allow' }; publicNetworkAccess = 'Enabled' }
}
function Fixture-Specs([object]$P) {
    $f = $global:TranslationFixture
    $cog = "$($f.prefix)/Microsoft.CognitiveServices/accounts/$($P.accountName.value)"
    $stage = "$($f.prefix)/Microsoft.Storage/storageAccounts/$($P.stagingAccountName.value)"
    $source = "$stage/blobServices/default/containers/translation-source"
    $target = "$stage/blobServices/default/containers/translation-target"
    $service = if ($P.provisionAccount.value) { '' } else { $f.translator.identity.principalId }
    @(
        @{ Scope = $cog; Role = 'a97b65f3-24c7-4388-baec-2e87135dc908'; Principal = $f.identity.principalId
            Assign = $P.assignRuntimeRole.value }
        @{ Scope = $source; Role = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'; Principal = $service
            Assign = $P.assignSourceRole.value }
        @{ Scope = $target; Role = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'; Principal = $service
            Assign = $P.assignTargetRole.value }
        @{ Scope = $source; Role = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'; Principal = $f.identity.principalId
            Assign = $P.assignRuntimeSourceRole.value }
        @{ Scope = $target; Role = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'; Principal = $f.identity.principalId
            Assign = $P.assignRuntimeTargetRole.value }
    )
}
function az {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $f = $global:TranslationFixture; $f.calls.Add($Arguments); $global:LASTEXITCODE = 0
    $key = ($Arguments | Select-Object -First 4) -join ' '
    $result = switch -Wildcard ($key) {
        'account show*' { @{ id = $f.sub; tenantId = $f.tenant }; break }
        'containerapp show*' { $f.app; break }
        'identity show*' { $f.identity; break }
        'storage account list*' { @($f.primary) + @($f.stage | Where-Object { $null -ne $_ }); break }
        'storage account check-name*' { @{ nameAvailable = $f.scenario -ne 'global-collision' }; break }
        'storage account show*' {
            $id = $Arguments[[Array]::IndexOf($Arguments, '--ids') + 1]
            if ($id -ieq $f.primary.id) { $f.primary }
            elseif ($f.stage -and $id -ieq $f.stage.id) { $f.stage }
            else { throw 'No guessed or foreign account may be read.' }
            break
        }
        'storage account management-policy show' {
            $name = $Arguments[[Array]::IndexOf($Arguments, '--account-name') + 1]
            if (-not $f.stage -or $name -cne $f.stage.name) { throw 'Original storage policy must never be read or written.' }
            if ($f.scenario -eq 'unreadable-policy') { $global:LASTEXITCODE = 1; break }
            $f.stagePolicy; break
        }
        'storage container-rm list*' {
            if ($Arguments -notcontains $f.stage.name) { throw 'Only isolated staging containers may be inspected.' }
            @($f.containers | ForEach-Object { @{ name = $_; publicAccess = 'None' } }); break
        }
        'cognitiveservices account list*' { @($f.translator | Where-Object { $null -ne $_ }); break }
        'cognitiveservices account show*' { $f.translator; break }
        'role assignment list*' {
            if ($f.scenario -eq 'role-read-failed') { $global:LASTEXITCODE = 1; break }
            if ($Arguments -contains '--all') { $f.roles }
            else {
                $scope = $Arguments[[Array]::IndexOf($Arguments, '--scope') + 1]
                @($f.roles | Where-Object scope -IEQ $scope)
            }
            break
        }
        'deployment group what-if*' {
            $path = $Arguments[[Array]::IndexOf($Arguments, '--parameters') + 1].Substring(1)
            $p = (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 100).parameters
            $f.parameters = $p
            if ($p.PSObject.Properties['storageAccountName']) { throw 'The original account cannot be a deployment parameter.' }
            $cog = "$($f.prefix)/Microsoft.CognitiveServices/accounts/$($p.accountName.value)"
            $stage = "$($f.prefix)/Microsoft.Storage/storageAccounts/$($p.stagingAccountName.value)"
            $changes = @()
            if ($p.provisionAccount.value) {
                $changes += @{ resourceId = $cog; changeType = 'Create'; after = New-Translator $p.accountName.value }
            }
            if ($p.provisionStagingAccount.value) {
                $after = New-Stage $p.stagingAccountName.value $cog
                $changes += @{ resourceId = $stage; changeType = 'Create'; after = $after }
                $changes += @{ resourceId = "$stage/managementPolicies/default"; changeType = 'Create'
                    after = @{ properties = @{ policy = @{ rules = @((Get-DocIntelTranslationLifecycleRule)) } } } }
            }
            foreach ($part in @(
                @{ Name = 'translation-source'; Create = $p.provisionSourceContainer.value }
                @{ Name = 'translation-target'; Create = $p.provisionTargetContainer.value }
            )) {
                if ($part.Create) { $changes += @{
                    resourceId = "$stage/blobServices/default/containers/$($part.Name)"
                    changeType = 'Create'; after = @{ properties = @{ publicAccess = 'None' } } } }
            }
            $i = 0
            foreach ($spec in @(Fixture-Specs $p)) {
                $i++
                if ($spec.Assign) { $changes += @{
                    resourceId = "$($spec.Scope)/providers/Microsoft.Authorization/roleAssignments/00000000-0000-0000-0000-$($i.ToString('D12'))"
                    changeType = 'Create'; after = @{ properties = @{
                        principalId = $spec.Principal
                        roleDefinitionId = "/subscriptions/$($f.sub)/providers/Microsoft.Authorization/roleDefinitions/$($spec.Role)"
                    } } } }
            }
            switch ($f.scenario) {
                'foreign-change' { $changes += @{ resourceId = $f.primary.id; changeType = 'Modify' } }
                'wrong-principal' { $changes[-1].after.properties.principalId = [guid]::NewGuid().ToString() }
                'wrong-role' { $changes[-1].after.properties.roleDefinitionId = '/roles/Owner' }
                'omitted-role' { $changes = $changes[0..($changes.Count - 2)] }
                'policy-broadened' {
                    ($changes | Where-Object resourceId -Like '*/managementPolicies/default').after.properties.policy.rules[0].
                        definition.filters.prefixMatch = @('translation-source/')
                }
                'ignored-existing' {
                    $changes += @{ resourceId = $f.primary.id; changeType = 'Ignore'
                        before = @{ operatorSetting = 'fixture-sensitive-value'; revision = $f.ignoreRevision } }
                }
                'escaped-create' {
                    $f.rejectedId = "$stage/blobServices/default"
                    $changes += @{ resourceId = $f.rejectedId; changeType = 'Create'
                        before = @{ env = @(@{ name = 'PRIVATE_SETTING'; value = 'fixture-before-secret' }) }
                        after = @{ properties = @{ env = @(@{
                            name = 'PRIVATE_SETTING'; value = 'fixture-after-secret'
                        }) } } }
                }
                'owned-modify' {
                    $f.rejectedId = $stage
                    $changes += @{ resourceId = $f.rejectedId; changeType = 'Modify'
                        before = @{ env = @(@{ name = 'PRIVATE_SETTING'; value = 'fixture-before-secret' }) }
                        after = @{ properties = @{ env = @(@{
                            name = 'PRIVATE_SETTING'; value = 'fixture-after-secret'
                        }) } } }
                }
            }
            @{ status = 'Succeeded'; changes = $changes
                diagnostics = $(if ($f.scenario -eq 'incomplete') { @(@{ code = 'Unsupported' }) } else { @() }) }
            break
        }
        'deployment group create*' {
            $f.writes.Add($Arguments)
            $p = $f.parameters
            if ($p.provisionAccount.value) { $f.translator = New-Translator $p.accountName.value }
            if ($p.provisionStagingAccount.value) {
                $f.stage = New-Stage $p.stagingAccountName.value $f.translator.id
                $f.stagePolicy = @{ policy = @{ rules = @((Get-DocIntelTranslationLifecycleRule)) } }
            }
            $f.containers = @('translation-source', 'translation-target')
            foreach ($spec in @(Fixture-Specs $p)) {
                if ($spec.Assign) {
                    $principal = if ($spec.Principal) { $spec.Principal } else { $f.translator.identity.principalId }
                    $f.roles += @{ scope = $spec.Scope; principalId = $principal
                        roleDefinitionId = "/subscriptions/$($f.sub)/providers/Microsoft.Authorization/roleDefinitions/$($spec.Role)" }
                }
            }
            if ($f.scenario -eq 'post-provision-drift') { $f.app.properties.latestRevisionName = 'concurrent-write' }
            @{ properties = @{ provisioningState = 'Succeeded' } }; break
        }
        'containerapp update*' {
            $f.writes.Add($Arguments)
            $start = [Array]::IndexOf($Arguments, '--set-env-vars') + 1
            foreach ($setting in $Arguments[$start..($Arguments.Count - 1)]) {
                if ($setting.StartsWith('--', [StringComparison]::Ordinal)) { break }
                $name, $value = $setting -split '=', 2
                if ($name -cnotin @('AzureAI__TranslatorEndpoint', 'DocumentTranslation__StagingBlobEndpoint')) {
                    throw 'Only Translator/staging bindings may change.'
                }
                $f.app.properties.template.containers[0].env = @(
                    $f.app.properties.template.containers[0].env | Where-Object name -CNE $name
                ) + @(@{ name = $name; value = $value })
            }
            $f.app.properties.template.revisionSuffix = 'next'
            if ($f.scenario -eq 'unrelated-loss') {
                $f.app.properties.template.containers[0].env = @(
                    $f.app.properties.template.containers[0].env | Where-Object name -CNE 'UserOwned'
                )
            }
            $f.app; break
        }
        default { throw "Unexpected Azure command in isolated-staging test: $key" }
    }
    ConvertTo-Json -InputObject $result -Depth 100 -Compress
}
function Invoke-Translation([hashtable]$Extra = @{}) {
    & $file -ResourceGroup fixture -ContainerAppName fixture-app -OutputDirectory $output @Extra
}
function Assert-Failure([string]$Message, [hashtable]$Extra = @{}) {
    try { Invoke-Translation $Extra | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        if ($global:TranslationFixture.writes.Count) { throw 'Rejected plan mutated Azure.' }
        return
    }
    throw "Expected failure containing '$Message'."
}
function Apply-Translation {
    $preview = Invoke-Translation @{ ReplaceExistingEndpoint = $true }
    Invoke-Translation @{ Mode = 'Apply'; Confirmation = 'TRANSLATE'; ExpectedPlanHash = $preview.PlanHash
        ReplaceExistingEndpoint = $true }
}
try {
    New-Fixture
    Assert-Failure 'exact preceding Preview' @{ Mode = 'Apply'; Confirmation = 'TRANSLATE'; ExpectedPlanHash = 'bad' }
    if ($global:TranslationFixture.calls.Count) { throw 'Invalid apply must fail before Azure discovery.' }
    $preview = Invoke-Translation
    if (-not $preview.Plan.ProvisionStagingAccount -or $preview.Plan.StagingSku -cne 'Standard_LRS' -or
        $preview.Plan.Lifecycle.Status -cne 'CreateWithNewStagingAccount' -or
        $preview.Plan.Permissions.Count -ne 5 -or
        $preview.Plan.StagingBlobEndpoint -eq 'https://primaryaccount.blob.core.windows.net/') {
        throw 'A missing original policy must not prevent planning isolated, owned, expiring staging.'
    }
    Assert-Failure 'explicit -ReplaceExistingEndpoint' @{ Mode = 'Apply'; Confirmation = 'TRANSLATE'
        ExpectedPlanHash = $preview.PlanHash }
    $original = Get-DocIntelObjectHash @($global:TranslationFixture.primary, $global:TranslationFixture.primaryPolicy)
    $result = Apply-Translation
    if ($result.Status -cne 'Configured' -or $global:TranslationFixture.writes.Count -ne 2 -or
        $global:TranslationFixture.roles.Count -ne 5 -or
        (Get-DocIntelObjectHash @($global:TranslationFixture.primary, $global:TranslationFixture.primaryPolicy)) -cne $original) {
        throw 'Isolated staging must persist five narrow grants without touching original settings/policy.'
    }
    $global:TranslationFixture.writes.Clear()
    $preview = Invoke-Translation
    if ($preview.Plan.RequiresApply -or $preview.Plan.ProvisionStagingAccount) { throw 'Owned staging must reconcile idempotently.' }
    $null = Apply-Translation
    if ($global:TranslationFixture.writes.Count) { throw 'An already connected installation must not be rewritten.' }
    $global:TranslationFixture.app.properties.template.containers[0].env = @(
        $global:TranslationFixture.app.properties.template.containers[0].env |
            Where-Object name -CNE 'DocumentTranslation__StagingBlobEndpoint'
    )
    $preview = Invoke-Translation
    if ($preview.Plan.ProvisionStagingAccount) { throw 'A partial apply must rediscover owned staging, not create a duplicate.' }
    $null = Apply-Translation
    foreach ($case in @(
        @{ Scenario = 'escaped-create'; Type = 'Create'; Message = 'escaped exact isolated' }
        @{ Scenario = 'owned-modify'; Type = 'Modify'; Message = 'never modify/delete' }
    )) {
        New-Fixture; $global:TranslationFixture.scenario = $case.Scenario
        $failed = $false
        try { Invoke-Translation | Out-Null } catch {
            $failed = $true
            $message = $_.Exception.Message
            if ($message -notlike "*$($case.Message)*") { throw }
            if ($message -match 'fixture-before-secret|fixture-after-secret|PRIVATE_SETTING|properties|before|after') {
                throw 'Scope rejection must not expose raw ARM/environment payloads.'
            }
            $start = $message.IndexOf('{', [StringComparison]::Ordinal)
            if ($start -lt 0) { throw 'Scope rejection must identify the rejected resourceId and changeType.' }
            $context = $message.Substring($start) | ConvertFrom-Json
            if (@($context.PSObject.Properties).Count -ne 2 -or
                $context.resourceId -cne $global:TranslationFixture.rejectedId -or
                $context.changeType -cne $case.Type) {
                throw 'Rejected-change context must contain only the exact resourceId/changeType.'
            }
        }
        if (-not $failed -or $global:TranslationFixture.writes.Count) {
            throw 'Unexpected Create/owned Modify must remain rejected without mutations.'
        }
    }
    New-Fixture
    $initial = Invoke-Translation @{ ReplaceExistingEndpoint = $true }
    $f = $global:TranslationFixture
    $p = $f.parameters
    $f.translator = New-Translator (($initial.Plan.Resource -split '/')[-1])
    $f.stage = New-Stage (($initial.Plan.StagingResource -split '/')[-1]) $f.translator.id
    $f.stagePolicy = @{ policy = @{ rules = @((Get-DocIntelTranslationLifecycleRule)) } }
    $f.containers = @('translation-source', 'translation-target')
    foreach ($spec in @(Fixture-Specs $p) | Select-Object -First 3) {
        $principal = if ($spec.Principal) { $spec.Principal } else { $f.translator.identity.principalId }
        $f.roles += @{ scope = $spec.Scope; principalId = $principal
            roleDefinitionId = "/subscriptions/$($f.sub)/providers/Microsoft.Authorization/roleDefinitions/$($spec.Role)" }
    }
    $recovery = Invoke-Translation @{ ReplaceExistingEndpoint = $true }
    $missing = @($recovery.Plan.Permissions | Where-Object Assign)
    if ($recovery.Plan.ProvisionAccount -or $recovery.Plan.ProvisionStagingAccount -or
        $recovery.Plan.Lifecycle.Status -cne 'Ready' -or $missing.Count -ne 2 -or
        @($missing | Where-Object {
            $_.Principal -ine $f.identity.principalId -or $_.Role -cne 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' -or
                $_.Scope -notin @($recovery.Plan.Source.Scope, $recovery.Plan.Target.Scope)
        }).Count -or $f.writes.Count) {
        throw 'Read-only partial recovery must show only the two missing runtime-container grants, not retry them.'
    }
    foreach ($case in @(
        @{ Scenario = 'incomplete'; Message = 'preview is incomplete' }
        @{ Scenario = 'foreign-change'; Message = 'never modify/delete' }
        @{ Scenario = 'wrong-principal'; Message = 'exact isolated' }
        @{ Scenario = 'wrong-role'; Message = 'exact isolated' }
        @{ Scenario = 'omitted-role'; Message = 'omitted required' }
        @{ Scenario = 'policy-broadened'; Message = 'exact isolated' }
        @{ Scenario = 'global-collision'; Message = 'name collision' }
    )) {
        New-Fixture; $global:TranslationFixture.scenario = $case.Scenario
        Assert-Failure $case.Message
    }
    New-Fixture; $global:TranslationFixture.scenario = 'ignored-existing'
    $preview = Invoke-Translation @{ ReplaceExistingEndpoint = $true }
    if (($preview | ConvertTo-Json -Depth 100) -match 'fixture-sensitive-value' -or
        (Get-Content (Join-Path $output 'translation-plan.json') -Raw) -match 'fixture-sensitive-value') {
        throw 'Preview logs/artifacts must not contain unrelated ARM payloads.'
    }
    $global:TranslationFixture.ignoreRevision = 'changed'
    Assert-Failure 'changed since Preview' @{ Mode = 'Apply'; Confirmation = 'TRANSLATE'
        ExpectedPlanHash = $preview.PlanHash; ReplaceExistingEndpoint = $true }
    New-Fixture
    $preview = Invoke-Translation
    $f = $global:TranslationFixture
    $f.stage = New-Stage (([uri]$preview.Plan.StagingBlobEndpoint).Host.Split('.')[0]) $preview.Plan.Resource
    $f.stage.tags['docintel-purpose'] = 'another-owner'
    Assert-Failure 'unknown ownership'
    New-Fixture
    $global:TranslationFixture.app.properties.template.containers[0].env += @{
        name = 'DocumentTranslation__StagingBlobEndpoint'; value = 'https://primaryaccount.blob.core.windows.net/'
    }
    Assert-Failure 'Original-artifact storage is forbidden'
    foreach ($case in 'missing', 'disabled', 'wide-prefix', 'unreadable') {
        New-Fixture; $null = Apply-Translation
        $f = $global:TranslationFixture; $f.writes.Clear()
        switch ($case) {
            'missing' { $f.stagePolicy = @{ policy = @{ rules = @() } } }
            'disabled' { $f.stagePolicy.policy.rules[0].enabled = $false }
            'wide-prefix' { $f.stagePolicy.policy.rules[0].definition.filters.prefixMatch = @('translation-source/') }
            'unreadable' { $f.scenario = 'unreadable-policy' }
        }
        $preview = Invoke-Translation @{ ReplaceExistingEndpoint = $true }
        if ($preview.Plan.Lifecycle.Status -cne 'ActionRequired') { throw 'Owned staging expiry must be revalidated.' }
        Assert-Failure 'whole-policy writes' @{ Mode = 'Apply'; Confirmation = 'TRANSLATE'
            ExpectedPlanHash = $preview.PlanHash; ReplaceExistingEndpoint = $true }
    }
    New-Fixture; $null = Apply-Translation
    $f = $global:TranslationFixture; $f.writes.Clear()
    $preview = Invoke-Translation @{ ReplaceExistingEndpoint = $true }
    $f.stage.tags['docintel-owner-app'] = 'concurrent-owner'
    Assert-Failure 'unknown ownership' @{ Mode = 'Apply'; Confirmation = 'TRANSLATE'
        ExpectedPlanHash = $preview.PlanHash; ReplaceExistingEndpoint = $true }
    foreach ($case in @(
        @{ Scenario = 'unrelated-loss'; Message = 'Unrelated app settings' }
        @{ Scenario = 'post-provision-drift'; Message = 'Runtime settings were not overwritten' }
    )) {
        New-Fixture; $global:TranslationFixture.scenario = $case.Scenario
        $failed = $false
        try { $null = Apply-Translation } catch {
            if ($_.Exception.Message -notlike "*$($case.Message)*") { throw }
            $failed = $true
        }
        if (-not $failed) { throw 'Concurrent changes/setting loss must not be reported as a successful connection.' }
    }
    Write-Output 'Isolated staging creation/reuse, ownership/collision fencing, five narrow grants, one-day expiry, no original-policy access, redacted previews and app preservation passed offline.'
}
finally {
    Remove-Item Function:\az
    Remove-Variable TranslationFixture -Scope Global -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $output -Recurse -Force -ErrorAction SilentlyContinue
}
