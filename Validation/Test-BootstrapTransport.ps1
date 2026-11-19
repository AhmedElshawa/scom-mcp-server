[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference='Stop'
$engine=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$probe=Join-Path $PSScriptRoot 'Get-BootstrapTransportShape.ps1'
foreach ($kind in @('Bootstrap','Empty','Malformed','Null','Unknown')) {
    $json=& $engine -NoProfile -File $probe -PackageDirectory $PackageDirectory -RequestKind $kind
    if ($LASTEXITCODE -ne 0) { throw 'Transport shape probe failed under normal policy; never bypass it.' }
    $shape=($json -join "`n") | ConvertFrom-Json
    if ($shape.rootKind -cne 'object' -or
        @($shape.properties | Where-Object { $_.name -ceq 'ok' -and $_.type -ceq 'boolean' }).Count -ne 1 -or
        $shape.unknownPropertyCount -ne 0) { throw 'Actual production bridge did not return the required boolean envelope.' }
    if ($kind -eq 'Bootstrap') {
        if ($shape.exitCode -ne 0 -or
            @($shape.properties | Where-Object { $_.name -ceq 'checks' -and $_.type -ceq 'array' }).Count -ne 1) {
            throw 'Actual Bootstrap did not return individual checks.'
        }
    } elseif ($kind -ne 'Unknown' -and $shape.exitCode -ne 1) {
        throw 'Invalid input did not fail through the production error envelope.'
    }
}
Write-Output 'PASS: actual Windows PowerShell 5.1 production entrypoint stdin/stdout with Bootstrap, empty, malformed, null and unknown-action requests: required top-level boolean ok and fixed envelope only. No installation or source access.'
