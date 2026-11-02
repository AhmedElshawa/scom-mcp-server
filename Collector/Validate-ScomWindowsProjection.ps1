param([ValidateSet('','complete','direct','partial','managementServer','unknownRole')] [string]$BridgeMode='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ScomWindowsProjection.psm1') -Force
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
public class ProjectionClass {
    public string Name;
    public ProjectionClass(string name) { Name=name; }
    public IList<ProjectionClass> GetBaseTypes() { return new ProjectionClass[0]; }
}
public class ProjectionProperty { public string Name; }
public class ProjectionValue { public object Value; }
public class ProjectionObject {
    public Guid Id=Guid.NewGuid();
    public string DisplayName="Synthetic";
    public string HealthState="Success";
    public string Kind;
    public Dictionary<string,object> Values=new Dictionary<string,object>();
    public bool IsInstanceOf(ProjectionClass c) { return Kind==c.Name; }
    public IList<ProjectionClass> GetMostDerivedClasses() { return new [] {new ProjectionClass(Kind)}; }
    public IList<ProjectionProperty> GetProperties() {
        var p=new List<ProjectionProperty>();
        foreach (var name in Values.Keys) p.Add(new ProjectionProperty {Name=name});
        return p;
    }
    public ProjectionValue this[ProjectionProperty p] { get {return new ProjectionValue {Value=Values[p.Name]};} }
}
'@
$computer=New-Object ProjectionObject; $computer.Kind='Synthetic.Computer'
$os=New-Object ProjectionObject; $os.Kind='Synthetic.Os'
$volume=New-Object ProjectionObject; $volume.Kind='Synthetic.Volume'
$root=New-Object ProjectionObject; $root.Kind='Synthetic.Group'
$computer.Values['DnsName']='synthetic.invalid'
$computer.Values['Cpu']=4
$os.Values['Version']='Synthetic version'
$os.Values['Memory']=8388608
$volume.Values['Name']='C:'
$volume.Values['FileSystem']='NTFS'
$volume.Values['Size']=10737418240
$mapping=[pscustomobject]@{
    verified=$true; agentMonitoringVerified=$true
    hostnameProperty='DnsName'; osVersionProperty='Version'; memoryProperty='Memory'
    processorCountProperty='Cpu'; volumeNameProperty='Name'; fileSystemProperty='FileSystem'; volumeSizeProperty='Size'
    memoryToMiB=(1.0/1024); volumeSizeToMiB=(1.0/1048576)
    computerHostsOsRelationship='Synthetic.ComputerHostsOs'; osHostsVolumeRelationship='Synthetic.OsHostsVolume'
}
$edges=@(
    [pscustomobject]@{id=[Guid]::NewGuid().ToString('D');className=$mapping.computerHostsOsRelationship;sourceId=$computer.Id;targetId=$os.Id}
    [pscustomobject]@{id=[Guid]::NewGuid().ToString('D');className=$mapping.osHostsVolumeRelationship;sourceId=$os.Id;targetId=$volume.Id}
)
$projectionArgs=@{
    Objects=@($computer,$os,$volume); ObservedObjects=@([pscustomobject]@{id=$computer.Id;health='healthy'})
    Relationships=$edges; ExpectedComputerIds=@($computer.Id); Mapping=$mapping
    ComputerClass=(New-Object ProjectionClass 'Synthetic.Computer')
    OperatingSystemClass=(New-Object ProjectionClass 'Synthetic.Os')
    VolumeClass=(New-Object ProjectionClass 'Synthetic.Volume')
}
function Assert-ProjectionFault([scriptblock]$Action,[string]$Code) {
    try { & $Action | Out-Null; throw "Expected $Code" }
    catch { if ($_.Exception.Message -ne $Code) { throw } }
}
$result=ConvertTo-SCOMWindowsInventory @projectionArgs
if (-not $result.Complete -or $result.Inventory.devices.Count -ne 1 -or $result.Inventory.volumes.Count -ne 1 -or
    $result.Inventory.devices[0].memoryTotalMiB -ne 8192 -or $result.Inventory.volumes[0].sizeMiB -ne 10240 -or
    $result.Inventory.volumes[0].parentEntityId -ne $computer.Id.ToString('D')) { throw 'Typed projection or unit conversion failed.' }
