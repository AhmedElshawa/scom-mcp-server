# Set-based relationship reader (v0.4) — plan

Status: implementation on `feature/set-reader`, redesigned after the Phase 4 lab (see "Verified SDK finding").
The Phase 0 spike is recorded in [`docs/evidence/set-read-spike.json`](../evidence/set-read-spike.json). Lab
re-validation of the redesigned reader is a separate step.

## Verified SDK finding

Lab-verified on SCOM 2025 (SDK assembly 7.0.5000.0):

- `GetRelationshipObjects<T>(ManagementPackRelationship, DerivedClassTraversalDepth, ObjectQueryOptions)` throws
  `System.NotImplementedException` ("MaxResultCount is not supported with this method", from
  `ObjectQueryOptions.ValidateQueryOptionsForRelationships`) when `MaxResultCount` is set. The original design
  (`MaxResultCount = expectedTargets + 1`) therefore always fell back to `perSource` on the real SDK.
- The same call succeeds with `ObjectQueryOptions.Default` (`MaxResultCount` 0): the OS relationship returned
  3 rows and the logical-device relationship returned 9 rows (5 disks and 4 network adapters).
- Rows carry `IsDeleted`, `RelationshipId` and `ManagementGroupId`.

The set call cannot be limited by the SDK, so the bound is enforced by the collector: a pre-call budget on the
expected targets and a post-call limit on the rows returned (see Decisions 18 to 21).

## Goal

Add an optional `relationshipReadMode: "set"` to the collector profile. In set mode the collector reads each
configured Windows hosting relationship with one bounded SDK call instead of one call per source object. The
per-source path (`"perSource"`, the default) is unchanged. Completeness is never inferred from the set result.

## Phase 1 — Collector

- New `profile.configuration.relationshipReadMode`: `"perSource"` | `"set"`. Default `perSource`.
  Unknown value → `SCOM_SET_READ_MODE_INVALID`.
- Rename `ScomExperimentalSetReader.psm1` → `ScomSetRelationshipReader.psm1` and rework it:
  - class and relationship names come only from `windowsMapping` (no hardcoding);
  - `ObjectQueryOptions.Default` (no `MaxResultCount`, which the SDK rejects for this call); expected targets
    greater than `maxObjects` → `SCOM_SET_READ_BUDGET` before any SCOM call (fail closed); rows returned greater
    than `maxObjects × 8` → `SCOM_SET_READ_OVERSIZE` and fall back to `perSource` for this run, logged;
  - call exception → `SCOM_SET_READ_UNAVAILABLE`; fall back to `perSource` for this run and log it;
  - skip and COUNT (never abort on) targets that are not instances of the configured OS/volume class and
    sources outside the served computer set;
  - probe row members (`IsDeleted`, `RelationshipId`, `ManagementGroupId`) once via `Get-Member`; validate only
    those that are present;
  - output `Relationships`, `SkippedNonTarget`, `SkippedOutsideScope`, `Complete = $false`,
    `SourceWorkBoundVerified = $false`.
- `Get-SCOMGenericScan`: after enumeration, in set mode make one call per configured relationship;
  `graphQueries` = number of set calls; emit `graph.skippedNonVolumeTargets` and
  `graph.skippedOutsideScopeSources`. The per-source path is unchanged. Completeness remains set reconciliation
  in `ConvertTo-SCOMWindowsInventory`; the set result count is never evidence of completeness.
- Alerts: one bulk `Get-SCOMAlert` with criteria replaces per-object calls, used by both modes.
- `Invoke-SCOMGenericGroupProfile.ps1` reads and validates the mode.

## Phase 2 — Setup / generator

- `SetupConfigurationValidation.cs` accepts the enum.
- `New-ScomMappingProfile.ps1` writes `"set"` only when both relationships were observed on at least one sampled
  computer, and records the choice in `evidence.json`.
- `worker.example.json` / `.md` and `configuration-reference.md` updated.

## Phase 3 — Tests (synthetic; wired into `validate.ps1`, `package.ps1`, `Test-Package.ps1`)

Normal 3/3/5 identical to the per-source fixture; budget exceeded; oversize → fallback; call exception →
fallback; NIC targets skipped; outside-scope sources skipped; missing OS edge → incomplete with the computer
listed; missing disk edge → incomplete with the disk listed; `IsDeleted` absent tolerated; perSource suite
unchanged; invalid mode rejected. The synthetic manager throws `NotImplementedException` when `MaxResultCount`
is set, as the real SDK does. 0 warnings.

## Phase 4 — Lab (owner, not part of this PR)

Lab validation on a real management group, run by the owner.

## Phase 5 — Docs only

`sizing-and-scope.md` call-model table for both modes with the scale tiers unchanged; `supported-configuration.md`
notes set mode (now: the verified SDK finding and the new bound); `deployment-guide.md` 6c one sentence on the mode the
generator chose; `troubleshooting.md` the three new codes.

## Guardrails

No changes outside `Collector/`, `ScomMcp/SetupConfigurationValidation.cs`, `Setup/New-ScomMappingProfile.ps1`,
`Validation/`, `examples/` and `docs/` (plus the requested `validate.ps1` / `package.ps1` wiring). No
`hostingLabPreview` / `hostingLabSetRead` revival. No new dependencies.

## Decisions taken

1. The plan text the owner said would follow the request was not included. This file was reconstructed from the
   written task phases and guardrails only.
2. A set-read call that cannot be made at all (SDK signature not found or not unique, call throws) is treated as
   `SCOM_SET_READ_UNAVAILABLE` and the run falls back to `perSource`; the fallback is logged. This is the
   conservative choice because per-source is the validated path.
3. `SCOM_SET_READ_BUDGET` is never retried or downgraded: it fails closed before any SCOM call and no dataset is
   produced. (The original `SCOM_SET_READ_CAP_HIT` was removed in the redesign, Decisions 18 to 21.)
