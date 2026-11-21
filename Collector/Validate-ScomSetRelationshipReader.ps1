param([switch]$NoIsDeleted)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ('Microsoft.EnterpriseManagement.IEntityObjectsManagement' -as [type]) {
    throw 'Synthetic set reader test requires a fresh process without SDK assemblies.'
}
# Synthetic stand-ins for the SDK types the set reader binds to by name. The manager truncates results at
# MaxResultCount exactly like the real SDK, so a full result is indistinguishable from a truncated one.
$typeText = @'
using System;
using System.Collections.Generic;
namespace Microsoft.EnterpriseManagement.Configuration {
    public class ManagementPackRelationship { public Guid Id, ManagementGroupId; public string Name; }
    public class ManagementPackClass {
        public Guid ManagementGroupId; public string Name;
        public IList<ManagementPackClass> GetBaseTypes() { return new ManagementPackClass[0]; }
    }
    public enum DerivedClassTraversalDepth { None, Recursive }
}
namespace Microsoft.EnterpriseManagement.Common {
    public class ObjectQueryOptions {
        public static bool Reject, IgnoreSetter;
        private int limit;
        public int MaxResultCount {
            get { return limit; }
            set { if (Reject) throw new NotSupportedException(); if (!IgnoreSetter) limit=value; }
        }
    }
    public class EnterpriseManagementRelationshipObject<T> {
        public Guid Id, RelationshipId, ManagementGroupId;
        public bool IsDeleted; //ISDELETED
        public T SourceObject, TargetObject;
    }
}
namespace Microsoft.EnterpriseManagement.Monitoring {
    public class MonitoringProperty { public string Name; }
    public class MonitoringValue { public object Value; }
    public class MonitoringObject {
        public Guid Id, ManagementGroupId;
        public string Kind, DisplayName="Synthetic", HealthState="Success";
        public Dictionary<string,object> Values=new Dictionary<string,object>();
        public bool IsInstanceOf(Configuration.ManagementPackClass c) { return Kind==c.Name; }
        public IList<Configuration.ManagementPackClass> GetMostDerivedClasses() {
            return new [] { new Configuration.ManagementPackClass { Name=Kind, ManagementGroupId=ManagementGroupId } };
        }
        public IList<MonitoringProperty> GetProperties() {
            var p=new List<MonitoringProperty>();
            foreach (var name in Values.Keys) p.Add(new MonitoringProperty { Name=name });
            return p;
        }
        public MonitoringValue this[MonitoringProperty p] { get { return new MonitoringValue { Value=Values[p.Name] }; } }
    }
}
namespace Microsoft.EnterpriseManagement {
    using Common;
    using Configuration;
    using Monitoring;
    public interface IEntityObjectsManagement {
        IList<EnterpriseManagementRelationshipObject<T>> GetRelationshipObjects<T>(
            ManagementPackRelationship relationship, DerivedClassTraversalDepth depth, ObjectQueryOptions options);
    }
    public class SyntheticEdge {
        public Guid Id=Guid.NewGuid();
        public string Rel;
        public MonitoringObject Source, Target;
        public bool Deleted, WrongGroup;
    }
    public class SyntheticSetManager : IEntityObjectsManagement {
        public int Calls;
        public List<int> Limits=new List<int>();
        public string Mode="valid";
        public Guid Group;
        public List<SyntheticEdge> Edges=new List<SyntheticEdge>();
        public IList<EnterpriseManagementRelationshipObject<T>> GetRelationshipObjects<T>(
            ManagementPackRelationship relationship, DerivedClassTraversalDepth depth, ObjectQueryOptions options) {
            Calls++; Limits.Add(options.MaxResultCount);
            if (depth!=DerivedClassTraversalDepth.None) throw new Exception("Unexpected derived traversal");
            if (Mode=="throw") throw new InvalidOperationException("Synthetic SDK failure");
            if (Mode=="null") return null;
            var rows=new List<EnterpriseManagementRelationshipObject<T>>();
            foreach (var e in Edges) {
                if (e.Rel!=relationship.Name) continue;
                if (rows.Count>=options.MaxResultCount) break;
                rows.Add(new EnterpriseManagementRelationshipObject<T> {
                    Id=e.Id,
                    RelationshipId=Mode=="derived" ? Guid.NewGuid() : relationship.Id,
                    ManagementGroupId=e.WrongGroup ? Guid.NewGuid() : Group,
                    IsDeleted=e.Deleted, //ISDELETED
                    SourceObject=(T)(object)e.Source,
                    TargetObject=(T)(object)e.Target });
            }
            return rows;
        }
    }
}
'@
if ($NoIsDeleted) {
    $typeText = (($typeText -split "`r?`n") | Where-Object { $_ -notmatch '//ISDELETED' }) -join "`n"
}
Add-Type -TypeDefinition $typeText
Import-Module (Join-Path $PSScriptRoot 'ScomSetRelationshipReader.psm1') -Force

