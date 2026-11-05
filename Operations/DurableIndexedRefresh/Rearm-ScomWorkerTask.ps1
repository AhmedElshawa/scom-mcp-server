[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$TaskName = 'SCOM-MCP Durable Indexed Refresh',
    [string]$TaskPath = '\',
    [Parameter(Mandatory = $true)][switch]$AcknowledgeFailure
)

. (Join-Path $PSScriptRoot 'DurableIndexedRefresh.Common.ps1')
$task = Get-OwnedRefreshTask -TaskName $TaskName -TaskPath $TaskPath
if ([string]$task.State -eq 'Running') {
    throw 'The owned worker task is running; stop it cleanly and verify no worker process remains before rearming.'
}
$state = Get-RefreshStateDirectory -Task $task
$runtime = Read-RefreshRuntimeStatus -StateDirectory $state
if (-not $runtime.NeedsRearm) {
    throw 'No terminal failure or unknown prior-running state requires rearm.'
}
if ($PSCmdlet.ShouldProcess("$TaskPath$TaskName", 'Acknowledge the preserved terminal/unknown worker state and explicitly rearm')) {
    Invoke-RefreshWorkerControl -Task $task -Operation rearm -StateDirectory $state
    [pscustomobject]@{
        TaskName = $TaskName
        TaskPath = $TaskPath
        LastFailureCode = $runtime.lastFailureCode
        Result = 'Runtime gate explicitly rearmed; preserved failure evidence remains available. Start the task only after diagnosis.'
    }
}
