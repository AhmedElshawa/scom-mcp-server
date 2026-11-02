Set-StrictMode -Version Latest

function Read-SCOMObjectIdPages {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object] $EntityObjects,
        [Parameter(Mandatory)] [Guid[]] $ObjectIds,
        [ValidateRange(1, 100000)] [int] $MaxObjects = 10000,
        [ValidateRange(1, 100)] [int] $PageSize = 50
    )

    $ids = New-Object 'System.Collections.Generic.List[System.Guid]'
    $unique = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    foreach ($id in $ObjectIds) {
        if ($id -eq [Guid]::Empty -or -not $unique.Add($id)) {
            throw 'SCOM_READER_IDS_INVALID'
        }
        if ($ids.Count -ge $MaxObjects) {
            throw 'SCOM_READER_ID_BUDGET'
        }
        $ids.Add($id)
    }
    if ($ids.Count -eq 0) {
        throw 'SCOM_READER_EXPECTED_IDS_REQUIRED'
    }

    # Reflection avoids PowerShell 7 generic-call syntax on the SDK's Desktop host.
    $managerType = 'Microsoft.EnterpriseManagement.Monitoring.IMonitoringObjectsManagement' -as [type]
    $objectType = 'Microsoft.EnterpriseManagement.Monitoring.MonitoringObject' -as [type]
    $optionsType = 'Microsoft.EnterpriseManagement.Common.ObjectQueryOptions' -as [type]
    if (-not $managerType -or -not $objectType -or -not $optionsType -or
        -not $managerType.IsInstanceOfType($EntityObjects)) {
        throw 'SCOM_READER_SDK_NOT_COMPATIBLE'
    }
    $methods = @($managerType.GetMethods())
    foreach ($interface in $managerType.GetInterfaces()) {
        $methods += @($interface.GetMethods())
    }
    $candidate = @($methods | Where-Object {
        if ($_.Name -ne 'GetObjectReader' -or -not $_.IsGenericMethodDefinition) { return $false }
        $parameters = $_.GetParameters()
        return $_.GetGenericArguments().Count -eq 1 -and $parameters.Count -eq 2 -and
            $parameters[0].ParameterType -eq [System.Collections.Generic.ICollection[Guid]] -and
            $parameters[1].ParameterType -eq $optionsType
    } | Sort-Object { $_.ToString() } -Unique)
    if ($candidate.Count -ne 1) {
        throw 'SCOM_READER_SIGNATURE_UNSUPPORTED'
    }
    $options = [Activator]::CreateInstance($optionsType)
    $propertyRetrieval = $optionsType.GetProperty('DefaultPropertyRetrievalBehavior')
    if ($null -eq $propertyRetrieval -or -not $propertyRetrieval.CanWrite -or
        -not $propertyRetrieval.PropertyType.IsEnum -or
        'All' -cnotin [Enum]::GetNames($propertyRetrieval.PropertyType)) {
        throw 'SCOM_READER_PROPERTY_RETRIEVAL_UNSUPPORTED'
    }
    $options.DefaultPropertyRetrievalBehavior = [Enum]::Parse($propertyRetrieval.PropertyType, 'All')
    $options.MaxResultCount = $ids.Count + 1
    $method = $candidate[0].MakeGenericMethod([type[]]@($objectType))
    $arguments = New-Object 'System.Object[]' 2
    $arguments[0] = $ids.PSObject.BaseObject
    $arguments[1] = $options.PSObject.BaseObject
    $reader = $method.Invoke($EntityObjects.PSObject.BaseObject, $arguments)
    if ($null -eq $reader) {
        throw 'SCOM_READER_NOT_RETURNED'
    }
    Read-SCOMReaderPages -Reader $reader -ExpectedIds $ids.ToArray() -PageSize $PageSize
}

