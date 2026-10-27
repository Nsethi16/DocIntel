#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('docintel-bundle-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $arguments = @{ Version = 'v0.2.0-rc.1'; Image = 'ghcr.io/nsethi16/docintel-api@sha256:' + ('a' * 64)
        SourceCommit = 'a' * 40; OutputDirectory = (Join-Path $temporary 'output') }
    $bundle = & (Join-Path $root 'scripts\Build-DocIntelCustomerBundle.ps1') @arguments
    if (-not $bundle.Bundle -or -not (Test-Path -LiteralPath $bundle.Bundle)) { throw 'Bundle was not produced.' }
    $extracted = Join-Path $temporary 'extracted'
    Expand-Archive -LiteralPath $bundle.Bundle -DestinationPath $extracted
    Import-Module (Join-Path $extracted 'scripts\DocIntelRelease.psm1') -Force
    $release = Read-DocIntelRelease -Root $extracted
    if ($release.image -cne $arguments.Image -or $release.version -cne $arguments.Version -or
        $release.sourceCommit -cne $arguments.SourceCommit -or
        $bundle.Sha256 -cne (Get-FileHash $bundle.Bundle -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'Bundle metadata/checksum must match the exact release and image.'
    }
    $failed = $false
    try { & (Join-Path $root 'scripts\Build-DocIntelCustomerBundle.ps1') @arguments | Out-Null }
    catch { $failed = $true; if ($_.Exception.Message -notlike '*Never overwrite*') { throw } }
    if (-not $failed) { throw 'A release bundle must not be overwritten.' }
    foreach ($property in $release.files.PSObject.Properties) {
        $path = Join-Path $extracted $property.Name.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $original = [IO.File]::ReadAllBytes($path)
        try {
            [IO.File]::AppendAllText($path, "`ncorruption")
            $failed = $false
            try { Read-DocIntelRelease -Root $extracted | Out-Null }
            catch { $failed = $true; if ($_.Exception.Message -notlike '*integrity verification failed*') { throw } }
            if (-not $failed) { throw 'Every executable/template file must be checked before setup.' }
        } finally { [IO.File]::WriteAllBytes($path, $original) }
    }
    $manifestPath = Join-Path $extracted 'release.json'
    $originalManifest = Get-Content -LiteralPath $manifestPath -Raw
    foreach ($bad in @('schema', 'image', 'path')) {
        try {
            $invalid = $originalManifest | ConvertFrom-Json -AsHashtable
            switch ($bad) {
                'schema' { $invalid.schemaVersion = 3 }
                'image' { $invalid.image = 'ghcr.io/nsethi16/docintel-api:latest' }
                'path' { $invalid.files['../escape.ps1'] = 'a' * 64 }
            }
            $invalid | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $manifestPath
            $failed = $false
            try { Read-DocIntelRelease -Root $extracted | Out-Null } catch { $failed = $true }
            if (-not $failed) { throw 'Invalid release schema/image/inventory was accepted.' }
        } finally { $originalManifest | Set-Content -LiteralPath $manifestPath }
    }
    & (Join-Path $PSScriptRoot 'Install-DocIntelCustomer.Tests.ps1') -ReleaseRoot $extracted
    $sourceArguments = $arguments.Clone()
    $sourceArguments.Remove('Image')
    $sourceArguments.ImageSource = 'CustomerRegistryBuild'
    $sourceArguments.OutputDirectory = Join-Path $temporary 'source-output'
    $sourceBundle = & (Join-Path $root 'scripts\Build-DocIntelCustomerBundle.ps1') @sourceArguments
    $sourceRoot = Join-Path $temporary 'source-extracted'
    Expand-Archive -LiteralPath $sourceBundle.Bundle -DestinationPath $sourceRoot
    Import-Module (Join-Path $sourceRoot 'scripts\DocIntelRelease.psm1') -Force
    $sourceRelease = Read-DocIntelRelease -Root $sourceRoot
    if ($sourceRelease.imageSource -cne 'CustomerRegistryBuild' -or $sourceRelease.image) {
        throw 'A source-build kit must not depend on an unpublished public image.'
    }
    & (Join-Path $PSScriptRoot 'Install-DocIntelCustomer.Tests.ps1') -ReleaseRoot $sourceRoot
    $workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\release-customer.yml') -Raw
    if ($workflow -notmatch 'needs: validate' -or $workflow -notmatch 'validation_only: true' -or
        $workflow -notmatch "github.ref == 'refs/heads/main'" -or $workflow -match 'docintel-api:latest' -or
        $workflow -match 'nuget_credentials|AZURE_CLIENT_SECRET|pull_request_target') {
        throw 'Customer publication must be validated, main-only and immutable without package-feed/cloud credentials.'
    }
    Write-Output 'Versioned bundle integrity and extracted-installer scenarios passed.'
} finally {
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
