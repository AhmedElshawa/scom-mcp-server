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
    'README.md', 'LICENSE', 'package-manifest.json',
    'ScomSetup.exe', 'ScomSetup.dll', 'ScomSetup.runtimeconfig.json', 'ScomSetup.deps.json',
    'coreclr.dll', 'hostfxr.dll', 'System.Windows.Forms.dll',
    'server\ScomMcp.dll', 'server\ScomMcp.deps.json', 'server\ScomMcp.runtimeconfig.json',
    'server\web.config', 'server\appsettings.example.json',
    'server\appsettings.ReadModel.example.json', 'server\appsettings.Worker.example.json',
    'Operations\DurableIndexedRefresh\DurableIndexedRefresh.Common.ps1',
    'Operations\DurableIndexedRefresh\Register-ScomWorkerTask.ps1',
    'Operations\DurableIndexedRefresh\Get-ScomWorkerTaskStatus.ps1',
    'Operations\DurableIndexedRefresh\Start-ScomWorkerTask.ps1',
    'Operations\DurableIndexedRefresh\Stop-ScomWorkerTask.ps1',
    'Operations\DurableIndexedRefresh\Rearm-ScomWorkerTask.ps1',
    'Operations\DurableIndexedRefresh\Unregister-ScomWorkerTask.ps1',
    'Operations\DurableIndexedRefresh\README.md',
    'collector\Collect-SCOMSnapshot.ps1', 'collector\collector.example.json',
    'collector\Get-SCOMCompatibilityReport.ps1', 'collector\Test-SCOMPreflight.ps1',
    'collector\Get-SCOMMetricCompatibilityReport.ps1',
    'collector\Invoke-SCOMGenericGroupProfile.ps1', 'collector\ScomSdkReader.psm1',
    'collector\ScomNormalization.psm1', 'collector\ScomMetricProfile.psm1',
    'collector\ScomWindowsProjection.psm1', 'collector\ScomOperationalLogging.psm1', 'docs\indexed-deployment-runbook.md',
    'docs\deployment-issue-log.md', 'docs\customer-evaluation.md',
    'docs\enterprise-readiness-roadmap.md',
    'docs\os-lifecycle-assessment.md',
    'docs\targeted-live-verification.md',
    'docs\guided-setup.md', 'setup\ScomSetup.psm1', 'setup\ScomSetup.Windows.psm1', 'setup\ScomEntra.psm1',
    'setup\Start-ScomSetup.ps1', 'setup\Start-ScomWorkerSetup.ps1', 'setup\Provision-ScomEntra.ps1',
    'setup\Initialize-ScomSetupState.ps1',
    'setup\Install-ScomPrerequisites.ps1', 'setup\ScomPrerequisiteInstall.psm1',
    'setup\Invoke-ScomSetupGui.ps1', 'setup\ScomSetup.Gui.psm1',
    'docs\images\scom-reference-architecture.png', 'docs\images\scom-reference-architecture.excalidraw'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
        throw "Required package file missing: $relative"
    }
}
$files = @(Get-ChildItem -LiteralPath $root -File -Recurse)
$runtimePrivateAssemblies=@('System.Private.CoreLib.dll','System.Private.DataContractSerialization.dll',
    'System.Private.Uri.dll','System.Private.Xml.dll','System.Private.Xml.Linq.dll')
foreach ($file in $files) {
    if ($file.Name -match '^(appsettings\.json|appsettings\.(Production|Development)\.json|worker\.json)$' -or
        $file.Name -match '\.(db|db-journal|db-wal|db-shm|pfx|p12|key|lock|bak)$' -or
        $file.Name -match '^(snapshot|collector-status)\.json$' -or
        ($file.Name -match '\.private\.' -and
            -not ($file.DirectoryName -ieq $root -and $file.Name -cin $runtimePrivateAssemblies))) {
        throw 'Package contains an unexpected runtime configuration, credential or state file.'
    }
}
foreach ($file in $files | Where-Object Extension -eq '.json') {
    $null = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -ErrorAction Stop
}
$manifest = Get-Content -LiteralPath (Join-Path $root 'package-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.sourceCommit -notmatch '^[a-f0-9]{40}$' -or
    $manifest.certificationStatus -cne 'notCertified' -or $manifest.files.Count -ne ($files.Count - 1)) {
    throw 'Package manifest identity or file count mismatch.'
}
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    if (-not $seen.Add($entry.path) -or $entry.path -match '(^|\\)\.\.?($|\\)' -or [IO.Path]::IsPathRooted($entry.path)) {
        throw 'Package manifest contains duplicate or unsafe relative paths.'
    }
    $target = [IO.Path]::GetFullPath((Join-Path $root $entry.path))
    if (-not $target.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase) -or
        $entry.sha256 -cne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
        throw 'Package manifest file digest mismatch.'
    }
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
