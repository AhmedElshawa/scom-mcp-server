<#
.SYNOPSIS
  Generates the windowsMapping section of worker.json from a read-only, bounded
  look at your own Operations Manager (SCOM) management group.

.DESCRIPTION
  Run in Windows PowerShell 5.1 as the worker account on the collector host (any
  host with the Operations Manager PowerShell module). The script only reads.

  Work performed:
    1. One cmdlet-based computer discovery, limited to -SampleComputers (1-10)
       with Select-Object -First. The SCOM SDK provides no server-side limit or
       completeness contract for this call; the limit applies to what this
       process keeps and inspects, not to the work SCOM performs.
    2. One single-source relationship read per sampled computer. Only when a
       computer shows no disk edge directly, one more read on its operating
       system object (so at most two reads per computer).
    3. Metadata reads (class and relationship types) to name the most-derived
       classes and any derived relationship types.
  A local deadline (-DeadlineSeconds) stops this process only.

  Units are never guessed. PhysicalMemory must fall in 0.5-4096 GiB under
  exactly one of KB, MB or bytes for every sample. Disk size prefers
  SizeInMBs; otherwise SizeNumeric or Size is read as bytes; when both forms
  exist they must agree. If no unit is provable the factor is written as null
  and the script exits non-zero.

  Output: <OutputPath> (a complete worker.json from the template with all six
  attestation flags false) and <OutputPath>.evidence.json. Review the printed
  summary, then run Approve-ScomMappingProfile.ps1.

  Exit codes: 0 ready; 1 invalid arguments or unexpected error; 2 nothing
  usable discovered or prerequisites missing (nothing written); 3 units not
  provable (worker.json written with null units); 4 relationships, classes or
  properties ambiguous or missing (evidence only).
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Run', Mandatory)] [string] $ManagementServer,
    [Parameter(ParameterSetName = 'Run', Mandatory)] [ValidateSet('group', 'managementGroup')] [string] $ScopeKind,
    [Parameter(ParameterSetName = 'Run')] [string] $GroupId,
    [Parameter(ParameterSetName = 'Run')] [ValidateRange(1, 10)] [int] $SampleComputers = 3,
    [Parameter(ParameterSetName = 'Run')] [string] $WorkerTemplatePath,
    [Parameter(ParameterSetName = 'Run', Mandatory)] [string] $OutputPath,
    [Parameter(ParameterSetName = 'Run')] [ValidateRange(30, 600)] [int] $DeadlineSeconds = 120,
    [Parameter(ParameterSetName = 'Run')] [string] $OperationsManagerModule = 'C:\Program Files\Microsoft System Center\Operations Manager\Powershell\OperationsManager\OperationsManager.psd1',
    [Parameter(ParameterSetName = 'Run')] [switch] $Force,
    [Parameter(ParameterSetName = 'Functions', Mandatory)] [switch] $LoadFunctionsOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:MemoryUnits = @(
    @{ unit = 'KB'; factor = 0.0009765625 },
    @{ unit = 'MB'; factor = 1.0 },
    @{ unit = 'bytes'; factor = 9.5367431640625E-07 })
$script:BytesToMiB = 9.5367431640625E-07
$script:Watch = [System.Diagnostics.Stopwatch]::StartNew()
$script:DeadlineSeconds = 120
$script:ClassChains = @{}
$script:RelationshipNames = @{}

