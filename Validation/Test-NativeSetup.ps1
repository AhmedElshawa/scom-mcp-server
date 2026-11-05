[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory,[switch]$RunBridgeDiagnostic)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PackageDirectory)
$exe=Join-Path $root 'ScomSetup.exe'
$bytes=[IO.File]::ReadAllBytes($exe)
$pe=[BitConverter]::ToInt32($bytes,0x3c)
if ($bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a -or
    [BitConverter]::ToUInt32($bytes,$pe) -ne 0x4550 -or
    [BitConverter]::ToUInt16($bytes,$pe+4) -ne 0x8664 -or
    [BitConverter]::ToUInt16($bytes,$pe+24+68) -ne 2) {
    throw 'Expected a genuine x64 Windows GUI PE apphost, not a script/console wrapper.'
}
$runtime=Get-Content -LiteralPath (Join-Path $root 'ScomSetup.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.PSObject.Properties['framework'] -or
    $runtime.runtimeOptions.PSObject.Properties['frameworks'] -or
    -not $runtime.runtimeOptions.PSObject.Properties['includedFrameworks']) {
    throw 'GUI must bundle its runtime rather than require an installed shared framework.'
}
$report=Join-Path ([IO.Path]::GetTempPath()) ('scom-native-proof-'+[guid]::NewGuid().ToString('N')+'.json')
try {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$exe
    $start.UseShellExecute=$false
    $start.WorkingDirectory=$root
    $start.Arguments='--bootstrap-probe "'+$report+'"'
    $start.EnvironmentVariables['PATH']=Join-Path $env:SystemRoot 'System32'
    $start.EnvironmentVariables['DOTNET_ROOT']=Join-Path $root 'no-installed-dotnet'
    $start.EnvironmentVariables['DOTNET_ROOT_X64']=Join-Path $root 'no-installed-dotnet'
    $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP']='0'
    $process=[Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(60000)) {
        Stop-Process -Id $process.Id
        throw 'Native bootstrap probe timed out.'
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) {
        throw 'Native GUI bootstrap failed; no external runtime fallback is acceptable.'
    }
    $proof=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $proof.uiLoaded -or $proof.bridgeCalls -ne 0 -or $proof.processArchitecture -cne 'X64' -or
        -not ([IO.Path]::GetFullPath($proof.runtimeDirectory)).StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Native UI did not prove x64 startup using only its packaged runtime.'
    }
    Write-Output 'PASS: genuine Windows GUI PE, self-contained runtime contract and native form loaded using packaged CLR with shared lookup disabled. No bridge or installation invoked.'
    Remove-Item -LiteralPath $report
    $start.Arguments='--self-test "'+$report+'"'
    $process=[Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(60000)) {
        Stop-Process -Id $process.Id
        throw 'Native wizard self-tests timed out.'
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) {
        throw 'Native wizard self-tests failed.'
    }
    $tests=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $tests.ok -or $tests.bridgeCalls -ne 0) { throw 'Native wizard did not pass its no-effect UI/protocol self-tests.' }
    Write-Output 'PASS: packaged native wizard sequential navigation, automatic fake preflight/status colors, defaults/resume, identity handoff arguments and approval/cancellation/failure assertions; no production bridge calls or host/source effects.'
    if ($RunBridgeDiagnostic) {
        Remove-Item -LiteralPath $report
        $start.Arguments='--diagnose-bootstrap "'+$report+'"'
        $process=[Diagnostics.Process]::Start($start)
        if (-not $process.WaitForExit(120000)) {
            Stop-Process -Id $process.Id
            throw 'Read-only native bridge diagnostic timed out.'
        }
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) {
            throw 'Actual native-to-Windows-PowerShell bootstrap did not obtain results. Inspect fixed diagnostic code; never bypass policy.'
        }
        $diagnostic=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if ($diagnostic.bridgeCalls -ne 1 -or $diagnostic.action -cne 'Bootstrap' -or
            $diagnostic.checks.Count -lt 10 -or
            @($diagnostic.checks | Where-Object { $_.name -ceq 'Windows PowerShell 5.1' -and $_.passed }).Count -ne 1) {
            throw 'Actual packaged GUI/PowerShell protocol did not return real individual local prerequisite results.'
        }
        Write-Output 'PASS: actual packaged native-to-Windows-PowerShell 5.1 Bootstrap transport/JSON/check contract; read-only local probes only, no SCOM connection or installation.'
    }
} finally {
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
}
