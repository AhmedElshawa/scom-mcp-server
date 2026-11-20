# Security review — v0.2.0-lab

Scope: commit `8365f3e` (tag `v0.2.0-lab`). Findings only: no product code was changed. The only
changes in this branch are scanner configuration (`.github/workflows/codeql.yml`,
`.github/dependabot.yml`) and this document. Proposed fixes are not applied.

Evidence labels: **[code]** read directly in the repository at the cited `file:line`;
**[tool]** produced by a scanner or command; **[assumed]** depends on deployment state this review
could not observe (for example IIS settings and directory ACLs on a real host).
Nothing here was penetration-tested against a running instance.

## 1. Automated results

| Control | Configuration | First-run result |
|---|---|---|
| CodeQL (csharp) | `.github/workflows/codeql.yml`: `security-extended` queries, `build-mode: none`, runs on PRs, pushes to `main`, weekly and on demand. Actions pinned by commit SHA, `security-events: write` only. | See section 1.1. |
| Dependabot alerts | Enabled on the repository (was disabled). `.github/dependabot.yml` covers nuget (`/ScomMcp`, `/SetupGui`, `/Validation`) and github-actions, with `open-pull-requests-limit: 0`, so only alerts and security updates are produced and no routine version-bump PRs. | 0 alerts at review time [tool]. |
| Secret scanning + push protection | Already enabled on the repository; no `.github` file is required. | 0 open alerts at review time [tool]. |
| `dotnet list package --vulnerable --include-transitive` | Run on `ScomMcp`, `SetupGui` and `Validation`. | **No vulnerable and no deprecated packages in any project.** No High/Critical entries, so there is no fix version to list [tool]. |

Direct dependencies of `ScomMcp`: ModelContextProtocol.AspNetCore 2.2.0,
Microsoft.AspNetCore.Authentication.JwtBearer 8.0.31, Microsoft.Data.Sqlite 8.0.31.

### 1.1 CodeQL first run

