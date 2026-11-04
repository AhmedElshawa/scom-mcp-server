# Native SCOM setup

`ScomSetup.exe` is a Windows x64, .NET 8 Windows Forms **WinExe** with an
`asInvoker` manifest. Opening it does not run PowerShell, inspect journals,
contact SCOM, inventory, install, or request elevation. It contains no package
references, credentials, impersonation, automatic approval or policy bypass.
Host and exact-worker lanes remain separate. The bridge validates authorization
and configuration; UI navigation and field validation do not establish trust.

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
policy overrides are not forwarded.

Request properties:

* `action`: `Preflight`, `Discover`, `Prepare`, `Configure`, `Activate`, `Status`,
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
* `selection`: selected group GUID string, `"ALL"` for whole-Windows, or null.
  The selected group's GUID is also set in `plan.groupId`.
* `approval`: null initially, then the **exact** returned `summary` after Yes.
* `installIis`, `hostingBundlePath`, `hostingBundleVersion`,
  `hostingBundleSha256`, `acknowledgeRestart`.

Response: `ok`, `code`, `message`, `checks: [{name,passed}]`,
`groups: [{id,displayName}]`, `stage`, `pendingAction`, `approvalRequired`,
`summary`, optionally `plan`. Unknown members are ignored for compatibility.
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
