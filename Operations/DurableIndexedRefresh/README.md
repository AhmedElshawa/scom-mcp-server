# Durable indexed refresh on Windows

For a **fresh guided installation**, use the packaged `ScomSetup.exe` through
one-shot publication and host acceptance, then the final page's optional
**Register optional startup worker**. The protected accepted deployment supplies
the installed DLL, private worker JSON and separate runtime-state paths; customers
do not need to recreate the task or copy the example paths below. The visible
administrator PowerShell window requests the exact selected worker credential
and a separate registration confirmation.

Registration creates an **enabled AtStartup task**, so a later reboot can run it;
it does not start it immediately. Starting now requires the separate **Start
registered worker** action and confirmation. Status and confirmed Stop are also
available there. Keep manual cadence by not registering. All accepted-journal,
installed/configuration hash, exact task ownership and failure/rearm guards still
apply. See [guided setup](../../docs/guided-setup.md#guided-durable-refresh-remains-a-separate-opt-in).
The command examples below are the advanced operations lane, not mandatory
fresh-install steps.

This is an opt-in Windows Task Scheduler host for the existing serialized
`--worker` process. It is not a service installer, deployment pack, source
profile, SCOM connection test, or production certification. It never schedules
`--worker-once` and it does not change collector scope, credentials, source
configuration, checkpoint policy, or read-model expiry.

## Requirements and operator choices

- Windows 10 / Windows Server 2016 or later, .NET 8 runtime, and the complete
  reviewed package. The worker's Desktop Windows PowerShell 5.1 profile,
  Operations Manager SDK, protected private configuration, publisher
  certificate, and checkpoint directory must already be prepared.
- An explicit operator-selected worker account with the approved SCOM read-only
  role, local checkpoint access, and certificate private-key access. The account
  must be permitted to log on as a batch task. Built-in `SYSTEM`, `LocalService`,
  and `NetworkService` identities are rejected; there is no fallback identity.
- Operator-selected `schedule.interval`, `attemptDeadline`, backoff values and
  `maxConsecutiveDeferredAttempts` in the private worker configuration. The
  attempt limit is 1..100 (default 5). A deferred attempt is retried only after
  the configured bounded backoff; after the limit the worker stops for operator
  investigation. A published attempt resets that counter.
- Operator-selected source/read-model freshness budgets. They remain the
  freshness policy; the task's running state and its last publication timestamp
  are not proof that data is currently available or fresh.
- A new, empty, local NTFS runtime-state directory separate from the checkpoint
  and server read-model databases. The installer protects it for SYSTEM,
  Administrators, and only the selected worker identity. Its log is bounded to
  three 4 MiB files, and its status file records sanitized lifecycle/outcome
  codes, UTC attempt/publication times, and the last deferred count.

## Register and operate

Follow the indexed deployment runbook's acceptance checks before enabling
recurrence. Use a shell under the organization's approved script-signing policy;
these scripts do not bypass or weaken execution policy. If required, use your
approved signing/trust process first. Run the commands from this package
directory, `Operations\DurableIndexedRefresh`.

```powershell
$workerCredential = Get-Credential -UserName 'DOMAIN\approved-worker'
.\Register-ScomWorkerTask.ps1 `
    -WorkerDllPath 'C:\ApprovedApps\ScomMcp\ScomMcp.dll' `
    -WorkerConfigPath 'C:\ProtectedWorker\worker.json' `
    -StateDirectory 'C:\ProgramData\ScomMcpWorker\runtime' `
    -Credential $workerCredential
```

Registration creates one task with an `AtStartup` trigger delayed **3 minutes** (so
the network, SCOM SDK and certificate store are ready after a reboot),
`StartWhenAvailable`, `IgnoreNew` duplicate-instance behavior, no execution time
limit, and **automatic restart on failure: 3 restarts, 5 minutes apart**. It also
creates the Windows Event Log source `ScomMcpWorker` (Application log) when missing;
this needs elevation, and the worker account only writes events to it.
Deferred attempts still retry within the current worker process after bounded
backoff and checkpoint reconciliation. The first run is at system startup
unless `-StartNow` is explicitly supplied. Task Scheduler keeps the supplied
task credential using Windows-protected storage; the scripts do not save it to
source, configuration, task arguments, or logs. Never pass a password as a
script parameter or command-line argument.

Worker exit codes: `0` published or already running; `1` transient failure
(network, timeout, I/O, exhausted deferrals, lock held by an unverifiable process);
`2` configuration or attestation failure (invalid JSON or arguments, publisher
certificate/scope/token failures, HTTP 401/403, refused recovery). Task Scheduler
restarts on **any** non-zero exit and cannot filter by exit code, so "not
restartable" (`2`) is enforced by the persisted rearm gate: a terminal failure sets
`rearmRequired`, restarted instances refuse at once with `WORKER_RECOVERY_REFUSED`
(exit `2`, no source access) and use up the three restarts. After the restarts are
used the task is not restarted again until the next startup or a manual start.

If registration or the optional `-StartNow` launch reports an error, inspect the
exact task and protected state directory before retrying. A task may already be
registered after a start-request failure.

The worker's existing checkpoint lease and reconciliation are used after startup.
An interrupted or ambiguous publication is reconciled before another source
scan; checkpoint policy conflicts and unexpected errors stop the worker. The
restart budget is bounded (3 × 5 minutes) to prevent an unattended restart storm.

**Self-healing after a reboot or crash.** While running, the worker holds an
exclusive `worker.lock` and writes `pid`, `processStartTimeUtc`, `bootId`,
`hostName` and `heartbeatUtc` (every 30 seconds) to `worker.status.json`. A leftover
`running` marker is recovered automatically when the process is gone, the PID was
reused, the boot id or host changed, or the heartbeat is older than 3 ×
`schedule.interval`: the worker logs `WORKER_STALE_MARKER_RECOVERED` (Event Log
`2001`), rearms itself and continues. A live worker with a fresh heartbeat makes a
second start exit `0` (`WORKER_ALREADY_RUNNING`). A lock held by a process that cannot
be verified is refused (exit `1`) and never taken over. Only a persisted **terminal**
failure blocks the start (`WORKER_RECOVERY_REFUSED`, Event Log `3001`). The heartbeat
is an in-process timer: it shows the process is alive, not that collection is
progressing. Event Log events: `1001` started, `2001` recovered or restartable
stop, `3001` non-restartable failure.

```powershell
.\Get-ScomWorkerTaskStatus.ps1
.\Stop-ScomWorkerTask.ps1
.\Start-ScomWorkerTask.ps1
.\Unregister-ScomWorkerTask.ps1
```

Each command selects the exact task name/path and verifies the ownership marker,
worker action, explicit password logon identity, duplicate policy, and the restart
contract (3 restarts, 5 minutes apart) before acting. Stop creates a cooperative request in protected
runtime state and waits for a clean worker stop; it never uses broad process
kills. A timeout, power loss, malformed status or mismatched task is an unknown
outcome and remains gated. The stop request cannot guarantee cancellation of
work already accepted by a remote/backend system. Unregister removes only that
task and deliberately preserves its protected status, logs, and checkpoint.

After diagnosing a **terminal** failure (log shows `WORKER_RECOVERY_REFUSED`) and
reconciling any ambiguous publication, rearm only the exact stopped owned task:

```powershell
.\Rearm-ScomWorkerTask.ps1 -AcknowledgeFailure
.\Get-ScomWorkerTaskStatus.ps1
.\Start-ScomWorkerTask.ps1
```

Run rearm only from an operator context authorized to administer that exact
task and modify its protected runtime-state directory.

Rearm requires the explicit acknowledgement switch, validates task ownership
and action, refuses a running task, and takes the same exclusive runtime lease
as the worker. It preserves the failure code and collection evidence while
clearing only the rearm gate. `Start-ScomWorkerTask.ps1` never rearms. Malformed
or unknown status is not repaired automatically; preserve and inspect it rather
than editing or deleting state to bypass the gate. Rearm is not proof of backend
cancellation or successful publication.

### Expired reservation recovery

A worker can stop after reserving/beginning ingestion but before persisting a
prepared dataset. Previously, an expired receiver session in this state caused
every subsequent Begin to return HTTP 409 for the same pinned reservation.
Rearm alone cannot correct that protocol conflict.

The receiver now returns `SESSION_EXPIRED_EMPTY_RESERVATION` only when the exact
authenticated publisher owns an expired staging session with zero stored
batches. After that response, the worker can atomically replace its reservation
only when its durable checkpoint has no pending/prepared payload and the
reservation is not acknowledged. Prior acknowledgement and reconciliation
history remain intact. One attempt permits at most one replacement and one new
Begin; a further conflict stops normally. The receiver's consumed-session
ledger is retained. No old prepared data is relabeled under the new identity.

Both the receiver and worker must use this updated package. An older receiver's
generic HTTP 409 does not authorize recovery. Prepared, nonempty, missing-ledger
or otherwise ambiguous sessions stay pinned and fail closed; committed pending
sessions retain the existing same-identity replay/acknowledgement path.

For an approved deployment, first stop the exact worker and verify its process
and source children have exited. Preserve protected backups of runtime status,
logs, worker checkpoint and receiver state. Deploy the reviewed package to both
receiver and worker using the existing controlled deployment procedure; keep
private configuration, publisher identity, source scope, freshness policy and
databases unchanged. Verify the running receiver and worker binaries match the
reviewed package. Then diagnose the stopped state, explicitly rearm with
`Rearm-ScomWorkerTask.ps1 -AcknowledgeFailure`, inspect status, and start once
with `Start-ScomWorkerTask.ps1`. Require a new committed publication and
authenticated source freshness, followed by another refresh cycle. If recovery
is refused, retain evidence and investigate; do not clear checkpoints, delete
ledger rows, submit manual SQL writes or repeatedly rearm.

## Observed evaluation and reboot boundary

The following is bounded lab evidence, not a guarantee or production acceptance:

- The first public preview's pasted upgrade block failed before stopping IIS or
  copying files because StrictMode encountered a scheduled-task action without an
  `Arguments` property. The guard now skips actions that do not expose that
  property and still checks executable worker actions. The fix is in the
  repository patch PR; the immutable earlier preview ZIP/tag has not been
  replaced.
- After running the corrected full upgrade block, the operator confirmed the
  existing private configuration, pinned profile and databases were preserved.
  The worker task was started and the agent reported a new fresh dataset with
  complete 3/3 expected coverage.
- A subsequent reboot was performed without first requesting a clean worker
  stop. The startup task ran and exited nonzero. Its runtime status still showed
  the preboot `running` state (worker start 07:47:48, last publication 07:48:29
  on the server clock); boot was at 07:53:33 and the task invocation at 07:53:53.
  This is consistent with the persisted prior-running fail-closed gate, but the
  exact exit cause was not independently established. Do not document it as a
  proven cause or as successful unattended recovery.
- After verifying the exact task was stopped, its identity/action matched, no
  worker process remained, and the status was stale from before boot, the
  operator explicitly rearmed and started the task. The agent then confirmed a
  fresh postboot dataset collected at 08:03:35Z with complete 3/3 expected
  coverage. This verifies the guarded, operator-driven recovery path only.

**Reboot procedure:** a planned reboot should still prefer a cooperative clean
stop (leave the startup task enabled), but it is no longer required: after an
unexpected interruption, crash or reboot without a clean stop, the worker recovers
the stale `running` marker automatically at the next start
(`WORKER_STALE_MARKER_RECOVERED`) and resumes publishing; manual rearm is needed
only if the log shows `WORKER_RECOVERY_REFUSED`. After boot, verify a new
publication and authenticated source freshness (or `GET /healthz/freshness`). The
automatic recovery is covered by synthetic tests; the lab reboot result is
recorded in the pull request that introduced it. A clean cooperative-stop-then-reboot
sequence has not been live-tested. The evidence above predates the
feature and describes the earlier manual-rearm behavior. Never delete or rewrite
status/checkpoints to make startup proceed. A clean process stop also cannot
guarantee cancellation of work already accepted by a remote/backend system.

For a planned stop without invoking the packaged PowerShell scripts, the native
worker control accepts only the explicitly supplied runtime-state directory.
Set the values to the exact saved task fields, then verify the task marker,
identity, action and running state before requesting stop:

```powershell
$ErrorActionPreference = 'Stop'
$taskName = 'YOUR EXACT TASK NAME'
$taskPath = '\'
$workerAccount = 'DOMAIN\approved-worker'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$dll = 'C:\ApprovedApps\ScomMcp\ScomMcp.dll'
$config = 'C:\ProtectedWorker\worker.json'
$runtime = 'C:\ProgramData\ScomMcpWorker\runtime'

$task = Get-ScheduledTask -TaskName $taskName -TaskPath $taskPath -ErrorAction Stop
$expectedArguments = '"{0}" --worker "{1}" --runtime-state-dir "{2}"' -f $dll, $config, $runtime
if ([string]$task.State -ne 'Running' -or
    $task.Description -ne "SCOM-MCP-DURABLE-INDEXED-REFRESH-V1; StateDirectory=$runtime" -or
    [string]$task.Principal.UserId -ine $workerAccount -or
    [string]$task.Principal.LogonType -ne 'Password' -or
    -not $task.Settings.Enabled -or
    @($task.Actions).Count -ne 1 -or
    $task.Actions[0].Execute.Trim('"') -ine $dotnet -or
    $task.Actions[0].Arguments -cne $expectedArguments -or
    $task.Actions[0].WorkingDirectory -ine (Split-Path -Parent $dll) -or
    [string]$task.Settings.MultipleInstances -ne 'IgnoreNew' -or
    [int]$task.Settings.RestartCount -ne 3 -or [string]$task.Settings.RestartInterval -ne 'PT5M') {
    throw 'Exact running owned task contract does not match; stop request refused.'
}
$workers = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
    $_.CommandLine -and $_.CommandLine.IndexOf($dll, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $_.CommandLine -match '(^|\s)--worker(\s|$)'
})
if ($workers.Count -ne 1) { throw 'Expected exactly one matching worker process; inspect before stopping.' }
$status = Get-Content -LiteralPath (Join-Path $runtime 'worker.status.json') -Raw | ConvertFrom-Json
if ($status.state -ne 'running' -or $status.rearmRequired -ne $false -or
    $status.failureCode -or [int]$status.processId -ne [int]$workers[0].ProcessId) {
    throw 'Runtime status does not identify this healthy running worker; stop request refused.'
}
& $dotnet $dll --worker-stop-request $runtime
if ($LASTEXITCODE -ne 0) { throw 'Stop request refused; preserve state and inspect the task.' }
```

Wait for the exact task to stop, then inspect its protected `worker.status.json`.
Proceed with reboot only when `state=stopped`, `failureCode` is null, and
`rearmRequired=false`. Do not disable the task for a planned reboot. For a
terminal failure (`WORKER_RECOVERY_REFUSED`), use the separately guarded native explicit
rearm procedure in the [upgrade and recovery guide](../../docs/easy-indexed-upgrade.md)
only after confirming the task is stopped, the saved identity/action/runtime
directory match, no worker remains, and the failure is understood.
Malformed or ambiguous status is a stop-and-investigate condition.

Inspect `TaskState`, `RuntimeState`, `RuntimeConsistency`, `HeartbeatUtc`, `LastOutcome`,
`FailureCode`, `LastFailureCode`, `RearmRequired`, and `LastPublicationUtc`
together. `deferred` means the last
attempt safely deferred; `stopped` means the worker stopped or failed; a running
task with a stale `running` runtime state is recovered automatically at the next
start (shown as `StaleMarker`), not a success. Use authenticated `get_source_status` and the configured
freshness budgets to assess published data. Do not extend expiry or equate
process liveness with freshness.

## Synthetic validation

The `Durable indexed refresh synthetic tests` workflow runs on Windows Server
2022 under Windows PowerShell 5.1 and PowerShell 7. It checks task setup
contracts using mocked task-scheduler cmdlets and `-WhatIf`, and exercises
multiple serialized collection cycles, bounded deferrals, checkpoint restart
reconciliation, process-child cleanup, persisted failure across a fresh
worker/reboot simulation, explicit rearm, unknown/malformed state refusal,
stale-marker self-recovery, healthy startup recovery, cooperative stop, and bounded log rotation.
It registers no scheduled task and uses no SCOM connection or customer
configuration. CI evidence is not deployment acceptance.
