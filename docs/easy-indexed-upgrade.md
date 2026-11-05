# Three-step indexed evaluation deployment

This is the supported **upgrade of an existing configured indexed installation
with receiver and worker on the same host and using the same installed runtime**.
It is not first-time authentication, SCOM setup, certificate provisioning or an
EXE installer. Preserve your existing publisher authentication unchanged.
For split receiver/worker hosts, use the [full runbook](indexed-deployment-runbook.md)
to update both components together; this block deliberately refuses that topology.

Prerequisites: Administrator Windows PowerShell 5.1; IIS WebAdministration;
.NET 8 ASP.NET Core Hosting Bundle; a dedicated IIS application pool and physical
directory; working private indexed server/worker configuration; existing pinned
SCOM profile, publisher identity and certificate access; protected checkpoint and
receiver database directories **outside** the IIS target; an approved worker
account with batch logon rights. Deny-logon policy takes precedence over Allow.
New installs must complete the [runbook](indexed-deployment-runbook.md) first.
No new scope, metrics, live checks, certificate or trust changes are performed.

## 1. Download, verify and extract

Download `scom-mcp-server-<version>.zip` and its `.sha256` file from
[Releases](https://github.com/AhmedElshawa/scom-mcp-server/releases). In PowerShell,
compare the ZIP's `Get-FileHash -Algorithm SHA256` with the published checksum
**before** extracting with Explorer or `Expand-Archive`. The ZIP contains
`server`, `collector`, `Operations`, `docs`, and `package-manifest.json`.
Do not copy an old installed DLL over the new package.

## 2. Paste the upgrade block into Administrator Windows PowerShell 5.1

First stop and temporarily **disable the exact existing scheduled worker task**
(so it cannot be started during file replacement), and verify its worker and
source child processes exited cleanly. If you have the approved operational
scripts available under your organization's policy, use their cooperative stop.
Do not repeatedly end tasks or kill PowerShell processes. This upgrade block
refuses any visible `--worker` process and does not stop or rearm one for you.
Save protected backups of your external checkpoint/receiver databases using your
approved offline backup procedure; this block backs up only the IIS directory.

Paste this block as text into the shell; it does **not** invoke a downloaded
unsigned `.ps1` file, change execution policy or unblock files. Enter your own
paths and exact IIS pool name. Target must be the same installed directory used
by the receiver and scheduled worker DLL, not the extracted package directory.
Existing `web.config` and `appsettings*.json` are preserved; profile scripts,
private worker configuration, databases and checkpoints are never copied over.

<!-- BEGIN UPGRADE BLOCK -->
```powershell
& {
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version 2
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run an Administrator PowerShell shell.' }
    Import-Module WebAdministration -ErrorAction Stop
    function LocalDirectory([string]$Path) {
        if ($Path -notmatch '^[A-Za-z]:\\' -or $Path.Contains('"')) { throw 'Use an absolute local path without quotes.' }
        $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
        if ($full.Length -le 3 -or -not (Test-Path -LiteralPath $full -PathType Container)) {
            throw 'An existing non-root local directory is required.'
        }
        $cursor = Get-Item -LiteralPath $full -Force
        while ($null -ne $cursor) {
            if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse paths are not supported.' }
            $cursor = $cursor.Parent
        }
        if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($full)).DriveType -ne 'Fixed') {
            throw 'Use a fixed local disk.'
        }
        return $full
    }
    $package = LocalDirectory (Read-Host 'Extracted package directory')
    $target = LocalDirectory (Read-Host 'Existing dedicated IIS and worker runtime directory')
    $pool = Read-Host 'Exact dedicated IIS application pool name'
    $workerConfig = Read-Host 'Existing absolute private worker JSON path (outside IIS)'
    $runtime = Read-Host 'Protected runtime-state directory (existing, or new child of an existing directory)'
    $workerAccount = Read-Host 'Approved worker account (DOMAIN\account)'
    $backupRoot = LocalDirectory (Read-Host 'Existing protected backup parent directory outside IIS')
    $dotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
    $runtimes = & $dotnet --list-runtimes
    if ($LASTEXITCODE -ne 0 -or -not ($runtimes -match '^Microsoft.AspNetCore.App 8\.') -or
        -not ($runtimes -match '^Microsoft.NETCore.App 8\.')) { throw '.NET 8 ASP.NET Core runtime is required.' }
    if ($workerAccount -match '^(NT AUTHORITY\\)?(SYSTEM|LOCAL SERVICE|NETWORK SERVICE)$' -or
        [string]::IsNullOrWhiteSpace($workerAccount)) { throw 'Supply the approved non-built-in worker identity.' }
    $sid = ([Security.Principal.NTAccount]::new($workerAccount)).Translate([Security.Principal.SecurityIdentifier])
    if ($sid.Value -in @('S-1-5-18','S-1-5-19','S-1-5-20')) { throw 'Built-in worker identities are not accepted.' }
    foreach ($path in @($workerConfig, $runtime)) {
        if ($path -notmatch '^[A-Za-z]:\\' -or $path.Contains('"')) { throw 'Use absolute local state/configuration paths.' }
    }
    $workerConfig = [IO.Path]::GetFullPath($workerConfig)
    $runtime = [IO.Path]::GetFullPath($runtime).TrimEnd('\')
    $null = LocalDirectory (Split-Path -Parent $workerConfig)
    $null = LocalDirectory (Split-Path -Parent $runtime)
    if (Test-Path -LiteralPath $runtime) { $null = LocalDirectory $runtime }
    if ((Get-Item -LiteralPath $workerConfig -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Private configuration must not be a reparse point.'
    }
    foreach ($outside in @($workerConfig, $runtime, $backupRoot, $package)) {
        if ($outside -eq $target -or $outside.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $target.StartsWith($outside + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Package, private configuration, runtime state and backup must be separate from the IIS directory.'
        }
    }
    foreach ($tree in @($package, $target)) {
        if (@(Get-ChildItem -LiteralPath $tree -Recurse -Force |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
            throw 'Package and installed runtime must not contain reparse points.'
        }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.format -ne 1 -or $manifest.sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid package manifest.' }
    $seen = @{}
    foreach ($entry in $manifest.files) {
        $file = [IO.Path]::GetFullPath((Join-Path $package $entry.path))
        if (-not $file.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $seen.ContainsKey($file) -or $entry.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw 'Package manifest path or checksum mismatch.'
        }
        $seen[$file] = $true
    }
    if (@(Get-ChildItem -LiteralPath $package -Recurse -File -Force).Count -ne $seen.Count + 1) {
        throw 'Unexpected files in the extracted package.'
    }
    foreach ($file in @('ScomMcp.dll','ScomMcp.deps.json','ScomMcp.runtimeconfig.json','web.config')) {
        if (-not (Test-Path -LiteralPath (Join-Path $target $file) -PathType Leaf)) { throw 'Not an existing configured runtime.' }
        if (-not $seen.ContainsKey((Join-Path $package ('server\' + $file)))) { throw 'Incomplete server package.' }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $target 'appsettings.json') -PathType Leaf)) {
        throw 'Existing private appsettings.json is required; alternate provider deployments use the full runbook.'
    }
    $config = Get-Content -LiteralPath $workerConfig -Raw | ConvertFrom-Json
    $null = LocalDirectory (Split-Path -Parent $config.checkpointPath)
    if (-not (Test-Path -LiteralPath $config.checkpointPath -PathType Leaf)) { throw 'Existing worker checkpoint is required.' }
    if ($config.checkpointPath.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Checkpoint must be outside IIS.'
    }
    $serverConfig = Get-Content -LiteralPath (Join-Path $target 'appsettings.json') -Raw | ConvertFrom-Json
    $null = LocalDirectory (Split-Path -Parent $serverConfig.ReadModel.DatabasePath)
    if (-not (Test-Path -LiteralPath $serverConfig.ReadModel.DatabasePath -PathType Leaf)) { throw 'Existing receiver database is required.' }
    if (-not $serverConfig.ReadModel.Enabled -or
        $serverConfig.ReadModel.DatabasePath.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Existing indexed read-model configuration with external database is required.'
    }
    foreach ($dataPath in @($config.checkpointPath, $serverConfig.ReadModel.DatabasePath, $workerConfig)) {
        if ($dataPath -eq $runtime -or $dataPath.StartsWith($runtime + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Runtime-state directory must be separate from private configuration and databases.'
        }
    }
    if (@(Get-ChildItem -LiteralPath $target -Recurse -File -Force |
        Where-Object { $_.Name -match '\.(db|db-wal|db-shm|db-journal)$' }).Count) {
        throw 'Database files inside IIS require the full runbook, not this upgrade.'
    }
    $workers = @(Get-CimInstance Win32_Process -ErrorAction Stop |
        Where-Object { $_.CommandLine -match '(^|\s)--worker(-once)?(\s|$)' })
    if ($workers.Count) { throw 'Stop and inspect existing workers/source children before upgrading; no process was killed.' }
    function Assert-NoActiveWorkerTask([object[]]$Tasks, [string]$TargetDll) {
        foreach ($task in $Tasks) {
            foreach ($action in $task.Actions) {
                $argumentsProperty = $action.PSObject.Properties['Arguments']
                if ($null -eq $argumentsProperty) { continue }
                $actionArguments = [string]$argumentsProperty.Value
                if ($actionArguments -match '(^|\s)--worker(\s|$)' -and
                    $actionArguments.IndexOf($TargetDll, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                    ([string]$task.State -eq 'Running' -or $task.Settings.Enabled)) {
                    throw 'An existing task for this worker must be stopped and disabled during upgrade.'
                }
            }
        }
    }
    Assert-NoActiveWorkerTask -Tasks @(Get-ScheduledTask -ErrorAction Stop) `
        -TargetDll (Join-Path $target 'ScomMcp.dll')
    $null = Get-Item -LiteralPath ('IIS:\AppPools\' + $pool) -ErrorAction Stop
    $apps = @()
    $virtualRoots = @()
    foreach ($site in Get-Website) {
        $apps += [pscustomobject]@{ Pool = [string]$site.applicationPool; Path = [Environment]::ExpandEnvironmentVariables([string]$site.physicalPath) }
        foreach ($virtual in Get-WebVirtualDirectory -Site $site.Name) {
            $virtualRoots += [Environment]::ExpandEnvironmentVariables([string]$virtual.physicalPath)
        }
        foreach ($app in Get-WebApplication -Site $site.Name) {
            $apps += [pscustomobject]@{ Pool = [string]$app.applicationPool; Path = [Environment]::ExpandEnvironmentVariables([string]$app.physicalPath) }
            foreach ($virtual in Get-WebVirtualDirectory -Site $site.Name -Application $app.Path) {
                $virtualRoots += [Environment]::ExpandEnvironmentVariables([string]$virtual.physicalPath)
            }
        }
    }
    $owned = @($apps | Where-Object { $_.Pool -eq $pool })
    $targetApps = @($apps | Where-Object { [IO.Path]::GetFullPath($_.Path).TrimEnd('\') -eq $target })
    if ($owned.Count -ne 1 -or $targetApps.Count -ne 1 -or
        [IO.Path]::GetFullPath($owned[0].Path).TrimEnd('\') -ne $target) {
        throw 'Pool and target must belong to exactly one dedicated IIS application.'
    }
    foreach ($iisPath in @($apps | ForEach-Object { $_.Path }) + @($virtualRoots)) {
        $iisRoot = [IO.Path]::GetFullPath($iisPath).TrimEnd('\')
        if ($backupRoot -eq $iisRoot -or $backupRoot.StartsWith($iisRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Backup parent must be outside every IIS site/application physical directory.'
        }
    }
    if ((Get-WebAppPoolState -Name $pool).Value -ne 'Started') { throw 'Pool is not started; diagnose before upgrading.' }
    $backup = Join-Path $backupRoot ('indexed-upgrade-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $backup
    $acl = Get-Acl -LiteralPath $backup
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRuleSpecific($rule) }
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach ($id in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($id), 'FullControl', $inherit, 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $backup -AclObject $acl
    Stop-WebAppPool -Name $pool
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Get-WebAppPoolState -Name $pool).Value -ne 'Stopped') {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Pool stop unconfirmed; no files copied.' }
        Start-Sleep -Milliseconds 250
    }
    # Failure leaves the pool stopped. Inspect the protected backup before recovery.
    Get-ChildItem -LiteralPath $target -Force | Copy-Item -Destination $backup -Recurse -Force
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $package 'server') -File -Recurse) {
        $relative = $file.FullName.Substring((Join-Path $package 'server').Length + 1)
        if ($relative -eq 'web.config' -or $file.Name -like 'appsettings*.json') { continue }
        $destination = Join-Path $target $relative
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw 'Installed file digest mismatch; pool remains stopped.' }
    }
    if (-not (Test-Path -LiteralPath $runtime)) { $null = New-Item -ItemType Directory -Path $runtime }
    $null = LocalDirectory $runtime
    $acl = Get-Acl -LiteralPath $runtime
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRuleSpecific($rule) }
    foreach ($id in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($id), 'FullControl', $inherit, 'None', 'Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'Modify', $inherit, 'None', 'Allow'))
    Set-Acl -LiteralPath $runtime -AclObject $acl
    Start-WebAppPool -Name $pool
    if ((Get-WebAppPoolState -Name $pool).Value -ne 'Started') { throw 'Pool start unconfirmed; inspect IIS diagnostics.' }
    Write-Output "Copied source commit $($manifest.sourceCommit). Protected backup: $backup"
    Write-Output "Task Program/script (ONE path, no arguments): $dotnet"
    Write-Output ('Task arguments: "{0}" --worker "{1}" --runtime-state-dir "{2}"' -f (Join-Path $target 'ScomMcp.dll'), $workerConfig, $runtime)
    Write-Output "Task Start in (unquoted): $target"
    Write-Output "Task Description: SCOM-MCP-DURABLE-INDEXED-REFRESH-V1; StateDirectory=$runtime"
    Write-Output 'Verify HTTPS health and authenticated reader access; then follow task and recurrence acceptance below.'
}
```
<!-- END UPGRADE BLOCK -->

If any command fails, **stop**. The block does not automatically rearm, restore,
delete files or restart on failure. If copying failed, keep the exact pool stopped
and recover the full protected backup with your approved procedure. Do not mix
individual old/new DLLs. Never reset databases to make an upgrade appear healthy.

## 3. Create the worker task in Task Scheduler

If the exact existing task already has these fields, inspect/reuse and re-enable it rather
than creating a duplicate. Keep the worker stopped until the receiver is healthy.

1. Choose **Create Task** (not Basic Task). Select the approved worker account,
   **Run whether user is logged on or not**, password logon (do not select
   **Do not store password**), and leave **Run with highest privileges** unchecked.
   Enter the description printed by the block exactly. The account needs batch
   logon permission, existing source/checkpoint/certificate permissions and the
   runtime directory ACL created above. Save the password only in Task Scheduler's
   protected credential store, never a command or file.
2. Trigger: **At startup**, enabled. Action: **Start a program**. Put the full
   `dotnet.exe` path printed by the block in **Program/script**; quote that field
   if it contains spaces, e.g. `"C:\Program Files\dotnet\dotnet.exe"`.
   Put ONLY the printed DLL/`--worker`/config/`--runtime-state-dir` arguments in
   **Add arguments**. Put the printed directory in **Start in**, **without quotes**.
   If Windows offers to split `C:\Program` from `Files\dotnet...`, cancel and fix
   quoting; do not accept that split. No `powershell.exe` launcher is required.
3. Settings: **Run task as soon as possible after a scheduled start is missed**;
   **Do not start a new instance** if already running. Disable automatic restart,
   execution time limit, idle-only, AC-only and stop-on-battery restrictions for
   this always-running host. Do not add a five-minute trigger: cadence is the
   worker's existing private `schedule.interval`, a delay after completion.

Before Run, inspect the saved action rather than relying on the GUI:

```powershell
$task = Get-ScheduledTask -TaskName 'YOUR EXACT TASK NAME' -TaskPath '\'
$task.Actions | Format-List Execute, Arguments, WorkingDirectory
$task.Principal | Format-List UserId, LogonType, RunLevel
$task.Settings | Format-List MultipleInstances, RestartCount, ExecutionTimeLimit
```

`Execute` must be the complete dotnet path (not `C:\Program`), arguments must
start with the quoted **DLL**, working directory is unquoted, `LogonType` is
Password, `RunLevel` Limited, `MultipleInstances` IgnoreNew, `RestartCount` 0,
and `ExecutionTimeLimit` PT0S.

Only for a **diagnosed prior terminal/unknown failure**, with this exact owned
task stopped and no remaining worker/source children, use the native control:

```powershell
# Set these to the exact values printed above; this does not start collection.
$ErrorActionPreference = 'Stop'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$dll = 'C:\ApprovedApps\ScomMcp\ScomMcp.dll'
$runtime = 'C:\ProgramData\ScomMcpWorker\runtime'
$config = 'C:\ProtectedWorker\worker.json'
$workerAccount = 'DOMAIN\approved-worker'
$task = Get-ScheduledTask -TaskName 'YOUR EXACT TASK NAME' -TaskPath '\' -ErrorAction Stop
$expectedArguments = '"{0}" --worker "{1}" --runtime-state-dir "{2}"' -f $dll, $config, $runtime
if ([string]$task.State -eq 'Running' -or @($task.Actions).Count -ne 1 -or
    $task.Description -ne "SCOM-MCP-DURABLE-INDEXED-REFRESH-V1; StateDirectory=$runtime" -or
    [string]$task.Principal.UserId -ine $workerAccount -or
    $task.Actions[0].Execute.Trim('"') -ne $dotnet -or
    $task.Actions[0].Arguments -ne $expectedArguments -or
    $task.Actions[0].WorkingDirectory -ne (Split-Path -Parent $dll) -or
    [string]$task.Principal.LogonType -ne 'Password' -or
    [string]$task.Settings.MultipleInstances -ne 'IgnoreNew' -or
    [int]$task.Settings.RestartCount -ne 0) { throw 'Stopped owned task contract does not match; rearm refused.' }
$workers = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
    $_.CommandLine -and $_.CommandLine.IndexOf($dll, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $_.CommandLine -match '(^|\s)--worker(\s|$)'
})
if ($workers.Count) { throw 'A matching worker process remains; rearm refused.' }
$statusPath = Join-Path $runtime 'worker.status.json'
if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) { throw 'Worker status is missing; rearm refused.' }
$status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
$expectedStatusProperties = @('state', 'startedUtc', 'updatedUtc', 'lastAttemptUtc', 'lastPublicationUtc',
    'lastOutcome', 'consecutiveDeferredAttempts', 'failureCode', 'processId', 'rearmRequired',
    'lastFailureCode', 'rearmedUtc')
$statusProperties = @($status.PSObject.Properties | ForEach-Object { $_.Name })
if ($statusProperties.Count -ne $expectedStatusProperties.Count -or
    @($statusProperties | Where-Object { $_ -notin $expectedStatusProperties }).Count -or
    @($expectedStatusProperties | Where-Object { $_ -notin $statusProperties }).Count) {
    throw 'Worker status schema is malformed or unknown; preserve it and investigate.'
}
$updatedUtc = [DateTimeOffset]::Parse([string]$status.updatedUtc).UtcDateTime
$bootUtc = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime()
$stalePriorRun = $status.state -eq 'running' -and $status.rearmRequired -eq $false -and
    [int]$status.processId -gt 0 -and $updatedUtc -lt $bootUtc
$terminalFailure = $status.state -eq 'stopped' -and $status.rearmRequired -eq $true -and
    -not [string]::IsNullOrWhiteSpace([string]$status.failureCode)
if (-not ($stalePriorRun -or $terminalFailure)) {
    throw 'Status is not a diagnosed stale preboot run or terminal failure; rearm refused.'
}
if ((Read-Host 'After diagnosis, type REARM to acknowledge this exact stopped task failure') -cne 'REARM') {
    throw 'Rearm not acknowledged.'
}
& $dotnet $dll --worker-rearm $runtime
if ($LASTEXITCODE -ne 0) { throw 'Rearm refused; preserve state and diagnose.' }
```

The pasted guard validates the exact task marker, worker identity/action, stopped
state, absence of the matching worker process, and either a stale preboot
`running` status or a structurally known terminal-failure status. The native
control takes the exclusive worker lease and independently validates its state.
Verify no remaining worker/source children before acknowledging. Prefer
`Rearm-ScomWorkerTask.ps1` where approved script execution is available:
it adds exact-task ownership and not-running validation. Never automatically
rearm on every deployment. Malformed state is not repaired; preserve it.

Choose **Run** once. Inspect protected `worker.status.json` and bounded `worker.log`
in the runtime directory. Require `state=running`, `failureCode=null`,
`rearmRequired=false` and a new `lastPublicationUtc`. Call authenticated agent
tools and record dataset ID, domain coverage and freshness. Wait the configured
interval **plus collection duration**, then require another dataset/publication
from the same worker without another manual Run. Verify an actual logoff and
reboot separately before claiming unattended recovery. A terminal failure stays
gated across reboot; there is no automatic process restart.

**Expected availability gaps:** each new Begin invalidates the previous result;
readers report `READ_MODEL_LATEST_COLLECTION_UNAVAILABLE` until commit, even if
the previous dataset was fresh. The observed lab scan took about 35 seconds;
this is not a guarantee. A failed scan can leave readers unavailable until
recovery. This release is lab-validated evaluation software, not production
certification or a continuous-availability guarantee.

| Error | Action |
|---|---|
| `0x80070569` | Check batch logon for the exact account; Deny overrides Allow. Do not switch to SYSTEM. |
| `0x80070002`, `C:\Program`, stray `Files\dotnet...` in arguments | Correct the saved action fields/quoting, inspect them, then Run once. |
| `PUBLISHER_HTTP_409` on an old package | Upgrade matching receiver and worker. Generic 409 never authorizes reservation rotation. |
| `PUBLISHER_SESSION_EXPIRED_EMPTY_RESERVATION` | Recovery occurs once only if local checkpoint has no prepared data. If it stops, diagnose; do not clear state or repeatedly rearm. |
| Stopped/rearm required, malformed status or unknown outcome | Preserve checkpoint/status/logs. Use the [durable recovery procedure](../Operations/DurableIndexedRefresh/README.md); never manual SQL or forced process kills. |
