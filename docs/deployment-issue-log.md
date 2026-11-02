# Deployment issue and fix log

Record evidence before assigning a root cause. Distinguish an approved proposal,
an applied operational correction, a verified source fix and a deployed package.
Never include customer hostnames, accounts, identifiers, private paths, tokens,
configuration payloads or certificate details.

## Confirmed findings and prevention

| Problem / evidence | Cause | Correction and prevention |
|---|---|---|
| MCP returned `SNAPSHOT_OR_STATUS_INVALID_OR_UNREADABLE` although indexed files were staged | Active settings still selected the default legacy snapshot reader; no indexed dataset had committed | Activate the reviewed receiver configuration in the actual IIS configuration source. Require successful startup, effective-mode checks, a committed dataset and authenticated inventory acceptance. Staging alone is not activation. |
| Worker staged on a different host from the agreed execution host | Staging did not follow the selected co-located MCP/worker layout | Verify hostname, complete installed package, local module paths, private config and profile hash on the chosen MCP host before activation. The corrected staging was confirmed; other-host files were left untouched. |
| `Register-ScheduledTask` returned access denied before worker launch | Scheduled-task registration failed; the worker had not executed | Use an approved interactive worker-account shell for first acceptance. Do not grant administrator rights or change task permissions to bypass the launcher failure. Provision recurrence separately. |
| Worker reported `JsonReaderException`; text-decoded strict JSON validation passed | Raw configuration bytes had a UTF-8 BOM; raw-byte parsing failed at line 0, byte 0 | A backed-up BOM-only removal preserved all other bytes and ACLs. Current source accepts an optional UTF-8 BOM and reports safe configuration JSON line/byte diagnostics. UTF-16 remains unsupported. Save private files explicitly as UTF-8. |
| Worker reported `PUBLISHER_HTTP_500` at session begin; IIS reported `500.30` | Receiver configuration validation failed before publisher authorization. Clearing a reported invalid metrics-map key did not resolve startup; a comprehensive check then identified the bound GroupId as an empty string. | Keep disabled metric maps as `{}` and inspect all effective providers. Require the exact failing predicates, not a guessed single cause from a generic exception. Current source identifies rejected setting names without values. |
| JSON `GroupId: null` failed management-group validation | Reproduced with the .NET 8 JSON configuration provider: JSON null binds to an empty string, while the original validator required a null reference | Current source normalizes only the empty string to null in management-group mode. Group scope still requires a valid GUID; whitespace, the string `"null"` and a real group ID remain rejected in management-group mode. The live workaround omitted only the key; validation then passed with Auth preserved. |
| Health probe recorded status 0 after a targeted recycle | The failing probe used localhost HTTP and encountered HTTP 503; it did not verify the configured HTTPS endpoint | The intended hostname-based HTTPS probe passed with normal certificate validation (200). Use the configured hostname, port and application prefix. Status 0 is not an HTTP response. Do not infer startup failure from an unrelated binding or recycle again without evidence. |
| Publisher could not use an HTTPS application base with a path prefix | Publisher originally required the URL root | Preserve the configured application prefix and normalize the trailing slash for relative ingestion routes. HTTPS/URI restrictions and no-redirect behavior remain. Root/single/nested-path regression coverage exists. |
| Binary package README linked to omitted deployment guides and image | Packaging copied README/license but not linked documentation assets | Package the explicit documentation/image allowlist and validate required content, local Markdown file links, example JSON and common private/runtime-state exclusions before distribution. |

## Verified outcome and remaining boundaries

- After the operational corrections, the actual one-shot worker emitted
  `SCOM_WORKER_PUBLISHED`, which follows successful publication and source
  acknowledgement. The user subsequently confirmed the agent works.
- This confirms the reported lab inventory recovery, not production
  certification, performance-counter acceptance, OS-lifecycle answers or a
  supervised recurring schedule. A detailed authenticated acceptance record and
  recurring refresh verification remain separate operational work.
