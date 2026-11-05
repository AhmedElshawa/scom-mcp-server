# Optional targeted current-state verification

## Status and boundary

The request/result transport, worker execution bridge, authorization and offline
fake-source flow are implemented. **No production SCOM live-check profile is
certified or shipped. Keep this disabled until the source contracts below are
established.** A generic collection profile is not a live-check profile.

The normal architecture remains scheduled collection and retained read-only
answers. A possible agent sequence is: find a retained critical application alert,
inspect its retained relationships and selected metrics, then call
`verify_object_current_state` for that one canonical object ID, **if enabled,
supported and authorized**. The result is current monitoring health and bounded
alert observations, not reachability, a guaranteed root cause, remediation or a
live graph/performance/global search.

The approved transport uses the existing HTTPS receiver and app-only publisher
identity. The source worker pulls requests outbound. It does not expose a new
inbound service on the SCOM host, load SDK assemblies into MCP, accept arbitrary
scripts/queries/hosts from callers, or create credentials/permissions.

## How it works

1. The delegated tool caller must be authorized for the fixed configured scope.
   The indexed read model must currently retain that exact object. Canonical
   nonempty GUIDs only; missing/unauthorized IDs reveal no source attributes.
2. With explicit server enablement and a recently polling verified provider,
   an expiring bounded request enters a volatile inbox. Without these, return
   `LIVE_NOT_ENABLED` or `LIVE_PROVIDER_UNAVAILABLE`, not retained data as live.
3. The long-running existing worker claims one request using its app-only
   ingestion role, exact approved publisher ID, source binding and pinned
   provider policy hash. The dedicated local hash-pinned PowerShell profile must
   freshly verify membership in the approved source scope **before** reading the
   target's health/alerts. Retained membership is only an admission prerequisite.
4. The worker joins its bounded child before posting the result. The receiver
   validates request/target/source/time/coverage/size bindings. Changed or removed
   source scope returns no attributes. Result acknowledgement may replay the
   same result once after transport ambiguity, never repeat source execution.
5. Before delivering, the tool checks retained authorization again and rejects
   a changed dataset or unavailable/removed object. The result declares
   `mode=targetedLive`, request ID, retained dataset ID, received/checked/scope
   times, health observation time and per-alert observation times.

`observedAtUtc` means the current source **read's observation time**, not an old
alert creation/last-change timestamp. Source verification must establish SDK
refresh/cache behavior; wrapping an old health value in a new timestamp is invalid.
`alertsComplete=true` is permitted only with verified complete source visibility
and complete bounded retrieval for the selected object. Empty partial results
never establish "no current alerts." No live result is merged into the background
dataset, and results never authorize future source access.

## Budgets, isolation and failure behavior

| Control | Default / hard bound |
|---|---|
| MCP request lifetime | 15 seconds / configurable 5..30 |
| Child source execution | 5 seconds / configurable 1..10, at least 2 seconds below request lifetime |
| Source query budget | At most 3 targeted source operations; must also prove backend result/work bounds |
| Alerts | 20 / configurable 1..100; complete or explicitly partial, never silently truncated |
| Pending/completed request slots | 8 / configurable 1..16 |
| Admission rate | 3 per caller/minute (1..10); 10 global/minute (1..30) |
| Active source leases | 1; at least 2 seconds between starts |
| Worker polling | 2 seconds / configurable 2..5, failure backoff to 30 seconds |
| Readiness | Last accepted poll within 6 seconds; busy background collection may make live verification unavailable |
| HTTP bodies | Claim 1 KiB; result 32 KiB; 5-second receiver body deadline |
| Worker HTTP | 3 seconds per attempt; no redirect/cookie following; fresh token per attempt; bounded responses |
| Alert fields | Target ID, alert ID, name up to 256 characters, severity, resolution state and observation time only |

Same-caller/same-target **pending** calls coalesce; different callers never share
request/result handles. Each entry allows at most two waiting tool calls, bounding
coalesced waiters as well as queued jobs. No completed-result cache is presented as a fresh check.
Unclaimed cancellation removes the request. An already claimed read may continue
only within its execution bound; cancellation does not abandon its child or start
a replacement in parallel.

