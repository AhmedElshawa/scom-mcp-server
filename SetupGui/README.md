# Native SCOM setup

`ScomSetup.exe` is a Windows x64, .NET 8 Windows Forms **WinExe** with an
`asInvoker` manifest. The first displayed screen automatically launches a
**read-only local Bootstrap** via Windows PowerShell to check prerequisites and
list IIS sites/certificates. It does not inspect deployment journals, contact
SCOM, inventory, install, or request elevation. Constructing the form or running
the probe never launches PowerShell. It contains no package
references, credentials, impersonation, automatic approval or policy bypass.
Host and exact-worker lanes remain separate. The bridge validates authorization
and configuration; UI navigation and field validation do not establish trust.

## Guided screens

Only Back/Next navigation is exposed; there are no selectable page tabs.
The first screen shows actual green PASS/red BLOCKED results with remedies.
No results, failed communication, or any failed check blocks staging/configuring/
activation, but the operator may inspect the next basic-choice screens while
prerequisites remain blocked. The ordinary screens ask for one existing local
NTFS log parent (separate new `ScomMcp-McpLogs`/`ScomMcp-CollectorLogs` children),
an existing discovered IIS site and NEW application name, an existing
worker account, then the management server and approved group choice. Existing
applications/pools are never silently reused: the proposed dedicated pool name
avoids discovered pool names and existing application names are refused.

Advanced holds proposed sibling installation/state/journal paths, module/GUID/
HTTPS/certificate overrides, discovered certificate selection, externally
reviewed JSON inputs, local prerequisite installer details, and explicit resume.
Bootstrap defaults apply only once and do not replace edited fields or resumed
plans. The GUI proposes existing `ProgramData` as the parent with new
`ScomMcp-McpLogs` and `ScomMcp-CollectorLogs` children; both
effective paths are displayed read-only on the basic screen. Advanced can
override them individually, and resuming preserves both exact saved paths.
Unknown site/certificate catalogs remain unknown; they do not earn PASS.
Only explicitly approved source discovery may fill the management-group GUID.
Configure/activation require all three selected JSON files to exist; existence
does not certify their contents, consent, key access or source mappings. The
backend remains authoritative and verifies effects after exact confirmation.

The worker account is forwarded to the plan/ACL backend. An explicit handoff
button launches Windows `runas.exe /user:DOMAIN\account "<package>\ScomSetup.exe"`
without `/savecred`, `/netonly`, redirection or application credential fields.
Credentials are entered only in the Windows RunAs console. The existing process
identity does not change; the new process must load the installed private plan
and pass the backend's exact-account check. Cancel/failure does not run a worker
bridge action. Managed/noninteractive accounts need an approved worker session;
this wizard cannot manufacture interactive logon rights.

## Build and package

From the repository root, with the .NET 8 or newer SDK on the build machine:

```powershell
dotnet restore .\SetupGui\ScomSetup.csproj --locked-mode
dotnet publish .\SetupGui\ScomSetup.csproj -c Release --no-restore -o .\SetupGui\publish
pwsh -NoProfile -File .\SetupGui.Tests\Test-PackagedGui.ps1 -PackageDirectory .\SetupGui\publish -ReportPath .\SetupGui.Tests\bootstrap-report.json
```

Copy **all** publish output into the complete reviewed package root alongside
`setup\Invoke-ScomSetupGui.ps1`. Keep `ScomSetup.exe`, its matching DLL,
deps/runtimeconfig files, native runtime and framework assets together. Do not
copy just the executable, rename the apphost independently, enable single-file
extraction or trimming. End-user startup does not need installed `dotnet`.
The server/Hosting Bundle prerequisite checks are separate from GUI startup.
No signing or publisher verification is implied by this project.

`packages.lock.json` is committed even with zero external package dependencies.
After deliberate SDK/runtime servicing updates regenerate it using
`dotnet restore .\SetupGui\ScomSetup.csproj --force-evaluate`, review the diff,
and return to locked restore. CI can also set `ContinuousIntegrationBuild=true`.
Build outputs and test reports are disposable; do not commit them.
`RuntimeFrameworkVersion=8.0.33` explicitly pins the runtime packs with
`TargetLatestRuntimePatch=false`; the empty dependency lockfile alone does not
pin framework pack servicing. This project uses no C# features newer than C# 12
and is intended for CI SDK 8.0.427. Local validation used SDK 9.0.320 against
the same pinned runtime. SDK selection itself belongs to the repository/CI,
not this project's dependency lockfile.

## JSON bridge contract

The UI launches the absolute Windows `System32\WindowsPowerShell\v1.0\powershell.exe`
with exactly `-NoProfile -File <package>\setup\Invoke-ScomSetupGui.ps1`.
It does not set execution policy, use `EncodedCommand`, or use a command shell.
Input is one UTF-8/no-BOM, newline-terminated JSON request on stdin, then EOF.
Output must be exactly one UTF-8 JSON object; stderr is drained and discarded.
Policy/signature failures remain failures and never trigger a bypass fallback.
The child environment is allowlisted Windows/user essentials; inherited
application configuration, `PSModulePath`, .NET overrides and process execution
policy overrides are not forwarded. PATH contains only canonical Windows
System32, Windows, and Program Files `dotnet`, not any caller PATH segment.
The GUI's bundled CLR does not satisfy the server runtime prerequisite.

