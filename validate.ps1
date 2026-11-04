$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Push-Location $PSScriptRoot
try {
    $powerShell = (Get-Process -Id $PID).Path
    & (Join-Path $PSScriptRoot 'Setup\Validate-ScomSetup.ps1')
    & (Join-Path $PSScriptRoot 'Setup\Validate-ScomSetupGui.ps1')
    & (Join-Path $PSScriptRoot 'Setup\Validate-ScomSetupWindows.ps1')
    & (Join-Path $PSScriptRoot 'Setup\Validate-ScomPrerequisiteInstall.ps1')
    $desktopPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $desktopPowerShell -NoProfile -File (Join-Path $PSScriptRoot 'Setup\Validate-ScomSetup.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Desktop PowerShell setup validation failed; inspect the preceding error. Never bypass signing policy.' }
    & $desktopPowerShell -NoProfile -File (Join-Path $PSScriptRoot 'Setup\Validate-ScomSetupGui.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Desktop PowerShell native GUI bridge validation failed.' }
    & $desktopPowerShell -NoProfile -File (Join-Path $PSScriptRoot 'Setup\Validate-ScomSetupWindows.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Desktop PowerShell Windows adapter validation failed.' }
    & $desktopPowerShell -NoProfile -File (Join-Path $PSScriptRoot 'Setup\Validate-ScomPrerequisiteInstall.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Desktop PowerShell prerequisite installer validation failed.' }
    & (Join-Path $PSScriptRoot 'Collector\Validate-Collector.ps1')
    & $powerShell -NoProfile -File (Join-Path $PSScriptRoot 'Collector\Validate-ScomSupportedWindowsScope.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Supported Windows scope validation failed.' }
    & $powerShell -NoProfile -File (Join-Path $PSScriptRoot 'Collector\Validate-ScomOperationalLogging.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Collector operational logging validation failed.' }
    & (Join-Path $PSScriptRoot 'Collector\Validate-ScomPreflight.ps1')
    & (Join-Path $PSScriptRoot 'Collector\Validate-ScomMetricCompatibilityReport.ps1')
    & $powerShell -NoProfile -File (Join-Path $PSScriptRoot 'Collector\Validate-ScomSdkReader.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'SDK reader validation failed.' }
    & $powerShell -NoProfile -File (Join-Path $PSScriptRoot 'Collector\Validate-ScomManagementGroupScope.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Management-group scope validation failed.' }
    & (Join-Path $PSScriptRoot 'Collector\Validate-ScomGroupMembership.ps1')
    & $powerShell -NoProfile -File (Join-Path $PSScriptRoot 'Collector\Validate-ScomRelationshipReader.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Relationship reader validation failed.' }
    & (Join-Path $PSScriptRoot 'Collector\Validate-ScomObjectNormalization.ps1')
    & (Join-Path $PSScriptRoot 'Collector\Validate-ScomAlertNormalization.ps1')
    & (Join-Path $PSScriptRoot 'Collector\Validate-ScomGenericProfile.ps1')
    & (Join-Path $PSScriptRoot 'Collector\Validate-ScomMetricProfile.ps1')
    & $powerShell -NoProfile -File (Join-Path $PSScriptRoot 'Collector\Validate-ScomWindowsProjection.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Windows source projection validation failed.' }
    $compatibility = & (Join-Path $PSScriptRoot 'Collector\Get-SCOMCompatibilityReport.ps1') | ConvertFrom-Json
    if ($compatibility.certificationStatus -ne 'notCertified' -or
        -not $compatibility.metadataOnly -or $compatibility.sourceQueriesExecuted -ne 0 -or
        $compatibility.operationsManager.commands.Count -ne 4 -or
        $compatibility.unresolvedEvidence.Count -ne 7) {
        throw 'Compatibility metadata must not imply source certification.'
    }
    dotnet run --project '.\Validation\Validation.csproj' --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Validation failed.' }
}
finally { Pop-Location }
