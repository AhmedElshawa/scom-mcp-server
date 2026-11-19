[CmdletBinding()]
param([string]$GuidePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
if ([string]::IsNullOrWhiteSpace($GuidePath)) {
    $GuidePath = Join-Path $PSScriptRoot '..\docs\easy-indexed-upgrade.md'
}
$guide = Get-Content -LiteralPath $GuidePath -Raw
$blocks = [regex]::Matches($guide, '(?s)```powershell\r?\n(.*?)```')
$count = 0
foreach ($block in $blocks) {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($block.Groups[1].Value, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Upgrade guide PowerShell block has syntax errors.' }
    $count++
}
$operatorGuidePath = Join-Path $PSScriptRoot '..\Operations\DurableIndexedRefresh\README.md'
$operatorGuide = Get-Content -LiteralPath $operatorGuidePath -Raw
foreach ($block in [regex]::Matches($operatorGuide, '(?s)```powershell\r?\n(.*?)```')) {
    $tokens = $null
    $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseInput(
        $block.Groups[1].Value, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Durable-refresh operator guide PowerShell block has syntax errors.' }
    $count++
}
foreach ($required in @(
    'was not independently established',
    'been live-tested',
    'rearmRequired=false',
    '--worker-stop-request'
)) {
    if (-not $operatorGuide.Contains($required)) { throw 'Durable-refresh reboot evidence or native control guidance is missing.' }
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
$taskGuard = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Assert-NoActiveWorkerTask'
}, $true)
if ($null -eq $taskGuard) { throw 'Missing existing-worker task guard.' }
. ([scriptblock]::Create($taskGuard.Extent.Text))
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
function New-SyntheticTask([string]$State, [bool]$Enabled, [object[]]$Actions) {
    return [pscustomobject]@{
        State = $State
        Settings = [pscustomobject]@{ Enabled = $Enabled }
        Actions = $Actions
    }
}
$targetDll = 'C:\Existing\IIS\ScomMcp.dll'
$nonExec = [pscustomobject]@{ ClassId = '{synthetic-com-handler}' }
Assert-NoActiveWorkerTask -TargetDll $targetDll -Tasks @(
    (New-SyntheticTask 'Running' $true @($nonExec))
)
$count++
$otherExec = [pscustomobject]@{
    Execute = 'C:\Windows\System32\cmd.exe'
    Arguments = '/c echo unrelated task'
}
Assert-NoActiveWorkerTask -TargetDll $targetDll -Tasks @(
    (New-SyntheticTask 'Running' $true @($otherExec))
)
$count++
$inactiveWorker = [pscustomobject]@{
    Execute = 'C:\Program Files\dotnet\dotnet.exe'
    Arguments = '"C:\Existing\IIS\ScomMcp.dll" --worker "C:\private\worker.json" --runtime-state-dir "C:\state"'
}
Assert-NoActiveWorkerTask -TargetDll $targetDll -Tasks @(
    (New-SyntheticTask 'Ready' $false @($inactiveWorker))
)
$count++
$activeWorkerRejected = $false
try {
    Assert-NoActiveWorkerTask -TargetDll $targetDll -Tasks @(
        (New-SyntheticTask 'Ready' $true @($nonExec, $inactiveWorker))
    )
}
catch {
    if ($_.Exception.Message -notmatch 'stopped and disabled') { throw }
    $activeWorkerRejected = $true
}
if (-not $activeWorkerRejected) { throw 'Active matching Exec task was not blocked.' }
$count++
foreach ($required in @(
    'Stop-WebAppPool -Name $pool',
    "if (`$relative -eq 'web.config' -or `$file.Name -like 'appsettings*.json') { continue }",
    'Copy-Item -LiteralPath $file.FullName -Destination $destination -Force',
    'Get-FileHash -LiteralPath $destination -Algorithm SHA256',
    'SetAccessRuleProtection($true, $false)',
    "Get-CimInstance Win32_Process -ErrorAction Stop",
    'Get-ScheduledTask -ErrorAction Stop',
    '$null -eq $argumentsProperty',
    "Assert-NoActiveWorkerTask -Tasks @(Get-ScheduledTask -ErrorAction Stop)",
    'Get-CimInstance Win32_Process -ErrorAction Stop',
    'Get-WebApplication -Site $site.Name',
    'Start-WebAppPool -Name $pool'
)) {
    if (-not $upgrade.Contains($required)) { throw 'Upgrade block lost a required scope/preservation guard.' }
    $count++
}
foreach ($required in @(
    '$task.Principal.UserId -ine $workerAccount',
    '$updatedUtc -lt $bootUtc',
    '& $dotnet $dll --worker-rearm $runtime'
)) {
    if (-not $guide.Contains($required)) { throw 'Upgrade recovery lost its exact-task or stale-status rearm guard.' }
    $count++
}
if ($upgrade -match '(?i)ExecutionPolicy|Unblock-File|Stop-Process|Remove-Item|--worker-rearm') {
    throw 'Happy-path upgrade must not bypass policy, delete data, force processes or implicitly rearm.'
}
$count++
Write-Output "PASS: $count upgrade/operator-guide assertions; PowerShell blocks parse, path preflight executes and preservation/reboot guards remain present. No IIS/task/deployment action ran."