$script:cases = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAILED: $Message" }
    $script:cases++
}
function Assert-Fault([scriptblock]$Action, [string]$Code) {
    $threw = $false
    try { & $Action | Out-Null }
    catch { $threw = $true; if ($_.Exception.Message -cne $Code) { throw "Expected $Code but got $($_.Exception.Message)" } }
    if (-not $threw) { throw "Expected $Code but the call succeeded" }
    $script:cases++
}

$ns = 'Microsoft.EnterpriseManagement'
$group = [Guid]::NewGuid()
$manager = New-Object "$ns.SyntheticSetManager"
$manager.Group = $group
function New-SynthClass([string]$Name) {
    $c = New-Object "$ns.Configuration.ManagementPackClass"; $c.Name = $Name; $c.ManagementGroupId = $group; $c
}
function New-SynthRelationship([string]$Name) {
    $r = New-Object "$ns.Configuration.ManagementPackRelationship"
    $r.Name = $Name; $r.Id = [Guid]::NewGuid(); $r.ManagementGroupId = $group; $r
}
function New-SynthObject([string]$Kind, [hashtable]$Values = @{}) {
    $o = New-Object "$ns.Monitoring.MonitoringObject"
    $o.Id = [Guid]::NewGuid(); $o.ManagementGroupId = $group; $o.Kind = $Kind
    foreach ($key in $Values.Keys) { $o.Values[$key] = $Values[$key] }
    $o
}
function Add-SynthEdge([string]$Rel, $Source, $Target) {
    $e = New-Object "$ns.SyntheticEdge"
    $e.Rel = $Rel; $e.Source = $Source; $e.Target = $Target
    $manager.Edges.Add($e)
    $e
}

