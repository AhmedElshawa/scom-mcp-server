Set-StrictMode -Version Latest

function Get-SCOMProjectionProperty {
    param([object]$Object, [string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    $properties = @($Object.GetProperties() | Where-Object Name -CEQ $Name)
    if ($properties.Count -gt 1) { throw 'SCOM_WINDOWS_PROPERTY_AMBIGUOUS' }
    if ($properties.Count -eq 0) { return $null }
    return $Object[$properties[0]].Value
}

function Get-SCOMProjectionNumber {
    param([object]$Object, [string]$Name, [double]$Multiplier)
    $raw = Get-SCOMProjectionProperty $Object $Name
    if ($null -eq $raw) { return $null }
    $number = 0.0
    if (-not [double]::TryParse([Convert]::ToString($raw, [Globalization.CultureInfo]::InvariantCulture),
        [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or
        [double]::IsNaN($number) -or [double]::IsInfinity($number) -or $number -le 0 -or
        [double]::IsNaN($Multiplier) -or [double]::IsInfinity($Multiplier) -or $Multiplier -le 0 -or
        $number * $Multiplier -le 0 -or $number * $Multiplier -gt 1e15) { throw 'SCOM_WINDOWS_CAPACITY_INVALID' }
    return $number * $Multiplier
}

function ConvertTo-SCOMWindowsInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]]$Objects,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]]$ObservedObjects,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]]$Relationships,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [Guid[]]$ExpectedComputerIds,
        [Parameter(Mandatory)] [object]$ComputerClass,
        [Parameter(Mandatory)] [object]$OperatingSystemClass,
        [Parameter(Mandatory)] [object]$VolumeClass,
        [Parameter(Mandatory)] [object]$Mapping,
        [hashtable]$AllowedPropertiesByClass = @{},
        [object]$AgentClass = $null,
        [object]$ManagementServerClass = $null
    )

    $captionProperty=$null
    if ($null -ne $Mapping.PSObject.Properties['osCaptionProperty'] -and $null -ne $Mapping.osCaptionProperty) {
        $captionProperty=$Mapping.osCaptionProperty
        if ($captionProperty -isnot [string] -or [string]::IsNullOrWhiteSpace($captionProperty) -or
            $captionProperty.Length -gt 128 -or $captionProperty -match '[*?\[\]]') {
            throw 'SCOM_WINDOWS_CAPTION_MAPPING_INVALID'
        }
    }
    $roleMapping=$null
    if ($null -ne $Mapping.PSObject.Properties['monitoringRoleMapping']) {
        $roleMapping=$Mapping.monitoringRoleMapping
    }
    if ($Mapping.verified -isnot [bool] -or -not $Mapping.verified) {
        throw 'SCOM_WINDOWS_MAPPING_UNVERIFIED'
    }
    if ($null -eq $roleMapping) {
        if ($Mapping.agentMonitoringVerified -isnot [bool] -or -not $Mapping.agentMonitoringVerified) {
            throw 'SCOM_WINDOWS_MAPPING_UNVERIFIED'
        }
    }
    else {
        if ($roleMapping.verified -isnot [bool] -or -not $roleMapping.verified) {
            throw 'SCOM_WINDOWS_MONITORING_ROLE_UNVERIFIED'
        }
        if ($null -eq $AgentClass -or $null -eq $ManagementServerClass -or
            $AgentClass.Name -ceq $ManagementServerClass.Name -or
            $AgentClass.Name -cne $roleMapping.agentClassName -or
            $ManagementServerClass.Name -cne $roleMapping.managementServerClassName -or
            $roleMapping.computerHostsApplicationRelationship -isnot [string] -or
            [string]::IsNullOrWhiteSpace($roleMapping.computerHostsApplicationRelationship) -or
            $roleMapping.computerHostsApplicationRelationship.Length -gt 256) {
            throw 'SCOM_WINDOWS_MONITORING_ROLE_MAPPING_INVALID'
        }
    }
    $classNames=@($ComputerClass.Name,$OperatingSystemClass.Name,$VolumeClass.Name)
    if (@($classNames | Sort-Object -Unique).Count -ne 3) { throw 'SCOM_WINDOWS_CLASS_MAPPING_AMBIGUOUS' }
    foreach ($name in @('hostnameProperty','osVersionProperty','memoryProperty','processorCountProperty',
        'volumeNameProperty','fileSystemProperty','volumeSizeProperty','computerHostsOsRelationship')) {
        if ($Mapping.$name -isnot [string] -or [string]::IsNullOrWhiteSpace($Mapping.$name) -or $Mapping.$name.Length -gt 256) {
            throw 'SCOM_WINDOWS_MAPPING_INVALID'
        }
        $diskMappings=@('osHostsVolumeRelationship','computerHostsVolumeRelationship' | Where-Object {
            $null -ne $Mapping.PSObject.Properties[$_] -and $null -ne $Mapping.$_
        })
        if ($diskMappings.Count -ne 1) { throw 'SCOM_WINDOWS_VOLUME_MAPPING_AMBIGUOUS' }
        $diskRelationship=$Mapping.($diskMappings[0])
        if ($diskRelationship -isnot [string] -or [string]::IsNullOrWhiteSpace($diskRelationship) -or
            $diskRelationship.Length -gt 256 -or $diskRelationship -ceq $Mapping.computerHostsOsRelationship) {
            throw 'SCOM_WINDOWS_MAPPING_INVALID'
        }
        $directDiskHosting=$diskMappings[0] -eq 'computerHostsVolumeRelationship'
    }
    foreach ($name in @('memoryToMiB','volumeSizeToMiB')) {
        $value=$Mapping.$name
        if ($value -is [string] -or $value -is [bool] -or $null -eq $value -or
            [double]::IsNaN([double]$value) -or [double]::IsInfinity([double]$value) -or [double]$value -le 0) {
            throw 'SCOM_WINDOWS_MAPPING_INVALID'
        }
    }
    $expected = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    foreach ($id in $ExpectedComputerIds) {
        if ($id -eq [Guid]::Empty -or -not $expected.Add($id)) { throw 'SCOM_WINDOWS_EXPECTED_MEMBERSHIP_INVALID' }
    }
    $byId = @{}
    $observed = @{}
    foreach ($object in $Objects) {
        $id=[Guid]$object.Id
        if ($byId.ContainsKey($id)) { throw 'SCOM_WINDOWS_DISCOVERY_DUPLICATE' }
        $byId[$id]=$object
    }
    foreach ($object in $ObservedObjects) { $observed[[Guid]$object.id]=$object }
    $devices=New-Object 'System.Collections.Generic.List[object]'
    $volumes=New-Object 'System.Collections.Generic.List[object]'
    $computerOs=@{}
    $osComputer=@{}
    $volumeComputer=@{}
    $computerRoles=@{}
    $roleParents=@{}
    $partial=$false
    if ($null -ne $roleMapping) {
        if ($roleMapping.computerHostsApplicationRelationship -cin @(
            $Mapping.computerHostsOsRelationship,$diskRelationship)) {
            throw 'SCOM_WINDOWS_MONITORING_ROLE_MAPPING_INVALID'
        }
        foreach ($edge in $Relationships) {
            if ($edge.className -cne $roleMapping.computerHostsApplicationRelationship) { continue }
            $source=[Guid]$edge.sourceId; $target=[Guid]$edge.targetId
            if (-not $byId.ContainsKey($source) -or -not $byId.ContainsKey($target) -or
                -not $byId[$source].IsInstanceOf($ComputerClass)) {
                throw 'SCOM_WINDOWS_MONITORING_ROLE_RELATIONSHIP_INVALID'
            }
            $isAgent=$byId[$target].IsInstanceOf($AgentClass)
            $isServer=$byId[$target].IsInstanceOf($ManagementServerClass)
            if ($isAgent -and $isServer) { throw 'SCOM_WINDOWS_MONITORING_ROLE_AMBIGUOUS' }
            if (-not $isAgent -and -not $isServer) { continue }
            if ($computerRoles.ContainsKey($source) -or $roleParents.ContainsKey($target)) {
                throw 'SCOM_WINDOWS_MONITORING_ROLE_AMBIGUOUS'
            }
            $mode='agent'
            if ($isServer) { $mode='managementServer' }
            $computerRoles[$source]=$mode
            $roleParents[$target]=$source
        }
    }
    foreach ($edge in $Relationships) {
        $source=[Guid]$edge.sourceId; $target=[Guid]$edge.targetId
        if ($edge.className -ceq $Mapping.computerHostsOsRelationship) {
            if (-not $byId.ContainsKey($source) -or -not $byId.ContainsKey($target) -or
                -not $byId[$source].IsInstanceOf($ComputerClass) -or
                -not $byId[$target].IsInstanceOf($OperatingSystemClass)) { throw 'SCOM_WINDOWS_OS_RELATIONSHIP_INVALID' }
            if ($computerOs.ContainsKey($source) -or $osComputer.ContainsKey($target)) { throw 'SCOM_WINDOWS_OS_PARENT_AMBIGUOUS' }
            $computerOs[$source]=$target
            $osComputer[$target]=$source
        }
    }
    foreach ($edge in $Relationships) {
        if ($edge.className -cne $diskRelationship) { continue }
        $source=[Guid]$edge.sourceId; $target=[Guid]$edge.targetId
        $sourceClass=$OperatingSystemClass
        if ($directDiskHosting) { $sourceClass=$ComputerClass }
        if (-not $byId.ContainsKey($source) -or -not $byId.ContainsKey($target) -or
            -not $byId[$source].IsInstanceOf($sourceClass)) { throw 'SCOM_WINDOWS_VOLUME_RELATIONSHIP_INVALID' }
        # A logical-device relationship can also host non-volume device classes.
        if (-not $byId[$target].IsInstanceOf($VolumeClass)) { continue }
        if ($volumeComputer.ContainsKey($target)) { throw 'SCOM_WINDOWS_VOLUME_PARENT_AMBIGUOUS' }
        if ($directDiskHosting) { $volumeComputer[$target]=$source }
        else {
            if (-not $osComputer.ContainsKey($source)) { $partial=$true; continue }
            $volumeComputer[$target]=$osComputer[$source]
        }
    }
    foreach ($object in $Objects) {
        $id=[Guid]$object.Id
        if (-not $object.IsInstanceOf($ComputerClass)) { continue }
        if (-not $expected.Contains($id) -or -not $observed.ContainsKey($id)) { throw 'SCOM_WINDOWS_COMPUTER_OUTSIDE_EXPECTED_SCOPE' }
        $hostname=Get-SCOMProjectionProperty $object $Mapping.hostnameProperty
        if ($hostname -isnot [string] -or [string]::IsNullOrWhiteSpace($hostname) -or $hostname.Length -gt 256 -or
            $hostname -match '\s|\*') { throw 'SCOM_WINDOWS_HOSTNAME_INVALID' }
        $osVersion=$null; $osCaption=$null; $memory=$null; $processors=$null
        $monitoringMode='agent'
        if ($null -ne $roleMapping) {
            $monitoringMode='unknown'
            if ($computerRoles.ContainsKey($id)) { $monitoringMode=$computerRoles[$id] }
            else { $partial=$true }
        }
        if ($computerOs.ContainsKey($id)) {
            $os=$byId[$computerOs[$id]]
            $osVersion=Get-SCOMProjectionProperty $os $Mapping.osVersionProperty
            if ($null -ne $osVersion -and ($osVersion -isnot [string] -or $osVersion.Length -gt 256)) {
                throw 'SCOM_WINDOWS_OS_VERSION_INVALID'
            }
            if ($null -ne $captionProperty) {
                $classes=@($os.GetMostDerivedClasses())
                if ($classes.Count -ne 1 -or
                    -not $AllowedPropertiesByClass.ContainsKey([string]$classes[0].Name) -or
                    $captionProperty -cnotin @($AllowedPropertiesByClass[[string]$classes[0].Name])) {
                    throw 'SCOM_WINDOWS_CAPTION_PROPERTY_NOT_APPROVED'
                }
                $osCaption=Get-SCOMProjectionProperty $os $captionProperty
                if ($null -ne $osCaption -and ($osCaption -isnot [string] -or $osCaption.Length -gt 256)) {
                    throw 'SCOM_WINDOWS_OS_CAPTION_INVALID'
                }
                if ([string]::IsNullOrWhiteSpace($osCaption)) { $osCaption=$null }
            }
            $memory=Get-SCOMProjectionNumber $os $Mapping.memoryProperty ([double]$Mapping.memoryToMiB)
        }
        else { $partial=$true }
        $cpu=Get-SCOMProjectionNumber $object $Mapping.processorCountProperty 1
        if ($null -ne $cpu) {
            if ($cpu -gt [int]::MaxValue -or $cpu -ne [Math]::Floor($cpu)) { throw 'SCOM_WINDOWS_PROCESSOR_COUNT_INVALID' }
            $processors=[int]$cpu
        }
        $devices.Add([pscustomobject]@{
            entityId=$id.ToString('D'); sourceObjectId=$id.ToString('D'); hostname=$hostname
            entityKind='computer'; osFamily='windows'; osVersion=$osVersion; osCaption=$osCaption
            health=$observed[$id].health; rawHealth=[string]$object.HealthState
            virtualization='unknown'; monitoringMode=$monitoringMode; memoryTotalMiB=$memory
            processorCount=$processors; discoveredAtUtc=$null; groupIds=@('scom-lab')
            sourceRefs=@('SCOM:'+$id.ToString('D'))
        })
    }
    foreach ($object in $Objects) {
        if (-not $object.IsInstanceOf($VolumeClass)) { continue }
        $id=[Guid]$object.Id
        if (-not $volumeComputer.ContainsKey($id)) { $partial=$true; continue }
        $parent=[Guid]$volumeComputer[$id]
        $name=Get-SCOMProjectionProperty $object $Mapping.volumeNameProperty
        $fileSystem=Get-SCOMProjectionProperty $object $Mapping.fileSystemProperty
        if ($name -isnot [string] -or [string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 256 -or
            ($null -ne $fileSystem -and ($fileSystem -isnot [string] -or $fileSystem.Length -gt 128))) {
            throw 'SCOM_WINDOWS_VOLUME_PROPERTY_INVALID'
        }
        $size=Get-SCOMProjectionNumber $object $Mapping.volumeSizeProperty ([double]$Mapping.volumeSizeToMiB)
        $volumes.Add([pscustomobject]@{
            entityId=$id.ToString('D'); parentEntityId=$parent.ToString('D'); deviceId=$name
            fileSystem=$fileSystem; sizeMiB=$size; sourceRefs=@('SCOM:'+$id.ToString('D'))
        })
    }
    if ($devices.Count -ne $expected.Count) { $partial=$true }
    foreach ($object in $Objects) {
        if ($object.IsInstanceOf($OperatingSystemClass) -and -not $osComputer.ContainsKey([Guid]$object.Id)) {
            $partial=$true
        }
    }
    [pscustomobject]@{ Inventory=@{devices=$devices.ToArray();volumes=$volumes.ToArray()}; Complete=(-not $partial) }
}

Export-ModuleMember -Function ConvertTo-SCOMWindowsInventory
