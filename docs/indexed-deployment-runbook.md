# Indexed deployment: from package to working inventory

This is the ordered operator runbook for running the worker and MCP application
as separate processes on **one MCP server**, reading an existing SCOM server
remotely. No collector .NET runtime installation is needed on the SCOM server.
The indexed path is opt-in; the legacy snapshot path remains supported separately.

**Staging files does not activate settings. Connecting Copilot Studio does not
start collection. Inventory becomes available only after a worker dataset commits.**
Do not treat this guide or a verification flag as production certification.

**Actual SCOM targeted-live verification is unavailable:** no certified bounded
source provider is shipped. `TargetedLive.Enabled` and worker
`liveChecks.enabled` must remain false until separately reviewed provider/source
acceptance. The implemented request/result transport is not a working production
SCOM live-check adapter. See [limits and blockers](targeted-live-verification.md).

## 1. Record the deployment plan

Have the responsible administrators approve scope, identities, filesystem
permissions, application activation and the initial source read before performing
those actions. Use a protected deployment record outside source control.

| Item | Record privately |
|---|---|
| MCP/worker host | One chosen Windows host; install and run the worker here |
| SCOM server | Remote management-server FQDN and supported build/update level |
| Source scope | Management-group GUID and approved group GUID; whole-management-group collection requires explicit separate approval |
| Worker Windows identity | Tested read-only SCOM account |
| Receiver identity | Effective IIS application-pool account |
| Reader identity | Delegated API scope and authorized immutable user object IDs |
| Publisher identity | Tenant, worker client ID, enterprise-app service-principal object ID, API resource URI and certificate thumbprint |
| Package | Reviewed version and complete installed server/collector paths |
| State | Separate local worker checkpoint and receiver database paths |
| Recovery | Protected configuration backup and application-specific recovery procedure |

Use the approved group as the default customer scope. Do not copy a lab's
whole-management-group approval into another environment.

## 2. Prepare the MCP server

