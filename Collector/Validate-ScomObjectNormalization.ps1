$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSdkReader.psm1') -Force
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
public class SyntheticProperty { public string Name { get; set; } }
public class SyntheticClass {
    public string Name { get; set; }
    public IList<SyntheticClass> GetBaseTypes() { return new [] { new SyntheticClass { Name="Synthetic.Base" } }; }
}
public class SyntheticNormalizedObject {
    public Guid Id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    public string DisplayName = "Synthetic server";
    public string HealthState = "Success";
    public bool AmbiguousClass, DuplicateProperty;
    public object Value = 0;
    public IList<SyntheticClass> GetMostDerivedClasses() {
        var row = new SyntheticClass { Name="Synthetic.Computer" };
        return AmbiguousClass ? new [] { row, row } : new [] { row };
    }
    public IList<SyntheticProperty> GetProperties() {
        var row = new SyntheticProperty { Name="Count" };
        return DuplicateProperty ? new [] { row, row } : new [] { row };
    }
    public SyntheticValue this[SyntheticProperty p] { get { return new SyntheticValue { Value=Value }; } }
}
public class SyntheticValue { public object Value { get; set; } }
'@
$object = New-Object SyntheticNormalizedObject
$now = [DateTimeOffset]'2026-11-01T08:00:00Z'
$result = ConvertTo-SCOMObservedObject -Object $object -ObservedAtUtc $now -AllowedProperties @('Count', 'Missing')
if ($result.health -ne 'healthy' -or $result.properties['Count'] -ne '0' -or
    $null -ne $result.properties['Missing'] -or $result.baseClasses.Count -ne 1) { throw 'Object normalization lost raw zero or missing property.' }
$count = 1
foreach ($state in @('Warning', 'Error', 'Uninitialized')) {
    $object.HealthState = $state
    ConvertTo-SCOMObservedObject -Object $object -ObservedAtUtc $now | Out-Null
    $count++
}
$object.HealthState = 'Success'
function Reject([scriptblock]$Action, [string]$Code) {
    try { & $Action | Out-Null }
    catch { if ($_.Exception.Message -eq $Code) { return }; throw }
    throw "Expected rejection: $Code"
}
$object.AmbiguousClass = $true
Reject { ConvertTo-SCOMObservedObject $object $now } 'SCOM_NORMALIZATION_CLASS_AMBIGUOUS'
$object.AmbiguousClass = $false
$object.DuplicateProperty = $true
Reject { ConvertTo-SCOMObservedObject $object $now -AllowedProperties Count } 'SCOM_NORMALIZATION_PROPERTY_AMBIGUOUS'
$object.DuplicateProperty = $false
$object.Value = [double]::NaN
Reject { ConvertTo-SCOMObservedObject $object $now -AllowedProperties Count } 'SCOM_NORMALIZATION_PROPERTY_NUMBER_INVALID'
$object.Value = @{ private='not a scalar' }
Reject { ConvertTo-SCOMObservedObject $object $now -AllowedProperties Count } 'SCOM_NORMALIZATION_PROPERTY_TYPE_INVALID'
$object.Value = [DateTime]::SpecifyKind([DateTime]'2026-11-01', [DateTimeKind]::Unspecified)
Reject { ConvertTo-SCOMObservedObject $object $now -AllowedProperties Count } 'SCOM_NORMALIZATION_PROPERTY_TIME_INVALID'
$object.HealthState = 'not-a-health-state'
Reject { ConvertTo-SCOMObservedObject $object $now } 'SCOM_NORMALIZATION_HEALTH_INVALID'
Reject { ConvertTo-SCOMObservedObject $object $now -AllowedProperties @('Count','Count') } 'SCOM_NORMALIZATION_INPUT_INVALID'
Write-Output 'Object normalization validation passed: 11 synthetic cases; explicit class/health/property mapping, no SDK or environment access.'