function Read-SCOMReaderPages {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object] $Reader,
        [Parameter(Mandatory)] [Guid[]] $ExpectedIds,
        [ValidateRange(1, 100)] [int] $PageSize = 50
    )

    $expected = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    foreach ($id in $ExpectedIds) {
        if ($id -eq [Guid]::Empty -or -not $expected.Add($id)) {
            throw 'SCOM_READER_IDS_INVALID'
        }
    }
    if ($expected.Count -eq 0 -or $expected.Count -gt 100000) {
        throw 'SCOM_READER_EXPECTED_IDS_REQUIRED'
    }
    $Reader.PageSize = $PageSize
    $count = [int]$Reader.Count
    if ($count -lt 0 -or $count -gt $expected.Count) {
        throw 'SCOM_READER_RESULT_OVERFLOW'
    }
    $found = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    $rows = New-Object 'System.Collections.Generic.List[object]'
    for ($offset = 0; $offset -lt $count; $offset += $PageSize) {
        $take = [Math]::Min($PageSize, $count - $offset)
        $page = $Reader.GetRange([int]$offset, [int]$take)
        if ($null -eq $page -or $page.Count -ne $take) {
            throw 'SCOM_READER_PAGE_INCOMPLETE'
        }
        foreach ($row in $page) {
            if ($null -eq $row) { throw 'SCOM_READER_OBJECT_INVALID' }
            $id = [Guid]::Empty
            if (-not [Guid]::TryParse([string]$row.Id, [ref]$id) -or
                -not $expected.Contains($id) -or -not $found.Add($id)) {
                throw 'SCOM_READER_OBJECT_OUTSIDE_IDS_OR_DUPLICATE'
            }
            $rows.Add($row)
        }
    }
    $missing = @($ExpectedIds | Where-Object { -not $found.Contains($_) })
    # This is ID-discovery coverage, never evidence that approved scope is complete.
    [pscustomobject]@{
        Objects = $rows.ToArray()
        ExpectedObjectCount = $expected.Count
        MissingObjectIds = $missing
        DiscoveryComplete = ($missing.Count -eq 0)
        ScopeMembershipVerified = $false
        SourceWorkBoundVerified = $false
    }
}

