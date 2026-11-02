[CmdletBinding()]
param([switch]$ReadOneHourSamples)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-SCOMMetricModuleReference {
    param([Parameter(Mandatory)] [object]$Reference)

    $reference=$Reference.PSObject.BaseObject
    $id=[Guid]::Empty
    if (-not [Guid]::TryParse([string]$reference.Id,[ref]$id) -or $id -eq [Guid]::Empty) {
        throw 'SCOM_METRIC_REPORT_MODULE_REFERENCE_INVALID'
    }
    $methods=@($reference.GetType().GetMethods() | Where-Object {
        $_.Name -ceq 'GetElement' -and -not $_.IsStatic -and -not $_.IsGenericMethod -and
        $_.GetParameters().Count -eq 0 -and
        $_.ReturnType.FullName -ceq 'Microsoft.EnterpriseManagement.Configuration.ManagementPackDataSourceModuleType'
    })
    if ($methods.Count -ne 1) {
        return [pscustomobject]@{Supported=$false;Definition=$null}
    }
    $definition=$methods[0].Invoke($reference,(New-Object 'System.Object[]' 0))
    if ($null -eq $definition -or -not $methods[0].ReturnType.IsInstanceOfType($definition) -or
        [Guid]$definition.Id -ne $id -or [string]::IsNullOrWhiteSpace([string]$definition.Name)) {
        throw 'SCOM_METRIC_REPORT_MODULE_RESOLUTION_INVALID'
    }
    [pscustomobject]@{Supported=$true;Definition=$definition}
}

foreach ($name in @('Get-SCOMManagementGroup','Get-SCOMClass','Get-SCRelationship',
    'Get-SCOMRule')) {
    $command=Get-Command $name -ErrorAction Stop
    if ($name -eq 'Get-SCOMRule' -and
        -not $command.Parameters.ContainsKey('Id')) {
        throw 'SCOM_METRIC_REPORT_COMMAND_SIGNATURE_UNSUPPORTED'
    }
    $moduleLookupSupported=$true
}
Import-Module (Join-Path $PSScriptRoot 'ScomSdkReader.psm1') -Force -ErrorAction Stop