$os.Values['Memory']=$null
$result=ConvertTo-SCOMWindowsInventory @projectionArgs
if ($null -ne $result.Inventory.devices[0].memoryTotalMiB) { throw 'Missing memory became zero.' }
$os.Values['Memory']=0
Assert-ProjectionFault { ConvertTo-SCOMWindowsInventory @projectionArgs } 'SCOM_WINDOWS_CAPACITY_INVALID'
$os.Values['Memory']=8388608
$computer.Values['Cpu']=1.5
Assert-ProjectionFault { ConvertTo-SCOMWindowsInventory @projectionArgs } 'SCOM_WINDOWS_PROCESSOR_COUNT_INVALID'
$computer.Values['Cpu']=4
$mapping.agentMonitoringVerified=$false
Assert-ProjectionFault { ConvertTo-SCOMWindowsInventory @projectionArgs } 'SCOM_WINDOWS_MAPPING_UNVERIFIED'
$mapping.agentMonitoringVerified=$true
$mapping.memoryToMiB=0
Assert-ProjectionFault { ConvertTo-SCOMWindowsInventory @projectionArgs } 'SCOM_WINDOWS_MAPPING_INVALID'
$mapping.memoryToMiB=1.0/1024
$projectionArgs.Relationships=@()
$result=ConvertTo-SCOMWindowsInventory @projectionArgs
if ($result.Complete -or $result.Inventory.volumes.Count -ne 0 -or $null -ne $result.Inventory.devices[0].osVersion) {
    throw 'Absent typed hosting must not infer volume parent or OS from names.'
}
$projectionArgs.Relationships=@($edges[0],$edges[0],$edges[1])
Assert-ProjectionFault { ConvertTo-SCOMWindowsInventory @projectionArgs } 'SCOM_WINDOWS_OS_PARENT_AMBIGUOUS'
$projectionArgs.Relationships=$edges
$projectionArgs.ExpectedComputerIds=@()
Assert-ProjectionFault { ConvertTo-SCOMWindowsInventory @projectionArgs } 'SCOM_WINDOWS_COMPUTER_OUTSIDE_EXPECTED_SCOPE'
$projectionArgs.ExpectedComputerIds=@($computer.Id,[Guid]::NewGuid())
$result=ConvertTo-SCOMWindowsInventory @projectionArgs
if ($result.Complete) { throw 'Undiscovered expected member became complete.' }
$projectionArgs.ExpectedComputerIds=@($computer.Id)
$mapping.PSObject.Properties.Remove('osHostsVolumeRelationship')
$mapping | Add-Member -NotePropertyName computerHostsVolumeRelationship -NotePropertyValue 'Synthetic.ComputerHostsDevice'
$directEdges=@($edges[0],[pscustomobject]@{
    id=[Guid]::NewGuid().ToString('D');className=$mapping.computerHostsVolumeRelationship
    sourceId=$computer.Id;targetId=$volume.Id
})
$projectionArgs.Relationships=$directEdges
$direct=ConvertTo-SCOMWindowsInventory @projectionArgs
if (-not $direct.Complete -or $direct.Inventory.volumes[0].parentEntityId -ne $computer.Id.ToString('D')) {
    throw 'Direct computer volume hosting did not preserve typed parent.'
}
$projectionArgs.Relationships=@($directEdges[1])
$direct=ConvertTo-SCOMWindowsInventory @projectionArgs
if ($direct.Complete -or $direct.Inventory.volumes.Count -ne 1) {
    throw 'Missing OS must remain partial without hiding a directly hosted volume.'
}
$projectionArgs.Relationships=@($directEdges)+@([pscustomobject]@{
    id=[Guid]::NewGuid().ToString('D');className=$mapping.computerHostsVolumeRelationship
    sourceId=$computer.Id;targetId=$root.Id
})
$projectionArgs.Objects=@($computer,$os,$volume,$root)
$direct=ConvertTo-SCOMWindowsInventory @projectionArgs
if ($direct.Inventory.volumes.Count -ne 1) { throw 'Non-volume logical device misclassified as disk.' }
$projectionArgs.Objects=@($computer,$os,$volume)
$projectionArgs.Relationships=@($directEdges[0],$directEdges[1],$directEdges[1])
Assert-ProjectionFault { ConvertTo-SCOMWindowsInventory @projectionArgs } 'SCOM_WINDOWS_VOLUME_PARENT_AMBIGUOUS'
$mapping | Add-Member -NotePropertyName osHostsVolumeRelationship -NotePropertyValue 'Synthetic.OsHostsVolume'
Assert-ProjectionFault { ConvertTo-SCOMWindowsInventory @projectionArgs } 'SCOM_WINDOWS_VOLUME_MAPPING_AMBIGUOUS'
$mapping.PSObject.Properties.Remove('computerHostsVolumeRelationship')
$projectionArgs.Relationships=$edges
$agentClass=New-Object ProjectionClass 'Synthetic.Agent'
$serverClass=New-Object ProjectionClass 'Synthetic.ManagementServer'
$roleObject=New-Object ProjectionObject; $roleObject.Kind='Synthetic.Agent'
$roleMapping=[pscustomobject]@{
    verified=$true;agentClassName=$agentClass.Name;managementServerClassName=$serverClass.Name
    computerHostsApplicationRelationship='Synthetic.ComputerHostsApplication'
}
$roleEdge=[pscustomobject]@{
    id=[Guid]::NewGuid().ToString('D');className=$roleMapping.computerHostsApplicationRelationship
    sourceId=$computer.Id;targetId=$roleObject.Id
}
$mapping | Add-Member -NotePropertyName monitoringRoleMapping -NotePropertyValue $roleMapping
$mapping.agentMonitoringVerified=$false
$projectionArgs.AgentClass=$agentClass; $projectionArgs.ManagementServerClass=$serverClass
$projectionArgs.Objects=@($computer,$os,$volume,$roleObject)
$projectionArgs.Relationships=@($edges)+@($roleEdge)
$roleResult=ConvertTo-SCOMWindowsInventory @projectionArgs
if (-not $roleResult.Complete -or $roleResult.Inventory.devices[0].monitoringMode -ne 'agent') {
    throw 'Typed agent classification failed.'
}
$roleObject.Kind='Synthetic.ManagementServer'
$roleResult=ConvertTo-SCOMWindowsInventory @projectionArgs
if (-not $roleResult.Complete -or $roleResult.Inventory.devices[0].monitoringMode -ne 'managementServer') {
    throw 'Management server was hidden or mislabeled.'
}
$projectionArgs.Relationships=$edges
$roleResult=ConvertTo-SCOMWindowsInventory @projectionArgs
if ($roleResult.Complete -or $roleResult.Inventory.devices[0].monitoringMode -ne 'unknown' -or
    $roleResult.Inventory.devices.Count -ne 1) { throw 'Missing role must preserve the computer as partial/unknown.' }
