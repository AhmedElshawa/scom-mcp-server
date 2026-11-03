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
$runtime = Read-RefreshRuntimeStatus -StateDirectory $state -AllowMissing `
    -TaskRunning:([string]$task.State -eq 'Running')
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
    LastFailureCode = if ($null -eq $runtime) { $null }
        elseif ($runtime.state -eq 'running' -and -not $runtime.lastFailureCode) { 'UNEXPECTED_PRIOR_RUNNING_STATE' }
        else { $runtime.lastFailureCode }
    RearmRequired = if ($null -eq $runtime) { $true } else { $runtime.NeedsRearm }
    FreshnessAuthority = 'Authenticated get_source_status; task liveness is not data freshness.'
}
