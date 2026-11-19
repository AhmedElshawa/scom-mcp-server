$ErrorActionPreference = 'Stop'
$module = Import-Module (Join-Path $PSScriptRoot 'ScomNormalization.psm1') -Force -PassThru
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
public class GenericProfileClass {
    public string Name = "Synthetic.Computer";
    public IList<GenericProfileClass> GetBaseTypes() { return new GenericProfileClass[0]; }
}
public class GenericProfileObject {
    public Guid Id;
    public string DisplayName = "Synthetic object";
    public string HealthState = "Success";
    public IList<GenericProfileClass> GetMostDerivedClasses() { return new [] { new GenericProfileClass() }; }
}
'@
$root = [Guid]'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$child = [Guid]'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
$outside = [Guid]'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
$a = New-Object GenericProfileObject
$a.Id = $root
$b = New-Object GenericProfileObject
$b.Id = $child
& $module {
    param($root, $child, $a, $b)
    $script:ScopeIds = @($root, $child)
    $script:Root = $root
    $script:Rows = @($a, $b)
    $script:Complete = $true
    $script:Target = $child
    function script:Resolve-SCOMApprovedGroupMembership {
        param($ReadDirectMembers)
        & $ReadDirectMembers $script:Root 1001 | Out-Null
        [pscustomobject]@{ ExpectedObjectIds=$script:ScopeIds; GroupIds=@($script:Root); MembershipVerified=$true }
    }
    function script:Read-SCOMObjectIdPages {
        [pscustomobject]@{ Objects=$script:Rows; DiscoveryComplete=$script:Complete }
    }
    function script:Read-SCOMDirectGroupMembers {
        [pscustomobject]@{ Relationships=@([pscustomobject]@{
            Id=[Guid]'dddddddd-dddd-4ddd-8ddd-dddddddddddd'
            SourceObject=[pscustomobject]@{Id=$script:Root}
            TargetObject=[pscustomobject]@{Id=$script:Target}
        }) }
    }
    function script:Read-SCOMSelectedMetricSeries {
        [pscustomobject]@{ Series=@(); SampleCount=0; Complete=$true; Reason=$null }
    }
    function script:Read-SCOMDirectRelationshipRows {
        param($SourceId,$IncludeDerivedRelationships)
        if ($IncludeDerivedRelationships) { throw 'Graph must preserve exact relationship type.' }
        [pscustomobject]@{Relationships=@([pscustomobject]@{
            Id=[Guid]::NewGuid()
            SourceObject=[pscustomobject]@{Id=$SourceId}
            TargetObject=[pscustomobject]@{Id=$script:GraphTarget}
        })}
    }
    $script:GraphTarget=$child
} $root $child $a $b
$request = @{
    requestedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    metricHistoryStartUtc=[DateTimeOffset]::UtcNow.AddDays(-14).ToString('o')
    priorUnresolvedAlerts=@()
}
# Both bounds derive from one instant, not separate clock reads.
$request.metricHistoryStartUtc = ([DateTimeOffset]$request.requestedAtUtc).AddDays(-14).ToString('o')
$calls = New-Object 'System.Collections.Generic.List[System.Guid]'
$args = @{
    EntityObjects=@{}
    ManagementGroupId=[Guid]'11111111-1111-4111-8111-111111111111'
    GroupId=$root
    GroupClass=[pscustomobject]@{Name='Microsoft.SystemCenter.InstanceGroup'}
    MembershipRelationshipClass=[pscustomobject]@{Name='Microsoft.SystemCenter.InstanceGroupContainsEntities'}
    ScopeVisibilityVerified=$true
    MembershipQueryContractVerified=$true
    Request=$request
    ReadActiveAlerts={ param($object) @() }
    ReadAlertById={ param($id) $calls.Add($id); @() }
}
$scan = Get-SCOMGenericGroupScan @args
if ($scan.objects.Count -ne 2 -or $scan.relationships.Count -ne 1 -or
    -not $scan.inventory.complete -or $scan.metricAssessment.complete -or
    $scan.metricAssessment.reason -ne 'VERIFIED_METRIC_PROFILE_NOT_CONFIGURED') { throw 'Generic scope/coverage normalization failed.' }