$volumeClass = New-SynthClass 'Synthetic.Volume'
$relationship = New-SynthRelationship 'Synthetic.ComputerHostsDevice'
$computers = @(1..3 | ForEach-Object { New-SynthObject 'Synthetic.Computer' })
$volumes = @(1..5 | ForEach-Object { New-SynthObject 'Synthetic.Volume' })
$nic = New-SynthObject 'Synthetic.Nic'
$outsider = New-SynthObject 'Synthetic.Computer'
function Read-Set {
    Read-SCOMRelationshipSet -EntityObjects $manager -ManagementGroupId $group -RelationshipClass $relationship `
        -TargetClass $volumeClass -ServedSourceIds @($computers.Id) -ServedTargetIds @($volumes.Id)
}
function Reset-Manager { $manager.Edges.Clear(); $manager.Calls = 0; $manager.Limits.Clear(); $manager.Mode = 'valid' }
$layout = @(@(0, 0), @(0, 1), @(1, 2), @(1, 3), @(2, 4))

if ($NoIsDeleted) {
    Reset-Manager
    foreach ($pair in $layout) { Add-SynthEdge $relationship.Name $computers[$pair[0]] $volumes[$pair[1]] | Out-Null }
    $absent = Read-Set
    Check ($absent.Relationships.Count -eq 5 -and -not $absent.Complete) 'Rows without IsDeleted must be accepted and stay unverified'
    Write-Output "$script:cases synthetic cases passed (IsDeleted absent)"
    return
}

# --- Reader: normal 3 computers / 5 volumes ---
Reset-Manager
foreach ($pair in $layout) { Add-SynthEdge $relationship.Name $computers[$pair[0]] $volumes[$pair[1]] | Out-Null }
$normal = Read-Set
Check ($normal.Relationships.Count -eq 5 -and $normal.SkippedNonTarget -eq 0 -and $normal.SkippedOutsideScope -eq 0 -and
    $normal.OutsideScopeTargets -eq 0) 'Normal set read changed the edges'
Check ($manager.Calls -eq 1 -and $manager.Limits[0] -eq 6) 'Cap must be expected targets plus one in one call'
Check (-not $normal.CapHit -and -not $normal.Complete -and -not $normal.SourceWorkBoundVerified -and
    -not $normal.ScopeMembershipVerified) 'A set read must never claim completeness or source coverage'

# --- Reader: cap hit fails closed, no dataset, no retry ---
Reset-Manager
foreach ($pair in $layout) { Add-SynthEdge $relationship.Name $computers[$pair[0]] $volumes[$pair[1]] | Out-Null }
Add-SynthEdge $relationship.Name $computers[2] $nic | Out-Null
Assert-Fault { Read-Set } 'SCOM_SET_READ_CAP_HIT'
Check ($manager.Calls -eq 1) 'Cap hit must not retry'
foreach ($i in 1..4) { Add-SynthEdge $relationship.Name $computers[2] $nic | Out-Null }
Assert-Fault { Read-Set } 'SCOM_SET_READ_CAP_HIT'

# --- Reader: SDK call failure and unusable SDK surface report UNAVAILABLE ---
Reset-Manager
foreach ($mode in 'throw', 'null') {
    $manager.Mode = $mode; $manager.Calls = 0
    Assert-Fault { Read-Set } 'SCOM_SET_READ_UNAVAILABLE'
    Check ($manager.Calls -eq 1) "Mode $mode must not retry"
}
$manager.Mode = 'valid'
Assert-Fault {
    Read-SCOMRelationshipSet -EntityObjects @{} -ManagementGroupId $group -RelationshipClass $relationship `
        -TargetClass $volumeClass -ServedSourceIds @($computers.Id) -ServedTargetIds @($volumes.Id)
} 'SCOM_SET_READ_UNAVAILABLE'
$manager.Calls = 0
foreach ($switch in 'Reject', 'IgnoreSetter') {
    [Microsoft.EnterpriseManagement.Common.ObjectQueryOptions]::$switch = $true
    try { Assert-Fault { Read-Set } 'SCOM_SET_READ_UNAVAILABLE' }
    finally { [Microsoft.EnterpriseManagement.Common.ObjectQueryOptions]::$switch = $false }
}
Check ($manager.Calls -eq 0) 'A cap that is not retained must be rejected before any call'

# --- Reader: NIC targets and outside-scope rows are counted, never fatal ---
Reset-Manager
Add-SynthEdge $relationship.Name $computers[0] $volumes[0] | Out-Null
Add-SynthEdge $relationship.Name $computers[0] $nic | Out-Null
$skip = Read-Set
Check ($skip.Relationships.Count -eq 1 -and $skip.SkippedNonTarget -eq 1 -and $skip.SkippedOutsideScope -eq 0) 'NIC target must be skipped and counted'
Reset-Manager
Add-SynthEdge $relationship.Name $computers[0] $volumes[0] | Out-Null
Add-SynthEdge $relationship.Name $outsider $volumes[1] | Out-Null
$outside = Read-Set
Check ($outside.Relationships.Count -eq 1 -and $outside.SkippedOutsideScope -eq 1 -and $outside.SkippedNonTarget -eq 0) 'Outside-scope source must be skipped and counted'
Reset-Manager
Add-SynthEdge $relationship.Name $computers[0] (New-SynthObject 'Synthetic.Volume') | Out-Null
$outsideTarget = Read-Set
Check ($outsideTarget.Relationships.Count -eq 0 -and $outsideTarget.OutsideScopeTargets -eq 1) 'Unserved volume target must be counted, not returned'
Reset-Manager
Check ((Read-Set).Relationships.Count -eq 0) 'An empty result is a valid observation, never completeness'

# --- Reader: row validation of the members that exist ---
Reset-Manager
$deleted = Add-SynthEdge $relationship.Name $computers[0] $volumes[0]
$deleted.Deleted = $true
Assert-Fault { Read-Set } 'SCOM_RELATIONSHIP_ROW_INVALID'
Reset-Manager
$wrong = Add-SynthEdge $relationship.Name $computers[0] $volumes[0]
$wrong.WrongGroup = $true
Assert-Fault { Read-Set } 'SCOM_RELATIONSHIP_ROW_INVALID'
Reset-Manager
$first = Add-SynthEdge $relationship.Name $computers[0] $volumes[0]
$second = Add-SynthEdge $relationship.Name $computers[0] $volumes[1]
$second.Id = $first.Id
Assert-Fault { Read-Set } 'SCOM_RELATIONSHIP_ROW_INVALID'
Reset-Manager
Add-SynthEdge $relationship.Name $computers[0] $volumes[0] | Out-Null
$manager.Mode = 'derived'
Assert-Fault { Read-Set } 'SCOM_RELATIONSHIP_ROW_INVALID'
$manager.Mode = 'valid'

