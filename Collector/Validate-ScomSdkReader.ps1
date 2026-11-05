$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSdkReader.psm1') -Force

class SyntheticScomReader {
    [int] $PageSize
    [int] $Count
    [object[]] $Rows
    [bool] $ShortPage
    [int] $Calls
    SyntheticScomReader([object[]] $rows) {
        $this.Rows = $rows
        $this.Count = $rows.Count
    }
    [object[]] GetRange([int] $offset, [int] $take) {
        $this.Calls++
        if ($this.ShortPage) { return @() }
        return @($this.Rows[$offset..($offset + $take - 1)])
    }
}

$ids = @([Guid]'00000000-0000-0000-0000-000000000001',
    [Guid]'00000000-0000-0000-0000-000000000002',
    [Guid]'00000000-0000-0000-0000-000000000003')
$rows = @($ids | ForEach-Object { [pscustomobject]@{ Id = $_ } })
$reader = [SyntheticScomReader]::new($rows)
$result = Read-SCOMReaderPages -Reader $reader -ExpectedIds $ids -PageSize 2
if ($result.Objects.Count -ne 3 -or $reader.Calls -ne 2 -or
    -not $result.DiscoveryComplete -or $result.ScopeMembershipVerified -or $result.SourceWorkBoundVerified) {
    throw 'Synthetic SDK paging/coverage failed.'
}
$partial = Read-SCOMReaderPages -Reader ([SyntheticScomReader]::new(@($rows[0]))) -ExpectedIds $ids
if ($partial.DiscoveryComplete -or $partial.MissingObjectIds.Count -ne 2) {
    throw 'Missing IDs must remain explicitly partial.'
}
function Assert-Rejected([scriptblock] $Action, [string] $Code) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -ne $Code) { throw }
        return
    }
    throw "Expected rejection: $Code"
}
Assert-Rejected { Read-SCOMReaderPages -Reader $reader -ExpectedIds @($ids[0], $ids[0]) } 'SCOM_READER_IDS_INVALID'
Assert-Rejected { Read-SCOMReaderPages -Reader $reader -ExpectedIds @([Guid]::Empty) } 'SCOM_READER_IDS_INVALID'
Assert-Rejected { Read-SCOMReaderPages -Reader $reader -ExpectedIds @($ids[0]) } 'SCOM_READER_RESULT_OVERFLOW'
Assert-Rejected {
    Read-SCOMReaderPages -Reader ([SyntheticScomReader]::new(@($rows[0], $rows[0]))) -ExpectedIds $ids
} 'SCOM_READER_OBJECT_OUTSIDE_IDS_OR_DUPLICATE'
Assert-Rejected {
    Read-SCOMReaderPages -Reader ([SyntheticScomReader]::new(@([pscustomobject]@{ Id = [Guid]::NewGuid() }))) -ExpectedIds $ids
} 'SCOM_READER_OBJECT_OUTSIDE_IDS_OR_DUPLICATE'
$short = [SyntheticScomReader]::new($rows)
$short.ShortPage = $true
Assert-Rejected { Read-SCOMReaderPages -Reader $short -ExpectedIds $ids } 'SCOM_READER_PAGE_INCOMPLETE'

if ('Microsoft.EnterpriseManagement.Monitoring.IMonitoringObjectsManagement' -as [type]) {
    throw 'Synthetic signature test must run in a separate process without the real SCOM SDK.'
}
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
namespace Microsoft.EnterpriseManagement.Common {
    public enum ObjectPropertyRetrievalBehavior { None, All }
    public class ObjectQueryOptions {
        public int MaxResultCount { get; set; }
        public ObjectPropertyRetrievalBehavior DefaultPropertyRetrievalBehavior { get; set; }
    }
    public interface IObjectReader<T> {
        int PageSize { get; set; }
        int Count { get; }
        IList<T> GetRange(int offset, int count);
    }
}
namespace Microsoft.EnterpriseManagement.Monitoring {
    using Microsoft.EnterpriseManagement.Common;
    public class MonitoringObject { public Guid Id { get; set; } }
    public interface IMonitoringObjectsManagement {
        IObjectReader<T> GetObjectReader<T>(ICollection<Guid> ids, ObjectQueryOptions options);
    }
    public class SyntheticManager : IMonitoringObjectsManagement {
        public int Calls { get; private set; }
        public int RequestedLimit { get; private set; }
        public IObjectReader<T> GetObjectReader<T>(ICollection<Guid> ids, ObjectQueryOptions options) {
            if (options.DefaultPropertyRetrievalBehavior != ObjectPropertyRetrievalBehavior.All)
                throw new InvalidOperationException("Discovery must explicitly retrieve properties.");
            Calls++;
            RequestedLimit = options.MaxResultCount;
            var values = new List<T>();
            foreach (var id in ids) values.Add((T)(object)new MonitoringObject { Id = id });
            return new SyntheticReader<T>(values);
        }
    }
    public class SyntheticReader<T> : IObjectReader<T> {
        private readonly List<T> rows;
        public SyntheticReader(List<T> values) { rows = values; }
        public int PageSize { get; set; }
        public int Count { get { return rows.Count; } }
        public IList<T> GetRange(int offset, int count) { return rows.GetRange(offset, count); }
    }
}
'@
$manager = New-Object Microsoft.EnterpriseManagement.Monitoring.SyntheticManager
$read = Read-SCOMObjectIdPages -EntityObjects $manager -ObjectIds $ids -PageSize 2 -MaxObjects 3
if ($read.Objects.Count -ne 3 -or $manager.Calls -ne 1 -or $manager.RequestedLimit -ne 4) {
    throw 'Verified generic signature binding and overflow sentinel failed.'
}
Assert-Rejected { Read-SCOMObjectIdPages -EntityObjects $manager -ObjectIds $ids -MaxObjects 2 } 'SCOM_READER_ID_BUDGET'
Assert-Rejected { Read-SCOMObjectIdPages -EntityObjects $manager -ObjectIds @($ids[0], $ids[0]) } 'SCOM_READER_IDS_INVALID'
if ($manager.Calls -ne 1) { throw 'Invalid IDs must not execute a source reader call.' }
Write-Output 'SDK reader validation passed: 11 synthetic cases; no SCOM connection or real SDK invocation.'
