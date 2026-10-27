#Requires -Version 7.3
<#
.SYNOPSIS
    Guided setup from a reviewed, versioned DocIntel release bundle.
.DESCRIPTION
    Verify the ZIP checksum from the trusted release page before extracting/running this script.
    Local manifest hashes detect corruption; they do not replace that external trust check.
#>
[CmdletBinding()]
param(
    [string]$CustomerSlug = '',
    [string]$CustomerName = '',
    [string]$SubscriptionId = '',
    [string]$Location = 'westus',
    [string]$EnvironmentName = 'prod',
    [string]$InitialOwnerObjectId = '',
    [string]$StateDirectory = '',
    [ValidateSet('Prompt', 'Disabled', 'UseExisting', 'Configure', 'PreserveExisting')]
    [string]$DirectoryMode = 'Prompt',
    [switch]$ConnectDirectory,
    [switch]$DirectoryRequired,
    [switch]$PreviewOnly,
    [switch]$RetryFailedBuild
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'scripts\DocIntelRelease.psm1') -Force
$release = Read-DocIntelRelease -Root $PSScriptRoot
if (-not $CustomerSlug) { $CustomerSlug = Read-Host 'Customer short name (3-16 lowercase letters/digits, internal hyphens)' }
if ($CustomerSlug.Length -lt 3 -or $CustomerSlug.Length -gt 16 -or
    $CustomerSlug -cnotmatch '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$' -or
    $EnvironmentName -cnotmatch '^[a-z0-9]{2,8}$') {
    throw 'The customer or environment name is invalid.'
}
if (-not $CustomerName) { $CustomerName = $CustomerSlug }
if (-not $SubscriptionId) {
    $json = & az account list --query "[?state=='Enabled'].{name:name,id:id,tenantId:tenantId}" --output json
    if ($LASTEXITCODE -ne 0) { throw 'Sign in to the customer directory using az login --tenant <directory-id>.' }
    $accounts = @((($json -join "`n") | ConvertFrom-Json) | Where-Object { $null -ne $_ })
    if (-not $accounts.Count) { throw 'No enabled subscriptions are available. Sign in to the customer directory first.' }
    for ($index = 0; $index -lt $accounts.Count; $index++) {
        Write-Host "$($index + 1): $($accounts[$index].name) [$($accounts[$index].id)]"
    }
    $choice = if ($accounts.Count -eq 1) { '1' } else { Read-Host 'Select the customer subscription number' }
    $selection = 0
    if (-not [int]::TryParse($choice, [ref]$selection) -or $selection -lt 1 -or $selection -gt $accounts.Count) {
        throw 'Select one of the displayed subscription numbers.'
    }
    $SubscriptionId = $accounts[$selection - 1].id
}
if (-not $StateDirectory) {
    $stateKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes("$SubscriptionId/$CustomerSlug/$EnvironmentName"))).Substring(0, 12).ToLowerInvariant()
    $StateDirectory = Join-Path $HOME ".docintel\installations\$CustomerSlug-$EnvironmentName-$stateKey"
}
if ($ConnectDirectory) {
    if ($DirectoryMode -notin @('Prompt', 'Configure')) {
        throw 'ConnectDirectory cannot be combined with a different DirectoryMode.'
    }
    $DirectoryMode = 'Configure'
}
if ($DirectoryMode -eq 'Prompt') {
    $previousPath = Join-Path $StateDirectory 'directory-result.json'
    if (Test-Path -LiteralPath $previousPath) {
        $previous = Get-Content -LiteralPath $previousPath -Raw | ConvertFrom-Json
        $DirectoryMode = if ($previous.Status -eq 'Connected' -or $previous.RequestedMode -eq 'Disabled') {
            'PreserveExisting'
        } else { $previous.RequestedMode }
        if ($previous.Required) { $DirectoryRequired = $true }
    } elseif ($DirectoryRequired) {
        $DirectoryMode = 'UseExisting'
    } elseif (Test-Path -LiteralPath (Join-Path $StateDirectory 'customer-installation.json')) {
        $DirectoryMode = 'PreserveExisting'
    } elseif ($PreviewOnly) {
        $DirectoryMode = 'PreserveExisting'
    } else {
        $choice = Read-Host 'Connect organization directory for people search? Requires tenant-wide read-only consent [y/N]'
        $DirectoryMode = if ($choice -in @('y', 'yes')) { 'Configure' } else { 'Disabled' }
    }
}
if ($DirectoryRequired -and $DirectoryMode -eq 'Disabled') { throw 'A required directory cannot be Disabled.' }
$setup = @{
    CustomerSlug = $CustomerSlug; CustomerName = $CustomerName; SubscriptionId = $SubscriptionId
    Image = $release.image; Location = $Location; EnvironmentName = $EnvironmentName
    OutputDirectory = $StateDirectory; InitialOwnerObjectId = $InitialOwnerObjectId
}
if ($release.schemaVersion -ge 2) {
    $setup.ImageSource = $release.imageSource
    if ($release.imageSource -ceq 'CustomerRegistryBuild') { $setup.SourceCommit = $release.sourceCommit }
}
if (Test-Path -LiteralPath (Join-Path $StateDirectory 'customer-installation.json')) { $setup.Resume = $true }
if ($RetryFailedBuild) { $setup.RetryFailedBuild = $true }
Write-Host "DocIntel $($release.version): $CustomerName ($EnvironmentName), region $Location."
Write-Host 'This creates billable Azure resources and explicitly grants the selected owner installation-wide administration.'
Write-Host 'Email/Teams and Microsoft 365 subscriptions are not included. Review your Azure pricing and the plan below.'
if ($setup.ImageSource -ceq 'CustomerRegistryBuild') {
    Write-Host "Image source: customer-owned Basic ACR and paid build from reviewed public commit $($release.sourceCommit)."
    Write-Host 'No public container package or additional public distribution repository is required.'
}
& (Join-Path $PSScriptRoot 'scripts\Install-DocIntelCustomer.ps1') @setup
$plan = Get-Content -LiteralPath (Join-Path $StateDirectory 'customer-plan.json') -Raw | ConvertFrom-Json -Depth 100
Write-Host "Plan: $($plan.Plan.Operation). Resource changes: $(@($plan.Plan.Changes).Count). State: $StateDirectory"
if ($PreviewOnly) { Write-Host 'Preview only; nothing was applied.'; return }
$confirmation = Read-Host 'Review customer-plan.json. Type APPLY to approve this exact plan, or Enter to stop'
if ($confirmation -cne 'APPLY') { Write-Host 'Not applied. Re-run the same bundle to review again.'; return }
& (Join-Path $PSScriptRoot 'scripts\Install-DocIntelCustomer.ps1') @setup `
    -Mode Apply -Confirmation APPLY -ExpectedPlanHash $plan.PlanHash
$result = Get-Content -LiteralPath (Join-Path $StateDirectory 'customer-result.json') -Raw | ConvertFrom-Json
$directoryArguments = @{ StateDirectory = $StateDirectory; DirectoryMode = $DirectoryMode
    DirectoryRequired = $DirectoryRequired }
$directoryPlan = & (Join-Path $PSScriptRoot 'scripts\Connect-DocIntelDirectory.ps1') @directoryArguments
$directoryApproval = ''
if ($directoryPlan.MutationPlanned) {
    $review = Get-Content -LiteralPath (Join-Path $StateDirectory 'directory-plan.json') -Raw | ConvertFrom-Json -Depth 50
    Write-Host "Customer directory: $($review.Plan.Directory); runtime identity: $($review.Plan.RuntimePrincipal)."
    Write-Host 'Only User.Read.All is requested: tenant-wide directory/profile reading, no mail or directory writes.'
    $word = if ($directoryPlan.NeedsConsent) { 'CONSENT' } else { 'CONNECT' }
    $directoryApproval = Read-Host "Review directory-plan.json. Type $word to approve this step, or Enter to leave it pending"
    if ($directoryApproval -cne $word) {
        @{ SchemaVersion = 1; Status = 'AwaitingUserApproval'; DirectoryConnected = $false
            RequestedMode = $DirectoryMode; Required = [bool]$DirectoryRequired } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $StateDirectory 'directory-result.json') -Encoding utf8
        Write-Warning 'Core deployment is ready; organization directory setup was not approved and remains pending.'
        if ($DirectoryRequired) { throw 'Required organization directory setup was not approved.' }
        Write-Host "Open the core workspace: $($result.WorkspaceUrl)"
        return
    }
}
$connection = & (Join-Path $PSScriptRoot 'scripts\Connect-DocIntelDirectory.ps1') @directoryArguments `
    -Mode Apply -Confirmation CONNECT -ExpectedPlanHash $directoryPlan.PlanHash `
    -ConsentConfirmation $(if ($directoryApproval -ceq 'CONSENT') { 'CONSENT' } else { '' })
Write-Host "Organization directory status: $($connection.Status)."
if ($connection.Status -eq 'AwaitingAdminApproval') {
    Write-Host 'An authorized Entra administrator can approve directory-consent-request.json using scripts\Approve-DocIntelDirectory.ps1.'
    Write-Host 'That administrator does not need Azure subscription/container access. Re-run this setup after consent.'
}
Write-Host "Open your customer workspace: $($result.WorkspaceUrl)"
Write-Host 'Sign in as the selected owner, process a representative PDF, then verify an Ask AI answer with source citations.'
