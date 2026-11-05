[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$guide = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\docs\easy-indexed-upgrade.md') -Raw
$blocks = [regex]::Matches($guide, '(?s)```powershell\r?\n(.*?)```')
$count = 0
foreach ($block in $blocks) {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($block.Groups[1].Value, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Upgrade guide PowerShell block has syntax errors.' }
    $count++
}
$upgrade = $blocks[0].Groups[1].Value
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($upgrade, [ref]$tokens, [ref]$errors)
$localDirectory = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'LocalDirectory'
}, $true)
if ($null -eq $localDirectory) { throw 'Missing local-path preflight.' }
. ([scriptblock]::Create($localDirectory.Extent.Text))
foreach ($invalid in @('relative', '\\server\share', 'C:\', 'C:\missing-synthetic-path-782a38')) {
    $rejected = $false
    try { $null = LocalDirectory $invalid }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Upgrade preflight accepted an unsafe path.' }
    $count++
}
$temp = [IO.Path]::GetTempPath().TrimEnd('\')
if ((LocalDirectory $temp) -ne $temp) { throw 'Upgrade preflight rejected the existing local temporary directory.' }
$count++
foreach ($required in @(
    'Stop-WebAppPool -Name $pool',
    "if (`$relative -eq 'web.config' -or `$file.Name -like 'appsettings*.json') { continue }",
    'Copy-Item -LiteralPath $file.FullName -Destination $destination -Force',
    'Get-FileHash -LiteralPath $destination -Algorithm SHA256',
    'SetAccessRuleProtection($true, $false)',
    "Get-CimInstance Win32_Process -ErrorAction Stop",
    'Get-ScheduledTask -ErrorAction Stop',
    'Get-WebApplication -Site $site.Name',
    'Start-WebAppPool -Name $pool'
)) {
    if (-not $upgrade.Contains($required)) { throw 'Upgrade block lost a required scope/preservation guard.' }
    $count++
}
if ($upgrade -match '(?i)ExecutionPolicy|Unblock-File|Stop-Process|Remove-Item|--worker-rearm') {
    throw 'Happy-path upgrade must not bypass policy, delete data, force processes or implicitly rearm.'
}
$count++
Write-Output "PASS: $count upgrade-guide assertions; actual pasted blocks parse, path preflight executes and preservation guards remain present. No IIS/task/deployment action ran."
