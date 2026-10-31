$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$collectorPath = Join-Path $PSScriptRoot 'Collect-SCOMSnapshot.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($collectorPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw ($parseErrors | Out-String) }

# Load only these function definitions, never the collector's top-level SDK or publication code.
$functionNames = @('Read-CollectorConfiguration', 'Write-AtomicJson', 'Set-CollectorUnavailable', 'Publish-SCOMSnapshot',
    'Get-Number', 'Convert-ScomUtc', 'Add-ObservedSamples', 'Get-ObservationStatus')
foreach ($name in $functionNames) {
    $definitions = @($ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq $name
    })
    if ($definitions.Count -ne 1) { throw "Expected exactly one collector function: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}

$script:testCount = 0
$script:assertionCount = 0
$script:started = [DateTime]::Parse('2026-09-28T16:00:00Z').ToUniversalTime()
$entityId = '00000000-0000-0000-0000-000000000001'
$otherEntityId = '00000000-0000-0000-0000-000000000002'
$ruleId = '11111111-1111-1111-1111-111111111111'
$otherRuleId = '22222222-2222-2222-2222-222222222222'

function Reset-Observations {
    $script:sampleIndex = @{}
    $script:observationCoverage = @{}
    $script:limitations = New-Object 'Collections.Generic.List[string]'
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    $script:assertionCount++
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Message)
    Assert-True ($Actual -ceq $Expected) "$Message Expected '$Expected'; got '$Actual'."
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$MessagePattern)
    $failure = $null
    try { & $Action } catch { $failure = $_.Exception.Message }
    Assert-True ($null -ne $failure) 'Expected an exception, but collection succeeded.'
    Assert-True ($failure -like $MessagePattern) "Unexpected exception: $failure"
}

function Invoke-Test {
    param([string]$Name, [scriptblock]$Body)
    Reset-Observations
    try { & $Body } catch { throw "Collector validation failed [$Name]: $($_.Exception.Message)" }
    $script:testCount++
}

function New-FakeSample {
    param($Value, [DateTime]$At = $started.AddMinutes(-5))
    [pscustomobject]@{ SampleValue = $Value; TimeSampled = $At }
}

function New-FakeSeries {
    param([string]$ObjectName, [string]$CounterName, [object[]]$Samples,
        [string]$Rule = $ruleId, [string]$InstanceName = '_Total')
    $series = [pscustomobject]@{
        ObjectName = $ObjectName; CounterName = $CounterName; InstanceName = $InstanceName
        RuleId = $Rule; Samples = $Samples; Calls = 0; Start = $null; End = $null
    }
    $series | Add-Member ScriptMethod GetValues {
        param([DateTime]$Start, [DateTime]$End)
        $this.Calls++
        $this.Start = $Start
        $this.End = $End
        return ,$this.Samples
    }
    return $series
}

function New-FakeObject {
    param([object[]]$Series)
    $object = [pscustomobject]@{ Series = $Series }
    $object | Add-Member ScriptMethod GetMonitoringPerformanceData { return ,$this.Series }
    return $object
}

$metrics = @(
    @{ Object = 'Processor Information'; Counter = '% Processor Time'; Metric = 'cpuPercent'; Volume = $false; Optimized = $true },
    @{ Object = 'Memory'; Counter = 'PercentMemoryUsed'; Metric = 'memoryUsedPercent'; Volume = $false; Optimized = $false },
    @{ Object = 'Memory'; Counter = 'Available MBytes'; Metric = 'memoryAvailableMiB'; Volume = $false; Optimized = $true },
    @{ Object = 'LogicalDisk'; Counter = '% Free Space'; Metric = 'diskFreePercent'; Volume = $true; Optimized = $true }
)