# --- Reader: IsDeleted absent from the SDK row type is tolerated (separate process, type compiled without it) ---
$powerShell = (Get-Process -Id $PID).Path
$childOutput = & $powerShell -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -NoIsDeleted 2>&1
Check ($LASTEXITCODE -eq 0 -and (($childOutput | Out-String) -match 'IsDeleted absent')) "IsDeleted-absent run failed: $($childOutput | Out-String)"

# --- Scan level: real set reader and per-source stubs over one master edge list ---
$module = Import-Module (Join-Path $PSScriptRoot 'ScomNormalization.psm1') -Force -PassThru
$computerClass = New-SynthClass 'Synthetic.Computer'
$osClass = New-SynthClass 'Synthetic.Os'
$scanVolumeClass = New-SynthClass 'Synthetic.Volume'
$osRel = New-SynthRelationship 'Synthetic.ComputerHostsOs'
$osVolumeRel = New-SynthRelationship 'Synthetic.OsHostsVolume'
$directRel = New-SynthRelationship 'Synthetic.ComputerHostsDevice'
$sc = @(1..3 | ForEach-Object { New-SynthObject 'Synthetic.Computer' @{DnsName="host$_.synthetic.invalid"; Cpu=4} })
$so = @(1..3 | ForEach-Object { New-SynthObject 'Synthetic.Os' @{Version='10.0.20348'; Memory=8388608} })
$sv = @(1..5 | ForEach-Object { New-SynthObject 'Synthetic.Volume' @{Name="V$_"; FileSystem='NTFS'; Size=10737418240} })
$snic = New-SynthObject 'Synthetic.Nic'
$unservedOs = New-SynthObject 'Synthetic.Os' @{Version='10.0.20348'; Memory=8388608}
$allObjects = @($sc) + @($so) + @($sv)
& $module {
    param($rows, $edges)
    $script:Rows = $rows
    $script:Edges = $edges
    $script:PerSourceCalls = 0
    function script:Read-SCOMWindowsManagementGroupScope {
        [pscustomobject]@{ExpectedObjectIds = @($script:Rows.Id); MembershipObjects = @($script:Rows)}
    }
    function script:Read-SCOMObjectIdPages {
        [pscustomobject]@{Objects = $script:Rows; DiscoveryComplete = $true}
    }
    function script:Read-SCOMDirectRelationshipRows {
        param($SourceId, $RelationshipClass)
        $script:PerSourceCalls++
        [pscustomobject]@{Relationships = @($script:Edges | Where-Object {
            $_.Source.Id -eq $SourceId -and $_.Rel -ceq $RelationshipClass.Name
        } | ForEach-Object { [pscustomobject]@{Id = $_.Id; SourceObject = $_.Source; TargetObject = $_.Target} })}
    }
} $allObjects $manager.Edges

