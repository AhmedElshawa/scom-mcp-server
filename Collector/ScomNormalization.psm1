Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ScomSdkReader.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'ScomMetricProfile.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'ScomWindowsProjection.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'ScomSetRelationshipReader.psm1') -ErrorAction Stop

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
        [ValidateSet('generic','supportedWindows')] [string] $InventoryKind = 'generic',
        [bool] $WholeManagementGroupApproved = $false,
        [bool] $EnumerationQueryContractVerified = $false,
        [object] $EntityClass = $null,
        [Parameter(Mandatory)] [object] $Request,
        # Called once, with no arguments, and returns every active (not closed) alert in the management group.
        # Alerts are then grouped by parent and kept only for discovered objects.
        [Parameter(Mandatory)] [scriptblock] $ReadActiveAlerts,
        [Parameter(Mandatory)] [scriptblock] $ReadAlertById,
        [string] $RelationshipReadMode = 'perSource',
        # Optional collector-side counters, filled in for the caller. They are never part of the scan object
        # because the .NET scan contract rejects unknown members.
        [hashtable] $GraphCounters = $null,
        [hashtable] $AllowedPropertiesByClass = @{},
        [hashtable] $MetricMappingsByClass = @{},
        [object[]] $AdditionalRelationshipClasses = @(),
        [bool] $AdditionalRelationshipContractVerified = $false,
        [ValidateRange(1, 100000)] [int] $MaxGraphQueries = 10000,
        [ValidateRange(1, 100000)] [int] $MaxSeriesSamples = 10000,
        [ValidateRange(1, 1000000)] [int] $MaxMetricSamples = 50000,
        [object] $WindowsMapping = $null,
        [switch] $HostingLabPreview,
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

    if ($HostingLabPreview -and ($ScopeKind -ne 'managementGroup' -or $InventoryKind -ne 'supportedWindows' -or
        $null -eq $WindowsMapping -or $AdditionalRelationshipClasses.Count -ne 2 -or
        $AdditionalRelationshipContractVerified -or $MetricMappingsByClass.Count -ne 0 -or $IncludeAlertDescriptions)) {
        throw 'SCOM_HOSTING_LAB_CONFIGURATION_INVALID'
    }
    if ($RelationshipReadMode -cnotin @('perSource','set') -or
        ($RelationshipReadMode -ceq 'set' -and ($HostingLabPreview -or $InventoryKind -ne 'supportedWindows' -or
            $null -eq $WindowsMapping -or $AdditionalRelationshipClasses.Count -eq 0))) {
        throw 'SCOM_SET_READ_MODE_INVALID'
    }
    if ($ManagementGroupId -eq [Guid]::Empty -or -not $ScopeVisibilityVerified -or
        ($ScopeKind -eq 'group' -and (-not $MembershipQueryContractVerified -or $GroupId -eq [Guid]::Empty -or
            $null -eq $GroupClass -or $null -eq $MembershipRelationshipClass)) -or
        ($ScopeKind -eq 'managementGroup' -and (-not $WholeManagementGroupApproved -or
            -not $EnumerationQueryContractVerified -or
            ($InventoryKind -eq 'generic' -and $null -eq $EntityClass) -or $GroupId -ne [Guid]::Empty))) {
        throw 'SCOM_PROFILE_SCOPE_UNVERIFIED'
    }
    if ($InventoryKind -eq 'supportedWindows') {
        if ($null -eq $WindowsMapping) { throw 'SCOM_WINDOWS_MAPPING_UNVERIFIED' }
        # Validate the reviewed projection before touching any source reader, including empty scopes.
        ConvertTo-SCOMWindowsInventory -Objects @() -ObservedObjects @() -Relationships @() `
            -ExpectedComputerIds @() -ComputerClass $WindowsComputerClass -OperatingSystemClass $WindowsOsClass `
            -VolumeClass $WindowsVolumeClass -Mapping $WindowsMapping -AgentClass $WindowsAgentClass -OfflineHostingPreview:$HostingLabPreview `
            -ManagementServerClass $WindowsManagementServerClass | Out-Null
        foreach ($pair in @(
            @($WindowsComputerClass,'computerClassName'), @($WindowsOsClass,'operatingSystemClassName'),
            @($WindowsVolumeClass,'volumeClassName'))) {
            if ([string]$pair[0].Name -cne [string]$WindowsMapping.($pair[1]) -or
                [string]$pair[0].Name -ceq 'System.Entity') { throw 'SCOM_WINDOWS_CLASS_MAPPING_INVALID' }
        }
    }
    if ($AdditionalRelationshipClasses.Count -gt 16 -or
        ($AdditionalRelationshipClasses.Count -gt 0 -and -not ($AdditionalRelationshipContractVerified -or $HostingLabPreview))) {
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
        $null -eq $WindowsVolumeClass -or -not ($AdditionalRelationshipContractVerified -or $HostingLabPreview))) {
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
    elseif ($InventoryKind -eq 'supportedWindows') {
        $classes=@($WindowsComputerClass,$WindowsOsClass,$WindowsVolumeClass)
        if ($null -ne $WindowsAgentClass -or $null -ne $WindowsManagementServerClass) {
            $classes+=@($WindowsAgentClass,$WindowsManagementServerClass)
        }
        $scope=Read-SCOMWindowsManagementGroupScope -EntityObjects $EntityObjects -Classes $classes `
            -Mapping $WindowsMapping -WholeManagementGroupApproved $WholeManagementGroupApproved `
            -ScopeVisibilityVerified $ScopeVisibilityVerified -EnumerationQueryContractVerified $EnumerationQueryContractVerified `
            -MaxObjects $MaxObjects -PageSize $PageSize -DeadlineSeconds $DeadlineSeconds
        foreach ($object in $scope.MembershipObjects) {
            if ($object.IsInstanceOf($WindowsComputerClass)) { $expectedComputers.Add([Guid]$object.Id) | Out-Null }
        }
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
        $remaining=$DeadlineSeconds-$watch.Elapsed.TotalSeconds
        if ($remaining -le 0) { throw 'SCOM_PROFILE_DEADLINE' }
        $discovery = Read-SCOMObjectIdPages -EntityObjects $EntityObjects -ObjectIds $scope.ExpectedObjectIds `
            -MaxObjects $MaxObjects -PageSize $PageSize `
            -DeadlineSeconds ([Math]::Max(1,[int][Math]::Floor($remaining)))
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
    $exactPropertyPolicy = New-SCOMExactPropertyPolicy $AllowedPropertiesByClass
    foreach ($object in $discovery.Objects) {
        if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'SCOM_PROFILE_DEADLINE' }
        $classes = @($object.GetMostDerivedClasses())
        if ($classes.Count -ne 1) { throw 'SCOM_NORMALIZATION_CLASS_AMBIGUOUS' }
        $allowed = @()
        if ($exactPropertyPolicy.ContainsKey([string]$classes[0].Name)) {
            $allowed = @($exactPropertyPolicy[[string]$classes[0].Name])
        }
        $at = [DateTimeOffset]::UtcNow
        $objects.Add((ConvertTo-SCOMObservedObject -Object $object -ObservedAtUtc $at -AllowedProperties $allowed))
        $served.Add([Guid]$object.Id) | Out-Null
        if ($HostingLabPreview) { continue }
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
    }
    if (-not $HostingLabPreview) {
        if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'SCOM_PROFILE_DEADLINE' }
        # One bulk active-alert read serves both relationship read modes. Alerts whose parent is not a
        # discovered object are outside the approved scope and are dropped, never normalized.
        foreach ($alert in @(& $ReadActiveAlerts)) {
            $parent = [Guid]::Empty
            if ($null -eq $alert -or -not [Guid]::TryParse([string]$alert.MonitoringObjectId, [ref]$parent) -or
                $parent -eq [Guid]::Empty) { throw 'SCOM_ALERT_ID_OR_PARENT_INVALID' }
            if (-not $served.Contains($parent)) { continue }
            $normalized = ConvertTo-SCOMObservedAlert -Alert $alert -ExpectedObjectId $parent `
                -ObservedAtUtc ([DateTimeOffset]::UtcNow) -IncludeDescription $IncludeAlertDescriptions
            if ($normalized.resolutionState -eq 255) { throw 'SCOM_PROFILE_ACTIVE_ALERT_QUERY_MISMATCH' }
            $key = [Guid]$normalized.id
            if ($alerts.ContainsKey($key)) { throw 'SCOM_PROFILE_ALERT_DUPLICATE' }
            if ($alerts.Count -ge $MaxAlerts) { throw 'SCOM_PROFILE_ALERT_BUDGET' }
            $alerts.Add($key, $normalized)
        }
    }
    $priorSeen = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    if ($HostingLabPreview -and $prior.Count -ne 0) { throw 'SCOM_HOSTING_LAB_CHECKPOINT_MUST_BE_NEW' }
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
    $setFallback = $false
    $setFallbackReason = ''
    $skippedNonVolume = 0
    $skippedNonOs = 0
    $skippedOutsideSources = 0
    $perSourceClasses = @($AdditionalRelationshipClasses)
    if ($RelationshipReadMode -ceq 'set') {
        # Only the reviewed OS and volume hosting relationships are read as sets. Names and classes come from
        # windowsMapping; any other configured relationship keeps the per-source reader.
        $setSpecs = New-Object 'System.Collections.Generic.List[object]'
        $perSourceClasses = @()
        foreach ($class in $AdditionalRelationshipClasses) {
            $name = [string]$class.Name
            $spec = $null
            if ($name -ceq [string]$WindowsMapping.computerHostsOsRelationship) {
                $spec = @{Source=$WindowsComputerClass; Target=$WindowsOsClass; Kind='os'}
            }
            elseif ($null -ne $WindowsMapping.PSObject.Properties['computerHostsVolumeRelationship'] -and
                $name -ceq [string]$WindowsMapping.computerHostsVolumeRelationship) {
                $spec = @{Source=$WindowsComputerClass; Target=$WindowsVolumeClass; Kind='volume'}
            }
            elseif ($null -ne $WindowsMapping.PSObject.Properties['osHostsVolumeRelationship'] -and
                $name -ceq [string]$WindowsMapping.osHostsVolumeRelationship) {
                $spec = @{Source=$WindowsOsClass; Target=$WindowsVolumeClass; Kind='volume'}
            }
            if ($null -eq $spec) { $perSourceClasses += $class; continue }
            $spec.Class = $class
            $setSpecs.Add($spec)
        }
        $setResults = New-Object 'System.Collections.Generic.List[object]'
        try {
            # Budget check for every set before the first SCOM call, so an over-budget scope makes no call.
            foreach ($spec in $setSpecs) {
                $spec.SourceIds = @($discovery.Objects | Where-Object { $_.IsInstanceOf($spec.Source) } |
                    ForEach-Object { [Guid]$_.Id })
                $spec.TargetIds = @($discovery.Objects | Where-Object { $_.IsInstanceOf($spec.Target) } |
                    ForEach-Object { [Guid]$_.Id })
                if ($spec.TargetIds.Count -gt $MaxObjects) { throw 'SCOM_SET_READ_BUDGET' }
            }
            foreach ($spec in $setSpecs) {
                $sourceIds = $spec.SourceIds
                $targetIds = $spec.TargetIds
                # With nothing served on either side no edge can be reported, so no call is made.
                if ($sourceIds.Count -eq 0 -or $targetIds.Count -eq 0) { continue }
                if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'SCOM_PROFILE_DEADLINE' }
                if ($graphQueries -ge $MaxGraphQueries) { throw 'SCOM_PROFILE_GRAPH_QUERY_BUDGET' }
                $graphQueries++
                $read = Read-SCOMRelationshipSet -EntityObjects $EntityObjects -ManagementGroupId $ManagementGroupId `
                    -RelationshipClass $spec.Class -TargetClass $spec.Target -ServedSourceIds $sourceIds `
                    -ServedTargetIds $targetIds -MaxObjects $MaxObjects
                $setResults.Add([pscustomobject]@{Spec=$spec; Read=$read})
            }
        }
        catch {
            # Budget and every other fault fail closed. An unsupported, failing or oversized set call is not a
            # data fault: discard partial results and read this run per source.
            if ($_.Exception.Message -cnotin @('SCOM_SET_READ_UNAVAILABLE', 'SCOM_SET_READ_OVERSIZE')) { throw }
            $setFallbackReason = [string]$_.Exception.Message
            $setFallback = $true
            $setResults.Clear()
            $graphQueries = 0
            $perSourceClasses = @($AdditionalRelationshipClasses)
        }
        foreach ($entry in $setResults) {
            $read = $entry.Read
            if ($entry.Spec.Kind -ceq 'volume') { $skippedNonVolume += $read.SkippedNonTarget }
            else { $skippedNonOs += $read.SkippedNonTarget }
            $skippedOutsideSources += $read.SkippedOutsideScope
            if ($read.OutsideScopeTargets -gt 0) { $outsideEdges = $true }
            foreach ($edge in $read.Relationships) {
                $key = [Guid]$edge.Id
                if ($relationships.ContainsKey($key)) { throw 'SCOM_PROFILE_RELATIONSHIP_DUPLICATE' }
                if ($relationships.Count -ge $MaxObjects) { throw 'SCOM_PROFILE_RELATIONSHIP_BUDGET' }
                $relationships.Add($key, [pscustomobject]@{
                    id=$key.ToString('D'); className=[string]$entry.Spec.Class.Name
                    sourceId=([Guid]$edge.SourceObject.Id).ToString('D')
                    targetId=([Guid]$edge.TargetObject.Id).ToString('D')
                })
            }
        }
    }
    foreach ($class in $perSourceClasses) {
        foreach ($source in $discovery.Objects) {
            if ($HostingLabPreview) {
                $sourceClass = $WindowsComputerClass
                if ($null -ne $WindowsMapping.PSObject.Properties['osHostsVolumeRelationship'] -and
                    [string]$class.Name -ceq $WindowsMapping.osHostsVolumeRelationship) {
                    $sourceClass = $WindowsOsClass
                }
                if (-not $source.IsInstanceOf($sourceClass)) { continue }
            }
            if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'SCOM_PROFILE_DEADLINE' }
            if ($graphQueries -ge $MaxGraphQueries) { throw 'SCOM_PROFILE_GRAPH_QUERY_BUDGET' }
            $graphQueries++
            $edges = Read-SCOMDirectRelationshipRows -EntityObjects $EntityObjects -SourceId ([Guid]$source.Id) `
                -RelationshipClass $class -IncludeDerivedRelationships $false -MaxRelationships $MaxObjects
            $targetClass = $null
            if ($null -ne $WindowsMapping) {
                if ($null -ne $WindowsMapping.PSObject.Properties['computerHostsVolumeRelationship'] -and
                    [string]$class.Name -ceq $WindowsMapping.computerHostsVolumeRelationship) {
                    $targetClass = $WindowsVolumeClass
                }
                elseif ([string]$class.Name -ceq $WindowsMapping.computerHostsOsRelationship) {
                    $targetClass = $WindowsOsClass
                }
            }
            foreach ($edge in $edges.Relationships) {
                # The mapped hosting relationship also targets non-volume/non-OS devices that the Windows
                # projection never serves; they are not outside-scope inventory.
                if ($null -ne $targetClass -and -not $edge.TargetObject.IsInstanceOf($targetClass)) { continue }
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
    if ($HostingLabPreview) { $alertReason='SCOM_HOSTING_LAB_ALERTS_NOT_COLLECTED' }
    if (-not $discovery.DiscoveryComplete -or $assessedMetricObjects -eq 0) { $metricComplete=$false }
    $metricReason = $null
    if (-not $metricComplete) {
        $metricReason = 'SCOM_SELECTED_METRICS_INCOMPLETE_OR_EXCLUDED'
        if ($MetricMappingsByClass.Count -eq 0) { $metricReason='VERIFIED_METRIC_PROFILE_NOT_CONFIGURED' }
    }
    $windows = $null
    if ($null -ne $WindowsMapping) {
        # Completeness is decided only by set reconciliation in ConvertTo-SCOMWindowsInventory. The number of
        # relationship rows a set read returned is never evidence that the inventory is complete.
        $projection = ConvertTo-SCOMWindowsInventory -Objects $discovery.Objects -ObservedObjects $objects.ToArray() `
            -Relationships @($relationships.Values) -ExpectedComputerIds @($expectedComputers) `
            -ComputerClass $WindowsComputerClass -OperatingSystemClass $WindowsOsClass -VolumeClass $WindowsVolumeClass `
            -Mapping $WindowsMapping -AllowedPropertiesByClass $AllowedPropertiesByClass `
            -AgentClass $WindowsAgentClass -ManagementServerClass $WindowsManagementServerClass -OfflineHostingPreview:$HostingLabPreview
        $windows=$projection.Inventory
        if (-not $projection.Complete) { $inventoryReason='SCOM_WINDOWS_PROJECTION_INCOMPLETE' }
        if ($HostingLabPreview) { $inventoryReason=$projection.Reason }
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
        alerts = @{ asOfUtc=$asOf; complete=($null -eq $alertReason); reason=$alertReason }
        metricSeries = $metricSeries.ToArray()
        metricsAsOfUtc = $requested.ToString('o')
        metricAssessment = @{ asOfUtc=$requested.ToString('o'); complete=$metricComplete; reason=$metricReason }
    }
    if ($null -ne $windows) {
        $result.windowsInventory=$windows
        $result.expectedComputerIds=@($expectedComputers | Sort-Object | ForEach-Object { $_.ToString('D') })
    }
    if ($InventoryKind -eq 'supportedWindows') { $result.inventoryKind=$InventoryKind }
    if ($null -ne $GraphCounters) {
        $GraphCounters['readMode'] = $(if ($RelationshipReadMode -ceq 'set' -and -not $setFallback) { 'set' } else { 'perSource' })
        $GraphCounters['graphQueries'] = $graphQueries
        $GraphCounters['setReadFallback'] = $setFallback
        $GraphCounters['setReadOversize'] = ($setFallbackReason -ceq 'SCOM_SET_READ_OVERSIZE')
        $GraphCounters['skippedNonVolumeTargets'] = $skippedNonVolume
        $GraphCounters['skippedNonOsTargets'] = $skippedNonOs
        $GraphCounters['skippedOutsideScopeSources'] = $skippedOutsideSources
    }
    [pscustomobject]$result
}

Set-Alias -Name Get-SCOMGenericGroupScan -Value Get-SCOMGenericScan
Export-ModuleMember -Function Get-SCOMGenericScan -Alias Get-SCOMGenericGroupScan
