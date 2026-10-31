$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Push-Location $PSScriptRoot
try {
    & (Join-Path $PSScriptRoot 'Collector\Validate-Collector.ps1')
    dotnet run --project '.\Validation\Validation.csproj' --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Validation failed.' }
}
finally { Pop-Location }
