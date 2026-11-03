[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$TaskName = 'SCOM-MCP Durable Indexed Refresh',
    [string]$TaskPath = '\',
    [ValidateRange(1, 300)][int]$WaitSeconds = 30
)

. (Join-Path $PSScriptRoot 'DurableIndexedRefresh.Common.ps1')
$task = Get-OwnedRefreshTask -TaskName $TaskName -TaskPath $TaskPath
if ($PSCmdlet.ShouldProcess("$TaskPath$TaskName", 'Request stop of this exact scheduled worker task')) {
    Stop-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -ErrorAction Stop
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    do {
        Start-Sleep -Milliseconds 250
        $current = Get-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -ErrorAction Stop
        if ([string]$current.State -ne 'Running') {
            [pscustomobject]@{
                TaskName = $TaskName
                TaskState = [string]$current.State
                Result = 'Stop request completed; inspect runtime status for graceful versus unknown outcome.'
            }
            return
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Task remains running or stop outcome is unknown. Do not kill processes; inspect task and protected runtime status.'
}
