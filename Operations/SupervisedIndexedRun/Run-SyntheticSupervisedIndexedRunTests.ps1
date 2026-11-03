[CmdletBinding()]
param(
    [string]$OutputRoot = '',
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $env:TEMP ("scom-supervised-run-tests-" + [guid]::NewGuid().ToString('N'))
}
$null = New-Item -ItemType Directory -Path $OutputRoot -Force
$runRoot = Join-Path $OutputRoot 'runs'
$null = New-Item -ItemType Directory -Path $runRoot -Force
$resultsPath = Join-Path $OutputRoot 'synthetic-test-results.json'

$base = $PSScriptRoot
$startScript = Join-Path $base 'Start-SupervisedIndexedWorker.ps1'
$stopScript = Join-Path $base 'Stop-SupervisedIndexedWorker.ps1'

if (-not (Test-Path $startScript)) { throw 'Start script not found.' }
if (-not (Test-Path $stopScript)) { throw 'Stop script not found.' }

function Invoke-StartCase {
    param(
        [string]$Name,
        [string]$WorkerCommand,
        [hashtable]$Overrides
    )
    $dummyDll = Join-Path $OutputRoot ("dummy-{0}.dll" -f $Name)
    $dummyCfg = Join-Path $OutputRoot ("dummy-{0}.json" -f $Name)
    Set-Content -LiteralPath $dummyDll -Value 'dummy' -Encoding ASCII
    Set-Content -LiteralPath $dummyCfg -Value '{}' -Encoding ASCII
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($WorkerCommand))

    $params = @{
        WorkerDllPath = $dummyDll
        WorkerConfigPath = $dummyCfg
        RunRoot = $runRoot
        WorkerExecutablePath = $(if ($PSVersionTable.PSEdition -eq 'Desktop') { 'powershell.exe' } else { 'pwsh' })
        WorkerArgumentList = @('-NoProfile', '-EncodedCommand', $encoded)
        HardStopSeconds = 4
        SampleIntervalSeconds = 1
        BreachConsecutiveSamples = 1
        WorkerPrivateMbLimit = 5000
        WorkerWorkingSetMbLimit = 5000
        ChildPrivateMbLimit = 5000
        ChildWorkingSetMbLimit = 5000
    }
    foreach ($k in $Overrides.Keys) { $params[$k] = $Overrides[$k] }
    $startOutput = & $startScript @params
    $runLine = $startOutput | Where-Object { $_ -like 'RUN_ID *' } | Select-Object -First 1
    if (-not $runLine) { throw "Start script did not return RUN_ID for case '$Name'." }
    $runId = $runLine.Substring(7)
    $statePath = Join-Path (Join-Path $runRoot $runId) 'run-state.json'
    Read-JsonResult $statePath
}

