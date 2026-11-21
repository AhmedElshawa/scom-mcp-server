# Screenshots

Committed so far: the five `entra-setup-*.png` files
(real captures from the lab tenant with the tenant, account and all identifiers redacted).
The others in the table below are still to be captured. Screenshots show lab or customer
identifiers (hostnames, tenant, app and object GUIDs, IP addresses, thumbprints, user
names), and capturing them needs an interactive Entra, Copilot Studio or RDP session, so
each one is redacted by hand before it is committed.

Capture, **redact** (blur or replace hostnames, GUIDs, IPs, thumbprints, accounts), save as PNG
at 1 MB or less, and name by documentation section:

| File name | Shows | Used in |
|---|---|---|
| `readme-agent-answer.png` | Copilot Studio agent answering "Give me the inventory summary" with collected_at, coverage and freshness | [README](../../README.md#7-screenshots), [copilot-studio-integration.md](../copilot-studio-integration.md) |
| `readme-wizard.png` | Setup wizard main page | [README](../../README.md#7-screenshots), [install-wizard.md](../install-wizard.md) |
| `install-wizard-plan.png`, `install-wizard-activate.png`, `install-wizard-accept.png` | Wizard plan, activate and accept pages | [install-wizard.md](../install-wizard.md) |
| `entra-setup-api-expose.png` | API app, Expose an API (scope `Scom.Read`) | [entra-setup.md](../entra-setup.md) |
| `entra-setup-api-approles.png` | API app, App roles (`Scom.Ingest`) | [entra-setup.md](../entra-setup.md) |
| `entra-setup-publisher-cert.png` | Publisher app, Certificates (public cert only) | [entra-setup.md](../entra-setup.md) |
| `entra-setup-publisher-permissions.png` | Publisher app, API permissions with admin consent granted | [entra-setup.md](../entra-setup.md) |
| `entra-setup-reader-permissions.png` | Reader app, delegated `Scom.Read` | [entra-setup.md](../entra-setup.md) |
| `copilot-studio-connector.png` | Custom MCP connector settings (no secret visible) | [copilot-studio-integration.md](../copilot-studio-integration.md) |
| `copilot-studio-connection.png` | Connection created and tool added to the agent | [copilot-studio-integration.md](../copilot-studio-integration.md) |

After adding a file, embed it with a normal relative image link in the section named above.