function New-Fixture([string]$Variant) {
    Reset-Manager
    foreach ($i in 0..2) { Add-SynthEdge $osRel.Name $sc[$i] $so[$i] | Out-Null }
    if ($Variant -ceq 'os') {
        foreach ($pair in @(@(0, 0), @(0, 1), @(1, 2), @(1, 3), @(2, 4))) {
            Add-SynthEdge $osVolumeRel.Name $so[$pair[0]] $sv[$pair[1]] | Out-Null
        }
    }
    else {
        foreach ($pair in $layout) { Add-SynthEdge $directRel.Name $sc[$pair[0]] $sv[$pair[1]] | Out-Null }
    }
}
function Remove-Edge([string]$Rel, $Target) {
    $edge = @($manager.Edges | Where-Object { $_.Rel -ceq $Rel -and $_.Target.Id -eq $Target.Id })[0]
    [void]$manager.Edges.Remove($edge)
}
function New-Mapping([string]$Variant) {
    $mapping = [pscustomobject]@{
        verified = $true; agentMonitoringVerified = $true
        computerClassName = 'Synthetic.Computer'; operatingSystemClassName = 'Synthetic.Os'; volumeClassName = 'Synthetic.Volume'
        hostnameProperty = 'DnsName'; osVersionProperty = 'Version'; memoryProperty = 'Memory'
        processorCountProperty = 'Cpu'; volumeNameProperty = 'Name'; fileSystemProperty = 'FileSystem'
        volumeSizeProperty = 'Size'; memoryToMiB = (1.0 / 1024); volumeSizeToMiB = (1.0 / 1048576)
        computerHostsOsRelationship = $osRel.Name
    }
    $diskName = if ($Variant -ceq 'os') { 'osHostsVolumeRelationship' } else { 'computerHostsVolumeRelationship' }
    $diskRel = if ($Variant -ceq 'os') { $osVolumeRel } else { $directRel }
    $mapping | Add-Member -NotePropertyName $diskName -NotePropertyValue $diskRel.Name
    $mapping
}
$script:AlertCalls = 0
$script:Alerts = @()
function Invoke-Scan([string]$Mode, [string]$Variant = 'os', [hashtable]$Counters = @{}, [switch]$Lab, [switch]$Generic, [switch]$NoRelationships, $EntityObjects = $manager) {
    $instant = [DateTimeOffset]::UtcNow
    $diskRel = if ($Variant -ceq 'os') { $osVolumeRel } else { $directRel }
    $arguments = @{
        EntityObjects = $EntityObjects; ManagementGroupId = $group; ScopeVisibilityVerified = $true
        ScopeKind = 'managementGroup'; WholeManagementGroupApproved = $true; EnumerationQueryContractVerified = $true
        InventoryKind = $(if ($Generic) { 'generic' } else { 'supportedWindows' })
        Request = @{requestedAtUtc = $instant.ToString('o'); metricHistoryStartUtc = $instant.AddDays(-14).ToString('o'); priorUnresolvedAlerts = @()}
        ReadActiveAlerts = { $script:AlertCalls++; @($script:Alerts) }
        ReadAlertById = { param($id) @() }
        RelationshipReadMode = $Mode; GraphCounters = $Counters; AllowedPropertiesByClass = @{}
    }
    if ($Generic) { $arguments.EntityClass = $computerClass }
    else {
        $arguments.WindowsMapping = New-Mapping $Variant
        $arguments.WindowsComputerClass = $computerClass
        $arguments.WindowsOsClass = $osClass
        $arguments.WindowsVolumeClass = $scanVolumeClass
        if (-not $NoRelationships) {
            $arguments.AdditionalRelationshipClasses = @($osRel, $diskRel)
            $arguments.AdditionalRelationshipContractVerified = -not $Lab
        }
        if ($Lab) { $arguments.HostingLabPreview = $true }
    }
    Get-SCOMGenericScan @arguments
}
function ConvertTo-Comparable($Scan) {
    $stamp = '\d{4}-\d\d-\d\dT[\d:.]+(Z|[+-]\d\d:\d\d)'
    [pscustomobject]@{
        Relationships = @($Scan.relationships | Sort-Object id | ForEach-Object { "$($_.id)|$($_.className)|$($_.sourceId)|$($_.targetId)" })
        Inventory = ($Scan.windowsInventory | ConvertTo-Json -Depth 20 -Compress) -replace $stamp, 'T'
        Expected = @($Scan.expectedComputerIds | Sort-Object)
        Objects = @($Scan.objects | ForEach-Object { $_.id } | Sort-Object)
        Complete = [bool]$Scan.inventory.complete
        Reason = [string]$Scan.inventory.reason
    }
}
function Assert-SameResult($Left, $Right, [string]$Message) {
    $a = ConvertTo-Comparable $Left
    $b = ConvertTo-Comparable $Right
    Check (($a.Relationships -join ';') -ceq ($b.Relationships -join ';') -and $a.Inventory -ceq $b.Inventory -and
        ($a.Expected -join ';') -ceq ($b.Expected -join ';') -and ($a.Objects -join ';') -ceq ($b.Objects -join ';') -and
        $a.Complete -eq $b.Complete -and $a.Reason -ceq $b.Reason) $Message
}
function Get-PerSourceCalls { & $module { $script:PerSourceCalls } }
function Reset-PerSourceCalls { & $module { $script:PerSourceCalls = 0 } }

