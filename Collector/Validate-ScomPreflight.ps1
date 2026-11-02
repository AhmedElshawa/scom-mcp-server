$ErrorActionPreference='Stop'
$path=Join-Path $PSScriptRoot 'Test-SCOMPreflight.ps1'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
if ($errors.Count -gt 0) { throw 'Preflight syntax invalid.' }
$commands=@($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst]
},$true) | ForEach-Object { $_.GetCommandName() } | Sort-Object -Unique)
foreach ($command in $commands) {
    if ($command -notin @('Set-StrictMode','Add-PreflightCheck','New-Object','Get-Module','Where-Object','Sort-Object','ForEach-Object','ConvertTo-Json')) {
        throw "Unexpected preflight command: $command"
    }
}
$report=& $path | ConvertFrom-Json
if ($report.certificationStatus -ne 'notCertified' -or -not $report.metadataOnly -or
    $report.sourceQueriesExecuted -ne 0 -or $report.configurationChanged -or
    $report.checks.Count -ne 7 -or $report.requiredAcceptance.Count -ne 5) {
    throw 'Preflight must preserve explicit incomplete acceptance.'
}
if (-not ('Microsoft.EnterpriseManagement.Monitoring.IMonitoringObjectsManagement' -as [type]) -and
    ($report.status -ne 'blocked' -or @($report.checks | Where-Object {
        $_.code -eq 'SDK_TYPES_LOADED' -and $_.status -eq 'blocked' -and $_.action
    }).Count -ne 1)) { throw 'Missing SDK must be blocked with actionable guidance.' }
if ('Microsoft.EnterpriseManagement.Monitoring.IMonitoringObjectsManagement' -as [type]) {
    throw 'Run this validation in an isolated shell without installed SDK types.'
}
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
namespace Microsoft.EnterpriseManagement.Configuration {
    public class ManagementPackClass {}
    public class ManagementPackRelationship {}
    public enum DerivedClassTraversalDepth { None, Recursive }
}
namespace Microsoft.EnterpriseManagement.Common {
    public enum ObjectPropertyRetrievalBehavior { None, All }
    public class ObjectQueryOptions {
        public ObjectPropertyRetrievalBehavior DefaultPropertyRetrievalBehavior { get; set; }
    }
    public enum TraversalDepth { OneLevel }
}
namespace Microsoft.EnterpriseManagement.Monitoring {
    using Microsoft.EnterpriseManagement.Configuration;
    using Microsoft.EnterpriseManagement.Common;
    public class MonitoringObject {}
    public interface IInheritedReaders {
        object GetObjectReader<T>(ManagementPackClass definition, ObjectQueryOptions options);
        object GetObjectReader<T>(ICollection<Guid> ids, ObjectQueryOptions options);
    }
    public interface IMonitoringObjectsManagement : IInheritedReaders {
        object GetRelationshipObjectsWhereSource<T>(Guid source, ManagementPackRelationship definition,
            DerivedClassTraversalDepth derived, TraversalDepth traversal, ObjectQueryOptions options);
    }
}
'@
$positiveReport=& $path | ConvertFrom-Json
foreach ($code in @('SDK_TYPES_LOADED','CLASS_READER_SIGNATURE','ID_READER_SIGNATURE','SOURCE_RELATIONSHIP_SIGNATURE','ALL_PROPERTY_RETRIEVAL_SUPPORTED')) {
    if (@($positiveReport.checks | Where-Object {
        $_.code -eq $code -and $_.status -eq 'pass' -and $null -eq $_.action
    }).Count -ne 1) { throw "Supported synthetic metadata not recognized: $code" }
}
if ($positiveReport.certificationStatus -ne 'notCertified' -or
    -not $positiveReport.metadataOnly -or $positiveReport.sourceQueriesExecuted -ne 0 -or
    $positiveReport.configurationChanged -or $positiveReport.status -ne 'blocked') {
    throw 'Synthetic SDK metadata must not bypass host/module checks or certify deployment.'
}
Write-Output 'Preflight validation passed: command allowlist, JSON contract, missing-SDK refusal and inherited supported metadata without source invocation or certification.'
