#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
$file = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Sync-DocIntelDecision.ps1'
function New-DecisionFixture {
    $subscription = [guid]::NewGuid().ToString()
    $directory = [guid]::NewGuid().ToString()
    $prefix = "/subscriptions/$subscription/resourceGroups/fixture/providers"
    $identity = @{ id = "$prefix/Microsoft.ManagedIdentity/userAssignedIdentities/runtime"
        clientId = [guid]::NewGuid().ToString(); principalId = [guid]::NewGuid().ToString(); tenantId = $directory }
    $native = @{ name = 'Decision-1'; properties = @{ provisioningState = 'Succeeded'
        model = @{ name = 'Microsoft-Decision-1'; format = 'Microsoft'; version = '1' } } }
    $global:DecisionFixture = @{
        subscription = $subscription; directory = $directory; identity = $identity; prefix = $prefix
        roles = @(); changes = [Collections.Generic.List[object]]::new(); scenario = ''
        accounts = @(
            @{ name = 'language'; id = "$prefix/Microsoft.CognitiveServices/accounts/language"
                kind = 'AIServices'; location = 'westus'; properties = @{ customSubDomainName = 'language' } }
            @{ name = 'native'; id = "$prefix/Microsoft.CognitiveServices/accounts/native"
                kind = 'AIServices'; location = 'westus'; properties = @{ customSubDomainName = 'native' } }
        )
        deployments = @{ language = @(@{ name = 'gpt-5-mini'; properties = @{
            provisioningState = 'Succeeded'; model = @{ name = 'gpt-5-mini'; format = 'OpenAI' } } }); native = @($native) }
        app = @{
            id = "$prefix/Microsoft.App/containerApps/fixture-app"
            identity = @{ userAssignedIdentities = @{ $identity.id = @{
                clientId = $identity.clientId; principalId = $identity.principalId } } }
            properties = @{
                latestRevisionName = 'existing'
                configuration = @{ ingress = @{ fqdn = 'fixture.example' }; secrets = @(@{ name = 'keep-secret' }) }
                template = @{ containers = @(@{
                    name = 'docintel-api'; image = 'fixture.azurecr.io/app:keep'
                    env = @(
                        @{ name = 'AZURE_CLIENT_ID'; value = $identity.clientId }
                        @{ name = 'AzureAI__ContentUnderstandingEndpoint'; value = 'https://language.services.ai.azure.com/' }
                        @{ name = 'AzureAI__AzureOpenAIEndpoint'; value = 'https://language.openai.azure.com/' }
                        @{ name = 'UserOwned'; secretRef = 'keep-secret' }
                    )
                }) }
            }
        }
    }
}
function az {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $f = $global:DecisionFixture
    $global:LASTEXITCODE = 0
    $key = ($Arguments | Select-Object -First 4) -join ' '
    $result = switch -Wildcard ($key) {
        'account show*' { @{ id = $f.subscription; tenantId = $f.directory }; break }
        'containerapp show*' { $f.app; break }
        'identity show*' { $f.identity; break }
        'cognitiveservices account list*' { $f.accounts; break }
        'cognitiveservices account deployment list' {
            $name = $Arguments[[Array]::IndexOf($Arguments, '--name') + 1]
            $f.deployments[$name]; break
        }
        'cognitiveservices model list*' {
            @(@{ kind = 'AIServices'; model = @{ name = 'Microsoft-Decision-1'; format = 'Microsoft'
                version = '1'; skus = @(@{ name = 'GlobalStandard'; usageName = 'native-quota' }) } }); break
        }
        'cognitiveservices usage list*' {
            @(@{ name = @{ value = 'native-quota' }; limit = $(if ($f.scenario -eq 'quota') { 0 } else { 1 })
                currentValue = 0 }); break
        }
        'role assignment list*' {
            if ($f.scenario -eq 'role-read-failed') { $global:LASTEXITCODE = 1; break }
            $f.roles; break
        }
        'deployment group what-if*' {
            $path = $Arguments[[Array]::IndexOf($Arguments, '--parameters') + 1].Substring(1)
            $p = (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).parameters
            $scope = "$($f.prefix)/Microsoft.CognitiveServices/accounts/$($p.accountName.value)"
            $changes = @()
            if ($p.provisionModel.value) {
                $changes += @{ resourceId = "$scope/deployments/$($p.deploymentName.value)"; changeType = 'Create'
                    after = @{ sku = @{ name = 'GlobalStandard'; capacity = 1 }; properties = @{
                        model = @{ name = 'Microsoft-Decision-1'; format = 'Microsoft'; version = '1' } } } }
            }
            if ($p.assignRuntimeRole.value) {
                $changes += @{ resourceId = "$scope/providers/Microsoft.Authorization/roleAssignments/11111111-1111-1111-1111-111111111111"
                    changeType = 'Create'; after = @{ properties = @{
                        principalId = $p.runtimePrincipalId.value
                        roleDefinitionId = "/subscriptions/$($f.subscription)/providers/Microsoft.Authorization/roleDefinitions/a97b65f3-24c7-4388-baec-2e87135dc908"
                    } } }
            }
            if ($f.scenario -eq 'foreign-change') {
                $changes += @{ resourceId = "$($f.prefix)/Microsoft.Storage/storageAccounts/keep"; changeType = 'Modify' }
            }
            if ($f.scenario -eq 'ignored-existing') {
                $changes += @{ resourceId = "$($f.prefix)/Microsoft.Storage/storageAccounts/keep"; changeType = 'Ignore' }
            }
            if ($f.scenario -eq 'ignored-target') { $changes[-1].changeType = 'Ignore' }
            if ($f.scenario -eq 'wrong-principal') { $changes[-1].after.properties.principalId = [guid]::NewGuid().ToString() }
            @{ status = 'Succeeded'; changes = $changes
                diagnostics = $(if ($f.scenario -eq 'incomplete') { @(@{ code = 'Unsupported' }) } else { @() }) }
            break
        }
        'deployment group create*' {
            $f.changes.Add($Arguments)
            $path = $Arguments[[Array]::IndexOf($Arguments, '--parameters') + 1].Substring(1)
            $p = (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).parameters
            if ($p.provisionModel.value) {
                $f.deployments[$p.accountName.value] += @{ name = $p.deploymentName.value; properties = @{
                    provisioningState = 'Succeeded'; model = @{ name = 'Microsoft-Decision-1'; format = 'Microsoft' } } }
            }
            @{ properties = @{ provisioningState = 'Succeeded' } }; break
        }
        'containerapp update*' {
            $f.changes.Add($Arguments)
            $start = [Array]::IndexOf($Arguments, '--set-env-vars') + 1
            foreach ($argument in $Arguments[$start..($Arguments.Count - 1)]) {
                if ($argument.StartsWith('--', [StringComparison]::Ordinal)) { break }
                $name, $value = $argument -split '=', 2
                $f.app.properties.template.containers[0].env = @(
                    $f.app.properties.template.containers[0].env | Where-Object name -CNE $name
                ) + @(@{ name = $name; value = $value })
            }
            if ($f.scenario -eq 'unrelated-loss') {
                $f.app.properties.template.containers[0].env = @(
                    $f.app.properties.template.containers[0].env | Where-Object name -CNE 'UserOwned'
                )
            }
            $f.app; break
        }
        default { throw "Unexpected Azure command in native Decision regression test: $key" }
    }
    ConvertTo-Json -InputObject $result -Depth 100 -Compress
}
function Assert-DecisionFailure([string]$Message, [hashtable]$Extra = @{}) {
    try { & $file -ResourceGroup fixture -ContainerAppName fixture-app @Extra | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        if ($global:DecisionFixture.changes.Count) { throw 'Rejected preview mutated Azure.' }
        return
    }
    throw "Expected failure containing '$Message'."
}
function Apply-Decision {
    $preview = & $file -ResourceGroup fixture -ContainerAppName fixture-app
    return & $file -ResourceGroup fixture -ContainerAppName fixture-app -Mode Apply `
        -Confirmation CONNECT -ExpectedPlanHash $preview.PlanHash
}
try {
    New-DecisionFixture
    $preview = & $file -ResourceGroup fixture -ContainerAppName fixture-app
    if ($global:DecisionFixture.changes.Count -or $preview.Plan.Resource -notlike '*/accounts/native' -or
        $preview.Plan.ProvisionModel -or -not $preview.Plan.AssignRuntimeRole) {
        throw 'Preview must uniquely reuse the actual separate native model without mutations.'
    }
    $result = Apply-Decision
    if ($result.Status -cne 'Configured' -or $result.Model -cne 'Microsoft-Decision-1' -or
        $global:DecisionFixture.changes.Count -ne 2) { throw 'Native role/configuration were not reconciled.' }
    $env = $global:DecisionFixture.app.properties.template.containers[0].env
    if (($env | Where-Object name -CEQ 'AzureAI__AzureOpenAIEndpoint').value -cne 'https://language.openai.azure.com/' -or
        ($env | Where-Object name -CEQ 'UserOwned').secretRef -cne 'keep-secret' -or
        ($env | Where-Object name -CEQ 'AzureAI__DecisionEndpoint').value -cne 'https://native.services.ai.azure.com/') {
        throw 'Provider separation or unrelated secret preservation failed.'
    }
    New-DecisionFixture; $global:DecisionFixture.scenario = 'ignored-existing'
    $null = Apply-Decision
    if ($global:DecisionFixture.changes.Count -ne 2) { throw 'Incremental previews may ignore untouched existing resources.' }
    New-DecisionFixture
    $global:DecisionFixture.roles = @(@{ principalId = $global:DecisionFixture.identity.principalId
        roleDefinitionId = '/providers/Microsoft.Authorization/roleDefinitions/a97b65f3-24c7-4388-baec-2e87135dc908' })
    $null = Apply-Decision
    if ($global:DecisionFixture.changes.Count -ne 1) { throw 'Existing inherited access must avoid an ARM write.' }
    $global:DecisionFixture.changes.Clear()
    $null = Apply-Decision
    if ($global:DecisionFixture.changes.Count) { throw 'An already connected installation must be a read-only no-op.' }
    New-DecisionFixture
    $global:DecisionFixture.deployments.native = @()
    $preview = & $file -ResourceGroup fixture -ContainerAppName fixture-app
    if (-not $preview.Plan.ProvisionModel -or $preview.Plan.Resource -notlike '*/accounts/language') {
        throw 'Missing native model must be provisioned only in the actual bound Foundry account.'
    }
    $null = Apply-Decision
    if (@($global:DecisionFixture.deployments.language).Count -ne 2 -or
        $global:DecisionFixture.deployments.language[0].name -cne 'gpt-5-mini') {
        throw 'Native provisioning must preserve the language deployment.'
    }
    foreach ($case in @(
        @{ Scenario = 'incomplete'; Message = 'preview is incomplete' }
        @{ Scenario = 'foreign-change'; Message = 'modify/delete' }
        @{ Scenario = 'wrong-principal'; Message = 'exact model/runtime-role scope' }
        @{ Scenario = 'role-read-failed'; Message = 'operation' }
        @{ Scenario = 'ignored-target'; Message = 'exactly the requested' }
    )) {
        New-DecisionFixture; $global:DecisionFixture.scenario = $case.Scenario
        Assert-DecisionFailure $case.Message
    }
    New-DecisionFixture; $global:DecisionFixture.deployments.native = @(); $global:DecisionFixture.scenario = 'quota'
    Assert-DecisionFailure 'quota'
    New-DecisionFixture
    $global:DecisionFixture.deployments.language += $global:DecisionFixture.deployments.native[0]
    $preview = & $file -ResourceGroup fixture -ContainerAppName fixture-app
    if ($preview.Plan.Resource -notlike '*/accounts/language') { throw 'Bound native account must take precedence.' }
    New-DecisionFixture
    $global:DecisionFixture.deployments.native += @{ name = 'Another-Decision'; properties = @{
        provisioningState = 'Succeeded'; model = @{ name = 'Microsoft-Decision-1'; format = 'Microsoft' } } }
    Assert-DecisionFailure 'Multiple native'
    $preview = & $file -ResourceGroup fixture -ContainerAppName fixture-app -DecisionDeploymentName 'Decision-1'
    if ($preview.Plan.Deployment -cne 'Decision-1') { throw 'Explicit deployment selection was not honored.' }
    New-DecisionFixture
    $global:DecisionFixture.roles = @(@{ principalId = $global:DecisionFixture.identity.principalId
        roleDefinitionId = '/roles/a97b65f3-24c7-4388-baec-2e87135dc908'; condition = 'restricted' })
    Assert-DecisionFailure 'conditional'
    New-DecisionFixture
    $preview = & $file -ResourceGroup fixture -ContainerAppName fixture-app
    $global:DecisionFixture.app.properties.latestRevisionName = 'concurrent-write'
    Assert-DecisionFailure 'changed since Preview' @{ Mode = 'Apply'; Confirmation = 'CONNECT'
        ExpectedPlanHash = $preview.PlanHash }
    New-DecisionFixture
    Assert-DecisionFailure 'exact preceding Preview' @{ Mode = 'Apply'; Confirmation = 'CONNECT'; ExpectedPlanHash = 'bad' }
    Assert-DecisionFailure 'resource root' @{ DecisionEndpoint = 'https://native.services.ai.azure.com/models' }
    Assert-DecisionFailure 'same-group' @{ DecisionEndpoint = 'https://outside.services.ai.azure.com/' }
    New-DecisionFixture
    $global:DecisionFixture.identity.tenantId = [guid]::NewGuid().ToString()
    Assert-DecisionFailure 'metadata does not match'
    New-DecisionFixture
    $global:DecisionFixture.deployments.native = @()
    $global:DecisionFixture.deployments.language += @{ name = 'Decision-1'; properties = @{
        model = @{ name = 'gpt-5-mini'; format = 'OpenAI' } } }
    Assert-DecisionFailure 'different model'
    New-DecisionFixture; $global:DecisionFixture.scenario = 'unrelated-loss'
    $failed = $false
    try { $null = Apply-Decision } catch {
        if ($_.Exception.Message -notlike '*unrelated application setting*') { throw }
        $failed = $true
    }
    if (-not $failed) { throw 'Connection success must not hide an unrelated setting loss.' }
    Write-Output 'Native Decision discovery, minimal provisioning, runtime-only access, plan fencing, preservation and failure tests passed without Azure calls.'
}
finally {
    Remove-Item Function:\az
    Remove-Variable DecisionFixture -Scope Global -ErrorAction SilentlyContinue
}
