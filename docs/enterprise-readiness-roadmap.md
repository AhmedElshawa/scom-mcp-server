# Enterprise readiness roadmap

## Goal and current position

Provide a reusable, read-only SCOM intelligence layer for Copilot Studio.
SCOM remains authoritative: scheduled collection publishes committed datasets,
and MCP reads retained data rather than querying SCOM for every question.
Preserve the working legacy snapshot path and six Windows tool contracts.
Approved-group scope is the customer default; whole-management-group coverage
requires explicit approval and independent visibility evidence.

This roadmap is a planning deliverable, not approval to implement every phase.
PR #1 remains unmerged. Do not change customer access, deploy, enable metrics,
schedule recurrence, merge or create a release without the relevant approval.

| State | Evidence and limits |
|---|---|
| Implemented and checked offline | Opt-in indexed worker/ingestion/read model, reconciliation/checkpoints/scheduler, generic tools, six-tool bridge, deployment hardening and package checks. Offline checks are not installed-SDK certification. |
| Reported working in the lab | First worker publication and agent inventory recovery after approved operational corrections. This does not certify the new packaged build or every tool/caller. |
| Unresolved code-review findings | Four findings below. They have not been fixed or independently accepted. |
| Not yet verified | Clean-host installation, customer-specific scope/platform acceptance, supervised recurring operation and recovery, metric source load and full Studio identity acceptance. |
| Not implemented | Guided configuration generator/checker and authoritative OS lifecycle assessment. |

The independent review identified concrete findings, but did not provide the
requested file-by-file coverage map. Do not describe it as an exhaustive audit,
security certification or proof that no other defects exist.

## 1. Close the current PR's correctness gaps

| Finding | Proposed correction | Regression/acceptance requirement |
|---|---|---|
| Worker death can leave a PowerShell collector running while a replacement starts | Host-enforced child lifetime, with no launch/assignment race; investigate Windows Job Object compatibility | Kill a synthetic worker during collection; prove its collector is gone before a replacement scan. Also test normal completion, deadlines and cancellation under intended execution mechanisms. |
| Receiver can adopt a worker checkpoint or foreign SQLite file | Establish receiver ownership before mutating a file; explicitly recognize supported existing schemas | Reciprocal ownership tests, unchanged rejected files, existing read-model upgrade and scope-policy preservation. Do not reject valid installed databases without an upgrade path. |
| Many-computer utilization repeatedly scans all samples | Immutable per-dataset entity/metric lookup over unchanged raw observations | Exact arithmetic/window/memory-fallback/capacity parity, deterministic cursors and measured many-computer workloads. No silent preaggregation. |
| Empty optional caller ObjectId breaks legacy UPN startup validation | Normalize only nonblank IDs, matching existing grant semantics | Real provider binding with omitted/null/empty/whitespace IDs; preserve indexed mode's immutable-ID-only requirement and denied-caller behavior. |

Use separate reviewable commits and exact-trigger regression tests, then the
complete offline suite and package checks. Keep the live installation unchanged.
Reconcile remaining PR requirements with implementation and tests; distinguish
confirmed defects from proposed enhancements and unknown source contracts.

## 2. Make configuration predictable

Implement a source-independent configuration generator and read-only checker
before an installer. Generate paired synthetic worker/receiver settings through
the actual binding path. Check host placement, installed paths/hash, scope,
identity shape, maps, encoding, budgets, deadlines and effective overrides.
Return all safe setting-level failures together, without values or credentials.

Test UTF-8 with/without BOM, unsupported encoding, JSON null binding, empty maps,
malformed maps and HTTPS application prefixes. Never set evidence gates true
automatically. Installation preview and approved application of files/ACLs are
separate later capabilities, not currently shipped tools.

## 3. Prove access boundaries and minimize data

Review delegated readers independently from app-only publishers: tenant,
issuer/audience, immutable identities, role/scope claims, revocation, replay and
cross-scope relationships. Treat source text as untrusted data, not instructions.
Review property/description allowlists and local database protection.

Require negative tests that disclose no unauthorized records and a separately
scoped security review with no unresolved release-blocking findings. An identity
or publisher's self-declared scope does not independently prove source visibility.

## 4. Verify source behavior and reconciliation

Cover nested groups, heterogeneous classes, hosting direction, monitoring roles,
health, alert lifecycle, partial absence, membership changes and late metrics.
Expected membership must remain distinct from discovered objects. Missing data
must not imply deletion, alert resolution or zero utilization.

Use synthetic cases first. Verify exact SDK/query/relationship/property/unit
contracts during separately authorized acceptance; mark unknown APIs unknown.
Post-materialization bounds are not proof of bounded SCOM backend work.

## 5. Preserve every existing tool

Compare all six Windows tools on equivalent legacy/indexed datasets: counts,
filters, details, native-memory preference, arithmetic observations, previous
windows, real zeros, missing values, freshness and caller/dataset/query-bound
pagination. Document any deliberate change before implementation.
No stale or incomplete result may silently become successful complete data.

## 6. Measure load and storage

Measure bootstrap and steady-state separately. Verify single ownership and
no overlap across crashes, concurrency, query budgets, explicit overflow, staging
expiry, retention, disk growth and source pressure. Review domain cadences/backoff
without implying they are already independently scheduled.

