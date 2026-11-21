Set-StrictMode -Version Latest

# Reads one configured hosting relationship for the whole management group with a single bounded SDK call.
# The result is an observation, never evidence of completeness: completeness stays set reconciliation in
# ConvertTo-SCOMWindowsInventory. Class and relationship objects come from the reviewed windowsMapping.
function Read-SCOMRelationshipSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object]$EntityObjects,
        [Parameter(Mandatory)] [Guid]$ManagementGroupId,
        [Parameter(Mandatory)] [object]$RelationshipClass,
        [Parameter(Mandatory)] [object]$TargetClass,
        [Parameter(Mandatory)] [ValidateCount(1, 100000)] [Guid[]]$ServedSourceIds,
        [Parameter(Mandatory)] [ValidateCount(1, 100000)] [Guid[]]$ServedTargetIds
    )
    $managerType = 'Microsoft.EnterpriseManagement.IEntityObjectsManagement' -as [type]
    $objectType = 'Microsoft.EnterpriseManagement.Monitoring.MonitoringObject' -as [type]
    $relationshipType = 'Microsoft.EnterpriseManagement.Configuration.ManagementPackRelationship' -as [type]
    $classType = 'Microsoft.EnterpriseManagement.Configuration.ManagementPackClass' -as [type]
    $derivedType = 'Microsoft.EnterpriseManagement.Configuration.DerivedClassTraversalDepth' -as [type]
    $optionsType = 'Microsoft.EnterpriseManagement.Common.ObjectQueryOptions' -as [type]
    $edgeType = 'Microsoft.EnterpriseManagement.Common.EnterpriseManagementRelationshipObject`1' -as [type]
    if (-not $managerType -or -not $objectType -or -not $relationshipType -or -not $classType -or
        -not $derivedType -or -not $optionsType -or -not $edgeType -or
        -not $managerType.IsInstanceOfType($EntityObjects) -or
        -not $relationshipType.IsInstanceOfType($RelationshipClass) -or
        -not $classType.IsInstanceOfType($TargetClass)) {
        throw 'SCOM_SET_READ_UNAVAILABLE'
    }
    if ($ManagementGroupId -eq [Guid]::Empty -or [Guid]$RelationshipClass.Id -eq [Guid]::Empty -or
        [Guid]$RelationshipClass.ManagementGroupId -ne $ManagementGroupId -or
        [Guid]$TargetClass.ManagementGroupId -ne $ManagementGroupId) {
        throw 'SCOM_SET_READ_UNAVAILABLE'
    }
    $sources = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    $targets = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    foreach ($id in $ServedSourceIds) {
        if ($id -eq [Guid]::Empty -or -not $sources.Add($id)) { throw 'SCOM_SET_READ_UNAVAILABLE' }
    }
    foreach ($id in $ServedTargetIds) {
        if ($id -eq [Guid]::Empty -or -not $targets.Add($id)) { throw 'SCOM_SET_READ_UNAVAILABLE' }
    }
    $methods = @($managerType.GetMethods())
    foreach ($interface in $managerType.GetInterfaces()) { $methods += @($interface.GetMethods()) }
    $candidate = @($methods | Where-Object {
        if ($_.Name -cne 'GetRelationshipObjects' -or -not $_.IsGenericMethodDefinition) { return $false }
        $p = $_.GetParameters()
        return $_.GetGenericArguments().Count -eq 1 -and $p.Count -eq 3 -and
            $p[0].ParameterType -eq $relationshipType -and $p[1].ParameterType -eq $derivedType -and
            $p[2].ParameterType -eq $optionsType
    } | Sort-Object { $_.ToString() } -Unique)
    if ($candidate.Count -ne 1) { throw 'SCOM_SET_READ_UNAVAILABLE' }
    $method = $candidate[0].MakeGenericMethod([type[]]@($objectType))
    $returnType = [System.Collections.Generic.IList``1].MakeGenericType(
        [type[]]@($edgeType.MakeGenericType([type[]]@($objectType))))
    if ($method.ReturnType -ne $returnType) { throw 'SCOM_SET_READ_UNAVAILABLE' }
    # The cap is one above the targets this scan serves. Reaching it means the call returned at least as many
    # rows as expected targets plus one, so the result is rejected rather than trusted or truncated.
    $cap = $targets.Count + 1
    $options = [Activator]::CreateInstance($optionsType)
    try {
        $options.MaxResultCount = $cap
        if ($options.MaxResultCount -ne $cap) { throw 'Cap was not retained' }
    }
    catch { throw 'SCOM_SET_READ_UNAVAILABLE' }
    try {
        $result = $method.Invoke($EntityObjects.PSObject.BaseObject, [object[]]@(
            $RelationshipClass.PSObject.BaseObject, [Enum]::Parse($derivedType, 'None'), $options.PSObject.BaseObject))
    }
    catch { throw 'SCOM_SET_READ_UNAVAILABLE' }
    if ($null -eq $result) { throw 'SCOM_SET_READ_UNAVAILABLE' }
    if ($result.Count -ge $cap) { throw 'SCOM_SET_READ_CAP_HIT' }
    # Optional row members differ between SDK builds. Probe once and validate only the ones that exist.
    $present = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    if ($result.Count -gt 0) {
        foreach ($member in @(Get-Member -InputObject $result[0] -MemberType Properties)) { $present.Add([string]$member.Name) | Out-Null }
    }
    $ids = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    $rows = New-Object 'System.Collections.Generic.List[object]'
    $skippedNonTarget = 0
    $skippedOutsideSources = 0
    $outsideTargets = 0
    foreach ($row in $result) {
        if ($null -eq $row -or -not $present.Contains('SourceObject') -or -not $present.Contains('TargetObject') -or
            -not $present.Contains('Id') -or $null -eq $row.SourceObject -or $null -eq $row.TargetObject) {
            throw 'SCOM_RELATIONSHIP_ROW_INVALID'
        }
        # Hosting relationships also reach devices the projection never serves; count them, never abort.
        if (-not $row.TargetObject.IsInstanceOf($TargetClass)) { $skippedNonTarget++; continue }
        $id = [Guid]::Empty
        $source = [Guid]::Empty
        $target = [Guid]::Empty
        if (-not [Guid]::TryParse([string]$row.Id, [ref]$id) -or $id -eq [Guid]::Empty -or -not $ids.Add($id) -or
            -not [Guid]::TryParse([string]$row.SourceObject.Id, [ref]$source) -or $source -eq [Guid]::Empty -or
            -not [Guid]::TryParse([string]$row.TargetObject.Id, [ref]$target) -or $target -eq [Guid]::Empty) {
            throw 'SCOM_RELATIONSHIP_ROW_INVALID'
        }
        if ($present.Contains('IsDeleted') -and ($row.IsDeleted -isnot [bool] -or $row.IsDeleted)) {
            throw 'SCOM_RELATIONSHIP_ROW_INVALID'
        }
        if ($present.Contains('RelationshipId')) {
            $relationshipId = [Guid]::Empty
            if (-not [Guid]::TryParse([string]$row.RelationshipId, [ref]$relationshipId) -or
                $relationshipId -ne [Guid]$RelationshipClass.Id) { throw 'SCOM_RELATIONSHIP_ROW_INVALID' }
        }
        if ($present.Contains('ManagementGroupId')) {
            $groupId = [Guid]::Empty
            if (-not [Guid]::TryParse([string]$row.ManagementGroupId, [ref]$groupId) -or
                $groupId -ne $ManagementGroupId) { throw 'SCOM_RELATIONSHIP_ROW_INVALID' }
        }
        if (-not $sources.Contains($source)) { $skippedOutsideSources++; continue }
        if (-not $targets.Contains($target)) { $outsideTargets++; continue }
        $rows.Add($row)
    }
    [pscustomobject]@{
        Relationships = $rows.ToArray()
        SkippedNonTarget = $skippedNonTarget
        SkippedOutsideScope = $skippedOutsideSources
        OutsideScopeTargets = $outsideTargets
        CapHit = $false
        Complete = $false
        ScopeMembershipVerified = $false
        SourceWorkBoundVerified = $false
    }
}

Export-ModuleMember -Function Read-SCOMRelationshipSet
