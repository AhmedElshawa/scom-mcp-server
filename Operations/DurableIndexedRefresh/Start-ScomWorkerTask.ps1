[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$TaskName = 'SCOM-MCP Durable Indexed Refresh',
    [string]$TaskPath = '\'
)

. (Join-Path $PSScriptRoot 'DurableIndexedRefresh.Common.ps1')
$task = Get-OwnedRefreshTask -TaskName $TaskName -TaskPath $TaskPath
$null = Get-RefreshTaskActionInfo -Task $task -RequireFiles
$state = Get-RefreshStateDirectory -Task $task
$runtime = Read-RefreshRuntimeStatus -StateDirectory $state -AllowMissing `
    -TaskRunning:([string]$task.State -eq 'Running')
if ($null -ne $runtime -and $runtime.NeedsRearm) {
    throw 'Worker is gated by a terminal failure or unknown prior-running state. Diagnose and use Rearm-ScomWorkerTask.ps1 explicitly.'
}
if ([string]$task.State -eq 'Running') {
    throw 'The owned worker task is already running; no second instance was requested.'
}
if ($PSCmdlet.ShouldProcess("$TaskPath$TaskName", 'Start this exact owned worker task after operator review')) {
    Start-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -ErrorAction Stop
    [pscustomobject]@{
        TaskName = $TaskName
        TaskPath = $TaskPath
        Result = 'Start requested; inspect worker publication status and authenticated source freshness separately.'
    }
}
