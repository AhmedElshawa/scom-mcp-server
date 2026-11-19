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
[configuration-reference.md](configuration-reference.md).

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

A set-based reader that collects many objects per SDK call would cut the per-object cost.
It is not implemented.

See [limitations.md](limitations.md) for what the SDK does and does not guarantee.
