[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunId,
    [string]$RunRoot = 'C:\ProgramData\SCOMMCP-Private\indexed-supervised-runs',
    [string]$Reason = 'OWNER_REQUESTED_STOP',
    [switch]$SimulateStopFailure
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

. (Join-Path $PSScriptRoot 'SupervisedIndexedRun.Common.ps1')

$runDirectory = Join-Path $RunRoot $RunId
$statePath = Join-Path $runDirectory 'run-state.json'
$eventsPath = Join-Path $runDirectory 'events.log'
$stopResultPath = Join-Path $runDirectory 'stop-result.json'

if (-not (Test-Path -LiteralPath $statePath)) { throw 'Run state file was not found.' }
$state = Read-JsonFile -Path $statePath
$workerIdentity = $state.worker.identity
if ($null -eq $workerIdentity) { throw 'Worker identity is missing in run state.' }

$stopOutcome = Stop-VerifiedProcessTree -Identity $workerIdentity -SimulateStopFailure:$SimulateStopFailure
$status = if ($stopOutcome.terminatedVerified) { 'root_stopped_child_join_pending' } elseif ($stopOutcome.stalePidRefused) { 'stop_refused_stale_pid' } else { 'stop_failed' }

$result = [ordered]@{
    runId = $RunId
    stoppedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    reason = $Reason
    status = $status
    workerPid = [int]$workerIdentity.pid
    workerStartTimeFileTimeUtc = [string]$workerIdentity.startTimeFileTimeUtc
    stop = $stopOutcome
}
Write-JsonFile -Path $stopResultPath -Object $result

Add-RunEvent -EventsPath $eventsPath -Message ("STOP_TRIGGER manual reason={0} status={1}" -f $Reason, $status)

if ($state.PSObject.Properties.Name -contains 'status') { $state.status = 'attention_required' }
else { $state | Add-Member -NotePropertyName status -NotePropertyValue (if ($status -eq 'stopped') { 'stopped' } else { 'attention_required' }) }
if ($state.PSObject.Properties.Name -contains 'result') { $state.result = $result }
else { $state | Add-Member -NotePropertyName result -NotePropertyValue $result }
Write-JsonFile -Path $statePath -Object $state

Write-Output ("RUN_ID {0}" -f $RunId)
Write-Output ("STATUS {0}" -f $status)
Write-Output ("REASON {0}" -f $Reason)
Write-Output ("STOP_RESULT_PATH {0}" -f $stopResultPath)
