$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSdkReader.psm1') -Force
if ('Microsoft.EnterpriseManagement.Monitoring.IMonitoringObjectsManagement' -as [type]) {
    throw 'Synthetic relationship test requires a process without real SDK assemblies.'
}
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
namespace Microsoft.EnterpriseManagement.Common {
    public class ObjectQueryOptions {
        private int maxResultCount;
        public int MaxResultCount {
            get { return maxResultCount; }
            set {
                if (value <= 0) throw new ArgumentOutOfRangeException("value");
                maxResultCount=value;
            }
        }
    }
    public enum TraversalDepth { OneLevel, Recursive }
    public class EnterpriseManagementRelationshipObject<T> {
        public Guid Id { get; set; }
        public T SourceObject { get; set; }
        public T TargetObject { get; set; }
    }
}
namespace Microsoft.EnterpriseManagement.Configuration {
    public class ManagementPackRelationship { public string Name = "Microsoft.SystemCenter.InstanceGroupContainsEntities"; }
    public class ManagementPackClass { public string Name = "Microsoft.SystemCenter.InstanceGroup"; }
    public enum DerivedClassTraversalDepth { None, Recursive }
}
namespace Microsoft.EnterpriseManagement.Monitoring {
    using Microsoft.EnterpriseManagement.Common;
    using Microsoft.EnterpriseManagement.Configuration;
    public class MonitoringObject {
        public Guid Id { get; set; }
        public bool IsInstanceOf(ManagementPackClass group) { return true; }
    }
    public interface IMonitoringObjectsManagement {
        IList<EnterpriseManagementRelationshipObject<T>> GetRelationshipObjectsWhereSource<T>(
            Guid source, ManagementPackRelationship relationship, DerivedClassTraversalDepth derived,
            TraversalDepth traversal, ObjectQueryOptions options);
    }
    public class SyntheticRelationshipManager : IMonitoringObjectsManagement {
        public int Calls, Limit;
        public bool Exact;
        public string Mode = "valid";
        public IList<EnterpriseManagementRelationshipObject<T>> GetRelationshipObjectsWhereSource<T>(
            Guid source, ManagementPackRelationship relationship, DerivedClassTraversalDepth derived,
            TraversalDepth traversal, ObjectQueryOptions options) {
            Calls++; Limit = options.MaxResultCount;
            if (options.MaxResultCount != 0)
                throw new NotImplementedException("MaxResultCount is not supported with this method.");
            if (derived != (Exact ? DerivedClassTraversalDepth.None : DerivedClassTraversalDepth.Recursive) || traversal != TraversalDepth.OneLevel)
                throw new Exception("Unexpected traversal");
            if (Mode == "null") return null;
            var result = new List<EnterpriseManagementRelationshipObject<T>>();
            if (Mode == "empty") return result;
            var row = new EnterpriseManagementRelationshipObject<T> {
                Id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
                SourceObject = (T)(object)new MonitoringObject { Id = Mode == "outside" ? Guid.NewGuid() : source },
                TargetObject = (T)(object)new MonitoringObject { Id = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb") }
            };
            result.Add(row);
            if (Mode == "duplicate" || Mode == "overflow") result.Add(row);
            return result;
        }
    }
}
'@
$manager = New-Object Microsoft.EnterpriseManagement.Monitoring.SyntheticRelationshipManager
$relationship = New-Object Microsoft.EnterpriseManagement.Configuration.ManagementPackRelationship
$source = [Guid]'11111111-1111-4111-8111-111111111111'
$result = Read-SCOMDirectRelationshipRows -EntityObjects $manager -SourceId $source -RelationshipClass $relationship -MaxRelationships 2
if ($result.Relationships.Count -ne 1 -or $manager.Limit -ne 0 -or $manager.Calls -ne 1 -or
    $result.ScopeMembershipVerified -or $result.SourceWorkBoundVerified) { throw 'Relationship reader contract failed.' }
$manager.Mode = 'empty'
$empty = Read-SCOMDirectRelationshipRows -EntityObjects $manager -SourceId $source -RelationshipClass $relationship
if ($empty.Relationships.Count -ne 0 -or $empty.ScopeMembershipVerified) { throw 'Empty relationship result self-certified scope.' }
function Reject([string]$Mode, [int]$Limit, [string]$Code) {
    $manager.Mode = $Mode
    try {
        Read-SCOMDirectRelationshipRows -EntityObjects $manager -SourceId $source -RelationshipClass $relationship -MaxRelationships $Limit | Out-Null
    }
    catch { if ($_.Exception.Message -eq $Code) { return }; throw }
    throw "Expected rejection: $Code"
}
Reject 'outside' 2 'SCOM_RELATIONSHIP_ROW_INVALID'
Reject 'duplicate' 2 'SCOM_RELATIONSHIP_ROW_INVALID'
Reject 'overflow' 1 'SCOM_RELATIONSHIP_RESULT_OVERFLOW'
Reject 'null' 2 'SCOM_RELATIONSHIP_RESULT_OVERFLOW'
$manager.Mode = 'valid'
$manager.Exact = $true
$exact = Read-SCOMDirectRelationshipRows -EntityObjects $manager -SourceId $source -RelationshipClass $relationship `
    -IncludeDerivedRelationships $false
if ($exact.Relationships.Count -ne 1) { throw 'Exact relationship class read failed.' }
$manager.Exact = $false
$groupClass = New-Object Microsoft.EnterpriseManagement.Configuration.ManagementPackClass
$members = Read-SCOMDirectGroupMembers -EntityObjects $manager -GroupId $source `
    -MembershipRelationshipClass $relationship -GroupClass $groupClass -MembershipQueryContractVerified $true
if (-not $members.Complete -or $members.Members.Count -ne 1 -or -not $members.Members[0].IsGroup -or
    $members.SourceWorkBoundVerified) { throw 'SDK direct group membership/classification failed.' }
try {
    Read-SCOMDirectGroupMembers -EntityObjects $manager -GroupId $source `
        -MembershipRelationshipClass $relationship -GroupClass $groupClass -MembershipQueryContractVerified $false | Out-Null
    throw 'Unverified query contract accepted.'
}
catch { if ($_.Exception.Message -ne 'SCOM_GROUP_QUERY_CONTRACT_UNVERIFIED') { throw } }
Write-Output 'Relationship SDK reader validation passed: 9 synthetic cases; untouched result-limit default with zero setter rejected, local overflow rejection, exact/derived classes and membership.'
