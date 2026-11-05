[CmdletBinding()]
param(
    [string]$RunId,
    [string]$RunRoot = 'C:\ProgramData\SCOMMCP-Private\indexed-supervised-runs',
    [string]$ArmFilePath = '',
    [string]$ReadyFilePath = '',
    [ValidateRange(1, 300)]
    [int]$ArmWaitSeconds = 60,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 86400)]
    [int]$HardStopSeconds,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 300)]
    [int]$SampleIntervalSeconds,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 120)]
    [int]$BreachConsecutiveSamples,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 1048576)]
    [int]$WorkerPrivateMbLimit,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 1048576)]
    [int]$WorkerWorkingSetMbLimit,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 1048576)]
    [int]$ChildPrivateMbLimit,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 1048576)]
    [int]$ChildWorkingSetMbLimit,
    [string]$ChildScriptPattern = 'Invoke-SCOMGenericGroupProfile.ps1',
    [switch]$PreflightOnly,
    [switch]$SimulateRuntimeFailure,
    [switch]$SimulateStopFailure
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

. (Join-Path $PSScriptRoot 'SupervisedIndexedRun.Common.ps1')

if ($PreflightOnly) {
    Write-Output 'PRECHECK_OK'
    exit 0
}

if ([string]::IsNullOrWhiteSpace($RunId)) { throw 'RunId is required unless PreflightOnly is used.' }

