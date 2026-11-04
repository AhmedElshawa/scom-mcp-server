[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$WarningPreference='SilentlyContinue'
$InformationPreference='SilentlyContinue'
$ProgressPreference='SilentlyContinue'
[Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
try {
    $text=[Console]::In.ReadLine()
    if ($null -eq $text -or [Text.Encoding]::UTF8.GetByteCount($text) -gt 1MB) { throw 'SETUP_GUI_REQUEST_INVALID' }
    $request=$text | ConvertFrom-Json -ErrorAction Stop
    Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Gui.psm1') -ErrorAction Stop
    $response=Invoke-ScomSetupGuiRequest -Request $request -PackageRoot (Split-Path $PSScriptRoot -Parent)
    [Console]::Out.Write(($response | ConvertTo-Json -Depth 48 -Compress))
}
catch {
    [Console]::Out.Write('{"ok":false,"code":"SETUP_GUI_BRIDGE_BLOCKED","message":"The setup bridge could not run. Review Windows PowerShell 5.1 availability, customer script signing policy and the evaluation guide. No policy bypass is attempted.","approvalRequired":false}')
    exit 1
}
