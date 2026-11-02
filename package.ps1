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
$collector = Join-Path $output 'collector'
$null = New-Item -ItemType Directory -Path $collector
foreach ($name in @('Collect-SCOMSnapshot.ps1', 'collector.example.json', 'Get-SCOMCompatibilityReport.ps1', 'Test-SCOMPreflight.ps1', 'Get-SCOMMetricCompatibilityReport.ps1',
    'ScomSdkReader.psm1', 'ScomNormalization.psm1', 'ScomMetricProfile.psm1', 'ScomWindowsProjection.psm1',
    'Invoke-SCOMGenericGroupProfile.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Collector\$name") -Destination $collector
}
$server = Join-Path $output 'server'
foreach ($name in @('appsettings.example.json', 'appsettings.ReadModel.example.json', 'appsettings.Worker.example.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "ScomMcp\$name") -Destination $server
}
foreach ($name in @('README.md', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $output
}
$docs = Join-Path $output 'docs'
$null = New-Item -ItemType Directory -Path (Join-Path $docs 'images')
foreach ($name in @('indexed-deployment-runbook.md', 'deployment-issue-log.md', 'customer-evaluation.md',
    'enterprise-readiness-roadmap.md', 'os-lifecycle-assessment.md', 'targeted-live-verification.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\$name") -Destination $docs
}
foreach ($name in @('scom-reference-architecture.png', 'scom-reference-architecture.excalidraw')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\images\$name") -Destination (Join-Path $docs 'images')
}
Write-Output 'Package created. It includes the legacy collector and opt-in worker host/ingestion foundation, not a certified SCOM normalization profile.'
