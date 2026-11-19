# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's private
vulnerability reporting (Security tab > Report a vulnerability) on this repository. Include
the affected version or commit, reproduction steps and impact. Never include real tenant
IDs, tokens, certificates or customer data.

This is a community project without a service-level commitment; reports are handled on a
best-effort basis.

## Supported versions

Only the latest release and the default branch.

## Verifying releases

Releases are unsigned (no code-signing certificate). Each tagged release carries a SHA256 file,
an SBOM and a build provenance attestation:

```powershell
(Get-FileHash .\<release>.zip -Algorithm SHA256).Hash   # compare with <release>.zip.sha256
gh attestation verify .\<release>.zip --repo <OWNER>/<REPO>
```

Design and threat model: [docs/security.md](docs/security.md). Limits:
[docs/limitations.md](docs/limitations.md).