foreach ($metric in $metrics) {
    Invoke-Test "$($metric.Metric): null plus valid and zero" {
        $series = New-FakeSeries $metric.Object $metric.Counter @(
            (New-FakeSample $null $started.AddMinutes(-30)),
            (New-FakeSample 15.5 $started.AddMinutes(-20)),
            (New-FakeSample $null $started.AddMinutes(-40)),
            (New-FakeSample 0 $started.AddMinutes(-10))
        )
        Add-ObservedSamples (New-FakeObject @($series)) $entityId $metric.Volume
        Assert-Equal $sampleIndex.Count 2 'Only actual numeric samples must survive.'
        $samples = @($sampleIndex.Values | Sort-Object observedAtUtc)
        Assert-Equal $samples[0].value 15.5 'Numeric value must be preserved.'
        Assert-Equal $samples[1].value 0 'Actual zero must not be treated as missing.'
        Assert-Equal $samples[0].observedAtUtc ($started.AddMinutes(-20).ToString('o')) 'Actual timestamp must survive.'
        Assert-Equal $samples[1].observedAtUtc ($started.AddMinutes(-10).ToString('o')) 'Zero timestamp must survive.'
        foreach ($sample in $samples) {
            Assert-Equal $sample.entityId $entityId 'Entity must be preserved.'
            Assert-Equal $sample.metric $metric.Metric 'Metric must be preserved.'
            Assert-Equal $sample.collectionRuleId $ruleId 'Rule must be preserved.'
            Assert-True ($sample.optimized -is [bool]) 'Optimized must remain a boolean.'
            Assert-Equal $sample.optimized $metric.Optimized 'Existing optimized mapping must remain unchanged.'
        }
        Assert-Equal $series.Calls 1 'Only one query per series.'
        Assert-Equal $series.Start ($started.AddDays(-14)) 'Query start must be unchanged.'
        Assert-Equal $series.End $started 'Query endpoint must be unchanged.'
        Assert-Equal (Get-ObservationStatus) 'partial' 'Null rows must make the snapshot partial.'
        Assert-Equal $limitations.Count 1 'Missing rows must be aggregated, not emitted individually.'
        Assert-True ($limitations[0].Contains('Excluded 2 null SCOM observations')) 'Missing count must be exact.'
        Assert-True ($limitations[0].Contains("entityId=$entityId; metric=$($metric.Metric); collectionRuleId=$ruleId")) 'Limitation must identify the entity, metric and rule.'
        Assert-True ($limitations[0].Contains("First missing UTC=$($started.AddMinutes(-40).ToString('o')); last missing UTC=$($started.AddMinutes(-30).ToString('o'))")) 'Missing timestamp bounds must use source min/max, not enumeration order.'
        Assert-True ($limitations[0].Length -le 2000) 'Limitation must fit the existing schema bound.'
        Assert-True ($limitations[0].Contains('cause of null values is unknown')) 'Missing rows must not be described as startup seeds.'
    }

    Invoke-Test "$($metric.Metric): null only" {
        $series = New-FakeSeries $metric.Object $metric.Counter @(
            (New-FakeSample $null), (New-FakeSample $null)
        )
        Add-ObservedSamples (New-FakeObject @($series)) $entityId $metric.Volume
        Assert-Equal $sampleIndex.Count 0 'Null-only input must never fabricate zero or any sample.'
        Assert-Equal (Get-ObservationStatus) 'partial' 'Null-only input must be partial.'
        Assert-Equal $limitations.Count 2 'Null-only input must disclose both rule coverage and an empty snapshot.'
        Assert-True ($limitations[0].Contains('Excluded 2 null SCOM observations')) 'Count raw missing rows even at the same timestamp.'
        Assert-True ($limitations[0].Contains('No usable observations for this entity/metric/rule')) 'Empty rule must be disclosed.'
        Assert-True ($limitations[1].Contains('No usable SCOM metric observations')) 'Empty metric set must be disclosed.'
    }

    Invoke-Test "$($metric.Metric): empty series" {
        $series = New-FakeSeries $metric.Object $metric.Counter @()
        Add-ObservedSamples (New-FakeObject @($series)) $entityId $metric.Volume
        Assert-Equal $sampleIndex.Count 0 'Empty series must not synthesize samples.'
        Assert-Equal (Get-ObservationStatus) 'partial' 'Empty series must be partial.'
        Assert-Equal $limitations.Count 2 'Empty series and snapshot must be disclosed.'
        Assert-True ($limitations[0].Contains('Excluded 0 null SCOM observations')) 'No null rows may be invented.'
        Assert-True ($limitations[0].Contains('No usable observations for this entity/metric/rule')) 'Empty rule must be identified.'
    }

    Invoke-Test "$($metric.Metric): valid range boundaries" {
        $series = New-FakeSeries $metric.Object $metric.Counter @(
            (New-FakeSample 0 $started.AddMinutes(-2)),
            (New-FakeSample 100 $started.AddMinutes(-1))
        )
        Add-ObservedSamples (New-FakeObject @($series)) $entityId $metric.Volume
        Assert-Equal $sampleIndex.Count 2 'Both range boundaries must be retained.'
        Assert-Equal (Get-ObservationStatus) 'ok' 'Valid-only input must not be marked partial by this fix.'
        Assert-Equal $limitations.Count 0 'Valid-only input must not have missing-data limitations.'
    }

    $invalidValues = @('malformed', '', ' ', '12 %', '2048 KB', 'NaN', 'Infinity',
        [double]::NaN, [double]::PositiveInfinity, [double]::NegativeInfinity, -1)
    if ($metric.Metric -ne 'memoryAvailableMiB') { $invalidValues += 100.01 }
    foreach ($invalid in $invalidValues) {
        Invoke-Test "$($metric.Metric): reject [$invalid] even after a null" {
            $series = New-FakeSeries $metric.Object $metric.Counter @(
                (New-FakeSample $null), (New-FakeSample $invalid)
            )
            Assert-Throws { Add-ObservedSamples (New-FakeObject @($series)) $entityId $metric.Volume } '*Invalid*SCOM*'
            Assert-Equal $sampleIndex.Count 0 'Invalid numeric data must not be published as a sample.'
        }
    }
}