4. Superseded: the original row cap `expectedTargets + 1` could not be applied because the SDK rejects
   `MaxResultCount` for this call (Decision 18).
5. Counters travel through the optional `-GraphCounters` hashtable and the operational log (`-Counters`, event
   `scan.setReadFallback`), not the scan object, because the strict wire contract has no `graph` member.
6. Bulk alerts: one whole-management-group `Get-SCOMAlert -Criteria 'ResolutionState < 255'` call, used by both
   modes. `MaxAlerts` applies to in-scope alerts only. This may be heavier than per-object reads for small groups.
7. Set mode requires `supportedWindows` plus `windowsMapping` and is invalid with the lab preview.
8. Set mode reads only the mapped OS and volume hosting relationships as sets; other relationships stay per-source.
9. Set-call skips are counted and never abort. Targets outside the served set count as outside edges.
10. Superseded: the `CapHit` output property was removed with the row cap.
11. A served set that is empty on either side means no call is made.
12. In per-source mode the skip counters are 0 and `graphQueries` counts the per-source calls.
13. Superseded: non-disk rows no longer reach a cap; they are skipped and counted (Decision 9).
14. "Missing OS edge: computer listed" means the computer stays in `windowsInventory.devices` and
    `expectedComputerIds`; "missing disk edge: disk listed" means the volume is in scan `objects` but not in
    `windowsInventory.volumes`, with `inventory.complete` false.
15. `docs/supported-configuration.md` does not exist; the set-mode note is in `docs/limitations.md`.
16. `verify-docs.ps1` does not exist; `Test-Package.ps1`, `Test-UpgradeGuide.ps1`, the other doc checks and the
    full `validate.ps1` serve as the docs verification.
17. The generator writes `set` only if both relationships were observed AND no non-disk device shares the disk
    relationship; otherwise it writes `perSource` and records the reason in `evidence.json`.
18. Redesign after the Phase 4 lab: `MaxResultCount` is removed from the set call because the SDK throws
    `NotImplementedException` for it on this method (see "Verified SDK finding"). The call uses
    `ObjectQueryOptions.Default`.
19. Pre-call guard: if any relationship's expected targets exceed `maxObjects`, the run fails closed with
    `SCOM_SET_READ_BUDGET` before any SCOM call. It is not downgraded to `perSource`.
20. Post-call guard: if a relationship returns more than `maxObjects × 8` rows, the run fails over with
    `SCOM_SET_READ_OVERSIZE`: that run falls back to `perSource` and logs `scan.setReadFallback` and the
    `setReadOversize` counter. The factor 8 leaves room for non-target rows (such as network adapters) that the
    set call returns and the reader skips.
21. Set mode materialises the full relationship set, so memory scales with management-group size, not with the
    served scope. The SDK offers no way to bound it before the call; the post-call guard only limits what is kept.
22. The generator rule in Decision 17 was written for the removed row cap. It is left unchanged in this PR
    (the owner did not ask for it to change) and is a follow-up candidate: skipped non-disk rows no longer make
    `set` unsafe.
23. Process lesson. **Phase 0 (a 30-second read-only spike against the real SDK) is mandatory before implementing
    any new SDK call. Synthetic tests prove plumbing, not SDK contracts. This rule exists because the
    `MaxResultCount` `NotImplementedException` was found only after a full day of implementation.** Applied here:
    the spike for the redesigned call is recorded in `docs/evidence/set-read-spike.json`. The rule is also in
    `CONTRIBUTING.md`.
24. Third SDK-contract defect found only on the real SDK: `ObjectQueryOptions.Default` is a static field, not a
    property, so a reflection lookup of the property returned null and the lab scan aborted. Rule: the spike
    script's exact call pattern is the production call pattern; reflection against SDK types only when the spike
    used reflection. The reader now reads `[Microsoft.EnterpriseManagement.Common.ObjectQueryOptions]::Default`
    exactly as the spike did.
25. Structural fix so this class of bug cannot abort a scan: all set-reader SDK interaction (type resolution,
    options construction, method lookup, invoke, row member probing) runs inside one try that maps any exception
    to `SCOM_SET_READ_UNAVAILABLE`, which falls back to `perSource`. Only the budget check is outside it.
    The two deliberate data-fault codes (`SCOM_SET_READ_OVERSIZE`, `SCOM_RELATIONSHIP_ROW_INVALID`) pass through.
    The synthetic suite injects a failure at each stage (missing `Default`, throwing `Default`, missing method,
    null options, wrong manager type, throwing call, null result) and asserts fallback, not abort. The fake SDK
    exposes `Default` as a field so the suite matches the real type.
26. Windows PowerShell 5.1 reads a throwing property getter as `$null`, so a row whose members cannot be read is
    indistinguishable from an invalid row and fails closed as `SCOM_RELATIONSHIP_ROW_INVALID` (covered by a test).
27. Supersedes Decisions 17 and 22. The generator writes `relationshipReadMode` `set` whenever both hosting
    relationships were observed on the sampled computers, regardless of non-disk devices (for example network
    adapters) that share the disk relationship: the lab (SCOM 2025, SDK 7.0.5000.0) proved the reader skips and
    counts them (`skippedNonVolumeTargets=4`, 8 relationships identical to `perSource`). It writes `perSource`
    otherwise (a run that observes neither relationship produces no mapping and exits non-zero). Evidence reason
    codes are `BOTH_RELATIONSHIPS_OBSERVED` and `RELATIONSHIP_NOT_OBSERVED`; `NON_DISK_EDGES_SHARE_DISK_RELATIONSHIP`
    is no longer written. The generator warning about non-disk edges remains, now saying they are skipped and counted.