function Read-JsonResult {
    param([string]$Path)
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

$tests = New-Object 'System.Collections.ArrayList'

# Independent native-handle probe: no launcher, monitor, or classification helper.
. (Join-Path $base 'SupervisedIndexedRun.Common.ps1')
$classificationCases = @(
    @('dotnet.exe', 'dotnet.exe ScomMcp.dll --worker config.json', $true),
    @('dotnet.exe', 'dotnet.exe ScomMcp.dll --worker-once config.json', $true),
    @('dotnet.exe', 'dotnet.exe other.dll --worker-label test', $false),
    @('pwsh.exe', 'pwsh.exe -File "C:\tests\Invoke-SCOMGenericGroupProfile.ps1" -Config a.json', $true),
    @('powershell.exe', 'powershell.exe -Command "& dotnet ScomMcp.dll --worker-once config.json"', $true),
    @('pwsh.exe', 'pwsh.exe -Command "Write-Output ''--worker-once Invoke-SCOMGenericGroupProfile.ps1''"', $false),
    @('pwsh.exe', 'pwsh.exe -File C:\tests\Run-SyntheticSupervisedIndexedRunTests.ps1 -OutputRoot C:\tests\Invoke-SCOMGenericGroupProfile.ps1', $false),
    @('pwsh.exe', 'pwsh.exe -File C:\tests\Run-SyntheticSupervisedIndexedRunTests.ps1 --worker-once', $false)
)
foreach ($case in $classificationCases) {
    $actual = Test-CollectorProcessCommandLine -Name $case[0] -CommandLine $case[1]
    if ($actual -ne $case[2]) { throw "Competitor classification regression: $($case[1])" }
}
[void]$tests.Add([pscustomobject]@{name='competitor argument classification';passed=$true;detail='8 invocation/string/path cases'})

$harmless = $null; $conflict = $null
try {
    $harmless = [SupervisedProcess]::Start((Get-Process -Id $PID).Path,
        '-NoProfile -Command "Write-Output ''--worker-once Invoke-SCOMGenericGroupProfile.ps1''; Start-Sleep -Seconds 60"',
        (Join-Path $OutputRoot 'harmless.out'), (Join-Path $OutputRoot 'harmless.err'))
    $record = Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $harmless.Id)
    if ($record.CommandLine -notmatch '--worker(\s|$|-)' -or
        (Test-CollectorProcessCommandLine -Name $record.Name -CommandLine $record.CommandLine)) {
        throw 'Failed to reproduce and reject the old whole-command-line false positive.'
    }
    $harmlessResult = Invoke-StartCase -Name 'harmless-competitor-marker' -WorkerCommand "Write-Output 'SCOM_WORKER_PUBLISHED'; exit 0" -Overrides @{}
    if ($harmlessResult.result.outcome -ne 'WORKER_SUCCESS_CONFIRMED_PUBLICATION') { throw 'Harmless marker blocked launcher.' }
    [void]$tests.Add([pscustomobject]@{name='live harmless harness marker';passed=$true;detail='old regex matched; launcher permitted; marker process alive'})

    $syntheticProfile = Join-Path $OutputRoot 'Invoke-SCOMGenericGroupProfile.ps1'
    Set-Content -LiteralPath $syntheticProfile -Value 'Start-Sleep -Seconds 60' -Encoding ASCII
    $conflict = [SupervisedProcess]::Start((Get-Process -Id $PID).Path,
        ('-NoProfile -File "{0}"' -f $syntheticProfile),
        (Join-Path $OutputRoot 'conflict.out'), (Join-Path $OutputRoot 'conflict.err'))
    $found = @(Get-CompetingCollectorProcesses | Where-Object { $_.ProcessId -eq $conflict.Id })
    if ($found.Count -ne 1) { throw 'Actual profile invocation not identified as competing.' }
    $rejected = $false
    try { Invoke-StartCase -Name 'actual-competitor' -WorkerCommand 'exit 0' -Overrides @{} | Out-Null }
    catch {
        if ($_.Exception.Message -notlike '*Competing collector/worker process already exists:*') { throw }
        $rejected = $true
    }
    if (-not $rejected -or $conflict.HasExited) { throw 'Actual conflict protection failed.' }
    [void]$tests.Add([pscustomobject]@{name='live actual profile conflict';passed=$true;detail='launcher rejected; competitor not killed'})
}
finally {
    foreach ($owned in @($conflict, $harmless)) {
        if ($null -ne $owned) {
            try { if (-not $owned.Stop(5000)) { throw 'Synthetic competitor cleanup unverified.' } }
            finally { $owned.Dispose() }
        }
    }
}
foreach ($exitValue in @(0, 7, 23)) {
    $probe = [SupervisedProcess]::Start((Get-Process -Id $PID).Path,
        ('-NoProfile -Command "exit {0}"' -f $exitValue),
        (Join-Path $OutputRoot "probe-$exitValue.out"), (Join-Path $OutputRoot "probe-$exitValue.err"))
    try {
        if (-not $probe.WaitForExit(10000)) { throw 'Independent child probe timed out.' }
        $actual = $probe.ExitCode
        if ($actual -ne $exitValue) { throw "Independent exit preservation failed: expected $exitValue actual $actual" }
        [void]$tests.Add([pscustomobject]@{name="independent exit $exitValue"; passed=$true; detail=$actual})
    }
    finally { $probe.Dispose() }
}

# 1) successful execution with publication signal
$r1 = Invoke-StartCase -Name 'success' -WorkerCommand "Start-Sleep -Seconds 1; Write-Output 'SCOM_WORKER_PUBLISHED'; exit 0" -Overrides @{}
[void]$tests.Add([pscustomobject]@{ name='successful execution'; passed=($r1.result.outcome -eq 'WORKER_SUCCESS_CONFIRMED_PUBLICATION'); detail=$r1.result.outcome })

# 2) nonzero exit
$r2 = Invoke-StartCase -Name 'nonzero' -WorkerCommand "Start-Sleep -Seconds 1; exit 7" -Overrides @{}
[void]$tests.Add([pscustomobject]@{ name='nonzero exit'; passed=($r2.result.outcome -eq 'WORKER_FAILED_NONZERO_EXIT' -and [int]$r2.result.workerExitCode -eq 7); detail=($r2.result.outcome + ':' + $r2.result.workerExitCode) })

