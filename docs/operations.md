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
`AtStartup`, `StartupDelay` `PT3M`, `DuplicateInstancePolicy` `IgnoreNew`,
`AutomaticRestartCount` `3`, `AutomaticRestartInterval` `PT5M` and
`State` `start-requested`. The account must hold *Log on as a batch job*; `SYSTEM`,
`LocalService` and `NetworkService` are rejected. Task Scheduler stores the credential.
The task runs `dotnet ScomMcp.dll --worker <worker.json> --runtime-state-dir <STATE_DIR>`.
Registration (elevated) also creates the Windows Event Log source `ScomMcpWorker` in the
Application log if it does not exist; the worker account only writes events to it.

The cadence is `schedule.interval` in `worker.json`; the recommended default for inventory
is `01:00:00`. The receiver freshness budget must exceed it
([examples/receiver.example.md](../examples/receiver.example.md)). Companion scripts in the same
folder: `Get-`, `Start-`, `Stop-`, `Rearm-` and `Unregister-ScomWorkerTask.ps1`.

- `Stop` is cooperative. `Start` never rearms a failed worker.
- After a **terminal** failure the worker is gated: run `Rearm-ScomWorkerTask.ps1 -AcknowledgeFailure` after fixing the cause.
- After a restart, recovery is **automatic**; manual `Rearm-` is needed only if the log shows `WORKER_RECOVERY_REFUSED`. Details: [After a server restart](troubleshooting.md#after-a-server-restart-worker-self-recovery) and [Self-healing worker](#self-healing-worker).
- Confirm freshness with the `get_source_status` tool after the first run.

### Self-healing worker

**Identity and heartbeat.** While running, the worker records `pid`, `processStartTimeUtc`, `bootId` (OS last boot time), `hostName` and `heartbeatUtc` in `worker.status.json`; the heartbeat is refreshed every 30 seconds. It also holds an exclusive lock on `worker.lock` in the state directory for the life of the process. Lock acquisition is the authoritative "am I alone" check.

**On start** the worker proceeds as follows.

| Situation | Result |
|---|---|
| Lock is held and the marker's pid, process start time and host match a live process with a fresh heartbeat | Logs `WORKER_ALREADY_RUNNING`, exits `0`. Status file untouched. |
| Lock is free but the marker says `running` and the process is gone, the PID was reused, `bootId` changed, the host changed, or the heartbeat is older than 3 × `schedule.interval` | Logs `WORKER_STALE_MARKER_RECOVERED` (with a `Reason`), rearms itself, continues and publishes. |
| Lock is held by a process that cannot be verified, or is alive with a stale heartbeat | Refuses (`WORKER_LOCK_HELD_UNVERIFIED` or `WORKER_HEARTBEAT_STALE_LOCK_HELD`), exits `1`, changes nothing. |
| Prior stop was **terminal** (`rearmRequired=true`) | Logs `WORKER_RECOVERY_REFUSED`, exits `2`. Manual `Rearm-` after diagnosis. |

**Exit codes.** `0` published or already running; `1` transient failure (network, timeout, I/O, exhausted deferrals, lock held); `2` configuration or attestation failure (invalid JSON, certificate/scope/token failures, HTTP 401/403, invalid arguments, refused recovery).

**Task Scheduler semantics.** The task restarts up to **3** times, **5 minutes** apart, when the action ends with a non-zero code; `AtStartup` fires after a **3 minute** delay so the network, SCOM SDK and certificate store are ready after a reboot. `IgnoreNew` prevents a second instance. Task Scheduler cannot filter restarts by exit code, so "not restartable" (`2`) is enforced by the persisted gate: restarted instances refuse immediately with `WORKER_RECOVERY_REFUSED` (exit `2`) without touching the source, and use up the three restarts. A worker that exits `1` after the retries is not restarted again until the next startup or a manual `Start-`.

**Heartbeat scope.** The heartbeat is written by an in-process timer. It proves the process is alive and not hung; it does not prove collection is progressing (collection is bounded by the scheduler's deadlines and attempt budgets, and data freshness is confirmed with `get_source_status` or `GET /healthz/freshness`).

**Windows Event Log** (source `ScomMcpWorker`, Application log): `1001` worker started; `2001` stale marker recovered or restartable (transient) stop; `3001` non-restartable failure or `WORKER_RECOVERY_REFUSED`. Messages carry only fixed codes and reasons. Event writes are best effort; a missing source never stops the worker.

**Freshness endpoint.** `GET /healthz/freshness` on the receiver is anonymous and returns `{"status","ageSeconds"}` only: `200` `fresh`; `503` `stale`, `expired` or `unavailable` (no committed dataset). Thresholds are the receiver's `FreshMinutes` and `UnavailableMinutes`. It is mapped only when `ReadModel:Enabled` is true (otherwise `404`). Use it for external monitors; `/healthz` still reports only that the site is up.

**State file upgrade.** The lock file is now `worker.lock` (previously `worker.runtime.lock`). An older worker's lock is refused while held. Existing 12-field `worker.status.json` files remain valid.

## Logs and state

| What | Where | Notes |
|---|---|---|
| Worker runtime log | `<STATE_DIR>` | Three 4 MiB rotating files; `worker.status.json` reports `state`, `failureCode`, `rearmRequired`, `processId`, `processStartTimeUtc`, `bootId`, `hostName`, `heartbeatUtc`; `worker.lock` is the single-instance lock. State directory must be new, empty, local NTFS and protected for SYSTEM, Administrators and the worker. |
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

## Lifecycle catalog and connector tool list

The OS lifecycle catalog (`windows-server-fixed-2026-11-02.1`, verified 2026-11-02) is
**compiled into `ScomMcp.dll`**. There is no separate catalog file to load. It changes only when
you install a newer build. `Lifecycle.MaxCatalogAgeDays` (default 90) makes the tool report
`CATALOG_STALE_OR_NOT_YET_VERIFIED` once the catalog is older than that, so plan to install a
newer build before then. `list_os_support_assessments` is registered by the receiver build itself
and appears in the server's tool list without extra configuration; it assesses only devices whose
captions are in the read model.

If the agent cannot see the tool:

1. Confirm the installed build contains it (the tool name is present in `ScomMcp.dll`).
2. In Copilot Studio, **Tools** > the connector > **Refresh**; Copilot Studio caches the tool list
   at connector creation and refresh.
3. Add `list_os_support_assessments` to the agent's tools and to its instructions
   ([agent-instructions.md](../examples/agent-instructions.md)).
4. Ask a lifecycle question and confirm in the activity map that the tool was called.

## Recovery

| Symptom | Action |
|---|---|
| `READ_MODEL_EXPIRED` | No fresh dataset within the budget: check the task and `worker.status.json`. After a reboot the worker recovers on its own ([procedure](troubleshooting.md#after-a-server-restart-worker-self-recovery)); rearm only if gated (`WORKER_RECOVERY_REFUSED`), or lengthen `FreshMinutes`. |
| Worker gated (`rearmRequired`, `WORKER_RECOVERY_REFUSED`) | Fix the cause, `Rearm-ScomWorkerTask.ps1 -AcknowledgeFailure`, `Start-ScomWorkerSetup`/task start. |
| Setup interrupted | `Start-ScomSetup.ps1 -Step Recover`; it does not clear a pending action; see [install-cli.md](install-cli.md). |
| Certificate rotation | Add the new cert to the publisher app, install in `LocalMachine\My`, grant private-key read, update the thumbprint in `worker.json`. |

Codes and fixes: [troubleshooting.md](troubleshooting.md). Source README:
[`Operations/DurableIndexedRefresh/README.md`](../Operations/DurableIndexedRefresh/README.md).
