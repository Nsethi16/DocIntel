#Requires -Version 7.3
Set-StrictMode -Version Latest

function ConvertTo-DocIntelCanonicalJson([object]$Value) {
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [Collections.IDictionary]) {
        $fields = foreach ($key in $Value.Keys | Sort-Object -CaseSensitive) {
            ($key | ConvertTo-Json -Compress) + ':' + (ConvertTo-DocIntelCanonicalJson $Value[$key])
        }
        return '{' + ($fields -join ',') + '}'
    }
    if ($Value -is [pscustomobject]) {
        $fields = foreach ($property in $Value.PSObject.Properties | Sort-Object Name -CaseSensitive) {
            ($property.Name | ConvertTo-Json -Compress) + ':' + (ConvertTo-DocIntelCanonicalJson $property.Value)
        }
        return '{' + ($fields -join ',') + '}'
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        return '[' + (@($Value | ForEach-Object { ConvertTo-DocIntelCanonicalJson $_ }) -join ',') + ']'
    }
    return $Value | ConvertTo-Json -Compress
}

function Get-DocIntelObjectHash([object]$Value) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes((ConvertTo-DocIntelCanonicalJson $Value)))).ToLowerInvariant()
}

function Read-DocIntelRelease {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root)
    $Root = [IO.Path]::GetFullPath($Root)
    $manifestPath = Join-Path $Root 'release.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 30
    if ($manifest.schemaVersion -notin @(1, 2, 3, 4) -or
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
    if ($manifest.schemaVersion -ge 3) {
        $required += @('scripts/Connect-DocIntelDirectory.ps1', 'scripts/Invoke-DocIntelDirectoryProbe.py',
            'scripts/Approve-DocIntelDirectory.ps1')
    }
    if ($manifest.schemaVersion -ge 4) {
        $required += @('scripts/Sync-DocIntelTranslation.ps1', 'infra/translation-connection.bicep',
            'infra/translation-connection.json')
    }
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

function Get-DocIntelTranslationLifecycleRule {
    return @{
        name = 'docintel-translation-staging-v1'; enabled = $true; type = 'Lifecycle'
        definition = @{
            filters = @{
                blobTypes = @('blockBlob')
                prefixMatch = @('translation-source/docintel-translation-v1/',
                    'translation-target/docintel-translation-v1/')
            }
            actions = @{
                baseBlob = @{ delete = @{ daysAfterModificationGreaterThan = 1 } }
                snapshot = @{ delete = @{ daysAfterCreationGreaterThan = 1 } }
                version = @{ delete = @{ daysAfterCreationGreaterThan = 1 } }
            }
        }
    }
}

function Get-DocIntelTranslationLifecycleState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$StorageResourceId, [AllowNull()][object]$Policy)
    $rule = Get-DocIntelTranslationLifecycleRule
    $ruleCount = $null
    $ready = $false
    $snapshot = $null
    $reason = 'Lifecycle metadata is missing or unreadable; no empty policy is assumed.'
    if ($null -ne $Policy) {
        $body = $Policy | ConvertTo-Json -Depth 100 | ConvertFrom-Json -Depth 100
        $properties = $body.PSObject.Properties['properties']
        if ($properties) { $body = $properties.Value }
        $content = $body.PSObject.Properties['policy']
        if (-not $content -or $null -eq $content.Value -or
            -not $content.Value.PSObject.Properties['rules'] -or $content.Value.rules -isnot [array]) {
            throw 'Lifecycle metadata is incomplete; existing policy rules cannot be verified or replaced.'
        }
        $snapshot = $content.Value
        $rules = @($snapshot.rules)
        $ruleCount = $rules.Count
        $owned = @($rules | Where-Object name -IEQ $rule.name)
        if ($owned.Count -eq 1) {
            $actual = $owned[0] | ConvertTo-Json -Depth 100 | ConvertFrom-Json -AsHashtable -Depth 100
            # Azure may return omitted optional fields as null; those fields have no lifecycle effect.
            $pending = [Collections.Generic.Queue[object]]::new()
            $pending.Enqueue($actual)
            while ($pending.Count) {
                $item = $pending.Dequeue()
                if ($item -is [Collections.IDictionary]) {
                    foreach ($key in @($item.Keys)) {
                        if ($null -eq $item[$key]) { $item.Remove($key) }
                        else { $pending.Enqueue($item[$key]) }
                    }
                } elseif ($item -is [Collections.IEnumerable] -and $item -isnot [string]) {
                    foreach ($entry in $item) {
                        if ($null -ne $entry) { $pending.Enqueue($entry) }
                    }
                }
            }
            $definition = $actual['definition']
            $filters = if ($definition -is [Collections.IDictionary]) { $definition['filters'] } else { $null }
            if ($filters -is [Collections.IDictionary] -and $filters['prefixMatch'] -is [array]) {
                $filters['prefixMatch'] = @($filters['prefixMatch'] | Sort-Object -CaseSensitive)
            }
            $actions = if ($definition -is [Collections.IDictionary]) { $definition['actions'] } else { $null }
            foreach ($kind in 'baseBlob', 'snapshot', 'version') {
                $field = if ($kind -eq 'baseBlob') {
                    'daysAfterModificationGreaterThan'
                } else { 'daysAfterCreationGreaterThan' }
                $kindActions = if ($actions -is [Collections.IDictionary]) { $actions[$kind] } else { $null }
                $delete = if ($kindActions -is [Collections.IDictionary]) { $kindActions['delete'] } else { $null }
                if ($delete -is [Collections.IDictionary] -and $delete.ContainsKey($field)) {
                    $days = $delete[$field]
                    if ($days -is [ValueType] -and $days -isnot [bool] -and $days -eq 1) {
                        $delete[$field] = 1
                    }
                }
            }
            $expected = $rule | ConvertTo-Json -Depth 100 | ConvertFrom-Json -AsHashtable -Depth 100
            $expected.definition.filters.prefixMatch = @($expected.definition.filters.prefixMatch | Sort-Object -CaseSensitive)
            $ready = (Get-DocIntelObjectHash $actual) -ceq (Get-DocIntelObjectHash $expected)
        }
        $reason = if ($owned.Count) {
            'The owned lifecycle rule differs, is disabled, or is duplicated. Operator review is required.'
        } elseif ($ruleCount -ge 100) {
            'The existing lifecycle policy has reached the 100-rule limit. No rule will be removed automatically.'
        } else { 'The exact one-day, application-prefix-only lifecycle rule is missing.' }
    }
    return @{
        Resource = $StorageResourceId.TrimEnd('/') + '/managementPolicies/default'
        Status = $(if ($ready) { 'Ready' } else { 'ActionRequired' })
        EligibleAfterDays = 1
        Prefixes = $rule.definition.filters.prefixMatch
        RequiredRule = $rule
        ExistingRuleCount = $ruleCount
        PolicyReadSucceeded = $null -ne $Policy
        ExistingPolicySnapshotHash = Get-DocIntelObjectHash $snapshot
        ExistingRulesPreserved = $true
        AutomaticExistingPolicyWrite = $false
        Blocker = $(if ($ready) { '' } else {
            "$reason Azure requires whole-policy writes; no conditional merge guarantee has been established. " +
            'An authorized policy owner must serialize writers and append/verify this rule while preserving every ' +
            'existing rule and all account settings. This setup never replaces an existing account policy.'
        })
    }
}