# 3) monitor failure before worker completion
$r3 = Invoke-StartCase -Name 'monitor-failure' -WorkerCommand "Start-Sleep -Seconds 30; exit 0" -Overrides @{ SimulateMonitorRuntimeFailure = $true }
[void]$tests.Add([pscustomobject]@{ name='monitor failure'; passed=($r3.result.outcome -eq 'UNKNOWN_MONITOR_FAILURE'); detail=$r3.result.outcome })

# 4) timeout stop
$r4 = Invoke-StartCase -Name 'timeout' -WorkerCommand "Start-Sleep -Seconds 30; exit 0" -Overrides @{ HardStopSeconds = 2 }
[void]$tests.Add([pscustomobject]@{ name='timeout'; passed=($r4.monitor.reason -eq 'RUN_TIMEOUT_LIMIT'); detail=$r4.monitor.reason })

# 5) memory breach
$r5 = Invoke-StartCase -Name 'memory-breach' -WorkerCommand '$buf = New-Object byte[] (200MB); Start-Sleep -Seconds 20; exit 0' -Overrides @{ WorkerPrivateMbLimit = 1; WorkerWorkingSetMbLimit = 1; HardStopSeconds = 30 }
[void]$tests.Add([pscustomobject]@{ name='memory breach'; passed=($r5.monitor.reason -like 'LIMIT_BREACH_*'); detail=$r5.monitor.reason })

