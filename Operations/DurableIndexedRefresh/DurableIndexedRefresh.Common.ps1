$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

function Get-RefreshTaskMarker { return 'SCOM-MCP-DURABLE-INDEXED-REFRESH-V1' }

function Resolve-RefreshLocalPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$MustExist,
        [switch]$Directory
    )
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains('"') -or
        $Path.StartsWith('\\', [StringComparison]::Ordinal) -or
        -not [IO.Path]::IsPathRooted($Path)) {
        throw 'A fully qualified local Windows path without quotes is required.'
    }
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath -match '^[A-Za-z]:\\') {
        $root = [IO.Path]::GetPathRoot($fullPath)
        if ([IO.DriveInfo]::new($root).DriveType -eq [IO.DriveType]::Network) {
            throw 'Durable refresh state and executable paths must not use a network drive.'
        }
    }
    elseif ($fullPath -notmatch '^\\\\\?\\Volume\{[0-9A-Fa-f-]+\}\\') {
        throw 'A fully qualified local Windows path is required.'
    }
    $cursor = if ($Directory -or -not $MustExist) { $fullPath } else { Split-Path -Parent $fullPath }
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Durable refresh paths must not traverse reparse points.'
            }
        }
        $parent = Split-Path -Parent $cursor
        if (-not $parent -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
    if ($MustExist) {
        $pathType = if ($Directory) { 'Container' } else { 'Leaf' }
        if (-not (Test-Path -LiteralPath $fullPath -PathType $pathType)) {
            throw 'A required durable refresh path does not exist.'
        }
        $requiredItem = Get-Item -LiteralPath $fullPath -Force
        if (($requiredItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Durable refresh paths must not be reparse points.'
        }
    }
    return $fullPath
}

function Resolve-RefreshTaskIdentity {
    param([Parameter(Mandatory = $true)][PSCredential]$Credential)
    $account = $Credential.UserName.Trim()
    if ([string]::IsNullOrWhiteSpace($account) -or
        $account -match '^(NT AUTHORITY\\)?(SYSTEM|LOCAL SERVICE|NETWORK SERVICE)$') {
        throw 'Use an explicit operator-supplied worker account; built-in service identities are not accepted.'
    }
    $sid = ([System.Security.Principal.NTAccount]::new($account)).Translate(
        [System.Security.Principal.SecurityIdentifier])
    if ($sid.Value -in @('S-1-5-18', 'S-1-5-19', 'S-1-5-20')) {
        throw 'Use an explicit operator-supplied worker account; built-in service identities are not accepted.'
    }
    if ([string]::IsNullOrEmpty($Credential.GetNetworkCredential().Password)) {
        throw 'A task logon credential is required; no password is supplied or stored by this script.'
    }
    return [pscustomobject]@{ Account = $account; Sid = $sid }
}

function Assert-RefreshTaskSelector {
    param([string]$TaskName, [string]$TaskPath)
    if ([string]::IsNullOrWhiteSpace($TaskName) -or $TaskName.Contains('\') -or
        [string]::IsNullOrWhiteSpace($TaskPath) -or
        -not $TaskPath.StartsWith('\', [StringComparison]::Ordinal) -or
        -not $TaskPath.EndsWith('\', [StringComparison]::Ordinal)) {
        throw 'Supply one exact task name and a task folder beginning and ending with backslash.'
    }
}

function Get-OwnedRefreshTask {
    param([Parameter(Mandatory = $true)][string]$TaskName, [Parameter(Mandatory = $true)][string]$TaskPath)
    Assert-RefreshTaskSelector -TaskName $TaskName -TaskPath $TaskPath
    $task = Get-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -ErrorAction Stop
    if ($task.Description -notlike "$(Get-RefreshTaskMarker); StateDirectory=*") {
        throw 'The selected task is not marked as an owned durable indexed-refresh task.'
    }
    if (@($task.Actions).Count -ne 1 -or
        $task.Principal.UserId -match '^(NT AUTHORITY\\)?(SYSTEM|LOCAL SERVICE|NETWORK SERVICE)$' -or
        [string]$task.Principal.LogonType -ne 'Password' -or
        [string]$task.Settings.MultipleInstances -ne 'IgnoreNew' -or
        [int]$task.Settings.RestartCount -ne 0) {
        throw 'The selected task no longer matches the durable indexed-refresh task contract.'
    }
    $action = Get-RefreshTaskActionInfo -Task $task
    if (-not [string]::Equals($action.StateDirectory,
        (Get-RefreshStateDirectoryFromDescription -Task $task),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The task action runtime-state directory does not match its ownership marker.'
    }
    return $task
}

function Get-RefreshStateDirectoryFromDescription {
    param([Parameter(Mandatory = $true)]$Task, [switch]$RequireExisting)
    $stored = [regex]::Match($Task.Description, '; StateDirectory=(.+)$')
    if (-not $stored.Success) { throw 'The task does not contain its protected runtime-state location.' }
    if ($RequireExisting) {
        return Resolve-RefreshLocalPath -Path $stored.Groups[1].Value -Directory -MustExist
    }
    return Resolve-RefreshLocalPath -Path $stored.Groups[1].Value -Directory
}

function Get-RefreshTaskActionInfo {
    param([Parameter(Mandatory = $true)]$Task, [switch]$RequireFiles)
    $match = [regex]::Match([string]$Task.Actions[0].Arguments,
        '^"(?<dll>[^"]+)" --worker "(?<config>[^"]+)" --runtime-state-dir "(?<state>[^"]+)"$')
    if (-not $match.Success) {
        throw 'The task action is not the exact long-running worker command.'
    }
    if ($RequireFiles) {
        $executable = Resolve-RefreshLocalPath -Path ([string]$Task.Actions[0].Execute) -MustExist
        $dll = Resolve-RefreshLocalPath -Path $match.Groups['dll'].Value -MustExist
        $configuration = Resolve-RefreshLocalPath -Path $match.Groups['config'].Value -MustExist
        $state = Resolve-RefreshLocalPath -Path $match.Groups['state'].Value -Directory -MustExist
    }
    else {
        $executable = Resolve-RefreshLocalPath -Path ([string]$Task.Actions[0].Execute)
        $dll = Resolve-RefreshLocalPath -Path $match.Groups['dll'].Value
        $configuration = Resolve-RefreshLocalPath -Path $match.Groups['config'].Value
        $state = Resolve-RefreshLocalPath -Path $match.Groups['state'].Value -Directory
    }
    if ([IO.Path]::GetFileName($executable) -ine 'dotnet.exe' -or
        -not [string]::Equals((Split-Path -Parent $dll), [string]$Task.Actions[0].WorkingDirectory,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The task executable or working directory no longer matches the packaged worker contract.'
    }
    return [pscustomobject]@{
        Executable = $executable
        DllPath = $dll
        ConfigurationPath = $configuration
        StateDirectory = $state
    }
}

function Get-RefreshStateDirectory {
    param(
        [Parameter(Mandatory = $true)]$Task,
        [string]$StateDirectory
    )
    $path = Get-RefreshStateDirectoryFromDescription -Task $Task -RequireExisting
    $action = Get-RefreshTaskActionInfo -Task $Task
    if (-not [string]::Equals($path, $action.StateDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The task action runtime-state directory does not match its ownership marker.'
    }
    if ($StateDirectory) {
        $requested = Resolve-RefreshLocalPath -Path $StateDirectory -Directory -MustExist
        if (-not [string]::Equals($path, $requested, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The requested runtime-state directory does not match the owned task.'
        }
    }
    return $path
}

function Read-RefreshRuntimeStatus {
    param(
        [Parameter(Mandatory = $true)][string]$StateDirectory,
        [switch]$AllowMissing,
        [switch]$TaskRunning
    )
    $statusPath = Join-Path $StateDirectory 'worker.status.json'
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) {
        if ($AllowMissing) { return $null }
        throw 'Worker runtime status is missing; rearm is refused and operator inspection is required.'
    }
    $item = Get-Item -LiteralPath $statusPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -eq 0 -or $item.Length -gt 8192) {
        throw 'Worker status file is invalid or exceeds its byte budget; inspect protected state.'
    }
    $runtime = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    $expected = @('state', 'startedUtc', 'updatedUtc', 'lastAttemptUtc', 'lastPublicationUtc',
        'lastOutcome', 'consecutiveDeferredAttempts', 'failureCode', 'processId', 'rearmRequired',
        'lastFailureCode', 'rearmedUtc')
    $names = @($runtime.PSObject.Properties | ForEach-Object { $_.Name })
    if ($names.Count -ne $expected.Count -or
        @($names | Where-Object { $_ -notin $expected }).Count -ne 0 -or
        @($expected | Where-Object { $_ -notin $names }).Count -ne 0) {
        throw 'Worker status properties are unknown or incomplete; inspect protected state.'
    }
    $parsedTimestamp = [DateTimeOffset]::MinValue
    foreach ($timestamp in @('startedUtc', 'updatedUtc')) {
        if (-not [DateTimeOffset]::TryParse([string]$runtime.$timestamp,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsedTimestamp)) {
            throw 'Worker status timestamps are malformed; inspect protected state.'
        }
    }
    foreach ($timestamp in @('lastAttemptUtc', 'lastPublicationUtc', 'rearmedUtc')) {
        if ($null -ne $runtime.$timestamp -and -not [DateTimeOffset]::TryParse(
            [string]$runtime.$timestamp, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsedTimestamp)) {
            throw 'Worker status timestamps are malformed; inspect protected state.'
        }
    }
    $failureCodePresent = -not [string]::IsNullOrEmpty([string]$runtime.failureCode)
    $stoppedGateMismatch = [bool]$runtime.rearmRequired -ne $failureCodePresent
    $stoppedFailureMismatch = $runtime.rearmRequired -and
        $runtime.lastFailureCode -ne $runtime.failureCode
    $invalidStatus = $runtime.state -notin @('running', 'stopped') -or
        $runtime.lastOutcome -notin @($null, 'published', 'deferred') -or
        $runtime.consecutiveDeferredAttempts -lt 0 -or $runtime.processId -lt 0 -or
        $runtime.rearmRequired -isnot [bool] -or
        ($runtime.failureCode -and $runtime.failureCode -notmatch '^[A-Za-z0-9_]{1,80}$') -or
        ($runtime.lastFailureCode -and $runtime.lastFailureCode -notmatch '^[A-Za-z0-9_]{1,80}$') -or
        ($runtime.rearmRequired -and $runtime.state -ne 'stopped') -or
        ($runtime.state -eq 'running' -and ($runtime.processId -eq 0 -or $runtime.failureCode)) -or
        ($runtime.state -eq 'stopped' -and ($runtime.processId -ne 0 -or
            $stoppedGateMismatch -or $stoppedFailureMismatch))
    if ($invalidStatus) {
        throw 'Worker status is inconsistent; inspect protected state before starting or rearming.'
    }
    $runtime | Add-Member -NotePropertyName NeedsRearm -NotePropertyValue (
        [bool]($runtime.rearmRequired -or ($runtime.state -eq 'running' -and -not $TaskRunning) -or
            ($runtime.failureCode -and $runtime.failureCode -ne 'WORKER_CANCELLED')))
    return $runtime
}

function Invoke-RefreshWorkerControl {
    param(
        [Parameter(Mandatory = $true)]$Task,
        [Parameter(Mandatory = $true)][ValidateSet('stop-request', 'rearm')][string]$Operation,
        [Parameter(Mandatory = $true)][string]$StateDirectory
    )
    $action = Get-RefreshTaskActionInfo -Task $Task -RequireFiles
    & $action.Executable $action.DllPath "--worker-$Operation" $StateDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Worker control operation '$Operation' was refused; inspect the exact task and protected runtime state."
    }
}

function Set-RefreshStateDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)]$WorkerSid)
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRuleSpecific($rule) }
    $inherit = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.AccessControl.FileSystemRights]::FullControl, $inherit,
        [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'),
        [Security.AccessControl.FileSystemRights]::FullControl, $inherit,
        [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $WorkerSid, [Security.AccessControl.FileSystemRights]::Modify, $inherit,
        [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Quote-RefreshArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    if ($Value.Contains('"')) { throw 'Paths containing quotes are not supported.' }
    return '"' + $Value + '"'
}
