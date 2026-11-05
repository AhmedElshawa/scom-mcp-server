$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomMetricProfile.psm1') -Force
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
public class MetricProfilePoint {
    public DateTime TimeSampled;
    public object SampleValue;
}
public class MetricProfileSeries {
    public string ObjectName="Synthetic";
    public string CounterName="Count";
    public string InstanceName="";
    public string RuleId="synthetic-rule";
    public List<MetricProfilePoint> Points=new List<MetricProfilePoint>();
    public object[] GetValues(DateTime start, DateTime end) { return Points.ToArray(); }
}
public class MetricProfileObject {
    public Guid Id=Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    public MetricProfileSeries Series=new MetricProfileSeries();
    public bool Missing;
    public object[] GetMonitoringPerformanceData() { return Missing ? new object[0] : new object[] { Series }; }
}
'@
$obj=New-Object MetricProfileObject
$end=[DateTimeOffset]'2026-11-01T08:00:00Z'
$start=$end.AddDays(-14)
$point=New-Object MetricProfilePoint
$point.TimeSampled=$end.UtcDateTime
$point.SampleValue=0
$obj.Series.Points.Add($point)
$mapping=[pscustomobject]@{
    metric='count'; unit='count'; objectName='Synthetic'; counterName='Count'; instanceName=''
    verified=$true; optimized=$false; unspecifiedTimestampIsUtc=$false; minValue=0; maxValue=100
}
function Read { Read-SCOMSelectedMetricSeries -Object $obj -Mappings @($mapping) -StartUtc $start -EndUtc $end @args }
$result=Read
if (-not $result.Complete -or $result.Series[0].observations[0].value -ne 0 -or
    $result.Series[0].observations[0].optimized) { throw 'Raw zero/optimized metric contract lost.' }
$point.SampleValue=$null
$partial=Read
if ($partial.Complete -or $partial.Series[0].observations.Count -ne 0 -or $partial.Series[0].complete) {
    throw 'Null series was partially used instead of wholly excluded.'
}
$point.SampleValue=2
$excluded=Read -MaxSamples 0
if ($excluded.Complete -or $excluded.Series[0].observations.Count -ne 0) { throw 'Budget overflow not excluded as whole series.' }
$obj.Missing=$true
if ((Read).Complete) { throw 'Missing selected series self-certified complete.' }
$obj.Missing=$false
function Reject([scriptblock]$Action,[string]$Code) {
    try { & $Action | Out-Null }
    catch { if ($_.Exception.Message -eq $Code) { return }; throw }
    throw "Expected rejection: $Code"
}
$mapping.verified=$false
Reject { Read } 'SCOM_METRIC_MAPPING_UNVERIFIED_OR_INVALID'
$mapping.verified=$true
$point.SampleValue=[double]::NaN
Reject { Read } 'SCOM_METRIC_VALUE_INVALID'
$point.SampleValue=2
$point.TimeSampled=[DateTime]::SpecifyKind($end.UtcDateTime,[DateTimeKind]::Unspecified)
Reject { Read } 'SCOM_METRIC_TIMESTAMP_UNVERIFIED'
$mapping.unspecifiedTimestampIsUtc=$true
if (-not (Read).Complete) { throw 'Verified timestamp treatment failed.' }
$point.TimeSampled=$end.UtcDateTime.AddSeconds(1)
Reject { Read } 'SCOM_METRIC_TIMESTAMP_OUTSIDE_WINDOW'
$point.TimeSampled=$end.UtcDateTime
Reject { Read-SCOMSelectedMetricSeries $obj @($mapping,$mapping) $start $end } 'SCOM_METRIC_MAPPING_DUPLICATE'
Write-Output 'Selected metric profile validation passed: 10 synthetic cases; raw zeros, explicit units/flags, missing/null whole-series exclusion, budget and timestamps.'
