#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Image = '',
    [ValidateSet('PublicImage', 'CustomerRegistryBuild')][string]$ImageSource = 'PublicImage',
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ($Version -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9]+(?:[.-][a-zA-Z0-9]+)*)?$' -or
    $SourceCommit -cnotmatch '^[a-f0-9]{40}$') {
    throw 'A semantic release version and full source commit are required.'
}
if (($ImageSource -eq 'PublicImage' -and $Image -cnotmatch '^ghcr\.io/nsethi16/docintel-api@sha256:[a-f0-9]{64}$') -or
    ($ImageSource -eq 'CustomerRegistryBuild' -and $Image)) {
    throw 'PublicImage requires an exact image digest; CustomerRegistryBuild must not preselect an image.'
}
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path ([IO.Path]::GetTempPath()) ('docintel-bundle-' + [guid]::NewGuid().ToString('N'))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$zipName = "docintel-setup-$Version.zip"
$zip = Join-Path $OutputDirectory $zipName
if (Test-Path -LiteralPath $zip) { throw 'This version already has a bundle. Never overwrite a published version.' }
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $files = @('Start-DocIntelSetup.ps1', 'scripts/Install-DocIntelCustomer.ps1',
        'scripts/Wait-DocIntelReady.ps1', 'scripts/DocIntelRelease.psm1',
        'infra/customer.bicep', 'infra/main.bicep', 'docs/customer-deployment.md')
    foreach ($relative in $files) {
        $path = $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $target = Join-Path $stage $path
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $path) -Destination $target
    }
    & az bicep build --file (Join-Path $stage 'infra\customer.bicep') `
        --outfile (Join-Path $stage 'infra\customer.json') --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Release template compilation failed; no bundle is published.' }
    $files += 'infra/customer.json'
    $hashes = [ordered]@{}
    foreach ($relative in $files | Sort-Object -CaseSensitive) {
        $path = Join-Path $stage $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $hashes[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $manifest = [ordered]@{ schemaVersion = 2; version = $Version; sourceCommit = $SourceCommit
        imageSource = $ImageSource; sourceRepository = 'Nsethi16/DocIntel'; image = $Image; files = $hashes }
    $manifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $stage 'release.json') -Encoding utf8
    Import-Module (Join-Path $stage 'scripts\DocIntelRelease.psm1') -Force
    Read-DocIntelRelease -Root $stage | Out-Null
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    $zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item -LiteralPath (Join-Path $stage 'release.json') -Destination (Join-Path $OutputDirectory 'release.json')
    "$zipHash  $zipName" | Set-Content -LiteralPath (Join-Path $OutputDirectory 'checksums.txt') -Encoding ascii
    $instructions = @"
# DocIntel $Version

This bundle uses image source ``$ImageSource`` and reviewed source commit ``$SourceCommit``.
Public-image reference (when applicable): ``$Image``.
Use Azure Cloud Shell **PowerShell**, signed in to your customer's directory.

1. Download ``$zipName`` from this release.
2. Verify SHA-256: ``$zipHash``. Do not execute a download whose checksum differs.
3. Extract the ZIP and run ``.\Start-DocIntelSetup.ps1``.
4. Choose the customer and subscription, review the plan/costs, then type APPLY.

The installer creates the workspace, infrastructure and application identities. Keep its state directory
for safe resume. It never grants itself permissions or silently adopts another installation.
Provider readiness is not proof of PDF/Ask AI behavior; complete those acceptance checks after sign-in.
Email/Teams, Microsoft 365 licenses and private networking are separate optional onboarding.
CustomerRegistryBuild creates a private Basic ACR and runs a paid, bounded build in the customer's
subscription from the existing public source commit. No public binary publishing is required.
"@
    $instructions | Set-Content -LiteralPath (Join-Path $OutputDirectory 'INSTALL.md') -Encoding utf8
    Write-Output ([pscustomobject]@{ Bundle = $zip; Sha256 = $zipHash; Version = $Version; Image = $Image })
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
