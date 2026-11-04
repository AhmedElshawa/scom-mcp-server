[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\package')
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw 'Package output must be a new directory to prevent mixing builds or local configuration.'
}
$null = New-Item -ItemType Directory -Path $output
dotnet publish (Join-Path $PSScriptRoot 'ScomMcp\ScomMcp.csproj') --configuration Release `
    --no-restore --no-self-contained --output (Join-Path $output 'server') --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Server publication failed. The incomplete output must not be deployed.' }
dotnet publish (Join-Path $PSScriptRoot 'SetupGui\ScomSetup.csproj') --configuration Release `
    --no-restore --runtime win-x64 --self-contained true --output $output --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Native self-contained setup publication failed. Do not deploy the incomplete package.' }
$collector = Join-Path $output 'collector'
$null = New-Item -ItemType Directory -Path $collector
foreach ($name in @('Collect-SCOMSnapshot.ps1', 'collector.example.json', 'Get-SCOMCompatibilityReport.ps1', 'Test-SCOMPreflight.ps1', 'Get-SCOMMetricCompatibilityReport.ps1',
    'ScomSdkReader.psm1', 'ScomNormalization.psm1', 'ScomMetricProfile.psm1', 'ScomWindowsProjection.psm1', 'ScomOperationalLogging.psm1',
    'Invoke-SCOMGenericGroupProfile.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Collector\$name") -Destination $collector
}
$server = Join-Path $output 'server'
$setup = Join-Path $output 'setup'
$null = New-Item -ItemType Directory -Path $setup
foreach ($name in @('ScomSetup.psm1', 'ScomSetup.Windows.psm1', 'ScomEntra.psm1', 'Start-ScomSetup.ps1',
    'Start-ScomWorkerSetup.ps1', 'Provision-ScomEntra.ps1', 'Initialize-ScomSetupState.ps1',
    'Install-ScomPrerequisites.ps1', 'ScomPrerequisiteInstall.psm1','ScomSetup.Gui.psm1','Invoke-ScomSetupGui.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Setup\$name") -Destination $setup
}
foreach ($name in @('appsettings.example.json', 'appsettings.ReadModel.example.json', 'appsettings.Worker.example.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "ScomMcp\$name") -Destination $server
}
$operations = Join-Path $output 'Operations\DurableIndexedRefresh'
$null = New-Item -ItemType Directory -Path $operations -Force
foreach ($name in @('DurableIndexedRefresh.Common.ps1', 'Register-ScomWorkerTask.ps1',
    'Get-ScomWorkerTaskStatus.ps1', 'Start-ScomWorkerTask.ps1', 'Stop-ScomWorkerTask.ps1',
    'Rearm-ScomWorkerTask.ps1', 'Unregister-ScomWorkerTask.ps1', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Operations\DurableIndexedRefresh\$name") -Destination $operations
}
foreach ($name in @('README.md', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $output
}
$docs = Join-Path $output 'docs'
$null = New-Item -ItemType Directory -Path (Join-Path $docs 'images')
foreach ($name in @('indexed-deployment-runbook.md', 'deployment-issue-log.md', 'customer-evaluation.md',
    'enterprise-readiness-roadmap.md', 'os-lifecycle-assessment.md', 'targeted-live-verification.md', 'guided-setup.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\$name") -Destination $docs
}
foreach ($name in @('scom-reference-architecture.png', 'scom-reference-architecture.excalidraw')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\images\$name") -Destination (Join-Path $docs 'images')
}
$commit = & git -C $PSScriptRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$') { throw 'Package source identity unavailable.' }
$manifestFiles = @(Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path=$_.FullName.Substring($output.TrimEnd('\').Length + 1)
        sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
$manifest = [ordered]@{schemaVersion=1; sourceCommit=$commit; certificationStatus='notCertified'; files=$manifestFiles}
[IO.File]::WriteAllText((Join-Path $output 'package-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Output 'Package created. It includes the legacy collector and opt-in worker host/ingestion foundation, not a certified SCOM normalization profile.'
