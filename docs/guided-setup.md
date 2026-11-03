# Guided, resumable setup (evaluation installer)

The console wizard stages an **opt-in indexed** on-premises installation and
keeps customer Entra provisioning separate. It does not provision an Azure VM:
an existing approved customer Windows host is sufficient. ARM/Bicep is not an
Entra application-registration/consent mechanism. Existing legacy deployments
and their tools are unchanged.

This is an evaluation installer, **not source certification or an unattended
production installer**. No live IIS/SCOM/Graph acceptance is implied by offline
tests. Every effect requires typing `APPROVE`; any other response cancels before
that effect. `Status` is read-only. There are no passwords, automatic credential
prompts, downloads, execution-policy changes, broad group creation or recurrence.

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
No prerequisite installer is run or acquired by the wizard.

Use serviced versions approved for your OS/SCOM combination. See
[Microsoft IIS prerequisites](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-8.0)
and the [existing two-server deployment guidance](../README.md#two-server-deployment-indexed-path).
SCOM SDK/Operations console installation, firewall/DNS, public HTTPS reachability,
certificate enrollment/private-key permissions and source contract/mapping review
remain customer responsibilities. Do not guess SDK signatures or source flags.

Installation, private configuration, receiver state, worker state and the private
journal must be separate, nonoverlapping local paths. Targets must be **new**
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
.\setup\Start-ScomSetup.ps1 -StateDirectory 'C:\ApprovedState\ScomSetup' -Step Prepare
```

`Initialize` only creates one new private journal directory, owned by
Administrators and accessible only to SYSTEM/Administrators. The parent must
already exist. `Prepare` prompts for complete package/install/private/state
paths, existing worker `DOMAIN\account`, management-server FQDN,
management-group GUID, **existing approved group GUID**, module path, dedicated
new pool, site/application path, HTTPS application base URL and existing
LocalMachine My TLS certificate thumbprint. It saves the private plan with a
separate approval, then checks prerequisites and stages files with another
explicit effect summary. Use `-PlanPath` to supply the same JSON shape instead
of entering fields; it does not skip approval.

The selected scope is an existing group. The wizard neither lists all groups
nor creates one, and cannot broaden it to the whole management group. Advanced,
separately approved management-group deployments remain supported by the manual
runbook, not this default customer wizard.

Prepare does **not** create or start IIS. The staged `appsettings.json` is `{}`,
deliberately unable to start anonymously or with synthetic credentials.
All installed package files are hash-recorded separately from mutable active
configuration. Worker config is read-only to the worker; only its checkpoint
directory is writable. SYSTEM/Administrators retain access. IIS will get only
application Read/Execute and receiver-state Modify, not checkpoint/private-key
or worker configuration access. Logs/backups/journals remain private.

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

These fixtures never contact customer IIS/SCOM/Graph or write a machine
certificate. Real customer integration, script signing, SCOM property/SDK
contracts, certificate RSA-PSS access, effective inherited IIS settings, network
reachability, reader OAuth consent and authenticated Studio acceptance remain
**unverified until authorized customer testing**. An existing app needing migration,
unsupported host/build or unverified source contract is a genuine blocker,
not a reason to enable anonymous access, broaden roles or invent compatibility.
