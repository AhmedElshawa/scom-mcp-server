$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
if (-not ('SupervisedProcess' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'SupervisedProcess.cs')
}

function Test-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if ($Path -match '^[a-zA-Z]:\\') { return $true }
    if ($Path -match '^\\\\') { return $true }
    return $false
}

function New-RunId {
    '{0}-{1}' -f ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')), ([guid]::NewGuid().ToString('N'))
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Object
    )
    $Object | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Add-RunEvent {
    param(
        [Parameter(Mandatory = $true)][string]$EventsPath,
        [Parameter(Mandatory = $true)][string]$Message
    )
    $line = '{0} {1}' -f ([DateTimeOffset]::UtcNow.ToString('o')), $Message
    Add-Content -LiteralPath $EventsPath -Value $line -Encoding UTF8
}

function Test-CollectorInvocation {
    param([string]$Executable, [string[]]$Arguments)
    $name = [IO.Path]::GetFileNameWithoutExtension($Executable)
    if ($name -ieq 'Invoke-SCOMGenericGroupProfile') { return $true }
    if ($name -ieq 'dotnet') {
        return (@($Arguments | Where-Object { $_ -in @('--worker', '--worker-once') }).Count -gt 0)
    }
    return $false
}

function Test-CollectorProcessCommandLine {
    param([string]$Name, [string]$CommandLine)
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $false }
    if ($Name -notin @('dotnet.exe', 'powershell.exe', 'pwsh.exe')) { return $false }
    $argv = [SupervisedProcess]::SplitCommandLine($CommandLine)
    if ($Name -ieq 'dotnet.exe') {
        return (Test-CollectorInvocation -Executable 'dotnet' -Arguments @($argv | Select-Object -Skip 1))
    }
    for ($i = 1; $i -lt $argv.Length; $i++) {
        if ($argv[$i] -ieq '-File') {
            return ($i + 1 -lt $argv.Length -and
                (Test-CollectorInvocation -Executable $argv[$i + 1] -Arguments @()))
        }
        if ($argv[$i] -in @('-Command', '-c', '-EncodedCommand', '-enc', '-e')) {
            if ($i + 1 -ge $argv.Length) { return $false }
            if ($argv[$i] -in @('-EncodedCommand', '-enc', '-e')) {
                try { $code = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($argv[$i + 1])) }
                catch [FormatException] { throw 'Cannot inspect encoded PowerShell process command line.' }
            }
            else { $code = ($argv[($i + 1)..($argv.Length - 1)] -join ' ') }
            $tokens = $null; $parseErrors = $null
            $ast = [Management.Automation.Language.Parser]::ParseInput($code, [ref]$tokens, [ref]$parseErrors)
            # Inspect invocations, never string contents/comments or evaluated expressions.
            foreach ($command in $ast.FindAll({
                param($node) $node -is [Management.Automation.Language.CommandAst]
            }, $true)) {
                $commandName = $command.GetCommandName()
                if ($null -eq $commandName) { continue }
                $arguments = @($command.CommandElements | Select-Object -Skip 1 | ForEach-Object {
                    if ($_ -is [Management.Automation.Language.StringConstantExpressionAst]) { $_.Value }
                    elseif ($_ -is [Management.Automation.Language.CommandParameterAst]) { $_.Extent.Text }
                })
                if (Test-CollectorInvocation -Executable $commandName -Arguments $arguments) { return $true }
            }
            return $false
        }
        if (-not $argv[$i].StartsWith('-') -and $argv[$i] -like '*.ps1') {
            return (Test-CollectorInvocation -Executable $argv[$i] -Arguments @())
        }
    }
    return $false
}

function Get-CompetingCollectorProcesses {
    Get-CimInstance Win32_Process | Where-Object {
        Test-CollectorProcessCommandLine -Name $_.Name -CommandLine $_.CommandLine
    } | Where-Object {
        # CIM can briefly return a process already joined by the prior launch.
        $candidate = Get-Process -Id ([int]$_.ProcessId) -ErrorAction SilentlyContinue
        $live = $false
        if ($null -ne $candidate) {
            try { $live = -not $candidate.HasExited }
            finally { $candidate.Dispose() }
        }
        $live
    }
}

