# Install with the setup wizard (evaluation)

> **Evaluation.** The GUI (`ScomSetup.exe`) drives the same state machine as the CLI
> ([install-cli.md](install-cli.md)). It is an evaluation aid: the CLI runbook is the
> path validated end to end in the lab. The wizard bootstrap self-check
> (`--diagnose-bootstrap`) passed in the lab, but a full wizard-driven production
> install has not been certified. The package is unsigned unless you sign it.

The wizard is shipped self-contained (`win-x64`) in the package. Run it elevated
on the receiver host. It never stores secrets, never enables recurrence, metrics or
targeted-live checks, and records everything in a private journal.

## Walkthrough

1. **Start.** Run `ScomSetup.exe` from the extracted package as a local administrator.
   Optionally run `ScomSetup.exe --diagnose-bootstrap` first to check the runtime.
2. **Plan.** Provide or review the plan: install and private paths, scope (group or
   management group), `<MG_ID>`, `<GROUP_ID>`, `<MGMT_SERVER_FQDN>`, receiver host/site
   and the `<FQDN>`. Review the preflight results; every line must pass.
3. **Prepare.** Approve each prompt. The wizard creates the private state and journal.
4. **Connect (worker lane).** Run the worker-lane step as the read-only worker account.
   For management-group scope the expected result is *group visible, not scope certified*.
5. **Configure.** Select your `identity.json` and reviewed receiver and worker settings
   ([`worker.example.json`](../examples/worker.example.json), [`receiver.example.json`](../examples/receiver.example.json)). The wizard merges identity, thumbprint, script hash,
   publisher endpoint and state paths, and refuses settings that change the approved scope.
6. **Activate.** Creates the IIS pool and site (or subapplication), the HTTPS binding and the
   deployment. Success proves HTTPS reachability only.
7. **Publish (worker lane, then host lane).** One SCOM collection is published and observed.
8. **Acceptance.** Do the eight checks in [install-cli.md](install-cli.md#acceptance-checks-between-steps-7-and-8-not-setup-commands)
   yourself, then **Accept**. Accept is an attestation.

Screenshots of each wizard page: see [screenshots/README.md](screenshots/README.md).
Complete wizard details and every page's fields: [guided-setup.md](guided-setup.md).