function Read-SCOMManagementGroupScope {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object]$EntityObjects,
        [Parameter(Mandatory)] [object]$EntityClass,
        [Parameter(Mandatory)] [bool]$WholeManagementGroupApproved,
        [Parameter(Mandatory)] [bool]$ScopeVisibilityVerified,
        [Parameter(Mandatory)] [bool]$EnumerationQueryContractVerified,
        [ValidateRange(1,100000)] [int]$MaxObjects=10000,
        [ValidateRange(1,100)] [int]$PageSize=50,
        [ValidateRange(1,7200)] [int]$DeadlineSeconds=900
    )
    if (-not $WholeManagementGroupApproved -or -not $ScopeVisibilityVerified -or
        -not $EnumerationQueryContractVerified) { throw 'SCOM_MANAGEMENT_GROUP_SCOPE_UNVERIFIED' }
    if ([string]$EntityClass.Name -cne 'System.Entity') { throw 'SCOM_MANAGEMENT_GROUP_ROOT_CLASS_INVALID' }
    $managerType='Microsoft.EnterpriseManagement.Monitoring.IMonitoringObjectsManagement' -as [type]
    $objectType='Microsoft.EnterpriseManagement.Monitoring.MonitoringObject' -as [type]
    $classType='Microsoft.EnterpriseManagement.Configuration.ManagementPackClass' -as [type]
    $optionsType='Microsoft.EnterpriseManagement.Common.ObjectQueryOptions' -as [type]
    if (-not $managerType -or -not $objectType -or -not $classType -or -not $optionsType -or
        -not $managerType.IsInstanceOfType($EntityObjects) -or -not $classType.IsInstanceOfType($EntityClass)) {
        throw 'SCOM_MANAGEMENT_GROUP_SDK_NOT_COMPATIBLE'
    }
    $methods=@($managerType.GetMethods())
    foreach ($interface in $managerType.GetInterfaces()) { $methods+=@($interface.GetMethods()) }
    $candidate=@($methods | Where-Object {
        if ($_.Name -ne 'GetObjectReader' -or -not $_.IsGenericMethodDefinition) { return $false }
        $p=$_.GetParameters()
        return $_.GetGenericArguments().Count -eq 1 -and $p.Count -eq 2 -and
            $p[0].ParameterType -eq $classType -and $p[1].ParameterType -eq $optionsType
    } | Sort-Object { $_.ToString() } -Unique)
    if ($candidate.Count -ne 1) { throw 'SCOM_MANAGEMENT_GROUP_SIGNATURE_UNSUPPORTED' }
    $options=[Activator]::CreateInstance($optionsType)
    $options.MaxResultCount=$MaxObjects+1
    $arguments=New-Object 'System.Object[]' 2
    $arguments[0]=$EntityClass.PSObject.BaseObject
    $arguments[1]=$options.PSObject.BaseObject
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $reader=$candidate[0].MakeGenericMethod([type[]]@($objectType)).Invoke($EntityObjects.PSObject.BaseObject,$arguments)
    if ($null -eq $reader) { throw 'SCOM_MANAGEMENT_GROUP_READER_MISSING' }
    $reader.PageSize=$PageSize
    $initialCount=$reader.Count
    if ($initialCount -isnot [int]) { throw 'SCOM_MANAGEMENT_GROUP_READER_COUNT_INVALID' }
    $count=[int]$initialCount
    if ($count -lt 0 -or $count -gt $MaxObjects) { throw 'SCOM_MANAGEMENT_GROUP_SCOPE_OVERFLOW' }
    $ids=New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    $rows=New-Object 'System.Collections.Generic.List[object]'
    for ($offset=0; $offset -lt $count; $offset+=$PageSize) {
        if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'SCOM_MANAGEMENT_GROUP_DEADLINE' }
        $take=[Math]::Min($PageSize,$count-$offset)
        $page=$reader.GetRange([int]$offset,[int]$take)
        if ($null -eq $page -or $page.Count -ne $take) { throw 'SCOM_MANAGEMENT_GROUP_PAGE_INCOMPLETE' }
        foreach ($row in $page) {
            $id=[Guid]::Empty
            if ($null -eq $row -or -not [Guid]::TryParse([string]$row.Id,[ref]$id) -or
                $id -eq [Guid]::Empty -or -not $ids.Add($id) -or -not $row.IsInstanceOf($EntityClass)) {
                throw 'SCOM_MANAGEMENT_GROUP_OBJECT_INVALID'
            }
            $rows.Add($row)
        }
    }
    if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds -or [int]$reader.Count -ne $count) {
        throw 'SCOM_MANAGEMENT_GROUP_READER_CHANGED_OR_DEADLINE'
    }
    [pscustomobject]@{
        ExpectedObjectIds=@($ids | Sort-Object)
        MembershipObjects=$rows.ToArray()
        MembershipVerified=$true
        SourceWorkBoundVerified=$false
    }
}