Accept selected 14-day metric history only after approved source-load tests.
Publish measured workload envelopes and test hardware, not a universal device
count or synthetic benchmark presented as production capacity.

## 7. Rehearse recovery and upgrades

Test source outage, token expiry, interrupted collection, ambiguous commit,
checkpoint restart, disk exhaustion, backup/restore and schema/package rollback.
Serve no staged or mixed dataset. Restoring data must not resurrect obsolete
authorization. Define and measure recovery objectives; do not invent them.
Never delete pinned state as a generic repair.

## 8. Make validation repeatable

Add reviewed PR CI for build, offline PowerShell/C# harnesses, synthetic load and
package smoke/content checks. Define dependency/secret scanning and clean-install
jobs with suitable runners and permissions. Do not claim CI is enforced merely
because a workflow file exists: verify required checks/branch policy separately.
Keep live customer tests out of public CI.

The current package includes linked docs/images and has passed offline content
checks, including missing-guide/private-file rejection. Still require a reviewed
candidate commit, dependency inventory, checksums and a clean-host rehearsal.

## 9. Run authorized platform and Studio acceptance

Verify SCOM 2022 and 2025 independently before claiming both. Record exact
build/UR, runtime/SDK/module and relevant management packs privately. Compare
inventory, graph, health, alerts and selected metrics against authoritative scoped
evidence. Test allowed and denied Studio users, refreshes and controlled failures.
Untested combinations remain unverified.

## 10. Release and support

Publish supported profiles, known limitations, measured sizing, ownership,
rotation, setup/upgrade/recovery procedures and release notes. Review the final
diff and package. Merge/tag/release require explicit approval; until all applicable
gates pass, label any distribution an evaluation candidate.

Do not include agent-driven administrative writes, full SCOM replication,
unrestricted live queries or untested multi-node/external-database designs.
OS lifecycle answers require verified applicability/version/edition mapping,
provenance, update cadence and explicit unknown results; they are not implemented.

## Human-readable operator journey

Use the [detailed deployment runbook](indexed-deployment-runbook.md) for existing
commands and package layout. Generated setup commands described above do not
exist yet. Every action below requires its applicable approval.

| Step | Where / identity | Exact action | Success evidence | Stop/failure handling |
|---|---|---|---|---|
| 1. Choose coverage | Planning; source/data owner | Record group or explicitly approved whole-management-group identity and exposed fields privately | Approved scope and field policy | Stop if authorization or expected membership evidence is unclear |
| 2. Check prerequisites | Chosen MCP/worker host; operator | Verify hostname, serviced runtimes, Hosting Bundle, Operations console/module and network route using runbook checks | Supported versions and paths recorded | Do not install collector runtimes on the SCOM server to repair wrong-host staging |
| 3. Prepare identities | Approved identity/SCOM administration; worker-account test shell | Establish separate read-only SCOM, publisher certificate/app role and delegated reader grants | Each path independently verified | Do not broaden grants, print tokens or recreate working identities speculatively |
| 4. Prepare private settings | Protected MCP-host location; operator | Populate both examples, UTF-8 encode and check effective binding, maps, gates, installed hash and budgets | Paired configuration validates with no unknown gates | Stop before activation; collect all failures instead of guessing one setting at a time |
| 5. Activate receiver | MCP host; authorized IIS operator | Back up settings, apply reviewed configuration/ACLs, recycle only the intended pool and probe configured HTTPS application URL | Validation passes and real HTTPS health is 200 | No global IIS/VM restart, TLS bypass or automatic state deletion |
| 6. Publish inventory | MCP host; read-only worker account | Run installed DLL with `--worker-once` and private config; then authorized source-status/list/details/summary calls | Commit, intended dataset/count/coverage and representative details | Launcher failure is not worker failure; stop on nonzero execution and inspect safe evidence |
| 7. Verify graph/health/alerts | Authorized reader plus source reviewer | Compare chosen retained relationships, role/health and alert examples with scoped evidence | Correct endpoints, states, timestamps and partial/missing behavior | Do not interpret absence as resolution/deletion or monitoring health as connectivity |
| 8. Accept selected metrics | Worker/source reviewer; separately approved test scope | Review mappings, units, timestamps and budgets; compare raw values/windows and measure source load | Accepted semantics and measured source envelope | Keep maps empty until accepted; no wider history/source reads by default |
| 9. Accept Studio callers | Copilot Studio; intended allowed/denied users | Test counts/details/health/alerts/available metrics and negative identity/scope cases | Source-backed answers and no unauthorized disclosure | Do not weaken OAuth or invent answers to make a demonstration pass |
| 10. Enable recurrence | MCP host; approved execution mechanism as worker account | Run one supervised `--worker` process with reviewed cadence, monitoring and recovery | Several later commits, membership refresh and controlled failure/recovery | No overlapping schedules; operator investigates stopped loop and freshness alerts |

## Definition of enterprise readiness

All applicable gates must have evidence: blocking findings closed; supported
platforms verified; scope/exposure proven; tool contracts preserved; load and
storage measured; recovery/upgrade rehearsed; repeatable package/CI checks;
clean-host and Studio acceptance; support ownership; explicit release approval.
Neither this plan, a successful lab inventory question nor offline tests satisfy
those gates alone.

See [evaluation acceptance gates](customer-evaluation.md) and the
[issue/fix log](deployment-issue-log.md) for complementary evidence boundaries.
