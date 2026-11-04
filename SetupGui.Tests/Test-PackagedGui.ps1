[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$ReportPath = (Join-Path $PSScriptRoot 'bootstrap-report.json')
)
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageDirectory)
$exe = Join-Path $package 'ScomSetup.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Packaged ScomSetup.exe missing' }

function Invoke-GuiTest([string[]]$Arguments, [switch]$WithoutDotnet) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $package
    $start.CreateNoWindow = $true
    foreach ($arg in $Arguments) { $start.ArgumentList.Add($arg) }
    if ($WithoutDotnet) {
        $start.Environment['DOTNET_ROOT'] = (Join-Path $package 'nonexistent-runtime')
        $start.Environment['DOTNET_ROOT_X64'] = (Join-Path $package 'nonexistent-runtime')
        $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        $start.Environment['PATH'] = "$env:SystemRoot\System32"
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(60000)) {
            # Test modes never invoke the bridge or mutate the host.
            Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
            throw 'Read-only GUI test timed out'
        }
        if ($process.ExitCode -ne 0) { throw "Read-only GUI test failed: $($process.ExitCode)" }
    } finally { $process.Dispose() }
}

Invoke-GuiTest -Arguments @('--self-test')
$report = [IO.Path]::GetFullPath($ReportPath)
if (Test-Path -LiteralPath $report) { throw 'Choose a new report path; existing report will not be overwritten by this harness' }
Invoke-GuiTest -Arguments @('--bootstrap-probe', $report) -WithoutDotnet
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $result.selfContained -or -not $result.uiLoaded -or
    $result.processArchitecture -cne 'X64' -or $result.framework -notlike '.NET 8.*' -or
    $result.bridgeCalls -ne 0 -or
    -not ([IO.Path]::GetFullPath($result.runtimeDirectory)).StartsWith(
        $package.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Self-contained native GUI bootstrap assertion failed'
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $package 'ScomSetup.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.PSObject.Properties.Name -contains 'framework' -or
    $runtimeConfig.runtimeOptions.PSObject.Properties.Name -contains 'frameworks') {
    throw 'Runtime configuration depends on an installed shared framework'
}
if (-not ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -ceq 'Microsoft.WindowsDesktop.App' -and $_.version -ceq '8.0.33' }) -or
    -not (Test-Path -LiteralPath (Join-Path $package 'coreclr.dll') -PathType Leaf)) {
    throw 'Package must contain the pinned Windows Desktop runtime and root coreclr.dll'
}
Write-Output 'PASS: sequential STA wizard, inert probe, read-only Shown Bootstrap, field mapping, approvals, gating, account handoff arguments, isolated bridge environment and packaged no-dotnet bootstrap.'