$projectionArgs.Relationships=@($edges)+@($roleEdge,$roleEdge)
Assert-ProjectionFault { ConvertTo-SCOMWindowsInventory @projectionArgs } 'SCOM_WINDOWS_MONITORING_ROLE_AMBIGUOUS'
$roleMapping.verified=$false
Assert-ProjectionFault { ConvertTo-SCOMWindowsInventory @projectionArgs } 'SCOM_WINDOWS_MONITORING_ROLE_UNVERIFIED'
$roleMapping.verified=$true
$projectionArgs.Relationships=@($edges)+@([pscustomobject]@{
    id=[Guid]::NewGuid().ToString('D');className=$roleEdge.className;sourceId=$os.Id;targetId=$roleObject.Id
})
Assert-ProjectionFault { ConvertTo-SCOMWindowsInventory @projectionArgs } 'SCOM_WINDOWS_MONITORING_ROLE_RELATIONSHIP_INVALID'
$otherAgent=New-Object ProjectionObject; $otherAgent.Kind='Synthetic.Agent'
$projectionArgs.Objects+=@($otherAgent)
$projectionArgs.Relationships=@($edges)+@($roleEdge,[pscustomobject]@{
    id=[Guid]::NewGuid().ToString('D');className=$roleEdge.className;sourceId=$computer.Id;targetId=$otherAgent.Id
})
Assert-ProjectionFault { ConvertTo-SCOMWindowsInventory @projectionArgs } 'SCOM_WINDOWS_MONITORING_ROLE_AMBIGUOUS'
$projectionArgs.Objects=@($computer,$os,$volume,$root,$roleObject)
$projectionArgs.Relationships=@($edges)+@($roleEdge,[pscustomobject]@{
    id=[Guid]::NewGuid().ToString('D');className=$roleEdge.className;sourceId=$computer.Id;targetId=$root.Id
})
$roleResult=ConvertTo-SCOMWindowsInventory @projectionArgs
if (-not $roleResult.Complete -or $roleResult.Inventory.devices[0].monitoringMode -ne 'managementServer') {
    throw 'Unrelated local application changed monitoring classification.'
}
$mapping.PSObject.Properties.Remove('monitoringRoleMapping')
$mapping.agentMonitoringVerified=$true
$projectionArgs.Objects=@($computer,$os,$volume);$projectionArgs.Relationships=$edges
$module=Import-Module (Join-Path $PSScriptRoot 'ScomNormalization.psm1') -Force -PassThru
& $module {
    param($root,$computer,$os,$volume,$edges)
    $script:Root=$root
    $script:Rows=@($root,$computer,$os,$volume)
    $script:Edges=$edges
    function script:Resolve-SCOMApprovedGroupMembership {
        param($ReadDirectMembers)
        & $ReadDirectMembers $script:Root.Id 1001 | Out-Null
        [pscustomobject]@{ExpectedObjectIds=@($script:Rows.Id);GroupIds=@($script:Root.Id)}
    }
    function script:Read-SCOMDirectGroupMembers {
        [pscustomobject]@{Relationships=@($script:Rows | Where-Object Id -NE $script:Root.Id | ForEach-Object {
            [pscustomobject]@{Id=[Guid]::NewGuid();SourceObject=$script:Root;TargetObject=$_}
        })}
    }
    function script:Read-SCOMObjectIdPages {
        [pscustomobject]@{Objects=$script:Rows;DiscoveryComplete=$true}
    }
    function script:Read-SCOMDirectRelationshipRows {
        param($SourceId,$RelationshipClass)
        [pscustomobject]@{Relationships=@($script:Edges | Where-Object {
            [Guid]$_.sourceId -eq $SourceId -and $_.className -eq $RelationshipClass.Name
        } | ForEach-Object {
            $edge=$_
            [pscustomobject]@{Id=$edge.id
                SourceObject=@($script:Rows | Where-Object Id -EQ ([Guid]$edge.sourceId))[0]
                TargetObject=@($script:Rows | Where-Object Id -EQ ([Guid]$edge.targetId))[0]}
        })}
    }
} $root $computer $os $volume $edges
$instant=[DateTimeOffset]::UtcNow
$scanArgs=@{
    EntityObjects=@{};ManagementGroupId=[Guid]::NewGuid();GroupId=$root.Id
    GroupClass=(New-Object ProjectionClass 'Synthetic.Group')
    MembershipRelationshipClass=[pscustomobject]@{Name='Microsoft.SystemCenter.InstanceGroupContainsEntities'}
    ScopeVisibilityVerified=$true;MembershipQueryContractVerified=$true
    Request=@{requestedAtUtc=$instant.ToString('o');metricHistoryStartUtc=$instant.AddDays(-14).ToString('o');priorUnresolvedAlerts=@()}
    ReadActiveAlerts={param($object) @()};ReadAlertById={param($id) @()}
    WindowsMapping=$mapping;WindowsComputerClass=$projectionArgs.ComputerClass;WindowsOsClass=$projectionArgs.OperatingSystemClass
    WindowsVolumeClass=$projectionArgs.VolumeClass;AdditionalRelationshipContractVerified=$true
    AdditionalRelationshipClasses=@([pscustomobject]@{Name=$mapping.computerHostsOsRelationship},
        [pscustomobject]@{Name=$mapping.osHostsVolumeRelationship})
}
if ($BridgeMode -eq 'direct') {
    $mapping.PSObject.Properties.Remove('osHostsVolumeRelationship')
    $mapping | Add-Member -NotePropertyName computerHostsVolumeRelationship -NotePropertyValue 'Synthetic.ComputerHostsDevice'
    $scanArgs.AdditionalRelationshipClasses=@([pscustomobject]@{Name=$mapping.computerHostsOsRelationship},
        [pscustomobject]@{Name=$mapping.computerHostsVolumeRelationship})
    & $module { param($directEdges) $script:Edges=$directEdges } $directEdges
}
if ($BridgeMode -in @('managementServer','unknownRole')) {
    $mapping | Add-Member -NotePropertyName monitoringRoleMapping -NotePropertyValue $roleMapping
    $mapping.agentMonitoringVerified=$false
    $scanArgs.WindowsAgentClass=$agentClass
    $scanArgs.WindowsManagementServerClass=$serverClass
    $scanArgs.AdditionalRelationshipClasses+=@([pscustomobject]@{Name=$roleMapping.computerHostsApplicationRelationship})
    & $module {
        param($roleObject,$roleEdge,$mode)
        $script:Rows+=@($roleObject)
        if ($mode -eq 'managementServer') { $script:Edges+=@($roleEdge) }
    } $roleObject $roleEdge $BridgeMode
}
$scan=Get-SCOMGenericGroupScan @scanArgs
$wire=$scan | ConvertTo-Json -Depth 24 | ConvertFrom-Json
if ($wire.inventory.complete -ne ($BridgeMode -ne 'unknownRole') -or $wire.windowsInventory.devices.Count -ne 1 -or
    $wire.expectedComputerIds.Count -ne 1 -or $wire.expectedComputerIds[0] -ne $computer.Id.ToString('D') -or
    $wire.windowsInventory.devices[0].groupIds[0] -ne 'scom-lab') { throw 'Generic scan lost typed Windows wire contract.' }