Invoke-Test 'Available MiB above 100 remains allowed' {
    $series = New-FakeSeries 'Memory' 'Available MBytes' @((New-FakeSample 8192))
    Add-ObservedSamples (New-FakeObject @($series)) $entityId $false
    Assert-Equal @($sampleIndex.Values)[0].value 8192 'MiB must not be treated as a percentage.'
    Assert-Equal (Get-ObservationStatus) 'ok' 'Valid MiB must remain available.'
}

Invoke-Test 'No series' {
    Add-ObservedSamples (New-FakeObject @()) $entityId $false
    Assert-Equal $sampleIndex.Count 0 'Absent metrics must not fabricate samples.'
    Assert-Equal (Get-ObservationStatus) 'partial' 'An empty metric set must be partial.'
    Assert-Equal $limitations.Count 1 'An empty metric set must be disclosed.'
}

Invoke-Test 'Unrelated counters, instances and entity types are not queried' {
    $badSamples = @((New-FakeSample $null), (New-FakeSample 'malformed'))
    $series = @(
        (New-FakeSeries 'Processor' '% Processor Time' $badSamples),
        (New-FakeSeries 'Processor Information' '% Processor Time' $badSamples -InstanceName '0'),
        (New-FakeSeries 'Memory' 'Available Bytes' $badSamples),
        (New-FakeSeries 'LogicalDisk' '% Free Space' $badSamples)
    )
    Add-ObservedSamples (New-FakeObject $series) $entityId $false
    $cpuOnVolume = New-FakeSeries 'Processor Information' '% Processor Time' $badSamples
    Add-ObservedSamples (New-FakeObject @($cpuOnVolume)) $entityId $true
    foreach ($item in @($series) + @($cpuOnVolume)) { Assert-Equal $item.Calls 0 'Unsupported streams must not be queried or reinterpreted.' }
    Assert-Equal $sampleIndex.Count 0 'Unrecognized units must not be inferred.'
    Assert-Equal $observationCoverage.Count 0 'Ignored streams must not be counted as recognized missing observations.'
    Assert-Equal (Get-ObservationStatus) 'partial' 'No supported observations must be disclosed as partial.'
}

Invoke-Test 'Missing counts aggregate by entity, metric and rule across calls' {
    $first = New-FakeSeries 'Processor Information' '% Processor Time' @((New-FakeSample $null), (New-FakeSample 4))
    $second = New-FakeSeries 'Processor Information' '% Processor Time' @((New-FakeSample $null), (New-FakeSample $null))
    $third = New-FakeSeries 'Processor Information' '% Processor Time' @((New-FakeSample $null)) -Rule $otherRuleId
    $memory = New-FakeSeries 'Memory' 'PercentMemoryUsed' @((New-FakeSample $null))
    Add-ObservedSamples (New-FakeObject @($first, $third, $memory)) $entityId $false
    Add-ObservedSamples (New-FakeObject @($second)) $entityId $false
    Add-ObservedSamples (New-FakeObject @($first)) $otherEntityId $false
    Assert-Equal $observationCoverage.Count 4 'Entity, metric and rule must each separate provenance.'
    Assert-Equal $observationCoverage["$entityId|cpuPercent|$ruleId"].missingCount 3 'Same-key missing rows must aggregate across streams and calls.'
    Assert-Equal $observationCoverage["$entityId|cpuPercent|$otherRuleId"].missingCount 1 'Other-rule missing rows must stay separate.'
    Assert-Equal $observationCoverage["$entityId|memoryUsedPercent|$ruleId"].missingCount 1 'Other-metric missing rows must stay separate.'
    Assert-Equal $observationCoverage["$otherEntityId|cpuPercent|$ruleId"].missingCount 1 'Other-entity missing rows must stay separate.'
    Assert-Equal $sampleIndex.Count 2 'Real samples across entities must survive.'
    Assert-Equal (Get-ObservationStatus) 'partial' 'Aggregated missing observations must mark partial.'
    Assert-Equal $limitations.Count 4 'Exactly one limitation per affected provenance key.'
}

