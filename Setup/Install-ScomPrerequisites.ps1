[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StateDirectory,
    [switch]$InstallIis,
    [string]$HostingBundlePath,
    [string]$HostingBundleVersion,
    [string]$HostingBundleSha256,
    [switch]$AcknowledgeRestart
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Windows.psm1')
Import-Module (Join-Path $PSScriptRoot 'ScomPrerequisiteInstall.psm1')
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'SETUP_ADMINISTRATOR_REQUIRED' }
Assert-ScomSetupPrivateDirectory $StateDirectory
$lease=[IO.File]::Open((Join-Path $StateDirectory 'prerequisite.lock'),'OpenOrCreate','ReadWrite','None')
try {
    Invoke-ScomPrerequisiteInstall -JournalPath (Join-Path $StateDirectory 'prerequisite-journal.json') `
        -Adapters (New-ScomPrerequisiteWindowsAdapters $StateDirectory) -InstallIis:$InstallIis `
        -InstallerPath $HostingBundlePath -ExpectedVersion $HostingBundleVersion -ExpectedSha256 $HostingBundleSha256 `
        -AcknowledgeRestart:$AcknowledgeRestart
}
finally { $lease.Dispose() }
