# Guided, resumable setup (evaluation installer)

## Double-click quickstart

Download the reviewed CI evaluation artifact. Verify the SHA-256 of the **inner
`scom-evaluation.zip`** against a value obtained independently from the reviewed CI
run or a trusted administrator. A matching hash verifies the downloaded bytes,
not who published them. If the customer's administrator approves running this
unsigned evaluation package, use the administrator-approved Windows download-trust
procedure on that verified ZIP only, then extract **the entire
`scom-evaluation.zip`** into a new directory. Do not recursively unblock extracted
contents. Open the extracted `ci-evaluation` folder and double-click
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
certificate/hostname, Entra/source evidence or approved configuration remains a
real blocked gate.
Use **Discover groups** only after approving the bounded read-only source
connection; the management-group GUID is obtained from that connection rather
than typed in normal setup. Then choose a group or separately confirm all supported
Windows inventory. Review the final proposal and explicitly confirm **Prepare**.
Configuration/activation/worker/publication/acceptance remain separate gated
actions, never automatic consequences of navigation.

### Resume the current MCPTest evaluation

The latest customer report says Step 5's whole-management-group checkbox is
checked. Step 6 still says “Discovery complete. Select an existing group
explicitly; no inventory or installation was approved,” but that message does
not reflect the selected whole-management-group mode. Step 6 reports **Stage:
Staged**. Treat that only as the prepared/staged phase, not configured or
activated. No inventory collection or activation is reported. The checkbox
selects a scope; it is not itself approval for collection or installation, and
each gated action still requires its own confirmation.

The inputs summary reports identity/receiver/worker JSON as missing; Advanced
shows the identity path populated and receiver and worker paths blank. A
populated path is not proof the file exists or is valid. Source connection
remains unverified. Do not treat green prerequisite checks, completed
discovery, or the staged state as a working deployment.

1. On **Choose SCOM scope**, confirm the intended mode: select one existing
   discovered group (the narrower default), or keep **ALL supported Windows
   inventory** only if the customer explicitly intends that broader scope.
   Whole-management-group means supported Windows inventory, not every SCOM
   class; it requires a distinct scope confirmation and is still not an
   installation or collection approval. Declining a confirmation starts no
   corresponding action.
2. On **Prepare the new deployment**, select **Prepare new deployment**. This
   saves the protected plan and stages new files after the separate exact effect
   approval. Pressing **Next (navigate only)** just changes screens. Prepare does
   not configure Entra, activate IIS or collect inventory.
3. On **Select identity and reviewed configuration**, check each file status
   separately. The identity path may remain populated; select actual reviewed
   receiver and worker JSON files only when available. The updated visible
   checklist distinguishes “no file selected”, “selected path does not exist”,
   and “selected file exists; backend review still required”. File presence is
   not validation. Receiver/worker Browse opens at an existing settings folder
   (or Documents), not the identity file's folder, and the wizard rejects using
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
See the [source contract and candidate MP evidence](../README.md).
Missing, blank or unconfigured captions remain **unknown/null**; the package does
not infer a Windows marketing name from a version/build. The presence of
`OSVersionDisplayName` in legacy repository code does not establish that the
customer's installed MP populates it.

**Trust/signing limitation:** this evaluation EXE and repository scripts do not
carry a customer-trusted publisher signature. A self-contained EXE does not
establish trust or override PowerShell `AllSigned`/RemoteSigned, UAC, SmartScreen,
certificate, antivirus or unsafe-content warnings. The native GUI invokes the
existing Windows PowerShell 5.1 scripts using normal `-File` execution with no
policy override; customer-approved download-trust approval may be needed before
checks/actions can run. If the customer does not approve the unsigned package or
Windows still blocks it, stop and use the customer's normal software-approval
process. No embedded credentials or fake signing certificate are supplied.

**Unsigned-download trust step:** an approved administrator may decide to remove
the Internet-download zone marker from the verified ZIP before extraction, using
the customer's normal reviewed download-trust procedure. For example, only after
the archive hash has been independently verified and approval granted:

```powershell
Get-FileHash -LiteralPath 'C:\ApprovedDownload\scom-evaluation.zip' -Algorithm SHA256
# Compare the result with the independently obtained reviewed-run hash first.
Unblock-File -LiteralPath 'C:\ApprovedDownload\scom-evaluation.zip'
```

This is an explicit, one-archive deployment trust decision, not a signing or
publisher-authentication step. Extract the complete archive to a **new** folder
afterward; do not run `Unblock-File` recursively, unblock individual scripts
automatically, lower execution policy, bypass PowerShell policy, suppress
security prompts or alter AppLocker/WDAC/application-control settings. The wizard
must still run and display every prerequisite check, and installation remains
blocked until checks pass and the customer confirms each action. If the customer
does not approve this unsigned-package procedure or security controls still
block execution, stop; this evaluation approach does not require the customer to
issue a signing certificate.

Before retrying, inspect the extracted scripts and modules for zone markers
from Windows PowerShell, with the current directory set to the new `ci-evaluation`
folder:

```powershell
Get-ChildItem -LiteralPath .\setup, .\collector, .\Operations -Recurse -File -ErrorAction Stop |
    Where-Object { $_.Extension -in '.ps1', '.psm1' } |
    ForEach-Object {
        Get-Item -LiteralPath $_.FullName -Stream * -ErrorAction Stop |
            Where-Object { $_.Stream -eq 'Zone.Identifier' }
    }
```

No output means no matching markers were found if the command completed without
errors. If a marker remains or inspection fails, stop and ask the customer security
process to review; do not assume success or unblock extracted files. Otherwise
open `ScomSetup.exe` normally and inspect the actual prerequisite results.

The normal PR artifact is an **unsigned evaluation package**. CI runs manifest,
tamper and missing-signer rejection tests without creating a certificate or
changing trust. Actual local signed-derivative integration remains a separate
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
| `SETUP_GUI_AUTHORIZATION_BLOCKED` | PowerShell reported `AuthorizationManager check failed` with `SecurityError` / `UnauthorizedAccess` and exited unsuccessfully without a usable response. The host can echo the stdin request as JSON before the bridge runs. Ask the administrator to review effective policy, downloaded-file zone/trust prompts, signatures and application-control policy. Even Unrestricted can warn about Internet-zone scripts; signing is not always mandatory. This does not establish a missing SDK. An administrator may separately approve the verified-archive trust step above; do not unblock extracted scripts, bypass policy or change application control. |
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
is not an appropriate channel for answering a Windows trust prompt. Do not send prompt answers through the bridge or change policy. Do not remove
zone markings as a diagnostic workaround. For an intended installation, an
authorized administrator may separately approve the one-archive trust step in
the quickstart; it is a deployment decision, not a diagnostic. Approved signing
or another customer software-approval path may be required by local policy, but
is not proven universally mandatory by this error. The evaluation package does
not provide a trusted publisher signature.

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
review that specific error. If the administrator separately approves deployment,
use only the verified-archive trust step in the quickstart. If JSON is returned, report the fixed `code` and the check names
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

