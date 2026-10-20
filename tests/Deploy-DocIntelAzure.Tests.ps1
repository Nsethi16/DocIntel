#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'Deploy-DocIntelAzure.ps1'
$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath, [ref]$tokens, [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -ne 0) {
    throw ($parseErrors | ForEach-Object Message | Out-String)
}

$script:AzureCalls = 0
function az {
    $script:AzureCalls++
    throw 'Azure must not be invoked by offline preflight tests.'
}

function Assert-PreflightFailure {
    param([hashtable]$Arguments, [string]$Expected)
    $failed = $false
    try {
        & $scriptPath @Arguments
    }
    catch {
        $failed = $true
        if ($_.Exception.Message -notlike "*$Expected*") { throw }
    }
    if (-not $failed) { throw 'Invalid deployment inputs were not rejected.' }
    if ($script:AzureCalls -ne 0) { throw 'A preflight failure invoked Azure.' }
}

$ids = @{
    SubscriptionId = [guid]::NewGuid().ToString()
    DirectoryTenantId = [guid]::NewGuid().ToString()
    WebClientId = [guid]::NewGuid().ToString()
    ApiClientId = [guid]::NewGuid().ToString()
}
$invalid = $ids.Clone()
$invalid.SubscriptionId = 'invalid'
Assert-PreflightFailure $invalid 'SubscriptionId'
Assert-PreflightFailure $ids 'IdentityGrantsPath'
$same = $ids.Clone()
$same.ApiClientId = $same.WebClientId
Assert-PreflightFailure $same 'separate'

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('docintel-grants-test-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    '{}' | Set-Content -LiteralPath $temporary
    $empty = $ids.Clone()
    $empty.IdentityGrantsPath = $temporary
    Assert-PreflightFailure $empty 'nonempty'
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
$templatePath = Join-Path (Split-Path $PSScriptRoot -Parent) 'infra\azuredeploy.json'
$template = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json -Depth 100
$applications = @($template.resources | Where-Object { $_.type -eq 'Microsoft.App/containerApps' })
if ($applications.Count -ne 1) { throw 'Expected exactly one conditional Container App resource.' }
$application = $applications[0]
if ($application.properties.template.PSObject.Properties['dependsOn']) {
    throw 'Container App dependencies must not be emitted as ARM resource lookups inside its API properties.'
}
if (-not @($application.dependsOn | Where-Object { $_ -match "variables\('documentIntelligenceName'\)" })) {
    throw 'The conditional OCR role must be a resource-level dependency, safe when an existing endpoint is used.'
}
Write-Output 'Deployment parser, fail-fast preflight, and ARM dependency-shape tests passed without Azure calls.'
