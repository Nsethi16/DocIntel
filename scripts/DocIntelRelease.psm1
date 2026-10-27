#Requires -Version 7.3
Set-StrictMode -Version Latest

function Read-DocIntelRelease {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root)
    $Root = [IO.Path]::GetFullPath($Root)
    $manifestPath = Join-Path $Root 'release.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 30
    if ($manifest.schemaVersion -notin @(1, 2) -or
        $manifest.version -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9]+(?:[.-][a-zA-Z0-9]+)*)?$' -or
        $manifest.sourceCommit -cnotmatch '^[a-f0-9]{40}$') {
        throw 'The release manifest has an unsupported schema, version or source commit.'
    }
    $imageSource = if ($manifest.schemaVersion -eq 1) { 'PublicImage' } else { $manifest.imageSource }
    if ($imageSource -ceq 'PublicImage') {
        if ($manifest.image -cnotmatch '^ghcr\.io/nsethi16/docintel-api@sha256:[a-f0-9]{64}$') {
            throw 'The public release image must be pinned by digest.'
        }
    } elseif ($imageSource -cne 'CustomerRegistryBuild' -or $manifest.image -cne '' -or
        $manifest.sourceRepository -cne 'Nsethi16/DocIntel') {
        throw 'The release image source or source repository is unsupported.'
    }
    $required = @('Start-DocIntelSetup.ps1', 'scripts/Install-DocIntelCustomer.ps1',
        'scripts/Wait-DocIntelReady.ps1', 'scripts/DocIntelRelease.psm1',
        'infra/customer.json', 'infra/customer.bicep', 'infra/main.bicep', 'docs/customer-deployment.md')
    $properties = @($manifest.files.PSObject.Properties)
    if ($properties.Count -ne $required.Count) { throw 'The release file inventory is incomplete or unexpected.' }
    foreach ($property in $properties) {
        if ($property.Name -cnotin $required -or $property.Value -cnotmatch '^[a-f0-9]{64}$') {
            throw 'The release contains an unexpected path or invalid file digest.'
        }
        $relative = $property.Name.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $file = Join-Path $Root $relative
        $current = Get-Item -LiteralPath $file
        while ($current.FullName -ne $Root) {
            if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Release files must not be symbolic links or directory junctions.'
            }
            $current = if ($current -is [IO.FileInfo]) { $current.Directory } else { $current.Parent }
            if ($null -eq $current) { throw 'A release path escaped the bundle root.' }
        }
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -cne $property.Value) { throw "Release integrity verification failed for '$($property.Name)'." }
    }
    return $manifest
}

Export-ModuleMember -Function Read-DocIntelRelease
