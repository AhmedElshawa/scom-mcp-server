# Supervised indexed-run wrapper

This directory contains an optional, review-only wrapper for one supervised
`--worker-once` invocation. It is not a recurring collector, production
acceptance, or authorization to use any real worker configuration.

## Synthetic validation

Run the complete local-only suite from the repository root with the approved
PowerShell engine:

```powershell
powershell.exe -NoProfile -File .\Operations\SupervisedIndexedRun\Run-SyntheticSupervisedIndexedRunTests.ps1
pwsh -NoProfile -File .\Operations\SupervisedIndexedRun\Run-SyntheticSupervisedIndexedRunTests.ps1
```

The Windows CI workflow runs both engines independently on Windows Server 2022
and retains a sanitized test summary tied to the exact pull-request head. The
suite creates dummy DLL/config files and bounded local synthetic processes; it
does not call SCOM, SQL, a real profile, or a customer source. Worker and
competitor processes are contained in owned Windows jobs and closed during
cleanup. A nonzero test exit, failed assertion, or missing passing summary
fails the job.

The scripts are unsigned. Follow the host's normal signing and execution
policy; neither the workflow nor the test instructions bypass or weaken it.
Hosts that require trusted signatures must use their standard approved
signing/trust process. The wrapper requires Windows 10 / Server 2016 or later
and job nesting permitted by the host; unsupported job-list creation fails
closed before worker code starts.

## Scope and limitations

- The launcher is still one-shot (`--worker-once`), not recurrence.
- Monitoring covers local worker/child process memory and elapsed time only; it
  does not monitor SCOM, SQL, service health, backend cancellation, or receiver
  commit state.
- Process matching parses Windows arguments and PowerShell AST invocations to
  avoid treating marker text as an actual worker call. Discovery remains
  best-effort, is not an atomic cross-launch mutex, and cannot resolve arbitrary
  dynamic PowerShell execution.
- A manual stop confirms the root process stop only. Treat
  `root_stopped_child_join_pending` as pending launcher-side child cleanup, not
  proof that the whole process tree is gone.
- This package does not configure ACLs, sign scripts, provision deployment
  settings, run real SCOM collection, or constitute production/customer
  acceptance. Preserve unknown cleanup state and do not retry an uncertain
  launch or stop.

`REPAIR-RESULTS.txt`, `README-REPAIRED.txt`, and
`verified-ps7-results.json` are preserved evidence/notes from the imported
review package, not results from this repository's CI run. See
`PROVENANCE.md` for the source archive identity and evidence scope.