# 6) child cleanup after stop trigger
$marker = "SYNTH_CHILD_{0}" -f ([guid]::NewGuid().ToString('N'))
$childCmd = "Start-Process powershell -ArgumentList '-NoProfile -Command `"Write-Output $marker; Start-Sleep -Seconds 60`"'; Start-Sleep -Seconds 30; exit 0"
$r6 = Invoke-StartCase -Name 'child-cleanup' -WorkerCommand $childCmd -Overrides @{ HardStopSeconds = 2 }
$orphan = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -match $marker })
[void]$tests.Add([pscustomobject]@{ name='child cleanup'; passed=($orphan.Count -eq 0); detail=("remaining={0}" -f $orphan.Count) })

# 7) stale PID rejection
$hostExe = (Get-Process -Id $PID).Path
$safeProc = [SupervisedProcess]::Start($hostExe,
    '-NoProfile -Command "Start-Sleep -Seconds 30"',
    (Join-Path $OutputRoot 'stale-pid.out'), (Join-Path $OutputRoot 'stale-pid.err'))
$fakeRunId = "fake-{0}" -f ([guid]::NewGuid().ToString('N'))
$fakeDir = Join-Path $runRoot $fakeRunId
$null = New-Item -ItemType Directory -Path $fakeDir -Force
$staleState = $null
try {
    $safeProcess = Get-Process -Id $safeProc.Id
    $fakeState = [ordered]@{
        runId = $fakeRunId
        status = 'running'
        result = $null
        worker = [ordered]@{
            identity = [ordered]@{
                pid = $safeProc.Id
                name = $safeProcess.ProcessName
                startTimeFileTimeUtc = '1'
            }
        }
    }
    $fakeState | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $fakeDir 'run-state.json') -Encoding UTF8
    & $stopScript -RunId $fakeRunId -RunRoot $runRoot -Reason 'STALE_TEST' | Out-Null
    $staleState = Read-JsonResult (Join-Path $fakeDir 'stop-result.json')
    [void]$tests.Add([pscustomobject]@{ name='stale PID'; passed=($staleState.status -eq 'stop_refused_stale_pid'); detail=$staleState.status })
}
finally {
    try { if (-not $safeProc.HasExited -and -not $safeProc.Stop(5000)) { throw 'Stale PID synthetic cleanup unverified.' } }
    finally { $safeProc.Dispose() }
}

# 8) stop failure reporting
$liveProc = [SupervisedProcess]::Start($hostExe,
    '-NoProfile -Command "Start-Sleep -Seconds 30"',
    (Join-Path $OutputRoot 'stop-failure.out'), (Join-Path $OutputRoot 'stop-failure.err'))
$liveRunId = "stop-fail-{0}" -f ([guid]::NewGuid().ToString('N'))
$liveDir = Join-Path $runRoot $liveRunId
$null = New-Item -ItemType Directory -Path $liveDir -Force
try {
    $liveProcess = Get-Process -Id $liveProc.Id
    $liveStartTime = $liveProcess.StartTime.ToUniversalTime()
    $liveState = [ordered]@{
        runId = $liveRunId
        status = 'running'
        result = $null
        worker = [ordered]@{
            identity = [ordered]@{
                pid = $liveProc.Id
                name = $liveProcess.ProcessName
                startTimeUtc = $liveStartTime.ToString('o')
                startTimeFileTimeUtc = [string]$liveStartTime.ToFileTimeUtc()
            }
        }
    }
    $liveState | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $liveDir 'run-state.json') -Encoding UTF8
    & $stopScript -RunId $liveRunId -RunRoot $runRoot -Reason 'SIM_STOP_FAIL' -SimulateStopFailure | Out-Null
    $stopFail = Read-JsonResult (Join-Path $liveDir 'stop-result.json')
    [void]$tests.Add([pscustomobject]@{ name='stop failure'; passed=($stopFail.status -eq 'stop_failed'); detail=$stopFail.status })
}
finally {
    try { if (-not $liveProc.HasExited -and -not $liveProc.Stop(5000)) { throw 'Stop-failure synthetic cleanup unverified.' } }
    finally { $liveProc.Dispose() }
}

# Failures after launch must stop the atomic job, including a real synthetic child.
foreach ($stage in @('identity', 'arm', 'state', 'monitor')) {
    $before = @(Get-ChildItem -LiteralPath $runRoot -Directory | Select-Object -ExpandProperty Name)
    $caught = $false
    try {
        $null = Invoke-StartCase -Name "setup-$stage" -WorkerCommand $childCmd `
            -Overrides @{ SimulatePostLaunchFailure=$stage; HardStopSeconds=15; SimulatePostLaunchDelayMilliseconds=3000 }
    }
    catch { $caught=$true }
    $new = @(Get-ChildItem -LiteralPath $runRoot -Directory | Where-Object { $_.Name -notin $before })
    if ($new.Count -ne 1) { throw 'Failure case did not create exactly one run.' }
    $failure = Read-JsonResult (Join-Path $new[0].FullName 'launcher-failure.json')
    $stillRunning = Get-Process -Id ([int]$failure.workerPid) -ErrorAction SilentlyContinue
    $orphans = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($marker) })
    $passed = $caught -and $failure.terminatedVerified -and $failure.activeProcessesBeforeStop -ge 2 -and
        $null -eq $stillRunning -and $orphans.Count -eq 0
    [void]$tests.Add([pscustomobject]@{name="postlaunch $stage cleanup";passed=$passed;detail=$failure.reason})
}
$before = @(Get-ChildItem -LiteralPath $runRoot -Directory | Select-Object -ExpandProperty Name)
$failureMessage = ''
try {
    $null = Invoke-StartCase -Name 'cleanup-stop-failure' -WorkerCommand 'Start-Sleep -Seconds 30' `
        -Overrides @{SimulatePostLaunchFailure='state'; SimulateCleanupStopFailure=$true}
}
catch { $failureMessage=$_.Exception.Message }
$new = @(Get-ChildItem -LiteralPath $runRoot -Directory | Where-Object { $_.Name -notin $before })
$failure = Read-JsonResult (Join-Path $new[0].FullName 'launcher-failure.json')
Start-Sleep -Milliseconds 200
$stillRunning = Get-Process -Id ([int]$failure.workerPid) -ErrorAction SilentlyContinue
[void]$tests.Add([pscustomobject]@{name='bounded explicit cleanup stop failure';passed=(
    $failure.stopFailure -and -not $failure.terminatedVerified -and
    $failureMessage -like '*CLEANUP_UNVERIFIED*' -and $null -eq $stillRunning);detail=$failure.reason})

$summary = [ordered]@{
    engine = $PSVersionTable.PSEdition + ' ' + $PSVersionTable.PSVersion.ToString()
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    tests = $tests
    passed = (@($tests | Where-Object { $_.passed -eq $false }).Count -eq 0)
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultsPath -Encoding UTF8
Write-Output ("RESULTS_PATH {0}" -f $resultsPath)
Write-Output ("OVERALL_PASSED {0}" -f $summary.passed)
if (-not $summary.passed) { throw 'Synthetic acceptance assertions failed.' }

if (-not $KeepArtifacts) {
    Write-Output ("ARTIFACT_ROOT {0}" -f $OutputRoot)
}