function Get-ProcessIdentitySnapshot {
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $proc) { return $null }

    $wmi = Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $ProcessId) -ErrorAction SilentlyContinue
    $commandLine = $null
    if ($null -ne $wmi) { $commandLine = $wmi.CommandLine }

    try {
        $startUtc = $proc.StartTime.ToUniversalTime()
        $name = $proc.ProcessName
    }
    catch {
        return $null
    }
    [pscustomobject]@{
        pid = $proc.Id
        name = $name
        startTimeUtc = $startUtc.ToString('o')
        startTimeFileTimeUtc = [string]$startUtc.ToFileTimeUtc()
        commandLine = $commandLine
    }
}

function Test-ProcessIdentity {
    param(
        [Parameter(Mandatory = $true)]$Identity
    )
    $current = Get-Process -Id ([int]$Identity.pid) -ErrorAction SilentlyContinue
    if ($null -eq $current) { return $false }
    try {
        $null = $current.Handle
        return (-not $current.HasExited -and
            [string]$current.StartTime.ToUniversalTime().ToFileTimeUtc() -eq [string]$Identity.startTimeFileTimeUtc -and
            $current.ProcessName -eq [string]$Identity.name)
    }
    finally { $current.Dispose() }
}

function Get-DescendantPids {
    param([Parameter(Mandatory = $true)][int]$ParentPid)

    $all = @(Get-CimInstance Win32_Process)
    $seen = @{}
    $desc = New-Object 'System.Collections.ArrayList'
    $queue = New-Object 'System.Collections.ArrayList'
    [void]$queue.Add($ParentPid)
    while ($queue.Count -gt 0) {
        $current = [int]$queue[0]
        $queue.RemoveAt(0)
        $children = @($all | Where-Object { $_.ParentProcessId -eq $current })
        foreach ($child in $children) {
            $childPid = [int]$child.ProcessId
            if (-not $seen.ContainsKey($childPid)) {
                $seen[$childPid] = $true
                [void]$desc.Add($childPid)
                [void]$queue.Add($childPid)
            }
        }
    }
    @($desc | Sort-Object)
}

function Stop-VerifiedProcessTree {
    param(
        [Parameter(Mandatory = $true)]$Identity,
        [switch]$SimulateStopFailure
    )

    $result = [ordered]@{
        stopAttempted = $true
        stalePidRefused = $false
        stopFailure = $false
        reason = ''
        descendantPids = @()
        failedPidStops = @()
        terminatedVerified = $false
    }

    # Pin a handle, revalidate creation identity, then kill through that handle.
    # The launcher owns the atomic job and joins its children on root exit.
    $target = $null
    try {
        $target = Get-Process -Id ([int]$Identity.pid) -ErrorAction Stop
        $null = $target.Handle
        if ([string]$target.StartTime.ToUniversalTime().ToFileTimeUtc() -ne [string]$Identity.startTimeFileTimeUtc -or
            $target.ProcessName -ne [string]$Identity.name) {
            $result.stalePidRefused = $true
            $result.reason = 'STALE_PID_OR_IDENTITY_MISMATCH'
            return [pscustomobject]$result
        }
        if ($SimulateStopFailure) {
            $result.stopFailure = $true
            $result.reason = 'SIMULATED_STOP_FAILURE'
            return [pscustomobject]$result
        }
        $target.Kill()
        $exited = $target.WaitForExit(5000)
    }
    catch {
        $result.stopFailure = $true
        $result.reason = 'PROCESS_STOP_FAILED'
        return [pscustomobject]$result
    }
    finally { if ($null -ne $target) { $target.Dispose() } }
    if ($exited) {
        $result.terminatedVerified = $true
        $result.reason = 'ROOT_TERMINATED_CHILD_JOIN_REQUIRES_LAUNCHER'
    }
    else {
        $result.stopFailure = $true
        $result.reason = 'PROCESS_STILL_RUNNING_AFTER_STOP'
    }
    [pscustomobject]$result
}