- The source changes for BOM compatibility, configuration binding and safe
  diagnostics have passed the offline regression harness. They must be delivered
  through a reviewed package update; the operational workaround does not mean
  those source changes are installed in the live application.

## Repository improvements

- [Ordered indexed deployment runbook](indexed-deployment-runbook.md) covering
  host responsibilities, identities, paired settings, permissions, activation,
  first publication, reader acceptance, recurrence and failure-specific guidance.
- Safe worker configuration diagnostics with a fixed error code and zero-based
  line/byte position; no exception messages or private values.
- Safe request-failure diagnostics with exception type and SQLite numeric codes,
  retaining the sanitized HTTP response. These do not replace IIS startup logs.
- Regression tests for JSON-provider null binding, strict group-scope rejection,
  multiple rejected settings, BOM compatibility and sanitized error output.
- [Customer evaluation gates](customer-evaluation.md) distinguish offline package
  verification from a clean-host installation, live customer acceptance and a
  reviewed versioned release.

## Independent-review corrections

### Optional targeted live verification

Retained data cannot establish current health. The opt-in
[targeted live transport](targeted-live-verification.md) separates such checks from
retained answers, with one-object requests, fresh scope proof, budgets and safe
unavailable states. No production SCOM provider is certified/shipped; documented
cmdlet parameters do not establish bounded backend work or membership completeness.
Do not enable it, fabricate fresh timestamps or bypass source gates to hide that gap.
Offline evidence: 38 broker assertions, real loopback fake-source tool/worker
flows (including lost accepted-result replay, fresh-scope refusal, timeout/join
and retained-source invalidation), complete regression suite and 27-file package
checks passed. This is not a production source/provider acceptance result.

### OS lifecycle capability gap

Earlier builds intentionally could not answer OS-support questions. The additive
`list_os_support_assessments` tool now uses a [versioned curated Microsoft
Windows Server catalog](os-lifecycle-assessment.md), never per-question scraping
or inferred build dates. Exact product/edition matching, support boundaries,
unknown coverage, bounded evidence age, ESU non-entitlement, complete counts and
bound paging are covered offline. This does not certify customer inventory,
patch state or Copilot Studio acceptance; the original six contracts remain.

| Finding | Implemented correction | Verification and remaining boundary |
|---|---|---|
| Worker crash could orphan its collector | Atomic Windows Job Object association during process creation, kill-on-close and descendant cleanup before releasing collection guard | Synthetic worker-kill/replacement, descendant, cancellation/deadline and UTF-8 tests pass. Requires Windows 10/Server 2016+ and supported job restrictions; intended service/task host acceptance remains. |
| Receiver could mutate a worker/foreign SQLite database | Read-only immutable ownership/schema/scope check before writable access, distinct application identity and transactional supported-schema migration | 159 ownership/recovery assertions include unchanged rejected files/sidecars, legacy upgrades and disk-full rollback. WAL and unknown schemas are refused unchanged for offline review. |
| Utilization repeatedly scanned all samples for every computer | Immutable raw series lookup cached weakly by snapshot identity; original sample order and arithmetic retained | 272 reference/boundary/memory/capacity/replacement assertions; 10,000-computer/50,000-sample synthetic CPU query completed below the five-second guard. Not a source-load or production SLA claim. |
| Blank optional ObjectId broke legacy UPN startup | Normalize absent/empty/whitespace IDs consistently, then normalize nonblank GUIDs | Real configuration-provider tests verify legacy grants, denied users and indexed-mode UPN rejection. Mutable UPN risk remains legacy-only; prefer immutable IDs. |

These are source fixes, not live upgrades or proof of exhaustive independent
review. The combined offline suite passed; clean-host/customer/recurring-operation
acceptance remains separate.

## Recording the next finding

Record symptom, sanitized evidence, confirmed cause (or explicitly unknown),
approved correction, verification result and release/deployment state. Update
existing findings when evidence changes; never mark an unverified proposal as
resolved or mistake offline coverage for live source certification.
