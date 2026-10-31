[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ConfigurationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$started = [DateTime]::UtcNow

function Read-CollectorConfiguration {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Collector configuration file is missing.'
    }
    $settings = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $settings) { throw 'Collector configuration is empty.' }
    foreach ($name in @('CollectorHost', 'ManagementServer', 'ManagementGroup',
            'OperationsManagerModule', 'LocalDataDirectory', 'PublishDirectory')) {
        $property = $settings.PSObject.Properties[$name]
        if ($null -eq $property -or $property.Value -isnot [string] -or
            [string]::IsNullOrWhiteSpace($property.Value) -or $property.Value -match '[\r\n]') {
            throw "Collector configuration requires a nonempty $name."
        }
    }
    if ($settings.CollectorHost -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,14}$' -or
        $settings.ManagementServer -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$') {
        throw 'Collector host or management server is invalid.'
    }
    foreach ($directory in @($settings.LocalDataDirectory, $settings.PublishDirectory)) {
        if ($directory -notmatch '^(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+(?:\\|$))') {
            throw 'Collector data directories must be absolute Windows paths.'
        }
    }
    $local = [IO.Path]::GetFullPath($settings.LocalDataDirectory)
    $publish = [IO.Path]::GetFullPath($settings.PublishDirectory)
    if ([string]::Equals($local.TrimEnd([char]'\'), $publish.TrimEnd([char]'\'),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Local and publish directories must be distinct.'
    }
    $computers = $settings.PSObject.Properties['Computers']
    if ($null -eq $computers -or $computers.Value -isnot [array] -or $computers.Value.Count -ne 2) {
        throw 'Exactly two approved computers are required.'
    }
    $expected = @{}
    $ids = @{}
    foreach ($computer in $computers.Value) {
        if ($null -eq $computer) { throw 'Each approved computer requires a hostname and SCOM object ID.' }
        $hostProperty = $computer.PSObject.Properties['Hostname']
        $idProperty = $computer.PSObject.Properties['EntityId']
        $id = [Guid]::Empty
        if ($null -eq $hostProperty -or $hostProperty.Value -isnot [string] -or
            $hostProperty.Value -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$' -or
            $null -eq $idProperty -or $idProperty.Value -isnot [string] -or
            -not [Guid]::TryParse($idProperty.Value, [ref]$id) -or $id -eq [Guid]::Empty) {
            throw 'Each approved computer requires a valid hostname and nonzero SCOM object ID.'
        }
        $canonicalId = $id.ToString('D')
        if ($expected.ContainsKey($hostProperty.Value) -or $ids.ContainsKey($canonicalId)) {
            throw 'Approved computer hostnames and SCOM object IDs must be distinct.'
        }
        $expected.Add($hostProperty.Value, $canonicalId)
        $ids.Add($canonicalId, $true)
    }
    return [pscustomobject]@{
        CollectorHost = $settings.CollectorHost
        ManagementServer = $settings.ManagementServer
        ManagementGroup = $settings.ManagementGroup
        OperationsManagerModule = $settings.OperationsManagerModule
        LocalDataDirectory = $local
        PublishDirectory = $publish
        ExpectedComputers = $expected
    }
}

function Write-AtomicJson {
    param([string]$Path, [object]$Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 16 -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 10000000) {
        throw 'Snapshot exceeds the approved size bound.'
    }
    if (-not ('ScomMcpCollector.AtomicFile' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
namespace ScomMcpCollector {
    public static class AtomicFile {
        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string source, string destination, uint flags);
        public static void MoveSameDirectory(string source, string destination, bool replaceExisting) {
            if (!String.Equals(Path.GetDirectoryName(Path.GetFullPath(source)),
                Path.GetDirectoryName(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Atomic publication requires the same directory.");
            const uint REPLACE_EXISTING = 0x1;
            const uint WRITE_THROUGH = 0x8;
            if (!MoveFileEx(source, destination, WRITE_THROUGH | (replaceExisting ? REPLACE_EXISTING : 0))) {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "SCOM atomic rename failed; Win32=" + error + ".");
            }
        }
    }
}
'@
    }
    $temp = Join-Path ([IO.Path]::GetDirectoryName($Path)) ([Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json)
        $stream = [IO.File]::Open($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        $replaceExisting = [IO.File]::Exists($Path)
        if ($replaceExisting) {
            $sections = [Security.AccessControl.AccessControlSections]::Access
            $stagedDacl = (Get-Acl -LiteralPath $temp).GetSecurityDescriptorSddlForm($sections)
            $currentDacl = (Get-Acl -LiteralPath $Path).GetSecurityDescriptorSddlForm($sections)
            if ($stagedDacl -cne $currentDacl) {
                throw 'Atomic publication refused: staged and destination access DACLs differ.'
            }
        }
        [ScomMcpCollector.AtomicFile]::MoveSameDirectory($temp, $Path, $replaceExisting)
    } finally {
        if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
    }
}

function Set-CollectorUnavailable {
    param([string[]]$Directories, [string]$AttemptedAtUtc, [string]$ErrorCode)
    $failure = @{ status = 'unavailable'; attemptedAtUtc = $AttemptedAtUtc; errorCode = $ErrorCode; datasetId = $null }
    $failures = New-Object 'Collections.Generic.List[Exception]'
    foreach ($directory in $Directories) {
        try { Write-AtomicJson (Join-Path $directory 'collector-status.json') $failure }
        catch { $failures.Add($_.Exception) }
    }
    if ($failures.Count -gt 0) {
        throw [AggregateException]::new('Could not invalidate every collector status; source availability must not be assumed.', $failures)
    }
}

function Publish-SCOMSnapshot {
    param($Snapshot, $CollectorStatus, [string]$LocalDirectory, [string]$PublishDirectory)
    try {
        Set-CollectorUnavailable @($LocalDirectory, $PublishDirectory) $CollectorStatus.attemptedAtUtc 'SCOM_COLLECTION_IN_PROGRESS'
        Write-AtomicJson (Join-Path $LocalDirectory 'snapshot.json') $Snapshot
        Write-AtomicJson (Join-Path $PublishDirectory 'snapshot.json') $Snapshot
        Write-AtomicJson (Join-Path $LocalDirectory 'collector-status.json') $CollectorStatus
        Write-AtomicJson (Join-Path $PublishDirectory 'collector-status.json') $CollectorStatus
    } catch {
        $publicationError = $_.Exception
        try {
            Set-CollectorUnavailable @($LocalDirectory, $PublishDirectory) $CollectorStatus.attemptedAtUtc 'SCOM_COLLECTION_FAILED'
        } catch {
            throw [AggregateException]::new('Publication failed and status invalidation also failed.',
                [Exception[]]@($publicationError, $_.Exception))
        }
        throw $publicationError
    }
}

function Get-DiscoveredValue {
    param($Object, [string]$Name)
    $properties = @($Object.GetProperties() | Where-Object Name -eq $Name)
    if ($properties.Count -gt 1) { throw "Ambiguous discovered property: $Name" }
    if ($properties.Count -eq 0) { return $null }
    $value = $Object[$properties[0]].Value
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) { return $null }
    return $value
}

function Get-Number {
    param($Value)
    if ($null -eq $Value) { return $null }
    $number = 0.0
    if (-not [double]::TryParse([string]$Value, [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or
        [double]::IsNaN($number) -or [double]::IsInfinity($number) -or $number -lt 0) {
        throw 'Invalid numeric SCOM observation.'
    }
    return $number
}

function Convert-ScomUtc {
    param([DateTime]$Value)
    if ($Value.Kind -eq [DateTimeKind]::Unspecified) {
        $Value = [DateTime]::SpecifyKind($Value, [DateTimeKind]::Utc)
    }
    return $Value.ToUniversalTime().ToString('o')
}

function Add-ObservedSamples {
    param($Object, [string]$EntityId, [bool]$IsVolume)
    foreach ($series in $Object.GetMonitoringPerformanceData()) {
        $metric = $null
        if ($IsVolume) {
            if ($series.ObjectName -eq 'LogicalDisk' -and $series.CounterName -eq '% Free Space') {
                $metric = 'diskFreePercent'
            }
        } elseif ($series.ObjectName -eq 'Processor Information' -and
                  $series.CounterName -eq '% Processor Time' -and $series.InstanceName -eq '_Total') {
            $metric = 'cpuPercent'
        } elseif ($series.ObjectName -eq 'Memory' -and $series.CounterName -eq 'PercentMemoryUsed') {
            $metric = 'memoryUsedPercent'
        } elseif ($series.ObjectName -eq 'Memory' -and $series.CounterName -eq 'Available MBytes') {
            $metric = 'memoryAvailableMiB'
        }
        if ($null -eq $metric) { continue }
        $values = $series.GetValues($started.AddDays(-14), $started)
        if ($values.Count -gt 10000) { throw 'A SCOM series exceeds the approved sample bound.' }
        $ruleId = [string]$series.RuleId
        if ([string]::IsNullOrWhiteSpace($ruleId) -or $ruleId.Length -gt 500) {
            throw 'Invalid SCOM collection rule identity.'
        }
        $optimized = ($metric -ne 'memoryUsedPercent')
        $coverageKey = "$EntityId|$metric|$ruleId"
        if (-not $observationCoverage.ContainsKey($coverageKey)) {
            $observationCoverage[$coverageKey] = [pscustomobject]@{
                entityId = $EntityId; metric = $metric; collectionRuleId = $ruleId
                missingCount = 0L; hasObservations = $false
                firstMissingAtUtc = $null; lastMissingAtUtc = $null
            }
        }
        $coverage = $observationCoverage[$coverageKey]
        foreach ($sample in $values) {
            $at = Convert-ScomUtc $sample.TimeSampled
            if ($null -eq $sample.SampleValue) {
                $coverage.missingCount++
                if ($null -eq $coverage.firstMissingAtUtc -or
                    [string]::CompareOrdinal($at, $coverage.firstMissingAtUtc) -lt 0) {
                    $coverage.firstMissingAtUtc = $at
                }
                if ($null -eq $coverage.lastMissingAtUtc -or
                    [string]::CompareOrdinal($at, $coverage.lastMissingAtUtc) -gt 0) {
                    $coverage.lastMissingAtUtc = $at
                }
                continue
            }
            $value = Get-Number $sample.SampleValue
            if ($null -eq $value -or ($metric -ne 'memoryAvailableMiB' -and $value -gt 100)) {
                throw 'Invalid SCOM metric value.'
            }
            $coverage.hasObservations = $true
            $key = "$EntityId|$metric|$at|$ruleId"
            if ($sampleIndex.ContainsKey($key)) {
                if ($sampleIndex[$key].value -ne $value -or $sampleIndex[$key].optimized -ne $optimized) {
                    throw 'Conflicting SCOM samples for one rule and timestamp.'
                }
                continue
            }
            $record = [pscustomobject]@{
                entityId = $EntityId
                metric = $metric
                observedAtUtc = $at
                value = $value
                collectionRuleId = $ruleId
                optimized = $optimized
            }
            $sampleIndex[$key] = $record
        }
    }
}

function Get-ObservationStatus {
    $status = 'ok'
    foreach ($coverage in ($observationCoverage.Values | Sort-Object entityId, metric, collectionRuleId)) {
        if ($coverage.missingCount -eq 0 -and $coverage.hasObservations) { continue }
        $status = 'partial'
        $detail = "Excluded $($coverage.missingCount) null SCOM observations: entityId=$($coverage.entityId); metric=$($coverage.metric); collectionRuleId=$($coverage.collectionRuleId)."
        if ($coverage.missingCount -gt 0) {
            $detail += " First missing UTC=$($coverage.firstMissingAtUtc); last missing UTC=$($coverage.lastMissingAtUtc)."
        }
        if (-not $coverage.hasObservations) { $detail += ' No usable observations for this entity/metric/rule.' }
        $detail += ' No samples were synthesized; the cause of null values is unknown.'
        if ($detail.Length -gt 2000) { throw 'SCOM observation limitation exceeds the approved length bound.' }
        $limitations.Add($detail)
    }
    if ($sampleIndex.Count -eq 0) {
        $status = 'partial'
        $limitations.Add('No usable SCOM metric observations are available for this snapshot.')
    }
    return $status
}

$config = Read-CollectorConfiguration $ConfigurationPath
$localData = $config.LocalDataDirectory
$publishData = $config.PublishDirectory
$expected = $config.ExpectedComputers

try {
    if ($env:COMPUTERNAME -ne $config.CollectorHost) { throw 'Collector is running on an unapproved host.' }
    if ([Security.Principal.WindowsIdentity]::GetCurrent().Name -ne 'NT AUTHORITY\NETWORK SERVICE') {
        throw 'Collector must run under its non-administrator service identity.'
    }
    if (-not (Test-Path -LiteralPath $localData) -or -not (Test-Path -LiteralPath $publishData)) {
        throw 'The protected local or publisher directory is unavailable.'
    }
    Set-CollectorUnavailable @($localData, $publishData) ($started.ToString('o')) 'SCOM_COLLECTION_IN_PROGRESS'
    Import-Module $config.OperationsManagerModule
    New-SCOMManagementGroupConnection -ComputerName $config.ManagementServer | Out-Null
    $mg = Get-SCOMManagementGroup
    if ($mg.Name -ne $config.ManagementGroup) { throw 'Unexpected source management group.' }
    $mgId = [string]$mg.Id

    $computerClass = Get-SCOMClass -Name 'Microsoft.Windows.Computer'
    $osClass = Get-SCOMClass -Name 'Microsoft.Windows.Server.10.0.OperatingSystem'
    $diskClass = Get-SCOMClass -Name 'Microsoft.Windows.Server.10.0.LogicalDisk'
    $computers = @(Get-SCOMClassInstance -Class $computerClass)
    $operatingSystems = @(Get-SCOMClassInstance -Class $osClass)
    $logicalDisks = @(Get-SCOMClassInstance -Class $diskClass)
    $byHost = @{}
    $osByHost = @{}
    $devices = New-Object 'Collections.Generic.List[object]'
    $volumes = New-Object 'Collections.Generic.List[object]'
    $sampleIndex = @{}
    $observationCoverage = @{}
    $limitations = New-Object 'Collections.Generic.List[string]'
    $limitations.Add('Only the two approved Windows computers are in scope; this is not enterprise-wide inventory.')
    $limitations.Add('SCOM does not establish purchased license entitlements or enterprise user ownership.')
    $limitations.Add('Performance history begins when collection started; optimized rules can suppress unchanged observations.')
    $limitations.Add('Memory capacity is OS-visible memory, normalized from a PhysicalMemory field assumed to be KiB; verify the management pack units.')
    $limitations.Add('Disk capacity describes discovered logical volumes, including temporary storage where discovered.')

    foreach ($os in $operatingSystems) {
        $hostName = [string]$os.Path
        if (-not $expected.ContainsKey($hostName)) { throw 'OS visibility exceeds the approved source scope.' }
        if ($osByHost.ContainsKey($hostName)) { throw 'More than one current OS instance exists for a pilot computer.' }
        $osByHost[$hostName] = $os
    }
    foreach ($computer in $computers) {
        $hostName = [string](Get-DiscoveredValue $computer 'PrincipalName')
        $entityId = ([string]$computer.Id).ToLowerInvariant()
        if (-not $expected.ContainsKey($hostName) -or $expected[$hostName] -ne $entityId) {
            throw 'Computer identity or visibility exceeds the approved scope.'
        }
        if ($byHost.ContainsKey($hostName)) { throw 'Duplicate source computer identity.' }
        $byHost[$hostName] = $entityId
        $os = $null
        if ($osByHost.ContainsKey($hostName)) { $os = $osByHost[$hostName] }
        $osVersion = $null
        $memory = $null
        $processors = $null
        if ($os) {
            $caption = Get-DiscoveredValue $os 'OSVersionDisplayName'
            $version = Get-DiscoveredValue $os 'OSVersion'
            $osVersion = if ($caption -and $version) { "$caption ($version)" } elseif ($caption) { [string]$caption } else { [string]$version }
            $rawMemory = Get-Number (Get-DiscoveredValue $os 'PhysicalMemory')
            if ($null -ne $rawMemory) { $memory = $rawMemory / 1024.0 }
            $logicalProcessors = Get-Number (Get-DiscoveredValue $os 'LogicalProcessors')
            if ($null -ne $logicalProcessors) { $processors = [int]$logicalProcessors }
        }
        $rawHealth = $computer.HealthState.ToString()
        $health = switch ($rawHealth) {
            'Success' { 'healthy' }
            'Warning' { 'warning' }
            'Error' { 'critical' }
            default { 'unknown' }
        }
        $virtualization = 'unknown'
        $virtualName = Get-DiscoveredValue $computer 'VirtualMachineName'
        if ($virtualName) { $virtualization = 'virtual' }
        $refs = @("SCOM:${mgId}:$entityId")
        if ($os) { $refs += "SCOM:${mgId}:$($os.Id)" }
        $devices.Add([pscustomobject]@{
            entityId = $entityId; sourceObjectId = $entityId; hostname = $hostName
            entityKind = 'computer'; osFamily = 'windows'; osVersion = $osVersion
            health = $health; rawHealth = $rawHealth; virtualization = $virtualization
            monitoringMode = 'agent'; memoryTotalMiB = $memory; processorCount = $processors
            discoveredAtUtc = (Convert-ScomUtc $computer.TimeAdded)
            groupIds = @('scom-lab'); sourceRefs = $refs
        })
        if ($os) { Add-ObservedSamples $os $entityId $false }
    }
    foreach ($disk in $logicalDisks) {
        $hostName = [string]$disk.Path
        if (-not $byHost.ContainsKey($hostName)) { throw 'Logical disk does not belong to a permitted computer.' }
        $id = ([string]$disk.Id).ToLowerInvariant()
        $sizeBytes = Get-Number (Get-DiscoveredValue $disk 'Size')
        $sizeMiB = if ($null -ne $sizeBytes) { $sizeBytes / 1048576.0 } else { $null }
        $volumes.Add([pscustomobject]@{
            entityId = $id; parentEntityId = $byHost[$hostName]
            deviceId = [string](Get-DiscoveredValue $disk 'DeviceID')
            fileSystem = Get-DiscoveredValue $disk 'FileSystem'
            sizeMiB = $sizeMiB; sourceRefs = @("SCOM:${mgId}:$id")
        })
        Add-ObservedSamples $disk $id $true
    }
    $status = Get-ObservationStatus
    if ($devices.Count -ne $expected.Count -or $osByHost.Count -ne $expected.Count) {
        $status = 'partial'
        $limitations.Add('Computer or operating-system discovery is incomplete within the approved pilot scope.')
    }
    foreach ($id in $byHost.Values) {
        if (-not @($volumes | Where-Object parentEntityId -eq $id).Count) {
            $status = 'partial'
            $limitations.Add('Logical-disk discovery is incomplete for at least one approved computer.')
            break
        }
    }
    $limitations.Add('processorCount is the discovered logical-processor count, not physical cores or licensing units.')
    $collected = [DateTime]::UtcNow
    $datasetId = $collected.ToString('yyyyMMddTHHmmssfffffffZ') + '-' + [Guid]::NewGuid().ToString('N')
    $snapshot = [ordered]@{
        schemaVersion = '1.0'; datasetId = $datasetId; sourceMode = 'scom'; status = $status
        managementGroup = @{ id = $mgId; name = [string]$mg.Name }
        scope = @{ id = 'scom-lab'; label = 'Approved SCOM computers' }
        collectedAtUtc = $collected.ToString('o'); collectionStartedAtUtc = $started.ToString('o')
        expectedDeviceCount = 2
        devices = @($devices.ToArray() | Sort-Object hostname)
        volumes = @($volumes.ToArray() | Sort-Object parentEntityId, deviceId)
        samples = @($sampleIndex.Values | Sort-Object entityId, metric, observedAtUtc, collectionRuleId)
        capabilities = @{
            classes = @('WindowsComputer', 'OperatingSystem', 'LogicalDisk')
            metrics = @('cpuPercent', 'memoryUsedPercent', 'memoryAvailableMiB', 'diskFreePercent')
            networkDevices = $false; licenseEntitlements = $false
        }
        limitations = $limitations.ToArray(); sourceRefs = @("SCOM:$mgId")
    }
    $collectorStatus = @{ status = 'ok'; attemptedAtUtc = $started.ToString('o'); errorCode = $null; datasetId = $datasetId }
    Publish-SCOMSnapshot $snapshot $collectorStatus $localData $publishData
} catch {
    $collectionError = $_.Exception
    try {
        Set-CollectorUnavailable @($localData, $publishData) ($started.ToString('o')) 'SCOM_COLLECTION_FAILED'
    } catch {
        $collectionError = [AggregateException]::new('Collection failed and status invalidation also failed.',
            [Exception[]]@($collectionError, $_.Exception))
    }
    if (Test-Path -LiteralPath $localData) {
        try { $collectionError.ToString() | Set-Content -LiteralPath (Join-Path $localData 'last-error.txt') -Encoding UTF8 }
        catch { Write-Warning ('Could not write collector error log: ' + $_.Exception.Message) }
    }
    throw $collectionError
}
