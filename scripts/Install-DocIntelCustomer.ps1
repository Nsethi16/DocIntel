#Requires -Version 7.3
<#
.SYNOPSIS
    Preview and provision an isolated, fresh customer installation using a public, digest-pinned image.
.DESCRIPTION
    No mutation occurs in Preview. Apply requires the reviewed plan hash and APPLY confirmation.
    Existing nonempty resource groups are refused; this is not an adoption or upgrade tool.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CustomerSlug,
    [Parameter(Mandatory)][string]$Image,
    [string]$Location = 'westus',
    [string]$EnvironmentName = 'prod',
    [string]$SubscriptionId = '',
    [string]$ResourceGroup = '',
    [string]$InitialOwnerObjectId = '',
    [ValidateSet('Preview', 'Apply')][string]$Mode = 'Preview',
    [string]$Confirmation = '',
    [string]$ExpectedPlanHash = '',
    [string]$OutputDirectory = ''
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
if ($Image -cnotmatch '^ghcr\.io/nsethi16/docintel-api@(?<digest>sha256:[a-f0-9]{64})$') {
    throw 'Image must be the reviewed public ghcr.io/nsethi16/docintel-api image pinned by SHA-256 digest, not a tag.'
}
$digest = $Matches.digest
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
$privateFiles = [Collections.Generic.List[string]]::new()
$receipt = @{ Status = 'Incomplete'; Stage = 'Preflight'; Customer = $CustomerSlug; ResourceGroup = $ResourceGroup }

