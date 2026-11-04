[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('scom-schema-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$exe=Join-Path ([IO.Path]::GetFullPath($PackageDirectory)) 'ScomSetup.exe'
try {
    foreach ($engine in @((Get-Process -Id $PID).Path,(Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe')) | Select-Object -Unique) {
        $fixtures=Join-Path $root ([guid]::NewGuid().ToString('N'))
        & $engine -NoProfile -File (Join-Path $PSScriptRoot '..\Setup\Write-ScomBootstrapFixtures.ps1') -OutputDirectory $fixtures
        if ($LASTEXITCODE -ne 0) { throw 'Schema fixture generation failed under normal policy; never bypass it.' }
        foreach ($fixture in Get-ChildItem -LiteralPath $fixtures -Filter '*.json') {
            $report=Join-Path $root ([guid]::NewGuid().ToString('N')+'.json')
            $start=[Diagnostics.ProcessStartInfo]::new()
            $start.FileName=$exe;$start.UseShellExecute=$false
            $start.Arguments='--decode-bootstrap-fixture "'+$fixture.FullName+'" "'+$report+'"'
            $process=[Diagnostics.Process]::Start($start)
            if (-not $process.WaitForExit(60000)) { Stop-Process -Id $process.Id;throw 'Schema fixture native decode timed out.' }
            if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw 'Actual PowerShell Bootstrap fixture failed native decode.' }
            $proof=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
            if (-not $proof.decoded -or $proof.bridgeCalls -ne 0) { throw 'Native fixture decode did not prove the wire contract.' }
        }
    }
    $invalid=Join-Path $root 'invalid.json'
    $report=Join-Path $root 'invalid-report.json'
    [IO.File]::WriteAllText($invalid,'{"ok":false,"defaults":{"operationsManagerModule":["synthetic-private-marker"]}}',[Text.UTF8Encoding]::new($false))
    $start.Arguments='--decode-bootstrap-fixture "'+$invalid+'" "'+$report+'"'
    $process=[Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(60000)) { Stop-Process -Id $process.Id;throw 'Invalid schema fixture timeout.' }
    $text=Get-Content -LiteralPath $report -Raw
    $proof=$text | ConvertFrom-Json
    if ($process.ExitCode -ne 1 -or $proof.decoded -or
        $proof.contractField -cne 'defaults.operationsManagerModule' -or
        $proof.expectedType -cne 'string' -or $proof.actualType -cne 'array' -or
        $text -match 'synthetic-private-marker') { throw 'Schema diagnostic did not report only fixed field/types and reject invalid payload.' }
    Write-Output 'PASS: actual backend Bootstrap projection and PowerShell 5.1/7 JSON decoded by packaged native DTO: zero/one/many nested sites/apps/bindings/certificates/pools, blocked/pass and error envelopes. No live probes or source calls.'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
