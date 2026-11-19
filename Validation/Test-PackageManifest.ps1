[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory,[switch]$ContentOnly)

$ErrorActionPreference = 'Stop'
$validate = Join-Path $PSScriptRoot 'Test-Package.ps1'
$source = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('scom-manifest-' + [guid]::NewGuid().ToString('N'))

function Test-UpgradeManifest {
    param([string]$Package)
    # Match the manifest and file-set checks in main's easy-indexed-upgrade guide.
    $manifest = Get-Content -LiteralPath (Join-Path $Package 'package-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.format -ne 1 -or $manifest.sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid package manifest.' }
    $seen = @{}
    foreach ($entry in $manifest.files) {
        $file = [IO.Path]::GetFullPath((Join-Path $Package $entry.path))
        if (-not $file.StartsWith($Package + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $seen.ContainsKey($file) -or $entry.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw 'Package manifest path or checksum mismatch.'
        }
        $seen[$file] = $true
    }
    if (@(Get-ChildItem -LiteralPath $Package -Recurse -File -Force).Count -ne $seen.Count + 1) {
        throw 'Unexpected files in the extracted package.'
    }
}

function Assert-SetupRejected {
    param([string]$Package)
    $rejected = $false
    try { & $validate -PackageDirectory $Package -ContentOnly | Out-Null } catch {
        if ($_.Exception.Message -cne 'Package manifest identity or file count mismatch.') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Setup accepted an invalid manifest identity.' }
}

try {
    & $validate -PackageDirectory $source -ContentOnly:$ContentOnly
    Test-UpgradeManifest $source
    Copy-Item -LiteralPath $source -Destination $fixture -Recurse
    $manifestPath = Join-Path $fixture 'package-manifest.json'
    $original = [IO.File]::ReadAllBytes($manifestPath)
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

    $manifest.PSObject.Properties.Remove('format')
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    & $validate -PackageDirectory $fixture -ContentOnly:$ContentOnly
    $rejected = $false
    try { Test-UpgradeManifest $fixture } catch {
        if ($_.Exception.Message -cne 'Invalid package manifest.') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Upgrade accepted a legacy schema-only manifest.' }

    $manifest = [Text.Encoding]::UTF8.GetString($original) | ConvertFrom-Json
    $manifest.PSObject.Properties.Remove('schemaVersion')
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Test-UpgradeManifest $fixture
    Assert-SetupRejected $fixture

    $manifest = [Text.Encoding]::UTF8.GetString($original) | ConvertFrom-Json
    $manifest.format = 2
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Assert-SetupRejected $fixture

    $manifest = [Text.Encoding]::UTF8.GetString($original) | ConvertFrom-Json
    $manifest.certificationStatus = 'certified'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Assert-SetupRejected $fixture

    [IO.File]::WriteAllBytes($manifestPath, $original)
    [IO.File]::AppendAllText((Join-Path $fixture 'README.md'), 'synthetic tamper')
    $rejected = $false
    try { Test-UpgradeManifest $fixture } catch {
        if ($_.Exception.Message -cne 'Package manifest path or checksum mismatch.') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Upgrade accepted a tampered file.' }
    $rejected = $false
    try { & $validate -PackageDirectory $fixture -ContentOnly | Out-Null } catch {
        if ($_.Exception.Message -cne 'Package manifest file digest mismatch.') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Setup accepted a tampered file.' }
    Write-Output 'PASS: dual manifest satisfies setup and upgrade checks; legacy schema-only remains supported by setup; invalid format, schema, certification and tampering are rejected.'
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
