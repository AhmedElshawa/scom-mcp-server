[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw 'Package directory does not exist.'
}
$required = @(
    'README.md', 'LICENSE',
    'server\ScomMcp.dll', 'server\ScomMcp.deps.json', 'server\ScomMcp.runtimeconfig.json',
    'server\web.config', 'server\appsettings.example.json',
    'server\appsettings.ReadModel.example.json', 'server\appsettings.Worker.example.json',
    'collector\Collect-SCOMSnapshot.ps1', 'collector\collector.example.json',
    'collector\Get-SCOMCompatibilityReport.ps1', 'collector\Test-SCOMPreflight.ps1',
    'collector\Get-SCOMMetricCompatibilityReport.ps1',
    'collector\Invoke-SCOMGenericGroupProfile.ps1', 'collector\ScomSdkReader.psm1',
    'collector\ScomNormalization.psm1', 'collector\ScomMetricProfile.psm1',
    'collector\ScomWindowsProjection.psm1', 'docs\indexed-deployment-runbook.md',
    'docs\deployment-issue-log.md', 'docs\customer-evaluation.md',
    'docs\enterprise-readiness-roadmap.md',
    'docs\os-lifecycle-assessment.md',
    'docs\targeted-live-verification.md',
    'docs\guided-setup.md', 'setup\ScomSetup.psm1', 'setup\ScomSetup.Windows.psm1', 'setup\ScomEntra.psm1',
    'setup\Start-ScomSetup.ps1', 'setup\Start-ScomWorkerSetup.ps1', 'setup\Provision-ScomEntra.ps1',
    'setup\Initialize-ScomSetupState.ps1',
    'docs\images\scom-reference-architecture.png', 'docs\images\scom-reference-architecture.excalidraw'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
        throw "Required package file missing: $relative"
    }
}
$files = @(Get-ChildItem -LiteralPath $root -File -Recurse)
foreach ($file in $files) {
    if ($file.Name -match '^(appsettings\.json|appsettings\.(Production|Development)\.json|worker\.json)$' -or
        $file.Name -match '\.(db|db-journal|db-wal|db-shm|pfx|p12|key|lock|bak)$' -or
        $file.Name -match '^(snapshot|collector-status)\.json$' -or
        $file.Name -match '\.private\.') {
        throw 'Package contains an unexpected runtime configuration, credential or state file.'
    }
}
foreach ($file in $files | Where-Object Extension -eq '.json') {
    $null = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -ErrorAction Stop
}
foreach ($file in $files | Where-Object Extension -eq '.md') {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($match in [regex]::Matches($text, '\]\(([^)]+)\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^(https?://|#)') { continue }
        $relative = ($target -split '#', 2)[0].Replace('/', [IO.Path]::DirectorySeparatorChar)
        $resolved = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName $relative))
        if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw 'Package documentation contains a broken or outside-package file link.'
        }
    }
}
Write-Output "Package content checks passed: $($required.Count) required files, JSON parsing, documentation links and runtime-state exclusions. No application or source connection started."
