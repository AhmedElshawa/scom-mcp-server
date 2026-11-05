Set-StrictMode -Version Latest

function Read-SCOMSelectedMetricSeries {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object] $Object,
        [Parameter(Mandatory)] [object[]] $Mappings,
        [Parameter(Mandatory)] [DateTimeOffset] $StartUtc,
        [Parameter(Mandatory)] [DateTimeOffset] $EndUtc,
        [ValidateRange(1, 100000)] [int] $MaxSeriesSamples = 10000,
        [ValidateRange(0, 1000000)] [int] $MaxSamples = 50000
    )

    if ($StartUtc.Offset -ne [TimeSpan]::Zero -or $EndUtc.Offset -ne [TimeSpan]::Zero -or
        $EndUtc -eq [DateTimeOffset]::MinValue -or $StartUtc -ne $EndUtc.AddDays(-14) -or
        $Mappings.Count -gt 64) { throw 'SCOM_METRIC_PROFILE_INPUT_INVALID' }
    $id = [Guid]::Empty
    if (-not [Guid]::TryParse([string]$Object.Id, [ref]$id) -or $id -eq [Guid]::Empty) {
        throw 'SCOM_METRIC_OBJECT_INVALID'
    }
    $outputs = New-Object 'System.Collections.Generic.List[object]'
    $selectors = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($mapping in $Mappings) {
        foreach ($field in @('metric','unit','objectName','counterName','instanceName')) {
            if ($null -eq $mapping.PSObject.Properties[$field] -or $mapping.$field -isnot [string] -or
                ($field -ne 'instanceName' -and [string]::IsNullOrWhiteSpace($mapping.$field)) -or
                $mapping.$field.Length -gt 128) { throw 'SCOM_METRIC_MAPPING_INVALID' }
        }
        if ($mapping.verified -isnot [bool] -or -not $mapping.verified -or
            $mapping.optimized -isnot [bool] -or $mapping.unspecifiedTimestampIsUtc -isnot [bool] -or
            $mapping.unit.Length -gt 64 -or
            $mapping.minValue -is [bool] -or $mapping.maxValue -is [bool] -or
            $mapping.minValue -isnot [ValueType] -or $mapping.maxValue -isnot [ValueType] -or
            [double]::IsNaN([double]$mapping.minValue) -or [double]::IsInfinity([double]$mapping.minValue) -or
            [double]::IsNaN([double]$mapping.maxValue) -or [double]::IsInfinity([double]$mapping.maxValue) -or
            [double]$mapping.minValue -gt [double]$mapping.maxValue) { throw 'SCOM_METRIC_MAPPING_UNVERIFIED_OR_INVALID' }
        $selector = @($mapping.objectName,$mapping.counterName,$mapping.instanceName) | ConvertTo-Json -Compress
        if (-not $selectors.Add($selector)) { throw 'SCOM_METRIC_MAPPING_DUPLICATE' }
    }
    if ($Mappings.Count -eq 0) {
        return [pscustomobject]@{ Series=@(); Complete=$false; Reason='VERIFIED_METRIC_PROFILE_NOT_CONFIGURED'; SampleCount=0 }
    }
    # SDK GetValues materializes a series: these limits protect local output,
    # not the source database. Verified source-work acceptance remains required.
    $series = @($Object.GetMonitoringPerformanceData())
    if ($series.Count -gt 10000) { throw 'SCOM_METRIC_SERIES_DISCOVERY_BUDGET' }
    $keys = New-Object 'System.Collections.Generic.HashSet[string]'
    $total = 0
    $complete = $true
    foreach ($mapping in $Mappings) {
        $matches = @($series | Where-Object {
            $_.ObjectName -ceq $mapping.objectName -and $_.CounterName -ceq $mapping.counterName -and
            $_.InstanceName -ceq $mapping.instanceName
        })
        if ($matches.Count -eq 0) { $complete=$false; continue }
        foreach ($source in $matches) {
            $rule = [string]$source.RuleId
            if ([string]::IsNullOrWhiteSpace($rule) -or $rule.Length -gt 500) { throw 'SCOM_METRIC_RULE_INVALID' }
            $key = @($mapping.metric,$rule) | ConvertTo-Json -Compress
            if (-not $keys.Add($key)) { throw 'SCOM_METRIC_SERIES_AMBIGUOUS' }
            $values = $source.GetValues($StartUtc.UtcDateTime, $EndUtc.UtcDateTime)
            if ($null -eq $values) { throw 'SCOM_METRIC_VALUES_UNAVAILABLE' }
            $reason = $null
            $rows = New-Object 'System.Collections.Generic.List[object]'
            if ($values.Count -gt $MaxSeriesSamples -or $total + $values.Count -gt $MaxSamples) {
                $reason = 'SCOM_METRIC_SERIES_BUDGET_EXCLUDED'
            }
            else {
                foreach ($value in $values) {
                    if ($null -eq $value -or $value.TimeSampled -isnot [DateTime]) { throw 'SCOM_METRIC_TIMESTAMP_INVALID' }
                    $at = $value.TimeSampled
                    if ($at.Kind -eq [DateTimeKind]::Unspecified) {
                        if (-not $mapping.unspecifiedTimestampIsUtc) { throw 'SCOM_METRIC_TIMESTAMP_UNVERIFIED' }
                        $at = [DateTime]::SpecifyKind($at, [DateTimeKind]::Utc)
                    }
                    $at = [DateTimeOffset]$at.ToUniversalTime()
                    if ($at -lt $StartUtc -or $at -gt $EndUtc) { throw 'SCOM_METRIC_TIMESTAMP_OUTSIDE_WINDOW' }
                    if ($null -eq $value.SampleValue) { $reason='SCOM_METRIC_NULL_OBSERVATIONS'; continue }
                    $number = 0.0
                    if (-not [double]::TryParse([Convert]::ToString($value.SampleValue, [Globalization.CultureInfo]::InvariantCulture),
                        [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or
                        [double]::IsNaN($number) -or [double]::IsInfinity($number) -or
                        $number -lt [double]$mapping.minValue -or $number -gt [double]$mapping.maxValue) {
                        throw 'SCOM_METRIC_VALUE_INVALID'
                    }
                    $rows.Add([pscustomobject]@{
                        id = [Guid]::NewGuid().ToString('D')
                        objectId = $id.ToString('D')
                        metric = $mapping.metric
                        unit = $mapping.unit
                        observedAtUtc = $at.ToString('o')
                        value = $number
                        ruleId = $rule
                        optimized = $mapping.optimized
                    })
                }
                if ($values.Count -eq 0) { $reason='SCOM_METRIC_NO_OBSERVATIONS' }
            }
            if ($null -ne $reason) {
                $complete=$false
                $rows.Clear()
            }
            $total += $rows.Count
            $outputs.Add([pscustomobject]@{
                objectId=$id.ToString('D'); metric=$mapping.metric; unit=$mapping.unit; ruleId=$rule
                startUtc=$StartUtc.ToString('o'); endUtc=$EndUtc.ToString('o')
                complete=($null -eq $reason); reason=$reason; observations=$rows.ToArray()
            })
        }
    }
    $reason = $null
    if (-not $complete) { $reason='SCOM_SELECTED_METRICS_INCOMPLETE_OR_EXCLUDED' }
    [pscustomobject]@{ Series=$outputs.ToArray(); Complete=$complete; Reason=$reason; SampleCount=$total }
}

Export-ModuleMember -Function Read-SCOMSelectedMetricSeries
