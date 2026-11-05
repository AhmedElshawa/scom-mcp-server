Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ScomSdkReader.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'ScomMetricProfile.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'ScomWindowsProjection.psm1') -ErrorAction Stop

function Get-SCOMGenericScan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object] $EntityObjects,
        [Parameter(Mandatory)] [Guid] $ManagementGroupId,
        [Guid] $GroupId = [Guid]::Empty,
        [object] $GroupClass = $null,
        [object] $MembershipRelationshipClass = $null,
        [Parameter(Mandatory)] [bool] $ScopeVisibilityVerified,
        [bool] $MembershipQueryContractVerified = $false,
        [ValidateSet('group','managementGroup')] [string] $ScopeKind = 'group',
        [bool] $WholeManagementGroupApproved = $false,
        [bool] $EnumerationQueryContractVerified = $false,
        [object] $EntityClass = $null,
        [Parameter(Mandatory)] [object] $Request,
        [Parameter(Mandatory)] [scriptblock] $ReadActiveAlerts,
        [Parameter(Mandatory)] [scriptblock] $ReadAlertById,
        [hashtable] $AllowedPropertiesByClass = @{},
        [hashtable] $MetricMappingsByClass = @{},
        [object[]] $AdditionalRelationshipClasses = @(),
        [bool] $AdditionalRelationshipContractVerified = $false,
        [ValidateRange(1, 100000)] [int] $MaxGraphQueries = 10000,
        [ValidateRange(1, 100000)] [int] $MaxSeriesSamples = 10000,
        [ValidateRange(1, 1000000)] [int] $MaxMetricSamples = 50000,
        [object] $WindowsMapping = $null,
        [object] $WindowsComputerClass = $null,
        [object] $WindowsOsClass = $null,
        [object] $WindowsVolumeClass = $null,
        [object] $WindowsAgentClass = $null,
        [object] $WindowsManagementServerClass = $null,
        [bool] $IncludeAlertDescriptions = $false,
        [ValidateRange(1, 10000)] [int] $MaxObjects = 1000,
        [ValidateRange(1, 1000)] [int] $MaxGroups = 100,
        [ValidateRange(1, 100000)] [int] $MaxAlerts = 10000,
        [ValidateRange(1, 100)] [int] $PageSize = 50,
        [ValidateRange(1, 7200)] [int] $DeadlineSeconds = 900
    )

    if ($ManagementGroupId -eq [Guid]::Empty -or -not $ScopeVisibilityVerified -or
        ($ScopeKind -eq 'group' -and (-not $MembershipQueryContractVerified -or $GroupId -eq [Guid]::Empty -or
            $null -eq $GroupClass -or $null -eq $MembershipRelationshipClass)) -or
        ($ScopeKind -eq 'managementGroup' -and (-not $WholeManagementGroupApproved -or
            -not $EnumerationQueryContractVerified -or $null -eq $EntityClass -or $GroupId -ne [Guid]::Empty))) {
        throw 'SCOM_PROFILE_SCOPE_UNVERIFIED'
    }
    if ($AdditionalRelationshipClasses.Count -gt 16 -or
        ($AdditionalRelationshipClasses.Count -gt 0 -and -not $AdditionalRelationshipContractVerified)) {
        throw 'SCOM_PROFILE_GRAPH_CONTRACT_UNVERIFIED'
    }
    $relationshipNames = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($class in $AdditionalRelationshipClasses) {
        if ($null -eq $class -or [string]::IsNullOrWhiteSpace([string]$class.Name) -or
            ([string]$class.Name).Length -gt 256 -or -not $relationshipNames.Add([string]$class.Name) -or
            ($ScopeKind -eq 'group' -and [string]$class.Name -ceq 'Microsoft.SystemCenter.InstanceGroupContainsEntities')) {
            throw 'SCOM_PROFILE_GRAPH_CLASS_INVALID'
        }
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $requested = [DateTimeOffset]::Parse([string]$Request.requestedAtUtc,
        [Globalization.CultureInfo]::InvariantCulture)
    $history = [DateTimeOffset]::Parse([string]$Request.metricHistoryStartUtc,
        [Globalization.CultureInfo]::InvariantCulture)
    if ($requested.Offset -ne [TimeSpan]::Zero -or $history -ne $requested.AddDays(-14) -or
        $requested -gt [DateTimeOffset]::UtcNow.AddMinutes(1)) { throw 'SCOM_PROFILE_REQUEST_INVALID' }
    $prior = @($Request.priorUnresolvedAlerts)
    if ($prior.Count -gt $MaxAlerts) { throw 'SCOM_PROFILE_PRIOR_ALERT_BUDGET' }
    $membershipEdges = New-Object 'System.Collections.Generic.Dictionary[System.Guid,object]'
    $expectedComputers = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    if ($null -ne $WindowsMapping -and ($null -eq $WindowsComputerClass -or $null -eq $WindowsOsClass -or
        $null -eq $WindowsVolumeClass -or -not $AdditionalRelationshipContractVerified)) {
        throw 'SCOM_WINDOWS_PROFILE_TYPES_OR_GRAPH_UNVERIFIED'
    }
    $direct = {
        param($id, $limit)
        $page = Read-SCOMDirectGroupMembers -EntityObjects $EntityObjects -GroupId $id `
            -MembershipRelationshipClass $MembershipRelationshipClass -GroupClass $GroupClass `
            -MembershipQueryContractVerified $MembershipQueryContractVerified -MaxMembers ($limit - 1)
        $membershipEdges.Add([Guid]$id, $page.Relationships)
        if ($null -ne $WindowsMapping) {
            foreach ($edge in $page.Relationships) {
                foreach ($object in @($edge.SourceObject,$edge.TargetObject)) {
                    if ($object.IsInstanceOf($WindowsComputerClass)) { $expectedComputers.Add([Guid]$object.Id) | Out-Null }
                }
            }
        }
        $page
    }
    if ($ScopeKind -eq 'group') {
        $scope = Resolve-SCOMApprovedGroupMembership -GroupId $GroupId -ReadDirectMembers $direct `
            -ScopeVisibilityVerified $ScopeVisibilityVerified -MaxObjects $MaxObjects -MaxGroups $MaxGroups `
            -DeadlineSeconds $DeadlineSeconds
    }
    else {
        $scope = Read-SCOMManagementGroupScope -EntityObjects $EntityObjects -EntityClass $EntityClass `
            -WholeManagementGroupApproved $WholeManagementGroupApproved -ScopeVisibilityVerified $ScopeVisibilityVerified `
            -EnumerationQueryContractVerified $EnumerationQueryContractVerified -MaxObjects $MaxObjects `
            -PageSize $PageSize -DeadlineSeconds $DeadlineSeconds
        if ($null -ne $WindowsMapping) {
            foreach ($object in $scope.MembershipObjects) {
                if ($object.IsInstanceOf($WindowsComputerClass)) { $expectedComputers.Add([Guid]$object.Id) | Out-Null }
            }
        }
    }
    $discovery=[pscustomobject]@{Objects=@();DiscoveryComplete=$true}
    if ($scope.ExpectedObjectIds.Count -gt 0) {
        $discovery = Read-SCOMObjectIdPages -EntityObjects $EntityObjects -ObjectIds $scope.ExpectedObjectIds `
            -MaxObjects $MaxObjects -PageSize $PageSize
    }
    $objects = New-Object 'System.Collections.Generic.List[object]'
    $expected = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    foreach ($id in $scope.ExpectedObjectIds) { $expected.Add($id) | Out-Null }
    $served = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    $alerts = New-Object 'System.Collections.Generic.Dictionary[System.Guid,object]'
    $metricSeries = New-Object 'System.Collections.Generic.List[object]'
    $metricSamples = 0
    $metricComplete = ($MetricMappingsByClass.Count -gt 0)
    $assessedMetricObjects = 0
    foreach ($object in $discovery.Objects) {
        if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'SCOM_PROFILE_DEADLINE' }
        $classes = @($object.GetMostDerivedClasses())
        if ($classes.Count -ne 1) { throw 'SCOM_NORMALIZATION_CLASS_AMBIGUOUS' }
        $allowed = @()
        if ($AllowedPropertiesByClass.ContainsKey([string]$classes[0].Name)) {
            $allowed = @($AllowedPropertiesByClass[[string]$classes[0].Name])
        }
        $at = [DateTimeOffset]::UtcNow
        $objects.Add((ConvertTo-SCOMObservedObject -Object $object -ObservedAtUtc $at -AllowedProperties $allowed))
        $served.Add([Guid]$object.Id) | Out-Null
        if ($MetricMappingsByClass.ContainsKey([string]$classes[0].Name)) {
            $metricResult = Read-SCOMSelectedMetricSeries -Object $object `
                -Mappings @($MetricMappingsByClass[[string]$classes[0].Name]) -StartUtc $history -EndUtc $requested `
                -MaxSeriesSamples $MaxSeriesSamples -MaxSamples ($MaxMetricSamples - $metricSamples)
            $assessedMetricObjects++
            if (-not $metricResult.Complete) { $metricComplete=$false }
            $metricSamples += $metricResult.SampleCount
            if ($metricSamples -gt $MaxMetricSamples) { throw 'SCOM_PROFILE_METRIC_BUDGET' }
            foreach ($series in $metricResult.Series) { $metricSeries.Add($series) }
        }
        # Callback reads active alerts for exactly this discovered object, never
        # an unscoped management-group alert enumeration.
        $rows = @(& $ReadActiveAlerts $object)
        if ($rows.Count -gt $MaxAlerts) { throw 'SCOM_PROFILE_ALERT_BUDGET' }
        foreach ($alert in $rows) {
            $normalized = ConvertTo-SCOMObservedAlert -Alert $alert -ExpectedObjectId ([Guid]$object.Id) `
                -ObservedAtUtc ([DateTimeOffset]::UtcNow) -IncludeDescription $IncludeAlertDescriptions
            if ($normalized.resolutionState -eq 255) { throw 'SCOM_PROFILE_ACTIVE_ALERT_QUERY_MISMATCH' }
            $key = [Guid]$normalized.id
            if ($alerts.ContainsKey($key)) { throw 'SCOM_PROFILE_ALERT_DUPLICATE' }
            if ($alerts.Count -ge $MaxAlerts) { throw 'SCOM_PROFILE_ALERT_BUDGET' }
            $alerts.Add($key, $normalized)
        }
    }
    $priorSeen = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    foreach ($hint in $prior) {
        if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'SCOM_PROFILE_DEADLINE' }
        $alertId = [Guid]$hint.alertId
        $parent = [Guid]$hint.objectId
        if ($alertId -eq [Guid]::Empty -or $parent -eq [Guid]::Empty -or -not $priorSeen.Add($alertId)) {
            throw 'SCOM_PROFILE_PRIOR_ALERT_INVALID'
        }
        # Hints are never scope authority. Newly removed or undiscovered parents
        # must not be queried, and missing results do not manufacture resolution.
        if (-not $served.Contains($parent) -or $alerts.ContainsKey($alertId)) { continue }
        $rows = @(& $ReadAlertById $alertId)
        if ($rows.Count -gt 1) { throw 'SCOM_PROFILE_ALERT_ID_QUERY_INVALID' }
        if ($rows.Count -eq 0) { continue }
        $normalized = ConvertTo-SCOMObservedAlert -Alert $rows[0] -ExpectedObjectId $parent `
            -ObservedAtUtc ([DateTimeOffset]::UtcNow) -IncludeDescription $IncludeAlertDescriptions
        if ([Guid]$normalized.id -ne $alertId) { throw 'SCOM_PROFILE_ALERT_ID_QUERY_INVALID' }
        if ($alerts.Count -ge $MaxAlerts) { throw 'SCOM_PROFILE_ALERT_BUDGET' }
        $alerts.Add($alertId, $normalized)
    }
    # Reuse the same membership observations instead of querying every group
    # twice or combining relationships from a later scope with the initial scope.
    $relationships = New-Object 'System.Collections.Generic.Dictionary[System.Guid,object]'
    $membershipGroups=@()
    if ($ScopeKind -eq 'group') { $membershipGroups=$scope.GroupIds }
    foreach ($group in $membershipGroups) {
        if (-not $served.Contains($group)) { continue }
        if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'SCOM_PROFILE_DEADLINE' }
        if (-not $membershipEdges.ContainsKey($group)) { throw 'SCOM_PROFILE_MEMBERSHIP_EDGES_UNAVAILABLE' }
        foreach ($row in $membershipEdges[$group]) {
            if (-not $expected.Contains([Guid]$row.TargetObject.Id)) { throw 'SCOM_PROFILE_MEMBERSHIP_CHANGED_DURING_SCAN' }
            if (-not $served.Contains([Guid]$row.TargetObject.Id)) { continue }
            $key = [Guid]$row.Id
            if ($relationships.ContainsKey($key)) { throw 'SCOM_PROFILE_RELATIONSHIP_DUPLICATE' }
            if ($relationships.Count -ge $MaxObjects) { throw 'SCOM_PROFILE_RELATIONSHIP_BUDGET' }
            $relationships.Add($key, [pscustomobject]@{
                id = $key.ToString('D')
                className = [string]$MembershipRelationshipClass.Name
                sourceId = ([Guid]$row.SourceObject.Id).ToString('D')
                targetId = ([Guid]$row.TargetObject.Id).ToString('D')
            })
        }
    }
    $outsideEdges = $false
    $graphQueries = 0
    foreach ($class in $AdditionalRelationshipClasses) {
        foreach ($source in $discovery.Objects) {
            if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'SCOM_PROFILE_DEADLINE' }
            if ($graphQueries -ge $MaxGraphQueries) { throw 'SCOM_PROFILE_GRAPH_QUERY_BUDGET' }
            $graphQueries++
            $edges = Read-SCOMDirectRelationshipRows -EntityObjects $EntityObjects -SourceId ([Guid]$source.Id) `
                -RelationshipClass $class -IncludeDerivedRelationships $false -MaxRelationships $MaxObjects
            foreach ($edge in $edges.Relationships) {
                if (-not $served.Contains([Guid]$edge.TargetObject.Id)) { $outsideEdges=$true; continue }
                $key = [Guid]$edge.Id
                if ($relationships.ContainsKey($key)) { throw 'SCOM_PROFILE_RELATIONSHIP_DUPLICATE' }
                if ($relationships.Count -ge $MaxObjects) { throw 'SCOM_PROFILE_RELATIONSHIP_BUDGET' }
                $relationships.Add($key, [pscustomobject]@{
                    id=$key.ToString('D'); className=[string]$class.Name
                    sourceId=([Guid]$edge.SourceObject.Id).ToString('D')
                    targetId=([Guid]$edge.TargetObject.Id).ToString('D')
                })
            }
        }
    }
    if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'SCOM_PROFILE_DEADLINE' }
    $asOf = [DateTimeOffset]::UtcNow.ToString('o')
    $inventoryReason = $null
    if (-not $discovery.DiscoveryComplete) { $inventoryReason = 'SCOM_DISCOVERY_INCOMPLETE' }
    if ($outsideEdges) { $inventoryReason='SCOM_GRAPH_ENDPOINTS_OUTSIDE_SERVED_SCOPE' }
    $alertReason = $null
    if (-not $discovery.DiscoveryComplete) { $alertReason = 'SCOM_ALERT_PARENTS_DISCOVERY_INCOMPLETE' }
    if (-not $discovery.DiscoveryComplete -or $assessedMetricObjects -eq 0) { $metricComplete=$false }
    $metricReason = $null
    if (-not $metricComplete) {
        $metricReason = 'SCOM_SELECTED_METRICS_INCOMPLETE_OR_EXCLUDED'
        if ($MetricMappingsByClass.Count -eq 0) { $metricReason='VERIFIED_METRIC_PROFILE_NOT_CONFIGURED' }
    }
    $windows = $null
    if ($null -ne $WindowsMapping) {
        $projection = ConvertTo-SCOMWindowsInventory -Objects $discovery.Objects -ObservedObjects $objects.ToArray() `
            -Relationships @($relationships.Values) -ExpectedComputerIds @($expectedComputers) `
            -ComputerClass $WindowsComputerClass -OperatingSystemClass $WindowsOsClass -VolumeClass $WindowsVolumeClass `
            -Mapping $WindowsMapping -AgentClass $WindowsAgentClass -ManagementServerClass $WindowsManagementServerClass
        $windows=$projection.Inventory
        if (-not $projection.Complete) { $inventoryReason='SCOM_WINDOWS_PROJECTION_INCOMPLETE' }
    }
    $result = [ordered]@{
        managementGroupId = $ManagementGroupId.ToString('D')
        scopeKind = $ScopeKind
        groupId = $(if ($ScopeKind -eq 'group') { $GroupId.ToString('D') } else { $null })
        membershipVerified = $true
        expectedObjectIds = @($scope.ExpectedObjectIds | ForEach-Object { $_.ToString('D') })
        objects = $objects.ToArray()
        relationships = @($relationships.Values | Sort-Object id)
        inventory = @{ asOfUtc=$asOf; complete=($null -eq $inventoryReason); reason=$inventoryReason }
        alertObservations = @($alerts.Values | Sort-Object id)
        alerts = @{ asOfUtc=$asOf; complete=[bool]$discovery.DiscoveryComplete; reason=$alertReason }
        metricSeries = $metricSeries.ToArray()
        metricsAsOfUtc = $requested.ToString('o')
        metricAssessment = @{ asOfUtc=$requested.ToString('o'); complete=$metricComplete; reason=$metricReason }
    }
    if ($null -ne $windows) {
        $result.windowsInventory=$windows
        $result.expectedComputerIds=@($expectedComputers | Sort-Object | ForEach-Object { $_.ToString('D') })
    }
    [pscustomobject]$result
}

Set-Alias -Name Get-SCOMGenericGroupScan -Value Get-SCOMGenericScan
Export-ModuleMember -Function Get-SCOMGenericScan -Alias Get-SCOMGenericGroupScan