Follow [build and packaging](../README.md#build-and-validate) on an approved build
host. Stage the **complete** packaged server output and collector dependencies in
protected locations on the MCP server, not over a running application.

On the MCP server, verify:

```powershell
hostname
dotnet --list-runtimes
Get-Module -ListAvailable -Name OperationsManager |
    Select-Object Name, Version, Path
```

The application and worker require compatible, serviced .NET 8 and ASP.NET Core 8
runtimes. IIS also requires the appropriate Hosting Bundle. The worker requires
Windows PowerShell 5.1 and a compatible OperationsManager module/SDK. Install
**Operations console only** from approved SCOM media on this host if needed.
See [host prerequisites](../README.md#two-server-deployment-indexed-path).

**Completion check:** the chosen MCP host has all prerequisites and package
files. A copy left on another host does not count; do not run it or delete it as
part of this procedure.

## 3. Establish the three separate identity paths

| Path | Purpose | Required evidence |
|---|---|---|
| Worker Windows account to SCOM | Read approved source data through the SDK | Connection and independent scope-visibility/query-contract evidence under this exact account |
| Worker certificate to ingestion API | Publish datasets | Existing machine certificate, private-key access, Entra credential and application-role assignment |
| Delegated user to MCP | Read authorized retained data | Valid reader OAuth configuration and immutable user authorization |

Use [publisher identity setup](../README.md#publisher-identity-setup-entra-and-windows)
and [reader OAuth setup](../README.md#azure-iis-and-copilot-studio-reader-setup).
Do not recreate working identities merely because data is unavailable.
Do not put Windows passwords, OAuth secrets, tokens or private keys in commands,
configuration examples, diagnostics or source control.

Open Windows PowerShell 5.1 as the intended worker account through the approved
Windows mechanism and verify identity and remote connection:

```powershell
[System.Security.Principal.WindowsIdentity]::GetCurrent().Name
Import-Module OperationsManager -ErrorAction Stop
New-SCOMManagementGroupConnection -ComputerName 'scom01.example.com' -ErrorAction Stop
```

The FQDN is a placeholder. This connection check does not prove complete scope
visibility. Metadata reports and preflight scripts do not publish inventory.
Follow the [acceptance checklist](../README.md#deployment-acceptance-checklist)
for source-specific evidence; leave unverified gates false.

## 4. Prepare two complete private configurations

Start from `appsettings.ReadModel.example.json` and
`appsettings.Worker.example.json` (under `ScomMcp` in source or `server` in the
packaged distribution). They deliberately
contain synthetic values and disabled verification/projection settings.
They are structures, not ready-to-run configurations.

**Receiver:** preserve existing approved `Auth` settings. Configure
`Source.SnapshotEnabled=false`, `ReadModel.Enabled=true`, and
`ReadModel.WindowsToolsEnabled=true` for the six Windows tools. Supply the reviewed
Windows projection hosting settings and the correct publisher **service-principal
object ID**, not its app-registration object ID or client ID.

**Worker:** configure the installed PowerShell executable, profile script,
reviewed installed script SHA256, protected dependencies, remote SCOM FQDN and
local module path. Supply the matching approved scope, source contract evidence,
Windows property/unit/hosting mappings, publisher identity and checkpoint path.

Review these paired settings before activation:

| Settings | Required agreement |
|---|---|
| Management-group ID, scope kind, group ID | Same on both sides; group ID is null only for explicitly approved `managementGroup` scope |
| Property exposure | Worker `allowedPropertiesByClass` and receiver `AllowedPropertiesByClass` approve the intended fields for actual discovered class names; base-class mappings are not automatically inherited |
| Windows mapping | Verified properties, units, typed relationships and monitoring-role mapping; collect the corresponding hosting relationships |
| Metrics | Start with worker `metricMappingsByClass={}` and receiver `AllowedMetricsByClass={}` until metric acceptance is separately complete |
| Freshness | `Source` and `ReadModel` fresh/unavailable settings match |
| Budgets | Reviewed source, subprocess, reconciliation, publisher, receiver, session/deadline and disk limits accommodate the approved workload without silent truncation |
| Publisher endpoint | HTTPS application base URL, including any IIS application prefix; omit `/mcp` and `/ingestion` |
| Identity resource | API Application ID URI; omit `/.default` because the worker adds it |
| Local storage | Worker checkpoint DB and receiver DB are separate; neither is a shared network database |

Do not guess class/property names, conversions or SDK methods. Reuse verified
evidence only for its documented environment/account context. Protect dependencies
as well as the script: its SHA256 alone does not validate imported modules.

Save worker JSON as **UTF-8**, preferably without a BOM for compatibility with
older packages. Current code accepts an optional UTF-8 BOM, not UTF-16. Do not
rely on Windows PowerShell 5.1's default `Out-File` encoding. For an approved
new private file, an explicit write from an existing in-memory settings object is:

```powershell
# Replace the placeholder with the approved private configuration path.
$json = $workerSettings | ConvertTo-Json -Depth 32
[System.IO.File]::WriteAllText(
    'C:\ProtectedWorker\worker.json',
    $json,
    [System.Text.UTF8Encoding]::new($false)
)
```

`$workerSettings` must already contain the complete reviewed configuration.
This is not a template-population command or permission grant; use a protected
destination and never overwrite an active file without backup and approval.
In JSON, Windows backslashes must be escaped (`"C:\\ProtectedWorker\\worker.json"`).
Keep disabled metric maps as `{}`, not an empty class-name entry.

**Completion check:** both complete JSON files are staged privately on the
intended host, their paths and installed hashes are recorded, and the review
contains no unresolved configuration or source-contract gaps.

## 5. Prepare least-privilege storage permissions

With explicit permission-change approval, preserve SYSTEM and Administrators
access and provision:

| Principal | Location | Access |
|---|---|---|
| Worker account | Worker executable, private config, profiles and modules | Read/Execute; no worker write access |
| Worker account | Separate checkpoint directory | Modify |
| Worker account | Existing publisher certificate private key | Required signing access only |
| Effective IIS account | Receiver application/configuration | Read/Execute |
| Effective IIS account | Separate receiver database directory | Modify, including SQLite journal files |

Do not grant the worker broad write access to application/profile files, run IIS
as the SCOM account, or grant IIS access to worker checkpoints. Provision any
approved logging directory separately. Use the actual effective pool identity,
not an assumed account.

## 6. Activate the receiver, not just its staged file

During the approved application maintenance window:

1. Back up active settings to a protected location outside the web-served directory.
2. Merge the reviewed receiver settings into the configuration actually loaded by
   the IIS application. Preserve existing reader `Auth` settings and unrelated
   approved hosting/logging settings.
3. Check environment-specific JSON and IIS/process environment overrides. A file
   under a private staging directory is **not loaded automatically** by this app.
   Empty objects do not erase nested keys inherited from other providers: remove
   unintended metric keys at their actual origin rather than assuming `{}` masks
   them. For management-group mode, current code normalizes the JSON provider's
   empty-string representation of null. On an older package, omit `GroupId` from
   all effective providers and verify the bound value is null; do not change scope.
4. Recycle only the intended dedicated application pool, not all IIS or the VM.
5. Confirm effective snapshot=false, indexed=true and Windows tools=true settings,
   expected database path and normal application startup.

Check health using the **configured HTTPS hostname, port and application prefix**,
with normal certificate validation. For an application hosted at the synthetic
base `https://mcp.example.com/monitoring`, use:

```powershell
(Invoke-WebRequest -UseBasicParsing -Uri 'https://mcp.example.com/monitoring/healthz' -ErrorAction Stop).StatusCode
```

Expect HTTP 200. Do not substitute `http://localhost` unless that binding is
explicitly configured for the same application. A probe that records `0` has not
reported an HTTP status: retain the actual transport exception or HTTP response.
For IIS `500.30`, inspect the startup validation error before investigating SCOM,
certificate permissions or SQLite writes. Do not treat inspection of JSON flags
alone as proof that the application started.

**Completion check:** the receiver is using the indexed path. Until publication,
an unavailable result is expected; `/healthz` returning 200 is not acceptance.

## 7. Publish once as the worker account

On the MCP server, in the intended worker account's shell, run the complete
installed package with its private worker configuration:

```powershell
dotnet 'C:\ApprovedApps\ScomMcp\ScomMcp.dll' --worker-once 'C:\ProtectedWorker\worker.json'
$LASTEXITCODE
```

These are placeholder paths. Do not run the legacy snapshot collector to populate
the indexed database. Do not print access tokens or certificate assertions.
The actual worker run checks its own certificate signing/token path; a separate
manual token test is not a substitute.

For this first run, use an approved interactive shell opened with **Run as
different user** for the worker account. Confirm
`[System.Security.Principal.WindowsIdentity]::GetCurrent().Name` before execution.
Do not create a scheduled task just to launch the acceptance test. If task
registration returns `0x80070005` (Access denied), that is a launcher failure,
not a worker execution result. Use the approved interactive method rather than
granting administrator rights, changing task permissions or reconfiguring IIS.
Use the recorded installed DLL path, not a guessed path or the example above.

**Completion check:** worker exit code is zero and ingestion committed the intended
dataset. A certificate check, SDK connection, legacy collector `ok`, accepted batch
or created SQLite file alone does not prove a successful commit.
`SCOM_WORKER_PUBLISHED` is emitted after publication and checkpoint
acknowledgement; still complete the authenticated reader checks below.

## 8. Accept the inventory before enabling recurrence

Using an authorized delegated reader, call:

1. `get_source_status`: record committed dataset ID, timestamps, expected/discovered
   computer counts and explicit coverage limitations.
2. `list_devices`: verify intended membership, deterministic pages and the same
   dataset while paging. Restart paging if the dataset changes.
3. `get_inventory_summary`: compare counts with the approved scope evidence.
4. `get_device_details`: inspect a known authorized computer and its volumes.

Then test through Copilot Studio. The agent must distinguish monitored inventory
from current connectivity, expose unknown/partial values and avoid unsupported
OS-lifecycle conclusions. With metrics disabled, utilization being unavailable
and capacity usage being unknown are expected, not reasons to fabricate values.

For `list_os_support_assessments`, follow the
[declared Windows Server profile](os-lifecycle-assessment.md). Confirm the effective
`Lifecycle` thresholds, catalog version/verification age, exact observed captions,
unknown/missing counts and Microsoft citations. Stop lifecycle acceptance if the
catalog is stale or applicability is ambiguous; inventory may still be useful.
Do not overwrite captions, raise the freshness ceiling, assume ESU entitlement or
modify source collection to force an assessment. This check is read-only and does
not replace the separate source/identity/recurrence gates.

**Completion check:** record results and limitations in the private deployment
record. Do not call the deployment successful merely because the agent connects.

## 9. Enable supervised recurring execution

After initial one-shot publication and read-model acceptance, use the separate
operational approval for recurring source reads. The opt-in task host is
documented in [`Operations/DurableIndexedRefresh`](../Operations/DurableIndexedRefresh/README.md).
It packages reusable task setup/inspect/start/stop/unregister commands; it does
not install or run a task until an operator invokes it. Select and document the
worker identity, recurrence and freshness budgets before registration.

The schedule is in private worker configuration. Set `schedule.interval`,
`attemptDeadline`, `failureBackoff`, `maxFailureBackoff` and
`maxConsecutiveDeferredAttempts` against measured collection duration and the
receiver's `Source`/`ReadModel` expiry budgets. The consecutive deferred-attempt
limit is bounded to 1..100 (example default 5). A deferred attempt is retried
only after reconciliation and bounded exponential backoff; exhaustion or any
unexpected error stops the loop and requires operator investigation. Do not
repeat `--worker-once` on a timer or widen freshness thresholds to hide a stop.

Prepare a new empty local runtime-state directory separate from the checkpoint
database, and register the long-running worker under the exact approved account.
The installer rejects built-in identities, protects the runtime-state directory,
sets Task Scheduler duplicate handling to `IgnoreNew`, requests one startup
recovery after restart, and configures zero automatic task restarts. This avoids
restart storms. Deferred attempts retry only inside the same worker process,
after reconciliation and bounded backoff; Task Scheduler never retries a
failed process. Windows-protected Task Scheduler credential storage is used for the
supplied operator credential; the password is not embedded in scripts,
configuration, command-line arguments, logs or source control. Follow the
organization's approved script signing policy and do not bypass it.

```powershell
$workerCredential = Get-Credential -UserName 'DOMAIN\approved-worker'
& 'C:\ApprovedPackage\Operations\DurableIndexedRefresh\Register-ScomWorkerTask.ps1' `
    -WorkerDllPath 'C:\ApprovedApps\ScomMcp\ScomMcp.dll' `
    -WorkerConfigPath 'C:\ProtectedWorker\worker.json' `
    -StateDirectory 'C:\ProgramData\ScomMcpWorker\runtime' `
    -Credential $workerCredential
```

The task runs the existing fixed-delay `--worker` scheduler; it does not create
queued or overlapping scans. Registration does not start collection immediately;
it runs at system startup unless the operator explicitly supplies `-StartNow`.
Use `Get-ScomWorkerTaskStatus.ps1` to inspect task liveness, deferred/stopped
state, rearm gate and last publication time. `Stop-ScomWorkerTask.ps1` creates
the protected cooperative stop request and waits for a recorded clean stop; it
does not forcibly terminate the worker. If the task does not stop or its status
is terminal/unknown, preserve the status/logs and investigate. A stop request
cannot guarantee cancellation of work already accepted by a remote/backend
system. `Unregister-ScomWorkerTask.ps1` and
`Start-ScomWorkerTask.ps1` target only the marked task; unregister preserves
logs, status and checkpoint data. The task still gets its normal startup
invocation after reboot, but before reading configuration or attempting any
source work the worker checks the durable runtime status. A known clean stop
allows startup; a terminal failure remains gated across reboot. Power loss or
an unexpected process exit leaves a prior `running` record, which is treated
as an unknown outcome and gated as well. A malformed or unknown status is not
repaired or overwritten automatically.

After diagnosis and reconciliation of any ambiguous publication, explicitly
rearm the exact stopped, owned task:

```powershell
.\Rearm-ScomWorkerTask.ps1 -AcknowledgeFailure
.\Get-ScomWorkerTaskStatus.ps1
.\Start-ScomWorkerTask.ps1
```

Rearm validates the ownership marker and exact worker action, refuses while
Task Scheduler reports the task running, and acquires the worker's exclusive
runtime lease before updating only the gate fields. It preserves publication,
checkpoint, logs and the last failure code. Normal start never rearms silently.
Run it only from an operator context authorized to administer the exact task
and modify its protected runtime-state directory.
If status is malformed, stop and inspect protected state; do not edit or delete
it to bypass the gate. Rearm does not establish that a remote/backend operation
was cancelled or that publication succeeded; retain evidence and reconcile
before resuming collection.

The worker's last publication timestamp and the task's `Running` state are
separate signals. Use authenticated `get_source_status`, coverage and the
configured freshness budgets to decide whether data is still available and
current. Define operator alerts for stopped/deferred status, stale published
data, disk capacity, certificate rotation and protected backups. Verify a later
committed dataset and membership refresh; do not widen expiry or clear/reseed
checkpoints as recovery.

## Troubleshooting without restarting the whole setup

| Evidence | Meaning and next check |
|---|---|
| `SNAPSHOT_OR_STATUS_INVALID_OR_UNREADABLE` | Legacy snapshot-reader error. Inspect effective source selection and routing first; staging indexed files did not activate them. If legacy mode is intended, inspect its files instead. |
| `READ_MODEL_LATEST_COLLECTION_UNAVAILABLE` | Indexed reader has no currently available committed run. Inspect worker/ingestion outcome; do not serve an older run as current. |
| `READ_MODEL_EXPIRED` | Domain timestamps exceeded availability policy. Inspect stopped/failed collection and cadence rather than blindly widening thresholds. |
| Worker nonzero exit | Stop at the failed step; record sanitized event/type/code and investigate approved local diagnostics. Do not recreate identities or enable gates by guesswork. |
| `WORKER_CONFIGURATION_JSON_INVALID` | Private worker JSON syntax or typed deserialization failed. LineNumber and BytePositionInLine are zero-based; byte position is not a character index. Check escaped Windows backslashes, quoting, value types, comments and trailing commas without sharing private file contents. Older packages report only a generic JSON exception. |
| Raw-byte JSON failure at line 0, byte 0 | Check encoding without displaying private contents. Older workers reject a UTF-8 BOM, even when text-decoded JSON validation passes. Current source accepts an optional UTF-8 BOM; UTF-16 is not supported. For an older installation, use an approved, backed-up BOM-only correction and repeat raw-byte validation; do not change configuration values or ACLs. |
| Management-group `GroupId` fails despite JSON null | The .NET 8 JSON configuration provider binds null to an empty string. Current source normalizes only that empty value to null for management-group mode. Older packages require an approved code update or omission of the key from all effective configuration providers, followed by bound-value validation. Do not write the string `"null"` or change scope. |
| `Register-ScheduledTask` access denied before launch | The worker has not run. Use the approved interactive worker-account shell for the initial one-shot test; task/service provisioning is a separate operational step. |
| HTTP 401/403 | Check the relevant reader or publisher identity path separately; successful reader OAuth does not establish publisher authorization. |
| IIS startup failure | Check effective settings, installed runtimes/Hosting Bundle, protected storage access and approved startup diagnostics. |
| Localhost health fails but configured HTTPS health is 200 | The localhost route did not verify the configured endpoint. Do not recycle or change authentication to repair an unrelated binding; continue acceptance using the approved HTTPS URL. |
| Healthy endpoint, no inventory | Health proves HTTP startup only. Require a commit and authenticated tool acceptance. |

On any activation failure, stop and retain the protected backup and sanitized
evidence. Do not automatically delete databases, clear checkpoints, change pinned
scope policies, kill unrelated processes or restart the VM. Execute only the
reviewed recovery procedure; restoring configuration does not undo permissions,
certificate changes or data-state changes. Do not search for or rerun unknown old
deployment scripts as a generic recovery step.