$groups=@(Get-SCOMManagementGroup -ErrorAction Stop)
$computers=@(Get-SCOMClass -Name 'Microsoft.Windows.Computer' -ErrorAction Stop)
$operatingSystems=@(Get-SCOMClass -Name 'Microsoft.Windows.OperatingSystem' -ErrorAction Stop)
$hosting=@(Get-SCRelationship -Name 'Microsoft.Windows.ComputerHostsOperatingSystem' -ErrorAction Stop)
if ($groups.Count -ne 1 -or $computers.Count -ne 1 -or
    $operatingSystems.Count -ne 1 -or $hosting.Count -ne 1) {
    throw 'SCOM_METRIC_REPORT_DEFINITION_AMBIGUOUS'
}
$manager=$groups[0].EntityObjects.PSObject.BaseObject
$classType='Microsoft.EnterpriseManagement.Configuration.ManagementPackClass' -as [type]
$optionsType='Microsoft.EnterpriseManagement.Common.ObjectQueryOptions' -as [type]
$objectType='Microsoft.EnterpriseManagement.Monitoring.MonitoringObject' -as [type]
if ($null -eq $classType -or $null -eq $optionsType -or $null -eq $objectType) {
    throw 'SCOM_METRIC_REPORT_SDK_UNAVAILABLE'
}
$methods=@($manager.GetType().GetMethods() | Where-Object {
    $p=$_.GetParameters()
    $_.Name -eq 'GetObjectReader' -and $_.IsGenericMethodDefinition -and
    $_.GetGenericArguments().Count -eq 1 -and $p.Count -eq 2 -and
    $p[0].ParameterType -eq $classType -and $p[1].ParameterType -eq $optionsType
})
if ($methods.Count -ne 1) { throw 'SCOM_METRIC_REPORT_READER_UNSUPPORTED' }
$options=[Activator]::CreateInstance($optionsType)
$arguments=New-Object 'System.Object[]' 2
$arguments[0]=$computers[0].PSObject.BaseObject
$arguments[1]=$options.PSObject.BaseObject
$timer=[Diagnostics.Stopwatch]::StartNew()
$reader=$methods[0].MakeGenericMethod([type[]]@($objectType)).Invoke($manager,$arguments)
if ($null -eq $reader -or $reader.Count -lt 1 -or $reader.Count -gt 3) {
    throw 'SCOM_METRIC_REPORT_LAB_COMPUTER_BUDGET'
}
$reader.PageSize=1
$page=@($reader.GetRange(0,1))
if ($page.Count -ne 1) { throw 'SCOM_METRIC_REPORT_PAGE_INCOMPLETE' }
$links=Read-SCOMDirectRelationshipRows -EntityObjects $manager -SourceId ([Guid]$page[0].Id) `
    -RelationshipClass $hosting[0] -IncludeDerivedRelationships $false -MaxRelationships 100
$targets=@($links.Relationships | Where-Object {
    $_.TargetObject.IsInstanceOf($operatingSystems[0].PSObject.BaseObject)
})
if ($targets.Count -ne 1) { throw 'SCOM_METRIC_REPORT_OS_AMBIGUOUS' }
$series=@($targets[0].TargetObject.GetMonitoringPerformanceData())
if ($series.Count -gt 200) { throw 'SCOM_METRIC_REPORT_SERIES_BUDGET' }
$checks=@(
    @{metric='cpuPercent';objectName='Processor Information';counterName='% Processor Time';instanceName='_Total'},
    @{metric='memoryUsedPercent';objectName='Memory';counterName='PercentMemoryUsed';instanceName=''}
)
$results=New-Object 'System.Collections.Generic.List[object]'
$sdkSignatures=New-Object 'System.Collections.Generic.HashSet[string]'
$historyQueries=0
$endUtc=[DateTime]::UtcNow
$startUtc=$endUtc.AddHours(-1)
foreach ($check in $checks) {
    if ($timer.Elapsed.TotalSeconds -ge 60) { throw 'SCOM_METRIC_REPORT_DEADLINE' }
    $matches=@($series | Where-Object {
        $_.ObjectName -ceq $check.objectName -and $_.CounterName -ceq $check.counterName -and
        $_.InstanceName -ceq $check.instanceName
    })
    if ($matches.Count -gt 1) { throw 'SCOM_METRIC_REPORT_SERIES_AMBIGUOUS' }
    $moduleNames=New-Object 'System.Collections.Generic.List[string]'
    $ruleName=$null
    $sampleSummary=$null
    if ($matches.Count -eq 1) {
        $rules=@(Get-SCOMRule -Id ([Guid]$matches[0].RuleId) -ErrorAction Stop)
        if ($rules.Count -ne 1) { throw 'SCOM_METRIC_REPORT_RULE_AMBIGUOUS' }
        $ruleName=[string]$rules[0].Name
        $sources=@($rules[0].PSObject.BaseObject.DataSourceCollection)
        if ($sources.Count -lt 1 -or $sources.Count -gt 10) {
            throw 'SCOM_METRIC_REPORT_DATASOURCE_BUDGET'
        }
        foreach ($source in $sources) {
            if ($timer.Elapsed.TotalSeconds -ge 60) { throw 'SCOM_METRIC_REPORT_DEADLINE' }
            $reference=$source.PSObject.BaseObject.TypeID.PSObject.BaseObject
            if ($null -eq $reference -or [Guid]$reference.Id -eq [Guid]::Empty) {
                throw 'SCOM_METRIC_REPORT_MODULE_REFERENCE_INVALID'
            }
            foreach ($method in $reference.GetType().GetMethods()) {
                if ($method.Name -ceq 'GetElement') {
                    $sdkSignatures.Add($method.ToString()) | Out-Null
                }
            }
            $resolution=Resolve-SCOMMetricModuleReference -Reference $reference
            if (-not $resolution.Supported) { $moduleLookupSupported=$false; continue }
            $moduleNames.Add([string]$resolution.Definition.Name)
        }
        if ($ReadOneHourSamples) {
            if ($timer.Elapsed.TotalSeconds -ge 60) { throw 'SCOM_METRIC_REPORT_DEADLINE' }
            $historyQueries++
            $values=$matches[0].GetValues($startUtc,$endUtc)
            if ($null -eq $values -or $values.Count -gt 1000) {
                throw 'SCOM_METRIC_REPORT_SAMPLE_RESULT_INVALID_OR_OVER_BUDGET'
            }
            $nullCount=0; $outsideCount=0; $numericCount=0
            $minimum=$null; $maximum=$null
            $earliestUnspecified=$null; $latestUnspecified=$null
            $kinds=@{Utc=0;Local=0;Unspecified=0}
            foreach ($value in $values) {
                if ($null -eq $value -or $value.TimeSampled -isnot [DateTime]) {
                    throw 'SCOM_METRIC_REPORT_SAMPLE_TIMESTAMP_INVALID'
                }
                $kind=$value.TimeSampled.Kind.ToString()
                $kinds[$kind]++
                if ($kind -eq 'Unspecified') {
                    if ($null -eq $earliestUnspecified -or $value.TimeSampled -lt $earliestUnspecified) {
                        $earliestUnspecified=$value.TimeSampled
                    }
                    if ($null -eq $latestUnspecified -or $value.TimeSampled -gt $latestUnspecified) {
                        $latestUnspecified=$value.TimeSampled
                    }
                }
                if ($null -eq $value.SampleValue) { $nullCount++; continue }
                $number=0.0
                if (-not [double]::TryParse(
                    [Convert]::ToString($value.SampleValue,[Globalization.CultureInfo]::InvariantCulture),
                    [Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,
                    [ref]$number) -or [double]::IsNaN($number) -or [double]::IsInfinity($number)) {
                    throw 'SCOM_METRIC_REPORT_SAMPLE_NUMBER_INVALID'
                }
                $numericCount++
                if ($number -lt 0 -or $number -gt 100) { $outsideCount++ }
                if ($null -eq $minimum -or $number -lt $minimum) { $minimum=$number }
                if ($null -eq $maximum -or $number -gt $maximum) { $maximum=$number }
            }
            $sampleSummary=[ordered]@{
                status=$(if ($values.Count -eq 0) {'noObservations'}
                    elseif ($nullCount -gt 0 -or $outsideCount -gt 0) {'invalidOrMissingObservations'}
                    else {'observationsReturned'})
                windowHours=1;observationCount=$values.Count;numericCount=$numericCount
                nullCount=$nullCount;outsidePercentRangeCount=$outsideCount
                timestampKinds=$kinds;minimum=$minimum;maximum=$maximum
                earliestUnspecified=$(if ($null -ne $earliestUnspecified) { $earliestUnspecified.ToString('o') } else { $null })
                latestUnspecified=$(if ($null -ne $latestUnspecified) { $latestUnspecified.ToString('o') } else { $null })
                timestampSemanticsVerified=$false
            }
        }
    }
    $results.Add([pscustomobject]@{
        metric=$check.metric
        status=$(if ($matches.Count -eq 0) {'missing'}
            elseif ($moduleNames.Count -ne $sources.Count) {'moduleLookupUnsupported'} else {'metadataResolved'})
        ruleName=$ruleName
        dataSourceTypes=$moduleNames.ToArray()
        optimizationVerified=$false
        timestampSemanticsVerified=$false
        sampleSummary=$sampleSummary
    })
}
$timer.Stop()
[ordered]@{
    schemaVersion='1.0'
    certificationStatus='notCertified'
    computersQueried=1
    performanceHistoryQueriesExecuted=$historyQueries
    configurationChanged=$false
    moduleLookupSupported=$moduleLookupSupported
    moduleLookupAction=$(if (-not $moduleLookupSupported) {
        'Installed module reference lacks the exact supported GetElement signature. Review reported SDK metadata; metric verification stays blocked.'
    } else { $null })
    ruleSdkMetadataSignatures=@($sdkSignatures | Sort-Object)
    checks=$results.ToArray()
    queryStartUtc=$startUtc.ToString('o')
    queryEndUtc=$endUtc.ToString('o')
    reportUtc=[DateTime]::UtcNow.ToString('o')
    reportLocal=[DateTimeOffset]::Now.ToString('o')
    elapsedMilliseconds=$timer.ElapsedMilliseconds
    limitations=@(
        'Lab-only: at most three visible computers, one selected OS, two metric selectors.',
        'SDK calls can materialize results before local budgets; deadline checks are cooperative.',
        'Module names alone do not verify optimization, effective overrides, timestamps or collection load.',
        'ReadOneHourSamples explicitly opts into at most two one-hour GetValues calls; each materializes before the 1000-observation local check. No 14-day load certification.',
        'No customer object IDs, hostnames, instance names or raw configuration are included.'
    )
} | ConvertTo-Json -Depth 8
