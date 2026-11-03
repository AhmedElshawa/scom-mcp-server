[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$TaskName = 'SCOM-MCP Durable Indexed Refresh',
    [string]$TaskPath = '\'
)

. (Join-Path $PSScriptRoot 'DurableIndexedRefresh.Common.ps1')
$null = Get-OwnedRefreshTask -TaskName $TaskName -TaskPath $TaskPath
if ($PSCmdlet.ShouldProcess("$TaskPath$TaskName", 'Unregister this exact owned scheduled task without deleting its state or logs')) {
    Unregister-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -Confirm:$false -ErrorAction Stop
    [pscustomobject]@{
        TaskName = $TaskName
        TaskPath = $TaskPath
        Result = 'Unregistered; protected runtime state and logs were preserved.'
    }
}
