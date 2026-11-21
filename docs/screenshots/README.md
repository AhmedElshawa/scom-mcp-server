# Screenshots

Committed so far: the five `entra-setup-*.png` files, `copilot-studio-connection.png`,
`copilot-studio-tools.png` and `readme-agent-answer.png` (real captures from the lab tenant with the tenant, account and
all identifiers redacted). The others in the table below are still **pending**; the reason
for each is in the Status column. Screenshots show lab or customer identifiers (hostnames,
tenant, app and object GUIDs, IP addresses, thumbprints, user names), so each one is
redacted before it is committed.

Capture, **redact** (opaque filled rectangles only; never blur or pixelate hostnames, GUIDs,
IPs, thumbprints, accounts), save as PNG at 1 MB or less and 1280 px wide or less with EXIF
stripped, and name by documentation section. `copilot-studio-tools.png` shows 13 tools, not
six.

`readme-agent-answer.png` is a partial capture: the question, the lifecycle table, the
"Not in the lifecycle catalog." line and Key Findings are visible; the Data status block is
below the visible area of the Test pane.

Status of the files not yet committed: `copilot-studio-connector.png`,
`readme-agent-count.png`, `iis-https-binding.png`, `wizard-prerequisites.png`,
`wizard-configuration.png` and `terminal-accept.png` were not captured. The Copilot Studio tool
detail page shows no server URL, transport or OAuth fields (those live in the Power Apps custom
connector, which was not reachable in the automated browser session), and the remaining
captures need interactive Bastion or Test-pane sessions that were not completed.

| File name | Shows | Used in |
|---|---|---|
| `readme-agent-answer.png` | Copilot Studio agent answering "Which servers are nearest end of support?" (partial; server names redacted) | [deployment-guide.md](../deployment-guide.md), [README](../../README.md#7-screenshots), [copilot-studio-integration.md](../copilot-studio-integration.md) |
| `readme-wizard.png` | Setup wizard main page | [README](../../README.md#7-screenshots), [install-wizard.md](../install-wizard.md) |
| `install-wizard-plan.png`, `install-wizard-activate.png`, `install-wizard-accept.png` | Wizard plan, activate and accept pages | [install-wizard.md](../install-wizard.md) |
| `entra-setup-api-expose.png` | API app, Expose an API (scope `Scom.Read`) | [entra-setup.md](../entra-setup.md) |
| `entra-setup-api-approles.png` | API app, App roles (`Scom.Ingest`) | [entra-setup.md](../entra-setup.md) |
| `entra-setup-publisher-cert.png` | Publisher app, Certificates (public cert only) | [entra-setup.md](../entra-setup.md) |
| `entra-setup-publisher-permissions.png` | Publisher app, API permissions with admin consent granted | [entra-setup.md](../entra-setup.md) |
| `entra-setup-reader-permissions.png` | Reader app, delegated `Scom.Read` | [entra-setup.md](../entra-setup.md) |
| `copilot-studio-connector.png` | Custom MCP connector settings (no secret visible) | [copilot-studio-integration.md](../copilot-studio-integration.md) |
| `copilot-studio-connection.png` | Connection created and tool added to the agent | [copilot-studio-integration.md](../copilot-studio-integration.md) |
| `copilot-studio-tools.png` | Agent Tools page, all tools enabled (13 tools) | [deployment-guide.md](../deployment-guide.md) |
| `readme-agent-count.png` | Agent answering "How many servers do we have?" (pending) | [deployment-guide.md](../deployment-guide.md) |
| `iis-https-binding.png` | IIS Manager https binding edit dialog (pending) | [deployment-guide.md](../deployment-guide.md) |
| `wizard-prerequisites.png`, `wizard-configuration.png` | Setup wizard prerequisites and configuration screens (pending) | [install-wizard.md](../install-wizard.md) |
| `terminal-accept.png` | Terminal showing `SETUP_STAGE=accepted` (pending) | [deployment-guide.md](../deployment-guide.md) |

After adding a file, embed it with a normal relative image link in the section named above.