$args.MetricMappingsByClass=@{ 'Synthetic.Computer'=@([pscustomobject]@{}) }
$assessed=Get-SCOMGenericGroupScan @args
if (-not $assessed.metricAssessment.complete -or $assessed.metricsAsOfUtc -ne $request.requestedAtUtc) {
    throw 'Selected metric assessment lost explicit bounded history timestamp.'
}
$args.MetricMappingsByClass=@{}
$args.AdditionalRelationshipClasses=@([pscustomobject]@{Name='Synthetic.Hosts'})
$args.AdditionalRelationshipContractVerified=$true
$graph=Get-SCOMGenericGroupScan @args
if ($graph.relationships.Count -ne 3 -or -not $graph.inventory.complete) { throw 'Scoped additional graph edges missing.' }
$args.MaxGraphQueries=1
try { Get-SCOMGenericGroupScan @args | Out-Null; throw 'Graph query budget ignored.' }
catch { if ($_.Exception.Message -ne 'SCOM_PROFILE_GRAPH_QUERY_BUDGET') { throw } }
$args.MaxGraphQueries=10000
& $module { param($outside) $script:GraphTarget=$outside } $outside
$outsideGraph=Get-SCOMGenericGroupScan @args
if ($outsideGraph.relationships.Count -ne 1 -or $outsideGraph.inventory.complete -or
    $outsideGraph.inventory.reason -ne 'SCOM_GRAPH_ENDPOINTS_OUTSIDE_SERVED_SCOPE') {
    throw 'Outside graph endpoints leaked or omitted silently.'
}
$args.AdditionalRelationshipContractVerified=$false
try { Get-SCOMGenericGroupScan @args | Out-Null; throw 'Unverified graph queried.' }
catch { if ($_.Exception.Message -ne 'SCOM_PROFILE_GRAPH_CONTRACT_UNVERIFIED') { throw } }
$args.AdditionalRelationshipClasses=@()
$args.Request.priorUnresolvedAlerts = @(@{
    alertId='eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee'; objectId=$outside.ToString('D')
})
Get-SCOMGenericGroupScan @args | Out-Null
if ($calls.Count -ne 0) { throw 'Outside-scope prior alert hint queried.' }
$args.Request.priorUnresolvedAlerts[0].objectId=$child.ToString('D')
Get-SCOMGenericGroupScan @args | Out-Null
if ($calls.Count -ne 1) { throw 'Authorized prior alert was not reassessed.' }
& $module { $script:Complete=$false; $script:Rows=@($script:Rows[0]) }
$partial = Get-SCOMGenericGroupScan @args
if ($partial.inventory.complete -or $partial.alerts.complete -or $partial.objects.Count -ne 1 -or
    $partial.relationships.Count -ne 0) { throw 'Missing discovery failed to mark inventory/alert coverage partial.' }