function Get-DocIntelTranslationStagingTags {
    param([string]$ApplicationResourceId, [string]$RuntimeIdentityResourceId, [string]$TranslatorResourceId)
    $values = @{
        'docintel-owner-app' = $ApplicationResourceId
        'docintel-runtime' = $RuntimeIdentityResourceId
        'docintel-translator' = $TranslatorResourceId
    }
    $tags = @{ 'docintel-purpose' = 'translation-staging-v1' }
    foreach ($entry in $values.GetEnumerator()) {
        if ([String]::IsNullOrWhiteSpace($entry.Value) -or $entry.Value.Length -gt 256) {
            throw 'Staging ownership requires nonempty resource identities within Azure tag limits.'
        }
        $tags[$entry.Key] = $entry.Value.ToLowerInvariant()
    }
    return $tags
}

function Assert-DocIntelTranslationStagingAccount {
    param([object]$Account, [hashtable]$ExpectedTags, [string]$ExpectedResourceId, [string]$Location)
    $value = $Account | ConvertTo-Json -Depth 100 | ConvertFrom-Json -AsHashtable -Depth 100
    if (-not $value -or $value['id'] -ine $ExpectedResourceId -or
        $value['name'] -cne ($ExpectedResourceId -split '/')[-1] -or
        $value['kind'] -cne 'StorageV2' -or $value['sku']['name'] -cne 'Standard_LRS' -or
        $value['location'] -ine $Location) { throw 'Staging account metadata/type/region does not match the reviewed target.' }
    foreach ($key in $ExpectedTags.Keys) {
        if (-not ($value['tags'] -is [Collections.IDictionary]) -or
            $value['tags'][$key] -cne $ExpectedTags[$key]) {
            throw 'Staging account collision or unknown ownership. Existing accounts are never silently adopted.'
        }
    }
    $settings = if ($value['properties'] -is [Collections.IDictionary]) { $value['properties'] } else { $value }
    $httpsOnly = if ($settings.Contains('supportsHttpsTrafficOnly')) {
        $settings['supportsHttpsTrafficOnly']
    } else { $settings['enableHttpsTrafficOnly'] }
    if ($settings['minimumTlsVersion'] -cne 'TLS1_2' -or $httpsOnly -ne $true -or
        $settings['allowBlobPublicAccess'] -ne $false -or $settings['allowSharedKeyAccess'] -ne $false -or
        $settings['isHnsEnabled'] -eq $true) {
        throw 'Owned staging security settings are incompatible; they will not be overwritten.'
    }
    return "https://$($value['name']).blob.core.windows.net/"
}

