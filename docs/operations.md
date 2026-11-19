# Operations

## Recurrence

Setup ends with no recurrence. After **Accept**, register the single worker task with the
package's own script (elevated host; never pass a password as an argument):

```powershell
$c = Get-Credential '<WORKER_ACCOUNT>'
& '<pkg>\Operations\DurableIndexedRefresh\Register-ScomWorkerTask.ps1' `
  -WorkerDllPath '<install>\server\ScomMcp.dll' `
  -WorkerConfigPath '<private>\worker.json' `
  -StateDirectory '<STATE_DIR>' `
  -Credential $c -StartNow
```

Expected: a task object with `TaskName` `SCOM-MCP Durable Indexed Refresh`, `StartupTrigger`
`AtStartup`, `DuplicateInstancePolicy` `IgnoreNew`, `AutomaticRestartCount` `0` and
`State` `start-requested`. The account must hold *Log on as a batch job*; `SYSTEM`,
`LocalService` and `NetworkService` are rejected. Task Scheduler stores the credential.
The task runs `dotnet ScomMcp.dll --worker <worker.json> --runtime-state-dir <STATE_DIR>`.

The cadence is `schedule.interval` in `worker.json`; the recommended default for inventory
is `01:00:00`. The receiver freshness budget must exceed it
([examples/receiver.example.md](../examples/receiver.example.md)). Companion scripts in the same
folder: `Get-`, `Start-`, `Stop-`, `Rearm-` and `Unregister-ScomWorkerTask.ps1`.

- `Stop` is cooperative. `Start` never rearms a failed worker.
- After a terminal failure the worker is gated: run `Rearm-ScomWorkerTask.ps1 -AcknowledgeFailure` after fixing the cause.
- A reboot without a clean stop left the worker in a gated state requiring an explicit rearm. A clean stop followed by a reboot has **not** been live-tested.
- Confirm freshness with the `get_source_status` tool after the first run.

## Logs and state

| What | Where | Notes |
|---|---|---|
| Worker runtime log | `<STATE_DIR>` | Three 4 MiB rotating files; `worker.status.json` reports `state`, `failureCode`, `rearmRequired`, `processId`. State directory must be new, empty, local NTFS and protected for SYSTEM, Administrators and the worker. |
| Collector log | `operationalLogging.directory` | Bounded, no secrets. |
| Receiver log | `OperationalLogging.Directory` | Ingestion acceptance and commit events. Also IIS logs. |
| Checkpoint | `checkpointPath` | Worker-private; never share with the pool. |
| Read model | `ReadModel.DatabasePath` | SQLite; back up before any policy change. |

## Policy changes

Changing the scope, allow-lists, `windowsMapping`, `WindowsMetricHosting` or
`WindowsOsCaptionProperty` on an existing read model is a policy change, because they are
pinned in the database. Procedure:

1. Stop the worker task and the receiver application pool.
2. Back up `read-model.db`.
3. Edit worker and receiver settings together so the allow-lists match.
4. Run `dotnet <install>\server\ScomMcp.dll --validate-setup <install>\server <private>\worker.json` (exit 0 ok; 2 source-evidence flags missing; 1 invalid).
5. Retire or reinitialize the old database as the change requires.
6. Start the pool, then the worker, and run one fresh collection.
7. Record the config hash change in your change journal and re-verify with `get_source_status`.

Cadence-only edits (`schedule.interval`) do not change the pinned policy, but still change the
worker config hash: record the change in your journal and restart the worker.

## Recovery

| Symptom | Action |
|---|---|
| `READ_MODEL_EXPIRED` | No fresh dataset within the budget: check the task and `worker.status.json`, rearm if gated, or lengthen `FreshMinutes`. |
| Worker gated (`rearmRequired`) | Fix the cause, `Rearm-ScomWorkerTask.ps1 -AcknowledgeFailure`, `Start-ScomWorkerSetup`/task start. |
| Setup interrupted | `Start-ScomSetup.ps1 -Step Recover`; it does not clear a pending action; see [install-cli.md](install-cli.md). |
| Certificate rotation | Add the new cert to the publisher app, install in `LocalMachine\My`, grant private-key read, update the thumbprint in `worker.json`. |

Codes and fixes: [troubleshooting.md](troubleshooting.md). Source README:
[`Operations/DurableIndexedRefresh/README.md`](../Operations/DurableIndexedRefresh/README.md).