function Get-ChildCollectorIdentity {
    param(
        [Parameter(Mandatory = $true)]$WorkerIdentity,
        [Parameter(Mandatory = $true)][string]$ChildScriptPattern
    )
    if (-not (Test-ProcessIdentity -Identity $WorkerIdentity)) { return $null }
    $desc = @(Get-DescendantPids -ParentPid ([int]$WorkerIdentity.pid))
    if ($desc.Count -eq 0) { return $null }
    $children = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ProcessId -in $desc -and $_.Name -in @('powershell.exe', 'pwsh.exe') -and $_.CommandLine -match $ChildScriptPattern
    })
    if ($children.Count -eq 0) { return $null }
    Get-ProcessIdentitySnapshot -ProcessId ([int]$children[0].ProcessId)
}

function Get-OutcomeClassification {
    param(
        [int]$ExitCode,
        [string]$StdoutPath,
        [string]$StderrPath,
        [string]$MonitorStatus,
        [string]$MonitorReason
    )

    $published = $false
    if (Test-Path -LiteralPath $StdoutPath) {
        $published = Select-String -LiteralPath $StdoutPath -Pattern 'SCOM_WORKER_PUBLISHED' -SimpleMatch -Quiet
    }

    if ($MonitorStatus -eq 'stop_failed') {
        return [pscustomobject]@{
            workerExitStatus = if ($ExitCode -eq 0) { 'success' } else { 'nonzero' }
            publicationStatus = if ($published) { 'confirmed' } else { 'unknown' }
            outcome = 'UNKNOWN_STOP_FAILURE'
            reason = $MonitorReason
        }
    }

    if ($MonitorStatus -eq 'monitor_failed') {
        return [pscustomobject]@{
            workerExitStatus = if ($ExitCode -eq 0) { 'success' } else { 'nonzero' }
            publicationStatus = if ($published) { 'confirmed' } else { 'unknown' }
            outcome = 'UNKNOWN_MONITOR_FAILURE'
            reason = $MonitorReason
        }
    }

    if ($MonitorStatus -eq 'stopped') {
        return [pscustomobject]@{
            workerExitStatus = if ($ExitCode -eq 0) { 'success' } else { 'nonzero' }
            publicationStatus = if ($published) { 'confirmed' } else { 'unknown' }
            outcome = 'UNKNOWN_MONITOR_STOP_TRIGGERED'
            reason = $MonitorReason
        }
    }

    if ($ExitCode -ne 0) {
        return [pscustomobject]@{
            workerExitStatus = 'nonzero'
            publicationStatus = if ($published) { 'confirmed' } else { 'unknown' }
            outcome = 'WORKER_FAILED_NONZERO_EXIT'
            reason = 'NONZERO_EXIT'
        }
    }

    if ($published) {
        return [pscustomobject]@{
            workerExitStatus = 'success'
            publicationStatus = 'confirmed'
            outcome = 'WORKER_SUCCESS_CONFIRMED_PUBLICATION'
            reason = 'EXIT_0_AND_PUBLICATION_SIGNAL'
        }
    }

    [pscustomobject]@{
        workerExitStatus = 'success'
        publicationStatus = 'unknown'
        outcome = 'WORKER_SUCCESS_PUBLICATION_UNCONFIRMED'
        reason = 'EXIT_0_WITHOUT_PUBLICATION_SIGNAL'
    }
}

function Join-ProcessArgumentLine {
    param([Parameter(Mandatory = $true)][string[]]$ArgumentList)
    $parts = @()
    foreach ($arg in $ArgumentList) {
        if ($null -eq $arg) { continue }
        if ($arg -eq '' -or $arg -match '[\s"]') {
            $escaped = [regex]::Replace($arg, '(\\*)"', '$1$1\"')
            $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
            $parts += ('"' + $escaped + '"')
        }
        else {
            $parts += $arg
        }
    }
    $parts -join ' '
}
