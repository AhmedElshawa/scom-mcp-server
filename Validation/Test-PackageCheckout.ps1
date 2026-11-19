[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
$validate = Join-Path $PSScriptRoot 'Test-Package.ps1'
$source = [IO.Path]::GetFullPath($PackageDirectory)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('scom-package-checkout-' + [guid]::NewGuid().ToString('N'))
try {
    & $validate -PackageDirectory $source
    Copy-Item -LiteralPath $source -Destination $fixture -Recurse
    $manifestPath = Join-Path $fixture 'package-manifest.json'
    $originalManifest = [IO.File]::ReadAllBytes($manifestPath)
    foreach ($name in @('ScomEntra.psm1', 'ScomSetup.Windows.psm1')) {
        $relative = 'setup\' + $name
        $path = Join-Path $fixture $relative
        $original = [IO.File]::ReadAllBytes($path)
        try {
            [IO.File]::WriteAllText($path, '# synthetic stale module', [Text.UTF8Encoding]::new($false))
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $entry = @($manifest.files | Where-Object { $_.path -ceq $relative })
            if ($entry.Count -ne 1) { throw 'Fixture manifest entry missing or ambiguous.' }
            $entry[0].sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

            # Prove the substituted file and regenerated manifest pass the existing content checks.
            & $validate -PackageDirectory $fixture -ContentOnly
            $rejected = $false
            try { & $validate -PackageDirectory $fixture } catch {
                if ($_.Exception.Message -cne "Package setup module does not match the reviewed checkout: $name") { throw }
                $rejected = $true
            }
            if (-not $rejected) { throw "Stale module was not rejected: $name" }
        } finally {
            [IO.File]::WriteAllBytes($path, $original)
            [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
        }
    }
    Write-Output 'PASS: each stale setup module is rejected even when the package manifest is regenerated.'
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
