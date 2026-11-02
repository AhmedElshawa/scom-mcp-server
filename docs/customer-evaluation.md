# Customer evaluation: release preparation and acceptance

**Status: evaluation preparation, not a production-certified release.**
The indexed implementation is opt-in. The legacy snapshot path remains the
default unless explicitly configured otherwise. An open pull request or source
branch is not a tagged, supported binary release.

## What is available

- Read-only MCP tools over committed retained data, not per-question SDK queries.
- Separately executed scoped SCOM worker, authenticated ingestion and local
  indexed read model with independent worker checkpoint storage.
- Inventory-only onboarding with explicit missing/partial/unavailable behavior;
  performance metrics need their own approved mappings and acceptance.
- Deployment hardening for UTF-8 BOM configuration, management-group null binding,
  application-prefix publisher URLs and sanitized diagnostic output.
- An [ordered deployment runbook](indexed-deployment-runbook.md) and
  [sanitized issue/fix log](deployment-issue-log.md).

## Package preparation

On an approved build machine with the supported SDK and PowerShell 7, follow the
README restore and offline validation instructions. Then choose a **new output
directory**, never a live application directory:

```powershell
pwsh -NoProfile -File .\package.ps1 -OutputDirectory .\artifacts\customer-evaluation
pwsh -NoProfile -File .\Validation\Test-Package.ps1 -PackageDirectory .\artifacts\customer-evaluation
```

The second command checks package structure, synthetic example JSON, documentation
links and absence of common runtime/private state files. It does not start the
application, connect to SCOM, certify SDK support or prove that arbitrary files
contain no secrets. Review the allowlisted package contents before distribution.

The package contains `server`, `collector`, `docs`, README and license files.
It is framework-dependent: it does not install runtimes, the Operations console,
IIS, identities, certificates, ACLs or a recurring execution mechanism.
Source build/test commands in the README require a source checkout, not just
this binary distribution.

Record the reviewed source commit and hashes of distribution files in the release
record. Do not equate assembly version `1.0.0.0` with proof of a particular commit.
Keep actual configuration and operational evidence outside public source/package.

## Release review findings addressed

| Finding | Resolution |
|---|---|
| Downloaded package README linked to omitted deployment docs and image | Packaging now includes the runbook, issue log, evaluation notes and architecture assets. Package validation checks local Markdown file links. |
| Runbook's source-only example links did not match packaged layout | Runbook explicitly identifies both source and packaged example locations. |
| Reported lab recovery could be mistaken for production acceptance | Evaluation status and evidence boundaries are explicit; no merge or live upgrade is implied. |
| Four independent-review correctness findings | Implemented crash-safe Windows collector lifetime, receiver DB ownership checks/migration, raw-observation lookup and legacy blank-ID compatibility with exact-trigger regressions. Independent re-review and intended-host acceptance remain pending. |

This was a bounded deployment/package-readiness review with regression validation,
not an exhaustive independent review of every feature-branch change or a security
certification.

### Upgrade constraints added by the correctness fixes

The Windows worker requires Windows 10/Server 2016+ Job Object process-creation
support. It fails closed if the execution host disallows required job membership;
test the actual approved service/task mechanism before rollout.

Receiver initialization recognizes the existing rollback-journal read-model
schemas and preserves pinned scope/current/session state during transactional
migration. Worker checkpoints, foreign/unknown schemas and WAL-mode databases are
refused without alteration. Use an approved stopped-writer backup and offline
review/conversion procedure for unsupported files; never clear ownership pins,
delete state or point the receiver at the worker checkpoint to bypass startup.

## Gates before calling a customer deployment accepted

| Gate | Evidence still required for each deployment |
|---|---|
| Release identity | Reviewed/approved PR, intended commit, tagged version/release notes and reviewed package/hash record. No merge or tag is performed by these instructions. |
| Clean installation | An authorized operator follows only the shipped runbook on a clean supported host, recording steps, deviations and corrections. Not yet established by offline packaging checks. |
| SCOM compatibility and scope | Exact SDK/management-pack versions, read-only identity, independently verified complete expected membership and reviewed relationship/property contracts. Do not enable verification flags by assumption. |
| Inventory acceptance | Successful first commit and authenticated source status, pages, details and summary with matching dataset/count evidence and honest coverage. Lab success does not replace customer evidence. |
| Continuous operation | Supervised recurring collection, subsequent datasets and membership changes, failure monitoring, freshness/cadence suitability, disk budgets and certificate rotation. |
| Recovery and upgrade | Protected backups, controlled package replacement, checkpoint/policy compatibility and approved failure/restore rehearsal. Never delete pinned state to make checks pass. |
| Copilot Studio identity | Intended per-caller delegated authorization and denied-caller/scope behavior through the real connector. |
| Metrics, if enabled | Timestamp semantics, rule/unit mappings, raw observation parity and approved 14-day workload/budget tests. Inventory acceptance alone is insufficient. |
| OS lifecycle, if requested | A verified lifecycle source/mapping with applicability, provenance and explicit unknown results. Not implemented by this release. |

Guided deployment checking/configuration generation is proposed future work,
not an included installer. Production rollout remains blocked on the applicable
gates above; do not promise a failure-free installation or universal support.
