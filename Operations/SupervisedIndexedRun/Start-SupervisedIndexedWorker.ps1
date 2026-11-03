[CmdletBinding()]
param(
    [string]$WorkerDllPath = 'C:\inetpub\scommcp2\ScomMcp.dll',
    [string]$WorkerConfigPath = 'C:\ProgramData\ScomAiWorker\config\worker.indexed.private.json',
    [string]$RunRoot = 'C:\ProgramData\SCOMMCP-Private\indexed-supervised-runs',
    [string]$CorrelationId = '',
    [Parameter(Mandatory = $true)][ValidateRange(1, 86400)][int]$HardStopSeconds,
    [Parameter(Mandatory = $true)][ValidateRange(1, 300)][int]$SampleIntervalSeconds,
    [Parameter(Mandatory = $true)][ValidateRange(1, 120)][int]$BreachConsecutiveSamples,
    [Parameter(Mandatory = $true)][ValidateRange(1, 1048576)][int]$WorkerPrivateMbLimit,
    [Parameter(Mandatory = $true)][ValidateRange(1, 1048576)][int]$WorkerWorkingSetMbLimit,
    [Parameter(Mandatory = $true)][ValidateRange(1, 1048576)][int]$ChildPrivateMbLimit,
    [Parameter(Mandatory = $true)][ValidateRange(1, 1048576)][int]$ChildWorkingSetMbLimit,
    [string]$ChildScriptPattern = 'Invoke-SCOMGenericGroupProfile.ps1',
    [string]$WorkerExecutablePath = 'dotnet',
    [string[]]$WorkerArgumentList = @(),
    [string]$MonitorScriptPath = '',
    [switch]$SimulateMonitorStartupFailure,
    [switch]$SimulateMonitorRuntimeFailure,
    [ValidateSet('', 'identity', 'arm', 'state', 'monitor')][string]$SimulatePostLaunchFailure = '',
    [switch]$SimulateCleanupStopFailure,
    [ValidateRange(0,5000)][int]$SimulatePostLaunchDelayMilliseconds = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

. (Join-Path $PSScriptRoot 'SupervisedIndexedRun.Common.ps1')

if (-not (Test-AbsolutePath -Path $WorkerDllPath)) { throw 'WorkerDllPath must be absolute.' }
if (-not (Test-AbsolutePath -Path $WorkerConfigPath)) { throw 'WorkerConfigPath must be absolute.' }
if (-not (Test-AbsolutePath -Path $RunRoot)) { throw 'RunRoot must be absolute.' }
if (-not (Test-Path -LiteralPath $WorkerDllPath)) { throw 'Worker DLL was not found at the provided path.' }
if (-not (Test-Path -LiteralPath $WorkerConfigPath)) { throw 'Worker config was not found at the provided path.' }

$runId = New-RunId
$runDirectory = Join-Path $RunRoot $runId
$null = New-Item -ItemType Directory -Path $runDirectory -Force

$stdoutPath = Join-Path $runDirectory 'stdout.log'
$stderrPath = Join-Path $runDirectory 'stderr.log'
$eventsPath = Join-Path $runDirectory 'events.log'
$statePath = Join-Path $runDirectory 'run-state.json'
$armPath = Join-Path $runDirectory 'monitor-arm.json'
$monitorReadyPath = Join-Path $runDirectory 'monitor-ready.txt'
$monitorStdoutPath = Join-Path $runDirectory 'monitor-stdout.log'
$monitorStderrPath = Join-Path $runDirectory 'monitor-stderr.log'
$monitorResultPath = Join-Path $runDirectory 'monitor-result.json'

Remove-Item -LiteralPath $stdoutPath, $stderrPath, $eventsPath, $armPath, $monitorReadyPath, $monitorStdoutPath, $monitorStderrPath, $monitorResultPath -ErrorAction SilentlyContinue

$competing = @(Get-CompetingCollectorProcesses)
if ($competing.Count -gt 0) {
    $details = $competing | ForEach-Object { "pid=$($_.ProcessId);name=$($_.Name)" }
    throw ("Competing collector/worker process already exists: " + ($details -join ', '))
}

if ([string]::IsNullOrWhiteSpace($MonitorScriptPath)) {
    $MonitorScriptPath = Join-Path $PSScriptRoot 'Monitor-SupervisedIndexedWorker.ps1'
}
if (-not (Test-Path -LiteralPath $MonitorScriptPath)) { throw 'Monitor script was not found.' }

$state = [ordered]@{
    runId = $runId
    mode = 'one-shot'
    correlationId = $CorrelationId
    createdAtUtc = ([DateTimeOffset]::UtcNow.ToString('o'))
    startedAtUtc = ([DateTimeOffset]::UtcNow.ToString('o'))
    host = $env:COMPUTERNAME
    user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    worker = [ordered]@{
        executable = $WorkerExecutablePath
        dllPath = $WorkerDllPath
        configPath = $WorkerConfigPath
        arguments = @()
        identity = $null
        exitCode = $null
        childrenTerminatedVerified = $false
    }
    monitor = [ordered]@{
        scriptPath = $MonitorScriptPath
        identity = $null
        status = 'preparing'
        reason = ''
    }
    limits = [ordered]@{
        hardStopSeconds = $HardStopSeconds
        sampleIntervalSeconds = $SampleIntervalSeconds
        breachConsecutiveSamples = $BreachConsecutiveSamples
        workerPrivateMbLimit = $WorkerPrivateMbLimit
        workerWorkingSetMbLimit = $WorkerWorkingSetMbLimit
        childPrivateMbLimit = $ChildPrivateMbLimit
        childWorkingSetMbLimit = $ChildWorkingSetMbLimit
    }
    diagnostics = [ordered]@{
        stdout = $stdoutPath
        stderr = $stderrPath
        events = $eventsPath
        monitorStdout = $monitorStdoutPath
        monitorStderr = $monitorStderrPath
        monitorResult = $monitorResultPath
        arm = $armPath
    }
    status = 'preparing'
    result = $null
}

Write-JsonFile -Path $statePath -Object $state
Add-RunEvent -EventsPath $eventsPath -Message "RUN_PREPARED runId=$runId mode=one-shot"

& $MonitorScriptPath -PreflightOnly `
    -HardStopSeconds $HardStopSeconds `
    -SampleIntervalSeconds $SampleIntervalSeconds `
    -BreachConsecutiveSamples $BreachConsecutiveSamples `
    -WorkerPrivateMbLimit $WorkerPrivateMbLimit `
    -WorkerWorkingSetMbLimit $WorkerWorkingSetMbLimit `
    -ChildPrivateMbLimit $ChildPrivateMbLimit `
    -ChildWorkingSetMbLimit $ChildWorkingSetMbLimit | Out-Null

if ($SimulateMonitorStartupFailure) { throw 'Simulated monitor startup failure before worker launch.' }

$hostExe = (Get-Process -Id $PID).Path
$monitorArgs = @(
    '-NoProfile',
    '-File', $MonitorScriptPath,
    '-RunId', $runId,
    '-RunRoot', $RunRoot,
    '-HardStopSeconds', [string]$HardStopSeconds,
    '-SampleIntervalSeconds', [string]$SampleIntervalSeconds,
    '-BreachConsecutiveSamples', [string]$BreachConsecutiveSamples,
    '-WorkerPrivateMbLimit', [string]$WorkerPrivateMbLimit,
    '-WorkerWorkingSetMbLimit', [string]$WorkerWorkingSetMbLimit,
    '-ChildPrivateMbLimit', [string]$ChildPrivateMbLimit,
    '-ChildWorkingSetMbLimit', [string]$ChildWorkingSetMbLimit,
    '-ChildScriptPattern', $ChildScriptPattern,
    '-ArmFilePath', $armPath,
    '-ReadyFilePath', $monitorReadyPath
)
if ($SimulateMonitorRuntimeFailure) { $monitorArgs += '-SimulateRuntimeFailure' }
$monitorArgLine = Join-ProcessArgumentLine -ArgumentList $monitorArgs

$monitorProc = $null
$workerProc = $null
try {
$monitorProc = Start-Process -FilePath $hostExe -ArgumentList $monitorArgLine -PassThru -NoNewWindow `
    -RedirectStandardOutput $monitorStdoutPath -RedirectStandardError $monitorStderrPath

$monitorIdentity = $null
$deadline = [DateTime]::UtcNow.AddSeconds(20)
while ([DateTime]::UtcNow -lt $deadline) {
    $monitorIdentity = Get-ProcessIdentitySnapshot -ProcessId $monitorProc.Id
    if ($null -eq $monitorIdentity) { break }
    if (Test-Path -LiteralPath $monitorReadyPath) { break }
    Start-Sleep -Milliseconds 200
}
if (-not (Test-Path -LiteralPath $monitorReadyPath)) {
    throw 'Monitor did not reach ready state before launch gate.'
}
if ($null -eq $monitorIdentity) {
    throw 'Monitor process exited before worker launch.'
}

if ($WorkerArgumentList.Count -eq 0) {
    $WorkerArgumentList = @($WorkerDllPath, '--worker-once', $WorkerConfigPath)
}
$state.worker.arguments = $WorkerArgumentList

$resolvedExecutable = (Get-Command $WorkerExecutablePath -CommandType Application -ErrorAction Stop).Source
$workerProc = [SupervisedProcess]::Start($resolvedExecutable,
    (Join-ProcessArgumentLine -ArgumentList $WorkerArgumentList), $stdoutPath, $stderrPath)
$elapsed = [Diagnostics.Stopwatch]::StartNew()
if ($SimulatePostLaunchDelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $SimulatePostLaunchDelayMilliseconds }
if ($SimulatePostLaunchFailure -eq 'identity') { throw 'SIMULATED_IDENTITY_CAPTURE_FAILURE' }
$workerIdentity = Get-ProcessIdentitySnapshot -ProcessId $workerProc.Id
if ($null -eq $workerIdentity) {
    if (-not $workerProc.HasExited) { throw 'Worker process could not be identified after launch.' }
    # A fast child's authoritative exit code remains available on the creation handle.
    $workerIdentity = [pscustomobject]@{pid=$workerProc.Id;name='exited';startTimeFileTimeUtc='0'}
}

if ($SimulatePostLaunchFailure -eq 'arm') { throw 'SIMULATED_ARM_WRITE_FAILURE' }
Write-JsonFile -Path $armPath -Object ([ordered]@{
    workerIdentity = $workerIdentity
    startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
})

$state.worker.identity = $workerIdentity
$state.monitor.identity = $monitorIdentity
$state.status = 'running'
if ($SimulatePostLaunchFailure -eq 'state') { throw 'SIMULATED_STATE_WRITE_FAILURE' }
Write-JsonFile -Path $statePath -Object $state
Add-RunEvent -EventsPath $eventsPath -Message ("RUN_STARTED runId={0} workerPid={1} monitorPid={2}" -f $runId, $workerProc.Id, $monitorIdentity.pid)

$monitorFailure = $false
if ($SimulatePostLaunchFailure -eq 'monitor') { throw 'SIMULATED_MONITOR_SETUP_FAILURE' }
while (-not $workerProc.HasExited) {
    if (-not (Test-ProcessIdentity -Identity $monitorIdentity)) {
        $monitorFailure = $true
        Add-RunEvent -EventsPath $eventsPath -Message "MONITOR_FAILED_BEFORE_WORKER_EXIT"
        break
    }
    if ($elapsed.Elapsed.TotalSeconds -ge $HardStopSeconds + $SampleIntervalSeconds + 5) {
        $monitorFailure = $true
        break
    }
    Start-Sleep -Milliseconds 500
}

if ($monitorFailure) {
    if (-not $workerProc.Stop(5000)) { throw 'WORKER_JOB_STOP_TIMEOUT' }
    Add-RunEvent -EventsPath $eventsPath -Message 'WORKER_JOB_STOP_AFTER_MONITOR_FAILURE'
}

if (-not $workerProc.WaitForExit(5000)) { throw 'WORKER_EXIT_TIMEOUT' }
if (-not $workerProc.WaitEmpty(5000)) {
    if (-not $workerProc.Stop(5000)) { throw 'WORKER_CHILD_CLEANUP_TIMEOUT' }
}
$workerExitCode = [int]$workerProc.ExitCode
$state.worker.exitCode = $workerExitCode

if (-not $monitorProc.WaitForExit(5000)) {
    $monitorFailure = $true
    $monitorProc.Kill()
    if (-not $monitorProc.WaitForExit(5000)) { throw 'MONITOR_STOP_TIMEOUT' }
}

$monitorStatus = 'unknown'
$monitorReason = 'NO_MONITOR_RESULT'
if (Test-Path -LiteralPath $monitorResultPath) {
    $monitorResult = Read-JsonFile -Path $monitorResultPath
    $monitorStatus = [string]$monitorResult.status
    $monitorReason = [string]$monitorResult.reason
}
if ($monitorFailure) {
    $monitorStatus = 'monitor_failed'
    $monitorReason = 'MONITOR_EXITED_BEFORE_RESULT'
}

$classification = Get-OutcomeClassification -ExitCode $workerExitCode `
    -StdoutPath $stdoutPath -StderrPath $stderrPath `
    -MonitorStatus $monitorStatus -MonitorReason $monitorReason
$state.worker.childrenTerminatedVerified = $true

$state.status = 'finished'
$state.monitor.status = $monitorStatus
$state.monitor.reason = $monitorReason
$state.result = [ordered]@{
    finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    workerExitCode = $workerExitCode
    workerExitStatus = $classification.workerExitStatus
    publicationStatus = $classification.publicationStatus
    outcome = $classification.outcome
    reason = $classification.reason
}
Write-JsonFile -Path $statePath -Object $state
Add-RunEvent -EventsPath $eventsPath -Message ("RUN_FINISHED outcome={0} exitCode={1}" -f $classification.outcome, $workerExitCode)

Write-Output ("RUN_ID {0}" -f $runId)
Write-Output ("STATE_PATH {0}" -f $statePath)
Write-Output ("WORKER_PID {0}" -f $workerProc.Id)
Write-Output ("WORKER_EXIT_CODE {0}" -f $workerExitCode)
Write-Output ("OUTCOME {0}" -f $classification.outcome)
Write-Output ("PUBLICATION_STATUS {0}" -f $classification.publicationStatus)
}
catch {
    $originalFailure = $_
    $cleanup = [ordered]@{
        workerPid = $(if ($null -ne $workerProc) { $workerProc.Id } else { $null })
        terminatedVerified = $true
        stopFailure = $false
        reason = 'NO_WORKER_LAUNCHED'
        activeProcessesBeforeStop = $null
    }
    if ($null -ne $workerProc) {
        try {
            $cleanup.activeProcessesBeforeStop = $workerProc.ActiveProcesses
            if ($SimulateCleanupStopFailure) { throw 'SIMULATED_STOP_FAILURE' }
            if (-not $workerProc.Stop(5000)) { throw 'JOB_STOP_TIMEOUT' }
            $cleanup.reason = 'JOB_EMPTY_VERIFIED'
        }
        catch {
            $cleanup.terminatedVerified = $false
            $cleanup.stopFailure = $true
            $cleanup.reason = 'STOP_FAILED_OPERATOR_ATTENTION_REQUIRED'
        }
    }
    # Even simulated stop failure closes the kill-on-close job in finally.
    # Do not claim verified cleanup when explicit stop/verification failed.
    Write-JsonFile -Path (Join-Path $runDirectory 'launcher-failure.json') -Object $cleanup
    if ($cleanup.stopFailure) { throw 'LAUNCH_FAILED_CLEANUP_UNVERIFIED: operator attention required.' }
    throw $originalFailure
}
finally {
    if ($null -ne $workerProc) { $workerProc.Dispose() }
    if ($null -ne $monitorProc) {
        try {
            if (-not $monitorProc.HasExited) {
                $monitorProc.Kill()
                if (-not $monitorProc.WaitForExit(5000)) { throw 'MONITOR_CLEANUP_TIMEOUT' }
            }
        }
        finally { $monitorProc.Dispose() }
    }
}