Invoke-Test 'An empty rule remains disclosed when another rule has valid samples' {
    $empty = New-FakeSeries 'Memory' 'PercentMemoryUsed' @()
    $valid = New-FakeSeries 'Memory' 'Available MBytes' @((New-FakeSample 4096))
    Add-ObservedSamples (New-FakeObject @($empty, $valid)) $entityId $false
    Assert-Equal $sampleIndex.Count 1 'Valid fallback must survive an empty native series.'
    Assert-Equal (Get-ObservationStatus) 'partial' 'Empty native series must not be hidden by valid fallback.'
    Assert-Equal $limitations.Count 1 'Only the empty rule must be disclosed.'
    Assert-True ($limitations[0].Contains('metric=memoryUsedPercent')) 'Limitation must identify the empty native metric.'
}

foreach ($otherValue in @(4, 7)) {
    Invoke-Test "Different rules at the same timestamp retain provenance ($otherValue)" {
        $first = New-FakeSeries 'Processor Information' '% Processor Time' @((New-FakeSample 4))
        $second = New-FakeSeries 'Processor Information' '% Processor Time' @((New-FakeSample $otherValue)) -Rule $otherRuleId
        Add-ObservedSamples (New-FakeObject @($first, $second)) $entityId $false
        Assert-Equal $sampleIndex.Count 2 'Different rules must never be deduplicated or conflict merely by timestamp.'
        Assert-Equal @($sampleIndex.Values | Where-Object collectionRuleId -eq $ruleId).Count 1 'First rule must survive.'
        Assert-Equal @($sampleIndex.Values | Where-Object collectionRuleId -eq $otherRuleId).Count 1 'Second rule must survive.'
    }
}

Invoke-Test 'Same rule/time duplicate values deduplicate' {
    $series = New-FakeSeries 'Processor Information' '% Processor Time' @((New-FakeSample 4), (New-FakeSample 4))
    Add-ObservedSamples (New-FakeObject @($series)) $entityId $false
    Assert-Equal $sampleIndex.Count 1 'Exact same-rule duplicates must not be counted twice.'
    Assert-Equal (Get-ObservationStatus) 'ok' 'Duplicate valid observations are not missing.'
}

Invoke-Test 'Same rule/time conflicting values fail closed' {
    $series = New-FakeSeries 'Processor Information' '% Processor Time' @((New-FakeSample 4), (New-FakeSample 7))
    Assert-Throws { Add-ObservedSamples (New-FakeObject @($series)) $entityId $false } '*Conflicting SCOM samples*'
}

Invoke-Test 'Same rule/time conflicting optimized metadata fails closed' {
    $series = New-FakeSeries 'Processor Information' '% Processor Time' @((New-FakeSample 4))
    Add-ObservedSamples (New-FakeObject @($series)) $entityId $false
    @($sampleIndex.Values)[0].optimized = $false
    Assert-Throws { Add-ObservedSamples (New-FakeObject @($series)) $entityId $false } '*Conflicting SCOM samples*'
}

Invoke-Test 'Existing unspecified timestamp normalization stays UTC' {
    $at = [DateTime]::SpecifyKind($started.AddHours(-1), [DateTimeKind]::Unspecified)
    $series = New-FakeSeries 'Memory' 'PercentMemoryUsed' @((New-FakeSample 50 $at))
    Add-ObservedSamples (New-FakeObject @($series)) $entityId $false
    Assert-Equal @($sampleIndex.Values)[0].observedAtUtc ($started.AddHours(-1).ToString('o')) 'Unspecified SDK timestamps must retain existing UTC interpretation.'
}

