# Security

## Threat model (summary)

| Threat | Control |
|---|---|
| Anonymous access to inventory | `/mcp` requires an Entra delegated token (tenant, audience, scope `Scom.Read`) **and** the caller's `oid` in `Auth.Callers`; `/healthz` is the only anonymous endpoint and returns no inventory. |
| Forged or rogue publisher | `/ingestion` accepts only app-only tokens with the `Scom.Ingest` role from an approved publisher object ID; reader tokens are refused (403). |
| Credential theft | No secrets in configuration. The publisher uses a certificate (private key readable only by the worker); SCOM access uses the worker's Windows identity; the reader secret lives only in the connector. |
| Privilege escalation via SCOM | Worker is Read-Only Operator; the receiver has no SCOM credentials and never connects to SCOM. |
| Data exposure in transit | HTTPS with a trusted certificate; inbound 443 only, from Power Platform service tags. |
| Resource exhaustion | Bounded request, batch, dataset, record and database sizes; paged queries; cursors bound to the caller and query. |
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

Detail: [technical-reference.md](technical-reference.md#security-and-known-limitations).