# Normal 3 computers / 3 OS / 5 volumes: set equals per-source, with 2 calls instead of 22.
foreach ($variant in 'os', 'direct') {
    New-Fixture $variant
    $perCounters = @{}; $setCounters = @{}
    Reset-PerSourceCalls
    $perSource = Invoke-Scan 'perSource' $variant $perCounters
    $perSourceCalls = Get-PerSourceCalls
    $manager.Calls = 0
    Reset-PerSourceCalls
    $set = Invoke-Scan 'set' $variant $setCounters
    Check ($perSource.inventory.complete -and $set.inventory.complete) "Normal $variant fixture must be complete in both modes"
    Assert-SameResult $perSource $set "Set and per-source results differ for $variant fixture"
    Check ($perCounters.graphQueries -eq 22 -and $perSourceCalls -eq 22 -and $perCounters.readMode -ceq 'perSource') "Per-source must make 22 calls ($variant)"
    Check ($setCounters.graphQueries -eq 2 -and $manager.Calls -eq 2 -and (Get-PerSourceCalls) -eq 0 -and
        $setCounters.readMode -ceq 'set' -and -not $setCounters.setReadFallback) "Set mode must make exactly 2 set calls ($variant)"
    Check ($setCounters.skippedNonVolumeTargets -eq 0 -and $setCounters.skippedNonOsTargets -eq 0 -and
        $setCounters.skippedOutsideScopeSources -eq 0) "Normal $variant fixture must skip nothing"
    Check ($perCounters.skippedNonVolumeTargets -eq 0 -and $perCounters.skippedOutsideScopeSources -eq 0) 'Per-source counters stay 0'
    Check ($null -eq $set.PSObject.Properties['graph']) 'The scan object must not carry collector counters'
}

# Call exception: fall back to per-source for this run, once, with an identical result.
New-Fixture 'os'
$expected = Invoke-Scan 'perSource'
$manager.Mode = 'throw'; $manager.Calls = 0
$fallbackCounters = @{}
$fallback = Invoke-Scan 'set' 'os' $fallbackCounters
Check ($fallbackCounters.setReadFallback -and $fallbackCounters.readMode -ceq 'perSource' -and
    $fallbackCounters.graphQueries -eq 22 -and $manager.Calls -eq 1) 'Set failure must fall back once to 22 per-source calls'
Assert-SameResult $expected $fallback 'Fallback result differs from per-source'
$manager.Mode = 'valid'

# Cap hit is fatal for the scan: no dataset, no fallback. A NIC row on the direct relationship reaches the cap.
New-Fixture 'direct'
Add-SynthEdge $directRel.Name $sc[2] $snic | Out-Null
$manager.Calls = 0
Reset-PerSourceCalls
Assert-Fault { Invoke-Scan 'set' 'direct' } 'SCOM_SET_READ_CAP_HIT'
Check ((Get-PerSourceCalls) -eq 0) 'Cap hit must not fall back to per-source'

# NIC target skipped and counted when it fits under the cap; the result still matches per-source.
New-Fixture 'direct'
Remove-Edge $directRel.Name $sv[4]
Add-SynthEdge $directRel.Name $sc[2] $snic | Out-Null
$nicPer = Invoke-Scan 'perSource' 'direct'
$nicCounters = @{}
$nicSet = Invoke-Scan 'set' 'direct' $nicCounters
Check ($nicCounters.skippedNonVolumeTargets -eq 1 -and -not $nicCounters.setReadFallback) 'NIC target must be skipped and counted, never fatal'
Assert-SameResult $nicPer $nicSet 'NIC fixture differs between modes'

# Source outside the served computer set is skipped and counted.
New-Fixture 'os'
Remove-Edge $osVolumeRel.Name $sv[4]
Add-SynthEdge $osVolumeRel.Name $unservedOs $sv[4] | Out-Null
$outsidePer = Invoke-Scan 'perSource'
$outsideCounters = @{}
$outsideSet = Invoke-Scan 'set' 'os' $outsideCounters
Check ($outsideCounters.skippedOutsideScopeSources -eq 1) 'Outside-scope source must be counted'
Assert-SameResult $outsidePer $outsideSet 'Outside-scope fixture differs between modes'
Check (@($outsideSet.relationships | Where-Object { $_.sourceId -eq $unservedOs.Id.ToString('D') }).Count -eq 0) 'Outside-scope source leaked into relationships'