Invoke-Test 'Ten thousand null rows produce one bounded provenance limitation' {
    $series = New-FakeSeries 'Processor Information' '% Processor Time' (@((New-FakeSample $null)) * 10000)
    Add-ObservedSamples (New-FakeObject @($series)) $entityId $false
    Assert-Equal (Get-ObservationStatus) 'partial' 'Large null-only series must remain partial.'
    Assert-Equal $sampleIndex.Count 0 'No missing row may become a synthetic sample.'
    Assert-Equal $limitations.Count 2 'Ten thousand rows must yield only one rule limitation and one empty-snapshot limitation.'
    Assert-True ($limitations[0].Contains('Excluded 10000 null SCOM observations')) 'All missing rows must be counted.'
    Assert-True ($limitations[0].Length -le 2000) 'Large missing count must still have bounded text.'
}

Invoke-Test 'Existing per-series sample bound still fails closed' {
    $series = New-FakeSeries 'Processor Information' '% Processor Time' (@((New-FakeSample $null)) * 10001)
    Assert-Throws { Add-ObservedSamples (New-FakeObject @($series)) $entityId $false } '*approved sample bound*'
}

foreach ($badRule in @('', ('x' * 501))) {
    Invoke-Test 'Missing or oversized provenance identity fails closed' {
        $series = New-FakeSeries 'Memory' 'PercentMemoryUsed' @((New-FakeSample $null)) -Rule $badRule
        Assert-Throws { Add-ObservedSamples (New-FakeObject @($series)) $entityId $false } '*Invalid SCOM collection rule identity*'
    }
}

