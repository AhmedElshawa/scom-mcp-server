[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$WarningPreference='SilentlyContinue'
$InformationPreference='SilentlyContinue'
$ProgressPreference='SilentlyContinue'
[Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$failureCode='SETUP_GUI_REQUEST_INVALID'
try {
    $text=[Console]::In.ReadLine()
    if ($null -eq $text -or [Text.Encoding]::UTF8.GetByteCount($text) -gt 1MB) { throw 'SETUP_GUI_REQUEST_INVALID' }
    $request=$text | ConvertFrom-Json -ErrorAction Stop
    $failureCode='SETUP_GUI_MODULE_LOAD_FAILED'
    Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Gui.psm1') -ErrorAction Stop
    $failureCode='SETUP_GUI_DISPATCH_FAILED'
    $response=Invoke-ScomSetupGuiRequest -Request $request -PackageRoot (Split-Path $PSScriptRoot -Parent)
    $failureCode='SETUP_GUI_RESPONSE_SERIALIZATION_FAILED'
    [Console]::Out.Write(($response | ConvertTo-Json -Depth 48 -Compress))
}
catch {
    # Fixed phase codes only: never serialize exception messages or request data.
    [Console]::Out.Write(('{"ok":false,"code":"'+$failureCode+'","message":"The local setup bridge failed at the reported phase. No prerequisite result or installation success is inferred. Review the matching diagnostic guidance; do not bypass Windows policy.","approvalRequired":false}'))
    exit 1
}
