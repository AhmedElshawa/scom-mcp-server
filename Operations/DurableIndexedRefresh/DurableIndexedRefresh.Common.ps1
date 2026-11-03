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
        $task.Actions[0].Arguments -notmatch '(^|\s)--worker(\s|$)' -or
        $task.Actions[0].Arguments -notmatch '(^|\s)--runtime-state-dir(\s|$)' -or
        $task.Principal.UserId -match '^(NT AUTHORITY\\)?(SYSTEM|LOCAL SERVICE|NETWORK SERVICE)$' -or
        [string]$task.Principal.LogonType -ne 'Password' -or
        [string]$task.Settings.MultipleInstances -ne 'IgnoreNew' -or
        [int]$task.Settings.RestartCount -ne 0) {
        throw 'The selected task no longer matches the durable indexed-refresh task contract.'
    }
    return $task
}

function Get-RefreshStateDirectory {
    param(
        [Parameter(Mandatory = $true)]$Task,
        [string]$StateDirectory
    )
    $stored = [regex]::Match($Task.Description, '; StateDirectory=(.+)$')
    if (-not $stored.Success) { throw 'The task does not contain its protected runtime-state location.' }
    $path = Resolve-RefreshLocalPath -Path $stored.Groups[1].Value -Directory -MustExist
    if ($StateDirectory) {
        $requested = Resolve-RefreshLocalPath -Path $StateDirectory -Directory -MustExist
        if (-not [string]::Equals($path, $requested, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The requested runtime-state directory does not match the owned task.'
        }
    }
    return $path
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
