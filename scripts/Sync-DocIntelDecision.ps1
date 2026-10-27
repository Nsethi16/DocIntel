#Requires -Version 7.3
<#
.SYNOPSIS
    Preview or apply the bounded native Decision connection required by an existing app release.
.DESCRIPTION
    Reuses only a verified native model, or adds that model to the app's bound Foundry account.
    Never changes language/OCR bindings, account properties, deployment-principal permissions or image.
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
    [string]$DecisionResourceId = $env:DOCINTEL_DECISION_RESOURCE_ID,
    [string]$DecisionEndpoint = $env:DOCINTEL_DECISION_ENDPOINT,
    [string]$DecisionDeploymentName = $env:DOCINTEL_DECISION_DEPLOYMENT
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Import-Module (Join-Path $PSScriptRoot 'DocIntelRelease.psm1') -Force
if ($Mode -eq 'Apply' -and ($Confirmation -cne 'CONNECT' -or
    $ExpectedPlanHash -cnotmatch '^[a-f0-9]{64}$')) {
    throw 'Apply requires CONNECT and the exact preceding Preview plan hash.'
}
if ($ResourceGroup -notmatch '^[A-Za-z0-9_.()-]{1,90}$' -or
    $ContainerAppName -notmatch '^[a-z][a-z0-9-]{1,30}[a-z0-9]$') {
    throw 'Invalid release target.'
}
function Invoke-DecisionAzure {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $result = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) { throw "Decision setup Azure operation '$($Arguments[0..1] -join ' ')' failed." }
    if (-not [String]::IsNullOrWhiteSpace(($result -join "`n"))) {
        return ($result -join "`n") | ConvertFrom-Json -Depth 100
    }
}
function Get-DecisionSetting([string]$Name) {
    $entries = @($container.env | Where-Object name -CEQ $Name)
    if ($entries.Count -gt 1 -or $entries.secretRef) { throw "Resolve duplicate/secret-backed '$Name' explicitly." }
    return [string]$entries.value
}
function Get-DecisionRoot([object]$Account) {
    $domain = [string]$Account.properties.customSubDomainName
    if ($Account.kind -cne 'AIServices' -or $domain -cnotmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,62}$') {
        throw 'Decision requires a validated AIServices account with a custom subdomain.'
    }
    return "https://$domain.services.ai.azure.com/"
}
function Assert-DecisionDeployment([object]$Deployment) {
    if ($Deployment.properties.model.name -cne 'Microsoft-Decision-1' -or
        $Deployment.properties.model.format -cne 'Microsoft' -or
        $Deployment.properties.provisioningState -cne 'Succeeded') {
        throw 'The selected deployment is not a successfully provisioned native Microsoft-Decision-1 model.'
    }
}
function Read-DecisionApp {
    return Invoke-DecisionAzure @('containerapp', 'show', '--subscription', $SubscriptionId,
        '--resource-group', $ResourceGroup, '--name', $ContainerAppName)
}
$login = Invoke-DecisionAzure @('account', 'show')
if (-not $SubscriptionId) { $SubscriptionId = [string]$login.id }
if ($SubscriptionId -notmatch '^[a-fA-F0-9-]{36}$' -or $login.id -ine $SubscriptionId) {
    throw 'Sign in to the selected subscription before connecting Decision.'
}
$app = Read-DecisionApp
$groupScope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup"
if ($app.id -ine "$groupScope/providers/Microsoft.App/containerApps/$ContainerAppName") {
    throw 'The application does not belong to the selected release target.'
}
$selected = @($app.properties.template.containers | Where-Object {
    -not $ContainerName -or $_.name -ceq $ContainerName
})
if ($selected.Count -ne 1) { throw 'Select exactly one application container.' }
$container = $selected[0]
$clientId = Get-DecisionSetting 'AZURE_CLIENT_ID'
$assigned = @($app.identity.userAssignedIdentities.PSObject.Properties | Where-Object {
    $_.Value.clientId -ieq $clientId -and $clientId
})
if ($assigned.Count -ne 1) { throw 'The configured runtime client is not an unambiguous assigned managed identity.' }
$identity = Invoke-DecisionAzure @('identity', 'show', '--subscription', $SubscriptionId, '--ids', $assigned[0].Name)
if ($identity.id -ine $assigned[0].Name -or $identity.clientId -ine $clientId -or
    $identity.principalId -ine $assigned[0].Value.principalId -or
    $identity.tenantId -ine $login.tenantId -or
    $identity.principalId -notmatch '^[a-fA-F0-9-]{36}$') {
    throw 'Runtime identity metadata does not match the application assignment and directory.'
}
$savedEndpoint = Get-DecisionSetting 'AzureAI__DecisionEndpoint'
$savedDeployment = Get-DecisionSetting 'AzureAI__DecisionDeploymentName'
if ($DecisionEndpoint -and $savedEndpoint -and $DecisionEndpoint.TrimEnd('/') -ine $savedEndpoint.TrimEnd('/')) {
    throw 'An existing Decision connection cannot be silently moved to another endpoint.'
}
if ($DecisionDeploymentName -and $savedDeployment -and $DecisionDeploymentName -cne $savedDeployment) {
    throw 'An existing Decision deployment selection cannot be silently replaced.'
}
if (-not $DecisionEndpoint) { $DecisionEndpoint = $savedEndpoint }
if (-not $DecisionDeploymentName) { $DecisionDeploymentName = $savedDeployment }
if ($DecisionEndpoint) {
    $uri = $null
    if (-not [uri]::TryCreate($DecisionEndpoint, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne 'https' -or $uri.Port -ne 443 -or $uri.AbsolutePath -cne '/' -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'Decision endpoint must be its HTTPS resource root.' }
    $DecisionEndpoint = $uri.AbsoluteUri
}
if ($DecisionDeploymentName -and $DecisionDeploymentName -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$') {
    throw 'Invalid Decision deployment name.'
}
$accounts = @(Invoke-DecisionAzure @('cognitiveservices', 'account', 'list',
    '--subscription', $SubscriptionId, '--resource-group', $ResourceGroup))
$accounts = @($accounts | Where-Object kind -CEQ 'AIServices')
if ($DecisionResourceId) {
    if ($DecisionResourceId -notmatch "^$([regex]::Escape($groupScope))/providers/Microsoft.CognitiveServices/accounts/[^/]+$") {
        throw 'Decision resource must be explicitly selected within the application resource group.'
    }
    $accounts = @($accounts | Where-Object id -IEQ $DecisionResourceId)
    if ($accounts.Count -ne 1) { throw 'The selected Decision resource is not an existing AIServices account.' }
}
if ($DecisionEndpoint) {
    $accounts = @($accounts | Where-Object { (Get-DecisionRoot $_) -ieq $DecisionEndpoint })
    if ($accounts.Count -ne 1) { throw 'Decision endpoint does not match a selected same-group AIServices account.' }
}
$bound = Get-DecisionSetting 'AzureAI__ContentUnderstandingEndpoint'
$inventory = @(
    foreach ($account in $accounts) {
        if ($account.id -notlike "$groupScope/providers/Microsoft.CognitiveServices/accounts/*") {
            throw 'The account inventory escaped the selected resource group.'
        }
        $endpoint = Get-DecisionRoot $account
        $deployments = @(Invoke-DecisionAzure @('cognitiveservices', 'account', 'deployment', 'list',
            '--subscription', $SubscriptionId, '--resource-group', $ResourceGroup, '--name', $account.name))
        foreach ($deployment in $deployments) {
            if ($deployment.properties.model.name -ceq 'Microsoft-Decision-1' -and
                (-not $DecisionDeploymentName -or $deployment.name -ceq $DecisionDeploymentName)) {
                Assert-DecisionDeployment $deployment
                [pscustomobject]@{ Account = $account; Endpoint = $endpoint; Deployment = $deployment.name }
            }
        }
    }
)
$preferred = @($inventory | Where-Object { $_.Endpoint.TrimEnd('/') -ieq $bound.TrimEnd('/') })
if ($preferred.Count) { $inventory = $preferred }
if ($inventory.Count -gt 1) { throw 'Multiple native Decision deployments match. Explicitly select a resource/deployment.' }
$provisionModel = $inventory.Count -eq 0
if ($provisionModel) {
    $targets = if ($DecisionResourceId -or $DecisionEndpoint) { $accounts } else {
        @($accounts | Where-Object { (Get-DecisionRoot $_).TrimEnd('/') -ieq $bound.TrimEnd('/') })
    }
    if (@($targets).Count -ne 1) { throw 'No native Decision deployment or unambiguous bound Foundry account exists.' }
    $target = $targets[0]
    if (-not $DecisionDeploymentName) { $DecisionDeploymentName = 'Decision-1' }
    $existing = @(Invoke-DecisionAzure @('cognitiveservices', 'account', 'deployment', 'list',
        '--subscription', $SubscriptionId, '--resource-group', $ResourceGroup, '--name', $target.name))
    if (@($existing | Where-Object name -CEQ $DecisionDeploymentName).Count) {
        throw 'The requested deployment name already belongs to a different model. No replacement is allowed.'
    }
    $available = @(Invoke-DecisionAzure @('cognitiveservices', 'model', 'list',
        '--subscription', $SubscriptionId, '--location', $target.location) | Where-Object {
        $_.kind -ceq 'AIServices' -and $_.model.format -ceq 'Microsoft' -and
            $_.model.name -ceq 'Microsoft-Decision-1' -and $_.model.version -ceq '1'
    })
    $sku = @($available.model.skus | Where-Object name -CEQ 'GlobalStandard')
    if ($available.Count -ne 1 -or $sku.Count -ne 1 -or -not $sku[0].usageName) {
        throw 'Native Microsoft Decision version 1 / GlobalStandard is unavailable in the bound account region.'
    }
    $usage = @(Invoke-DecisionAzure @('cognitiveservices', 'usage', 'list',
        '--subscription', $SubscriptionId, '--location', $target.location))
    $quota = @($usage | Where-Object { $_.name.value -ceq $sku[0].usageName })
    if ($quota.Count -ne 1 -or $null -eq $quota[0].limit -or $null -eq $quota[0].currentValue -or
        [double]$quota[0].limit - [double]$quota[0].currentValue -lt 1) {
        throw 'Obtain one unit of native Decision quota before releasing. No GPT substitution is used.'
    }
    $DecisionEndpoint = Get-DecisionRoot $target
} else {
    $target = $inventory[0].Account
    $DecisionEndpoint = $inventory[0].Endpoint
    $DecisionDeploymentName = $inventory[0].Deployment
}
$roleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
$roles = @(Invoke-DecisionAzure @('role', 'assignment', 'list', '--subscription', $SubscriptionId,
    '--scope', $target.id, '--include-inherited'))
$matchingRoles = @($roles | Where-Object {
    $_.principalId -ieq $identity.principalId -and ($_.roleDefinitionId -split '/')[-1] -ieq $roleId
})
if (@($matchingRoles | Where-Object { $_.condition }).Count) {
    throw 'The runtime Decision grant is conditional; operator review is required instead of bypassing its restriction.'
}
$assignRole = $matchingRoles.Count -eq 0
$template = Join-Path (Split-Path $PSScriptRoot -Parent) 'infra\decision-connection.bicep'
$parameterFile = Join-Path ([IO.Path]::GetTempPath()) ('docintel-decision-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    $parameters = @{ parameters = @{} }
    foreach ($entry in @{
        accountName = $target.name; deploymentName = $DecisionDeploymentName
        runtimePrincipalId = $identity.principalId; runtimeIdentityResourceId = $identity.id
        provisionModel = $provisionModel; assignRuntimeRole = $assignRole
    }.GetEnumerator()) { $parameters.parameters[$entry.Key] = @{ value = $entry.Value } }
    $parameters | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $parameterFile -Encoding utf8
    $changes = @()
    if ($provisionModel -or $assignRole) {
        $whatIf = Invoke-DecisionAzure @('deployment', 'group', 'what-if', '--subscription', $SubscriptionId,
            '--resource-group', $ResourceGroup, '--template-file', $template, '--parameters', "@$parameterFile",
            '--result-format', 'FullResourcePayloads', '--no-pretty-print')
        if ($whatIf.status -cne 'Succeeded' -or
            @($whatIf.diagnostics | Where-Object { $null -ne $_ }).Count -or
            @($whatIf.potentialChanges | Where-Object { $null -ne $_ }).Count) {
            throw 'Native Decision ARM preview is incomplete. No apply is allowed.'
        }
        $changes = @($whatIf.changes | Where-Object { $null -ne $_ })
        $expectedCreates = [int]$provisionModel + [int]$assignRole
        if (@($changes | Where-Object changeType -CEQ 'Create').Count -ne $expectedCreates) {
            throw 'Decision preview must contain exactly the requested new model/role resources.'
        }
        foreach ($change in $changes) {
            if ($change.changeType -ceq 'NoChange') { continue }
            if ($change.changeType -ceq 'Ignore' -and
                $change.resourceId.StartsWith($groupScope + '/', [StringComparison]::OrdinalIgnoreCase) -and
                $change.resourceId -notmatch '/providers/Microsoft.Resources/deployments/' -and
                -not ($provisionModel -and $change.resourceId -ieq "$($target.id)/deployments/$DecisionDeploymentName") -and
                -not ($assignRole -and $change.resourceId -like "$($target.id)/providers/Microsoft.Authorization/roleAssignments/*")) {
                continue
            }
            $modelChange = $provisionModel -and $change.resourceId -ieq "$($target.id)/deployments/$DecisionDeploymentName" -and
                $change.after.properties.model.name -ceq 'Microsoft-Decision-1' -and
                $change.after.properties.model.format -ceq 'Microsoft' -and
                $change.after.properties.model.version -ceq '1' -and
                $change.after.sku.name -ceq 'GlobalStandard' -and $change.after.sku.capacity -eq 1
            $roleChange = $assignRole -and $change.resourceId -match "^$([regex]::Escape($target.id))/providers/Microsoft.Authorization/roleAssignments/[a-fA-F0-9-]{36}$" -and
                $change.after.properties.principalId -ieq $identity.principalId -and
                $change.after.properties.roleDefinitionId -ieq "/subscriptions/$SubscriptionId/providers/Microsoft.Authorization/roleDefinitions/$roleId"
            if ($change.changeType -cne 'Create' -or (-not $modelChange -and -not $roleChange)) {
                throw 'Decision preview would modify/delete an existing resource or escape the exact model/runtime-role scope.'
            }
        }
    }
    $plan = @{
        Application = $app.id; Container = $container.name; SnapshotHash = Get-DocIntelObjectHash $app
        Resource = $target.id; Endpoint = $DecisionEndpoint; Deployment = $DecisionDeploymentName
        RuntimeIdentity = $identity.id; RuntimePrincipal = $identity.principalId
        ProvisionModel = $provisionModel; AssignRuntimeRole = $assignRole
        TemplateHash = (Get-FileHash -LiteralPath $template -Algorithm SHA256).Hash
        Changes = @($changes | Sort-Object resourceId)
    }
    $planHash = Get-DocIntelObjectHash $plan
    if ($Mode -eq 'Preview') { return [pscustomobject]@{ PlanHash = $planHash; Plan = $plan } }
    if ($ExpectedPlanHash -cne $planHash) { throw 'Decision targets or plan changed since Preview. No writes were performed.' }
    if ((Get-DocIntelObjectHash (Read-DecisionApp)) -cne $plan.SnapshotHash) {
        throw 'The application changed during Decision setup. No writes were performed.'
    }
    if ($provisionModel -or $assignRole) {
        $deploymentResult = Invoke-DecisionAzure @('deployment', 'group', 'create', '--subscription', $SubscriptionId,
            '--resource-group', $ResourceGroup, '--name', 'docintel-native-decision',
            '--template-file', $template, '--parameters', "@$parameterFile")
        if ($deploymentResult.properties.provisioningState -cne 'Succeeded') { throw 'Decision infrastructure did not succeed.' }
    }
    $live = @(Invoke-DecisionAzure @('cognitiveservices', 'account', 'deployment', 'list',
        '--subscription', $SubscriptionId, '--resource-group', $ResourceGroup, '--name', $target.name) |
        Where-Object name -CEQ $DecisionDeploymentName)
    if ($live.Count -ne 1) { throw 'The native Decision deployment could not be verified after provisioning.' }
    Assert-DecisionDeployment $live[0]
    if ((Get-DocIntelObjectHash (Read-DecisionApp)) -cne $plan.SnapshotHash) {
        throw 'Application changed while Decision infrastructure was prepared. Its runtime configuration was not overwritten.'
    }
    if ($savedEndpoint -cne $DecisionEndpoint -or $savedDeployment -cne $DecisionDeploymentName) {
        Invoke-DecisionAzure @('containerapp', 'update', '--subscription', $SubscriptionId,
            '--resource-group', $ResourceGroup, '--name', $ContainerAppName, '--container-name', $container.name,
            '--set-env-vars', "AzureAI__DecisionEndpoint=$DecisionEndpoint",
            "AzureAI__DecisionDeploymentName=$DecisionDeploymentName") | Out-Null
    }
    $verified = Read-DecisionApp
    $verifiedContainer = @($verified.properties.template.containers | Where-Object name -CEQ $container.name)
    if ($verifiedContainer.Count -ne 1 -or $verifiedContainer[0].image -cne $container.image -or
        (Get-DocIntelObjectHash $verified.identity) -cne (Get-DocIntelObjectHash $app.identity) -or
        (Get-DocIntelObjectHash $verified.properties.configuration) -cne (Get-DocIntelObjectHash $app.properties.configuration)) {
        throw 'The application image, identity or configuration changed unexpectedly. Connection success is not claimed.'
    }
    $expectedEnvironment = @($container.env | Where-Object {
        $_.name -cnotin @('AzureAI__DecisionEndpoint', 'AzureAI__DecisionDeploymentName')
    })
    $remainingEnvironment = @($verifiedContainer[0].env | Where-Object {
        $_.name -cnotin @('AzureAI__DecisionEndpoint', 'AzureAI__DecisionDeploymentName')
    })
    if ((Get-DocIntelObjectHash @($expectedEnvironment | Sort-Object name)) -cne
        (Get-DocIntelObjectHash @($remainingEnvironment | Sort-Object name))) {
        throw 'An unrelated application setting changed. Connection success is not claimed.'
    }
    foreach ($entry in @{ AzureAI__DecisionEndpoint = $DecisionEndpoint
        AzureAI__DecisionDeploymentName = $DecisionDeploymentName }.GetEnumerator()) {
        $actual = @($verifiedContainer[0].env | Where-Object name -CEQ $entry.Key)
        if ($actual.Count -ne 1 -or $actual[0].value -cne $entry.Value -or $actual[0].secretRef) {
            throw 'The native Decision connection was not persisted.'
        }
    }
    return [pscustomobject]@{ Status = 'Configured'; Resource = $target.id; Deployment = $DecisionDeploymentName
        Model = 'Microsoft-Decision-1'; RuntimeAccess = 'Inference readiness must be verified by the new revision.' }
}
finally { Remove-Item -LiteralPath $parameterFile -Force -ErrorAction SilentlyContinue }
