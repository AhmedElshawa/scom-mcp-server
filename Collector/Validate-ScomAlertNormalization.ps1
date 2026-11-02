$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSdkReader.psm1') -Force
$parent = [Guid]'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$now = [DateTimeOffset]'2026-11-01T08:00:00Z'
$alert = [pscustomobject]@{
    Id = [Guid]'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
    MonitoringObjectId = $parent
    ResolutionState = [byte]0
    Severity = 'Warning'
    Name = 'Synthetic alert'
    Description = 'Synthetic description'
}
$result = ConvertTo-SCOMObservedAlert $alert $parent $now
if ($result.resolutionState -ne 0 -or $result.severity -ne 'warning' -or $null -ne $result.description) {
    throw 'Alert normalization lost new state or leaked opt-out description.'
}
$alert.ResolutionState = [byte]255
$resolved = ConvertTo-SCOMObservedAlert $alert $parent $now -IncludeDescription $true
if ($resolved.resolutionState -ne 255 -or $resolved.description -ne 'Synthetic description') {
    throw 'Explicit resolved observation or opt-in description changed.'
}
function Reject([scriptblock]$Action, [string]$Code) {
    try { & $Action | Out-Null }
    catch { if ($_.Exception.Message -eq $Code) { return }; throw }
    throw "Expected rejection: $Code"
}
Reject { ConvertTo-SCOMObservedAlert $alert ([Guid]::NewGuid()) $now } 'SCOM_ALERT_ID_OR_PARENT_INVALID'
$alert.ResolutionState = $null
Reject { ConvertTo-SCOMObservedAlert $alert $parent $now } 'SCOM_ALERT_RESOLUTION_INVALID'
$alert.ResolutionState = -1
Reject { ConvertTo-SCOMObservedAlert $alert $parent $now } 'SCOM_ALERT_RESOLUTION_INVALID'
$alert.ResolutionState = [byte]0
$alert.Severity = 'MatchMonitorHealth'
Reject { ConvertTo-SCOMObservedAlert $alert $parent $now } 'SCOM_ALERT_SEVERITY_INVALID'
$alert.Severity = 'Error'
$alert.Description = ('x' * 2001)
Reject { ConvertTo-SCOMObservedAlert $alert $parent $now -IncludeDescription $true } 'SCOM_ALERT_DESCRIPTION_INVALID'
Write-Output 'Alert normalization validation passed: 7 synthetic cases; explicit state/parent/severity and description opt-in.'
