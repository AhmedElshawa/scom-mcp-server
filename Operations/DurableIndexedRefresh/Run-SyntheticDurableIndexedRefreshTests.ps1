[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$scriptRoot = $PSScriptRoot
$files = @(Get-ChildItem -LiteralPath $scriptRoot -Filter '*.ps1' -File)
function Write-SyntheticRuntimeStatus {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)]$Status)
    [IO.File]::WriteAllText($Path, ($Status | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
}
foreach ($file in $files) {
    $tokens = $null
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile(
        $file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw "PowerShell parse failed for $($file.Name)." }
    if ($file.Name -ne 'Run-SyntheticDurableIndexedRefreshTests.ps1') {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        if ($text -match '(?i)-ExecutionPolicy\s+Bypass|Stop-Process\b|\.Kill\(') {
            throw "Unsafe execution or broad process-control pattern found in $($file.Name)."
        }
    }
}

. (Join-Path $scriptRoot 'DurableIndexedRefresh.Common.ps1')
$assertions = 0
$currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$credential = [PSCredential]::new($currentIdentity, (ConvertTo-SecureString 'synthetic-only' -AsPlainText -Force))
$resolvedIdentity = Resolve-RefreshTaskIdentity -Credential $credential
if ([string]::IsNullOrWhiteSpace($resolvedIdentity.Account) -or
    $resolvedIdentity.Sid.Value -in @('S-1-5-18', 'S-1-5-19', 'S-1-5-20')) {
    throw 'Explicit current test identity was not accepted as a non-service identity.'
}
$assertions++
try {
    $systemCredential = [PSCredential]::new('NT AUTHORITY\SYSTEM',
        (ConvertTo-SecureString 'synthetic-only' -AsPlainText -Force))
    Resolve-RefreshTaskIdentity -Credential $systemCredential
    throw 'SYSTEM identity was accepted.'
}
catch {
    if ($_.Exception.Message -notmatch 'explicit operator-supplied worker account') { throw }
    $assertions++
}
if ((Quote-RefreshArgument -Value 'C:\Synthetic Path\worker.json') -ne '"C:\Synthetic Path\worker.json"') {
    throw 'Windows argument quoting failed.'
}
$assertions++
try {
    Assert-RefreshTaskSelector -TaskName 'wrong\name' -TaskPath '\'
    throw 'Non-exact task name was accepted.'
}
catch {
    if ($_.Exception.Message -notmatch 'exact task name') { throw }
    $assertions++
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('durable-refresh-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
try {
    $dll = Join-Path $testRoot 'Synthetic Worker.dll'
    $config = Join-Path $testRoot 'Synthetic Worker.json'
    [IO.File]::WriteAllText($dll, 'synthetic')
    [IO.File]::WriteAllText($config, '{}')
    $aclPath = Join-Path $testRoot 'protected-state'
    $null = New-Item -ItemType Directory -Path $aclPath
    Set-RefreshStateDirectoryAcl -Path $aclPath -WorkerSid $resolvedIdentity.Sid
    $acl = Get-Acl -LiteralPath $aclPath
    $sids = @($acl.Access | ForEach-Object {
        if ($_.IdentityReference -is [System.Security.Principal.SecurityIdentifier]) {
            $_.IdentityReference.Value
        }
        else {
            ([System.Security.Principal.NTAccount]::new($_.IdentityReference.Value)).Translate(
                [System.Security.Principal.SecurityIdentifier]).Value
        }
    } | Sort-Object -Unique)
    if (-not $acl.AreAccessRulesProtected -or $sids.Count -ne 3 -or
        $sids -notcontains 'S-1-5-18' -or $sids -notcontains 'S-1-5-32-544' -or
        $sids -notcontains $resolvedIdentity.Sid.Value) {
        throw 'Runtime-state ACL is not restricted to SYSTEM, Administrators and the worker.'
    }
    $assertions++

    function Get-ScheduledTask {
        [CmdletBinding()]
        param([string]$TaskName, [string]$TaskPath)
        return $global:mockRefreshTask
    }
    function Get-ScheduledTaskInfo {
        [CmdletBinding()]
        param([string]$TaskName, [string]$TaskPath)
        return [pscustomobject]@{ LastTaskResult = 1 }
    }
    function Stop-ScheduledTask {
        [CmdletBinding()]
        param([string]$TaskName, [string]$TaskPath)
        $global:mockRefreshTask.State = 'Ready'
    }
    function Start-ScheduledTask {
        [CmdletBinding()]
        param([string]$TaskName, [string]$TaskPath)
        $global:mockRefreshTask.State = 'Running'
    }
    function Unregister-ScheduledTask {
        [CmdletBinding(SupportsShouldProcess = $true)]
        param([string]$TaskName, [string]$TaskPath)
        $global:mockRefreshTask = $null
        $global:mockRefreshUnregistered = $true
    }
    function New-ScheduledTaskAction {
        param([string]$Execute, [string]$Argument, [string]$WorkingDirectory)
        $global:capturedRefreshAction = [pscustomobject]@{ Execute = $Execute; Arguments = $Argument; WorkingDirectory = $WorkingDirectory }
        return $global:capturedRefreshAction
    }
    function New-ScheduledTaskTrigger { param([switch]$AtStartup) return [pscustomobject]@{ AtStartup = $AtStartup } }
    function New-ScheduledTaskSettingsSet {
        param([string]$MultipleInstances, [switch]$StartWhenAvailable, [TimeSpan]$ExecutionTimeLimit, [int]$RestartCount)
        $global:capturedRefreshSettings = [pscustomobject]@{
            MultipleInstances = $MultipleInstances; StartWhenAvailable = $StartWhenAvailable
            ExecutionTimeLimit = $ExecutionTimeLimit; RestartCount = $RestartCount
        }
        return $global:capturedRefreshSettings
    }
    function New-ScheduledTaskPrincipal {
        param([string]$UserId, [string]$LogonType, [string]$RunLevel)
        return [pscustomobject]@{ UserId = $UserId; LogonType = $LogonType; RunLevel = $RunLevel }
    }
    function New-ScheduledTask {
        param($Action, $Trigger, $Settings, $Principal, [string]$Description)
        $global:capturedRefreshDefinition = [pscustomobject]@{
            Action = $Action; Trigger = $Trigger; Settings = $Settings
            Principal = $Principal; Description = $Description
        }
        return $global:capturedRefreshDefinition
    }

    $register = Join-Path $scriptRoot 'Register-ScomWorkerTask.ps1'
    $global:mockRefreshTask = $null
    & $register -WorkerDllPath $dll -WorkerConfigPath $config `
        -StateDirectory (Join-Path $testRoot 'state') -Credential $credential `
        -TaskName 'Synthetic Refresh' -TaskPath '\' -WhatIf | Out-Null
    if ($global:capturedRefreshSettings.MultipleInstances -ne 'IgnoreNew' -or
        $global:capturedRefreshSettings.RestartCount -ne 0 -or
        -not $global:capturedRefreshSettings.StartWhenAvailable -or
        $global:capturedRefreshDefinition.Principal.UserId -ne $currentIdentity -or
        $global:capturedRefreshDefinition.Principal.LogonType -ne 'Password' -or
        $global:capturedRefreshDefinition.Principal.RunLevel -ne 'Limited' -or
        $global:capturedRefreshAction.Arguments -notmatch '^".+" --worker ".+" --runtime-state-dir ".+"$') {
        throw 'Task registration contract does not match the durable worker host policy.'
    }
    if (Test-Path -LiteralPath (Join-Path $testRoot 'state')) {
        throw 'WhatIf created runtime state or registered a runnable worker task.'
    }
    $assertions += 3

    $statusDirectory = Join-Path $testRoot 'owned-runtime-state'
    $null = New-Item -ItemType Directory -Path $statusDirectory
    $statusData = [ordered]@{
        state = 'running'; startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        lastAttemptUtc = [DateTimeOffset]::UtcNow.ToString('O')
        lastPublicationUtc = [DateTimeOffset]::UtcNow.ToString('O')
        lastOutcome = 'deferred'; consecutiveDeferredAttempts = 1
        failureCode = $null; processId = 1234; rearmRequired = $false
        lastFailureCode = $null; rearmedUtc = $null
    }
    $global:mockRefreshStatusData = $statusData
    $global:mockRefreshStatusPath = Join-Path $statusDirectory 'worker.status.json'
    $global:mockRefreshRequestPath = Join-Path $statusDirectory 'worker.stop.request'
    Write-SyntheticRuntimeStatus -Path $global:mockRefreshStatusPath -Status $statusData
    $workerAssembly = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..\..\ScomMcp\bin\Release\net8.0\ScomMcp.dll'))
    $dotnetExecutable = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
    $global:mockRefreshTask = [pscustomobject]@{
        Description = (Get-RefreshTaskMarker) + '; StateDirectory=' + $statusDirectory
        Actions = @([pscustomobject]@{
            Execute = $dotnetExecutable
            Arguments = '"' + $workerAssembly + '" --worker "' + $config + '" --runtime-state-dir "' + $statusDirectory + '"'
            WorkingDirectory = (Split-Path -Parent $workerAssembly)
        })
        Principal = [pscustomobject]@{ UserId = $currentIdentity; LogonType = 'Password' }
        Settings = [pscustomobject]@{ MultipleInstances = 'IgnoreNew'; RestartCount = 0 }
        State = 'Running'
    }
    $statusScript = Join-Path $scriptRoot 'Get-ScomWorkerTaskStatus.ps1'
    $status = & $statusScript -TaskName 'Synthetic Refresh' -TaskPath '\' -StateDirectory $statusDirectory
    if ($status.TaskState -ne 'Running' -or $status.RuntimeConsistency -ne 'consistent' -or
        $status.LastOutcome -ne 'deferred' -or $status.FreshnessAuthority -notmatch 'get_source_status' -or
        $status.RearmRequired) {
        throw 'Status command did not distinguish liveness, deferred outcome and data freshness.'
    }
    $assertions++
    try {
        & (Join-Path $scriptRoot 'Start-ScomWorkerTask.ps1') `
            -TaskName 'Synthetic Refresh' -TaskPath '\' -Confirm:$false
        throw 'Start accepted a duplicate running task.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'already running') { throw }
        $assertions++
    }
    function Get-ScheduledTask {
        [CmdletBinding()]
        param([string]$TaskName, [string]$TaskPath)
        if (Test-Path -LiteralPath $global:mockRefreshRequestPath) {
            $global:mockRefreshTask.State = 'Ready'
            $global:mockRefreshStatusData.state = 'stopped'
            $global:mockRefreshStatusData.failureCode = $null
            $global:mockRefreshStatusData.rearmRequired = $false
            $global:mockRefreshStatusData.processId = 0
            Write-SyntheticRuntimeStatus -Path $global:mockRefreshStatusPath -Status $global:mockRefreshStatusData
            Remove-Item -LiteralPath $global:mockRefreshRequestPath
        }
        return $global:mockRefreshTask
    }
    & (Join-Path $scriptRoot 'Stop-ScomWorkerTask.ps1') -TaskName 'Synthetic Refresh' -TaskPath '\' -Confirm:$false | Out-Null
    if ($global:mockRefreshTask.State -ne 'Ready') { throw 'Stop command did not target the exact owned task.' }
    $assertions++
    & (Join-Path $scriptRoot 'Start-ScomWorkerTask.ps1') -TaskName 'Synthetic Refresh' -TaskPath '\' -Confirm:$false | Out-Null
    if ($global:mockRefreshTask.State -ne 'Running') { throw 'Start command did not target the exact owned task.' }
    $assertions++
    $global:mockRefreshTask.State = 'Ready'
    $global:mockRefreshStatusData.state = 'stopped'
    $global:mockRefreshStatusData.failureCode = 'SYNTHETIC_TERMINAL_FAILURE'
    $global:mockRefreshStatusData.rearmRequired = $true
    $global:mockRefreshStatusData.lastFailureCode = 'SYNTHETIC_TERMINAL_FAILURE'
    Write-SyntheticRuntimeStatus -Path $global:mockRefreshStatusPath -Status $global:mockRefreshStatusData
    try {
        & (Join-Path $scriptRoot 'Start-ScomWorkerTask.ps1') `
            -TaskName 'Synthetic Refresh' -TaskPath '\' -Confirm:$false
        throw 'Normal start silently bypassed the persisted rearm gate.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'explicitly') { throw }
        $assertions++
    }
    $global:mockRefreshTask.State = 'Running'
    try {
        & (Join-Path $scriptRoot 'Rearm-ScomWorkerTask.ps1') `
            -TaskName 'Synthetic Refresh' -TaskPath '\' -AcknowledgeFailure -Confirm:$false
        throw 'Rearm was allowed while the task was running.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'running') { throw }
        $assertions++
    }
    $global:mockRefreshTask.State = 'Ready'
    & (Join-Path $scriptRoot 'Rearm-ScomWorkerTask.ps1') -TaskName 'Synthetic Refresh' `
        -TaskPath '\' -AcknowledgeFailure -WhatIf -Confirm:$false | Out-Null
    $assertions++
    $rearmResult = & (Join-Path $scriptRoot 'Rearm-ScomWorkerTask.ps1') -TaskName 'Synthetic Refresh' `
        -TaskPath '\' -AcknowledgeFailure -Confirm:$false
    $rearmedRuntime = Read-RefreshRuntimeStatus -StateDirectory $statusDirectory
    if (($rearmResult | Out-String) -notmatch 'explicitly rearmed' -or $rearmedRuntime.NeedsRearm -or
        $rearmedRuntime.lastFailureCode -ne 'SYNTHETIC_TERMINAL_FAILURE') {
        throw 'Explicit task rearm did not invoke the worker control command or preserve failure evidence.'
    }
    $assertions++
    $savedStatus = Get-Content -LiteralPath $global:mockRefreshStatusPath -Raw
    try {
        $global:mockRefreshStatusData | Out-Null
        $global:mockRefreshStatusPath | Out-Null
        $invalid = $global:mockRefreshStatusData
        $invalid | Add-Member -NotePropertyName unknownField -NotePropertyValue 'unknown'
        Write-SyntheticRuntimeStatus -Path $global:mockRefreshStatusPath -Status $invalid
        try {
            & (Join-Path $scriptRoot 'Get-ScomWorkerTaskStatus.ps1') `
                -TaskName 'Synthetic Refresh' -TaskPath '\'
            throw 'Unknown runtime status properties were accepted.'
        }
        catch {
            if ($_.Exception.Message -notmatch 'unknown or incomplete') { throw }
            $assertions++
        }
    }
    finally { [IO.File]::WriteAllText($global:mockRefreshStatusPath, $savedStatus, [Text.UTF8Encoding]::new($false)) }
    & (Join-Path $scriptRoot 'Unregister-ScomWorkerTask.ps1') -TaskName 'Synthetic Refresh' -TaskPath '\' -Confirm:$false | Out-Null
    if (-not $global:mockRefreshUnregistered -or
        -not (Test-Path -LiteralPath (Join-Path $statusDirectory 'worker.status.json'))) {
        throw 'Unregister removed runtime data or failed to target the exact owned task.'
    }
    $assertions++
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}

Write-Output "PASS: $assertions durable-refresh PowerShell assertions; syntax, explicit identity, task contract, no restart storm and WhatIf isolation."
