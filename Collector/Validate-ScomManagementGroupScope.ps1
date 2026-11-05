param([ValidateSet('','complete','partial','empty')] [string]$BridgeMode='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ScomSdkReader.psm1') -Force
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
namespace Microsoft.EnterpriseManagement.Configuration {
    public class ManagementPackClass {
        public string Name {get;set;}
        public IList<ManagementPackClass> GetBaseTypes() {return new ManagementPackClass[0];}
    }
}
namespace Microsoft.EnterpriseManagement.Common {
    public enum ObjectPropertyRetrievalBehavior { None, All }
    public class ObjectQueryOptions {
        public int MaxResultCount {get;set;}
        public ObjectPropertyRetrievalBehavior DefaultPropertyRetrievalBehavior {get;set;}
    }
    public interface IObjectReader<T> {
        int PageSize {get;set;}
        int Count {get;}
        IList<T> GetRange(int offset,int take);
    }
}
namespace Microsoft.EnterpriseManagement.Monitoring {
    using Microsoft.EnterpriseManagement.Common;
    using Microsoft.EnterpriseManagement.Configuration;
    public class MonitoringObject {
        public Guid Id {get;set;}
        public string DisplayName {get {return "Synthetic";}}
        public string HealthState {get {return "Success";}}
        public bool InRoot=true;
        public bool IsInstanceOf(ManagementPackClass c) {return InRoot && c.Name=="System.Entity";}
        public IList<ManagementPackClass> GetMostDerivedClasses() {
            return new [] {new ManagementPackClass {Name="Synthetic.Object"}};
        }
    }
    public interface IMonitoringObjectsManagement {
        IObjectReader<T> GetObjectReader<T>(ManagementPackClass c,ObjectQueryOptions o);
        IObjectReader<T> GetObjectReader<T>(ICollection<Guid> ids,ObjectQueryOptions o);
    }
    public class ScopeManager:IMonitoringObjectsManagement {
        public List<MonitoringObject> Rows=new List<MonitoringObject>();
        public string Mode="";
        public int Calls;
        public int IdCalls;
        public int RequestedLimit;
        public int PageCalls;
        public IObjectReader<T> GetObjectReader<T>(ManagementPackClass c,ObjectQueryOptions o) {
            Calls++;RequestedLimit=o.MaxResultCount;
            var values=new List<T>();
            foreach(var r in Rows) values.Add((T)(object)r);
            return new ScopeReader<T>(values,this);
        }
        public IObjectReader<T> GetObjectReader<T>(ICollection<Guid> ids,ObjectQueryOptions o) {
            IdCalls++;
            var values=new List<T>();
            foreach(var r in Rows) if(ids.Contains(r.Id)) values.Add((T)(object)r);
            return new ScopeReader<T>(values,this);
        }
    }
    public class ScopeReader<T>:IObjectReader<T> {
        private List<T> rows;
        private ScopeManager manager;
        public ScopeReader(List<T> values,ScopeManager m) {rows=values;manager=m;}
        public int PageSize {get;set;}
        private int reads;
        public int Count {get {reads++;return rows.Count+(manager.Mode=="changed" && reads>1?1:0);}}
        public IList<T> GetRange(int offset,int take) {
            manager.PageCalls++;
            if(manager.Mode=="short") return new List<T>();
            return rows.GetRange(offset,take);
        }
    }
}
'@
$manager=New-Object Microsoft.EnterpriseManagement.Monitoring.ScopeManager
$entityClass=New-Object Microsoft.EnterpriseManagement.Configuration.ManagementPackClass
$entityClass.Name='System.Entity'
foreach ($i in 1..3) {
    $row=New-Object Microsoft.EnterpriseManagement.Monitoring.MonitoringObject
    $row.Id=[Guid]::NewGuid()
    $manager.Rows.Add($row)
}
$scopeArgs=@{
    EntityObjects=$manager;EntityClass=$entityClass;WholeManagementGroupApproved=$true
    ScopeVisibilityVerified=$true;EnumerationQueryContractVerified=$true;MaxObjects=3;PageSize=2
}
function Assert-ScopeFault([scriptblock]$Action,[string]$Code) {
    try {& $Action | Out-Null;throw "Expected $Code"}
    catch {if ($_.Exception.Message -ne $Code) {throw}}
}
$scope=Read-SCOMManagementGroupScope @scopeArgs
if ($scope.ExpectedObjectIds.Count -ne 3 -or $scope.MembershipObjects.Count -ne 3 -or
    -not $scope.MembershipVerified -or $scope.SourceWorkBoundVerified -or $manager.RequestedLimit -ne 4 -or
    $manager.PageCalls -ne 2) {throw 'All-object source signature, max+1 or bounded pages invalid.'}
