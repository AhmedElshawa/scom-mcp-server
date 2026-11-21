# Screenshots

Committed so far: the five `entra-setup-*.png` files, `copilot-studio-connection.png`,
`copilot-studio-tools.png`, `readme-agent-answer.png`, `readme-agent-count.png`,
`iis-https-binding.png` and `terminal-accept.png` (real captures from the lab with the
tenant, account, hostnames and all identifiers redacted), plus `copilot-studio-connector.png`
(the blank "Add a Model Context Protocol server" form: OAuth 2.0 / Manual with every field empty; it
shows the form layout only, not the lab connector's values). The files marked **pending** in the
table below were not captured; the reason for each is given after the table. Screenshots show lab or customer identifiers (hostnames,
tenant, app and object GUIDs, IP addresses, thumbprints, user names), so each one is
redacted before it is committed.

Capture, **redact** (opaque filled rectangles only; never blur or pixelate hostnames, GUIDs,
IPs, thumbprints, accounts), save as PNG at 1 MB or less and 1280 px wide or less with EXIF
stripped, and name by documentation section. `copilot-studio-tools.png` shows 13 tools, not
six.

`readme-agent-answer.png` shows the full answer: the lifecycle table, the
"Not in the lifecycle catalog." cell, Key Findings, the Data Status block and Limitations.

Status of the files not committed: `wizard-prerequisites.png` and `wizard-configuration.png`
are **evaluation: pending**. They were deliberately skipped in this round; the GUI wizard
screens are to be captured from the signed package with a separate state directory so that the
accepted install is not touched.

| File name | Shows | Used in |
|---|---|---|
| `readme-agent-answer.png` | Copilot Studio agent answering "Which servers are nearest end of support?" (full answer including Data Status; server names redacted) | [deployment-guide.md](../deployment-guide.md), [README](../../README.md#7-screenshots), [copilot-studio-integration.md](../copilot-studio-integration.md) |
| `readme-wizard.png` | Setup wizard main page | [README](../../README.md#7-screenshots), [install-wizard.md](../install-wizard.md) |
| `install-wizard-plan.png`, `install-wizard-activate.png`, `install-wizard-accept.png` | Wizard plan, activate and accept pages | [install-wizard.md](../install-wizard.md) |
| `entra-setup-api-expose.png` | API app, Expose an API (scope `Scom.Read`) | [entra-setup.md](../entra-setup.md) |
| `entra-setup-api-approles.png` | API app, App roles (`Scom.Ingest`) | [entra-setup.md](../entra-setup.md) |
| `entra-setup-publisher-cert.png` | Publisher app, Certificates (public cert only) | [entra-setup.md](../entra-setup.md) |
| `entra-setup-publisher-permissions.png` | Publisher app, API permissions with admin consent granted | [entra-setup.md](../entra-setup.md) |
| `entra-setup-reader-permissions.png` | Reader app, delegated `Scom.Read` | [entra-setup.md](../entra-setup.md) |
| `copilot-studio-connector.png` | Add a Model Context Protocol server form (OAuth 2.0, Manual; all fields empty, so no secret or URL visible; form layout only, not a configured connector) | [deployment-guide.md](../deployment-guide.md) |
| `copilot-studio-connection.png` | Connection created and tool added to the agent | [copilot-studio-integration.md](../copilot-studio-integration.md) |
| `copilot-studio-tools.png` | Agent Tools page, all tools enabled (13 tools) | [deployment-guide.md](../deployment-guide.md) |
| `readme-agent-count.png` | Agent answering "How many servers do we have?" (server names redacted) | [deployment-guide.md](../deployment-guide.md) |
| `iis-https-binding.png` | IIS Manager https binding edit dialog (host, site and certificate redacted) | [deployment-guide.md](../deployment-guide.md) |
| `wizard-prerequisites.png`, `wizard-configuration.png` | Setup wizard prerequisites and configuration screens (evaluation: pending, not captured) | [install-wizard.md](../install-wizard.md) |
| `terminal-accept.png` | Terminal showing `SETUP_STAGE=accepted` | [deployment-guide.md](../deployment-guide.md) |

After adding a file, embed it with a normal relative image link in the section named above.
