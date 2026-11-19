# Install with the CLI (runbook)

Eight commands, in order. Run each from the package root in **Windows PowerShell 5.1**.
Stop at the first command that does not print its expected line; do not retry blindly.

Placeholders: `<pkg>` = signed package folder; `<state>` = new state directory
(ACL-restricted by step 1); `<plan-input>` = the plan JSON you reviewed;
`<private>` = the plan's `privatePath`.

Lanes: **Host** = elevated shell as a local administrator. **Worker** = a separate
session as the approved read-only SCOM worker account
([service-accounts.md](service-accounts.md)); it must be able to read `<private>\worker.json`.

## Prerequisites

- Windows Server host(s) with IIS and the **.NET 8 ASP.NET Core Hosting Bundle** (install it after IIS).
- A public TLS certificate for `<FQDN>` ([network-and-ports.md](network-and-ports.md)); the host resolves its own FQDN.
- Entra registrations and a reviewed `identity.json` ([entra-setup.md](entra-setup.md)).
- Reviewed receiver and worker settings, based on [`worker.example.json`](../examples/worker.example.json) and [`receiver.example.json`](../examples/receiver.example.json).
- The worker account and permissions ([service-accounts.md](service-accounts.md)); the SCOM Operations console, PowerShell module and a reachable management server.
- No pending reboot; the parent directory of the install path already exists.
- Use the package built from this repository at or after commit `55f3062` (the publication-marker fix). The package is unsigned unless you sign it yourself ([local-evaluation-signing.md](local-evaluation-signing.md)).

## Commands

| # | Lane | Command | Expected output line |
|---|------|---------|----------------------|
| 1 | Host | `.\setup\Initialize-ScomSetupState.ps1 -Path <state>` (type `APPROVE`) | `Private journal directory ready.` |
| 2 | Host | `.\setup\Start-ScomSetup.ps1 -StateDirectory <state> -Step Prepare -PlanPath <plan-input>` (all preflight lines `PASS`; type `APPROVE` at each prompt) | `SETUP_STAGE=prepared. No recurrence, metrics or targeted-live enabled.` |
| 3 | Worker | `.\setup\Start-ScomWorkerSetup.ps1 -PlanPath <private>\setup-plan.json -Step Connect` | `SCOM_SETUP_CONNECTION_GROUP_VISIBLE_NOT_SCOPE_CERTIFIED` (expected for management-group scope) |
| 4 | Host | `.\setup\Start-ScomSetup.ps1 -StateDirectory <state> -Step Configure -IdentityPath <identity.json> -ReceiverSettingsPath <receiver.json> -WorkerSettingsPath <worker.json>` | `SETUP_STAGE=configured. No recurrence, metrics or targeted-live enabled.` |
| 5 | Host | `.\setup\Start-ScomSetup.ps1 -StateDirectory <state> -Step Activate` (the server must resolve its own HTTPS FQDN) | `HTTPS reachable only; inventory and reader acceptance NOT proven. ...` then `SETUP_STAGE=activated. ...` |
| 6 | Worker | `.\setup\Start-ScomWorkerSetup.ps1 -PlanPath <private>\setup-plan.json -Step Publish` (one SCOM collection) | `SCOM_WORKER_PUBLISHED observed after checkpoint acknowledgement. ...` |
| 7 | Host | `.\setup\Start-ScomSetup.ps1 -StateDirectory <state> -Step Publish` | `Publication observed; complete intended-user Studio/tool and unauthorized-user acceptance before Accept.` then `SETUP_STAGE=published. ...` |
| 8 | Host | `.\setup\Start-ScomSetup.ps1 -StateDirectory <state> -Step Accept` (only after the acceptance checks below; type `APPROVE`) | `SETUP_STAGE=accepted. No recurrence, metrics or targeted-live enabled.` |

Setup never enables recurrence. Register the recurring worker task afterwards:
[operations.md](operations.md#recurrence).

## Acceptance checks (between steps 7 and 8; not setup commands)

1. `GET /healthz` returns 200.
2. `POST /mcp` without a token returns 401.
3. `get_source_status` as an allowed caller returns the expected computer count with complete coverage.
4. The Operations console computer count matches the inventory.
5. `get_inventory_summary` counts and health match.
6. `get_device_details` returns HTTP 200 for each computer.
7. A signed-in user who is not an allowed caller gets 403.
8. A reader token against the ingestion endpoint gets 403.

Accept is an **operator attestation**: it records that you did these checks; it does
not run them. State which ones you skipped.

## Failure notes

- Step 2: `SETUP_PREREQUISITES_BLOCKED` (pending reboot) or `SETUP_EXISTING_PARENT_DIRECTORY_REQUIRED` (create the parent of the install path). Fix the cause and rerun.
- Step 5: if the server cannot resolve its own HTTPS FQDN, activation fails and a retry is refused with `SETUP_INTERRUPTED_ACTION_REQUIRES_RECOVERY`. `-Step Recover` does not clear a pending action; clearing it needs an operator-reviewed journal edit and removal of the half-created site and pool. Resolve DNS **before** step 5.
- Step 6: requires commit `55f3062` or later; earlier packages fail `SETUP_PUBLICATION_NOT_ACKNOWLEDGED_OR_CONFIGURATION_CHANGED`.
- Step 8: `SETUP_ADMINISTRATOR_REQUIRED_FOR_HOST_LANE` from a non-elevated shell.

More codes: [troubleshooting.md](troubleshooting.md). GUI alternative:
[install-wizard.md](install-wizard.md).