function Get-ScomJsonValue {
    param($Object, [string] $Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    $property.Value
}

function Set-ScomJsonValue {
    param([Parameter(Mandatory)] $Object, [Parameter(Mandatory)] [string] $Name, $Value)
    if ($null -eq $Object.PSObject.Properties[$Name]) { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
    else { $Object.$Name = $Value }
}

function ConvertTo-ScomDouble {
    param($Value)
    if ($null -eq $Value) { return $null }
    $number = 0.0
    if ([double]::TryParse([string]$Value, [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -and
        -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number)) { return $number }
    $null
}

function Resolve-ScomMemoryUnit {
    param([double[]] $Samples)
    $candidates = @(foreach ($candidate in $script:MemoryUnits) {
            $gib = @($Samples | ForEach-Object { $_ * $candidate.factor / 1024.0 })
            $plausible = $gib.Count -gt 0 -and @($gib | Where-Object { $_ -lt 0.5 -or $_ -gt 4096 }).Count -eq 0
            [ordered]@{
                unit = $candidate.unit; factor = $candidate.factor; plausible = $plausible
                gibSamples = @($gib | ForEach-Object { [Math]::Round($_, 3) })
            }
        })
    $fit = @($candidates | Where-Object { $_.plausible })
    $result = [ordered]@{ resolved = $false; unit = $null; factor = $null; rawSamples = @($Samples); candidates = $candidates; reason = $null }
    if ($Samples.Count -eq 0) { $result.reason = 'No numeric PhysicalMemory samples were observed.' }
    elseif ($fit.Count -eq 0) { $result.reason = 'No unit (KB, MB or bytes) puts every PhysicalMemory sample in 0.5-4096 GiB.' }
    elseif ($fit.Count -gt 1) { $result.reason = 'More than one unit is plausible for the PhysicalMemory samples (' + (($fit | ForEach-Object { $_.unit }) -join ', ') + '); the unit cannot be proven.' }
    else { $result.resolved = $true; $result.unit = $fit[0].unit; $result.factor = $fit[0].factor }
    $result
}

function Resolve-ScomDiskSize {
    # $Disks are property tables (property name -> raw value), one per observed disk.
    param([object[]] $Disks)
    $result = [ordered]@{ resolved = $false; property = $null; unit = $null; factor = $null; crossCheck = 'notApplicable'; samples = @(); reason = $null }
    $rows = @(foreach ($disk in $Disks) {
            $mb = ConvertTo-ScomDouble $disk['SizeInMBs']
            if ($null -ne $mb -and $mb -le 0) { $mb = $null }
            $bytes = $null; $bytesName = $null
            foreach ($name in 'SizeNumeric', 'Size') {
                $candidate = ConvertTo-ScomDouble $disk[$name]
                if ($null -ne $candidate -and $candidate -gt 0) { $bytes = $candidate; $bytesName = $name; break }
            }
            [pscustomobject]@{ mb = $mb; bytes = $bytes; bytesName = $bytesName }
        })
    $result.samples = @($rows | ForEach-Object { [ordered]@{ SizeInMBs = $_.mb; bytes = $_.bytes; bytesProperty = $_.bytesName } })
    $mbRows = @($rows | Where-Object { $null -ne $_.mb })
    $bytesRows = @($rows | Where-Object { $null -ne $_.bytes })
    if ($mbRows.Count -eq 0 -and $bytesRows.Count -eq 0) {
        $result.reason = 'No usable disk size (SizeInMBs, SizeNumeric or Size) was observed.'
        return $result
    }
    $both = @($rows | Where-Object { $null -ne $_.mb -and $null -ne $_.bytes })
    if ($both.Count -gt 0) {
        $disagree = @($both | Where-Object { [Math]::Abs($_.bytes / 1048576.0 - $_.mb) -gt [Math]::Max(1.0, 0.01 * $_.mb) })
        if ($disagree.Count -gt 0) {
            $result.crossCheck = 'disagree'
            $result.reason = 'SizeInMBs and the byte size disagree on at least one disk; the unit cannot be proven.'
            return $result
        }
        $result.crossCheck = 'agree'
    }
    if ($mbRows.Count -gt 0) { $result.property = 'SizeInMBs'; $result.unit = 'MiB'; $result.factor = 1.0 }
    else {
        $names = @($bytesRows | ForEach-Object { $_.bytesName } | Sort-Object -Unique)
        if ($names.Count -ne 1) { $result.reason = 'Disks report their byte size under different properties; the unit cannot be proven.'; return $result }
        $result.property = $names[0]; $result.unit = 'bytes'; $result.factor = $script:BytesToMiB
    }
    $result.resolved = $true
    $result
}

function Test-ScomClassUnder {
    # True when the chain is unknown (no evidence against) or contains the base class.
    param($Chains, [string] $Class, [string] $Base)
    if ($Class -ceq $Base) { return $true }
    $chain = $null
    if ($Chains -is [System.Collections.IDictionary]) { $chain = $Chains[$Class] }
    elseif ($null -ne $Chains) { $chain = Get-ScomJsonValue $Chains $Class }
    $chain = @($chain | Where-Object { $_ })
    $chain.Count -eq 0 -or $chain -contains $Base
}

function Resolve-ScomMappingObservation {
    param([Parameter(Mandatory)] $Observation, [Parameter(Mandatory)] $TemplateMapping)
    $errors = New-Object System.Collections.Generic.List[string]
    $warnings = New-Object System.Collections.Generic.List[string]
    $result = [ordered]@{
        status = 'Ambiguous'; errors = @(); warnings = @(); windowsMapping = $null
        allowedPropertiesByClass = $null; additionalRelationshipNames = @()
        units = [ordered]@{ memory = $null; disk = $null }; facts = [ordered]@{}
    }
    $computers = @(@($Observation.computers) | Where-Object { $null -ne $_ })
    if ($computers.Count -eq 0) {
        $result.status = 'NoComputers'
        $result.facts = [ordered]@{ computersSampled = 0 }
        $result.errors = @('No Windows computers were discovered in the requested scope. Nothing was generated.')
        return $result
    }
    $chains = $Observation.classChains
    $names = [ordered]@{}
    foreach ($key in 'computerClassName', 'operatingSystemClassName', 'volumeClassName', 'hostnameProperty', 'osVersionProperty',
        'osCaptionProperty', 'memoryProperty', 'processorCountProperty', 'volumeNameProperty', 'fileSystemProperty') {
        $names[$key] = Get-ScomJsonValue $TemplateMapping $key
    }
    foreach ($key in 'computerClassName', 'operatingSystemClassName', 'volumeClassName', 'hostnameProperty', 'osVersionProperty',
        'memoryProperty', 'processorCountProperty', 'volumeNameProperty', 'fileSystemProperty') {
        if (-not $names[$key]) { $errors.Add("The worker template has no windowsMapping.$key.") }
    }

    $osEdges = New-Object System.Collections.Generic.List[object]
    $diskEdges = New-Object System.Collections.Generic.List[object]
    $otherEdges = New-Object System.Collections.Generic.List[object]
    $withoutOs = 0
    foreach ($computer in $computers) {
        $edges = @(@($computer.edges) | Where-Object { $null -ne $_ })
        $mine = @($edges | Where-Object { $_.source -eq 'computer' -and $_.kind -eq 'os' })
        if ($mine.Count -eq 0) { $withoutOs++ }
        elseif ($mine.Count -gt 1) { $errors.Add("Computer '$($computer.name)' has $($mine.Count) operating-system edges; the OS mapping would be ambiguous.") }
        foreach ($edge in $edges) {
            if ($edge.kind -eq 'os' -and $edge.source -eq 'computer') { $osEdges.Add($edge) }
            elseif ($edge.kind -eq 'disk') { $diskEdges.Add($edge) }
            elseif ($edge.kind -eq 'other') { $otherEdges.Add($edge) }
        }
    }
    if ($withoutOs -gt 0) { $warnings.Add("$withoutOs sampled computer(s) have no operating-system edge and contributed nothing to the OS mapping.") }

    $osRelationships = @($osEdges | ForEach-Object { $_.relationship } | Sort-Object -Unique)
    if ($osEdges.Count -eq 0) { $errors.Add('No operating-system hosting edge was observed on any sampled computer.') }
    elseif ($osRelationships.Count -ne 1) { $errors.Add('More than one operating-system hosting relationship was observed: ' + ($osRelationships -join ', ') + '.') }

    $diskCombos = @($diskEdges | ForEach-Object { '{0}|{1}' -f $_.source, $_.relationship } | Sort-Object -Unique)
    $diskRelationship = $null; $diskHostedBy = $null
    if ($diskEdges.Count -eq 0) { $errors.Add('No logical-disk hosting edge was observed on any sampled computer or its operating system object.') }
    elseif ($diskCombos.Count -ne 1) { $errors.Add('Disks are hosted through more than one relationship (' + ($diskCombos -join ', ') + '); exactly one is required.') }
    else { $diskHostedBy, $diskRelationship = $diskCombos[0].Split('|', 2) }
    if ($osRelationships.Count -eq 1 -and $diskRelationship -and $osRelationships[0] -ceq $diskRelationship) {
        $errors.Add('The OS and disk hosting relationships must differ.')
    }
    $nonDisk = @($otherEdges | Where-Object { $_.source -eq $diskHostedBy -and $_.relationship -ceq $diskRelationship })
    if ($nonDisk.Count -gt 0) {
        $warnings.Add("$($nonDisk.Count) non-disk logical device edge(s) (for example network adapters) share the disk relationship; the collector ignores them.")
    }
    foreach ($derived in @($Observation.derivedRelationshipTypes)) {
        if ($null -ne $derived -and @($derived.derived).Count -gt 0) {
            $warnings.Add("Relationship '$($derived.name)' has derived types ($(@($derived.derived) -join ', ')); the collector reads exact relationship types only and would not return edges of derived types.")
        }
    }

    $computerClasses = @($computers | ForEach-Object { @($_.mostDerivedClasses) } | Where-Object { $_ -and (Test-ScomClassUnder $chains $_ $names.computerClassName) } | Sort-Object -Unique)
    $osClasses = @($osEdges | ForEach-Object { @($_.targetMostDerivedClasses) } | Where-Object { $_ -and (Test-ScomClassUnder $chains $_ $names.operatingSystemClassName) } | Sort-Object -Unique)
    $diskTargets = @($diskEdges | Where-Object { @($_.targetMostDerivedClasses | Where-Object { $_ -and (Test-ScomClassUnder $chains $_ $names.volumeClassName) }).Count -gt 0 })
    $diskClasses = @($diskTargets | ForEach-Object { @($_.targetMostDerivedClasses) } | Where-Object { $_ -and (Test-ScomClassUnder $chains $_ $names.volumeClassName) } | Sort-Object -Unique)
    if ($computerClasses.Count -eq 0) { $errors.Add("No observed computer class derives from $($names.computerClassName).") }
    if ($osEdges.Count -gt 0 -and $osClasses.Count -eq 0) { $errors.Add("No observed operating-system class derives from $($names.operatingSystemClassName).") }
    if ($diskEdges.Count -gt 0 -and $diskClasses.Count -eq 0) { $errors.Add("No observed disk class derives from $($names.volumeClassName).") }

    function Get-ClassKeys($records, [string] $class, [string] $propertyHolder) {
        $keys = @{}
        foreach ($record in $records) {
            $classes = if ($propertyHolder -eq 'computer') { @($record.mostDerivedClasses) } else { @($record.targetMostDerivedClasses) }
            if ($classes -notcontains $class) { continue }
            $table = $record.properties
            if ($null -eq $table) { continue }
            $propertyNames = if ($table -is [hashtable] -or $table -is [System.Collections.Specialized.OrderedDictionary]) { @($table.Keys) } else { @($table.PSObject.Properties | ForEach-Object Name) }
            foreach ($name in $propertyNames) { $keys[[string]$name] = $true }
        }
        $keys
    }
    function Get-Raw($table, [string] $name) {
        if ($null -eq $table) { return $null }
        if ($table -is [hashtable] -or $table -is [System.Collections.Specialized.OrderedDictionary]) { return $table[$name] }
        Get-ScomJsonValue $table $name
    }

    $memorySamples = @($osEdges | Where-Object { @($_.targetMostDerivedClasses | Where-Object { $osClasses -contains $_ }).Count -gt 0 } |
            ForEach-Object { ConvertTo-ScomDouble (Get-Raw $_.properties $names.memoryProperty) } | Where-Object { $null -ne $_ -and $_ -gt 0 })
    $memory = Resolve-ScomMemoryUnit -Samples ([double[]]$memorySamples)
    $disk = Resolve-ScomDiskSize -Disks @($diskTargets | ForEach-Object {
            $table = @{}
            foreach ($name in 'SizeInMBs', 'SizeNumeric', 'Size') { $table[$name] = Get-Raw $_.properties $name }
            $table
        })
    $result.units.memory = $memory
    $result.units.disk = $disk

    $allowed = [ordered]@{}
    $allowedFor = {
        param($records, $classes, $holder, $required, $optional)
        $selected = @{}
        foreach ($class in $classes) {
            $keys = Get-ClassKeys $records $class $holder
            $list = New-Object System.Collections.Generic.List[string]
            foreach ($property in $required) {
                if (-not $property) { continue }
                if ($keys.ContainsKey($property)) { if (-not $list.Contains($property)) { $list.Add($property) } }
                else { $errors.Add("Property '$property' was not observed on class '$class'.") }
            }
            foreach ($property in $optional) { if ($property -and $keys.ContainsKey($property) -and -not $list.Contains($property)) { $list.Add($property) } }
            $selected[$class] = @($list)
        }
        $selected
    }
    $captionProperty = $names.osCaptionProperty
    $osSelected = & $allowedFor $osEdges $osClasses 'target' @($names.osVersionProperty, $names.memoryProperty) @($captionProperty)
    $captionEverywhere = $captionProperty -and $osClasses.Count -gt 0 -and @($osClasses | Where-Object { $osSelected[$_] -notcontains $captionProperty }).Count -eq 0
    if ($captionProperty -and -not $captionEverywhere -and $osClasses.Count -gt 0) {
        $warnings.Add("OS caption property '$captionProperty' was not observed on every OS class; osCaptionProperty is set to null.")
        $captionProperty = $null
    }
    $sizeProperty = if ($disk.resolved) { $disk.property } else { $null }
    $computerSelected = & $allowedFor $computers $computerClasses 'computer' @($names.hostnameProperty, $names.processorCountProperty) @()
    $diskSelected = & $allowedFor $diskTargets $diskClasses 'target' @($names.volumeNameProperty, $names.fileSystemProperty) @($sizeProperty)
    if ($disk.resolved -and $diskClasses.Count -gt 0 -and @($diskClasses | Where-Object { $diskSelected[$_] -notcontains $sizeProperty }).Count -gt 0) {
        $errors.Add("Disk size property '$sizeProperty' was not observed on every disk class.")
    }
    foreach ($class in $computerClasses) { $allowed[$class] = @($computerSelected[$class]) }
    foreach ($class in $osClasses) { $allowed[$class] = @($osSelected[$class]) }
    foreach ($class in $diskClasses) { $allowed[$class] = @($diskSelected[$class]) }

    $result.facts = [ordered]@{
        computersSampled = $computers.Count; computersWithoutOsEdge = $withoutOs
        osRelationship = $(if ($osRelationships.Count -eq 1) { $osRelationships[0] } else { $null })
        diskRelationship = $diskRelationship; diskHostedBy = $diskHostedBy
        computerClasses = $computerClasses; osClasses = $osClasses; diskClasses = $diskClasses
        nonDiskLogicalDeviceEdges = $nonDisk.Count; diskCount = $diskTargets.Count
    }
    $result.warnings = @($warnings)
    if ($errors.Count -gt 0) { $result.errors = @($errors); return $result }

    $mapping = [ordered]@{
        verified = $false; agentMonitoringVerified = $false
        computerClassName = $names.computerClassName
        operatingSystemClassName = $names.operatingSystemClassName
        volumeClassName = $names.volumeClassName
        computerHostsOsRelationship = $osRelationships[0]
    }
    if ($diskHostedBy -eq 'os') { $mapping.osHostsVolumeRelationship = $diskRelationship }
    else { $mapping.computerHostsVolumeRelationship = $diskRelationship }
    $mapping.hostnameProperty = $names.hostnameProperty
    $mapping.osVersionProperty = $names.osVersionProperty
    $mapping.osCaptionProperty = $captionProperty
    $mapping.memoryProperty = $names.memoryProperty
    $mapping.memoryToMiB = $memory.factor
    $mapping.processorCountProperty = $names.processorCountProperty
    $mapping.volumeNameProperty = $names.volumeNameProperty
    $mapping.fileSystemProperty = $names.fileSystemProperty
    $mapping.volumeSizeProperty = $sizeProperty
    $mapping.volumeSizeToMiB = $disk.factor
    $skip = @('verified', 'agentMonitoringVerified', 'computerHostsVolumeRelationship', 'osHostsVolumeRelationship') + @($mapping.Keys)
    foreach ($property in $TemplateMapping.PSObject.Properties) {
        if ($skip -notcontains $property.Name) { $mapping[$property.Name] = $property.Value }
    }
    # Set reads need both hosting relationships observed. Non-disk rows sharing the disk relationship would reach the
    # set-read cap (expected targets + 1) and fail closed, so that case stays per-source.
    $readMode = 'perSource'; $readModeReason = 'RELATIONSHIP_NOT_OBSERVED'
    if ($osEdges.Count -gt 0 -and $diskEdges.Count -gt 0) {
        if ($nonDisk.Count -gt 0) { $readModeReason = 'NON_DISK_EDGES_SHARE_DISK_RELATIONSHIP' }
        else { $readMode = 'set'; $readModeReason = 'BOTH_RELATIONSHIPS_OBSERVED' }
    }
    $result.relationshipReadMode = $readMode
    $result.relationshipReadModeReason = $readModeReason
    $result.windowsMapping = $mapping
    $result.allowedPropertiesByClass = $allowed
    $result.additionalRelationshipNames = @($osRelationships[0], $diskRelationship)
    if (-not $memory.resolved) { $result.errors = @($memory.reason) }
    if (-not $disk.resolved) { $result.errors = @($result.errors) + @($disk.reason) }
    $result.status = if ($memory.resolved -and $disk.resolved) { 'Ready' } else { 'UnitsUnresolved' }
    $result
}

function New-ScomWorkerDocument {
    param(
        [Parameter(Mandatory)] [string] $TemplateJson, [Parameter(Mandatory)] $Resolution,
        [Parameter(Mandatory)] $Observation, [Parameter(Mandatory)] [string] $ScopeKind, [string] $GroupId, [string] $ModulePath
    )
    $document = $TemplateJson | ConvertFrom-Json
    $configuration = $document.profile.configuration
    if ($ModulePath) { Set-ScomJsonValue $configuration 'operationsManagerModule' $ModulePath }
    Set-ScomJsonValue $configuration 'managementServer' $Observation.managementServer
    Set-ScomJsonValue $configuration 'managementGroupId' $Observation.managementGroupId
    Set-ScomJsonValue $configuration 'scopeKind' $ScopeKind
    Set-ScomJsonValue $configuration 'groupId' $(if ($ScopeKind -eq 'group') { $GroupId } else { $null })
    Set-ScomJsonValue $configuration 'wholeManagementGroupApproved' ($ScopeKind -eq 'managementGroup')
    foreach ($flag in 'scopeVisibilityVerified', 'membershipQueryContractVerified', 'enumerationQueryContractVerified', 'additionalRelationshipContractVerified') {
        Set-ScomJsonValue $configuration $flag $false
    }
    $allowed = [ordered]@{}
    foreach ($class in $Resolution.allowedPropertiesByClass.Keys) { $allowed[$class] = @($Resolution.allowedPropertiesByClass[$class]) }
    Set-ScomJsonValue $configuration 'allowedPropertiesByClass' ([pscustomobject]$allowed)
    Set-ScomJsonValue $configuration 'additionalRelationshipNames' @($Resolution.additionalRelationshipNames)
    Set-ScomJsonValue $configuration 'windowsMapping' ([pscustomobject]$Resolution.windowsMapping)
    Set-ScomJsonValue $configuration 'relationshipReadMode' $(if ($Resolution.Contains('relationshipReadMode')) { $Resolution.relationshipReadMode } else { 'perSource' })
    $document
}

function ConvertTo-ScomSummaryLines {
    param([Parameter(Mandatory)] $Observation, [Parameter(Mandatory)] $Resolution, [int] $Requested, [int] $Deadline, [string] $ScopeKind, [string] $GroupId)
    $facts = $Resolution.facts
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('SCOM MCP windowsMapping evidence summary')
    $lines.Add(('  Management group : {0} (server {1})' -f $Observation.managementGroupId, $Observation.managementServer))
    $lines.Add(('  Scope            : {0}{1}' -f $ScopeKind, $(if ($ScopeKind -eq 'group') { " $GroupId" } else { ' (whole management group)' })))
    $lines.Add(('  Computers sampled: {0} of at most {1} requested' -f $facts.computersSampled, $Requested))
    if ($facts.Contains('osRelationship')) {
        $lines.Add(('  OS hosting       : {0}; OS classes {1}' -f $facts.osRelationship, (@($facts.osClasses) -join ', ')))
        $lines.Add(('  Disk hosting     : {0} ({1}-hosted); disk classes {2}; {3} disk(s), {4} non-disk logical device edge(s)' -f
                $facts.diskRelationship, $facts.diskHostedBy, (@($facts.diskClasses) -join ', '), $facts.diskCount, $facts.nonDiskLogicalDeviceEdges))
        $lines.Add(('  Computer classes : {0}' -f (@($facts.computerClasses) -join ', ')))
    }
    if ($Resolution.Contains('relationshipReadMode')) {
        $lines.Add(('  Relationship read: {0} ({1})' -f $Resolution.relationshipReadMode, $Resolution.relationshipReadModeReason))
    }
    $memory = $Resolution.units.memory
    if ($null -ne $memory) {
        $unit = if ($memory.resolved) { "{0} -> memoryToMiB {1}" -f $memory.unit, $memory.factor } else { "NOT PROVEN: $($memory.reason)" }
        $lines.Add(('  Memory unit      : raw PhysicalMemory {0}; {1}' -f (@($memory.rawSamples) -join ', '), $unit))
    }
    $disk = $Resolution.units.disk
    if ($null -ne $disk) {
        $unit = if ($disk.resolved) { "{0} ({1}) -> volumeSizeToMiB {2}; cross-check {3}" -f $disk.property, $disk.unit, $disk.factor, $disk.crossCheck } else { "NOT PROVEN: $($disk.reason)" }
        $lines.Add(('  Disk size unit   : {0}' -f $unit))
    }
    if ($null -ne $Resolution.allowedPropertiesByClass) {
        $lines.Add('  allowedPropertiesByClass (by observed most-derived class):')
        foreach ($class in $Resolution.allowedPropertiesByClass.Keys) { $lines.Add(('    {0}: {1}' -f $class, (@($Resolution.allowedPropertiesByClass[$class]) -join ', '))) }
    }
    foreach ($warning in @($Resolution.warnings)) { $lines.Add("  Warning: $warning") }
    foreach ($errorText in @($Resolution.errors)) { $lines.Add("  Problem: $errorText") }
    $lines.Add(('  Bounds: at most {0} computers kept via Select-Object -First; one relationship read per computer (one extra OS-object read only when no disk edge is direct); {1} s local deadline.' -f $Requested, $Deadline))
    $lines.Add('  These bounds limit local consumption only. The SCOM SDK provides no completeness or cancellation contract and SCOM-side work is not bounded by them.')
    if ($Resolution.status -eq 'Ready') {
        $lines.Add('  Next: set the same allowedPropertiesByClass in the receiver settings, then run Approve-ScomMappingProfile.ps1. The six flags stay false until you approve.')
    }
    @($lines)
}

function Write-ScomUtf8File {
    param([string] $Path, [string] $Text)
    [IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-ScomMappingProfile {
    # Pure orchestration over an observation; used by the script and by validation.
    param(
        [Parameter(Mandatory)] $Observation, [Parameter(Mandatory)] [string] $TemplateJson, [Parameter(Mandatory)] [string] $ScopeKind,
        [string] $GroupId, [Parameter(Mandatory)] [string] $OutputPath, [int] $SampleComputers = 3, [int] $DeadlineSeconds = 120,
        [string] $ModulePath, [switch] $Force
    )
    $template = $TemplateJson | ConvertFrom-Json
    $resolution = Resolve-ScomMappingObservation -Observation $Observation -TemplateMapping $template.profile.configuration.windowsMapping
    $summary = ConvertTo-ScomSummaryLines -Observation $Observation -Resolution $resolution -Requested $SampleComputers -Deadline $DeadlineSeconds -ScopeKind $ScopeKind -GroupId $GroupId
    $exitCode = switch ($resolution.status) { 'Ready' { 0 } 'NoComputers' { 2 } 'UnitsUnresolved' { 3 } default { 4 } }
    $evidencePath = "$OutputPath.evidence.json"
    $written = @()
    if ($exitCode -ne 2) {
        if (-not $Force -and ((Test-Path -LiteralPath $OutputPath) -or (Test-Path -LiteralPath $evidencePath))) {
            throw "Refusing to overwrite existing '$OutputPath' or its evidence file. Use -Force to replace them."
        }
        $evidence = [ordered]@{
            schemaVersion = 1; generator = 'New-ScomMappingProfile.ps1'; generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
            operator = [Security.Principal.WindowsIdentity]::GetCurrent().Name; computerName = $env:COMPUTERNAME
            powerShellVersion = $PSVersionTable.PSVersion.ToString()
            scopeKind = $ScopeKind; groupId = $(if ($ScopeKind -eq 'group') { $GroupId } else { $null })
            requestedSampleComputers = $SampleComputers; deadlineSeconds = $DeadlineSeconds
            relationshipReadMode = $(if ($resolution.Contains('relationshipReadMode')) { $resolution.relationshipReadMode } else { 'perSource' })
            relationshipReadModeReason = $(if ($resolution.Contains('relationshipReadModeReason')) { $resolution.relationshipReadModeReason } else { 'RELATIONSHIP_NOT_OBSERVED' })
            summaryLines = $summary; observation = $Observation; resolution = $resolution
        }
        Write-ScomUtf8File $evidencePath ($evidence | ConvertTo-Json -Depth 20)
        $written += $evidencePath
        if ($exitCode -in 0, 3) {
            $document = New-ScomWorkerDocument -TemplateJson $TemplateJson -Resolution $resolution -Observation $Observation `
                -ScopeKind $ScopeKind -GroupId $GroupId -ModulePath $ModulePath
            Write-ScomUtf8File $OutputPath ($document | ConvertTo-Json -Depth 20)
            $written += $OutputPath
        }
    }
    [pscustomobject]@{ ExitCode = $exitCode; Resolution = $resolution; Summary = $summary; Written = $written }
}

function Assert-ScomBudget {
    if ($script:Watch.Elapsed.TotalSeconds -ge $script:DeadlineSeconds) {
        throw "GENERATOR_DEADLINE: the local budget of $($script:DeadlineSeconds) s is exhausted. This stops this process only; it does not stop work already requested from SCOM."
    }
}

function Get-ScomClassChain {
    param([string] $Name, $Connection)
    if ($script:ClassChains.ContainsKey($Name)) { return $script:ClassChains[$Name] }
    $chain = New-Object System.Collections.Generic.List[string]
    try {
        $type = @(Get-SCOMClass -Name $Name -SCSession $Connection) | Select-Object -First 1
        while ($null -ne $type) {
            $chain.Add([string]$type.Name)
            $type = try { $type.GetBaseType() } catch { $null }
        }
    }
    catch { $chain.Clear() }
    $script:ClassChains[$Name] = @($chain)
    $script:ClassChains[$Name]
}

function Get-ScomRawProperties {
    param($Object)
    $table = [ordered]@{}
    foreach ($property in $Object.GetProperties()) {
        $value = $null
        try { $value = $Object[$property].Value } catch { $value = $null }
        $table[[string]$property.Name] = $(if ($null -eq $value) { $null }
            elseif ($value -is [IConvertible]) { [Convert]::ToString($value, [Globalization.CultureInfo]::InvariantCulture) }
            else { [string]$value })
    }
    $table
}

function Get-ScomEdges {
    param($Source, [string] $SourceKind, $Connection)
    foreach ($edge in @(Get-SCOMRelationshipInstance -SourceInstance $Source -SCSession $Connection)) {
        $id = [string]$edge.RelationshipId
        if (-not $script:RelationshipNames.ContainsKey($id)) {
            $script:RelationshipNames[$id] = [string](Get-SCOMRelationship -Id $edge.RelationshipId -SCSession $Connection).Name
        }
        $target = $edge.TargetObject
        $classes = @($target.GetMostDerivedClasses() | ForEach-Object { [string]$_.Name })
        $kind = 'other'
        foreach ($class in $classes) {
            $chain = Get-ScomClassChain $class $Connection
            if ($chain -contains 'Microsoft.Windows.OperatingSystem') { $kind = 'os'; break }
            if ($chain -contains 'Microsoft.Windows.LogicalDisk') { $kind = 'disk'; break }
        }
        [ordered]@{
            source = $SourceKind; relationship = $script:RelationshipNames[$id]; kind = $kind
            targetId = $target.Id.ToString(); targetMostDerivedClasses = $classes
            properties = $(if ($kind -ne 'other') { Get-ScomRawProperties $target } else { $null })
            targetObject = $target
        }
    }
}

function Get-ScomMappingObservation {
    param(
        [Parameter(Mandatory)] [string] $ManagementServer, [Parameter(Mandatory)] [string] $ScopeKind, [string] $GroupId,
        [Parameter(Mandatory)] [int] $SampleComputers, [Parameter(Mandatory)] [string] $ModulePath
    )
    Import-Module $ModulePath -ErrorAction Stop
    $connection = New-SCOMManagementGroupConnection -ComputerName $ManagementServer -PassThru
    $mg = Get-SCOMManagementGroup -SCSession $connection
    Assert-ScomBudget
    $computerClass = @(Get-SCOMClass -Name 'Microsoft.Windows.Computer' -SCSession $connection) | Select-Object -First 1
    if ($null -eq $computerClass) { throw 'The Microsoft.Windows.Computer class is not installed in this management group.' }
    if ($ScopeKind -eq 'group') {
        $group = @(Get-SCOMGroup -Id ([guid]$GroupId) -SCSession $connection) | Select-Object -First 1
        if ($null -eq $group) { throw "Group $GroupId was not found." }
        $sample = @($group.GetRelatedMonitoringObjects() | Where-Object { $_.IsInstanceOf($computerClass) } | Select-Object -First $SampleComputers)
    }
    else {
        $sample = @($computerClass | Get-SCOMClassInstance -SCSession $connection | Select-Object -First $SampleComputers)
    }
    Assert-ScomBudget

    $records = New-Object System.Collections.Generic.List[object]
    foreach ($computer in $sample) {
        Assert-ScomBudget
        $classes = @($computer.GetMostDerivedClasses() | ForEach-Object { [string]$_.Name })
        foreach ($class in $classes) { $null = Get-ScomClassChain $class $connection }
        $edges = @(Get-ScomEdges $computer 'computer' $connection)
        if (@($edges | Where-Object { $_.kind -eq 'disk' }).Count -eq 0) {
            $os = @($edges | Where-Object { $_.kind -eq 'os' })
            if ($os.Count -eq 1) { $edges += @(Get-ScomEdges $os[0].targetObject 'os' $connection | Where-Object { $_.kind -ne 'os' }) }
        }
        $records.Add([ordered]@{
                id = $computer.Id.ToString(); name = [string]$computer.DisplayName
                mostDerivedClasses = $classes; properties = (Get-ScomRawProperties $computer)
                edges = @($edges | ForEach-Object { $_.Remove('targetObject'); $_ })
            })
    }
    $chains = [ordered]@{}
    foreach ($name in @($script:ClassChains.Keys | Sort-Object)) { $chains[$name] = @($script:ClassChains[$name]) }

    $hostingNames = @($records | ForEach-Object { @($_.edges) } | Where-Object { $_.kind -ne 'other' } |
            ForEach-Object { $_.relationship } | Sort-Object -Unique)
    $derived = @()
    try {
        $chainsByType = @(foreach ($type in @(Get-SCOMRelationship -SCSession $connection)) {
                $chain = New-Object System.Collections.Generic.List[string]
                $walk = $type
                while ($null -ne $walk) { $chain.Add([string]$walk.Name); $walk = try { $walk.GetBaseType() } catch { $null } }
                [pscustomobject]@{ name = [string]$type.Name; chain = @($chain) }
            })
        $derived = @(foreach ($name in $hostingNames) {
                [ordered]@{ name = $name; derived = @($chainsByType | Where-Object { $_.name -ne $name -and $_.chain -contains $name } | ForEach-Object { $_.name }) }
            })
    }
    catch { $derived = @() }

    [ordered]@{
        managementServer = $ManagementServer; managementGroupId = $mg.Id.ToString(); managementGroupName = [string]$mg.Name
        modulePath = $ModulePath; sampleLimit = $SampleComputers
        classChains = $chains; derivedRelationshipTypes = $derived; computers = $records.ToArray()
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Functions') { return }

function Stop-ScomScript {
    param([string] $Message, [int] $Code)
    [Console]::Error.WriteLine("ERROR: $Message")
    exit $Code
}

$script:DeadlineSeconds = $DeadlineSeconds
try {
    if (-not $WorkerTemplatePath) { $WorkerTemplatePath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\examples\worker.example.json' }
    if ($ScopeKind -eq 'group') {
        $parsed = [guid]::Empty
        if (-not [guid]::TryParse($GroupId, [ref]$parsed) -or $parsed -eq [guid]::Empty) { Stop-ScomScript '-GroupId must be the GUID of a SCOM group when -ScopeKind is group.' 1 }
    }
    elseif ($GroupId) { Stop-ScomScript '-GroupId must not be set when -ScopeKind is managementGroup.' 1 }
    if (-not (Test-Path -LiteralPath $WorkerTemplatePath -PathType Leaf)) { Stop-ScomScript "Worker template not found: $WorkerTemplatePath" 1 }
    if ($PSVersionTable.PSEdition -ne 'Desktop') { Stop-ScomScript 'Run this script in Windows PowerShell 5.1; the OperationsManager module requires it. Nothing was written.' 2 }
    if (-not (Test-Path -LiteralPath $OperationsManagerModule -PathType Leaf)) {
        Stop-ScomScript "The OperationsManager module was not found at '$OperationsManagerModule'. Install the Operations Manager console or pass -OperationsManagerModule. Nothing was written." 2
    }
    $outputFull = [IO.Path]::GetFullPath($OutputPath)
    if (-not $Force -and ((Test-Path -LiteralPath $outputFull) -or (Test-Path -LiteralPath "$outputFull.evidence.json"))) {
        Stop-ScomScript "Refusing to overwrite existing '$outputFull' or its evidence file. Use -Force to replace them." 1
    }
    $outputDirectory = Split-Path -Parent $outputFull
    if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) { Stop-ScomScript "Output directory does not exist: $outputDirectory" 1 }
    $templateJson = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $WorkerTemplatePath).ProviderPath)
    $null = $templateJson | ConvertFrom-Json
}
catch { Stop-ScomScript $_.Exception.Message 1 }

try {
    $observation = Get-ScomMappingObservation -ManagementServer $ManagementServer -ScopeKind $ScopeKind -GroupId $GroupId `
        -SampleComputers $SampleComputers -ModulePath $OperationsManagerModule
}
catch { Stop-ScomScript "Discovery failed: $($_.Exception.Message) Nothing was written." 2 }
try {
    $outcome = Invoke-ScomMappingProfile -Observation $observation -TemplateJson $templateJson -ScopeKind $ScopeKind -GroupId $GroupId `
        -OutputPath $outputFull -SampleComputers $SampleComputers -DeadlineSeconds $DeadlineSeconds -ModulePath $OperationsManagerModule -Force:$Force
}
catch { Stop-ScomScript $_.Exception.Message 1 }

$outcome.Summary | ForEach-Object { Write-Host $_ }
foreach ($path in $outcome.Written) { Write-Host "Wrote: $path" }
switch ($outcome.ExitCode) {
    0 { Write-Host 'Result: READY FOR REVIEW. Review the summary above, then run Approve-ScomMappingProfile.ps1.' }
    3 { Stop-ScomScript 'Result: UNITS NOT PROVEN. worker.json carries null units and cannot be approved. Use the manual path in the deployment guide (6e/6f).' 3 }
    4 { Stop-ScomScript 'Result: MAPPING AMBIGUOUS OR INCOMPLETE. No worker.json was written; see the evidence file and the manual path in the deployment guide (6e/6f).' 4 }
    default { Stop-ScomScript 'Result: nothing was generated.' $outcome.ExitCode }
}
exit $outcome.ExitCode