First run (PR #14, `security-extended` queries, `build-mode: none`): the `CodeQL (csharp)` job completed successfully and the code-scanning API reported **0 open alerts** [tool]. Note that `build-mode: none` analyses C# sources without compiling, so this is weaker evidence than a build-traced run.

## 2. Summary of findings

No High findings. Three Medium findings, then Low and Informational items.

| # | Severity | Finding | Location |
|---|---|---|---|
| F1 | Medium | No rate or concurrency limit on `/mcp`; one global lock serialises all read-model access | `Hosting.cs:40-48,116,171`, `ReadModelStore.cs:220-436` |
| F2 | Medium | The app does not enforce HTTPS; no HSTS or response hardening; TLS is entirely an IIS deployment property | `Hosting.cs:100-171`, `ScomSetup.Windows.psm1:442-444`, `web.config` |
| F3 | Medium | Hash-then-load gap for the normalization script, and a latent PATH-search executable fallback | `PowerShellNormalizationProfile.cs:99-118`, `NormalizationChildProcess.cs:99-106` |
| F4 | Low | Mapping-profile attestation is not bound to the worker file and is not verified downstream | `Approve-ScomMappingProfile.ps1:72-87,110-122` |
| F5 | Low | Generator/approve output files are written non-atomically with inherited ACLs; evidence contains host data | `New-ScomMappingProfile.ps1:400-436`, `Approve-ScomMappingProfile.ps1:109,122` |
| F6 | Low | v1 `sts.windows.net` issuer is accepted alongside v2 | `Hosting.cs:85-89` |
| F7 | Low | Ingestion returns the raw fault message to the publisher | `ReadModelHosting.cs:95` |
| F8 | Low | Single global ingestion permit lets one slow publisher monopolise ingestion | `Hosting.cs:43` |
| F9 | Low | Publisher check does not examine `azp`/`idtyp` | `ReadModelHosting.cs:15-25` |
| F10 | Low | SQLite file gets no explicit ACL at creation; the install-time directory ACL is relied upon | `ReadModelStore.cs:90` |
| F11 | Low | Signing-key refresh uses library defaults (not tuned) | `Hosting.cs:55-63` |
| F12 | Info | `/healthz` is anonymous; version is hard-coded `1.0.0` and does not match the release | `Hosting.cs:75,170` |
| F13 | Info | Cursor key is per-process; cursors die on restart and do not work across instances; cursor logic is duplicated | `CursorCodec.cs:9`, `ReadModelStore.cs:769-795` |
| F14 | Info | Lab signing script imports a certificate into Root and TrustedPublisher (CurrentUser, lab only) | `Sign-ScomEvaluationPackage.ps1:88` |
| F15 | Info | Release assets are unsigned (hash, SBOM, provenance only); already documented in `SECURITY.md` | `SECURITY.md` |

## 3. Findings and proposed fixes

### F1 — No request limiting on `/mcp` (Medium) [code]
`/mcp` is protected by `RequireAuthorization("ScomRead")` (`Hosting.cs:171`), so only authenticated,
allow-listed callers reach it. The only rate limiter registered is the `ScomIngestion` concurrency
limiter (`Hosting.cs:40-48`), which is applied to `/ingestion*`, not `/mcp`. `ReadModelStore` runs
synchronous SQLite under a single `lock (gate)` (`ReadModelStore.cs:220,271,322,422,436`), so a caller
issuing many concurrent expensive queries can starve every other caller and ingestion. A holder of a
valid token is already trusted, which is why this is Medium and not High.
**Fix:** add a per-caller (`oid`) token-bucket or concurrency limiter on `/mcp`, set Kestrel/IIS request
timeouts, and replace the global lock with a reader/writer scheme or per-query connections on the
read-only immutable database.

### F2 — HTTPS and response hardening are not enforced by the app (Medium) [code] / [assumed]
`Hosting.cs` registers no HTTPS redirection, HSTS or forwarded-headers handling, and `web.config`
contains no `<security>`/header configuration. Setup creates only an HTTPS binding with SNI
(`ScomSetup.Windows.psm1:442-444`), so a default install is correct. However, if an operator later adds
an HTTP binding, or the app sits behind a TLS-terminating proxy, nothing in the process refuses a bearer
token that arrived in clear text. The in-process hosting model also means Kestrel limits are not under
this project's control.
**Fix:** reject non-HTTPS requests on `/mcp` and `/ingestion*` (check `Request.IsHttps` or a configured
trusted forwarded proto) and fail closed; add `Strict-Transport-Security`, `X-Content-Type-Options:
nosniff` and `Cache-Control: no-store` headers; document the required IIS bindings and add a setup
validation that no HTTP binding exists on the site.

### F3 — Script hash verification vs. load (Medium) [code], residual risk [assumed]
The normalization profile pins the script SHA-256, requires a fully qualified local-drive path with no
reparse points, and re-verifies the hash at each start (`PowerShellNormalizationProfile.cs:99,111-118`).
The configuration is passed by stdin and the argument array is fixed (`-NoLogo -NoProfile
-NonInteractive -File`), so there is no string-built command. The remaining gap is the interval between
hash verification (`:114-117`) and PowerShell opening the file (`:106`): a principal that can write to
the script directory could swap the file in that window and obtain code execution as the worker account,
which holds SCOM read rights. Setup applies an admin-only ACL, so this requires an ACL regression or
non-default install. Separately, `NormalizationChildProcess.cs:99-106` resolves non-fully-qualified
executables with `SearchPathW(null, …)`. It is not reachable today because the profile always passes the
fully qualified system `powershell.exe`, but a future caller would inherit a PATH-search behaviour.
**Fix:** copy the verified script bytes to a worker-private file opened with `FileShare.Read` (or run the
verified bytes via `-Command` from stdin) and execute that copy; verify at startup that the script
directory ACL grants write only to SYSTEM/Administrators; remove the `SearchPathW` fallback and require a
fully qualified path.

### F4 — Attestation not bound or verified (Low) [code]
`Approve-ScomMappingProfile.ps1` refuses approval unless `windowsMapping`, `allowedPropertiesByClass`
and `additionalRelationshipNames` equal the evidence (`:72-87`) and the six flags are currently false,
then writes an attestation with operator, UTC time and the evidence SHA-256 (`:110-122`). Gaps: the
evidence file itself is unsigned, so someone who can write both files can fabricate matching evidence;
the other worker fields (scope, paths, identity) are not compared; and nothing downstream
(`--validate-setup`, Configure) checks that an attestation file exists or that its hash matches. The
flags are an operator attestation by design (the script says so), which keeps this Low.
**Fix:** include a SHA-256 of the final worker file in the attestation and have Configure verify it;
compare the full non-identity worker document to the evidence; optionally sign evidence with the
operator's certificate.

### F5 — Generator/approve file handling (Low) [code]
`Write-ScomUtf8File` uses `[IO.File]::WriteAllText` straight to the final path
(`New-ScomMappingProfile.ps1:400-403`), and Approve rewrites `worker.json` in place (`:109`). A crash
mid-write leaves a truncated file; no ACL is set, so the files inherit the parent directory. The
overwrite guard is `Test-Path` followed by write (`:419`, `:586`), a small TOCTOU that only matters in
shared directories. The evidence file records operator name, computer name, PowerShell version and
sampled SCOM computer names (`:422-429`), which are operationally sensitive. No temporary files are used
and nothing is built into a command line, so injection is not applicable here: values reach output only
through `ConvertTo-Json` and `-f` formatting.
**Fix:** write to `<path>.tmp` created with `FileMode.CreateNew`, then move into place; refuse output
directories whose ACL allows non-admin write, or tighten the file ACL after creation; mention the
sensitivity of the evidence file in the deployment guide.

### F6 — v1 issuer accepted (Low) [code]
`ValidIssuers` accepts both the v2 and the v1 (`https://sts.windows.net/{tenant}/`) issuer
(`Hosting.cs:85-89`). Both are tenant-pinned, audience is validated with trailing slash significant
(`:90-92`), algorithms are limited to RS256 (`:97`), and lifetime, signed-token and key validation are
required (`:93-96`), so this is acceptable. It widens the accepted token shapes.
**Fix:** accept only the issuer that the registered API actually issues (check
`accessTokenAcceptedVersion` in the app manifest) and drop the other.

### F7 — Raw fault message returned to publisher (Low) [code]
`ReadModelHosting.cs:95` returns `fault.Message` while the log records only the mapped reason (`:92-94`).
Only the single allow-listed publisher sees it, but messages could carry internal detail.
**Fix:** return the mapped `RejectionReason` instead of the raw message.

### F8 — Global ingestion permit (Low) [code]
The ingestion limiter allows one permit and no queue (`Hosting.cs:43`); a slow or stalled publisher
holds it until the request-body timeout, blocking other publishes. It is a DoS on an authenticated
principal only.
**Fix:** keep concurrency at one but bound the permit with a short total-request timeout.

### F9 — Publisher principal checks (Low) [code]
`IsPublisher` requires an authenticated principal, a matching single `tid`, no `scp`, exactly one `oid`
on the allow-list and the ingestion role (`ReadModelHosting.cs:15-25`). It does not check `idtyp`/`azp`,
so a token for the same service principal obtained through another client would be accepted. The
allow-list and role make this low.
**Fix:** also require `idtyp == app` (where emitted) and an expected `azp`/`appid`.

### F10 — Database file ACL (Low) [code] / [assumed]
New databases are created with `FileMode.CreateNew`, `FileShare.None` (`ReadModelStore.cs:90`); orphan
`-journal`/`-wal`/`-shm` files and WAL databases are refused (`:88`, `:147-153`). No ACL is applied to
the file; protection comes from the receiver state directory ACL set by setup
(`ScomSetup.Windows.psm1:366-374`, `Set-ScomSetupDirectoryAcl` at `:243-267`).
**Fix:** verify the state directory ACL at startup (refuse to run if non-admin principals can write),
as the journal directory check already does (`ScomSetup.Windows.psm1:269-287`).

### F11 — Signing-key refresh (Low) [code]
Authority metadata uses library defaults (automatic refresh 12 h, minimum refresh interval 5 min) and
`RequireHttpsMetadata` is on (`Hosting.cs:55-63`). A key rolled by Entra is picked up on the next
refresh or on signature failure (the library's refresh-on-unknown-kid behaviour); there is no explicit
configuration.
**Fix:** set `AutomaticRefreshInterval` explicitly (for example 24 h) and add a test for key rollover.

### F12 — `/healthz` (Info) [code]
Anonymous; returns only `{service, version}` (`Hosting.cs:170`). No inventory, paths or configuration.
Both `/healthz` and MCP `ServerInfo` (`:75`) report `1.0.0`, which does not match the release, so the
endpoint cannot be used to identify the deployed build and is misleading for operations.
**Fix:** report the assembly informational version, or keep it anonymous and version-free.

### F13 — Cursors (Info) [code]
`CursorCodec` signs a payload with HMAC-SHA256 using a random per-process 32-byte key
(`CursorCodec.cs:9,17-18`), compares in constant time (`:31`), caps length at 4096 (`:26`), and binds
the cursor to caller (hashed), scope, dataset id, fingerprint, filter hash and expiry (`:11,35`).
**Not forgeable.** It is replayable only by the same caller within its lifetime, which is the design.
The payload is signed, not encrypted, so a caller can read the hashes of its own binding. Cursors do not
survive a restart and do not work across instances. `ReadModelStore.cs:769-795` carries a second copy
of the logic with its own key. No security impact; maintainability only.

### F14 — Lab signing script (Info) [code]
`Setup/Sign-ScomEvaluationPackage.ps1:88` imports the lab certificate into the Root and TrustedPublisher
stores; `docs/local-evaluation-signing.md` documents this as CurrentUser and lab only.
**Fix:** keep the script out of release packages and keep it explicit that lab trust must not be
used for production.

### F15 — Unsigned release assets (Info)
Releases ship SHA-256, SBOM and a provenance attestation but no code signature; stated in `SECURITY.md`.

## 4. Review areas with no finding

| Area | Result |
|---|---|
| JWT parameters | Tenant-pinned authority, HTTPS metadata, issuer/audience/lifetime/expiry/signature required, RS256 only, 30 s skew, `MapInboundClaims=false`, `SaveToken=false`, `IncludeErrorDetails=false` (`Hosting.cs:55-63,82-99`). Only F6 and F11 noted. |
| Caller authorization | `Authorization.cs:57-71`: tenant, `scp` contains required scope, then `oid` grant; ambiguous UPN claims fail closed. Every tool calls `Resolve` and `AuthorizeScope` (`ScomReadOnlyTools.cs:74-76`, `TargetedLiveHosting.cs:20-22`, `ReadModelTools.cs:39-41`). Per-record scope inside `store.Page` was not traced. |
| Middleware order | ExceptionHandler, Routing, Authentication, Authorization, RateLimiter, then the `/mcp` body middleware (`Hosting.cs:103-116`). Unauthenticated requests are rejected before the body is read. |
| JSON-RPC limits | 32768-byte cap by `Content-Length` and by streaming (`Hosting.cs:121,131`), `MaxDepth=32` (`:141`), 413 on overflow. The pre-check of `scopeId` covers single-object `tools/call` only, but tools re-enforce scope, so it is defence in depth. No explicit limit on batch size. |
| SQL | All values are parameterised; the one concatenated filter is a compile-time constant (`ReadModelStore.cs:462-467`); interpolated PRAGMAs use constants (`:112,182`). No injection. |
| PowerShell injection | No `Invoke-Expression`, `-EncodedCommand`, `GetTempFileName` or script-block construction in non-validation scripts. Configured values are passed as data (JSON on stdin, fixed argument arrays). Path inputs go through `Assert-ScomSetupPath` (`ScomSetup.psm1:13-30`), which rejects reparse points, device names, traversal and non-fixed drives. |
| Setup temp files and journal | `Save-ScomSetupJson` writes `<path>.new` inside the private directory and refuses to continue if one exists (`ScomSetup.psm1:128-137`). The private directory is created then ACL'd with a protected, verified ACL (`ScomSetup.Windows.psm1:366-374`, `:243-287`); the journal requires an admin-owned directory. The directory is empty between creation and ACL, so the creation window exposes nothing. |
| Logging | Operational logging is an allow-list projection that ignores formatter and exception (`OperationalLogging.cs:96-119`); runtime logs contain event codes, error types and trace IDs only (`CollectionWorkerHost.cs`, `CollectionScheduler.cs`, `Hosting.cs:107`). No tokens, claims, oids or UPNs found in any log call. Log files reject reparse points (`:279-287`). |
| Publisher token | Certificate must be unique in LocalMachine/My, RSA ≥ 2048, validity checked; assertion is PS256 with `jti`; token response bounded to 64 KiB with duplicate-property rejection; tokens are not persisted (`CertificatePublisherToken.cs:44-125`). |

## 5. Threat model

| Asset | Threat | Existing control | Gap |
|---|---|---|---|
| Inventory data served over `/mcp` | Anonymous or wrong-tenant access | Entra JWT validation, tenant/audience/scope checks, `oid` allow-list | Accepts the v1 issuer (F6) |
| Inventory data in transit | Token or data sniffed on a clear-text hop | HTTPS-only binding created by setup | App does not enforce HTTPS or HSTS (F2) |
| Availability of `/mcp` | Authenticated caller floods queries | Body cap, auth before body read | No `/mcp` rate limit; global store lock (F1) |
| Read model integrity | Forged or replayed ingestion | Publisher `oid` allow-list, role, no `scp`, single permit, strict body validation | No `azp`/`idtyp` check (F9); single permit stall (F8) |
| Pagination cursors | Forgery, cross-user replay | HMAC with random key, caller/scope/filter binding, expiry | None; cursors are per-process (F13) |
| SQLite read-model file | Tampering, planted journals | Exclusive create, orphan journal refusal, WAL refusal, schema/app-id checks | No ACL set by the app (F10) |
| Normalization script | Substitution to run code as worker | Pinned SHA-256, fully qualified path, no reparse points, job object, deadline | Hash-then-load window; ACL not re-verified (F3) |
| Setup journal and config | Local tampering or disclosure | Protected admin-only ACL, locks, interrupted-write refusal | None found |
| Mapping profile attestation | Falsified or stale approval | Evidence/worker consistency check, six flags default false | Attestation not bound or verified downstream (F4); non-atomic, inherited-ACL outputs (F5) |
| Publisher credential | Theft of certificate private key | Cert in LocalMachine/My, no key or token persisted, bounded token exchange | Machine-level protection is an OS property |
| Logs | Leakage of tokens or PII | Allow-list log projection, safe console logger | None found |
| Supply chain | Vulnerable dependency or tampered build | Clean vulnerability scan, Dependabot alerts, SHA-pinned actions, SHA-256, SBOM and provenance | Releases unsigned (F15) |

## 6. Not verified

- Behaviour against a running IIS instance (bindings, request filtering, ACLs on a real install).
- Per-record scope filtering inside `ReadModelStore.Page` and `QueryEngine`.
- The WPF setup GUI, `ScomPrerequisiteInstall.psm1` download and install logic (only the presence of
  an Authenticode signer check at `:137` was observed), and Entra Graph mutation code beyond its journal handling.
- Entra-side configuration (app manifest token version, consent).