One semaphore serializes live work with scheduled collection in the existing
worker. The checkpoint lease still prevents another worker using that configured
checkpoint. Multi-worker/multi-receiver scaling is unsupported. When collection
is busy, do not increase live concurrency or bypass the gate to improve latency.

Request expiry does **not** release an ambiguous active source lease. A late
worker acknowledgement, only after joining its child, releases it without
delivering late data. A crashed/missing worker or lost acknowledgement may leave
the volatile broker blocked. Preserve safe diagnostic IDs; an operator must
establish that the old worker/child is gone before a controlled restart.
Never automatically reassign the old request. Server restart loses pending
requests/results and returns unavailable through broken/failed calls, not cached
live success. There is no durable request replay across receiver restart.

Sanitized audit events carry request/correlation IDs, outcomes and safe exception
types. They do not log target names, alert text, profile configuration or tokens.
Source text remains untrusted data.

## Configuration and source-provider acceptance

The server examples contain a disabled `TargetedLive` section. Enabling requires
indexed read model mode, `PublisherObjectId` in the already-approved ingestion
publisher list, and a 64-hex `ProviderPolicySha256`. Budgets use the table above.
Do not grant extra publisher roles simply to bypass a rejection.

The worker example contains disabled `LiveChecks`. Enabling additionally requires
`QueryContractVerified=true` **with independent evidence**, a configured `Source`
(`managementGroupId`, `scopeKind`, `groupId`) matching the receiver and a dedicated
installed `Profile` using the existing protected Desktop PowerShell path/hash/
configuration settings. Set its output cap at most 32768 and deadline no greater
than the receiver execution budget. Its policy hash is the SHA256 of
`Wire.Encode(new { profile.ScriptSha256, profile.Configuration })`, as produced by
`LiveWorkerSettings.Policy`; exact configuration serialization participates in
the pin. The server independently approves this hash. Changed script/configuration
requires review and matching pins, not automatic trust.

The profile receives protocol version 1 plus fixed configuration and a typed
`LiveCheckRequest`; it returns `LiveCheckObservation` JSON only. stdout/stderr,
input size, strict members/required fields, deadline and Windows Job Object
cleanup use the existing protected bridge. Hosts require the same Windows
10/Server 2016+ job support as background collection. The `--worker` long-running
mode polls only after explicit enablement; `--worker-once` refuses enabled live
checks instead of starting hidden recurrence.

**Blocking source contracts:** Microsoft's
[Get-SCOMClassInstance](https://learn.microsoft.com/en-us/powershell/module/operationsmanager/get-scomclassinstance?view=systemcenter-ps-2025)
documents ID-scoped retrieval and
[Get-SCOMAlert](https://learn.microsoft.com/en-us/powershell/module/operationsmanager/get-scomalert?view=systemcenter-ps-2025)
documents `-Instance` and alert filters. These establish candidate targeted
operations, **not** server-side maximum rows/work, complete role visibility,
fresh nested approved-group membership or cache-refresh guarantees. The inspected
API reference candidate for `MonitoringObject.GetMonitoringAlerts` returned 404;
no method signature was invented from it.

Before any real provider can be enabled, establish exact installed query methods,
independent scope/role completeness, fresh target membership proof, fresh health
reads and bounded complete/partial alerts within 3 source operations and the
execution budget. A full management-group enumeration, graph walk, performance
history read, or post-materialization count check alone is not acceptable.
If these cannot be proven, return unavailable; do not broaden queries or claim
this transport itself bounds SCOM backend work. No live probes or permission
changes were performed to satisfy this gate.

## Operator acceptance

Use synthetic fake-source tests first: authorization, scope changes, removed
members, unknown IDs, coalescing/isolation, cancellation/deadlines, overflow,
partial/empty alerts, lost result response, replay and source invocation counts.
Then obtain separately authorized provider/source/Studio acceptance. Keep the
feature disabled until that evidence and the runtime/transport pins are reviewed.
This document is not a deployment, credential, permission, merge or release action.
