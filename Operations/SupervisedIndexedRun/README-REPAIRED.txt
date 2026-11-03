Corrected external supervised-worker wrapper package

Read REPAIR-RESULTS.txt first. No live use is authorized by this ZIP.
The full Windows PowerShell 5.1 suite is blocked by unsigned-script policy on
the repair host; only its independent native exit-code probes passed.

Keep all source files together, including SupervisedProcess.cs.
No execution-policy bypass, remote script download or automatic signing.
Use normal approved signing/trust procedures before running on AllSigned hosts.

Synthetic-only commands on an authorized test host:
  pwsh -NoProfile -File .\Run-SyntheticSupervisedIndexedRunTests.ps1
  powershell.exe -NoProfile -File .\Run-SyntheticSupervisedIndexedRunTests.ps1

The harness uses dummy DLL/config files and bounded local sleep/exit/memory
processes, not SCOM. Each command must exit0 and summary passed must be true.
Any failure exits nonzero. Run on a test host without competing collections.
Independent child probes inside the suite verify exit0,7,23; no weakened
exit-code assertion or null-to-zero conversion is allowed. The suite also starts
two bounded owned jobs for competitor regression: a harmless marker must not
block launch; a synthetic sleeping script with the actual profile file name
must block launch without being killed by the launcher.

Actual worker invocation remains one-shot and is outside this review:
the script defaults to the existing DLL/config paths but their existence does
not authorize source execution. All memory/time/breach parameters are mandatory.
Simulation/custom-worker parameters are for synthetic tests only.

Launcher final run-state worker.childrenTerminatedVerified=true means it observed
its job empty. Manual Stop returns root_stopped_child_join_pending until launcher
verification; do not treat it alone as entire child cleanup proof.
launcher-failure.json records failed setup cleanup, including unknown stop outcome.
If cleanup is unverified, stop and retain evidence; do not retry/launch another
collector or kill unrelated services. No SCOM, SQL or IIS service stop is supplied.

Do not treat output strings or process exit alone as receiver commit evidence.
Do not include this ZIP in a release until full Desktop suite, source-owner
controls and separate customer acceptance are complete.
