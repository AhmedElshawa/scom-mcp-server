$ErrorActionPreference='Stop'
$path=Join-Path $PSScriptRoot 'Get-SCOMMetricCompatibilityReport.ps1'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
if ($errors.Count -gt 0) { throw 'Metric report syntax invalid.' }
$commands=@($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst]
},$true) | ForEach-Object { $_.GetCommandName() } | Sort-Object -Unique)
foreach ($command in $commands) {
    if ($command -notin @('Set-StrictMode','Get-Command','Import-Module','Join-Path',
        'Get-SCOMManagementGroup','Get-SCOMClass','Get-SCRelationship','New-Object',
        'Where-Object','Read-SCOMDirectRelationshipRows','Get-SCOMRule','Get-SCOMModuleType','ConvertTo-Json',
        'Out-Null','Sort-Object','Resolve-SCOMMetricModuleReference')) {
        throw "Unexpected metric report command: $command"
    }
}
$calls=@($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.InvokeMemberExpressionAst]
},$true))
$historyCalls=@($calls | Where-Object { $_.Member.Value -eq 'GetValues' })
if ($historyCalls.Count -ne 1) {
    throw 'Expected exactly one guarded history call site.'
}
$parent=$historyCalls[0].Parent
$guarded=$false
while ($null -ne $parent) {
    if ($parent -is [Management.Automation.Language.IfStatementAst] -and
        $parent.Clauses[0].Item1.Extent.Text -eq '$ReadOneHourSamples') { $guarded=$true; break }
    $parent=$parent.Parent
}
if (-not $guarded) { throw 'History reads require explicit opt-in.' }
if (-not (Get-Command Get-SCOMManagementGroup -ErrorAction SilentlyContinue)) {
    $rejected=$false
    try { & $path | Out-Null }
    catch [System.Management.Automation.CommandNotFoundException] { $rejected=$true }
    if (-not $rejected) { throw 'Missing SCOM commands must explicitly reject.' }
}
$resolver=$ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Resolve-SCOMMetricModuleReference'
},$true)
if ($null -eq $resolver) { throw 'Missing module resolver.' }
. ([scriptblock]::Create($resolver.Extent.Text))
Add-Type -TypeDefinition @'
using System;
namespace Microsoft.EnterpriseManagement.Configuration {
    public class ManagementPackDataSourceModuleType {
        public Guid Id { get; set; }
        public string Name { get; set; }
    }
}
public class SyntheticMetricModuleReference {
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool WrongId { get; set; }
    public bool Fail { get; set; }
    public Microsoft.EnterpriseManagement.Configuration.ManagementPackDataSourceModuleType GetElement() {
        if (Fail) throw new InvalidOperationException("Synthetic resolution failure");
        return new Microsoft.EnterpriseManagement.Configuration.ManagementPackDataSourceModuleType {
            Id = WrongId ? Guid.NewGuid() : Id, Name = "Synthetic.DataSource"
        };
    }
}
public class SyntheticUnsupportedModuleReference {
    public Guid Id { get; set; } = Guid.NewGuid();
}
public class SyntheticMetricSample {
    public DateTime TimeSampled { get; set; }
    public object SampleValue { get; set; }
}
public class SyntheticMetricSampleSeries {
    public System.Collections.Generic.List<SyntheticMetricSample> Values =
        new System.Collections.Generic.List<SyntheticMetricSample>();
    public int Calls;
    public System.Collections.Generic.List<SyntheticMetricSample> GetValues(DateTime start, DateTime end) {
        if (end.Kind != DateTimeKind.Utc || start != end.AddHours(-1))
            throw new InvalidOperationException("Expected UTC one-hour bounds.");
        Calls++;
        return Values;
    }
}
'@
$reference=New-Object SyntheticMetricModuleReference
$resolved=Resolve-SCOMMetricModuleReference $reference
if (-not $resolved.Supported -or $resolved.Definition.Name -ne 'Synthetic.DataSource') {
    throw 'Supported runtime module signature was not resolved.'
}
$unsupported=Resolve-SCOMMetricModuleReference (New-Object SyntheticUnsupportedModuleReference)
if ($unsupported.Supported -or $null -ne $unsupported.Definition) {
    throw 'Unsupported resolver must remain explicitly blocked.'
}
$reference.WrongId=$true
$rejected=$false
try { Resolve-SCOMMetricModuleReference $reference | Out-Null }
catch { if ($_.Exception.Message -ne 'SCOM_METRIC_REPORT_MODULE_RESOLUTION_INVALID') { throw }; $rejected=$true }
if (-not $rejected) { throw 'Mismatched module identity must reject.' }
$reference.WrongId=$false; $reference.Fail=$true
$rejected=$false
try { Resolve-SCOMMetricModuleReference $reference | Out-Null }
catch { if ($_.Exception.ToString() -notmatch 'Synthetic resolution failure') { throw }; $rejected=$true }
if (-not $rejected) { throw 'Resolver errors must propagate, not become metadata success.' }
$reference.Fail=$false; $reference.Id=[Guid]::Empty
$rejected=$false
try { Resolve-SCOMMetricModuleReference $reference | Out-Null }
catch { if ($_.Exception.Message -ne 'SCOM_METRIC_REPORT_MODULE_REFERENCE_INVALID') { throw }; $rejected=$true }
if (-not $rejected) { throw 'Empty module identity must reject.' }
$historyGuard=$parent
$historyBody=[scriptblock]::Create($historyGuard.Clauses[0].Item2.Extent.Text.Trim().Substring(
    1,$historyGuard.Clauses[0].Item2.Extent.Text.Trim().Length-2))
