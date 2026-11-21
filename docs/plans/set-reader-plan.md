# Set-based relationship reader (v0.4) — plan

Status: implementation in progress on `feature/set-reader`. Lab validation (Phase 4) is pending.

## Goal

Add an optional `relationshipReadMode: "set"` to the collector profile. In set mode the collector reads each
configured Windows hosting relationship with one bounded SDK call instead of one call per source object. The
per-source path (`"perSource"`, the default) is unchanged. Completeness is never inferred from the set result.

## Phase 1 — Collector

- New `profile.configuration.relationshipReadMode`: `"perSource"` | `"set"`. Default `perSource`.
  Unknown value → `SCOM_SET_READ_MODE_INVALID`.
- Rename `ScomExperimentalSetReader.psm1` → `ScomSetRelationshipReader.psm1` and rework it:
  - class and relationship names come only from `windowsMapping` (no hardcoding);
  - `MaxResultCount = expectedTargets + 1`; result count equal to the cap → `SCOM_SET_READ_CAP_HIT`
    (fail closed, no dataset);
  - call exception → `SCOM_SET_READ_UNAVAILABLE`; fall back to `perSource` for this run and log it;
  - skip and COUNT (never abort on) targets that are not instances of the configured OS/volume class and
    sources outside the served computer set;
  - probe row members (`IsDeleted`, `RelationshipId`, `ManagementGroupId`) once via `Get-Member`; validate only
    those that are present;
  - output `Relationships`, `SkippedNonTarget`, `SkippedOutsideScope`, `CapHit`, `Complete = $false`,
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

Normal 3/3/5 identical to the per-source fixture; cap hit; call exception → fallback; NIC targets skipped;
outside-scope sources skipped; missing OS edge → incomplete with the computer listed; missing disk edge →
incomplete with the disk listed; `IsDeleted` absent tolerated; perSource suite unchanged; invalid mode rejected.
0 warnings.

## Phase 4 — Lab (owner, not part of this PR)

Lab validation on a real management group, run by the owner.

## Phase 5 — Docs only

`sizing-and-scope.md` call-model table for both modes with the scale tiers unchanged; `supported-configuration.md`
notes set mode "implemented, lab validation pending"; `deployment-guide.md` 6c one sentence on the mode the
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
3. `SCOM_SET_READ_CAP_HIT` is never retried or downgraded: it fails closed and no dataset is produced.
4. The cap is `expectedTargets + 1` over all rows of the relationship. A hosting relationship that also carries
   targets we do not read (for example network adapters under the logical-device relationship) can reach the cap
   and fail closed. This is implemented literally, with no extra configuration knob; Phase 4 must measure it.