Request properties:

* `action`: `Bootstrap`, `Preflight`, `Discover`, `Prepare`, `Configure`, `Activate`, `Status`,
  `Recover`, `InstallPrerequisites`, `Connect`, `PublishWorker`, `Publish`, `Accept`.
* `plan`: schema version 1 and every field in `Assert-ScomSetupPlan`.
  `packagePath` is always the executable directory. Default scope is `group`,
  `groupId: null`, `wholeManagementGroupApproved: false`.
* `stateDirectory`, `identityPath`, `receiverSettingsPath`, `workerSettingsPath`.
* `planPath`: optional installed private `setup-plan.json` path, used by explicit
  `Status`/worker resume. The UI never reads this file itself. A returned
  `plan` populates fields only following explicit successful `Status`.
  The explicit worker-plan checkbox controls this mode (browsing for a worker
  plan selects it). With the checkbox off, host Status sends an empty `planPath`
  even if a stale worker path remains in the field. Host effects never forward
  `planPath`. `SETUP_GUI_WORKER_PLAN_READY` explicitly reports no host-journal
  read and no known host stage/evidence.
  The installed worker path is proposed as `<privatePath>\setup-plan.json`,
  follows changes to the private directory unless explicitly overridden, and
  is never read automatically. The basic worker screen's explicit **Load
  installed worker plan** button selects worker mode and requests Status.
  Opening the handed-off process still performs only local Bootstrap.
* `selection`: selected group GUID string, `"ALL"` for whole-Windows, or null.
  The selected group's GUID is also set in `plan.groupId`.
* `approval`: null initially, then the **exact** returned `summary` after Yes.
* `installIis`, `hostingBundlePath`, `hostingBundleVersion`,
  `hostingBundleSha256`, `acknowledgeRestart`.

Response: `ok`, `code`, `message`, `checks: [{name,passed}]`,
`groups: [{id,displayName}]`, `stage`, `pendingAction`, `approvalRequired`,
`summary`, optionally `plan`. Unknown members are ignored for compatibility.
Bootstrap adds `defaults` (proposed plan), `stateDirectory`, `sites`
(`name`, `applications`, `httpsBindings:[{host,port}]`), `pools` (name strings),
`certificates` (`thumbprint`, `displayName`, `dnsNames`), `currentAccount`;
checks additionally expose `remedy`. `ok` can be false while a useful local
catalog is returned. Local SDK metadata checks are not connectivity, signature
verification or full compatibility certification. Successful approved Discover
may return `managementGroupId`, copied into the previously manual advanced field.
No raw messages, exception text or stderr are logged. Known static check names
and known remedies are displayed; source names are bounded and stripped of
control/format characters. An unsafe or oversized effect summary is refused;
a valid summary is displayed verbatim, not rewritten or truncated.

Every action starts unapproved. Prepare's first summary must include private
journal creation, plan save and installation effects, with no preceding
mutation. Whole-Windows selection requires its own separate default-No warning
before the action; only Yes sets `wholeManagementGroupApproved: true`.
The checkbox alone never sets this approval. This warning is repeated for
subsequent whole-scope effects rather than trusting a loaded approval flag.

If an action returns another `approvalRequired` after an approved effect,
the next exact summary requires another Yes. The dialog warns that prior
effects may already have completed. No does not roll back; there is no further
invocation. The bridge must journal/resume each accepted effect and revalidate
the request, approvals, paths, policy and exact caller itself.

No action is cancellable after bridge start; closing is deferred and clearly
warns that accepted effects are not rolled back. No bridge process is killed
or timed out. UAC restart is explicit and handles cancellation; unsaved form
entries are not transferred through arguments or disk.

## Read-only tests

`ScomSetup.exe --self-test [reportPath]` (alias `--smoke-readonly`) creates a real STA form
and handle with a bridge that throws on any use, tests field mapping, default
scope, UTF-8 request framing/round-trip and child-environment isolation.
Additional in-memory fake-bridge tests exercise default-No approval cancellation,
exact-summary resubmission without request changes, successive independent effects,
explicit prerequisite rerun, whole-scope-before-effect ordering, terminal failure,
busy duplicate suppression and deferred closing during a pending failed call.
They never launch PowerShell or perform host effects. Dialog decisions are
injected for these tests; actual UAC and human dialog interaction are not automated.
Tests also show the real form with a fake bridge to verify exactly one automatic
Bootstrap on Shown, sequential navigation, missing-prerequisite/evidence gating,
actual-result colors, derived log paths, safe placement/pool defaults, bootstrap
edit/resume preservation, and Windows RunAs argument construction without launching it.
It exits 0 on success, 1 on failure. An optional report contains `{ok:true,bridgeCalls:0}`.
No installed plan, SCOM or host action runs.

`ScomSetup.exe --bootstrap-probe <reportPath>` creates a form with the same
forbidden-call bridge and writes only the requested report:
`{selfContained, framework, processArchitecture, uiLoaded, runtimeDirectory, bridgeCalls}`.
The harness launches
the **published apphost**, with invalid `DOTNET_ROOT`/`DOTNET_ROOT_X64` and PATH
without dotnet, and verifies that the runtime directory is inside the package.
This proves isolated loader/runtime/UI startup, not a clean-server test, IIS
readiness, signing or SCOM operation.
Run the harness under PowerShell 7 on Windows. The report path must be new.
