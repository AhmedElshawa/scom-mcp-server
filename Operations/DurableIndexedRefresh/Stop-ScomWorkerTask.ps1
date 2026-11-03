[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$TaskName = 'SCOM-MCP Durable Indexed Refresh',
    [string]$TaskPath = '\',
    [ValidateRange(1, 300)][int]$WaitSeconds = 30
)

. (Join-Path $PSScriptRoot 'DurableIndexedRefresh.Common.ps1')
$task = Get-OwnedRefreshTask -TaskName $TaskName -TaskPath $TaskPath
$state = Get-RefreshStateDirectory -Task $task
if ([string]$task.State -ne 'Running') {
    throw 'The owned worker task is not running; no stop request was sent.'
}
$runtime = Read-RefreshRuntimeStatus -StateDirectory $state -TaskRunning
if ($runtime.state -ne 'running' -or $runtime.NeedsRearm) {
    throw 'Worker runtime status is inconsistent or gated; inspect protected status instead of sending a stop request.'
}
if ($PSCmdlet.ShouldProcess("$TaskPath$TaskName", 'Request stop of this exact scheduled worker task')) {
    Invoke-RefreshWorkerControl -Task $task -Operation stop-request -StateDirectory $state
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    do {
        Start-Sleep -Milliseconds 250
        $current = Get-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -ErrorAction Stop
        if ([string]$current.State -ne 'Running') {
            $finalRuntime = Read-RefreshRuntimeStatus -StateDirectory $state
            if ($finalRuntime.state -ne 'stopped' -or $finalRuntime.NeedsRearm -or $finalRuntime.failureCode) {
                throw 'Worker stopped with a terminal or unknown outcome; preserve status/logs and diagnose before explicit rearm.'
            }
            [pscustomobject]@{
                TaskName = $TaskName
                TaskState = [string]$current.State
                Result = 'Worker observed the explicit stop request and recorded a clean stop.'
            }
            return
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Task remains running or stop outcome is unknown. Do not kill processes; inspect task and protected runtime status.'
}