function Resolve-SCOMApprovedGroupMembership {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [Guid] $GroupId,
        [Parameter(Mandatory)] [scriptblock] $ReadDirectMembers,
        [Parameter(Mandatory)] [bool] $ScopeVisibilityVerified,
        [ValidateRange(1, 100000)] [int] $MaxObjects = 10000,
        [ValidateRange(1, 10000)] [int] $MaxGroups = 1000,
        [ValidateRange(1, 7200)] [int] $DeadlineSeconds = 300
    )

    if (-not $ScopeVisibilityVerified -or $GroupId -eq [Guid]::Empty) {
        throw 'SCOM_GROUP_SCOPE_UNVERIFIED'
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $pending = New-Object 'System.Collections.Generic.Queue[System.Guid]'
    $scheduled = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    $members = New-Object 'System.Collections.Generic.Dictionary[System.Guid,System.Boolean]'
    $scheduled.Add($GroupId) | Out-Null
    $pending.Enqueue($GroupId)
    $members.Add($GroupId, $true)
    $queries = 0
    while ($pending.Count -gt 0) {
        if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'SCOM_GROUP_DEADLINE' }
        $current = $pending.Dequeue()
        # Callback must return one bounded page envelope, not pipeline member rows.
        # A result cap does not prove the underlying SDK call bounds source work.
        $result = & $ReadDirectMembers $current ($MaxObjects + 1)
        $queries++
        if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'SCOM_GROUP_DEADLINE' }
        if ($null -eq $result -or $result -is [array] -or
            $null -eq $result.PSObject.Properties['Complete'] -or
            $result.Complete -isnot [bool] -or -not $result.Complete -or
            $null -eq $result.PSObject.Properties['Members'] -or $null -eq $result.Members) {
            throw 'SCOM_GROUP_MEMBERSHIP_INCOMPLETE'
        }
        if ($result.Members -isnot [array] -or $result.Members.Count -gt $MaxObjects) {
            throw 'SCOM_GROUP_MEMBER_BUDGET'
        }
        $direct = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
        foreach ($member in $result.Members) {
            if ($null -eq $member -or $null -eq $member.PSObject.Properties['Id'] -or
                $null -eq $member.PSObject.Properties['IsGroup'] -or $member.IsGroup -isnot [bool]) {
                throw 'SCOM_GROUP_MEMBER_INVALID'
            }
            $id = [Guid]::Empty
            if (-not [Guid]::TryParse([string]$member.Id, [ref]$id) -or
                $id -eq [Guid]::Empty -or -not $direct.Add($id)) {
                throw 'SCOM_GROUP_MEMBER_INVALID'
            }
            if ($members.ContainsKey($id)) {
                if ($members[$id] -ne $member.IsGroup) { throw 'SCOM_GROUP_CLASSIFICATION_CONFLICT' }
            }
            else {
                if ($members.Count -ge $MaxObjects) { throw 'SCOM_GROUP_MEMBER_BUDGET' }
                $members.Add($id, $member.IsGroup)
            }
            if ($member.IsGroup -and -not $scheduled.Contains($id)) {
                if ($scheduled.Count -ge $MaxGroups) { throw 'SCOM_GROUP_NESTING_BUDGET' }
                $scheduled.Add($id) | Out-Null
                $pending.Enqueue($id)
            }
        }
    }
    [pscustomobject]@{
        ExpectedObjectIds = @($members.Keys | Sort-Object)
        GroupIds = @($scheduled | Sort-Object)
        MembershipVerified = $true
        SourceQueries = $queries
        SourceWorkBoundVerified = $false
    }
}

