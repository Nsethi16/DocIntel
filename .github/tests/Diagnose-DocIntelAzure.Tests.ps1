#Requires -Version 7.3
$ErrorActionPreference = 'Stop'
$workflowPath = Join-Path $PSScriptRoot '..\workflows\diagnose-azure.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$match = [regex]::Match($workflow,
    '(?ms)      - name: Inspect revision[^\r\n]*\r?\n        shell: pwsh\r?\n        run: \|\r?\n(?<script>(?:          [^\r\n]*(?:\r?\n|$))+)')
if (-not $match.Success) { throw 'The runtime diagnostic command block is missing.' }
$source = $match.Groups['script'].Value -replace '(?m)^          ', ''
$command = [scriptblock]::Create($source)
if ($workflow -notmatch "if: github.ref == 'refs/heads/main'" -or
    $workflow -notmatch 'environment: docintel-dev' -or
    $workflow -notmatch 'workflow_dispatch:') {
    throw 'Diagnostics must remain manual, main-only and protected by the deployment environment.'
}
if ($source -match 'az (?:deployment|role|ad|storage)|containerapp (?:update|exec)|--account-key') {
    throw 'Runtime diagnostics must not mutate resources, inspect keys or execute in the application.'
}
$savedGroup = $env:AZURE_RESOURCE_GROUP
$savedApp = $env:AZURE_CONTAINER_APP_NAME
$savedContainer = $env:DOCINTEL_CONTAINER_NAME
$global:DocIntelDiagnosticsFixture = @{
    scenario = ''; calls = [Collections.Generic.List[object]]::new()
}

function az {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $global:DocIntelDiagnosticsFixture.calls.Add($Arguments)
    $global:LASTEXITCODE = 0
    switch ($Arguments[0..1] -join ' ') {
        'containerapp show' {
            if ($global:DocIntelDiagnosticsFixture.scenario -eq 'azure-failure') {
                throw 'Azure inspection failed.'
            }
            $containers = @(@{
                name = 'api'; image = 'registry.example/app:expected'
                env = @(
                    @{ name = 'AzureAI__AzureOpenAIEndpoint'; value = 'https://private-endpoint.example/' },
                    @{ name = 'AzureAI__ContentUnderstandingEndpoint'; secretRef = 'private-secret-reference' },
                    @{ name = 'UnrelatedPrivateSetting'; value = 'customer-private-value' }
                )
            })
            if ($global:DocIntelDiagnosticsFixture.scenario -eq 'multiple-containers') {
                $containers += @{ name = 'sidecar'; image = 'registry.example/sidecar:expected' }
            }
            @{
                properties = @{
                    latestRevisionName = 'revision-current'; latestReadyRevisionName = 'revision-current'
                    template = @{ containers = $containers }
                }
            } | ConvertTo-Json -Depth 20 -Compress
        }
        'containerapp replica' {
            if ($global:DocIntelDiagnosticsFixture.scenario -eq 'no-replicas') { '[]'; return }
            @(@{ name = 'replica-one' }, @{ name = 'replica-two' }) |
                ConvertTo-Json -Depth 10 -Compress
        }
        'containerapp logs' {
            if ($Arguments -notcontains 'revision-current' -or $Arguments -notcontains 'api' -or
                $Arguments -notcontains '--replica' -or
                $Arguments[[Array]::IndexOf($Arguments, '--follow') + 1] -ne 'false' -or
                $Arguments[[Array]::IndexOf($Arguments, '--tail') + 1] -ne '300') {
                throw 'Logs must target the selected revision/container/replica and remain bounded.'
            }
            Write-Output '{"Log":"customer-private-value should never be printed"}'
            Write-Output '{"Log":"Request private-correlation-id failed with RequestFailedException; code 403; site BlobRestClient.UploadAsync."}'
            Write-Output '{"Log":"Document private-job-id failed with RequestFailedException; code 404; site ContentUnderstandingRestClient.AnalyzeBinaryAsync."}'
            Write-Output '{"Log":"Request secret-id failed with CustomerException; code 500; site SensitiveCustomerIdentifier.Read."}'
            Write-Output '{"Log":"Request private-correlation-id failed with InvalidOperationException; code 5385; site ThrowHelper.ThrowInvalidOperationException."}'
            Write-Output '{"Log":"Document private-job-id failed with InvalidDataException; code 5377; site <RecognizeAsync>d__11.MoveNext."}'
            Write-Output '{"Log":"Operation Executor.ExtractPageLayoutsExecutor failed after 12.4 ms; error type InvalidDataException."}'
            Write-Output '{"Log":"Operation Executor.PrivateCustomerIdentifier failed after 12.4 ms; error type CustomerException."}'
            Write-Output '{"Log":"Ask AI run private-run-id failed with InvalidOperationException; root cause CosmosException; code 500; site <SearchDocumentsAsync>d__2.MoveNext."}'
            Write-Output '{"Log":"Ask AI run private-run-id failed with InvalidOperationException; root cause ArgumentException; code 87; site DocumentQuery.Validate."}'
            $type = if ($global:DocIntelDiagnosticsFixture.scenario -eq 'unknown-type') {
                'SensitiveCustomerIdentifier'
            } else { 'NotSupportedException' }
            if ($global:DocIntelDiagnosticsFixture.scenario -ne 'no-startup-error') {
                Write-Output ('{"Log":"Startup provider verification failed with ' + $type +
                    '; no provider details were returned."}')
            }
        }
        'cognitiveservices account' {
            if ($Arguments[2] -eq 'list') {
                @(
                    @{ name = 'test-account'; kind = 'AIServices'; properties = @{ customSubDomainName = 'test-account' } },
                    @{ name = 'other-account'; kind = 'AIServices'; properties = @{ customSubDomainName = 'other-account' } }
                ) | ConvertTo-Json -Depth 10 -Compress
            } elseif ($Arguments[2..3] -join ' ' -eq 'deployment list') {
                @(
                    @{ name = 'gpt-5-mini'; properties = @{ model = @{ name = 'gpt-5-mini' } } },
                    @{ name = 'PrivateCustomDeployment'; properties = @{ model = @{ name = 'PrivateCustomModel' } } }
                ) | ConvertTo-Json -Depth 10 -Compress
            } else { throw 'Diagnostics must only list management-plane accounts and deployments.' }
        }
        default { throw 'Unexpected Azure command in diagnostic tests.' }
    }
}

