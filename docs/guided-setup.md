# Guided, resumable setup (evaluation installer)

## Double-click quickstart

Download the reviewed CI evaluation artifact, verify the supplied SHA-256, then
extract **the entire `scom-evaluation.zip`**. Open the extracted `ci-evaluation`
folder and double-click **`ScomSetup.exe`**. Keep the executable, its bundled
runtime DLLs, `server`, `collector`, `setup` and documentation together; copying
only the EXE is not supported. This is a genuine Windows Forms application with
its own x64 .NET runtime, not a renamed PowerShell script or a console launcher.
It can open before the application's .NET 8 Hosting Bundle has been installed.
Windows Server **Desktop Experience** is required for the graphical wizard;
Server Core uses the separate console lane below.

The wizard opens directly to automatic **read-only prerequisite checks**, without
host changes or SCOM connections. Each completed check is shown in green for PASS
or red for BLOCKED, with a remedy. Unknown or failed checks are never marked green.
Use **Back / Next** for the sequential screens. Normal setup asks for a log folder
(separate MCP/collector subfolders), safe IIS placement, the approved execution
account and SCOM server/scope. Installation/private/state paths and a new dedicated
pool are proposed; detailed overrides, certificate/HTTPS evidence, reviewed JSON
and resume options live in **Advanced**, not a long initial form. Proposed defaults
do not grant permission, override existing paths or certify external evidence.
If required, use the explicit elevation action and approve the genuine Windows
UAC prompt yourself, then rerun the checks.

Existing IIS sites are discovered for a **new** application. Existing applications
and shared pools are not silently reused or overwritten. A dedicated new pool is
proposed; conflicts still fail the original ownership checks. Missing HTTPS
certificate/hostname, Entra/source evidence or approved configuration is summarized
as a real blocked gate, not made green by hiding its inputs in Advanced.
Use **Discover groups** only after approving the bounded read-only source
connection; the management-group GUID is obtained from that connection rather
than typed in normal setup. Then choose a group or separately confirm all supported
Windows inventory. Review the final proposal and explicitly confirm **Prepare**.
Configuration/activation/worker/publication/acceptance remain separate gated
actions, never automatic consequences of navigation.

The local SCOM checks safely parse the installed OperationsManager manifest and
inspect SDK assembly identities/version metadata; they do not execute the module,
verify a connection, prove source authorization or certify the actual SDK query
contract. The console's presence alone is insufficient. A missing/broken manifest
or SDK fails locally; actual type/query compatibility and scope identity are
checked only in the separately approved source lane. Assembly identity metadata
is not publisher-signature verification.

The selected `DOMAIN\sp_service`-style account is the **collector/worker** identity:
it determines the plan, private/worker/log ACL grants and exact-account worker
gate. The IIS MCP application deliberately retains a separate dedicated
`IIS AppPool\<new-pool>` virtual identity. Entering an account does not change the
identity of the current wizard, provision a domain account, store a password or
approve recurring work. Use the explicit Windows account handoff when the
worker lane is needed; Windows handles credentials outside the wizard. Host
actions still require a separately elevated administrator.

The prerequisite page can install only explicitly selected IIS features and an
operator-supplied Microsoft Hosting Bundle whose official signature, version and
independently approved SHA-256 pass the existing verifier. No installer is
downloaded. Supported OS servicing, SCOM console/SDK installation, approved script
signing, reboot/IIS maintenance, Entra grants/certificates and reviewed source
mapping/visibility/query contracts are real customer prerequisites. The wizard
identifies these gates and accepts the separately reviewed identity/receiver/worker
JSON files; it does not invent source evidence or silently grant cloud access.

Use the wizard's resume/status action with the same protected journal location.
An interrupted action stays pending until operator review; closing or cancelling
does not claim rollback. While an approved operation is active, wait for its
reported result rather than terminating setup. For the worker identity lane,
open `ScomSetup.exe` through the customer's approved existing-account mechanism
as the **exact selected worker account**, choose its installed private
`setup-plan.json`, then explicitly connect or publish once. The GUI does not
capture credentials, impersonate the worker or require that account to read the
administrator-only host journal. Return as the host administrator to record the
receipt and reader acceptance. Durable task registration and recurrence remain
separate opt-in operations, not a consequence of double-clicking.

