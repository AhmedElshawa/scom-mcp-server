# Guided, resumable setup (evaluation installer)

Example paths, account names and commands in this guide are illustrative, not
verified deployment inputs. Replace them with values reviewed for the actual
environment before use; preserve existing private state and approval gates.

## Double-click quickstart

Download the reviewed CI evaluation artifact. Verify the SHA-256 of the **inner
`scom-evaluation.zip`** against a value obtained independently from the reviewed CI
run or a trusted administrator. A matching hash verifies the downloaded bytes,
not who published them or whether Windows trusts them. Use only a distribution
and signing route approved by the organization's software-security process. If
the package is blocked or lacks required authorization, stop and request an
approved build/distribution path; do not remove download markers or change policy.
Then extract **the entire `scom-evaluation.zip`** into a new directory. Open the
extracted `ci-evaluation` folder and double-click
**`ScomSetup.exe`**. Keep the executable, its bundled
runtime DLLs, `server`, `collector`, `setup` and documentation together; copying
only the EXE is not supported. This is a genuine Windows Forms application with
its own x64 .NET runtime, not a renamed PowerShell script or a console launcher.
It can open before the application's .NET 8 Hosting Bundle has been installed.
Windows Server **Desktop Experience** is required for the graphical wizard;
Server Core uses the separate console lane below.

The wizard opens directly to automatic **read-only prerequisite checks**, without
host changes or SCOM connections. Each completed check is shown in green for PASS
or red for BLOCKED, with a remedy. Unknown or failed checks are never marked green.
The Step 1 summary distinguishes checks not yet run, checks in progress,
returned passing or blocked results, and missing results. Passing prerequisites
does not approve installation; script signing and Windows policy remain enforced.
Use **Back / Next** for the sequential screens. Normal setup asks for a log folder
(separate MCP/collector subfolders), safe IIS placement, the approved execution
account and SCOM server/scope. Installation/private/state paths and a new dedicated
pool are proposed; detailed overrides, certificate/HTTPS evidence and resume
options live in **Advanced**. The configuration screen shows the three reviewed
JSON inputs separately. Proposed defaults do not grant permission, override
existing paths or certify external evidence.
If required, use the explicit elevation action and approve the genuine Windows
UAC prompt yourself, then rerun the checks.

Existing IIS sites are discovered for a **new** application. Existing applications
and shared pools are not silently reused or overwritten. A dedicated new pool is
proposed; conflicts still fail the original ownership checks. Missing HTTPS
certificate/hostname, Entra/source evidence or approved configuration remains a
real blocked gate.
Use **Discover groups** only after approving the bounded read-only source
connection; the management-group GUID is obtained from that connection rather
than typed in normal setup. Then choose a group or separately confirm all supported
Windows inventory. Review the final proposal and explicitly confirm **Prepare**.
Configuration/activation/worker/publication/acceptance remain separate gated
actions, never automatic consequences of navigation.

### What setup checks automatically, and what the operator must do

Follow these steps in order. A displayed value, discovered asset, successful
local check or prepared stage is not approval for the next operation.

1. **Resolve package authorization through the normal software process.** Obtain
   the evaluation build through an approved distribution channel and use the
   organization's required signing, trust and endpoint-security review. If
   Windows or organizational policy blocks launch, stop and ask the responsible
   administrator; do not suppress a warning, remove a zone marker, or change
   policy to continue. The separate
   [local evaluation signing procedure](local-evaluation-signing.md) is for an
   explicitly approved personal-lab derivative only. It is not a production
   prerequisite, publisher model or production distribution recommendation.
2. **Run Bootstrap and review the returned local checks.** On launch, the wizard
   reads local OS/runtime/IIS/SCOM-module metadata and discovers local IIS and
   certificate options. These checks do not install components, connect to SCOM,
   verify a source query contract, or collect inventory. Continue only when the
   individual prerequisite results have actually returned and all required
   checks pass. `NOT CHECKED`, missing results, or a blocked row means stop and
   investigate the displayed code; do not infer a missing component or treat
   navigation as a retry.
3. **Handle pending reboot outside setup.** Bootstrap only detects common
   pending-restart indicators. It does not reboot or clear them. If a restart is
   reported, stop before installation effects, have the host owner schedule the
   appropriate maintenance, and let that operator perform the restart. Reopen
   the wizard and run the checks again; use the new results rather than the
   pre-restart report.    Never clear a pending-restart indicator to pass the gate.

   In one observed evaluation, nine local checks passed while a pending-reboot
   check blocked. A read-only check later found a pending file-rename indicator;
   after the operator performed the restart, the indicators were clear and a
   separately approved Bootstrap run returned all ten local checks as passing.
   This records only that local prerequisite sequence; it does not establish
   SCOM connectivity, installation, configuration or acceptance.