function Invoke-WithAtomicFixture {
    param([scriptblock]$Body)
    $fixtureRoot = Join-Path (Split-Path -Parent $PSScriptRoot) '.validation-data'
    $createdFixtureRoot = -not (Test-Path -LiteralPath $fixtureRoot)
    $root = Join-Path $fixtureRoot ('collector-atomic-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $root) { throw 'Atomic-write fixture directory already exists.' }
    [IO.Directory]::CreateDirectory($root) | Out-Null
    try {
        & $Body $root
    } finally {
        [IO.Directory]::Delete($root, $true)
        if ($createdFixtureRoot) {
            try { [IO.Directory]::Delete($fixtureRoot, $false) }
            catch [IO.IOException] { }
        }
    }
    Assert-True (-not (Test-Path -LiteralPath $root)) 'Atomic-write fixtures must be removed after validation.'
}

function Get-AccessSddl {
    param([string]$Path)
    (Get-Acl -LiteralPath $Path).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
}

function New-TestCollectorConfiguration {
    param([string]$Root)
    return @{
        CollectorHost = 'SCOM-TEST'
        ManagementServer = 'scom.example.invalid'
        ManagementGroup = 'SyntheticManagementGroup'
        OperationsManagerModule = 'OperationsManager'
        LocalDataDirectory = (Join-Path $Root 'local')
        PublishDirectory = (Join-Path $Root 'publisher')
        Computers = @(
            @{ Hostname = 'server-a.example.invalid'; EntityId = $entityId },
            @{ Hostname = 'server-b.example.invalid'; EntityId = $otherEntityId }
        )
    }
}

Invoke-Test 'Collector accepts explicit synthetic configuration' {
    Invoke-WithAtomicFixture {
        param($root)
        $path = Join-Path $root 'collector.json'
        $settings = New-TestCollectorConfiguration $root
        [IO.File]::WriteAllText($path, ($settings | ConvertTo-Json -Depth 5))
        $config = Read-CollectorConfiguration $path
        Assert-Equal $config.CollectorHost 'SCOM-TEST' 'The approved collector host must be retained.'
        Assert-Equal $config.ManagementServer 'scom.example.invalid' 'The management server must be retained.'
        Assert-Equal $config.ExpectedComputers.Count 2 'Exactly two approved identities must be retained.'
        Assert-Equal $config.ExpectedComputers['server-a.example.invalid'] $entityId 'The configured SCOM ID must be retained.'
        $settings.LocalDataDirectory = [IO.Path]::GetPathRoot($root)
        [IO.File]::WriteAllText($path, ($settings | ConvertTo-Json -Depth 5))
        $config = Read-CollectorConfiguration $path
        Assert-Equal $config.LocalDataDirectory ([IO.Path]::GetPathRoot($root)) 'An absolute drive root must remain absolute.'
    }
}

Invoke-Test 'Collector refuses unsafe or ambiguous configuration' {
    Invoke-WithAtomicFixture {
        param($root)
        $path = Join-Path $root 'collector.json'
        $settings = New-TestCollectorConfiguration $root
        $settings.Computers[1].Hostname = $settings.Computers[0].Hostname
        [IO.File]::WriteAllText($path, ($settings | ConvertTo-Json -Depth 5))
        Assert-Throws { Read-CollectorConfiguration $path } '*must be distinct*'
        $settings = New-TestCollectorConfiguration $root
        $settings.Computers[1].EntityId = $settings.Computers[0].EntityId
        [IO.File]::WriteAllText($path, ($settings | ConvertTo-Json -Depth 5))
        Assert-Throws { Read-CollectorConfiguration $path } '*must be distinct*'
        $settings = New-TestCollectorConfiguration $root
        $settings.Computers[0].EntityId = [Guid]::Empty.ToString()
        [IO.File]::WriteAllText($path, ($settings | ConvertTo-Json -Depth 5))
        Assert-Throws { Read-CollectorConfiguration $path } '*nonzero SCOM object ID*'
        $settings = New-TestCollectorConfiguration $root
        $settings.PublishDirectory = $settings.LocalDataDirectory
        [IO.File]::WriteAllText($path, ($settings | ConvertTo-Json -Depth 5))
        Assert-Throws { Read-CollectorConfiguration $path } '*must be distinct*'
        $settings = New-TestCollectorConfiguration $root
        $settings.LocalDataDirectory = 'relative'
        [IO.File]::WriteAllText($path, ($settings | ConvertTo-Json -Depth 5))
        Assert-Throws { Read-CollectorConfiguration $path } '*absolute Windows paths*'
        $settings = New-TestCollectorConfiguration $root
        $settings.ManagementServer = '*'
        [IO.File]::WriteAllText($path, ($settings | ConvertTo-Json -Depth 5))
        Assert-Throws { Read-CollectorConfiguration $path } '*management server is invalid*'
    }
}

Invoke-Test 'Same-directory atomic JSON creation and replacement preserve DACL' {
    Invoke-WithAtomicFixture {
        param($root)
        $path = Join-Path $root 'synthetic-atomic.json'
        $firstDacl = $null
        Assert-True (-not (Test-Path -LiteralPath $path)) 'Creation must start without a destination file.'
        foreach ($generation in @(1, 2)) {
            Write-AtomicJson $path ([pscustomobject]@{
                generation = $generation; marker = 'SYNTHETIC_ATOMIC_WRITE_TEST_ONLY'
            })
            Assert-True ([IO.File]::Exists($path)) 'Atomic write must leave the destination present.'
            $actual = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($path))
            Assert-Equal $actual.generation $generation 'Creation/replacement must preserve the newly written JSON.'
            Assert-Equal $actual.marker 'SYNTHETIC_ATOMIC_WRITE_TEST_ONLY' 'Synthetic fixture content must survive.'
            Assert-Equal @(Get-ChildItem -LiteralPath $root -Force).Count 1 'Only the destination may remain; no temporary or backup artifacts.'
            Assert-Equal @(Get-ChildItem -LiteralPath $root -Filter '*.tmp' -Force).Count 0 'Atomic writes must consume their temporary file.'
            $bytes = [IO.File]::ReadAllBytes($path)
            Assert-True (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) 'Published JSON must remain UTF-8 without BOM.'
            if ($generation -eq 1) { $firstDacl = Get-AccessSddl $path }
            else { Assert-Equal (Get-AccessSddl $path) $firstDacl 'Replacement must retain the exact destination access DACL.' }
        }
    }
}

Invoke-Test 'Failed native rename preserves destination content and DACL without leftovers' {
    Invoke-WithAtomicFixture {
        param($root)
        $path = Join-Path $root 'synthetic-locked.json'
        Write-AtomicJson $path @{ marker = 'SYNTHETIC_OLD_VALUE' }
        $before = [IO.File]::ReadAllText($path)
        $dacl = Get-AccessSddl $path
        $lock = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $failure = $null
            try { Write-AtomicJson $path @{ marker = 'SYNTHETIC_NEW_VALUE' } }
            catch { $failure = $_.Exception }
            Assert-True ($null -ne $failure) 'A destination without delete sharing must reject the rename.'
            while ($null -ne $failure -and $failure -isnot [ComponentModel.Win32Exception]) { $failure = $failure.InnerException }
            Assert-True ($failure -is [ComponentModel.Win32Exception]) 'Native error must retain its Win32Exception type.'
            Assert-True (@(5, 32) -contains $failure.NativeErrorCode) 'Native access/sharing error code must be exact, not swallowed.'
        } finally { $lock.Dispose() }
        Assert-Equal ([IO.File]::ReadAllText($path)) $before 'Failed rename must not delete or truncate the old destination.'
        Assert-Equal (Get-AccessSddl $path) $dacl 'Failed rename must not change destination permissions.'
        Assert-Equal @(Get-ChildItem -LiteralPath $root -Force).Count 1 'A failed rename must remove its staged file.'
    }
}

