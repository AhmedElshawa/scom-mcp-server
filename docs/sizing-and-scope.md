# Sizing and scope

## Per-object call model

Each collected object costs roughly **3 SDK calls**, and a Windows computer yields roughly
**5 objects** (the computer, its operating system and about 3 logical disks), so about
**15 SDK calls per computer**. The estimate comes from the lab (3 computers, 11 objects)
and the collector's graph-query budget; it is not a Microsoft sizing figure.

| Computers | Objects (≈5 each) | SDK calls (≈3 each) |
|---:|---:|---:|
| 50 | 250 | 750 |
| 300 | 1,500 | 4,500 |
| 1,000 | 5,000 | 15,000 |

The shipped example limits (`maxObjects` 1000, `maxGraphQueries` 10000) cover roughly 200
computers. Above that, raise them deliberately and re-validate; see
[configuration-reference.md](configuration-reference.md). These figures and the scale tiers
below are unchanged by the read modes described next; they remain the conservative planning
numbers.

## Call model by read mode

`profile.configuration.relationshipReadMode` selects how relationships are read. Counts
below are for the generated Windows mapping (two relationships: OS hosting and disk
hosting) and the lab-shaped example of 3 computers, 3 operating systems and 5 logical disks
(11 source objects).

| | `perSource` (default, lab-validated) | `set` |
|---|---|---|
| Relationship calls per run | objects × relationships = 11 × 2 = **22** | one per mapped relationship = **2** |
| Grows with | number of objects | number of relationships (not objects) |
| `maxGraphQueries` counts | each per-source call | each set call |
| Active alerts | **1** bulk `Get-SCOMAlert` per run | **1** bulk `Get-SCOMAlert` per run |
| Row bound | one source per call | expected targets ≤ `maxObjects` before the call; rows returned ≤ `maxObjects × 8` after it |
| Memory | scales with the served scope | **materialises the full relationship set**: scales with management-group size |
| Other configured relationships (for example monitoring role) | per source | per source (only the OS and disk relationships are read as sets) |

Both modes read alerts with one bulk call (criteria `ResolutionState < 255`) instead of one
call per object. The bulk call is management-group wide; the collector drops alerts whose
parent is outside the served scope and applies `maxAlerts` to in-scope alerts only. For a
small group scope in a large management group this read can return more rows than the
previous per-object reads did, so measure it before relying on it.

Set-mode safety rules:

- **Verified SDK limit.** On SCOM 2025 (SDK 7.0.5000.0) the set call
  `GetRelationshipObjects<T>(relationship, depth, options)` throws `NotImplementedException` when
  `ObjectQueryOptions.MaxResultCount` is set, so the collector cannot ask the SDK to cap the result. It
  uses `ObjectQueryOptions.Default` and bounds the read itself. Set mode therefore materialises the
  **full relationship set**: memory scales with management-group size, not with the served scope. It is
  still 2 calls.
- **Fail closed on the budget.** If the expected targets of a relationship exceed `maxObjects`
  (`SCOM_SET_READ_BUDGET`) nothing is published for the run and no SCOM call is made.
- **Fall back on an oversize result.** If a set read returns more than `maxObjects × 8` rows
  (`SCOM_SET_READ_OVERSIZE`), that run falls back to `perSource` and logs `scan.setReadFallback`
  (`setReadFallback` and `setReadOversize` true in the `scan.completed` counters). The rows have already
  been materialised by then; the limit bounds what the collector keeps, not what the SDK allocates.
- **Fall back on call failure.** If the set call itself throws
  (`SCOM_SET_READ_UNAVAILABLE`), that run falls back to `perSource` and logs `scan.setReadFallback`.
- **Skip and count, never abort.** Targets that are not an instance of the configured OS or
  disk class and sources outside the served computer set are skipped and counted
  (`skippedNonVolumeTargets`, `skippedOutsideScopeSources` in the `scan.completed` log
  counters).
- **The result count is never evidence of completeness.** Completeness is decided only by
  reconciling the expected computers and disks against the returned edges; a missing edge
  makes the inventory incomplete and is listed.

The SDK provides no completeness or cancellation contract for either mode, so SCOM-side work
is not bounded by these counts. See [limitations.md](limitations.md).

## Recommendation

1. **Group scope first.** Start with a dedicated group of the computers you care about. It
   bounds objects, SDK calls and runtime.
2. **Whole management group as a measured event.** Run it once, off-peak, with a SCOM
   administrator present to watch the management server and database. Record runtime and call
   counts before enabling a schedule.
3. **Hourly recurrence by default** for inventory (`schedule.interval` `01:00:00`) and a
   receiver freshness budget longer than the interval. Shorten only with measurement.

Lab evidence: a 3-computer group completed at a 5-minute cadence with fresh, complete 3/3
coverage. The hourly default and larger sizes have **not** been run in the lab.

## Memory unit

The `PhysicalMemory` value is in KB, so the worker example uses `memoryToMiB` = `0.0009765625`.
Evidence: `get_device_details` returned 14,335.6 MiB for a server with 14 GiB of RAM; the
management pack metadata that labels the property as MB is wrong for this value. See
[worker.example.md](../examples/worker.example.md).

## Future work

Set mode (above) is implemented but not lab validated; larger tiers stay unvalidated until
it is measured.

See [limitations.md](limitations.md) for what the SDK does and does not guarantee.