function Get-DocIntelArmGuid {
    param([Parameter(Mandatory)][string[]]$Values)
    # ARM guid() uses RFC 4122 UUIDv5, this fixed namespace and case-sensitive, hyphen-joined seeds.
    $namespace = ([guid]'11fb06fb-712d-4ddd-98c7-e71bbd588830').ToByteArray()
    foreach ($part in @(@(0, 4), @(4, 2), @(6, 2))) { [Array]::Reverse($namespace, $part[0], $part[1]) }
    $name = [Text.Encoding]::UTF8.GetBytes(($Values -join '-'))
    $hash = [Security.Cryptography.SHA1]::HashData([byte[]]($namespace + $name))
    [byte[]]$bytes = $hash[0..15]
    $bytes[6] = ($bytes[6] -band 15) -bor 80
    $bytes[8] = ($bytes[8] -band 63) -bor 128
    foreach ($part in @(@(0, 4), @(4, 2), @(6, 2))) { [Array]::Reverse($bytes, $part[0], $part[1]) }
    return ([guid]::new($bytes)).ToString()
}

function Test-DocIntelTranslationPermission {
    [CmdletBinding()]
    param(
        [object[]]$Assignments,
        [Parameter(Mandatory)][string]$PrincipalId,
        [Parameter(Mandatory)][string]$RequiredRoleId,
        [Parameter(Mandatory)][string]$RequiredScope,
        [Parameter(Mandatory)][string]$StagingAccountScope
    )
    $reader = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
    $contributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    $dataOwner = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
    $cognitive = 'a97b65f3-24c7-4388-baec-2e87135dc908'
    $required = $RequiredScope.TrimEnd('/')
    $account = $StagingAccountScope.TrimEnd('/')
    $dataRequirement = $RequiredRoleId -in @($reader, $contributor)
    if ($dataRequirement -and $required -notin @(
        "$account/blobServices/default/containers/translation-source",
        "$account/blobServices/default/containers/translation-target")) {
        throw 'Translation data access can only be verified for the dedicated staging containers.'
    }
    if (-not $dataRequirement -and $RequiredRoleId -ine $cognitive) {
        throw 'Unsupported translation permission requirement.'
    }
    $conditioned = $false
    foreach ($assignment in @($Assignments | Where-Object { $null -ne $_ })) {
        $entry = $assignment | ConvertTo-Json -Depth 100 | ConvertFrom-Json -AsHashtable -Depth 100
        if ($entry['principalId'] -ine $PrincipalId -or -not $entry['scope'] -or -not $entry['roleDefinitionId']) {
            continue
        }
        $scope = ([string]$entry['scope']).TrimEnd('/')
        $role = ([string]$entry['roleDefinitionId'] -split '/')[-1]
        if ($dataRequirement) {
            if ($scope -in @($required, $account) -and $role -in @($reader, $contributor, $dataOwner)) {
                $sufficient = $role -in $(if ($RequiredRoleId -ieq $reader) {
                    @($reader, $contributor, $dataOwner)
                } else { @($contributor, $dataOwner) })
            } else { continue }
        } else {
            if ($role -ine $cognitive -or
                ($scope -ine $required -and
                    -not $required.StartsWith($scope + '/', [StringComparison]::OrdinalIgnoreCase))) {
                continue
            }
            $sufficient = $true
        }
        $hasCondition = -not [String]::IsNullOrEmpty([string]$entry['condition']) -or
            -not [String]::IsNullOrEmpty([string]$entry['conditionVersion'])
        if ($hasCondition) {
            $conditioned = $true
        } elseif ($sufficient) {
            return $true
        }
    }
    if ($conditioned) {
        throw 'A relevant data/inference grant is conditional and no sufficient unconditioned access was verified; no bypass or redundant grant is planned.'
    }
    return $false
}

Export-ModuleMember -Function Read-DocIntelRelease, Get-DocIntelObjectHash, Get-DocIntelArmGuid,
    Test-DocIntelTranslationPermission,
    Get-DocIntelTranslationLifecycleRule, Get-DocIntelTranslationLifecycleState,
    Get-DocIntelTranslationStagingTags, Assert-DocIntelTranslationStagingAccount
