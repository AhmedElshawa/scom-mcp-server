# Security

## Threat model (summary)

| Threat | Control |
|---|---|
| Anonymous access to inventory | `/mcp` requires an Entra delegated token (tenant, audience, scope `Scom.Read`) **and** the caller's `oid` in `Auth.Callers`; `/healthz` and `/healthz/freshness` are the only anonymous endpoints; neither returns inventory. `/healthz/freshness` discloses only a coarse status (`fresh`/`stale`/`expired`/`unavailable`) and the dataset age in seconds, and exists only when the read model is enabled. |
| Forged or rogue publisher | `/ingestion` accepts only app-only tokens with the `Scom.Ingest` role from an approved publisher object ID; reader tokens are refused (403). |
| Credential theft | No secrets in configuration. The publisher uses a certificate (private key readable only by the worker); SCOM access uses the worker's Windows identity; the reader secret lives only in the connector. |
| Privilege escalation via SCOM | Worker is Read-Only Operator; the receiver has no SCOM credentials and never connects to SCOM. The collector script is hash-pinned, and the worker refuses to start unless the collector directory and script are writable only by SYSTEM and Administrators. The child executable must be an absolute path (no PATH search). |
| Data exposure in transit | HTTPS with a trusted certificate; inbound 443 only, from Power Platform service tags. Outside Development the app also sends HSTS (`max-age` one year) and redirects HTTP to HTTPS when the HTTPS port is known (`HTTPS_PORT` or an https binding); the redirect is not a substitute for the HTTPS-only IIS binding that setup creates, and by design the Activate step guarantees that binding, so the app-level redirect is defence in depth and needs no further configuration; setup refuses an `httpsBaseUrl` that does not start with `https://`. |
| Resource exhaustion | Bounded request, batch, dataset, record and database sizes; paged queries; cursors bound to the caller and query; `/mcp` limited per caller (`oid`) to 30 requests per minute and 4 concurrent requests, queue 0 (HTTP 429). By design the limiter runs after authentication so that it can partition by the token's `oid`; unauthenticated requests are rejected with 401 before they reach it. |
| Log leakage | Bounded operational logs without tokens, secrets or payloads. |

## What is collected

Only allow-listed properties of the approved scope: computer name, logical processors,
OS version and optional caption, memory, logical-disk name/file system/size, relationships,
and health state as of collection. Alert descriptions and metrics are off by default. The data
stays on your infrastructure; the connector transports query results to Copilot Studio.

## What is not collected

SCOM or Entra secrets, certificates' private keys, tokens, user content, file contents,
credentials stored in SCOM, anything outside the allow-listed classes and properties.

## Residual risks

- Copilot Studio and the model process answers returned by the tools; apply your data-handling policy.
- Machine administrators can read local logs and configuration.
- Service tags are broad; tighten using observed source addresses.
- Operator-attested verification flags can be set wrongly.
- Unsigned release: verify hashes and the release attestation ([../SECURITY.md](../SECURITY.md)).
- Accepted residual window: the collector script's SHA-256 is verified and PowerShell then opens the file in a separate step, so a principal able to write to the collector directory could swap the file between the two. The control is the directory ACL: the worker refuses to start unless only SYSTEM and Administrators can write to the collector directory and script (and the directory owner is one of them), which setup applies. A machine administrator, or a regression in that ACL after the start-time check, is therefore outside this control.

Detail: [technical-reference.md](technical-reference.md#security-and-known-limitations).
