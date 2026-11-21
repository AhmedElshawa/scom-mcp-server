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
    else { 'stale-marker-recovers-automatically-on-next-start' }
[pscustomobject]@{
    TaskName = $TaskName
    TaskPath = $TaskPath
    TaskState = $taskState
    LastTaskResult = $taskInfo.LastTaskResult
    RuntimeState = $runtimeState
    RuntimeConsistency = $consistency
    HeartbeatUtc = if ($null -eq $runtime) { $null } else { $runtime.heartbeatUtc }
    HostName = if ($null -eq $runtime) { $null } else { $runtime.hostName }
    LastOutcome = if ($null -eq $runtime) { $null } else { $runtime.lastOutcome }
    LastAttemptUtc = if ($null -eq $runtime) { $null } else { $runtime.lastAttemptUtc }
    LastPublicationUtc = if ($null -eq $runtime) { $null } else { $runtime.lastPublicationUtc }
    ConsecutiveDeferredAttempts = if ($null -eq $runtime) { $null } else { $runtime.consecutiveDeferredAttempts }
    FailureCode = if ($null -eq $runtime) { $null } else { $runtime.failureCode }
    LastFailureCode = if ($null -eq $runtime) { $null } else { $runtime.lastFailureCode }
    RearmRequired = if ($null -eq $runtime) { $true } else { $runtime.NeedsRearm }
    FreshnessAuthority = 'Authenticated get_source_status; task liveness is not data freshness.'
}
