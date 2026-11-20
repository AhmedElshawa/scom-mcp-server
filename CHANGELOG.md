# Changelog

## Unreleased

### Added
- `setup\New-ScomMappingProfile.ps1` and `setup\Approve-ScomMappingProfile.ps1`: generate a complete `worker.json` (`windowsMapping`, allow-list, relationships, scope) from read-only SCOM observation with proven units, then approve the six attestation flags with one `APPROVE` and an attestation record. `Validation\Validate-ScomMappingProfile.ps1` covers them offline.
- Partner distribution package: landing README and the `docs/` set (architecture, CLI runbook, wizard walkthrough, Entra setup, network and ports, service accounts, Copilot Studio integration, MCP server spec, configuration reference, operations, sizing and scope, troubleshooting, security, limitations).
- `examples/` with receiver, worker and identity examples (placeholders only), field notes and the Copilot Studio agent instructions.
- `package.ps1 -Release`: release ZIP, SHA256 sidecar and a CycloneDX SBOM list.
- `release.yml`: builds on `v*` tags, attaches the ZIP, SHA256 and SBOM, and attests build provenance.
- `SECURITY.md`, `CONTRIBUTING.md`, issue templates.

### Changed
- The previous detailed README moved to `docs/technical-reference.md`; relative links updated.
- Example worker schedule default is hourly (`01:00:00`).

### Status
Lab-validated on a 3-server Windows Server 2022 lab, 18–19 Nov 2026. Unsigned community release.
