[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$operationalLog=$null
try {
    if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'SCOM_PROFILE_DESKTOP_REQUIRED' }
    $text = [Console]::In.ReadToEnd()
    if ([Text.Encoding]::UTF8.GetByteCount($text) -gt 1048576) { throw 'SCOM_PROFILE_INPUT_BUDGET' }
    $inputData = $text | ConvertFrom-Json
    if ($inputData.protocolVersion -ne 1 -or $null -eq $inputData.configuration -or $null -eq $inputData.request) {
        throw 'SCOM_PROFILE_PROTOCOL_INVALID'
    }
    $config = $inputData.configuration
    $inventoryKind='generic'
    if ($null -ne $config.PSObject.Properties['inventoryKind']) {
        $inventoryKind=$config.inventoryKind
        if ($inventoryKind -cnotin @('generic','supportedWindows')) { throw 'SCOM_PROFILE_INVENTORY_KIND_INVALID' }
    }
    if ($null -ne $config.PSObject.Properties['operationalLogging'] -and $null -ne $config.operationalLogging) {
        Import-Module (Join-Path $PSScriptRoot 'ScomOperationalLogging.psm1') -ErrorAction Stop | Out-Null
        $operationalLog=New-SCOMOperationalLog -Configuration $config.operationalLogging
        Write-SCOMOperationalEvent -Log $operationalLog -Event 'profile.started'
    }
    elseif ($inventoryKind -ceq 'supportedWindows') { throw 'SCOM_PROFILE_LOGGING_REQUIRED' }
    if ($inventoryKind -ceq 'supportedWindows' -and (
        $null -eq $config.PSObject.Properties['windowsMapping'] -or $null -eq $config.windowsMapping -or
        $config.windowsMapping.verified -isnot [bool] -or -not $config.windowsMapping.verified)) {
        throw 'SCOM_WINDOWS_MAPPING_UNVERIFIED'
    }
    if ($config.scopeKind -cnotin @('group','managementGroup') -or
        $config.scopeVisibilityVerified -isnot [bool] -or -not $config.scopeVisibilityVerified) {
        throw 'SCOM_PROFILE_SCOPE_UNVERIFIED'
    }
    if ($config.scopeKind -ceq 'group') {
        if ($config.membershipQueryContractVerified -isnot [bool] -or -not $config.membershipQueryContractVerified) {
            throw 'SCOM_PROFILE_SCOPE_UNVERIFIED'
        }
    }
    elseif ($config.wholeManagementGroupApproved -isnot [bool] -or -not $config.wholeManagementGroupApproved -or
        $config.enumerationQueryContractVerified -isnot [bool] -or -not $config.enumerationQueryContractVerified -or
        $null -ne $config.groupId) { throw 'SCOM_PROFILE_SCOPE_UNVERIFIED' }
    $modulePath = [string]$config.operationsManagerModule
    if (-not [IO.Path]::IsPathRooted($modulePath) -or $modulePath -notmatch '^[A-Za-z]:\\' -or
        $config.includeAlertDescriptions -isnot [bool]) { throw 'SCOM_PROFILE_CONFIGURATION_INVALID' }
    $module = [IO.Path]::GetFullPath($modulePath)
    if ($module.StartsWith('\\') -or
        -not (Test-Path -LiteralPath $module -PathType Leaf)) { throw 'SCOM_PROFILE_MODULE_PATH_INVALID' }
    $managementGroupId = [Guid]$config.managementGroupId
    $groupId = [Guid]::Empty
    if ($config.scopeKind -ceq 'group') { $groupId=[Guid]$config.groupId }
    if ($managementGroupId -eq [Guid]::Empty -or
        ($config.scopeKind -ceq 'group' -and $groupId -eq [Guid]::Empty) -or
        [string]::IsNullOrWhiteSpace([string]$config.managementServer)) { throw 'SCOM_PROFILE_SOURCE_INVALID' }
    if ($null -ne $operationalLog) { Write-SCOMOperationalEvent -Log $operationalLog -Event 'configuration.validated' }
    Import-Module $module -ErrorAction Stop | Out-Null
    Import-Module (Join-Path $PSScriptRoot 'ScomNormalization.psm1') -ErrorAction Stop | Out-Null
    New-SCOMManagementGroupConnection -ComputerName ([string]$config.managementServer) -ErrorAction Stop | Out-Null
    $groups = @(Get-SCOMManagementGroup -ErrorAction Stop)
    if ($groups.Count -ne 1 -or [Guid]$groups[0].Id -ne $managementGroupId) {
        throw 'SCOM_PROFILE_MANAGEMENT_GROUP_MISMATCH'
    }
    if ($null -ne $operationalLog) { Write-SCOMOperationalEvent -Log $operationalLog -Event 'source.connected' }
    $scopeParameters=@{ScopeKind=$config.scopeKind}
    if ($config.scopeKind -ceq 'group') {
        $root = @(Get-SCOMGroup -Id $groupId -ErrorAction Stop)
        if ($root.Count -ne 1 -or [Guid]$root[0].Id -ne $groupId) { throw 'SCOM_PROFILE_APPROVED_GROUP_UNAVAILABLE' }
        $groupClass = @(Get-SCOMClass -Name 'Microsoft.SystemCenter.InstanceGroup' -ErrorAction Stop)
        $relationship = @(Get-SCRelationship -Name 'Microsoft.SystemCenter.InstanceGroupContainsEntities' -ErrorAction Stop)
        if ($groupClass.Count -ne 1 -or $relationship.Count -ne 1 -or -not $root[0].IsInstanceOf($groupClass[0])) {
            throw 'SCOM_PROFILE_GROUP_TYPES_UNAVAILABLE'
        }
        $scopeParameters.GroupId=$groupId
        $scopeParameters.GroupClass=$groupClass[0]
        $scopeParameters.MembershipRelationshipClass=$relationship[0]
        $scopeParameters.MembershipQueryContractVerified=$config.membershipQueryContractVerified
    }
    elseif ($inventoryKind -ceq 'generic') {
        $entityClass=@(Get-SCOMClass -Name 'System.Entity' -ErrorAction Stop)
        if ($entityClass.Count -ne 1 -or [string]$entityClass[0].Name -cne 'System.Entity') {
            throw 'SCOM_PROFILE_ENTITY_CLASS_UNAVAILABLE'
        }
        $scopeParameters.EntityClass=$entityClass[0]
        $scopeParameters.WholeManagementGroupApproved=$config.wholeManagementGroupApproved
        $scopeParameters.EnumerationQueryContractVerified=$config.enumerationQueryContractVerified
    }
    else {
        $scopeParameters.WholeManagementGroupApproved=$config.wholeManagementGroupApproved
        $scopeParameters.EnumerationQueryContractVerified=$config.enumerationQueryContractVerified
    }
    $allowed = @{}
    foreach ($property in $config.allowedPropertiesByClass.PSObject.Properties) {
        $allowed[$property.Name] = @($property.Value)
    }
    $metrics = @{}
    if ($null -ne $config.PSObject.Properties['metricMappingsByClass']) {
        foreach ($property in $config.metricMappingsByClass.PSObject.Properties) {
            $metrics[$property.Name] = @($property.Value)
        }
    }
    $maxSeriesSamples = 10000
    $maxMetricSamples = 50000
    if ($null -ne $config.PSObject.Properties['maxSeriesSamples']) { $maxSeriesSamples=[int]$config.maxSeriesSamples }
    if ($null -ne $config.PSObject.Properties['maxMetricSamples']) { $maxMetricSamples=[int]$config.maxMetricSamples }
    $graphClasses = @()
    $maxGraphQueries = 10000
    if ($null -ne $config.PSObject.Properties['maxGraphQueries']) { $maxGraphQueries=[int]$config.maxGraphQueries }
    $graphVerified = $false
    if ($null -ne $config.PSObject.Properties['additionalRelationshipNames']) {
        if (@($config.additionalRelationshipNames).Count -gt 16 -or
            $config.additionalRelationshipContractVerified -isnot [bool]) { throw 'SCOM_PROFILE_GRAPH_CONFIGURATION_INVALID' }
        $graphVerified = $config.additionalRelationshipContractVerified
        if (@($config.additionalRelationshipNames).Count -gt 0 -and -not $graphVerified) {
            throw 'SCOM_PROFILE_GRAPH_CONTRACT_UNVERIFIED'
        }
        foreach ($name in $config.additionalRelationshipNames) {
            if ($name -isnot [string] -or [string]::IsNullOrWhiteSpace($name) -or $name -match '[*?\[\]]') {
                throw 'SCOM_PROFILE_GRAPH_CLASS_INVALID'
            }
            $found = @(Get-SCRelationship -Name $name -ErrorAction Stop)
            if ($found.Count -ne 1 -or [string]$found[0].Name -cne $name) { throw 'SCOM_PROFILE_GRAPH_CLASS_UNAVAILABLE' }
            $graphClasses += $found[0]
        }
    }
    $parameters = @{
        EntityObjects = $groups[0].EntityObjects
        ManagementGroupId = $managementGroupId
        InventoryKind = $inventoryKind
        ScopeVisibilityVerified = $config.scopeVisibilityVerified
        Request = $inputData.request
        AllowedPropertiesByClass = $allowed
        MetricMappingsByClass = $metrics
        AdditionalRelationshipClasses = $graphClasses
        AdditionalRelationshipContractVerified = $graphVerified
        MaxGraphQueries = $maxGraphQueries
        MaxSeriesSamples = $maxSeriesSamples
        MaxMetricSamples = $maxMetricSamples
        IncludeAlertDescriptions = [bool]$config.includeAlertDescriptions
        MaxObjects = [int]$config.maxObjects
        MaxGroups = [int]$config.maxGroups
        MaxAlerts = [int]$config.maxAlerts
        PageSize = [int]$config.pageSize
        DeadlineSeconds = [int]$config.deadlineSeconds
        ReadActiveAlerts = {
            param($object)
            Get-SCOMAlert -Instance $object -ResolutionState (0..254) -ErrorAction Stop
        }
        ReadAlertById = {
            param($id)
            Get-SCOMAlert -Id $id -ErrorAction Stop
        }
    }
    foreach ($key in $scopeParameters.Keys) { $parameters[$key]=$scopeParameters[$key] }
    if ($null -ne $config.PSObject.Properties['windowsMapping'] -and $null -ne $config.windowsMapping) {
        $mapping=$config.windowsMapping
        $types=@{}
        foreach ($name in @('computerClassName','operatingSystemClassName','volumeClassName')) {
            if ($mapping.$name -isnot [string] -or [string]::IsNullOrWhiteSpace($mapping.$name) -or
                $mapping.$name -match '[*?\[\]]') { throw 'SCOM_WINDOWS_CLASS_NAME_INVALID' }
            $found=@(Get-SCOMClass -Name $mapping.$name -ErrorAction Stop)
            if ($found.Count -ne 1 -or [string]$found[0].Name -cne $mapping.$name) { throw 'SCOM_WINDOWS_CLASS_UNAVAILABLE' }
            $types[$name]=$found[0]
        }
        $diskMappings=@('osHostsVolumeRelationship','computerHostsVolumeRelationship' | Where-Object {
            $null -ne $mapping.PSObject.Properties[$_] -and $null -ne $mapping.$_
        })
        if ($diskMappings.Count -ne 1) { throw 'SCOM_WINDOWS_VOLUME_MAPPING_AMBIGUOUS' }
        foreach ($name in @($mapping.computerHostsOsRelationship,$mapping.($diskMappings[0]))) {
            if (@($graphClasses | Where-Object Name -CEQ $name).Count -ne 1) { throw 'SCOM_WINDOWS_REQUIRED_GRAPH_CLASS_MISSING' }
        }
        $parameters.WindowsMapping=$mapping
        $parameters.WindowsComputerClass=$types.computerClassName
        $parameters.WindowsOsClass=$types.operatingSystemClassName
        $parameters.WindowsVolumeClass=$types.volumeClassName
        if ($null -ne $mapping.PSObject.Properties['monitoringRoleMapping'] -and
            $null -ne $mapping.monitoringRoleMapping) {
            $roles=$mapping.monitoringRoleMapping
            foreach ($name in @('agentClassName','managementServerClassName')) {
                if ($roles.$name -isnot [string] -or [string]::IsNullOrWhiteSpace($roles.$name) -or
                    $roles.$name -match '[*?\[\]]') { throw 'SCOM_WINDOWS_CLASS_NAME_INVALID' }
                $found=@(Get-SCOMClass -Name $roles.$name -ErrorAction Stop)
                if ($found.Count -ne 1 -or [string]$found[0].Name -cne $roles.$name) {
                    throw 'SCOM_WINDOWS_CLASS_UNAVAILABLE'
                }
                $types[$name]=$found[0]
            }
            if (@($graphClasses | Where-Object Name -CEQ $roles.computerHostsApplicationRelationship).Count -ne 1) {
                throw 'SCOM_WINDOWS_REQUIRED_GRAPH_CLASS_MISSING'
            }
            $parameters.WindowsAgentClass=$types.agentClassName
            $parameters.WindowsManagementServerClass=$types.managementServerClassName
        }
    }
    $scan = Get-SCOMGenericScan @parameters
    $output = @{ protocolVersion=1; scan=$scan } | ConvertTo-Json -Depth 24 -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($output) -gt 33554432) { throw 'SCOM_PROFILE_OUTPUT_BUDGET' }
    if ($null -ne $operationalLog) { Write-SCOMOperationalEvent -Log $operationalLog -Event 'scan.completed' }
    [Console]::Out.Write($output)
}
catch {
    if ($null -ne $operationalLog) {
        try { Write-SCOMOperationalEvent -Log $operationalLog -Event 'profile.failed' } catch { }
    }
    # Do not expose arbitrary SDK exceptions, configuration or customer values.
    [Console]::Error.Write('SCOM_GENERIC_PROFILE_FAILED')
    exit 1
}