**Trust/signing limitation:** this evaluation EXE and repository scripts do not
carry a customer-trusted publisher signature. A self-contained EXE does not
establish trust or override PowerShell `AllSigned`/RemoteSigned, UAC, SmartScreen,
certificate, antivirus or unsafe-content warnings. The native GUI invokes the
existing Windows PowerShell 5.1 scripts using normal `-File` execution with no
policy override; customer-approved signing of the scripts/modules may be needed
before checks/actions can run. Have the customer's authorized signing/review
process approve the EXE and scripts. If Windows blocks it, stop and use that
process; do not bypass the warning. No embedded credentials or fake signing
certificate are supplied.

Offline CI starts the packaged EXE with shared .NET lookup paths disabled and
checks that its loaded runtime is the packaged runtime and the native form can
be constructed without invoking the bridge. That is a loader/entrypoint proof,
not a completed clean-server installation or production certification.

## Console lane and detailed contract

The console wizard stages an **opt-in indexed** on-premises installation and
keeps customer Entra provisioning separate. It does not provision an Azure VM:
an existing approved customer Windows host is sufficient. ARM/Bicep is not an
Entra application-registration/consent mechanism. Existing legacy deployments
and their tools are unchanged.

This is an evaluation installer, **not source certification or an unattended
production installer**. No live IIS/SCOM/Graph acceptance is implied by offline
tests. Every effect requires typing `APPROVE`; any other response cancels before
that effect. `Preflight` (the default) and `Status` are read-only. There are no passwords, automatic credential
prompts, script-managed downloads, execution-policy changes, broad group creation or recurrence.

## Before starting