try {
    $runDirectory = Join-Path $RunRoot $RunId
    $statePath = Join-Path $runDirectory 'run-state.json'
    $eventsPath = Join-Path $runDirectory 'events.log'
    $samplesPath = Join-Path $runDirectory 'monitor-samples.csv'
    $resultPath = Join-Path $runDirectory 'monitor-result.json'
    if ([string]::IsNullOrWhiteSpace($ArmFilePath)) { $ArmFilePath = Join-Path $runDirectory 'monitor-arm.json' }
    if ([string]::IsNullOrWhiteSpace($ReadyFilePath)) { $ReadyFilePath = Join-Path $runDirectory 'monitor-ready.txt' }

    if (-not (Test-Path -LiteralPath $statePath)) { throw 'Run state file was not found.' }
    $state = Read-JsonFile -Path $statePath
    $startedAtUtc = [DateTimeOffset]::Parse($state.startedAtUtc)

    if (-not (Test-Path -LiteralPath $samplesPath)) {
        'sampleUtc,elapsedSeconds,workerPid,workerPrivateMb,workerWorkingSetMb,childPid,childPrivateMb,childWorkingSetMb' |
            Set-Content -LiteralPath $samplesPath -Encoding UTF8
    }

    Set-Content -LiteralPath $ReadyFilePath -Value ([DateTimeOffset]::UtcNow.ToString('o')) -Encoding UTF8
    Add-RunEvent -EventsPath $eventsPath -Message 'MONITOR_READY'

    $armDeadline = [DateTime]::UtcNow.AddSeconds($ArmWaitSeconds)
    while (-not (Test-Path -LiteralPath $ArmFilePath)) {
        if ([DateTime]::UtcNow -ge $armDeadline) {
            $result = [ordered]@{
                runId = $RunId
                completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
                status = 'monitor_failed'
                reason = 'ARM_TIMEOUT'
            }
            Write-JsonFile -Path $resultPath -Object $result
            Add-RunEvent -EventsPath $eventsPath -Message 'MONITOR_FAILED reason=ARM_TIMEOUT'
            exit 2
        }
        Start-Sleep -Milliseconds 200
    }

    $arm = Read-JsonFile -Path $ArmFilePath
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $workerIdentity = $arm.workerIdentity
    if ($null -eq $workerIdentity) { throw 'Monitor arm file is missing worker identity.' }

    $workerPid = [int]$workerIdentity.pid
    $resultStatus = 'completed'
    $resultReason = 'WORKER_EXITED'

$breachCount = @{
    workerPrivateMb = 0
    workerWorkingSetMb = 0
    childPrivateMb = 0
    childWorkingSetMb = 0
}

    while ($true) {
    if ($SimulateRuntimeFailure) { throw 'SIMULATED_MONITOR_RUNTIME_FAILURE' }

    $sampleUtc = [DateTimeOffset]::UtcNow
    $elapsed = [int][Math]::Floor($clock.Elapsed.TotalSeconds)

    if (-not (Test-ProcessIdentity -Identity $workerIdentity)) {
        $resultStatus = 'completed'
        $resultReason = 'WORKER_EXITED'
        break
    }

    $workerProc = Get-Process -Id $workerPid -ErrorAction SilentlyContinue
    if ($null -eq $workerProc) {
        $resultStatus = 'completed'
        $resultReason = 'WORKER_EXITED'
        break
    }
    $workerPrivateMb = [Math]::Round($workerProc.PrivateMemorySize64 / 1MB, 2)
    $workerWorkingSetMb = [Math]::Round($workerProc.WorkingSet64 / 1MB, 2)

    $child = Get-ChildCollectorIdentity -WorkerIdentity $workerIdentity -ChildScriptPattern $ChildScriptPattern
    $childPid = ''
    $childPrivateMb = ''
    $childWorkingSetMb = ''
    if ($null -ne $child) {
        $childPid = [string]$child.pid
        $childProc = Get-Process -Id ([int]$child.pid) -ErrorAction SilentlyContinue
        if ($null -ne $childProc) {
            $childPrivateMb = [string][Math]::Round($childProc.PrivateMemorySize64 / 1MB, 2)
            $childWorkingSetMb = [string][Math]::Round($childProc.WorkingSet64 / 1MB, 2)
        }
    }

    '{0},{1},{2},{3},{4},{5},{6},{7}' -f `
        $sampleUtc.ToString('o'), $elapsed, $workerPid, $workerPrivateMb, $workerWorkingSetMb, `
        $childPid, $childPrivateMb, $childWorkingSetMb | Add-Content -LiteralPath $samplesPath -Encoding UTF8

    $breachCount.workerPrivateMb = if ($workerPrivateMb -gt $WorkerPrivateMbLimit) { $breachCount.workerPrivateMb + 1 } else { 0 }
    $breachCount.workerWorkingSetMb = if ($workerWorkingSetMb -gt $WorkerWorkingSetMbLimit) { $breachCount.workerWorkingSetMb + 1 } else { 0 }

    if ($childPrivateMb -ne '') {
        $childPrivateValue = [double]$childPrivateMb
        $breachCount.childPrivateMb = if ($childPrivateValue -gt $ChildPrivateMbLimit) { $breachCount.childPrivateMb + 1 } else { 0 }
    } else {
        $breachCount.childPrivateMb = 0
    }

    if ($childWorkingSetMb -ne '') {
        $childWorkingValue = [double]$childWorkingSetMb
        $breachCount.childWorkingSetMb = if ($childWorkingValue -gt $ChildWorkingSetMbLimit) { $breachCount.childWorkingSetMb + 1 } else { 0 }
    } else {
        $breachCount.childWorkingSetMb = 0
    }

    if ($clock.Elapsed.TotalSeconds -ge $HardStopSeconds) {
        $stopOutcome = Stop-VerifiedProcessTree -Identity $workerIdentity -SimulateStopFailure:$SimulateStopFailure
        if ($stopOutcome.terminatedVerified) {
            $resultStatus = 'stopped'
            $resultReason = 'RUN_TIMEOUT_LIMIT'
        }
        else {
            $resultStatus = 'stop_failed'
            $resultReason = $stopOutcome.reason
        }
        Add-RunEvent -EventsPath $eventsPath -Message ("STOP_TRIGGER reason={0} workerPid={1}" -f $resultReason, $workerPid)
        break
    }

    foreach ($metric in @('workerPrivateMb', 'workerWorkingSetMb', 'childPrivateMb', 'childWorkingSetMb')) {
        if ($breachCount[$metric] -ge $BreachConsecutiveSamples) {
            $stopOutcome = Stop-VerifiedProcessTree -Identity $workerIdentity -SimulateStopFailure:$SimulateStopFailure
            if ($stopOutcome.terminatedVerified) {
                $resultStatus = 'stopped'
                $resultReason = ('LIMIT_BREACH_' + $metric)
            }
            else {
                $resultStatus = 'stop_failed'
                $resultReason = $stopOutcome.reason
            }
            Add-RunEvent -EventsPath $eventsPath -Message ("STOP_TRIGGER metric={0} reason={1}" -f $metric, $resultReason)
            break
        }
    }
    if ($resultStatus -ne 'completed') { break }

    Start-Sleep -Seconds $SampleIntervalSeconds
    }
    $result = [ordered]@{
    runId = $RunId
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    status = $resultStatus
    reason = $resultReason
    workerPid = $workerPid
    limits = [ordered]@{
        hardStopSeconds = $HardStopSeconds
        sampleIntervalSeconds = $SampleIntervalSeconds
        breachConsecutiveSamples = $BreachConsecutiveSamples
        workerPrivateMbLimit = $WorkerPrivateMbLimit
        workerWorkingSetMbLimit = $WorkerWorkingSetMbLimit
        childPrivateMbLimit = $ChildPrivateMbLimit
        childWorkingSetMbLimit = $ChildWorkingSetMbLimit
    }
    }
    Write-JsonFile -Path $resultPath -Object $result

    Write-Output ("RUN_ID {0}" -f $RunId)
    Write-Output ("STATUS {0}" -f $resultStatus)
    Write-Output ("REASON {0}" -f $resultReason)
    Write-Output ("RESULT_PATH {0}" -f $resultPath)
}
catch {
    $fail = [ordered]@{
        runId = $RunId
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        status = 'monitor_failed'
        reason = $_.Exception.Message
    }
    if (-not [string]::IsNullOrWhiteSpace($resultPath)) {
        Write-JsonFile -Path $resultPath -Object $fail
    }
    if (-not [string]::IsNullOrWhiteSpace($eventsPath)) {
        Add-RunEvent -EventsPath $eventsPath -Message ("MONITOR_FAILED reason={0}" -f $_.Exception.Message)
    }
    throw
}