& $module { param($outside) $script:Target=$outside } $outside
try { Get-SCOMGenericGroupScan @args | Out-Null; throw 'Mid-scan outside target accepted.' }
catch { if ($_.Exception.Message -ne 'SCOM_PROFILE_MEMBERSHIP_CHANGED_DURING_SCAN') { throw } }
$args.ScopeVisibilityVerified=$false
try { Get-SCOMGenericGroupScan @args | Out-Null; throw 'Unverified scope accepted.' }
catch { if ($_.Exception.Message -ne 'SCOM_PROFILE_SCOPE_UNVERIFIED') { throw } }
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
public class HostingFixtureClass {
    public string Name;
    public HostingFixtureClass(string name) { Name = name; }
    public IList<HostingFixtureClass> GetBaseTypes() { return new HostingFixtureClass[0]; }
}
public class HostingFixtureProperty { public string Name; }
public class HostingFixtureValue { public object Value; }
public class HostingFixtureObject {
    public Guid Id = Guid.NewGuid();
    public string DisplayName = "Synthetic";
    public string HealthState = "Success";
    public string Kind;
    public Dictionary<string,object> Values = new Dictionary<string,object>();
    public bool IsInstanceOf(HostingFixtureClass c) { return Kind == c.Name; }
    public IList<HostingFixtureClass> GetMostDerivedClasses() { return new [] { new HostingFixtureClass(Kind) }; }
    public IList<HostingFixtureProperty> GetProperties() {
        var p = new List<HostingFixtureProperty>();
        foreach (var name in Values.Keys) p.Add(new HostingFixtureProperty { Name = name });
        return p;
    }
    public HostingFixtureValue this[HostingFixtureProperty p] { get { return new HostingFixtureValue { Value = Values[p.Name] }; } }
}
'@
$fixtureRoot = New-Object HostingFixtureObject; $fixtureRoot.Kind = 'Synthetic.Group'
$fixtureComputer = New-Object HostingFixtureObject; $fixtureComputer.Kind = 'Synthetic.Computer'
$fixtureOs = New-Object HostingFixtureObject; $fixtureOs.Kind = 'Synthetic.Os'
$fixtureVolume = New-Object HostingFixtureObject; $fixtureVolume.Kind = 'Synthetic.Volume'
$fixtureDevice = New-Object HostingFixtureObject; $fixtureDevice.Kind = 'Synthetic.NonVolumeDevice'
$fixtureComputer.Values['DnsName'] = 'synthetic.invalid'
$fixtureComputer.Values['Cpu'] = 4
$fixtureOs.Values['Version'] = 'Synthetic version'
$fixtureOs.Values['Memory'] = 8388608
$fixtureVolume.Values['Name'] = 'C:'
$fixtureVolume.Values['FileSystem'] = 'NTFS'
$fixtureVolume.Values['Size'] = 10737418240
$hostingMapping = [pscustomobject]@{
    verified=$true; agentMonitoringVerified=$true
    hostnameProperty='DnsName'; osVersionProperty='Version'; memoryProperty='Memory'
    processorCountProperty='Cpu'; volumeNameProperty='Name'; fileSystemProperty='FileSystem'; volumeSizeProperty='Size'
    memoryToMiB=(1.0/1024); volumeSizeToMiB=(1.0/1048576)
    computerHostsOsRelationship='Synthetic.ComputerHostsOs'; computerHostsVolumeRelationship='Synthetic.ComputerHostsDevice'
}
& $module {
    param($root, $computer, $os, $volume, $device)
    $script:FixtureRoot = $root
    $script:FixtureRows = @($root, $computer, $os, $volume)
    $script:FixtureTargets = @{
        'Synthetic.ComputerHostsOs' = @($os)
        'Synthetic.ComputerHostsDevice' = @($volume, $device)
    }
    function script:Resolve-SCOMApprovedGroupMembership {
        param($ReadDirectMembers)
        & $ReadDirectMembers $script:FixtureRoot.Id 1001 | Out-Null
        [pscustomobject]@{ ExpectedObjectIds=@($script:FixtureRows.Id); GroupIds=@($script:FixtureRoot.Id) }
    }
    function script:Read-SCOMDirectGroupMembers {
        [pscustomobject]@{ Relationships=@($script:FixtureRows | Where-Object Id -NE $script:FixtureRoot.Id | ForEach-Object {
            [pscustomobject]@{ Id=[Guid]::NewGuid(); SourceObject=$script:FixtureRoot; TargetObject=$_ }
        }) }
    }
    function script:Read-SCOMObjectIdPages {
        [pscustomobject]@{ Objects=$script:FixtureRows; DiscoveryComplete=$true }
    }
    function script:Read-SCOMDirectRelationshipRows {
        param($SourceId, $RelationshipClass)
        $source = @($script:FixtureRows | Where-Object Id -EQ $SourceId)[0]
        $rows = @()
        if ($source.Kind -ceq 'Synthetic.Computer') {
            $rows = @($script:FixtureTargets[$RelationshipClass.Name] | ForEach-Object {
                [pscustomobject]@{ Id=[Guid]::NewGuid(); SourceObject=$source; TargetObject=$_ }
            })
        }
        [pscustomobject]@{ Relationships=$rows }
    }
} $fixtureRoot $fixtureComputer $fixtureOs $fixtureVolume $fixtureDevice
$hostingArgs = @{
    EntityObjects=@{}
    ManagementGroupId=[Guid]'11111111-1111-4111-8111-111111111111'
    GroupId=$fixtureRoot.Id
    GroupClass=(New-Object HostingFixtureClass 'Synthetic.Group')
    MembershipRelationshipClass=[pscustomobject]@{Name='Microsoft.SystemCenter.InstanceGroupContainsEntities'}
    ScopeVisibilityVerified=$true
    MembershipQueryContractVerified=$true
    Request=$request
    ReadActiveAlerts={ param($object) @() }
    ReadAlertById={ param($id) @() }
    WindowsMapping=$hostingMapping
    WindowsComputerClass=(New-Object HostingFixtureClass 'Synthetic.Computer')
    WindowsOsClass=(New-Object HostingFixtureClass 'Synthetic.Os')
    WindowsVolumeClass=(New-Object HostingFixtureClass 'Synthetic.Volume')
    AdditionalRelationshipContractVerified=$true
    AdditionalRelationshipClasses=@([pscustomobject]@{Name=$hostingMapping.computerHostsOsRelationship},
        [pscustomobject]@{Name=$hostingMapping.computerHostsVolumeRelationship})
}
$hosting = Get-SCOMGenericGroupScan @hostingArgs
if (-not $hosting.inventory.complete -or $null -ne $hosting.inventory.reason -or
    $hosting.windowsInventory.devices.Count -ne 1 -or $hosting.windowsInventory.volumes.Count -ne 1 -or
    $hosting.windowsInventory.volumes[0].parentEntityId -ne $fixtureComputer.Id.ToString('D')) {
    throw 'Non-volume logical-device hosting target made Windows inventory partial.'
}
$tokens=$null; $errors=$null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Invoke-SCOMGenericGroupProfile.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Profile entrypoint syntax invalid.' }
$metricConfiguration = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -match "Properties\['metricMappingsByClass'\]"
}, $true))
if ($metricConfiguration.Count -ne 1 -or
    $metricConfiguration[0].Extent.Text -match '\$graphClasses|\$maxSeriesSamples|\$maxMetricSamples') {
    throw 'Optional metric mapping must not gate graph or budget configuration.'
}
Write-Output 'Generic SCOM profile validation passed: 14 synthetic cases; scoped graph/alerts/metrics, query budget, outside-edge partial coverage, non-volume hosting targets and verification gates. Entrypoint parsed only.'
