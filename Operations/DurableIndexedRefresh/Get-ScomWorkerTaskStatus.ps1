[CmdletBinding()]
param(
    [string]$TaskName = 'SCOM-MCP Durable Indexed Refresh',
    [string]$TaskPath = '\',
    [string]$StateDirectory
)

. (Join-Path $PSScriptRoot 'DurableIndexedRefresh.Common.ps1')
$task = Get-OwnedRefreshTask -TaskName $TaskName -TaskPath $TaskPath
$state = Get-RefreshStateDirectory -Task $task -StateDirectory $StateDirectory
$taskInfo = Get-ScheduledTaskInfo -TaskName $TaskName -TaskPath $TaskPath -ErrorAction Stop
$statusPath = Join-Path $state 'worker.status.json'
$runtime = $null
if (Test-Path -LiteralPath $statusPath -PathType Leaf) {
    if ((Get-Item -LiteralPath $statusPath).Length -gt 8192) {
        throw 'Worker status file exceeds its byte budget; inspect the protected state before restarting.'
    }
    $runtime = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if ($runtime.state -notin @('running', 'stopped') -or
        $runtime.lastOutcome -notin @($null, 'published', 'deferred')) {
        throw 'Worker status file is invalid; inspect the protected state before restarting.'
    }
}
$taskState = [string]$task.State
$runtimeState = if ($null -eq $runtime) { 'missing' } else { [string]$runtime.state }
$consistency = if ($null -eq $runtime) { 'status-missing' }
    elseif (($taskState -eq 'Running') -eq ($runtimeState -eq 'running')) { 'consistent' }
    else { 'unknown-outcome-inspect-required' }
[pscustomobject]@{
    TaskName = $TaskName
    TaskPath = $TaskPath
    TaskState = $taskState
    LastTaskResult = $taskInfo.LastTaskResult
    RuntimeState = $runtimeState
    RuntimeConsistency = $consistency
    LastOutcome = if ($null -eq $runtime) { $null } else { $runtime.lastOutcome }
    LastAttemptUtc = if ($null -eq $runtime) { $null } else { $runtime.lastAttemptUtc }
    LastPublicationUtc = if ($null -eq $runtime) { $null } else { $runtime.lastPublicationUtc }
    ConsecutiveDeferredAttempts = if ($null -eq $runtime) { $null } else { $runtime.consecutiveDeferredAttempts }
    FailureCode = if ($null -eq $runtime) { $null } else { $runtime.failureCode }
    FreshnessAuthority = 'Authenticated get_source_status; task liveness is not data freshness.'
}