function Read-SCOMDirectRelationshipRows {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object] $EntityObjects,
        [Parameter(Mandatory)] [Guid] $SourceId,
        [Parameter(Mandatory)] [object] $RelationshipClass,
        [bool] $IncludeDerivedRelationships = $true,
        [ValidateRange(1, 100000)] [int] $MaxRelationships = 10000
    )

    if ($SourceId -eq [Guid]::Empty) { throw 'SCOM_RELATIONSHIP_SOURCE_INVALID' }
    $managerType = 'Microsoft.EnterpriseManagement.Monitoring.IMonitoringObjectsManagement' -as [type]
    $objectType = 'Microsoft.EnterpriseManagement.Monitoring.MonitoringObject' -as [type]
    $relationshipType = 'Microsoft.EnterpriseManagement.Configuration.ManagementPackRelationship' -as [type]
    $derivedType = 'Microsoft.EnterpriseManagement.Configuration.DerivedClassTraversalDepth' -as [type]
    $traversalType = 'Microsoft.EnterpriseManagement.Common.TraversalDepth' -as [type]
    $optionsType = 'Microsoft.EnterpriseManagement.Common.ObjectQueryOptions' -as [type]
    if (-not $managerType -or -not $objectType -or -not $relationshipType -or
        -not $derivedType -or -not $traversalType -or -not $optionsType -or
        -not $managerType.IsInstanceOfType($EntityObjects) -or
        -not $relationshipType.IsInstanceOfType($RelationshipClass)) {
        throw 'SCOM_RELATIONSHIP_SDK_NOT_COMPATIBLE'
    }
    $methods = @($managerType.GetMethods())
    foreach ($interface in $managerType.GetInterfaces()) { $methods += @($interface.GetMethods()) }
    $candidate = @($methods | Where-Object {
        if ($_.Name -ne 'GetRelationshipObjectsWhereSource' -or -not $_.IsGenericMethodDefinition) { return $false }
        $p = $_.GetParameters()
        return $_.GetGenericArguments().Count -eq 1 -and $p.Count -eq 5 -and
            $p[0].ParameterType -eq [Guid] -and $p[1].ParameterType -eq $relationshipType -and
            $p[2].ParameterType -eq $derivedType -and $p[3].ParameterType -eq $traversalType -and
            $p[4].ParameterType -eq $optionsType
    } | Sort-Object { $_.ToString() } -Unique)
    if ($candidate.Count -ne 1) { throw 'SCOM_RELATIONSHIP_SIGNATURE_UNSUPPORTED' }
    $options = [Activator]::CreateInstance($optionsType)
    # Leave MaxResultCount untouched: this overload rejects positive limits,
    # and the SDK setter rejects zero despite its getter defaulting to zero.
    # The budget below applies after materialization, not to source work.
    $arguments = New-Object 'System.Object[]' 5
    $arguments[0] = $SourceId
    $arguments[1] = $RelationshipClass.PSObject.BaseObject
    $derived = 'None'
    if ($IncludeDerivedRelationships) { $derived='Recursive' }
    $arguments[2] = [Enum]::Parse($derivedType, $derived)
    $arguments[3] = [Enum]::Parse($traversalType, 'OneLevel')
    $arguments[4] = $options.PSObject.BaseObject
    $result = $candidate[0].MakeGenericMethod([type[]]@($objectType)).Invoke(
        $EntityObjects.PSObject.BaseObject, $arguments)
    if ($null -eq $result -or $result.Count -gt $MaxRelationships) {
        throw 'SCOM_RELATIONSHIP_RESULT_OVERFLOW'
    }
    $seen = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    $rows = New-Object 'System.Collections.Generic.List[object]'
    foreach ($row in $result) {
        if ($null -eq $row -or $null -eq $row.SourceObject -or $null -eq $row.TargetObject) {
            throw 'SCOM_RELATIONSHIP_ROW_INVALID'
        }
        $id = [Guid]::Empty
        $source = [Guid]::Empty
        $target = [Guid]::Empty
        if (-not [Guid]::TryParse([string]$row.Id, [ref]$id) -or $id -eq [Guid]::Empty -or
            -not $seen.Add($id) -or
            -not [Guid]::TryParse([string]$row.SourceObject.Id, [ref]$source) -or $source -ne $SourceId -or
            -not [Guid]::TryParse([string]$row.TargetObject.Id, [ref]$target) -or $target -eq [Guid]::Empty) {
            throw 'SCOM_RELATIONSHIP_ROW_INVALID'
        }
        $rows.Add($row)
    }
    # A scoped overload and local budget do not establish visibility completeness or
    # prove SDK/database work bounds. The adapter must validate both separately.
    [pscustomobject]@{
        Relationships = $rows.ToArray()
        ScopeMembershipVerified = $false
        SourceWorkBoundVerified = $false
    }
}