if ($BridgeMode -in @('complete','direct','managementServer','unknownRole')) {
    [Console]::Out.Write((@{protocolVersion=1;scan=$scan} | ConvertTo-Json -Depth 24 -Compress))
    return
}
& $module { param($computer) $script:Rows=@($script:Rows | Where-Object Id -NE $computer.Id) } $computer
# Preserve authoritative membership observations, while discovery loses a computer.
& $module {
    param($root,$computer,$os,$volume)
    function script:Read-SCOMDirectGroupMembers {
        [pscustomobject]@{Relationships=@($script:MembershipRows | ForEach-Object {
            [pscustomobject]@{Id=[Guid]::NewGuid();SourceObject=$script:Root;TargetObject=$_}
        })}
    }
    $script:MembershipRows=@($computer,$os,$volume)
    function script:Resolve-SCOMApprovedGroupMembership {
        param($ReadDirectMembers)
        & $ReadDirectMembers $script:Root.Id 1001 | Out-Null
        [pscustomobject]@{ExpectedObjectIds=@($script:Root.Id)+@($script:MembershipRows.Id);GroupIds=@($script:Root.Id)}
    }
    function script:Read-SCOMObjectIdPages {
        [pscustomobject]@{Objects=$script:Rows;DiscoveryComplete=$false}
    }
} $root $computer $os $volume
$scan=Get-SCOMGenericGroupScan @scanArgs
if ($scan.inventory.complete -or $scan.windowsInventory.devices.Count -ne 0 -or
    $scan.expectedComputerIds.Count -ne 1) { throw 'Expected computers were derived from successful discovery.' }
if ($BridgeMode -eq 'partial') {
    [Console]::Out.Write((@{protocolVersion=1;scan=$scan} | ConvertTo-Json -Depth 24 -Compress))
    return
}
Write-Host 'Windows source projection: 25 synthetic cases passed; typed monitoring roles, missing-role partial coverage and hosting ambiguity rejection.'