4. **Review existing identity, HTTPS and IIS assets.** Reuse existing assets
   only after their owner and suitability are reviewed. The Entra publisher
   certificate and the IIS HTTPS/TLS certificate have different purposes and
   are not interchangeable. An identity file on disk does not prove its
   contents, ownership, permissions or source authorization. A visible TLS
   certificate does not prove chain trust, usable private-key access or an IIS
   binding; a discovered IIS site may have no matching HTTPS binding. Verify the
   intended DNS name, certificate and placement through the normal host-owner
   process. Do not issue duplicate identity or certificates merely because a
   selector is blank. The observed inspection found an existing identity file
   and a locally eligible TLS certificate, but did not inspect identity
   contents, prove certificate chain/key usability, or establish served HTTPS.
5. **Supply reviewed configuration and source evidence.** The operator and
   relevant identity/source owners must provide the real nonsecret identity file
   and complete reviewed receiver and worker JSON. Confirm their schema,
   intended reader grants, source mapping, authorization and evidence separately;
   repository examples are synthetic and are not deployable inputs. Approve
   bounded group discovery as a read-only source operation, then select and
   approve the intended scope. Discovery does not read inventory or set source
   verification flags. Before configuring or publishing supported Windows
   inventory, follow the [typed relationship-query stop gate](indexed-deployment-runbook.md#typed-relationship-query-stop-gate).
   If required files or independent source evidence are absent, stop; setup does
   not generate them or certify claims.
6. **Approve each consequential stage separately.** After reviewing paths,
   scope, IIS placement, log destinations and the exact effect summary, the
   operator separately approves Prepare/staging. Configure requires the reviewed
   files and source gates; activation requires the configured stage and an
   explicitly approved, conflict-free IIS/HTTPS target. A pending action or
   ambiguous result stays subject to journal review, not blind retry. Installing
   optional IIS features or a Hosting Bundle is another explicit operator
   choice; the operator supplies the independently verified installer, approves
   each host-wide effect, and reruns preflight afterward. Setup does not download
   prerequisites or restart Windows.
7. **Connect, publish and accept as distinct actions.** The exact worker account
   separately verifies its selected source scope and, after reviewed configuration,
   performs an explicitly approved one-shot publication. The host administrator
   separately records the worker receipt. An intended reader must then verify
   tool access, expected scope/coverage and unauthorized-user denial before the
   operator records acceptance. These steps do not enable recurrence, metrics or
   targeted-live checks; recurrence remains a later opt-in after acceptance.

The observed evaluation reached local prerequisite success, but the reviewed
receiver/worker inputs, source verification, scope approval, configuration,
installation/activation and reader acceptance were not established by that
result. Do not represent the evaluation as a completed setup.

### Continue or resume a deployment

Use **Status** to inspect the existing private journal before resuming. A prepared
or staged deployment is not configured or activated; green prerequisite checks,
group discovery and selected paths do not establish a working deployment. If a
previous action is pending, inspect the journal and follow the recovery guidance
instead of repeating the action blindly.

1. On **Choose SCOM scope**, confirm the intended mode: select one existing
   discovered group (the narrower default), or keep **ALL supported Windows
   inventory** only if the customer explicitly intends that broader scope.
   Whole-management-group means supported Windows inventory, not every SCOM
   class; it requires a distinct scope confirmation and is still not an
   installation or collection approval. Declining a confirmation starts no
   corresponding action.
2. For a new deployment, select **Prepare new deployment** only after reviewing
   the proposal. This saves the protected plan and stages new files after the
   separate exact effect approval. Pressing **Next (navigate only)** just changes
   screens. Prepare does not configure Entra, activate IIS or collect inventory.
3. On **Select identity and reviewed configuration**, check each file status
   separately. Select the nonsecret identity file and actual reviewed
   receiver and worker JSON files only when available. The updated visible
   checklist distinguishes “no file selected”, “selected path does not exist”,
   and “selected file exists; backend review still required”. File presence is
   not validation. Receiver/worker Browse opens at an existing settings folder
   (or Documents), and the wizard rejects using
   the same JSON path for multiple roles. Store the two reviewed settings files
   separately from Entra's `identity.json`; there is no required customer path.
   Do not copy the repository examples as customer config: they use synthetic IDs
   and verification flags set false. The wizard cannot invent reader grants,
   class/property mappings, source visibility or query-contract evidence.
4. As the exact selected worker account, use an approved Windows sign-in/RunAs
   session, load the installed private plan in Advanced, then explicitly run
   **Worker: connect and verify selected scope**. This bounded read checks only
   management-group/group identity; it reads no inventory and does not set
   source-verification flags. Return to the host administrator session for
   **Configure from selected files**, which is a separate approved action and
   validates all three reviewed inputs.
5. Continue only after Status reports the expected configured stage. The host
   administrator then separately approves **Activate configured HTTPS app**.
   The exact worker separately performs the one-shot publication; the host
   administrator records its receipt and performs external intended-reader,
   unauthorized-user and scope-coverage acceptance. None of these steps enables
   recurrence, metrics or targeted-live checks.

If any required JSON is unavailable, the source check fails, or the intended
scope is not approved, stop at that gate. Do not fabricate files, set `verified`
flags, or activate merely to get past a disabled button.

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
separate opt-in operations on the final **Acceptance and recovery** screen, not
a consequence of double-clicking.

## One package for a fresh customer installation

The fresh-install path uses the same complete package for the GUI, server,
collector and durable worker. Examples are not installed customer configuration:
do not copy a developer's `C:\ProgramData\ScomAiWorker\...` paths into production.
The wizard proposes local paths, accepts customer-selected alternatives and derives
the installed worker JSON, checkpoint, collector script/hash and runtime directory.

| Customer gate | What happens only after approval |
|---|---|
| Local prerequisites | Inspect actual results; explicitly approve a supported prerequisite install if needed, then recheck. |
| Environment inputs | Supply the existing worker identity, approved SCOM management group/group or supported-Windows scope, IIS/HTTPS hostname and certificate, Entra identity/reader configuration and reviewed source/property mappings. Unknown evidence remains blocked. |
| Prepare / Configure / Activate | Confirm each stage. Configure derives private paths and script pins from the installed bytes; Activate starts only the owned HTTPS app. |
| Worker publication / host acceptance | Connect and publish once as the exact worker account. Return as administrator to record the receipt and attest external reader/scope acceptance. |
| Optional ongoing cadence | Keep manual cadence, or use **Register optional startup worker** after acceptance. No hand-built scheduled task or hand-entered runtime paths are needed. |

This is guided setup, not a no-input or unattended installation. Customer SCOM MP
properties/visibility, identities/permissions, certificates/HTTPS, approved package
trust and external reader acceptance cannot be inferred or supplied by the package.

**OS version and product name:** `osVersion` retains the exact observed version;
the same package also supports a separate optional `osCaption` product/edition.
For a customer-approved `OSVersionDisplayName` property, configure worker
`windowsMapping.osCaptionProperty` and receiver
`ReadModel.WindowsOsCaptionProperty` with that exact name, matching explicit
property allowlists for each actual most-derived OS class and the existing
reviewed `WindowsMetricHosting` relationship. Configure rejects mismatched paired
caption mappings/allowlists/hosting. It never silently enables or invents them.
See the [source contract and candidate MP evidence](technical-reference.md).
Missing, blank or unconfigured captions remain **unknown/null**; the package does
not infer a Windows marketing name from a version/build. The presence of
`OSVersionDisplayName` in legacy repository code does not establish that the
customer's installed MP populates it.

**Trust/signing limitation:** the evaluation EXE and repository scripts do not
carry a production customer-trusted publisher signature. A self-contained EXE
does not establish trust or override PowerShell policy, UAC, SmartScreen,
certificate, antivirus or unsafe-content controls. The native GUI invokes the
existing Windows PowerShell 5.1 scripts normally, without a policy override. If
Windows or organizational policy blocks launch, stop and ask the responsible
security administrator for the approved signing or software-distribution route.
Do not remove download markers, suppress warnings, lower/bypass policy or alter
application-control settings. A matching archive hash proves only byte
integrity against the supplied checksum; it is not publisher authentication.

The separate [local evaluation signing procedure](local-evaluation-signing.md)
describes an explicitly approved, untimestamped personal-lab derivative. Its
certificate, trust and transfer steps are lab-specific and are **not** a
production prerequisite or a production signing recommendation. A lab signature
does not imply that another account or host trusts the package. No embedded
credentials or production signing certificate are supplied.

The normal PR artifact is an **unsigned evaluation package**. CI runs manifest,
tamper and missing-signer rejection tests without creating a certificate or
changing trust. Before packaging is accepted, validation also compares the
packaged `ScomEntra.psm1` and `ScomSetup.Windows.psm1` bytes with the reviewed
checkout, independent of the package's own manifest. A signed derivative changes
those bytes, so its content-only check must be paired with separate signing
provenance validation. Actual local signed-derivative integration remains a separate
explicit workflow-dispatch opt-in: Windows protected-root trust can require an
interactive prompt, which unattended CI must not auto-approve or bypass.
Passing the package workflow does not claim that signing integration passed,
establish customer publisher trust or certify a customer installation.

Offline CI starts the packaged EXE with shared .NET lookup paths disabled and
checks that its loaded runtime is the packaged runtime and the native form can
be constructed without invoking the bridge. That is a loader/entrypoint proof,
not a completed clean-server installation or production certification.

### If the first prerequisite screen cannot obtain results

**NOT CHECKED is not a missing-prerequisite diagnosis.** Do not install the SDK,
Hosting Bundle or IIS based on a communication error alone. Before a successful
Prepare there may be no deployment journal at all. The corrected wizard displays
a fixed startup failure code and remedy without copying private exceptions,
request JSON or raw PowerShell output into logs.

The first-screen fixed-code log is
`%LOCALAPPDATA%\ScomSetup\Logs\bootstrap.jsonl` for the Windows user running the
wizard. It is separate from the not-yet-selected MCP/collector logs and protected
deployment journal, contains allowlisted codes only, and is bounded to about
64 KiB. The screen reports an early-log write failure explicitly. A log does not
prove that a prerequisite probe ran; only returned individual checks do.

| Code | Meaning / next read-only action |
|---|---|
| `SETUP_GUI_BRIDGE_MISSING` | The adjacent `setup\Invoke-ScomSetupGui.ps1` is missing. Verify complete extraction. |
| `SETUP_GUI_POLICY_BLOCKED` | PowerShell emitted specific policy/security markers. Have the customer security process inspect signing/zone/policy; do not bypass it. |
| `SETUP_GUI_MODULE_LOAD_FAILED` | The entry script ran but could not import its setup module. Verify complete extraction and ask the administrator to review the applicable trust/application-control policy; this alone does not establish a policy cause or universal signature requirement. |
| `SETUP_GUI_INVALID_JSON` / `SETUP_GUI_INVALID_SCHEMA` | Returned output was not accepted. The schema message/report includes an allowlisted `contractField`, `expectedType` and `actualType` (no values). Report those; mixed files, a missing prerequisite or a policy problem has not been established by this code alone. |
| `SETUP_GUI_AUTHORIZATION_BLOCKED` | PowerShell reported `AuthorizationManager check failed` with `SecurityError` / `UnauthorizedAccess` and exited unsuccessfully without a usable response. The host can echo the stdin request as JSON before the bridge runs. Ask the administrator to review effective policy, downloaded-file zone/trust prompts, signatures and application-control policy. Even Unrestricted can warn about Internet-zone scripts; signing is not always mandatory. This does not establish a missing SDK. Use only the organization's approved software-signing/distribution process; do not unblock extracted scripts, bypass policy or change application control. |
| `SETUP_GUI_CHILD_EXIT` / `SETUP_GUI_NO_RESPONSE` | No usable response was obtained. Collect the read-only diagnostic; no missing component is inferred. |
| `SETUP_GUI_BOOTSTRAP_PROBE_FAILED` | Local probes could not complete. Privately inspect local component access; do not install based on this code alone. |

The EXE also supports a single real read-only transport diagnostic:

```powershell
.\ScomSetup.exe --diagnose-bootstrap "$env:TEMP\scom-bootstrap-diagnostic.json"
```

Use a new report filename (existing reports are not overwritten). It makes one
local Bootstrap request and writes fixed codes/check names/results only, no plan,
catalog, paths, configuration or raw exception output. A nonzero exit means no
individual results were obtained, not that any specific prerequisite is missing.
No setup stage, source access or policy change is performed.

On `SETUP_GUI_INVALID_SCHEMA`, the corrected report also includes only the
allowlisted rejected DTO field and expected/received JSON types. For example,
`contractField: defaults.operationsManagerModule`, `expectedType: string`,
`actualType: array` would identify a string/array contract mismatch **without
revealing the module path**. This is an example, not a diagnosis of a particular
server. Array indices are replaced with `[]`; unknown fields/types become
`response`/`unknown`. Send those three report fields, not the underlying Bootstrap
catalog or raw JSON. Validation remains fail-closed; arbitrary arrays, objects,
strings-as-booleans or missing check results are not silently accepted.

`Contract field: ok; expected: boolean; received: missing` means a JSON
object was received without the required top-level boolean. It does not identify
a missing prerequisite. All normal backend results and entrypoint error
envelopes include `ok`; a BOM/banner or CLIXML outside JSON would instead fail
JSON parsing. Preserve this distinction rather than assuming a signing or
component-installation remedy.

An observed exception to the usual envelope path is PowerShell refusing to
authorize `-File` before executing the entrypoint while echoing piped request
JSON to stdout. That object contains `action`, not response `ok`. On a nonzero
exit, the native decoder now recognizes the fixed AuthorizationManager message
plus SecurityError/UnauthorizedAccess markers instead of reporting only missing
`ok`. Valid sanitized backend failure envelopes still take precedence. No raw
stderr/request is displayed or logged, and this classification grants no
installation or navigation approval. The administrator must review the
`.ps1`/`.psm1` trust and authorization chain. Unrestricted permits unsigned
scripts but can still prompt for Internet-zone files; redirected protocol stdin
is not an appropriate channel for answering a Windows trust prompt. Do not send
prompt answers through the bridge, remove zone markings, or change policy. For
an intended installation, use only the signing or distribution route approved
by the organization's software-security process; it is a deployment decision,
not a diagnostic. The personal-lab signing procedure is not a production route.
The evaluation package does not provide a production trusted publisher signature.

Maintainers can run `Validation\Get-BootstrapTransportShape.ps1` under Windows
PowerShell 5.1 with `-PackageDirectory` set to the extracted package. It uses
the production entrypoint, UTF-8 stdin and native launch environment allowlist,
and reports only exit code, root kind/count, allowlisted top-level property
names/types and a count of unknown names. It never prints unknown names,
values, stdout or stderr. `Validation\Test-BootstrapTransport.ps1` also
exercises empty, malformed, null and unknown-action input against that actual
entrypoint, without installation, SCOM access or policy overrides.

For a minimal read-only diagnostic, open Windows PowerShell in the extracted
`ci-evaluation` folder (the folder containing `ScomSetup.exe`) and run:

```powershell
Test-Path .\setup\Invoke-ScomSetupGui.ps1
Get-ExecutionPolicy -List
'{"action":"Bootstrap"}' | & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File (Join-Path $PWD 'setup\Invoke-ScomSetupGui.ps1')
```

This uses normal `-File` execution, performs only local read-only checks and
neither connects to SCOM nor installs anything. Do not change execution policy,
unblock files as a diagnostic or bypass a Windows warning to make it run. If
policy prevents the script from starting, the customer security process should
review that specific error. If JSON is returned, report the fixed `code` and the check names
and PASS/BLOCKED values; do not share raw configuration, paths, identity details
or credentials. Missing files require the complete reviewed package to be
extracted, not prerequisite installation.

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

Use the complete reviewed [evaluation package](technical-reference.md#build-and-validate),
not a single DLL. On the customer host, open **64-bit Windows PowerShell 5.1**
through the approved administrator mechanism. Use the organization's approved
signing/distribution route. If policy blocks the package, stop and request an
approved software-distribution path; never bypass `AllSigned`, OS authentication
or unsafe-content controls. This repository does not ship a production
customer-trusted signing certificate.

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
Hosting Bundle detection checks official Windows Server Hosting registrations in
both native and WOW6432Node uninstall registry views, plus the native ANCM V2 DLL;
the separate "Hosting Bundle Options" entry alone does not qualify. An unreadable
registry probe blocks rather than assuming installation. Do not move registry
entries or reinstall solely to compensate for a detection error.

Use Microsoft's [Windows Hosting Bundle 8.0.34 download](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-aspnetcore-8.0.34-windows-hosting-bundle-installer)
as a concrete example; check the [.NET 8 downloads page](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
for the latest customer-approved .NET 8 servicing version. Version 8.0.34 is an
example, not a pinned minimum or a claim that it remains the latest. The Windows
Hosting Bundle includes IIS integration; it is distinct from the standalone
ASP.NET Core Runtime or .NET SDK. Install the approved bundle after IIS, verify
its Microsoft signature and obtain customer approval. Existing SDKs or runtimes
do not need to be uninstalled. Installing the bundle is not a diagnosis or
guaranteed fix for a separate failed runtime-detection check.

Use serviced versions approved for your OS/SCOM combination. See
[Microsoft IIS prerequisites](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-8.0)
and the [existing two-server deployment guidance](technical-reference.md#two-server-deployment-indexed-path).
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

### Recovery: local TLS certificate discovery reports missing `Value`

In Windows PowerShell 5.1 the certificate provider's
`EnhancedKeyUsageRepresentation.ObjectId` can be a **string**, not an OID object
with a `.Value` member. Older catalog filters can therefore throw
`PropertyNotFoundStrict` while examining a certificate with an EKU. A corrected
filter compares the string to the Server Authentication OID
`1.3.6.1.5.5.7.3.1`. Discovery still requires a private key, current validity
and that exact EKU; a signing-only certificate is not eligible. The fix does
not require replacing or modifying a certificate.

1. Inspect the failure; let any active operation finish before closing the GUI.
   Preserve the current package, private journal, identity, configuration and
   certificates; do not reinitialize state or provision replacement identities.
2. Obtain a reviewed complete package containing the corrected
   `setup\ScomSetup.Windows.psm1`. Verify the archive against the hash obtained
   independently for that exact artifact and follow the organization's software
   approval process. Do not substitute individual files into a manifest-verified
   package or change policy or trust markers to force execution.
3. Extract the verified package to a new approved directory. Its actual path is
   deployment-specific, not a required example path. Before resuming an existing
   deployment, inspect the journal and package-drift requirements; stop if an
   action is pending or the package does not match the recorded plan. Do not
   clear pending state or override drift checks.
4. With approval, reopen the corrected GUI and retry the local certificate
   catalog. For a read-only retry in an existing PowerShell process, reload the
   exact corrected package module before invoking `Get-ScomSetupLocalCatalog`;
   cached module code can outlive a package change. A newly launched GUI uses a
   fresh bridge process. Do not install or activate anything during the check.

These steps do not establish that the actual certificate is suitable or that
the deployment is configured, activated or accepted. Verify those gates
separately in the target environment.

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

## Guided durable refresh remains a separate opt-in

The complete package includes
[DurableIndexedRefresh operations](../Operations/DurableIndexedRefresh/README.md).
After reviewed one-shot publication and acceptance, the host administrator can use
the final native page's **Register optional startup worker**. Its visible normal
Windows PowerShell window reads the protected accepted journal, checks installed
and configuration hashes, derives all paths and requests the exact selected worker
credential through Windows. It does not add trust, change execution policy or
save credentials in JSON, logs or GUI arguments. Package scripts must be allowed
by the customer's approved trust policy for that account.

Confirming registration creates an **enabled AtStartup task**: it does **not**
start it immediately, but the task **can start after a reboot** without another
prompt. Explicitly approve that recurrence before confirming registration. To run
now, separately choose **Start registered worker** and confirm in its window.
**Worker task status** is read-only; **Stop registered worker** requires its own
confirmation. The existing exact ownership/action/worker checks still apply; an
unrelated existing task or changed deployment is not reused or overwritten.
Closing/cancelling the window is not proof of success; inspect its result and task
status. An unresolved terminal failure still requires the separate reviewed rearm
procedure in the operations runbook; Start never silently rearms.

The handoff derives `<installPath>\server\ScomMcp.dll`,
`<privatePath>\worker.json` and `<workerStatePath>\runtime`; runtime state is
separate from `<workerStatePath>\worker.db`. The new runtime directory must be
empty for new registration. Status/start/stop preserve existing runtime state and
require an exactly matching owned task. Do not edit the private installed-file
manifest, configuration hashes or journal to force a handoff through.

Keeping manual cadence is supported: do not register the startup task and use the
separately approved one-shot worker lane when needed. Preflight, navigation and
ordinary setup stages never register/start tasks or collect automatically.
Metrics and targeted-live checks remain disabled. Bounded
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
[reader setup](technical-reference.md#azure-iis-and-copilot-studio-reader-setup). No secret is
created/exported by this script. Do not invent redirect URIs or use an
administrator/shared reader connection.

Graph v1.0 contracts:
[applications create](https://learn.microsoft.com/en-us/graph/api/application-post-applications?view=graph-rest-1.0),
[application update](https://learn.microsoft.com/en-us/graph/api/application-update?view=graph-rest-1.0),
[service principal create](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-serviceprincipals?view=graph-rest-1.0),
[publisher role assignment](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-approleassignments?view=graph-rest-1.0).

### Publisher identity and certificate: reuse or create

First review whether an existing publisher registration and certificate are
approved and suitable for this worker. Reuse them when their ownership,
permissions, validity and private-key access meet the requirements below;
creating a new application or certificate is **not** required for every setup.
Do not modify an existing registration or credential without its owner's
approval. The creation steps below are an optional lane, not a prerequisite
for an approved existing identity.

Use the creation lane when the administrator has chosen a **new isolated test publisher**
while reusing reviewed API/reader registrations. Do not edit production publisher
credentials, permissions or certificates. Reusing an API/reader still shares its
authorization boundary: a new publisher credential is not a separate test tenant,
API or dataset. Obtain explicit owner approval for that reuse and the test scope;
otherwise stop and arrange separate resources.

The package already includes `setup\Provision-ScomEntra.ps1`, the guarded Graph
provisioning script described above. It is **not** an ARM/Bicep application template,
a full Azure portal wizard or automatic tenant consent. The following manual
portal steps do not require a new runtime feature or template. DNS and the IIS
HTTPS/TLS certificate remain separate pending requirements.

1. **Record the approved identities before changing anything.** In Microsoft Entra
   admin center, select the intended tenant. Under **App registrations**, inspect
   the API resource's Overview, Expose an API, App roles and Manifest. Confirm
   single-tenant audience, v2 access tokens, the exact `api://<API-client-ID>`
   resource, enabled admin-only delegated `Scom.Read`, and enabled application-only
   `Scom.Ingest`. Inspect the existing reader's intended delegated permission and
   ownership without changing its callbacks, secret or credentials. A screenshot
   of a scope/role alone does not establish this full compatibility contract.
2. **Reuse or create the publisher key.** If an approved certificate with an
   accessible private key already exists on the worker, verify it and skip the
   creation commands. Otherwise, only after customer approval, run the following
   illustrative commands in elevated Windows PowerShell 5.1 on the worker host.
   Replace both placeholders with approved values before running.
   Select an existing approved local directory for the public `.cer` file.
   This is a lab client-authentication certificate, **not** TLS or code signing.
   No Root/TrustedPublisher import or PowerShell trust change is needed. The
   software key is nonexportable; do not generate or request a PFX.

```powershell
$ErrorActionPreference = 'Stop'
$subject = '<approved-certificate-subject>'
$publicFile = '<approved-absolute-public-certificate-path>'
if (-not (Test-Path -LiteralPath (Split-Path -Parent $publicFile) -PathType Container)) {
    throw 'Select an existing approved public-certificate directory.'
}
if ((Test-Path -LiteralPath $publicFile) -or
    @(Get-ChildItem Cert:\LocalMachine\My -ErrorAction Stop |
        Where-Object Subject -EQ $subject).Count -ne 0) {
    throw 'Certificate or output already exists. Inspect it; do not create a duplicate or overwrite.'
}
$cert = New-SelfSignedCertificate -Type Custom -Subject $subject `
    -CertStoreLocation 'Cert:\LocalMachine\My' `
    -Provider 'Microsoft Software Key Storage Provider' -KeyAlgorithm RSA -KeyLength 2048 `
    -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy NonExportable `
    -NotAfter (Get-Date).AddYears(1)
$cert | Select-Object Subject, Thumbprint, NotBefore, NotAfter, HasPrivateKey
Export-Certificate -Cert $cert -FilePath $publicFile -Type CERT -NoClobber -ErrorAction Stop
```

If creation succeeds but export fails, stop and inspect the created certificate;
do not rerun creation blindly. Record its **full** thumbprint and expiry. A public
certificate already present in an existing publisher app does not put its private
key on the worker host. Use customer-managed enrollment/renewal instead where self-signed
lab credentials are not allowed.

3. **Reuse or create the publisher app.** In **App registrations**, first check
   whether an approved publisher app already exists. If it does, inspect its
   recorded object/client IDs rather than registering another. Only when a new
   app is explicitly approved, select **New registration**, use an approved name,
   select **Accounts in this organizational directory only**, leave Redirect URI
   empty and register.
   Record the Directory (tenant) ID, Application (client) ID and this registration's
   Object ID. Do not create a client secret or add a redirect for this app.
4. **Verify the public certificate.** If the approved certificate is not already
   registered, upload only its DER `.cer` in the publisher's **Certificates &
   secrets > Certificates > Upload certificate**, after explicit approval.
   Compare the complete portal thumbprint and validity dates with the local
   `LocalMachine\My` certificate, not just its subject or last characters. Stop on
   mismatch. Never upload/export the private key.
5. **Verify the ingestion role.** Confirm `Scom.Ingest` is granted for the
   intended API and inspect the corresponding Enterprise application. If a
   grant is missing, only with owner approval use **API permissions > Add a
   permission > My APIs** to select the exact reviewed API and its application
   permission `Scom.Ingest`; an authorized tenant administrator must review and
   grant admin consent. Adding a requested permission alone is not an app-role
   grant. The publisher does not need delegated `Scom.Read` or Microsoft Graph
   runtime permissions. On a newly created publisher, remove the default Graph
   `User.Read` permission if present; do not alter existing applications without
   owner approval. Stop if the API/role cannot be identified exactly.
6. **Verify private-key Read for the exact worker account.** Confirm the Windows
   account selected in setup; do not guess an account from the app display name.
   On the worker host open `certlm.msc` as the authorized administrator, then **Personal >
   Certificates**, locate the exact full thumbprint, right-click **All Tasks >
   Manage Private Keys**. If access is missing, after approval add **Read** only
   for that confirmed worker account.
   Preserve existing SYSTEM/administrator permissions; do not grant Everyone,
   Users, the IIS pool or Full Control, replace the ACL wholesale, or export the
   key. Stop if Manage Private Keys is absent or the account cannot be resolved.
7. **Verify actual RSA-PSS key access as the worker.** Open a normal approved
   Windows session as that exact account (not the administrator or IIS identity)
   and run this local, non-network challenge after substituting the recorded
   thumbprint. Certificate presence/`HasPrivateKey` alone is not proof of access.

```powershell
$ErrorActionPreference = 'Stop'
$thumbprint = '<full-40-hex-publisher-thumbprint>'
if ($thumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Enter the exact recorded thumbprint.' }
$cert = Get-Item -LiteralPath "Cert:\LocalMachine\My\$thumbprint" -ErrorAction Stop
if (-not $cert.HasPrivateKey -or $cert.NotBefore -gt (Get-Date) -or $cert.NotAfter -le (Get-Date)) {
    throw 'Valid matching private-key certificate required.'
}
$rsa = $null
$public = $null
try {
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    if ($null -eq $rsa -or $rsa.KeySize -lt 2048) { throw 'Accessible RSA-2048-or-greater key required.' }
    $challenge = [Text.Encoding]::UTF8.GetBytes([guid]::NewGuid().ToString('D'))
    $signature = $rsa.SignData($challenge, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pss)
    $public = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($cert)
    if (-not $public.VerifyData($challenge, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'RSA-PSS challenge verification failed.' }
    'PASS: this account can use this local key for RSA-PSS. No token request or publication performed.'
} finally {
    if ($null -ne $public) { $public.Dispose() }
    if ($null -ne $rsa) { $rsa.Dispose() }
    $cert.Dispose()
}
```

This proves only local key use under that account, not Entra token issuance,
tenant consent, SCOM visibility, receiver authorization or successful publication.
Retain the existing separately approved connection/publication/acceptance gates.

8. **Use the validated nonsecret identity file.** An existing reviewed file can
   be used if it matches the approved identities and certificate; generate one
   only when needed. The following IDs have different meanings; never substitute
   display names or swap object/client IDs.

| Value | Correct source / use |
|---|---|
| Tenant ID | Intended tenant's Directory ID |
| API client ID | API App registration's Application ID; forms `api://<API-client-ID>` |
| API/reader/publisher registration Object IDs | App registrations > Overview; script reuse inputs `-ApiObjectId`, `-ReaderObjectId`, `-PublisherObjectId` |
| Publisher client ID | Approved publisher's Application ID; worker token client |
| Publisher service-principal Object ID | Enterprise applications > exact publisher > Overview; output `publisherObjectId` for receiver authorization, **not** `-PublisherObjectId` input |
| Certificate thumbprint | Full matching local/portal public certificate thumbprint; worker private key stays local |

Use an authorized provisioning workstation/session with the existing approved
Global-cloud delegated Graph context and a protected journal initialized through
`Initialize-ScomSetupState.ps1`. The Graph administration scopes belong to that
administrator session, **not the publisher's runtime permissions**. Supply all
three reviewed registration Object IDs to avoid creating more apps:

```powershell
.\setup\Provision-ScomEntra.ps1 -TenantId '<approved-tenant-guid>' `
    -NamePrefix '<approved-name-prefix>' -PublicCertificatePath '<approved-public-DER-cer-path>' `
    -StateDirectory '<approved-private-state-directory>' `
    -ApiObjectId '<existing-api-registration-object-guid>' `
    -PublisherObjectId '<approved-publisher-registration-object-guid>' `
    -ReaderObjectId '<existing-reader-registration-object-guid>'
```

Omit `-GrantPublisherRole` here when the role has already been granted. The
script reads API/client/certificate contracts, service
principals and the actual role assignment before producing identity output;
incompatible reuse blocks instead of patching existing apps. Inspect all prompts:
if it proposes an unexpected create, cancel and review the IDs/service principals.
It can offer creation of a missing service principal; this is not a read-only
export command. Saving `identity.json` itself requires explicit confirmation.
Shape validation does not replace independent permission/ownership review,
including unwanted additional grants. Use the resulting validated file in
Configure; do not hand-build a different schema or edit the existing production
identity/configuration. Preserve the journal and use explicit IDs on resumption;
never clear pending state or rerun without IDs to force duplicate creation.

#### Recovery: publisher certificate already uploaded, comparison rejected

Microsoft Graph defines `keyCredential.customKeyIdentifier` as binary; its REST
JSON representation is Base64. Some customer-facing output may render the same
20-byte certificate thumbprint as 40 hexadecimal characters instead. The
provisioner accepts only canonical Base64 for exactly 20 bytes or exactly 40 hex
digits, decodes either to bytes, and requires an exact match to the local
certificate hash. Malformed identifiers, a different thumbprint, an expired
credential or more than one matching credential still fail closed. See the
[Microsoft Graph `keyCredential` definition](https://learn.microsoft.com/en-us/graph/api/resources/keycredential?view=graph-rest-1.0).

For `ENTRA_EXISTING_PUBLISHER_CERTIFICATE_CONFLICT_NO_KEY_OVERWRITE`, stop and
independently verify the exact publisher, certificate and credential validity.
Use a reviewed, verified package containing the comparison fix; do not overwrite
credentials, change execution policy or remove trust markers to force a retry.
Replacing a module on disk does not update a module already loaded in a PowerShell
process; confirm that the approved package's module is the one actually loaded.
Preserve the existing state directory and public certificate. Inspect any journal
read-only and stop if `pending` is non-null. Do not reinitialize state or create
another app or credential to work around the error.

After review, use the same tenant, state directory, public `.cer`, and reviewed
API, publisher and reader **app-registration Object IDs** for the existing
resources. Cancel unexpected create/grant prompts. The guarded retry uses the
following placeholders:

```powershell
.\setup\Provision-ScomEntra.ps1 -TenantId '<same-approved-tenant-guid>' `
    -NamePrefix '<same-name-prefix-in-existing-journal>' `
    -PublicCertificatePath '<same-existing-public-DER-cer-path>' `
    -StateDirectory '<same-existing-private-state-directory>' `
    -ApiObjectId '<existing-api-registration-object-guid>' `
    -PublisherObjectId '<existing-publisher-registration-object-guid>' `
    -ReaderObjectId '<existing-reader-registration-object-guid>'
```

Keep the role-grant switch omitted if `Scom.Ingest` is already assigned and
granted. Add `-GrantPublisherRole` only if that specific role assignment is still
missing and the authorized tenant administrator explicitly approves the
provisioner's separate confirmation. Review every prompt: an unexpected create,
incompatible API/client contract, malformed/mismatched identifier, or pending
journal is a stop condition. This recovery does not require changing the tenant,
certificate, Graph permissions, customer machine or trust policy.

Identity generation does not establish reader consent or deploy/configure DNS,
firewall, TLS or a live collector. The approved host name, HTTPS
certificate/private-key access for IIS, reader consent and end-to-end acceptance
remain independent gates.

## Stage 3: configure, activate, publish once, accept

Use the package examples as schema references only; create separate,
customer-specific **complete reviewed** receiver and worker JSON. Fill the
intended immutable reader-user grants, actual class/property/Windows mapping and
budgets, and independently established source-contract evidence. The examples
contain synthetic identifiers and intentionally start with evidence flags false;
they are not deployable customer configuration.
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
[reader/data acceptance](technical-reference.md#azure-iis-and-copilot-studio-reader-setup):
tool discovery, source status, intended inventory/coverage, unauthorized-user
denial, pagination and stale/partial behavior. Then explicitly attest that evidence:

```powershell
.\setup\Start-ScomSetup.ps1 -StateDirectory 'C:\ApprovedState\ScomSetup' -Step Accept
```

`Accept` records operator attestation only. Recurrence is a **separate opt-in**
decision after acceptance through the guided task handoff (or advanced operations
scripts), not a required hand-built Task Scheduler step. Metrics and targeted-live remain disabled;
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