Use the complete reviewed [evaluation package](../README.md#build-and-validate),
not a single DLL. On the customer host, open **64-bit Windows PowerShell 5.1**
through the approved administrator mechanism. Obtain administrator approval for
the unsigned package using the quickstart's verified-archive trust procedure if
permitted; never bypass `AllSigned`, OS authentication or unsafe-content warnings.
If customer policy forbids the unsigned package, stop and use its normal software
approval process. This repository does not ship a customer-trusted signing
certificate.

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

### Recovery: local TLS certificate discovery reports missing `Value`

In Windows PowerShell 5.1 the certificate provider's
`EnhancedKeyUsageRepresentation.ObjectId` is a **string**, not an OID object
with a `.Value` member. The original `a5105db5` catalog filter could therefore
throw `PropertyNotFoundStrict` at `ScomSetup.Windows.psm1:96` when examining a
certificate with an EKU. The local repair compares that string directly to the
Server Authentication OID `1.3.6.1.5.5.7.3.1`. It still requires a private key,
current validity and that exact EKU; a signing-only certificate is not eligible.
This fix does not require replacing or modifying any customer certificate.

The local-only `ScomSetup-Windows-EKU-fix.zip` contains exactly one file,
`ScomSetup.Windows.psm1`, with no changed dependencies. These hashes apply only
to this repair and baseline:

| File | SHA256 |
|---|---|
| Repair ZIP | `33E1296A1732DC61E5856DFB8859469EC9B38F299637824E70EA4850438A1F06` |
| Fixed module | `ECDF109CAC300994C4DC160DDC566CECB3BD2892BAF390B84CD5C4F2F7F4E288` |
| Original CI package module (CRLF) | `E71E675FE8F602DA2DCBC0673299FB2CD3F48B52932E68B0047FBE230340CD48` |
| Original Git module at `a5105db500b68cd7f283fd1cc7c8495c97896da0` (LF) | `D50671CDE73FEE38ADE86F26E27CC9C452873992098E1008E7DD8293B89354CE` |

Close the setup GUI and any active setup operation before customer-approved
replacement. Use the actual downloaded ZIP path (attachments may prefix `0-`),
check `Test-Path -LiteralPath ... -PathType Leaf` before hashing, then require
the ZIP hash above. Follow only the already-approved archive handling procedure;
do not bypass policy or recursively unblock files. Extract to a new staging
directory, verify exactly the one named module and its fixed hash, and stop on
any discrepancy.

In the existing
`C:\9\evaluation-a5105db500b68cd7f283fd1cc7c8495c97896da0\reviewed-a5105db5\ci-evaluation`
package, require `setup\ScomSetup.Windows.psm1` to match one of the two original
hashes above. Make a uniquely named backup of that file, verify the backup hash,
replace **only** that module, and verify its fixed hash before restarting
`ScomSetup.exe` normally. On replacement verification failure stop and restore
the verified backup. Preserve the earlier `ScomEntra.psm1` repair, all private
state, identity JSON and certificates; do not rerun initialization or Entra
provisioning. The package's original manifest is intentionally unchanged and
will no longer attest the replaced module; retain the backup and repair
checksums as the explicit local exception, not as a newly CI-built package.

For a read-only catalog retry in an already-open PowerShell process, force-reload
the exact package module before calling `Get-ScomSetupLocalCatalog`; cached
module code can outlive a file replacement. A newly launched GUI uses a fresh
bridge process. Do not select/install anything automatically during this check.

Local validation confirmed the Windows PowerShell 5.1 provider property type,
and 46 synthetic Windows checks passed, covering provider-shaped server-auth
EKU, unsupported/no EKU, missing private key, expired and not-yet-valid
certificates. Windows PowerShell 5.1 suite execution on the development host
remains blocked by its unsigned-script policy; no bypass was used. Customer
catalog/GUI recovery with this repair is **not yet verified**.

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
[reader setup](../README.md#azure-iis-and-copilot-studio-reader-setup). No secret is
created/exported by this script. Do not invent redirect URIs or use an
administrator/shared reader connection.

Graph v1.0 contracts:
[applications create](https://learn.microsoft.com/en-us/graph/api/application-post-applications?view=graph-rest-1.0),
[application update](https://learn.microsoft.com/en-us/graph/api/application-update?view=graph-rest-1.0),
[service principal create](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-serviceprincipals?view=graph-rest-1.0),
[publisher role assignment](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-approleassignments?view=graph-rest-1.0).

### MCPTest: a separate publisher certificate and app, step by step

Use this lane when the administrator has chosen a **new isolated test publisher**
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
2. **Create one dedicated publisher key on MCPTest.** Run the following in elevated
   Windows PowerShell 5.1 on the intended worker host, only after customer approval.
   Select an existing approved local directory for the public `.cer` file.
   This is a lab client-authentication certificate, **not** TLS or code signing.
   No Root/TrustedPublisher import or PowerShell trust change is needed. The
   software key is nonexportable; do not generate or request a PFX.

```powershell
$ErrorActionPreference = 'Stop'
$subject = 'CN=SCOM-MCPTest-Worker-Publisher'
$publicFile = 'C:\ApprovedCerts\SCOM-MCPTest-Worker-Publisher.cer'
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
key on MCPTest. Use customer-managed enrollment/renewal instead where self-signed
lab credentials are not allowed.

3. **Create only the new publisher app.** In **App registrations**, first check
   whether the exact intended test app already exists. If it does, inspect its
   recorded object/client IDs rather than registering another. Otherwise select
   **New registration**, name it `SCOM MCPTest Worker Publisher`, select **Accounts
   in this organizational directory only**, leave Redirect URI empty and register.
   Record the Directory (tenant) ID, Application (client) ID and this registration's
   Object ID. Do not create a client secret or add a redirect for this app.
4. **Upload public certificate only.** In the new publisher's **Certificates &
   secrets > Certificates > Upload certificate**, upload the exported DER `.cer`.
   Compare the complete portal thumbprint and validity dates with the local
   `LocalMachine\My` certificate, not just its subject or last characters. Stop on
   mismatch. Never upload/export the private key or modify the existing publisher.
5. **Approve only the ingestion role.** In the new publisher's **API permissions >
   Add a permission > My APIs**, choose the reviewed API by its exact client ID,
   select **Application permissions**, then `Scom.Ingest`. The publisher does not
   need delegated `Scom.Read` or Microsoft Graph runtime permissions. If a default
   Graph `User.Read` permission exists on this newly created publisher, remove that
   default only; do not alter any existing application. An authorized tenant
   administrator must explicitly review and **Grant admin consent** for this
   tenant. Confirm `Scom.Ingest` shows Granted for the intended resource, and inspect
   the corresponding Enterprise application. Adding a requested permission alone
   is not an app-role grant. Stop if the API/role cannot be identified exactly.
6. **Grant private-key Read to the exact worker account.** Confirm the Windows
   account selected in setup; do not guess an account from the app display name.
   On MCPTest open `certlm.msc` as the authorized administrator, then **Personal >
   Certificates**, locate the exact full thumbprint, right-click **All Tasks >
   Manage Private Keys**, and add **Read** only for that confirmed worker account.
   Preserve existing SYSTEM/administrator permissions; do not grant Everyone,
   Users, the IIS pool or Full Control, replace the ACL wholesale, or export the
   key. Stop if Manage Private Keys is absent or the account cannot be resolved.
7. **Verify actual RSA-PSS key access as the worker.** Open a normal approved
   Windows session as that exact account (not the administrator or IIS identity)
   and run this local, non-network challenge after substituting the recorded
   thumbprint. Certificate presence/`HasPrivateKey` alone is not proof of access.

```powershell
$ErrorActionPreference = 'Stop'
$thumbprint = '<full-40-hex-MCPTest-publisher-thumbprint>'
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

8. **Generate the validated nonsecret identity file.** The following IDs have
   different meanings; never substitute display names or swap object/client IDs.

| Value | Correct source / use |
|---|---|
| Tenant ID | Intended tenant's Directory ID |
| API client ID | API App registration's Application ID; forms `api://<API-client-ID>` |
| API/reader/publisher registration Object IDs | App registrations > Overview; script reuse inputs `-ApiObjectId`, `-ReaderObjectId`, `-PublisherObjectId` |
| Publisher client ID | New test publisher's Application ID; worker token client |
| Publisher service-principal Object ID | Enterprise applications > exact test publisher > Overview; output `publisherObjectId` for receiver authorization, **not** `-PublisherObjectId` input |
| Certificate thumbprint | Full matching local/portal public certificate thumbprint; worker private key stays local |

Use an authorized provisioning workstation/session with the existing approved
Global-cloud delegated Graph context and a protected journal initialized through
`Initialize-ScomSetupState.ps1`. The Graph administration scopes belong to that
administrator session, **not the publisher's runtime permissions**. Supply all
three reviewed registration Object IDs to avoid creating more apps:

```powershell
.\setup\Provision-ScomEntra.ps1 -TenantId '<approved-tenant-guid>' `
    -NamePrefix 'SCOM-MCPTest' -PublicCertificatePath 'C:\ApprovedCerts\SCOM-MCPTest-Worker-Publisher.cer' `
    -StateDirectory 'C:\ApprovedState\ScomEntra-MCPTest' `
    -ApiObjectId '<existing-api-registration-object-guid>' `
    -PublisherObjectId '<new-MCPTest-publisher-registration-object-guid>' `
    -ReaderObjectId '<existing-reader-registration-object-guid>'
```

Omit `-GrantPublisherRole` here because the administrator explicitly granted it
in the portal. The script reads API/client/certificate contracts, service
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

For `ENTRA_EXISTING_PUBLISHER_CERTIFICATE_CONFLICT_NO_KEY_OVERWRITE` after the
publisher app and public certificate already exist, use only a reviewed package
containing this comparison fix. On the authorized provisioning workstation:

1. Use the actual full path of the downloaded repair ZIP; the chat attachment
   download may prefix the filename (for example, `0-`). Do not hash a guessed
   path. Verify the file exists before reading it, then check its published hash:

```powershell
$zipPath = '<actual-full-path-to-downloaded-ScomEntra-keyidentifier-fix.zip>'
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw 'Repair ZIP not found; check the actual downloaded filename and path.' }
if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -cne
    '2D8B6194B74C141FD480D5F03B2D1EAD0012B1060144D4B850F90C15D45C1B96') {
    throw 'Repair ZIP hash mismatch; do not extract or run it.'
}
```

2. Extract only after that check to a new, empty staging directory. Confirm it
   contains only `ScomEntra.psm1` and that the module hash is
   `B582AA5F2A7CFAFA2A4BC996A471F30112FBDC5960736890C0E697E395F427BE`.
   If using the package archive's trusted-download procedure, apply only its
   explicitly approved archive-only handling; do not recursively unblock files
   or change execution policy.

```powershell
$stagingPath = Join-Path $env:TEMP 'ScomEntra-keyidentifier-fix'
if (Test-Path -LiteralPath $stagingPath) { throw 'Staging directory already exists; choose a new empty directory.' }
Expand-Archive -LiteralPath $zipPath -DestinationPath $stagingPath -ErrorAction Stop
$stagedFiles = @(Get-ChildItem -LiteralPath $stagingPath -File -Recurse)
if ($stagedFiles.Count -ne 1 -or $stagedFiles[0].Name -cne 'ScomEntra.psm1') {
    throw 'Repair ZIP did not extract as exactly one ScomEntra.psm1; do not install it.'
}
$fixedModulePath = $stagedFiles[0].FullName
if ((Get-FileHash -LiteralPath $fixedModulePath -Algorithm SHA256).Hash -cne
    'B582AA5F2A7CFAFA2A4BC996A471F30112FBDC5960736890C0E697E395F427BE') {
    throw 'Fixed module hash mismatch; do not install it.'
}
```
3. Keep the existing state directory and already-exported public DER `.cer`.
   In the confirmed customer retry, the original error occurred before
   `entra-journal.json` existed. Do **not** rerun first-run state initialization,
   replace/export the certificate, or create another app or credential. The
   corrected provisioner creates its journal in the existing state directory.
4. Before replacement, confirm the package root and module path, then require
   the existing package module to match the published baseline bytes. The
   Git-source copy differs from the CI ZIP only by line endings, so either exact
   hash is acceptable:

```powershell
$packageRoot = 'C:\9\evaluation-a5105db500b68cd7f283fd1cc7c8495c97896da0\reviewed-a5105db5\ci-evaluation'
$modulePath = Join-Path $packageRoot 'setup\ScomEntra.psm1'
if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) { throw 'Expected package module not found.' }
$originalHash = (Get-FileHash -LiteralPath $modulePath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($originalHash -cnotin @(
    '1ADCFBBA3079E73B6E2F1E6D842EA25600D661ACB8D6B37325322F73F544CA66',
    '02737EBEC52DDA04349FD1DCD2E04C1754D16066780877E541CE7F087EC0AC8A'
)) { throw 'Package module is not a verified baseline; stop without replacing it.' }
if (-not (Test-Path -LiteralPath $fixedModulePath -PathType Leaf) -or
    (Get-FileHash -LiteralPath $fixedModulePath -Algorithm SHA256).Hash -cne
    'B582AA5F2A7CFAFA2A4BC996A471F30112FBDC5960736890C0E697E395F427BE') {
    throw 'Fixed module missing or hash mismatch; stop.'
}
$backupPath = "$modulePath.before-keyidentifier-fix-$(Get-Date -Format yyyyMMdd-HHmmss).bak"
Copy-Item -LiteralPath $modulePath -Destination $backupPath -ErrorAction Stop
if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash.ToUpperInvariant() -cne $originalHash) {
    throw 'Backup verification failed; do not replace the package module.'
}
Copy-Item -LiteralPath $fixedModulePath -Destination $modulePath -Force -ErrorAction Stop
if ((Get-FileHash -LiteralPath $modulePath -Algorithm SHA256).Hash -cne
    'B582AA5F2A7CFAFA2A4BC996A471F30112FBDC5960736890C0E697E395F427BE') {
    Copy-Item -LiteralPath $backupPath -Destination $modulePath -Force -ErrorAction Stop
    throw 'Replacement verification failed; original module restored from the verified backup.'
}
```

5. In the **same authenticated PowerShell process**, unload the old in-memory
   module and import the exact replaced file. Replacing a file on disk does not
   update module code already loaded in that process. Verify the active command
   comes from this path and its definition calls the new identifier decoder:

```powershell
Remove-Module -Name ScomEntra -Force -ErrorAction SilentlyContinue
Import-Module -Name $modulePath -Force -ErrorAction Stop
$command = Get-Command Invoke-ScomEntraProvisioning -ErrorAction Stop
if ($command.Module.Path -ne $modulePath -or
    $command.Definition -notmatch 'ConvertFrom-ScomEntraKeyIdentifier') {
    throw 'Corrected provisioning function is not loaded from the replaced package module.'
}
```

6. Retry only after confirming the state and active module. In the confirmed
   customer case there was no journal before this retry; preserve the existing
   state directory and certificate. If a journal has appeared, inspect it
   read-only and stop if `pending` is non-null. Use the same tenant, state
   directory, public `.cer`, and
   reviewed API, publisher and reader **app-registration Object IDs** used for
   the existing resources.
   Do not include `-GrantPublisherRole` when the role is already granted. Cancel
   unexpected create/grant prompts. Rerun the guarded provisioner:

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

**Verified customer outcome:** the local repair passed hash verification, the
original module was backed up, and the customer force-reloaded the exact package
module after confirming its exported provisioning function contained
`ConvertFrom-ScomEntraKeyIdentifier`. The identity-only retry then saved the
nonsecret identity successfully, without repeating state initialization or
certificate export. Configure the Studio-generated Web callback, protected
OAuth credential and scoped delegated reader consent separately; this success
does not attest those gates or deploy/configure DNS, firewall, TLS or a live
collector.

No existing reader callback/credential, production publisher key, DNS record or
TLS binding is changed by these instructions. The approved host name, HTTPS
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
[reader/data acceptance](../README.md#azure-iis-and-copilot-studio-reader-setup):
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