Use the complete reviewed [evaluation package](../README.md#build-and-validate),
not a single DLL. On the customer host, open **64-bit Windows PowerShell 5.1**
through the approved administrator mechanism. Have the customer signing process
approve/sign the scripts and modules; never bypass `AllSigned`, OS authentication
or unsafe-content warnings. This repository does not ship a customer-trusted
signing certificate.

The metadata prerequisite checks recognize Windows Server 2019/2022/2025 build
families (17763/20348/26100), .NET and ASP.NET Core 8 runtimes, a registered .NET 8
Windows Server Hosting Bundle plus ANCM V2, IIS management tools, an existing
OperationsManager module path and common pending-restart indicators. They do
**not** establish patch currency, SCOM build/SDK support or complete reboot
readiness. Missing/ambiguous prerequisites block; install from approved Microsoft
media through the platform process and resume after any required restart.
The default prerequisite check does not install anything. The separately opt-in
`InstallPrerequisites` step below can install explicit IIS features and an
operator-supplied approved local .NET 8 Hosting Bundle. It never acquires an installer.

Use serviced versions approved for your OS/SCOM combination. See
[Microsoft IIS prerequisites](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-8.0)
and the [existing two-server deployment guidance](../README.md#two-server-deployment-indexed-path).
SCOM SDK/Operations console installation, firewall/DNS, public HTTPS reachability,
certificate enrollment/private-key permissions and source contract/mapping review
remain customer responsibilities. Do not guess SDK signatures or source flags.

Installation, private configuration, receiver state, worker state, MCP logs,
collector/worker logs and the private journal must be separate, nonoverlapping
local paths. Both log destinations must be on fixed local **NTFS** volumes.
Targets must be **new**
directories with existing parents, not drive roots, Windows directories, network
drives, reparse points or an existing application. Setup refuses existing
pool/site/application conflicts rather than overwrite a working deployment.
For upgrades or existing-app migration, use the
[manual runbook](indexed-deployment-runbook.md) with a maintenance/rollback review.

## Stage 1: private plan, prerequisites and bounded connection

All paths below are illustrative. Replace them with approved customer paths in
the customer's private shell; never paste private configuration into a PR.

```powershell
.\setup\Initialize-ScomSetupState.ps1 -Path 'C:\ApprovedState\ScomSetup'
```

Initialization is its own explicitly approved private-directory action, not an
application install. Run the read-only prerequisite check before staging:

```powershell
.\setup\Start-ScomSetup.ps1 -StateDirectory 'C:\ApprovedState\ScomSetup' -Step Preflight
```

The wizard displays PASS or each BLOCKED prerequisite code. Any failure stops
before saving a deployment plan, taking a mutation lease or creating installation
directories. Preflight does not connect to SCOM, enumerate inventory, install
prerequisites, register a task or start the application. An existing complete
`-PlanPath` can supply fields for this check; passing preflight never installs.

If IIS/Hosting Bundle is missing, the authorized host administrator may explicitly
opt into installation **before Prepare**, in an approved host maintenance window:

```powershell
.\setup\Start-ScomSetup.ps1 -StateDirectory 'C:\ApprovedState\ScomSetup' -Step InstallPrerequisites `
    -InstallIis -HostingBundlePath 'C:\ApprovedMedia\dotnet-hosting-8.0.22-win.exe' `
    -HostingBundleVersion '8.0.22' -HostingBundleSha256 '<independently-approved-64-hex-SHA256>'
```

The version is illustrative, not a recommended current patch. The customer must
select a supported, serviced **8.0.x** Hosting Bundle from
[Microsoft's official versioned downloads](https://dotnet.microsoft.com/en-us/download/dotnet/8.0),
independently validate that artifact against Microsoft's release checksum/signature,
and record its approved SHA256 privately. A hash computed from an untrusted file
alone does not establish trust. The script compares the mandatory independently
approved SHA256, exact `dotnet-hosting-<version>-win.exe` name and ProductVersion
prefix, valid Windows Authenticode status and exact Microsoft Corporation signer CN.
Unsigned/invalid/ambiguous version or checksum evidence blocks **before any OS
change**. The bounded local executable must not traverse reparse/network paths.
The Windows adapter rechecks it under a read-only file lease immediately before
launch; no warning, UAC credential or signing policy is bypassed.

`-InstallIis` installs only missing `Web-Server`, `Web-Mgmt-Console` and
`Web-Scripting-Tools`, their required dependencies and management tools through
the supported local ServerManager cmdlet. It does not request all subfeatures,
remote machines, credentials or `-Restart`. Windows servicing may obtain payloads
according to the **customer's existing source policy**; this script neither
downloads files nor overrides that policy. Unavailable feature/media contracts
stop explicitly. Existing IIS features are not removed or reconfigured.

IIS must be present before the bundle. The approved bundle runs `/install`
(or `/repair` if that exact bundle is registered), `/quiet /norestart`, with a
private `/log`; runtime/shared-framework/ANCM opt-outs are explicitly disabled,
x86 support retained and shared-configuration safety checks preserved.
The administrator separately approves **each** effect summary. A repair is needed
when IIS was added after a preexisting bundle; a requested rerun requires a fresh
explicit repair approval, never a hidden retry. Installer logs/temp are directed
to the private journal directory using restored process-local environment settings.
This is a host-wide runtime/module install: it may affect existing applications
and is not limited to the new MCP pool.

Exit 0 is followed by installed-version/runtime/ANCM probes, not treated as proof
of deployment acceptance. Exit 3010 or IIS `RestartNeeded=Yes` records a required
restart and pauses before further effects. All other exits (including unexpected
1641), exceptions and the 30-minute installer wait timeout retain the pending
journal. Timeout never forcibly kills the installer; its PID is recorded for
customer inspection and the process may still be running.
No OS restart, global IIS/WAS/W3SVC restart, component uninstall or rollback is
performed. Even exit 0 can require approved IIS maintenance to reload ANCM/PATH.
Reopen the shell and perform that customer maintenance before `Prepare`.

After an explicitly required OS restart, `-AcknowledgeRestart` only records a
detected **newer UTC boot** with cleared common restart indicators, with another
confirmation. It never initiates a reboot. Resume with the intended install
options if further effects remain. Interrupted-effect journals require authorized
inspection of installer/servicing outcome and private logs before manual journal
resolution; do not clear them to force another run.
SCOM console/media installation, customer script signing and servicing/support
approval remain manual prerequisites.

```powershell
.\setup\Start-ScomSetup.ps1 -StateDirectory 'C:\ApprovedState\ScomSetup' -Step Prepare
```

`Initialize` only creates one new private journal directory, owned by
Administrators and accessible only to SYSTEM/Administrators. The parent must
already exist. `Prepare` prompts for complete package/install/private/state
paths and separate MCP/collector log destinations, existing worker
`DOMAIN\account`, management-server FQDN, management-group GUID, module path, dedicated
new pool, site/application path, HTTPS application base URL and existing
LocalMachine My TLS certificate thumbprint. It saves the private plan with a
separate approval, only after prerequisite checks pass, and stages files with another
explicit effect summary. Use `-PlanPath` to supply the same JSON shape instead
of entering fields; it does not skip approval.

After prerequisites pass, interactive Prepare requests separate approval for
read-only group discovery using the console identity. It checks the selected
management-group identity, reads only InstanceGroup IDs/display names, caps the
SDK reader at 201 results, refuses more than 200 selectable groups, and enforces
a 60-second job deadline. Unsupported SDK contracts, timeout, ambiguity or missing
access fail closed; no inventory/members are read. A large catalog needs an
independently reviewed exact group plan via `-PlanPath`, not an unbounded retry.
Choose a displayed group number, or type `ALL` and separately approve the broader
**all supported Windows inventory in this management group** warning. This is
not every SCOM class and does not create or expand a group.

Plans now require `mcpLogPath`, `collectorLogPath`, `scopeKind` (`group` or
`managementGroup`) and boolean `wholeManagementGroupApproved`. Group plans require
a nonempty GUID and false whole-group approval. Management-group plans require
JSON `groupId: null` (not an empty/fake GUID) and true approval, and always prompt
again before initial plan persistence. Both reviewed receiver and worker configs
must already match the selected scope/IDs and whole-group approval; merge refuses
mismatches rather than silently changing source authorization. Whole-group
configuration also requires enabled Windows tools and a reviewed Windows mapping.
Setup stamps `inventoryKind: supportedWindows` on the source profile. Source
visibility, membership/enumeration and graph contract evidence are still separately
reviewed; setup never infers those flags from discovery or a successful preflight.

Prepare does **not** create or start IIS. The staged `appsettings.json` is `{}`,
deliberately unable to start anonymously or with synthetic credentials.
All installed package files are hash-recorded separately from mutable active
configuration. Worker config is read-only to the worker; only its checkpoint
and selected collector/worker log directory are writable. SYSTEM/Administrators retain access. IIS will get only
application Read/Execute and receiver-state Modify, not checkpoint/private-key
or worker configuration access. The selected MCP log directory gets Modify for
only the new pool identity at activation; collector logs get Modify for only the
worker identity during staging. Both include SYSTEM/Administrators, disable ACL
inheritance and verify the resulting owner/rules. No existing pool, application or
log directory is reused. Logs/backups/journals remain private.

## Structured operational logs

Setup writes `OperationalLogging` in receiver JSON, `operationalLogging` in worker
JSON and the same worker destination in the PowerShell profile configuration.
Runtime events are sanitized structured JSON, not serialized framework state or
exception messages. They include allowlisted lifecycle/failure/outcome/timing
information only: never bearer tokens, credentials, connection strings, private
configuration or raw SCOM records. Profile logs use their own file prefix and
never write diagnostic text to the JSON protocol stdout. Log I/O failures are
explicit failures, not silently discarded events.

The wizard selects a 1 MiB per-file limit and five retained files per stream.
The worker and its PowerShell profile share the collector destination but use
different streams; account for both budgets when sizing the volume. Reparse,
remote, overlapping or non-NTFS log destinations are rejected. Logging is not an
audit certification, and ACLs do not isolate logs from machine administrators.
Normal IIS/Windows event and approved prerequisite-installer logs are separate
platform diagnostics governed by customer retention policy.

## Durable refresh remains a separate opt-in

The complete package includes
[DurableIndexedRefresh operations](../Operations/DurableIndexedRefresh/README.md).
After reviewed one-shot publication and acceptance, use that lane to explicitly
approve registration and separately starting the durable worker under the exact
worker identity. Setup/preflight never registers a task, runs a collection or
enables recurrence. Metrics and targeted-live checks remain disabled. Bounded
runtime-state evidence from the durable worker is separate from the operational
logs above; preserve both when investigating interrupted work.

Open a separate approved Windows PowerShell shell **as the exact existing
read-only worker account**, not as the IIS pool or provisioning administrator:

```powershell
.\setup\Start-ScomWorkerSetup.ps1 -PlanPath 'C:\ApprovedWorkerConfig\setup-plan.json' -Step Connect
```

The copied plan is in the chosen private worker-config directory. After a
specific confirmation, the connection job imports the installed approved module,
connects to the chosen management server, checks the management-group identity
and reads **only the chosen group by GUID**. It stops after 60 seconds. It does
not read members, alerts or inventory. A visible group is not proof of complete
scope visibility/query contracts; no verification flag is set.

## Stage 2: separate authorized Entra provisioning

On the customer's authorized provisioning host, use an approved installed
`Microsoft.Graph.Authentication` module and an already authenticated
**Global cloud, delegated context for the exact tenant**. This script does not
install Graph, sign in, grant itself rights or change persistent accounts.
Provisioning administration and worker runtime are separate identities.

The provisioning context requires `Application.ReadWrite.All`. Requesting the
optional publisher role grant additionally requires
`AppRoleAssignment.ReadWrite.All` and the tenant's appropriate administrator
role/policy. These are provisioning rights, **never worker app permissions**.
Delegated reader consent remains a separately reviewed customer action, not
automatic all-principals consent.

```powershell
.\setup\Provision-ScomEntra.ps1 -TenantId '<approved-tenant-guid>' `
    -NamePrefix 'ApprovedScom' -PublicCertificatePath 'C:\ApprovedCerts\publisher.cer' `
    -StateDirectory 'C:\ApprovedState\ScomEntra' -GrantPublisherRole
```

Initialize that private journal directory first using `Initialize-ScomSetupState`.
Provide a bounded public **DER `.cer`**, never a PFX/private key. The matching
RSA-2048-or-greater private-key certificate must already be provisioned in
`LocalMachine\My` on the worker host with approved signing access under the exact
worker account. Reading a public certificate does not prove that access.

The script creates or reads three single-tenant apps: API resource with v2
tokens, `Scom.Read` admin-consent delegated scope and application-only
`Scom.Ingest` role; certificate-based publisher requesting only that role; and
Studio OAuth client requesting only that delegated scope. It creates/reuses
their service principals and, **only with the switch and a separate exact
confirmation**, grants the publisher that role on that API. It reads the
assignment back before producing ready identity settings.

Reuse requires explicitly reviewed **app-registration object IDs** via
`-ApiObjectId`, `-PublisherObjectId`, `-ReaderObjectId`. Names only detect
conflicts; they are not ownership evidence. A matching-name app without an ID
blocks duplicate creation. Reused scopes, roles, token versions, credentials and
permissions are read-only: incompatibility blocks instead of overwriting unrelated
settings. Successful reruns with those same IDs are no-op reads, apart from the
explicitly approved local identity-output write. Save created IDs from the
private journal for resumption.

Graph writes are journaled **before** dispatch. Interruption, denied permission,
ambiguous response or propagation delay stops with a pending journal, never a
success-shaped fallback or blind retry. No application/credential/consent is
automatically deleted on failure. For an interrupted create or URI assignment,
inspect the exact tenant objects and journal under the authorized admin process.
Resolve the pending record only after independently establishing its outcome;
finish an incomplete new API URI through approved administration if needed,
then rerun with explicit IDs. Do not clear a pending record to force retries.

`identity.json` contains only nonsecret tenant/API/client/service-principal IDs,
resource URI, token version, scope/role and certificate thumbprint. Its shape
is validated on import, but **file contents do not attest ownership, consent,
source rights or private-key access**.

For the reader, separately configure the **Studio-generated Web callback URI**,
protected OAuth client credential and scoped delegated consent using the
[reader setup](../README.md#azure-iis-and-copilot-studio-reader-setup). No secret is
created/exported by this script. Do not invent redirect URIs or use an
administrator/shared reader connection.

Graph v1.0 contracts:
[applications create](https://learn.microsoft.com/en-us/graph/api/application-post-applications?view=graph-rest-1.0),
[application update](https://learn.microsoft.com/en-us/graph/api/application-update?view=graph-rest-1.0),
[service principal create](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-serviceprincipals?view=graph-rest-1.0),
[publisher role assignment](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-approleassignments?view=graph-rest-1.0).

## Stage 3: configure, activate, publish once, accept

Prepare **complete reviewed** receiver and worker JSON from the package examples.
Fill the intended immutable reader-user grants, actual class/property/Windows
mapping and budgets, and independently established source-contract evidence.
The examples intentionally start with evidence flags false.
The wizard does not synthesize certification or set them true. Leave metric maps
empty and both live-check paths disabled. Windows tools require the reviewed
Windows mapping; a generic indexed setup does not imply all Windows tools work.

```powershell
.\setup\Start-ScomSetup.ps1 -StateDirectory 'C:\ApprovedState\ScomSetup' -Step Configure `
    -IdentityPath 'C:\ApprovedState\ScomEntra\identity.json' `
    -ReceiverSettingsPath 'C:\PrivateReview\receiver.json' `
    -WorkerSettingsPath 'C:\PrivateReview\worker.json'
.\setup\Start-ScomSetup.ps1 -StateDirectory 'C:\ApprovedState\ScomSetup' -Step Activate
```

Missing identity/config inputs pause `Configure` without effect. Configuration
import backs up staged settings privately, preserves reviewed caller grants and
source flags, binds paired identity/storage/scope, and pins the installed profile
hash. `Configure` may be repeated while stopped at `configured`, with another
approval and private backups. It refuses scope mismatches or enabled metric/live
guards. Only an unchanged reviewed configuration can activate or publish.

`--validate-setup <absolute-server-directory> <absolute-worker-json>` uses the
application's existing configuration binding/service validators plus paired setup
guards. It does not build a host, query SCOM, open SQLite, load a private key or
request a token. Exit 0 means configuration/source gates permit a future attempt;
exit 2 means source evidence is still required; exit 1 means invalid configuration.
It does not certify any self-declared source gate. Environment/Production
overrides affecting the protected settings block rather than silently broaden
effective scope/authentication. IIS inherited/global configuration still requires
the administrator's review; this CLI is not an IIS configuration simulator.

Activation creates **only new** dedicated resources. A new site or a new
single-segment subapplication under an approved existing HTTPS site is supported.
Existing subapplication bindings must match the exact host/port/certificate.
Pool and HTTPS-bound site/application are committed while stopped, with no
Managed CLR, Integrated pipeline and ApplicationPoolIdentity; permissions are
then granted and only that application starts. No existing bindings/apps/pools
are overwritten. TLS-valid, nonredirected health must identify this service.
Normal IIS pool/site auto-start is enabled only after that health check succeeds;
this is application hosting, not recurring collector execution.
Health is **not inventory or authenticated-reader acceptance**.

In the intended worker's separate approved shell:

```powershell
.\setup\Start-ScomWorkerSetup.ps1 -PlanPath 'C:\ApprovedWorkerConfig\setup-plan.json' -Step Publish
```

This is another explicit one-shot source approval, not hidden preflight work.
It runs `--worker-once`, under that account only, using the reviewed source and
publication deadlines. Before invocation it records an attempt. It records a
worker-owned operational receipt only after exit zero and
`SCOM_WORKER_PUBLISHED` (publication plus checkpoint acknowledgement).
A failed/interrupted attempt requires private checkpoint/journal review before
any new approved run; it never auto-reruns after a crash.

Back in the host administrator shell:

```powershell
.\setup\Start-ScomSetup.ps1 -StateDirectory 'C:\ApprovedState\ScomSetup' -Step Publish
```

The host lane only checks/records the matching worker receipt; it does **not**
execute another source run. The receipt is worker-reported evidence, not
cryptographic attestation or proof that Studio can read inventory.
Using individual-user authentication, follow
[reader/data acceptance](../README.md#azure-iis-and-copilot-studio-reader-setup):
tool discovery, source status, intended inventory/coverage, unauthorized-user
denial, pagination and stale/partial behavior. Then explicitly attest that evidence:

```powershell
.\setup\Start-ScomSetup.ps1 -StateDirectory 'C:\ApprovedState\ScomSetup' -Step Accept
```

`Accept` records operator attestation only. Recurrence is a **separate opt-in**
manual decision after acceptance. Metrics and targeted-live remain disabled;
no certified live SCOM provider ships in this package.

## Interruption and recovery

Exclusive journal/worker leases prevent concurrent setup runs. Each host/Graph
effect has a durable pending marker. State transitions cannot skip stages.
Plan/config/package drift and reused output directories block; no automatic
configuration restore, directory deletion or collector retry occurs.

For an activation or running-application failure:

```powershell
.\setup\Start-ScomSetup.ps1 -StateDirectory 'C:\ApprovedState\ScomSetup' -Step Recover
```

After another effect confirmation, recovery disables auto-start and stops only the pool if its exact
site/application path, pool assignment/identity and dedicated use still match.
It leaves every file, DB, site, certificate and journal intact. If ownership is
ambiguous or stopping fails, the warning explicitly says the app may be active;
the pending record is **not** cleared and no rollback is claimed.

For interrupted preparation/import, use private backups and the recorded plan to
inspect the exact partial files, ACLs and resources. A failed fresh prepare is
not automatically adopted; retain it for review and use new approved target paths
and a new private journal if restarting. For interrupted activation, stop/review
the exact owned resources before any administrator-authorized resource cleanup.
Restoring config requires a stopped app, independently reviewed backup and
revalidation. Never restore global IIS backups, delete broad directories, clear
worker checkpoints or overwrite another deployment to make setup resume.

## Offline validation and limits

`validate.ps1` includes fake setup/Graph transitions and substituted Windows
prerequisite/IIS-conflict probes, plus Desktop 5.1 execution and the existing C#
regression suite. Checks cover approvals/cancellation, each effect's pending
journal, resume order, scope/identity/disabled guards, failed health/failed stop,
package drift, one-shot approval/no-rerun and no secret export. Package checks
require these scripts/docs and exclude runtime state/credentials.
Additional prerequisite installer fixtures exercise vendor/version/checksum,
explicit IIS/bundle opt-in, each mutation failure, repair, invalid/3010/timeout
outcomes and newer-boot acknowledgement without actual OS or installer execution.

These fixtures never contact customer IIS/SCOM/Graph or write a machine
certificate. Real customer integration, script signing, SCOM property/SDK
contracts, certificate RSA-PSS access, effective inherited IIS settings, network
reachability, reader OAuth consent and authenticated Studio acceptance remain
**unverified until authorized customer testing**. An existing app needing migration,
unsupported host/build or unverified source contract is a genuine blocker,
not a reason to enable anonymous access, broaden roles or invent compatibility.