Invoke-Test 'DACL mismatch refuses replacement without altering destination permissions' {
    Invoke-WithAtomicFixture {
        param($root)
        $path = Join-Path $root 'synthetic-acl.json'
        Write-AtomicJson $path @{ marker = 'SYNTHETIC_OLD_VALUE' }
        $acl = Get-Acl -LiteralPath $path
        $acl.SetAccessRuleProtection($true, $true)
        Set-Acl -LiteralPath $path -AclObject $acl
        $before = [IO.File]::ReadAllText($path)
        $dacl = Get-AccessSddl $path
        Assert-Throws { Write-AtomicJson $path @{ marker = 'SYNTHETIC_NEW_VALUE' } } '*access DACLs differ*'
        Assert-Equal ([IO.File]::ReadAllText($path)) $before 'A DACL mismatch must retain old content.'
        Assert-Equal (Get-AccessSddl $path) $dacl 'The collector must never rewrite or widen a mismatched DACL.'
        Assert-Equal @(Get-ChildItem -LiteralPath $root -Force).Count 1 'DACL refusal must clean up the staged file.'
    }
}

Invoke-Test 'Native helper refuses cross-directory movement without copying' {
    Invoke-WithAtomicFixture {
        param($root)
        $source = Join-Path $root 'synthetic-source.json'
        Write-AtomicJson $source @{ marker = 'SYNTHETIC_SOURCE' }
        $other = Join-Path $root 'other'
        [IO.Directory]::CreateDirectory($other) | Out-Null
        $destination = Join-Path $other 'synthetic-target.json'
        Assert-Throws { [ScomMcpCollector.AtomicFile]::MoveSameDirectory($source, $destination, $false) } '*same directory*'
        Assert-True ([IO.File]::Exists($source)) 'Refused movement must retain the original file.'
        Assert-True (-not [IO.File]::Exists($destination)) 'There must be no cross-directory copy fallback.'
    }
}

Invoke-Test 'Successful publication commits matching dataset identifiers to both directories' {
    Invoke-WithAtomicFixture {
        param($root)
        $local = Join-Path $root 'local'
        $publisher = Join-Path $root 'publisher'
        [IO.Directory]::CreateDirectory($local) | Out-Null
        [IO.Directory]::CreateDirectory($publisher) | Out-Null
        foreach ($generation in @(1, 2)) {
            $dataset = "SYNTHETIC_DATASET_$generation"
            $snapshot = @{ datasetId = $dataset; marker = 'SYNTHETIC_PUBLICATION_TEST_ONLY' }
            $status = @{ status = 'ok'; attemptedAtUtc = $started.ToString('o'); datasetId = $dataset; errorCode = $null }
            Publish-SCOMSnapshot $snapshot $status $local $publisher
            foreach ($directory in @($local, $publisher)) {
                $actual = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $directory 'snapshot.json')))
                $actualStatus = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $directory 'collector-status.json')))
                Assert-Equal $actual.datasetId $dataset 'Snapshot must contain the committed generation.'
                Assert-Equal $actualStatus.datasetId $dataset 'Success status must match the written snapshot.'
                Assert-Equal $actualStatus.status 'ok' 'Only completed publication may have an ok status.'
                Assert-Equal @(Get-ChildItem -LiteralPath $directory -Force).Count 2 'No temporary or backup files may remain.'
            }
        }
    }
}

