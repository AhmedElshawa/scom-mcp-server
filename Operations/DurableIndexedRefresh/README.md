# Durable indexed refresh on Windows

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

Registration creates one task with an `AtStartup` trigger,
`StartWhenAvailable`, `IgnoreNew` duplicate-instance behavior, no execution time
limit, and **zero automatic restarts**. The first run is at system startup
unless `-StartNow` is explicitly supplied. Task Scheduler keeps the supplied
task credential using Windows-protected storage; the scripts do not save it to
source, configuration, task arguments, or logs. Never pass a password as a
script parameter or command-line argument.

If registration or the optional `-StartNow` launch reports an error, inspect the
exact task and protected state directory before retrying. A task may already be
registered after a start-request failure.

The worker's existing checkpoint lease and reconciliation are used after startup.
An interrupted or ambiguous publication is reconciled before another source
scan; checkpoint policy conflicts and unexpected errors stop the worker. There
is no automatic retry after a fatal exit: this intentionally prevents an
unattended restart storm. Startup recovery and a reviewed manual start use
`--worker`, not repeated one-shot runs.

```powershell
.\Get-ScomWorkerTaskStatus.ps1
.\Stop-ScomWorkerTask.ps1
.\Start-ScomWorkerTask.ps1
.\Unregister-ScomWorkerTask.ps1
```

Each command selects the exact task name/path and verifies the ownership marker,
worker action, explicit password logon identity, duplicate policy, and zero
restart count before acting. Stop requests are never followed by broad process
kills; a timeout or mismatch is an unknown stop outcome. Unregister removes
only that task and deliberately preserves its protected status, logs, and
checkpoint.

Inspect `TaskState`, `RuntimeState`, `RuntimeConsistency`, `LastOutcome`,
`FailureCode`, and `LastPublicationUtc` together. `deferred` means the last
attempt safely deferred; `stopped` means the worker stopped or failed; a running
task with stale `running` runtime state is an unknown outcome to investigate,
not a success. Use authenticated `get_source_status` and the configured
freshness budgets to assess published data. Do not extend expiry or equate
process liveness with freshness.

## Synthetic validation

The `Durable indexed refresh synthetic tests` workflow runs on Windows Server
2022 under Windows PowerShell 5.1 and PowerShell 7. It checks task setup
contracts using mocked task-scheduler cmdlets and `-WhatIf`, and exercises
multiple serialized collection cycles, bounded deferrals, checkpoint restart
reconciliation, process-child cleanup, runtime status, and bounded log rotation.
It registers no scheduled task and uses no SCOM connection or customer
configuration. CI evidence is not deployment acceptance.