function Assert-DiagnosticFailure([string]$Scenario, [string]$Message) {
    $global:DocIntelDiagnosticsFixture.scenario = $Scenario
    $failed = $false
    try { & $command | Out-Null }
    catch {
        $failed = $true
        if ($_.Exception.Message -notlike "*$Message*") { throw }
    }
    if (-not $failed) { throw "Expected diagnostic failure: $Message." }
}

try {
    $env:AZURE_RESOURCE_GROUP = 'test-group'
    $env:AZURE_CONTAINER_APP_NAME = 'test-app'
    $env:DOCINTEL_CONTAINER_NAME = ''
    $output = & $command 3>&1 | Out-String
    if ($output -notmatch 'Startup provider error type: NotSupportedException' -or
        $output -notmatch 'revision-current' -or $output -notmatch '"configured":true') {
        throw 'Diagnostics must report the selected revision, configured flags and actual startup error type.'
    }
    if ($output -match 'private-endpoint|private-secret-reference|customer-private-value|UnrelatedPrivateSetting') {
        throw 'Diagnostics leaked raw configuration, logs or customer data.'
    }
    if ($output -notmatch 'test-account' -or $output -notmatch 'gpt-5-mini' -or
        $output -match 'PrivateCustomDeployment|PrivateCustomModel') {
        throw 'Model diagnostics must mask custom deployment/model names and show known deployments.'
    }
    if ($output -notmatch 'Runtime failure: Request: RequestFailedException; code 403; component BlobStorage' -or
        $output -notmatch 'Runtime failure: Document: RequestFailedException; code 404; component ContentUnderstanding' -or
        $output -notmatch 'Runtime failure: Request: UnclassifiedException; code 500; component Other' -or
        $output -notmatch 'site ThrowHelper.ThrowInvalidOperationException' -or
        $output -notmatch 'Document: InvalidDataException; code 5377; component ContentUnderstanding; site RecognizeAsync.MoveNext' -or
        $output -notmatch 'Failed processing stage: ExtractPageLayoutsExecutor: InvalidDataException' -or
        $output -notmatch 'AskAI: CosmosException; code 500; component Other; site SearchDocumentsAsync.MoveNext' -or
        $output -notmatch 'AskAI: ArgumentException; code 87; component Other; site DocumentQuery.Validate' -or
        $output -match 'private-run-id' -or
        $output -match 'PrivateCustomerIdentifier' -or
        $output -match 'private-correlation-id|private-job-id|secret-id|SensitiveCustomerIdentifier|CustomerException') {
        throw 'Runtime error output must report only known types, numeric codes and fixed component categories.'
    }
    $logs = @($global:DocIntelDiagnosticsFixture.calls | Where-Object { $_[0..1] -join ' ' -eq 'containerapp logs' })
    if ($logs.Count -ne 2) { throw 'Each sampled replica must be inspected.' }

    Assert-DiagnosticFailure 'multiple-containers' 'Select exactly one'
    $env:DOCINTEL_CONTAINER_NAME = 'api'
    & $command | Out-Null
    $env:DOCINTEL_CONTAINER_NAME = ''
    Assert-DiagnosticFailure 'no-replicas' 'no active replicas'
    Assert-DiagnosticFailure 'azure-failure' 'Azure inspection failed'

    $global:DocIntelDiagnosticsFixture.scenario = 'unknown-type'
    $output = & $command 3>&1 | Out-String
    if ($output -match 'SensitiveCustomerIdentifier' -or $output -notmatch 'UnclassifiedException') {
        throw 'Unrecognized log text must not be copied into diagnostic output.'
    }
    $global:DocIntelDiagnosticsFixture.scenario = 'no-startup-error'
    $output = & $command 3>&1 | Out-String
    if ($output -notmatch 'No startup provider error type was found' -or
        $output -match 'Startup provider error type:') {
        throw 'Missing startup evidence must be reported explicitly without assuming a diagnosis.'
    }
    Write-Output 'Runtime diagnostic privacy, read-only operation and explicit failure tests passed without Azure calls.'
}
finally {
    Remove-Item Function:\az
    Remove-Variable -Name DocIntelDiagnosticsFixture -Scope Global
    $env:AZURE_RESOURCE_GROUP = $savedGroup
    $env:AZURE_CONTAINER_APP_NAME = $savedApp
    $env:DOCINTEL_CONTAINER_NAME = $savedContainer
}