function ConvertTo-SCOMObservedObject {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object] $Object,
        [Parameter(Mandatory)] [DateTimeOffset] $ObservedAtUtc,
        [string[]] $AllowedProperties = @()
    )

    if ($ObservedAtUtc.Offset -ne [TimeSpan]::Zero -or $ObservedAtUtc -eq [DateTimeOffset]::MinValue -or
        $AllowedProperties.Count -gt 64 -or
        @($AllowedProperties | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_.Length -gt 128 }).Count -gt 0 -or
        @($AllowedProperties | Sort-Object -Unique).Count -ne $AllowedProperties.Count) {
        throw 'SCOM_NORMALIZATION_INPUT_INVALID'
    }
    $id = [Guid]::Empty
    if (-not [Guid]::TryParse([string]$Object.Id, [ref]$id) -or $id -eq [Guid]::Empty) {
        throw 'SCOM_NORMALIZATION_OBJECT_ID_INVALID'
    }
    $classes = @($Object.GetMostDerivedClasses())
    if ($classes.Count -ne 1 -or $null -eq $classes[0] -or
        [string]::IsNullOrWhiteSpace([string]$classes[0].Name) -or ([string]$classes[0].Name).Length -gt 256) {
        throw 'SCOM_NORMALIZATION_CLASS_AMBIGUOUS'
    }
    $bases = @($classes[0].GetBaseTypes() | ForEach-Object {
        if ($null -eq $_ -or [string]::IsNullOrWhiteSpace([string]$_.Name) -or ([string]$_.Name).Length -gt 256) {
            throw 'SCOM_NORMALIZATION_BASE_CLASS_INVALID'
        }
        [string]$_.Name
    } | Sort-Object -Unique)
    if ($bases.Count -gt 32) { throw 'SCOM_NORMALIZATION_BASE_CLASS_BUDGET' }
    $health = switch ([string]$Object.HealthState) {
        'Success' { 'healthy' }
        'Warning' { 'warning' }
        'Error' { 'critical' }
        'Uninitialized' { 'unknown' }
        default { throw 'SCOM_NORMALIZATION_HEALTH_INVALID' }
    }
    $display = [string]$Object.DisplayName
    if ([string]::IsNullOrWhiteSpace($display) -or $display.Length -gt 512) {
        throw 'SCOM_NORMALIZATION_DISPLAY_NAME_INVALID'
    }
    $properties = [ordered]@{}
    # Only requested discovered properties cross the boundary, never arbitrary
    # SDK properties, path-derived topology or serialized SDK object graphs.
    if ($AllowedProperties.Count -gt 0) {
        $discovered = @($Object.GetProperties())
        foreach ($name in $AllowedProperties) {
            $matches = @($discovered | Where-Object Name -CEQ $name)
            if ($matches.Count -gt 1) { throw 'SCOM_NORMALIZATION_PROPERTY_AMBIGUOUS' }
            $value = $null
            if ($matches.Count -eq 1) {
                $raw = $Object[$matches[0]].Value
                if ($null -ne $raw) {
                    if ($raw -is [DateTime]) {
                        if ($raw.Kind -ne [DateTimeKind]::Utc) { throw 'SCOM_NORMALIZATION_PROPERTY_TIME_INVALID' }
                        $value = $raw.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
                    }
                    elseif ($raw -is [string] -or $raw -is [Guid] -or $raw -is [bool] -or
                        $raw -is [byte] -or $raw -is [int16] -or $raw -is [int32] -or $raw -is [int64] -or
                        $raw -is [uint16] -or $raw -is [uint32] -or $raw -is [uint64] -or
                        $raw -is [decimal] -or $raw -is [double] -or $raw -is [single]) {
                        if (($raw -is [double] -or $raw -is [single]) -and
                            ([double]::IsNaN([double]$raw) -or [double]::IsInfinity([double]$raw))) {
                            throw 'SCOM_NORMALIZATION_PROPERTY_NUMBER_INVALID'
                        }
                        $value = [Convert]::ToString($raw, [Globalization.CultureInfo]::InvariantCulture)
                    }
                    else { throw 'SCOM_NORMALIZATION_PROPERTY_TYPE_INVALID' }
                    if ($value.Length -gt 2000) { throw 'SCOM_NORMALIZATION_PROPERTY_BUDGET' }
                }
            }
            $properties[$name] = $value
        }
    }
    [pscustomobject][ordered]@{
        id = $id.ToString('D')
        className = [string]$classes[0].Name
        baseClasses = @($bases)
        displayName = $display
        health = $health
        observedAtUtc = $ObservedAtUtc.ToString('o')
        properties = $properties
    }
}

