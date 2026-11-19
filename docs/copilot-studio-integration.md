# Copilot Studio integration

Prerequisites: receiver reachable over HTTPS ([network-and-ports.md](network-and-ports.md)),
Entra registrations done ([entra-setup.md](entra-setup.md)), your user's object ID in
`Auth.Callers`, and the first dataset published.

## 1. Create the custom MCP connector

In Copilot Studio, open the agent and enable **generative orchestration**.
Tools > **Add a tool** > **New tool** > **Model Context Protocol**.

| Field | Value |
|---|---|
| Name | `<CONNECTOR_NAME>` (no spaces or parentheses) |
| Description | Read-only SCOM inventory (retained snapshot) |
| Server URL | `https://<FQDN>/mcp` |
| Transport | **Streamable HTTP** (not SSE) |
| Authentication | **OAuth 2.0**, **Manual** (no dynamic client registration) |
| Client ID | `<READER_APP_ID>` (the Copilot Studio client registration, not the API or publisher) |
| Client secret | The reader app's secret; enter it only here |
| Authorization URL | `https://login.microsoftonline.com/<TENANT_ID>/oauth2/v2.0/authorize` |
| Token URL and Refresh URL | `https://login.microsoftonline.com/<TENANT_ID>/oauth2/v2.0/token` |
| Scopes | `api://<API_APP_ID>/Scom.Read offline_access` |

Click **Create**, copy the generated **callback (redirect) URI**, and register it on
the reader app as a **Web** redirect URI exactly as shown. Then **Next** > **Create new
connection**, sign in as an allow-listed user, and **Add to agent**. Keep individual-user
authentication.

## 2. Agent instructions

Paste [`examples/agent-instructions.md`](../examples/agent-instructions.md) into the agent's
Instructions. The text names every tool the agent uses, including `list_os_support_assessments`,
so lifecycle answers come from the server's catalog rather than the model's own knowledge.

## 3. Test prompts

| Prompt | Expected shape of answer |
|---|---|
| "Give me the inventory summary." | Total computers, OS and health breakdown, followed by `collected_at`/`asOfUtc`, coverage (expected vs unique devices, complete or not), freshness state and any stale/partial flags. No claim of live health. |
| "List devices." | One row per computer with name, OS, health as of collection and machine type, with the same disclosure. Paged results include a continuation if more exist. |
| "Group computers by OS." | Counts per OS caption from the retained snapshot, with disclosure. If the snapshot has expired the agent reports `SOURCE_UNAVAILABLE` / `READ_MODEL_EXPIRED` as-is and returns no data. |

## Agent acceptance test

Run these nine prompts in a new test session after publishing the instructions. Replace
`<server>` with a computer in your scope.

| Prompt | Pass criteria |
|---|---|
| How many servers do we have? | Count from `list_devices`; Data status shows "Inventory complete; performance metrics not collected". |
| Which servers are nearest end of support? | `list_os_support_assessments` called; uncatalogued OS shows "Not in the lifecycle catalog."; reference date and catalog version stated. |
| Show me the disks on `<server>` | Device ID, file system, size with unit. |
| Is `<server>` healthy right now? | Health "as of collected_at"; states live status is unavailable. |
| Group computers by OS | `osCaption` used; no build-number translation. |
| What is the total disk capacity across all servers? | Per-volume table; MiB with GiB conversion stated. |
| Are we licensed for Windows Server 2022? | Declines; names authoritative licensing sources; no guess. |
| Which servers have network adapters? | Declines; lists what is collected. |
| When was the data last collected? | `collected_at`, freshness, coverage. |

A transient "source unavailable" answer while a collection commit is in progress is correct
behaviour, not a failure; ask again after the commit completes.

## 4. Verify

`/healthz` proves reachability only. Check that an unauthenticated `/mcp` returns 401, a
user who is not an allowed caller gets 403, and an MCP tool error can arrive inside an HTTP 200
response. If the connector times out, see [network-and-ports.md](network-and-ports.md);
other symptoms: [troubleshooting.md](troubleshooting.md).

Screenshots of the connector and an agent answer (redacted): [screenshots/README.md](screenshots/README.md).
