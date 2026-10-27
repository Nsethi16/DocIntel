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
$setup = @{
    CustomerSlug = $CustomerSlug; CustomerName = $CustomerName; SubscriptionId = $SubscriptionId
    Image = $release.image; Location = $Location; EnvironmentName = $EnvironmentName
    OutputDirectory = $StateDirectory; InitialOwnerObjectId = $InitialOwnerObjectId
}
if ($release.schemaVersion -eq 2) {
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
Write-Host "Open your customer workspace: $($result.WorkspaceUrl)"
Write-Host 'Sign in as the selected owner, process a representative PDF, then verify an Ask AI answer with source citations.'