$source=New-Object SyntheticMetricSampleSeries
$source.Values.Add((New-Object SyntheticMetricSample -Property @{
    TimeSampled=[DateTime]::SpecifyKind([DateTime]::Now,[DateTimeKind]::Unspecified);SampleValue=0
}))
$source.Values.Add((New-Object SyntheticMetricSample -Property @{
    TimeSampled=[DateTime]::UtcNow;SampleValue=$null
}))
$source.Values.Add((New-Object SyntheticMetricSample -Property @{
    TimeSampled=[DateTime]::Now;SampleValue=101
}))
$matches=@($source);$timer=[Diagnostics.Stopwatch]::StartNew()
$historyQueries=0;$endUtc=[DateTime]::UtcNow;$startUtc=$endUtc.AddHours(-1)
. $historyBody
if ($source.Calls -ne 1 -or $historyQueries -ne 1 -or
    $sampleSummary.observationCount -ne 3 -or $sampleSummary.numericCount -ne 2 -or
    $sampleSummary.nullCount -ne 1 -or $sampleSummary.minimum -ne 0 -or
    $sampleSummary.maximum -ne 101 -or $sampleSummary.outsidePercentRangeCount -ne 1 -or
    $sampleSummary.timestampKinds.Utc -ne 1 -or $sampleSummary.timestampKinds.Local -ne 1 -or
    $sampleSummary.timestampKinds.Unspecified -ne 1 -or $sampleSummary.timestampSemanticsVerified) {
    throw 'One-hour summary must preserve zero, nulls, ranges and timestamp uncertainty.'
}
if ($sampleSummary.earliestUnspecified -ne $source.Values[0].TimeSampled.ToString('o') -or
    $sampleSummary.latestUnspecified -ne $source.Values[0].TimeSampled.ToString('o') -or
    $sampleSummary.earliestUnspecified -match '(Z|[+-]\d\d:\d\d)$') {
    throw 'Unspecified bounds must preserve the SDK clock without assigning a time zone.'
}
$source.Values.Clear()
. $historyBody
if ($sampleSummary.status -ne 'noObservations' -or $sampleSummary.observationCount -ne 0 -or $null -ne $sampleSummary.minimum -or
    $null -ne $sampleSummary.maximum) { throw 'Empty observations must not manufacture zero ranges.' }
if ($null -ne $sampleSummary.earliestUnspecified -or $null -ne $sampleSummary.latestUnspecified) {
    throw 'Empty observations must not manufacture timestamp bounds.'
}
$source.Values.Add((New-Object SyntheticMetricSample -Property @{
    TimeSampled=[DateTime]::UtcNow;SampleValue=[double]::NaN
}))
$rejected=$false
try { . $historyBody } catch {
    if ($_.Exception.Message -ne 'SCOM_METRIC_REPORT_SAMPLE_NUMBER_INVALID') { throw }
    $rejected=$true
}
if (-not $rejected) { throw 'Invalid sample numbers must reject.' }
Write-Output 'Metric report validation passed: explicit opt-in, 5 resolver cases and one-hour zero/null/range/timestamp/empty/invalid sample checks; live SDK path not certified.'