# Missing OS edge: computer stays listed, scan is incomplete in both modes.
New-Fixture 'os'
Remove-Edge $osRel.Name $so[1]
foreach ($mode in 'perSource', 'set') {
    $missing = Invoke-Scan $mode
    $device = @($missing.windowsInventory.devices | Where-Object { $_.entityId -eq $sc[1].Id.ToString('D') })
    Check (-not $missing.inventory.complete -and $missing.inventory.reason -ceq 'SCOM_WINDOWS_PROJECTION_INCOMPLETE' -and
        $device.Count -eq 1 -and $null -eq $device[0].osVersion -and
        ($missing.expectedComputerIds -contains $sc[1].Id.ToString('D'))) "Missing OS edge must leave the computer listed and the scan incomplete ($mode)"
}

# Missing disk edge: the disk is observed but not in the inventory, and the scan is incomplete in both modes.
New-Fixture 'os'
Remove-Edge $osVolumeRel.Name $sv[4]
foreach ($mode in 'perSource', 'set') {
    $missing = Invoke-Scan $mode
    Check (-not $missing.inventory.complete -and $missing.inventory.reason -ceq 'SCOM_WINDOWS_PROJECTION_INCOMPLETE' -and
        @($missing.objects | Where-Object { $_.id -eq $sv[4].Id.ToString('D') }).Count -eq 1 -and
        @($missing.windowsInventory.volumes).Count -eq 4) "Missing disk edge must leave the disk observed and the scan incomplete ($mode)"
}

# Invalid modes are rejected before any SDK call.
New-Fixture 'os'
$manager.Calls = 0
Reset-PerSourceCalls
foreach ($bad in 'bogus', 'Set', 'PERSOURCE', '') {
    Assert-Fault { Invoke-Scan $bad } 'SCOM_SET_READ_MODE_INVALID'
}
Assert-Fault { Invoke-Scan 'set' 'os' -Generic } 'SCOM_SET_READ_MODE_INVALID'
Assert-Fault { Invoke-Scan 'set' 'os' -Lab } 'SCOM_SET_READ_MODE_INVALID'
Assert-Fault { Invoke-Scan 'set' 'os' -NoRelationships } 'SCOM_SET_READ_MODE_INVALID'
Check ($manager.Calls -eq 0 -and (Get-PerSourceCalls) -eq 0) 'Invalid mode must not touch the SDK'

# Bulk alerts: one call in either mode, out-of-scope parents dropped, malformed parents rejected.
$inScope = [pscustomobject]@{Id = [Guid]::NewGuid(); MonitoringObjectId = $sc[0].Id; ResolutionState = [byte]0; Severity = 'Error'; Name = 'Synthetic alert'}
$outOfScope = [pscustomobject]@{Id = [Guid]::NewGuid(); MonitoringObjectId = [Guid]::NewGuid(); ResolutionState = [byte]0; Severity = 'Warning'; Name = 'Elsewhere'}
New-Fixture 'os'
foreach ($mode in 'perSource', 'set') {
    $script:AlertCalls = 0
    $script:Alerts = @($inScope, $outOfScope)
    $withAlerts = Invoke-Scan $mode
    Check ($script:AlertCalls -eq 1) "Alerts must be read in one bulk call ($mode)"
    Check (@($withAlerts.alertObservations).Count -eq 1 -and $withAlerts.alertObservations[0].objectId -eq $sc[0].Id.ToString('D')) "Out-of-scope alert must be dropped ($mode)"
}
$script:Alerts = @([pscustomobject]@{Id = [Guid]::NewGuid(); MonitoringObjectId = 'not-a-guid'; ResolutionState = [byte]0; Severity = 'Error'; Name = 'Bad'})
Assert-Fault { Invoke-Scan 'perSource' } 'SCOM_ALERT_ID_OR_PARENT_INVALID'
$script:Alerts = @()

# The collector entry point reads and validates the mode; it only runs on Windows PowerShell with the SDK.
$tokens = $null; $parseErrors = $null
$invokePath = Join-Path $PSScriptRoot 'Invoke-SCOMGenericGroupProfile.ps1'
[void][Management.Automation.Language.Parser]::ParseFile($invokePath, [ref]$tokens, [ref]$parseErrors)
$invokeText = Get-Content -LiteralPath $invokePath -Raw
Check ($parseErrors.Count -eq 0 -and $invokeText.Contains('relationshipReadMode') -and
    $invokeText.Contains('SCOM_SET_READ_MODE_INVALID') -and $invokeText.Contains('-Criteria')) 'Invoke script must parse and validate the mode'

Write-Output "$script:cases synthetic cases passed"