function Invoke-CustomerAzure([string[]]$Arguments) {
    $scope = if ($SubscriptionId) { @('--subscription', $SubscriptionId) } else { @() }
    $lines = & az @Arguments @scope --only-show-errors --output json 2>&1
    if ($LASTEXITCODE -ne 0) {
        $code = 'UnclassifiedAzureError'
        $errorText = $lines -join "`n"
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
    foreach ($provider in @('Microsoft.App', 'Microsoft.OperationalInsights', 'Microsoft.ManagedIdentity',
        'Microsoft.Storage', 'Microsoft.DocumentDB', 'Microsoft.KeyVault', 'Microsoft.CognitiveServices')) {
        $state = Invoke-CustomerAzure @('provider', 'show', '--namespace', $provider, '--query', 'registrationState')
        if ($state -cne 'Registered') { throw "Have the Azure administrator register $provider before previewing setup." }
    }
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
    $groupExists = Invoke-CustomerAzure @('group', 'exists', '--name', $ResourceGroup)
    $groupTags = @{}
    if ($groupExists -eq $true) {
        $group = Invoke-CustomerAzure @('group', 'show', '--name', $ResourceGroup)
        if ($group.location -ine $Location) { throw 'The existing empty resource group has a different location.' }
        $inventory = @(Invoke-CustomerAzure @('resource', 'list', '--resource-group', $ResourceGroup))
        if ($inventory.Count) { throw 'The resource group is not empty. Use the existing-deployment setup path; nothing will be adopted.' }
        foreach ($property in $group.tags.PSObject.Properties) { $groupTags[$property.Name] = $property.Value }
    } elseif ($groupExists -ne $false) { throw 'Unable to establish whether the customer resource group exists.' }
    if (($groupTags.ContainsKey('docintel-customer') -and $groupTags['docintel-customer'] -cne $CustomerSlug) -or
        ($groupTags.ContainsKey('docintel-environment') -and $groupTags['docintel-environment'] -cne $EnvironmentName)) {
        throw 'The resource group is tagged for a different customer/environment.'
    }
    $key = (Get-CustomerHash @($directoryId, $SubscriptionId, $ResourceGroup, $CustomerSlug, $EnvironmentName)).Substring(0, 16)
    $installationTag = "docintel-installation:$key"
    $webName = "DocIntel $CustomerSlug $EnvironmentName Web $key"
    $apiName = "DocIntel $CustomerSlug $EnvironmentName API $key"
    $web = Get-CustomerApplication $webName
    $api = Get-CustomerApplication $apiName
    $parameters = @{
        customerSlug = @{ value = $CustomerSlug }; environmentName = @{ value = $EnvironmentName }
        location = @{ value = $Location }; resourceGroupName = @{ value = $ResourceGroup }
        resourceGroupTags = @{ value = $groupTags }; directoryTenantId = @{ value = $directoryId }
        initialOwnerObjectId = @{ value = $InitialOwnerObjectId }; containerImage = @{ value = $Image }
        webClientId = @{ value = $(if ($web) { $web.appId } else { '11111111-1111-1111-1111-111111111111' }) }
        apiClientId = @{ value = $(if ($api) { $api.appId } else { '22222222-2222-2222-2222-222222222222' }) }
        deployApplication = @{ value = $true }; infrastructureReady = @{ value = $false }
    }
    $parameterFile = Write-CustomerParameterFile @{ parameters = $parameters }
    $whatIfArguments = @('deployment', 'sub', 'what-if', '--location', $Location,
        '--name', "docintel-$key", '--template-file', $template, '--parameters', "@$parameterFile",
        '--result-format', 'FullResourcePayloads', '--no-pretty-print')
    $whatIf = Invoke-CustomerAzure $whatIfArguments
    Assert-CustomerChanges $whatIf
    $files = @('infra\main.bicep', 'infra\customer.bicep', 'scripts\Install-DocIntelCustomer.ps1',
        'scripts\Wait-DocIntelReady.ps1')
    $fileHashes = @{}
    foreach ($file in $files) { $fileHashes[$file] = (Get-FileHash (Join-Path $root $file) -Algorithm SHA256).Hash }
    $plan = @{
        Version = 1; Parameters = $parameters; Web = $web; Api = $api
        ResourceGroupExists = $groupExists; Files = $fileHashes; Changes = @($whatIf.changes)
        Diagnostics = @($whatIf.diagnostics | Where-Object { $null -ne $_ })
        PotentialChanges = @($whatIf.potentialChanges | Where-Object { $null -ne $_ })
        DirectoryOperations = @('Create/reuse installation-tagged applications and service principals',
            'Expose and preauthorize DocIntel.Access; configure the final web redirect and runtime federation')
        Boundaries = @('No Graph admin consent or deployer self-elevation', 'No existing-resource adoption',
            'No email/Teams/private networking', 'Readiness is not a PDF/Ask AI acceptance test')
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

    $receipt.Stage = 'DirectoryApplications'
    if (-not $web) { $web = New-CustomerApplication $webName }
    if (-not $api) { $api = New-CustomerApplication $apiName }
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
    $parameters.deployApplication.value = $false
    $receipt.Stage = 'SharedInfrastructure'
    $parameterFile = Write-CustomerParameterFile @{ parameters = $parameters }
    $deploymentArguments = @('deployment', 'sub', 'create', '--location', $Location, '--name', "docintel-$key",
        '--template-file', $template, '--parameters', "@$parameterFile")
    $foundation = Invoke-CustomerAzure $deploymentArguments
    if ($foundation.properties.provisioningState -cne 'Succeeded') { throw 'Shared infrastructure did not finish successfully.' }
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
    $parameterFile = Write-CustomerParameterFile @{ parameters = $parameters }
    $whatIfArguments[[Array]::IndexOf($whatIfArguments, '--parameters') + 1] = "@$parameterFile"
    $finalPlan = Invoke-CustomerAzure $whatIfArguments
    Assert-CustomerChanges $finalPlan -ApplicationOnly
    $deploymentArguments[[Array]::IndexOf($deploymentArguments, '--parameters') + 1] = "@$parameterFile"
    $deployed = Invoke-CustomerAzure $deploymentArguments
    if ($deployed.properties.provisioningState -cne 'Succeeded') { throw 'Application deployment did not finish successfully.' }
    $receipt.Stage = 'Readiness'
    $readinessArguments = @{
        SubscriptionId = $SubscriptionId; ResourceGroup = $ResourceGroup
        ContainerAppName = $outputs.containerAppName.value; ContainerName = 'docintel-api'
    }
    & (Join-Path $PSScriptRoot 'Wait-DocIntelReady.ps1') @readinessArguments -ConfigurationOnly
    & (Join-Path $PSScriptRoot 'Wait-DocIntelReady.ps1') @readinessArguments -ExpectedImage $Image
    $receipt.Status = 'InfrastructureReady'
    Write-Host "Infrastructure and provider readiness passed: $origin"
    Write-Host 'Customer acceptance remains: sign in as the chosen owner, create the customer workspace, upload a PDF and verify a cited Ask AI answer.'
}
finally {
    $publicToken = $null
    foreach ($file in $privateFiles) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
    if ($Mode -eq 'Apply' -and $OutputDirectory -and (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
        $receipt | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'customer-result.json') -Encoding utf8
    }
}
