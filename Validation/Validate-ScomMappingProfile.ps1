[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$generator = Join-Path $root 'Setup\New-ScomMappingProfile.ps1'
$approver = Join-Path $root 'Setup\Approve-ScomMappingProfile.ps1'
$templatePath = Join-Path $root 'examples\worker.example.json'
foreach ($path in $generator, $approver, $templatePath) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing file: $path" }
}
. $generator -LoadFunctionsOnly
. $approver -LoadFunctionsOnly

$templateJson = [IO.File]::ReadAllText($templatePath)
$work = Join-Path ([IO.Path]::GetTempPath()) ('scom-mapping-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$script:Checks = 0

function Assert-That {
    param([bool] $Condition, [string] $Message)
    $script:Checks++
    if (-not $Condition) { throw "Mapping profile validation failed: $Message" }
}

$chains = @{
    'Microsoft.Windows.Server.10.0.Full.Computer' = @('Microsoft.Windows.Server.10.0.Full.Computer', 'Microsoft.Windows.Server.Computer', 'Microsoft.Windows.Computer')
    'Microsoft.Windows.Server.2016.Computer' = @('Microsoft.Windows.Server.2016.Computer', 'Microsoft.Windows.Computer')
    'Microsoft.Windows.Server.10.0.Full.OperatingSystem' = @('Microsoft.Windows.Server.10.0.Full.OperatingSystem', 'Microsoft.Windows.Server.OperatingSystem', 'Microsoft.Windows.OperatingSystem')
    'Microsoft.Windows.Server.10.0.LogicalDisk' = @('Microsoft.Windows.Server.10.0.LogicalDisk', 'Microsoft.Windows.Server.LogicalDisk', 'Microsoft.Windows.LogicalDisk')
    'Microsoft.Windows.NetworkAdapter' = @('Microsoft.Windows.NetworkAdapter', 'Microsoft.Windows.LogicalHardware')
}

function New-Edge {
    param([string] $Source, [string] $Relationship, [string] $Kind, [string[]] $Classes, $Properties)
    [ordered]@{ source = $Source; relationship = $Relationship; kind = $Kind; targetId = [guid]::NewGuid().ToString(); targetMostDerivedClasses = $Classes; properties = $Properties }
}

function New-Computer {
    param(
        [string] $Name, [string] $Memory = '14679604', $DiskProperties = @{ DeviceID = 'C:'; FileSystem = 'NTFS'; Size = '135839870976'; SizeNumeric = '-1'; SizeInMBs = '129547' },
        [string] $OsRelationship = 'Microsoft.Windows.ComputerHostsOperatingSystem', [string] $DiskRelationship = 'Microsoft.Windows.ComputerHostsLogicalDevice',
        [string] $DiskSource = 'computer', [string] $ComputerClass = 'Microsoft.Windows.Server.10.0.Full.Computer',
        [string] $OsClass = 'Microsoft.Windows.Server.10.0.Full.OperatingSystem', [string] $DiskClass = 'Microsoft.Windows.Server.10.0.LogicalDisk',
        [switch] $NetworkAdapter, [switch] $NoDisk
    )
    $edges = @(New-Edge 'computer' $OsRelationship 'os' @($OsClass) ([ordered]@{ OSVersion = '10.0.20348.0'; OSVersionDisplayName = 'Windows Server 2022'; PhysicalMemory = $Memory }))
    if (-not $NoDisk) { $edges += New-Edge $DiskSource $DiskRelationship 'disk' @($DiskClass) $DiskProperties }
    if ($NetworkAdapter) { $edges += New-Edge 'computer' $DiskRelationship 'other' @('Microsoft.Windows.NetworkAdapter') $null }
    [ordered]@{
        id = [guid]::NewGuid().ToString(); name = $Name; mostDerivedClasses = @($ComputerClass)
        properties = [ordered]@{ PrincipalName = "$Name.contoso.com"; LogicalProcessors = '4' }; edges = $edges
    }
}

function New-Observation {
    param($Computers)
    [ordered]@{
        managementServer = 'scom01.contoso.com'; managementGroupId = 'a951d50d-b46c-380b-eaa4-49dee6fd563e'; managementGroupName = 'LAB'
        modulePath = 'C:\fake\OperationsManager.psd1'; sampleLimit = 3; classChains = $chains; derivedRelationshipTypes = @(); computers = @($Computers)
    }
}

function Invoke-Generator {
    param($Observation, [string] $Name, [string] $ScopeKind = 'managementGroup', [string] $GroupId, [switch] $Force)
    $out = Join-Path $work "$Name.json"
    $outcome = Invoke-ScomMappingProfile -Observation $Observation -TemplateJson $templateJson -ScopeKind $ScopeKind -GroupId $GroupId -OutputPath $out -Force:$Force
    [pscustomobject]@{ Outcome = $outcome; Path = $out; Mapping = $(if (Test-Path -LiteralPath $out) { ([IO.File]::ReadAllText($out) | ConvertFrom-Json).profile.configuration } else { $null }) }
}

function Get-Many { param([int] $Count, [scriptblock] $Factory) @(1..$Count | ForEach-Object { & $Factory $_ }) }

# Lab-shaped happy path: KB memory, SizeInMBs, computer-hosted disks, derived classes, three computers.
$run = Invoke-Generator (New-Observation (Get-Many 3 { param($i) New-Computer "srv$i" })) 'lab'
Assert-That ($run.Outcome.ExitCode -eq 0) "lab-shaped observation should be Ready, got $($run.Outcome.ExitCode): $($run.Outcome.Resolution.errors -join '; ')"
$map = $run.Mapping.windowsMapping
Assert-That ($map.memoryToMiB -eq 0.0009765625) 'KB memory must give 0.0009765625'
Assert-That ($map.volumeSizeProperty -eq 'SizeInMBs' -and $map.volumeSizeToMiB -eq 1) 'SizeInMBs must be preferred with factor 1'
Assert-That ($map.computerHostsOsRelationship -eq 'Microsoft.Windows.ComputerHostsOperatingSystem') 'OS relationship must be the observed one'
Assert-That ($map.computerHostsVolumeRelationship -eq 'Microsoft.Windows.ComputerHostsLogicalDevice') 'computer-hosted disk relationship must be set'
Assert-That ($null -eq $map.PSObject.Properties['osHostsVolumeRelationship']) 'osHostsVolumeRelationship must be absent for computer-hosted disks'
Assert-That ($map.computerClassName -eq 'Microsoft.Windows.Computer' -and $map.volumeClassName -eq 'Microsoft.Windows.Server.LogicalDisk') 'base class names must come from the template'
$allowed = $run.Mapping.allowedPropertiesByClass
Assert-That (@($allowed.PSObject.Properties.Name | Sort-Object) -join ',' -eq 'Microsoft.Windows.Server.10.0.Full.Computer,Microsoft.Windows.Server.10.0.Full.OperatingSystem,Microsoft.Windows.Server.10.0.LogicalDisk') 'allowedPropertiesByClass must be keyed by the observed most-derived classes'
Assert-That (@($allowed.'Microsoft.Windows.Server.10.0.LogicalDisk') -join ',' -eq 'DeviceID,FileSystem,SizeInMBs') 'disk allowed properties'
Assert-That (@($run.Mapping.additionalRelationshipNames) -join ',' -eq 'Microsoft.Windows.ComputerHostsOperatingSystem,Microsoft.Windows.ComputerHostsLogicalDevice') 'additionalRelationshipNames must be the two observed relationships'
Assert-That ($run.Mapping.scopeKind -eq 'managementGroup' -and $null -eq $run.Mapping.groupId -and $run.Mapping.wholeManagementGroupApproved -eq $true) 'whole-MG scope fields'
foreach ($flag in 'scopeVisibilityVerified', 'membershipQueryContractVerified', 'enumerationQueryContractVerified', 'additionalRelationshipContractVerified') {
    Assert-That ($run.Mapping.$flag -eq $false) "$flag must be false after generation"
}
Assert-That ($map.verified -eq $false -and $map.agentMonitoringVerified -eq $false) 'mapping flags must be false after generation'
Assert-That (Test-Path -LiteralPath "$($run.Path).evidence.json") 'evidence file must be written'
Assert-That (@($run.Outcome.Summary | Where-Object { $_ -match 'local consumption only' }).Count -eq 1) 'summary must say the bounds are local only'
$runEvidence = Get-Content -LiteralPath "$($run.Path).evidence.json" -Raw | ConvertFrom-Json
Assert-That ($run.Mapping.relationshipReadMode -eq 'set') 'both relationships observed must write set read mode'
Assert-That ($runEvidence.relationshipReadMode -eq 'set' -and $runEvidence.relationshipReadModeReason -eq 'BOTH_RELATIONSHIPS_OBSERVED') 'evidence must record the chosen read mode and reason'
Assert-That (@($run.Outcome.Summary | Where-Object { $_ -match 'Relationship read: set' }).Count -eq 1) 'summary must state the read mode'

# Group scope keeps the group id and does not approve the whole management group.
$group = Invoke-Generator (New-Observation (Get-Many 1 { param($i) New-Computer 'g1' })) 'group' 'group' '11111111-2222-3333-4444-555555555555'
Assert-That ($group.Outcome.ExitCode -eq 0 -and $group.Mapping.groupId -eq '11111111-2222-3333-4444-555555555555' -and $group.Mapping.wholeManagementGroupApproved -eq $false) 'group scope fields'

# Memory units.
$mb = Invoke-Generator (New-Observation (Get-Many 2 { param($i) New-Computer "m$i" -Memory '16384' })) 'mb'
Assert-That ($mb.Outcome.ExitCode -eq 0 -and $mb.Mapping.windowsMapping.memoryToMiB -eq 1) 'MB memory must give factor 1'
$bytes = Invoke-Generator (New-Observation (Get-Many 2 { param($i) New-Computer "b$i" -Memory '17179869184' })) 'bytes'
Assert-That ($bytes.Outcome.ExitCode -eq 0 -and $bytes.Mapping.windowsMapping.memoryToMiB -eq 9.5367431640625E-07) 'byte memory must give 1/1048576'

# Disk units.
$bytesDisk = @{ DeviceID = 'C:'; FileSystem = 'NTFS'; Size = '135839870976'; SizeNumeric = '135839870976' }
$byBytes = Invoke-Generator (New-Observation @(New-Computer 'd1' -DiskProperties $bytesDisk)) 'diskbytes'
Assert-That ($byBytes.Outcome.ExitCode -eq 0 -and $byBytes.Mapping.windowsMapping.volumeSizeProperty -eq 'SizeNumeric' -and $byBytes.Mapping.windowsMapping.volumeSizeToMiB -eq 9.5367431640625E-07) 'byte-only disk size must be read as bytes'
$sizeOnly = @{ DeviceID = 'C:'; FileSystem = 'NTFS'; Size = '135839870976' }
$bySize = Invoke-Generator (New-Observation @(New-Computer 'd2' -DiskProperties $sizeOnly)) 'disksize'
Assert-That ($bySize.Outcome.ExitCode -eq 0 -and $bySize.Mapping.windowsMapping.volumeSizeProperty -eq 'Size') 'Size must be accepted as bytes when it is the only size'

# Implausible or disagreeing units: null factor, non-zero exit, evidence still written.
$tiny = Invoke-Generator (New-Observation @(New-Computer 'u1' -Memory '12')) 'implausible'
Assert-That ($tiny.Outcome.ExitCode -eq 3 -and $null -eq $tiny.Mapping.windowsMapping.memoryToMiB) 'implausible memory must write null and exit 3'
Assert-That (Test-Path -LiteralPath "$($tiny.Path).evidence.json") 'evidence must be written when units are unresolved'
$ambiguous = Invoke-Generator (New-Observation @(New-Computer 'u2' -Memory '1048576')) 'ambiguous'
Assert-That ($ambiguous.Outcome.ExitCode -eq 3 -and $null -eq $ambiguous.Mapping.windowsMapping.memoryToMiB) 'memory plausible under two units must write null and exit 3'
$disagree = @{ DeviceID = 'C:'; FileSystem = 'NTFS'; Size = '135839870976'; SizeInMBs = '9999' }
$bad = Invoke-Generator (New-Observation @(New-Computer 'u3' -DiskProperties $disagree)) 'disagree'
Assert-That ($bad.Outcome.ExitCode -eq 3 -and $null -eq $bad.Mapping.windowsMapping.volumeSizeToMiB) 'disagreeing disk sizes must write null and exit 3'
$noSize = Invoke-Generator (New-Observation @(New-Computer 'u4' -DiskProperties @{ DeviceID = 'C:'; FileSystem = 'NTFS' })) 'nosize'
Assert-That ($noSize.Outcome.ExitCode -eq 3) 'a disk without any size property must exit 3'

# Zero computers: non-zero exit, nothing written.
$zero = Invoke-Generator (New-Observation @()) 'zero'
Assert-That ($zero.Outcome.ExitCode -eq 2 -and -not (Test-Path -LiteralPath $zero.Path) -and -not (Test-Path -LiteralPath "$($zero.Path).evidence.json")) 'zero computers must exit 2 and write nothing'
Assert-That (@(@($zero.Outcome.Summary) -match 'Next:').Count -eq 0) 'a non-zero exit must not print the Next: approve hint'
$readyHint = Invoke-Generator (New-Observation (Get-Many 1 { param($i) New-Computer "n$i" })) 'nexthint'
Assert-That (@(@($readyHint.Outcome.Summary) -match 'Next:').Count -eq 1) 'a Ready run must print the Next: approve hint'

# OS-hosted disks.
$osHosted = Invoke-Generator (New-Observation (Get-Many 2 { param($i) New-Computer "o$i" -DiskSource 'os' -DiskRelationship 'Contoso.OsHostsDisk' })) 'oshosted'
Assert-That ($osHosted.Outcome.ExitCode -eq 0) "OS-hosted disks should be Ready: $($osHosted.Outcome.Resolution.errors -join '; ')"
Assert-That ($osHosted.Mapping.windowsMapping.osHostsVolumeRelationship -eq 'Contoso.OsHostsDisk' -and $null -eq $osHosted.Mapping.windowsMapping.PSObject.Properties['computerHostsVolumeRelationship']) 'OS-hosted disks must set only osHostsVolumeRelationship'
$mixed = Invoke-Generator (New-Observation @((New-Computer 'x1'), (New-Computer 'x2' -DiskSource 'os' -DiskRelationship 'Contoso.OsHostsDisk'))) 'mixed'
Assert-That ($mixed.Outcome.ExitCode -eq 4 -and -not (Test-Path -LiteralPath $mixed.Path)) 'disks hosted two ways must be ambiguous (exit 4) with no worker file'

# Derived OS and disk classes: keys are the most-derived classes, base-class chain is honored.
$derived = Invoke-Generator (New-Observation @(
        (New-Computer 'v1'),
        (New-Computer 'v2' -ComputerClass 'Microsoft.Windows.Server.2016.Computer' -OsClass 'Microsoft.Windows.Server.10.0.Full.OperatingSystem'))) 'derived'
Assert-That ($derived.Outcome.ExitCode -eq 0 -and @($derived.Mapping.allowedPropertiesByClass.PSObject.Properties).Count -eq 4) 'two computer classes plus one OS and one disk class must give four allowed-property keys'
$chains['Contoso.Unrelated.Disk'] = @('Contoso.Unrelated.Disk', 'Contoso.Thing')
$foreign = New-Computer 'f1' -DiskClass 'Contoso.Unrelated.Disk'
$notDisk = Invoke-Generator (New-Observation @($foreign)) 'notdisk'
Assert-That ($notDisk.Outcome.ExitCode -eq 4) 'a hosted device whose class does not derive from the disk base must not become the disk mapping'

# Non-disk logical devices sharing the relationship are ignored with a warning.
$nics = Invoke-Generator (New-Observation (Get-Many 2 { param($i) New-Computer "n$i" -NetworkAdapter })) 'nics'
Assert-That ($nics.Outcome.ExitCode -eq 0 -and @($nics.Outcome.Resolution.warnings | Where-Object { $_ -match 'non-disk' }).Count -eq 1) 'non-disk logical devices must be ignored with a warning'
Assert-That ($nics.Mapping.windowsMapping.computerHostsVolumeRelationship -eq 'Microsoft.Windows.ComputerHostsLogicalDevice' -and @($nics.Mapping.allowedPropertiesByClass.PSObject.Properties.Name) -notcontains 'Microsoft.Windows.NetworkAdapter') 'network adapters must not enter the mapping or the allow-list'
Assert-That ($nics.Mapping.relationshipReadMode -eq 'perSource') 'non-disk edges sharing the disk relationship must keep per-source reads'
$nicEvidence = Get-Content -LiteralPath "$($nics.Path).evidence.json" -Raw | ConvertFrom-Json
Assert-That ($nicEvidence.relationshipReadMode -eq 'perSource' -and $nicEvidence.relationshipReadModeReason -eq 'NON_DISK_EDGES_SHARE_DISK_RELATIONSHIP') 'evidence must record why set reads were not chosen'

# Derived relationship types are reported.
$observation = New-Observation (Get-Many 1 { param($i) New-Computer 'r1' })
$observation.derivedRelationshipTypes = @([ordered]@{ name = 'Microsoft.Windows.ComputerHostsLogicalDevice'; derived = @('Contoso.Derived') })
$rel = Invoke-Generator $observation 'derivedrel'
Assert-That (@($rel.Outcome.Resolution.warnings | Where-Object { $_ -match 'derived types' }).Count -eq 1) 'derived relationship types must be reported'

# Missing property.
$missing = New-Computer 'p1' -DiskProperties @{ DeviceID = 'C:'; Size = '135839870976'; SizeInMBs = '129547' }
$prop = Invoke-Generator (New-Observation @($missing)) 'missingprop'
Assert-That ($prop.Outcome.ExitCode -eq 4) 'a required property that was not observed must be ambiguous (exit 4)'

# Lab regression: @() over a List[object] of ordered dictionaries threw "Argument types do not match"
# on Windows PowerShell 5.1 in the lab, so the observation must hand records over with ToArray().
$generatorSource = [IO.File]::ReadAllText($generator)
Assert-That ($generatorSource -notmatch '@\(\$records\)') 'observation records must not be wrapped in @() (use .ToArray())'
Assert-That ($generatorSource -match 'computers = \$records\.ToArray\(\)') 'observation records must be returned via .ToArray()'
$recordList = New-Object System.Collections.Generic.List[object]
$recordList.Add([ordered]@{ id = 'a'; edges = @([ordered]@{ kind = 'os' }) })
Assert-That ($recordList.ToArray().Count -eq 1) 'record list must convert to an array'

# Refuse to overwrite.
$threw = $false
try { Invoke-Generator (New-Observation (Get-Many 1 { param($i) New-Computer 'w1' })) 'lab' | Out-Null } catch { $threw = $true }
Assert-That $threw 'existing output must not be overwritten without -Force'
Assert-That ((Invoke-Generator (New-Observation (Get-Many 1 { param($i) New-Computer 'w1' })) 'lab' -Force).Outcome.ExitCode -eq 0) '-Force must allow overwrite'

# Approve: exactly six flags change and the attestation is written.
$worker = Join-Path $work 'lab.json'
$evidenceFile = "$worker.evidence.json"
$before = [IO.File]::ReadAllText($worker) | ConvertFrom-Json
$attestation = Join-Path $work 'lab.json.attestation.txt'
$text = Set-ScomMappingProfileApproval -WorkerPath $worker -EvidencePath $evidenceFile -AttestationPath $attestation -Operator 'CONTOSO\operator'
$after = [IO.File]::ReadAllText($worker) | ConvertFrom-Json
function Get-FlatPaths {
    param($Value, [string] $Path = '')
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { [pscustomobject]@{ Path = $Path; Value = (ConvertTo-Json $Value -Compress) }; return }
    if ($Value -is [System.Collections.IEnumerable]) { $i = 0; foreach ($item in $Value) { Get-FlatPaths $item "$Path[$i]"; $i++ }; return }
    foreach ($property in $Value.PSObject.Properties) { Get-FlatPaths $property.Value "$Path/$($property.Name)" }
}
$old = @{}; Get-FlatPaths $before | ForEach-Object { $old[$_.Path] = $_.Value }
$changed = @(Get-FlatPaths $after | Where-Object { $old[$_.Path] -ne $_.Value } | ForEach-Object { $_.Path } | Sort-Object)
$expected = @('/profile/configuration/additionalRelationshipContractVerified', '/profile/configuration/enumerationQueryContractVerified',
    '/profile/configuration/membershipQueryContractVerified', '/profile/configuration/scopeVisibilityVerified',
    '/profile/configuration/windowsMapping/agentMonitoringVerified', '/profile/configuration/windowsMapping/verified') | Sort-Object
Assert-That (($changed -join '|') -eq ($expected -join '|')) "Approve must change exactly six flags, changed: $($changed -join ', ')"
Assert-That (@(Get-FlatPaths $after).Count -eq @(Get-FlatPaths $before).Count) 'Approve must not add or remove any field'
foreach ($path in $expected) { Assert-That (($after.profile.configuration.windowsMapping.verified) -eq $true) 'verified must be true' }
Assert-That ($after.liveChecks.queryContractVerified -eq $false) 'Approve must not touch liveChecks'
$attText = [IO.File]::ReadAllText($attestation)
$hash = (Get-FileHash -LiteralPath $evidenceFile -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-That ($attText.Contains($hash) -and $attText.Contains('CONTOSO\operator') -and $attText -match 'Approved \(UTC\): \d{4}-\d{2}-\d{2}T') 'attestation must carry operator, UTC date and evidence SHA-256'
$again = $false
try { Set-ScomMappingProfileApproval -WorkerPath $worker -EvidencePath $evidenceFile -AttestationPath $attestation | Out-Null } catch { $again = $true }
Assert-That $again 'Approve must refuse a file that is already approved'

# Approve refuses null units and edited mappings.
$nullWorker = $tiny.Path
$refused = $false
try { Set-ScomMappingProfileApproval -WorkerPath $nullWorker -EvidencePath "$nullWorker.evidence.json" -AttestationPath (Join-Path $work 'null.attestation.txt') | Out-Null } catch { $refused = $true }
Assert-That ($refused -and -not (Test-Path -LiteralPath (Join-Path $work 'null.attestation.txt'))) 'Approve must refuse null units'
$edited = Invoke-Generator (New-Observation (Get-Many 1 { param($i) New-Computer 'e1' })) 'edited'
$json = [IO.File]::ReadAllText($edited.Path) | ConvertFrom-Json
$json.profile.configuration.windowsMapping.hostnameProperty = 'Other'
[IO.File]::WriteAllText($edited.Path, ($json | ConvertTo-Json -Depth 20))
$refused = $false
try { Set-ScomMappingProfileApproval -WorkerPath $edited.Path -EvidencePath "$($edited.Path).evidence.json" -AttestationPath (Join-Path $work 'e.attestation.txt') | Out-Null } catch { $refused = $true }
Assert-That $refused 'Approve must refuse a mapping edited after generation'

# Script entry point without the OperationsManager module: non-zero, nothing written.
$hostExe = (Get-Process -Id $PID).Path
$childOut = Join-Path $work 'child.json'
$ErrorActionPreference = 'Continue'
& $hostExe -NoProfile -ExecutionPolicy Bypass -File $generator -ManagementServer scom01.contoso.com -ScopeKind managementGroup -OutputPath $childOut `
    -OperationsManagerModule (Join-Path $work 'missing.psd1') 2>&1 | Out-Null
$missingModuleExit = $LASTEXITCODE
& $hostExe -NoProfile -ExecutionPolicy Bypass -File $generator -ManagementServer scom01.contoso.com -ScopeKind group -GroupId 'not-a-guid' -OutputPath $childOut 2>&1 | Out-Null
$badGroupExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
Assert-That ($missingModuleExit -eq 2) "missing module must exit 2, got $missingModuleExit"
Assert-That (-not (Test-Path -LiteralPath $childOut) -and -not (Test-Path -LiteralPath "$childOut.evidence.json")) 'missing module must write nothing'
Assert-That ($badGroupExit -eq 1 -and -not (Test-Path -LiteralPath $childOut)) 'group scope without a GUID must exit 1 and write nothing'

Remove-Item -LiteralPath $work -Recurse -Force
"SCOM_MAPPING_PROFILE_VALIDATION_PASSED checks=$($script:Checks)"
