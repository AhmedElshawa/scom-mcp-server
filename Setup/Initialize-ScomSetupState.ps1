[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.psm1')
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Windows.psm1')
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'SETUP_ADMINISTRATOR_REQUIRED' }
Assert-ScomSetupPath $Path
if (Test-Path -LiteralPath $Path) { throw 'SETUP_NEW_STATE_DIRECTORY_REQUIRED' }
if (-not (Test-Path -LiteralPath (Split-Path $Path -Parent) -PathType Container)) { throw 'SETUP_EXISTING_PARENT_DIRECTORY_REQUIRED' }
if (-not (Confirm-ScomSetupEffect "Create ONLY $Path and restrict it to SYSTEM and Administrators for deployment/provisioning journals. No IIS/source/Graph/install actions.")) {
    throw 'SETUP_CANCELLED_NO_ACTION'
}
$null = New-Item -ItemType Directory -Path $Path
Set-ScomSetupDirectoryAcl $Path @{}
Assert-ScomSetupPrivateDirectory $Path
Write-Output 'Private journal directory ready.'