Invoke-Test 'Publisher snapshot failure leaves both statuses unavailable and old publisher bytes intact' {
    Invoke-WithAtomicFixture {
        param($root)
        $local = Join-Path $root 'local'
        $publisher = Join-Path $root 'publisher'
        [IO.Directory]::CreateDirectory($local) | Out-Null
        [IO.Directory]::CreateDirectory($publisher) | Out-Null
        $status = @{ status = 'ok'; attemptedAtUtc = $started.ToString('o'); datasetId = 'SYNTHETIC_OLD'; errorCode = $null }
        Publish-SCOMSnapshot @{ datasetId = 'SYNTHETIC_OLD' } $status $local $publisher
        $path = Join-Path $publisher 'snapshot.json'
        $before = [IO.File]::ReadAllText($path)
        $status.datasetId = 'SYNTHETIC_NEW'
        $lock = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            Assert-Throws { Publish-SCOMSnapshot @{ datasetId = 'SYNTHETIC_NEW' } $status $local $publisher } '*Win32=*'
        } finally { $lock.Dispose() }
        Assert-Equal ([IO.File]::ReadAllText($path)) $before 'Failed publisher rename must retain the original snapshot.'
        foreach ($directory in @($local, $publisher)) {
            $actual = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $directory 'collector-status.json')))
            Assert-Equal $actual.status 'unavailable' 'A failed attempt must not leave a success-shaped status.'
            Assert-Equal $actual.errorCode 'SCOM_COLLECTION_FAILED' 'Failure must override any retained old success.'
            Assert-True ($null -eq $actual.datasetId) 'Failure must not advertise a committed dataset.'
            Assert-Equal @(Get-ChildItem -LiteralPath $directory -Force).Count 2 'Failure cleanup must leave only snapshot and status.'
        }
    }
}

Invoke-Test 'Failure of the final publisher success marker rolls back local success too' {
    Invoke-WithAtomicFixture {
        param($root)
        $local = Join-Path $root 'local'
        $publisher = Join-Path $root 'publisher'
        [IO.Directory]::CreateDirectory($local) | Out-Null
        [IO.Directory]::CreateDirectory($publisher) | Out-Null
        $publisherStatus = Join-Path $publisher 'collector-status.json'
        $actualAtomicWriter = (Get-Command Write-AtomicJson).ScriptBlock
        function Write-AtomicJson {
            param([string]$Path, $Value)
            if ($Path -eq $publisherStatus -and $Value.status -eq 'ok') { throw 'SYNTHETIC_FINAL_STATUS_COMMIT_FAILURE' }
            & $actualAtomicWriter $Path $Value
        }
        $status = @{ status = 'ok'; attemptedAtUtc = $started.ToString('o'); datasetId = 'SYNTHETIC_NEW'; errorCode = $null }
        Assert-Throws { Publish-SCOMSnapshot @{ datasetId = 'SYNTHETIC_NEW' } $status $local $publisher } '*SYNTHETIC_FINAL_STATUS_COMMIT_FAILURE*'
        foreach ($directory in @($local, $publisher)) {
            $actual = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $directory 'collector-status.json')))
            Assert-Equal $actual.status 'unavailable' 'A final-status failure must invalidate both directories.'
            Assert-Equal $actual.errorCode 'SCOM_COLLECTION_FAILED' 'Rollback must publish a failure marker.'
            Assert-Equal @(Get-ChildItem -LiteralPath $directory -Force).Count 2 'Rollback must not leave staged artifacts.'
        }
    }
}

Invoke-Test 'Unwritable status fails explicitly while other status invalidation is still attempted' {
    Invoke-WithAtomicFixture {
        param($root)
        $local = Join-Path $root 'local'
        $publisher = Join-Path $root 'publisher'
        [IO.Directory]::CreateDirectory($local) | Out-Null
        [IO.Directory]::CreateDirectory($publisher) | Out-Null
        $status = @{ status = 'ok'; attemptedAtUtc = $started.ToString('o'); datasetId = 'SYNTHETIC_OLD'; errorCode = $null }
        Publish-SCOMSnapshot @{ datasetId = 'SYNTHETIC_OLD' } $status $local $publisher
        $lock = [IO.File]::Open((Join-Path $local 'collector-status.json'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            Assert-Throws { Set-CollectorUnavailable @($local, $publisher) ($started.ToString('o')) 'SCOM_COLLECTION_FAILED' } '*Could not invalidate every collector status*'
        } finally { $lock.Dispose() }
        $actual = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $publisher 'collector-status.json')))
        Assert-Equal $actual.status 'unavailable' 'One locked directory must not prevent invalidation of the other.'
        Assert-Equal @(Get-ChildItem -LiteralPath $root -Recurse -Filter '*.tmp' -Force).Count 0 'Invalidation failure must not leave temporary files.'
    }
}

Write-Output "Collector validation passed: $script:testCount tests, $script:assertionCount assertions. PowerShell $($PSVersionTable.PSVersion); function ASTs only; no SDK, top-level collector execution, real publication or scheduling."