function ConvertTo-SCOMObservedAlert {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object] $Alert,
        [Parameter(Mandatory)] [Guid] $ExpectedObjectId,
        [Parameter(Mandatory)] [DateTimeOffset] $ObservedAtUtc,
        [bool] $IncludeDescription = $false
    )

    $id = [Guid]::Empty
    $parent = [Guid]::Empty
    if ($ExpectedObjectId -eq [Guid]::Empty -or
        -not [Guid]::TryParse([string]$Alert.Id, [ref]$id) -or $id -eq [Guid]::Empty -or
        -not [Guid]::TryParse([string]$Alert.MonitoringObjectId, [ref]$parent) -or $parent -ne $ExpectedObjectId -or
        $ObservedAtUtc.Offset -ne [TimeSpan]::Zero -or $ObservedAtUtc -eq [DateTimeOffset]::MinValue) {
        throw 'SCOM_ALERT_ID_OR_PARENT_INVALID'
    }
    if ($null -eq $Alert.PSObject.Properties['ResolutionState'] -or $null -eq $Alert.ResolutionState -or
        ($Alert.ResolutionState -isnot [byte] -and $Alert.ResolutionState -isnot [int]) -or
        [int]$Alert.ResolutionState -lt 0 -or [int]$Alert.ResolutionState -gt 255) {
        throw 'SCOM_ALERT_RESOLUTION_INVALID'
    }
    # ManagementPackAlertSeverity: Information=0, Warning=1, Error=2.
    # MatchMonitorHealth is not an observed severity. Never invent its state
    # or default a missing/unknown severity to information.
    $severity = switch ([string]$Alert.Severity) {
        'Information' { 'information' }
        'Warning' { 'warning' }
        'Error' { 'critical' }
        default { throw 'SCOM_ALERT_SEVERITY_INVALID' }
    }
    $name = [string]$Alert.Name
    if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 512) { throw 'SCOM_ALERT_NAME_INVALID' }
    $description = $null
    if ($IncludeDescription) {
        if ($null -eq $Alert.PSObject.Properties['Description']) { throw 'SCOM_ALERT_DESCRIPTION_UNAVAILABLE' }
        if ($null -ne $Alert.Description) {
            if ($Alert.Description -isnot [string] -or $Alert.Description.Length -gt 2000) {
                throw 'SCOM_ALERT_DESCRIPTION_INVALID'
            }
            $description = $Alert.Description
        }
    }
    [pscustomobject][ordered]@{
        id = $id.ToString('D')
        objectId = $parent.ToString('D')
        name = $name
        description = $description
        severity = $severity
        resolutionState = [int]$Alert.ResolutionState
        observedAtUtc = $ObservedAtUtc.ToString('o')
    }
}

function Read-SCOMDirectGroupMembers {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object] $EntityObjects,
        [Parameter(Mandatory)] [Guid] $GroupId,
        [Parameter(Mandatory)] [object] $MembershipRelationshipClass,
        [Parameter(Mandatory)] [object] $GroupClass,
        [Parameter(Mandatory)] [bool] $MembershipQueryContractVerified,
        [ValidateRange(1, 100000)] [int] $MaxMembers = 10000
    )

    if (-not $MembershipQueryContractVerified -or
        [string]$MembershipRelationshipClass.Name -cne 'Microsoft.SystemCenter.InstanceGroupContainsEntities' -or
        [string]$GroupClass.Name -cne 'Microsoft.SystemCenter.InstanceGroup') {
        throw 'SCOM_GROUP_QUERY_CONTRACT_UNVERIFIED'
    }
    $result = Read-SCOMDirectRelationshipRows -EntityObjects $EntityObjects -SourceId $GroupId `
        -RelationshipClass $MembershipRelationshipClass -MaxRelationships $MaxMembers
    $members = New-Object 'System.Collections.Generic.Dictionary[System.Guid,System.Boolean]'
    foreach ($relationship in $result.Relationships) {
        $target = $relationship.TargetObject
        $id = [Guid]$target.Id
        $isGroup = $target.IsInstanceOf($GroupClass)
        if ($isGroup -isnot [bool]) { throw 'SCOM_GROUP_CLASSIFICATION_INVALID' }
        if ($members.ContainsKey($id)) {
            if ($members[$id] -ne $isGroup) { throw 'SCOM_GROUP_CLASSIFICATION_CONFLICT' }
        }
        else { $members.Add($id, $isGroup) }
    }
    [pscustomobject]@{
        Complete = $true
        Members = @($members.Keys | Sort-Object | ForEach-Object {
            [pscustomobject]@{ Id = $_; IsGroup = $members[$_] }
        })
        Relationships = $result.Relationships
        SourceWorkBoundVerified = $false
    }
}

Export-ModuleMember -Function Read-SCOMObjectIdPages, Read-SCOMReaderPages, Read-SCOMManagementGroupScope, Resolve-SCOMApprovedGroupMembership, Read-SCOMDirectRelationshipRows, ConvertTo-SCOMObservedObject, ConvertTo-SCOMObservedAlert, Read-SCOMDirectGroupMembers