foreach ($flag in @('WholeManagementGroupApproved','ScopeVisibilityVerified','EnumerationQueryContractVerified')) {
    $calls=$manager.Calls
    $scopeArgs[$flag]=$false
    Assert-ScopeFault {Read-SCOMManagementGroupScope @scopeArgs} 'SCOM_MANAGEMENT_GROUP_SCOPE_UNVERIFIED'
    if ($manager.Calls -ne $calls) {throw 'Unverified scope touched SDK.'}
    $scopeArgs[$flag]=$true
}
$scopeArgs.MaxObjects=2
Assert-ScopeFault {Read-SCOMManagementGroupScope @scopeArgs} 'SCOM_MANAGEMENT_GROUP_SCOPE_OVERFLOW'
$pageCalls=$manager.PageCalls
$scopeArgs.MaxObjects=1
Assert-ScopeFault {Read-SCOMManagementGroupScope @scopeArgs} 'SCOM_MANAGEMENT_GROUP_SCOPE_OVERFLOW'
if ($manager.RequestedLimit -ne 2 -or $manager.PageCalls -ne $pageCalls) {
    throw 'Ignored SDK result limit must reject oversized Count before any page read.'
}
$scopeArgs.MaxObjects=3
$manager.Mode='short'
Assert-ScopeFault {Read-SCOMManagementGroupScope @scopeArgs} 'SCOM_MANAGEMENT_GROUP_PAGE_INCOMPLETE'
$manager.Mode='changed'
Assert-ScopeFault {Read-SCOMManagementGroupScope @scopeArgs} 'SCOM_MANAGEMENT_GROUP_READER_CHANGED_OR_DEADLINE'
$manager.Mode=''
$manager.Rows[1].Id=$manager.Rows[0].Id
Assert-ScopeFault {Read-SCOMManagementGroupScope @scopeArgs} 'SCOM_MANAGEMENT_GROUP_OBJECT_INVALID'
$manager.Rows[1].Id=[Guid]::NewGuid()
$manager.Rows[1].InRoot=$false
Assert-ScopeFault {Read-SCOMManagementGroupScope @scopeArgs} 'SCOM_MANAGEMENT_GROUP_OBJECT_INVALID'
$manager.Rows[1].InRoot=$true
$entityClass.Name='Synthetic.UnapprovedRoot'
Assert-ScopeFault {Read-SCOMManagementGroupScope @scopeArgs} 'SCOM_MANAGEMENT_GROUP_ROOT_CLASS_INVALID'
$entityClass.Name='System.Entity'
$module=Import-Module (Join-Path $PSScriptRoot 'ScomNormalization.psm1') -Force -PassThru
$instant=[DateTimeOffset]::UtcNow
$scanArgs=@{
    EntityObjects=$manager;ManagementGroupId=[Guid]::NewGuid();ScopeKind='managementGroup';EntityClass=$entityClass
    WholeManagementGroupApproved=$true;ScopeVisibilityVerified=$true;EnumerationQueryContractVerified=$true
    Request=@{requestedAtUtc=$instant.ToString('o');metricHistoryStartUtc=$instant.AddDays(-14).ToString('o');priorUnresolvedAlerts=@()}
    ReadActiveAlerts={param($object) @()};ReadAlertById={param($id) @()}
}
$scan=Get-SCOMGenericScan @scanArgs
$completeScan=$scan
if ($scan.scopeKind -cne 'managementGroup' -or $null -ne $scan.groupId -or
    $scan.objects.Count -ne 3 -or $scan.expectedObjectIds.Count -ne 3 -or -not $scan.inventory.complete -or
    $scan.relationships.Count -ne 0 -or $scan.metricAssessment.complete) {throw 'Whole-MG scan provenance/coverage invalid.'}
$scanArgs.GroupId=[Guid]::NewGuid()
$calls=$manager.Calls
Assert-ScopeFault {Get-SCOMGenericScan @scanArgs} 'SCOM_PROFILE_SCOPE_UNVERIFIED'
if ($manager.Calls -ne $calls) {throw 'Mixed group/MG identities triggered collection.'}
$scanArgs.Remove('GroupId')
& $module {
    param($manager)
    function script:Read-SCOMObjectIdPages {
        param($ObjectIds)
        [pscustomobject]@{Objects=@($script:ScopeManager.Rows[0]);DiscoveryComplete=$false}
    }
    $script:ScopeManager=$manager
} $manager
$partial=Get-SCOMGenericScan @scanArgs
if ($partial.inventory.complete -or $partial.expectedObjectIds.Count -ne 3 -or $partial.objects.Count -ne 1) {
    throw 'Scope membership was reduced to successful discovery.'
}
$manager.Rows.Clear()
$empty=Read-SCOMManagementGroupScope @scopeArgs
if ($empty.ExpectedObjectIds.Count -ne 0 -or $empty.MembershipObjects.Count -ne 0) {throw 'Empty verified scope failed.'}
$idCalls=$manager.IdCalls
$scan=Get-SCOMGenericScan @scanArgs
if (-not $scan.inventory.complete -or $scan.objects.Count -ne 0 -or $manager.IdCalls -ne $idCalls) {
    throw 'Empty verified scope must not invoke an unscoped ID reader.'
}
if ($BridgeMode -ne '') {
    $output=$completeScan
    if ($BridgeMode -eq 'partial') {$output=$partial}
    if ($BridgeMode -eq 'empty') {$output=$scan}
    [Console]::Out.Write((@{protocolVersion=1;scan=$output} | ConvertTo-Json -Depth 24 -Compress))
    return
}
Write-Output 'Management-group scope: 16 synthetic cases passed; installed class-reader signature, approval gates, ignored-limit rejection before paging and independent discovery.'
