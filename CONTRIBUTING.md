# Contributing

Thanks for helping. This project is security-sensitive: please keep changes small and evidence-based.

- Open an issue first for behaviour changes. Use the issue templates.
- Never commit secrets, tokens, certificates, tenant or lab identifiers, real hostnames or IP addresses. Use `<PLACEHOLDER>` values in docs and examples.
- Do not claim lab validation you did not perform; label untested behaviour as untested.
- Keep read-only guarantees: no change may add a write path to SCOM or widen the allow-lists by default.
- Follow existing conventions; keep the package directory limited to the files listed in `package-manifest.json`.

Validation before a pull request (Windows, .NET 8 SDK pinned in CI):

```powershell
dotnet restore .\Validation\Validation.csproj --locked-mode
dotnet restore .\SetupGui\ScomSetup.csproj --runtime win-x64 --locked-mode
```

Then run the package and validation steps in `.github/workflows/offline-validation.yml`. Documentation
changes must keep relative links valid and contain no environment-specific identifiers.

Security issues: see [SECURITY.md](SECURITY.md). By contributing you agree that your contribution
is licensed under the repository [LICENSE](LICENSE).
