# MCP server specification

This document specifies the wire-level behavior of the SCOM MCP receiver (`ScomReadOnlyMcp`, version `1.0.0`): transport, HTTP endpoints, token validation, every MCP tool with its parameters and response fields, pagination, rate and size limits, error codes, freshness semantics, and the ingestion contract used by the worker. Every route, default and code below was verified against the source in this package; anything that could not be determined is marked "Not determined from source". This is an unsigned community release; nothing here is a certification or a statement of customer acceptance. Related documents: [architecture.md](architecture.md), [configuration-reference.md](configuration-reference.md), [troubleshooting.md](troubleshooting.md), [security.md](security.md), [limitations.md](limitations.md), [copilot-studio-integration.md](copilot-studio-integration.md), [entra-setup.md](entra-setup.md).

## 1. Transport and endpoints

- **Transport:** MCP Streamable HTTP, hosted by ASP.NET Core in **stateless** mode (no server-side MCP session). Server info: name `ScomReadOnlyMcp`, version `1.0.0`.
- **Format:** JSON over HTTPS. The receiver runs on IIS behind a TLS binding (see [network-and-ports.md](network-and-ports.md)).

| Route | Method | Authorization | Purpose |
|---|---|---|---|
| `/healthz/freshness` | GET | Anonymous | Returns only `{"status","ageSeconds"}`: 200 when the committed dataset is fresh, 503 when `stale`, `expired` or `unavailable`. Mapped only when `ReadModel:Enabled` is true (404 otherwise). Intended for external monitors. |
| `/healthz` | GET | Anonymous | Returns `{"service":"ScomReadOnlyMcp","version":"1.0.0"}`. Liveness only; it does not prove that data is fresh. |
| `/mcp` | MCP Streamable HTTP | Policy `ScomRead` | All MCP tools in section 4. |
| `/ingestion/sessions/{sessionId}` | POST | Policy `ScomIngest` | Begin an ingestion session. Mapped only when `ReadModel.Enabled` is true. |
| `/ingestion/sessions/{sessionId}/batches/{sequence}` | POST | Policy `ScomIngest` | Append batch `sequence` (integer). |
| `/ingestion/sessions/{sessionId}/commit` | POST | Policy `ScomIngest` | Commit with the manifest as body. |
| `/ingestion/sessions/{sessionId}/fail` | POST | Policy `ScomIngest` | Mark the session failed. |
| `/ingestion/live/claim` | POST | Policy `ScomIngest` | Targeted-live provider claim. Mapped only when `TargetedLive.Enabled` is true. |
| `/ingestion/live/result` | POST | Policy `ScomIngest` | Targeted-live provider result. Mapped only when `TargetedLive.Enabled` is true. |

Unhandled exceptions return HTTP 500 `{"error":"REQUEST_FAILED"}`; the operational log records the trace ID and error type only (see [troubleshooting.md](troubleshooting.md#where-logs-live)).

### Request-size and timing limits on `/mcp`

| Limit | Value |
|---|---|
| Maximum `POST /mcp` body | 32768 bytes (HTTP 413 above this; enforced by `Content-Length` and while streaming) |
| JSON maximum depth | 32 |
| Scope pre-check | For `tools/call`, if `arguments.scopeId` is a string the scope is authorized before dispatch; failure returns HTTP 403 `{"error":"FORBIDDEN"}` |

## 2. Authentication and authorization

Tokens are Microsoft Entra ID v2 access tokens validated by the ASP.NET Core JWT bearer handler.

### 2.1 Token validation (all routes except `/healthz` and `/healthz/freshness`)

| Rule | Value |
|---|---|
| Authority | `https://login.microsoftonline.com/<TENANT_ID>/v2.0` (HTTPS metadata required) |
| Accepted issuers | `https://login.microsoftonline.com/<TENANT_ID>/v2.0` and `https://sts.windows.net/<TENANT_ID>/` |
| Audience | Must exactly equal one entry of `Auth.Audiences` (no trailing-slash tolerance) |
| Lifetime | Required and validated; clock skew 30 seconds |
| Signature | Signed tokens required; algorithm `RS256` only |
| Claim mapping | Inbound claim mapping disabled; token not saved; error details not returned |

A missing or invalid token yields the standard bearer-challenge HTTP 401. A policy failure yields HTTP 403.

### 2.2 Policy `ScomRead` (`/mcp`)

All conditions must hold:

1. Authenticated and `tid` equals `Auth.TenantId`.
2. The space-separated `scp` claim contains `Auth.RequiredScope` (ordinal comparison).
3. The caller matches exactly one entry of `Auth.Callers`: `oid` (parsed as a GUID) equals the entry's `ObjectId`; or, for a `UserPrincipalName` entry, exactly one distinct lower-cased value among `preferred_username` / `upn` / `unique_name` equals it (more than one distinct value fails closed).
4. The matched entry's `Scopes` contain `scom-lab`. `scom-lab` is the only scope id the server knows.
5. When `ReadModel.Enabled` is true, every caller entry must have an `ObjectId` (UPN-only entries are rejected at startup).

### 2.3 Policy `ScomIngest` (`/ingestion/*`)

All conditions must hold:

1. Authenticated, with exactly one `tid` equal to `Auth.TenantId`.
2. **No** `scp` claim (an application-only token).
3. Exactly one `oid`, a GUID listed in `ReadModel.PublisherObjectIds`.
4. A `roles` claim equal to `ReadModel.IngestionRole` (default `Scom.Ingest`).

Obtain such a token with the client-credentials flow, scope `<RESOURCE>/.default`, for the publisher application `<PUBLISHER_APP_ID>`; the shipped worker authenticates with a certificate client assertion (RSA, at least 2048 bits, from `LocalMachine\My`).

## 3. Rate limits

- **`/ingestion/*` (including `/ingestion/live/*`):** a concurrency limiter named `ScomIngestion` with permit limit 1 and queue limit 0. A second concurrent request is rejected with HTTP 429.
- **`/mcp`:** a per-caller limiter named `ScomRead`, partitioned by the token's `oid`: 30 requests per fixed one-minute window and at most 4 concurrent requests per caller, with no queue. A request over either limit is rejected with HTTP 429. It runs after authentication and authorization, so unauthenticated or unauthorized requests get 401/403 first and do not consume a caller's budget. `/healthz` is anonymous and not limited. The targeted-live admission limits (section 4.3) and the 32 KB body cap still apply.
- Throttling at the network or API-gateway layer is outside this package: Not determined from source.

## 4. MCP tools

All tools are annotated read-only, non-destructive and closed-world (`verify_object_current_state` is additionally not idempotent). The optional `scopeId` parameter accepts only `scom-lab` (the default).

| Tool | Registered when |
|---|---|
| `get_source_status`, `get_inventory_summary`, `list_devices`, `get_device_details`, `get_utilization`, `get_capacity_summary`, `list_os_support_assessments` | Always |
| `list_monitored_objects`, `get_monitored_object`, `list_object_relationships`, `list_monitored_alerts`, `list_metric_observations` | `ReadModel.Enabled` |
| `verify_object_current_state` | Always registered; returns `LIVE_NOT_ENABLED` unless `TargetedLive.Enabled` |

Data source for the first group: when `ReadModel.WindowsToolsEnabled` is true the indexed Windows reader serves it from the SQLite read model; otherwise the legacy snapshot reader is used (`Source.SnapshotEnabled`). Setup requires the indexed read model (see [configuration-reference.md](configuration-reference.md)).

### 4.1 Windows inventory and capacity tools

Common filter parameters for `get_inventory_summary` and `list_devices` (case-insensitive, lower-cased before use):

| Parameter | Type | Allowed values / limits |
|---|---|---|
| `scopeId` | string, optional | `scom-lab` |
| `entityKind` | string, optional | `computer`, `networkDevice` (network devices are unsupported and yield a limitation) |
| `osFamily` | string, optional | `windows`, `linux`, `unknown` |
| `osVersion` | string, optional | 1–256 characters |
| `health` | string, optional | `healthy`, `warning`, `critical`, `unknown` |
| `virtualization` | string, optional | `physical`, `virtual`, `unknown` |
| `groupId` | string, optional | 1–100 characters |

| Tool | Parameters | Notes |
|---|---|---|
| `get_source_status` | `scopeId?` | Dataset provenance, counts and capabilities. |
| `get_inventory_summary` | common filters | Counts and breakdowns. |
| `list_devices` | common filters; `pageSize` (int, default 20, 1–50); `cursor` (string, optional) | Ordered by hostname, then `entityId`. |
| `get_device_details` | `entityId` (GUID, **required**); `scopeId?` | Returns `device`, `volumes`, `managementGroup`. `NOT_FOUND` if unknown. |
| `get_utilization` | `metric` (**required**: `cpuPercent`, `memoryUsedPercent`, `diskFreePercent`); `entityId?` (GUID); `window` (`1h` default, `24h`, `7d`); `statistic` (`latest` default, `mean`, `max`); `top` (int, default 20, 1–20); `comparePrevious` (bool, default false); `sortOrder` (`asc`, `desc`; default `desc`); `thresholdPercent` (0–100); `comparison` (`lt`, `gt`, strict); `scopeId?` | `thresholdPercent` and `comparison` must be supplied together. Window is `(collectedAt − span, collectedAt]`. Memory uses native `memoryUsedPercent` when present, otherwise derives it from available MiB with a known total. |
| `get_capacity_summary` | `resource` (**required**: `memory`, `disk`); `scopeId?`; `entityId?`; `groupId?`; `minFreePercent?`, `maxFreePercent?` (0–100, inclusive, min ≤ max); `pageSize` (default 20, 1–50); `cursor?` | Observation cutoff is `collectedAt − FreshMinutes`. Ordered by `entityId`. |
| `list_os_support_assessments` | `scopeId?`; `assessment?` (`mainstream`, `extended`, `ended`, `unknown`, `unsupported`, `approaching`); `pageSize` (default 20, 1–50); `cursor?` | See section 4.4 and [os-lifecycle-assessment.md](os-lifecycle-assessment.md). |

#### Response envelope

Every tool in this group returns one JSON text content block with this envelope (`isError` is set on errors):

| Field | Meaning |
|---|---|
| `schemaVersion` | `"1.0"` |
| `status` | `ok`, `partial` or `unavailable` |
| `sourceMode` | `"scom"` |
| `datasetId` | Committed dataset identifier |
| `scope` | `{id, label}` |
| `data` | Tool-specific payload (below); on error `{errorCode, message}` |
| `asOfUtc` | Dataset collection time |
| `freshness` | `{ageSeconds, state}`; `state` is `fresh` or `stale` |
| `coverage` | `{expectedDeviceCount, uniqueDeviceCount, complete, collectionStartedAtUtc}` |
| `limitations` | Array of limitation codes (section 4.5) |
| `sourceRefs` | Source references |
| `correlationId` | Request correlation id |

`status` is `partial` when any of these hold: the dataset is stale (age greater than `FreshMinutes` × 60 s), the snapshot reports partial, coverage is incomplete, validation limitations exist, or the tool adds limitations.

#### Payload fields (`data`)

| Tool | Fields |
|---|---|
| `get_source_status` | `mode`, `capabilities`, `collectionStartedAtUtc`, `collectedAtUtc`, `expectedDeviceCount`, `uniqueComputerCount`, `metricObservationCount`, `optimizedObservationCount`, `nonOptimizedObservationCount`, `duplicateDevicesResolved`, `duplicateVolumesResolved`, `duplicateSamplesRemoved` |
| `get_inventory_summary` | `uniqueComputerCount`, `countsAreCompleteForFilteredDataset`, breakdowns `health`, `osFamily`, `osVersion`, `virtualization`, `osCaption` (each a list of `{value,count}`), `unknownMemoryCount`, `unknownProcessorCount`, `duplicatesResolved`, `volumesExcludedFromComputerCounts` |
| `list_devices` | `rows`, `totalCount`, `pageSize`, `nextCursor`. Row (device): `entityId`, `sourceObjectId`, `hostname`, `entityKind`, `osFamily`, `osVersion`, `health`, `rawHealth`, `virtualization`, `monitoringMode`, `memoryTotalMiB`, `processorCount`, `discoveredAtUtc`, `groupIds`, `sourceRefs`, `osCaption` |
| `get_device_details` | `device` (as above), `volumes` (`entityId`, `parentEntityId`, `deviceId`, `fileSystem`, `sizeMiB`, `sourceRefs`), `managementGroup` |
| `get_utilization` | `metric`, `window`, `statistic`, `windowStartUtc`, `windowEndUtc`, `previousWindowStartUtc`, `consideredCount`, `availableCount`, `missingCount`, `matchedCount`, `returnedCount`, `missingEntityIds`, `top`, `sortOrder`, `rows`. Row: `entityId`, `parentEntityId`, `entityKind`, `deviceId`, `metric`, `value`, `previousValue`, `changePercentagePoints`, `coverage` (`firstSampleUtc`, `lastSampleUtc`, `sampleCount`, `ageSeconds`, `fullWindowCovered` — always false, `optimizedSampleCount`, `nonOptimizedSampleCount`), `previousCoverage`, `sourceMetric`, `previousSourceMetric` |
| `get_capacity_summary` | `resource`, `consideredRowCount`, `matchingRowCount`, `observationsAsOfUtc`, `observationCutoffUtc`, `usageStatus` (`ok`, `partial`, `unavailable`), `usageObservationAvailableCount`, `usageObservationMissingCount`, `matchingVolumeCount`, `distinctMachineCount`, `consideredMachineCount`, `unknownTotalCount`, `unknownFreePercentCount`, `unknownUsedOrFreeCount`, `knownTotalMiB`, `knownTotalRowCount`, `comparableCapacity` (`rowCount`, `totalMiB`, `usedMiB`, `freeMiB`), `page` (`rows`, `totalCount`, `pageSize`, `nextCursor`). Row: `entityId`, `parentEntityId`, `entityKind`, `deviceId`, `totalMiB`, `usedMiB`, `freeMiB`, `freePercent`, `observedAtUtc`, `sourceMetric`, `optimized` |
| `list_os_support_assessments` | See section 4.4 |

If no entity has an observation for the requested metric and window, `get_utilization` returns `SOURCE_UNAVAILABLE` with message `NO_USABLE_OBSERVATIONS_FOR_REQUESTED_METRIC_WINDOW`.

### 4.2 Generic read-model tools (`ReadModel.Enabled`)

These return the stored records of the latest committed dataset. `objectId` values must be canonical GUID strings. Ordering is by record id.

| Tool | Parameters | Filter semantics |
|---|---|---|
| `list_monitored_objects` | `pageSize` (default 20, 1–50), `cursor?` | All stored objects |
| `get_monitored_object` | `objectId` (**required**) | Object with that id |
| `list_object_relationships` | `objectId` (**required**), `pageSize`, `cursor?` | Relationships whose source or target is `objectId` |
| `list_monitored_alerts` | `objectId?`, `pageSize`, `cursor?` | Alerts for the object, or all alerts when omitted |
| `list_metric_observations` | `objectId` (**required**), `pageSize`, `cursor?` | Metric observations whose parent is `objectId` |

Page response: `{datasetId, status, manifest, totalCount, pageSize, nextCursor, rows[]}`. `status` is `ok` only when the inventory, alert and metric domains are all complete **and** fresh, otherwise `partial`. `manifest` is `{managementGroupId, scopeKind, groupId, membershipVerified, expectedObjectCount, inventory, alerts, metrics, alertHistoryStartUtc}` where each domain is `{asOfUtc, complete, reason}`. Rows are the stored records:

| Record | Fields |
|---|---|
| Object | `id`, `className`, `baseClasses`, `displayName`, `health`, `observedAtUtc`, `properties` (only properties approved by `AllowedPropertiesByClass`) |
| Relationship | `id`, `className`, `sourceId`, `targetId` |
| Alert | `id`, `objectId`, `name`, `description` (null unless `IncludeAlertDescriptions`), `severity`, `resolutionState`, `observedAtUtc` |
| Metric | `id`, `objectId`, `metric`, `unit`, `observedAtUtc`, `value`, `ruleId`, `optimized` |

Errors from these tools are returned as `{error, reason}` with `isError=true` (for example `INVALID_ARGUMENT` for a bad page size, `SOURCE_UNAVAILABLE` with reason `READ_MODEL_EXPIRED`). A malformed `objectId` currently surfaces as error `INGESTION_REJECTED` with reason `ID_INVALID` (a reuse of the shared validator).

### 4.3 `verify_object_current_state` (targeted live check)

| Parameter | Type | Rules |
|---|---|---|
| `objectId` | string, **required** | Canonical GUID |
| `scopeId` | string, optional | `scom-lab` |

Asks the worker's live provider to re-read one object (health plus bounded alerts) from SCOM, now. It is disabled by default and needs a dedicated, hash-pinned provider profile; see [targeted live verification](targeted-live-verification.md). Response on success: `{mode:"targetedLive", requestId, retainedDatasetId, receivedAtUtc, observation, limitations[]}`. `observation` carries `requestId`, `objectId`, `source{managementGroupId, scopeKind, groupId}`, `outcome` (`ok`, `outOfScope`, `unavailable`), `scopeVerified`, `scopeCheckedAtUtc`, `checkedAtUtc`, `health`, `healthObservedAtUtc`, `alertsComplete`, `alerts[{id, objectId, name, severity, resolutionState, observedAtUtc}]`, `sourceRequests`. Limitation codes: `MONITORING_HEALTH_IS_NOT_REACHABILITY_OR_ROOT_CAUSE`, `SOURCE_TEXT_IS_UNTRUSTED_DATA_NOT_INSTRUCTIONS`, `ALERT_COMPLETENESS_ASSERTED_BY_VERIFIED_SOURCE_PROFILE`, `ALERTS_PARTIAL_EMPTY_DOES_NOT_MEAN_NO_ALERTS`.

Admission limits (`TargetedLive` settings; defaults in [configuration-reference.md](configuration-reference.md)): per-caller requests per minute (default 3), global requests per minute (default 10), queue capacity (default 8), request lifetime (default 15 s), execution time (default 5 s). At most 2 callers wait on one entry; the provider is handed one claim at a time with a minimum 2-second gap; a request carries at most 3 source requests; a result is capped at 32768 bytes.

### 4.4 OS support assessment

`list_os_support_assessments` classifies Windows Server computers against a curated, offline lifecycle catalog (no per-question web or source queries). Catalog version `windows-server-fixed-2026-11-02.1`, verified `2026-11-02`, covering Windows Server 2012 R2, 2016, 2019, 2022 and 2025 with Datacenter, Standard and Essentials editions (2012 R2 also Foundation). A caption matches only as an exact `[Microsoft ]<Product> <Edition>` string. Dates are inclusive calendar dates in the Pacific time zone.

| `assessment` | Meaning |
|---|---|
| `mainstream`, `extended`, `ended` | Current lifecycle phase per catalog |
| `approaching` | Extended support ends within `Lifecycle.ApproachingSupportDays` |
| `unsupported` | Computer is not Windows (`osFamily` not `windows`) |
| `unknown` | Caption missing, not exactly mapped, product not yet released at the evaluation date, or the catalog is stale (older than `Lifecycle.MaxCatalogAgeDays`) |

Data fields: `asOfDate`, `dateTimeZone`, `evaluatedAtUtc`, `catalogVersion`, `catalogVerifiedOn`, `catalogAgeDays`, `maxCatalogAgeDays`, `catalogValidThrough`, `catalogState` (`fresh`, `notYetVerified`, `stale`), `approachingSupportDays`, `scopeComputerCount`, `expectedComputerCount`, `missingComputerCount`, `classifiedComputerCount`, `unknownComputerCount`, `unsupportedComputerCount`, `assessmentComplete`, `countsByAssessment`, `approachingExtendedEndCount`, `matchedComputerCount`, `rows`, `totalCount`, `pageSize`, `nextCursor`. Row: `entityId`, `hostname`, `observedOsCaption`, `assessment`, `reason`, `product`, `edition`, `lifecyclePolicy`, `mainstreamEnd`, `extendedEnd`, `daysToExtendedEnd`, `approachingExtendedEnd`, `optionalEsuEnd`, `esuEntitlement` (always `notAssessed`), `evidenceUrls`. Row `reason` values: `OS_FAMILY_OUTSIDE_WINDOWS_SERVER_PROFILE`, `OS_CAPTION_MISSING`, `PRODUCT_EDITION_OR_CHANNEL_NOT_EXACTLY_MAPPED`, `CATALOG_STALE_OR_NOT_YET_VERIFIED`, `PRODUCT_NOT_RELEASED_AS_OF_DATE`, `EXACT_PRODUCT_EDITION_CAPTION`. Product lifecycle is not patch compliance and does not prove entitlement to support.

### 4.5 Limitation codes

Returned in the envelope `limitations` array (not errors). Grouped:

| Area | Codes |
|---|---|
| Scope | `ONLY_APPROVED_WINDOWS_COMPUTERS_NOT_AN_ENTERPRISE_TOTAL`, `ONLY_APPROVED_WINDOWS_VIEW_NOT_ALL_MONITORED_OBJECTS`, `INDEXED_WINDOWS_VIEW_NOT_ALL_MONITORED_OBJECTS`, `NETWORK_DEVICES_AND_LICENSE_ENTITLEMENTS_UNSUPPORTED`, `REQUESTED_ENTITY_CLASS_OR_OS_IS_NOT_SUPPORTED_BY_THIS_SCOPE`, `EXPECTED_DEVICE_COVERAGE_INCOMPLETE`, `SCOM_OFFLINE_HOSTING_PREVIEW_SOURCE_UNVERIFIED` |
| Freshness | `SNAPSHOT_STALE`, `INDEXED_DOMAIN_STALE`, `LATEST_METRIC_OBSERVATION_IS_STALE`, `CAPACITY_OBSERVATIONS_STALE_AT_REQUEST_TIME_VALUES_ARE_AS_OF_SNAPSHOT`, `INVENTORY_DOMAIN_INCOMPLETE`, `ALERT_DOMAIN_INCOMPLETE`, `METRIC_DOMAIN_INCOMPLETE` |
| Metrics | `NO_HISTORY_BEFORE_RETAINED_SCOM_OBSERVATIONS_IS_ASSUMED`, `MEAN_IS_ARITHMETIC_MEAN_OF_RETAINED_SCOM_OBSERVATIONS_NOT_TIME_WEIGHTED`, `RETAINED_OBSERVATIONS_DO_NOT_ESTABLISH_CONTINUOUS_FULL_WINDOW_COVERAGE`, `PARTIAL_WINDOW_OR_MISSING_ENTITY_OBSERVATIONS`, `PREVIOUS_WINDOW_UNAVAILABLE_FOR_SOME_ENTITIES`, `MEMORY_USED_PERCENT_DERIVED_FROM_AVAILABLE_MIB_WHERE_NATIVE_PERCENT_ABSENT`, `CURRENT_AND_PREVIOUS_MEMORY_WINDOWS_USE_DIFFERENT_SOURCE_METRICS`, `NO_METRIC_OBSERVATIONS_AVAILABLE`, `CAPACITY_USAGE_UNKNOWN_WHERE_TOTAL_OR_RECENT_SAMPLE_MISSING`, `DUPLICATES_RESOLVED_BY_KNOWN_FIELD_COUNT_THEN_ORDINAL_CANONICAL_JSON` |
| Lifecycle | `PRODUCT_LIFECYCLE_NOT_PATCH_COMPLIANCE_OR_GUARANTEED_SUPPORT_ELIGIBILITY`, `SUPPORT_END_DATES_ARE_INCLUSIVE_PACIFIC_CALENDAR_DATES`, `OPTIONAL_ESU_IS_NOT_EXTENDED_SUPPORT_OR_PROOF_OF_ENTITLEMENT`, `CURATED_CATALOG_NO_PER_QUESTION_SOURCE_OR_WEB_QUERIES`, `LIFECYCLE_CATALOG_STALE_OR_NOT_YET_VERIFIED`, `INCOMPLETE_OS_LIFECYCLE_ASSESSMENT` |
| Health | `MONITORING_HEALTH_DOES_NOT_PROVE_CONNECTIVITY` |

## 5. Pagination and cursors

- `pageSize` is 1–50, default 20 for every paged tool.
- A response includes `nextCursor` when more rows remain; pass it unchanged as `cursor` with identical filters and `pageSize`.
- Cursors are opaque, HMAC-SHA256 signed with a random 32-byte key generated at process start. **They do not survive an application-pool recycle or restart**; restart paging after one.
- A cursor is bound to the caller (`oid`/UPN), scope, dataset id and fingerprint, the filter hash, `pageSize` and offset. Maximum cursor length 4096 characters.
- Lifetime: `Source.CursorLifetimeSeconds` (default 300, valid 30–900); the read-model cursor uses the same setting.
- A malformed, expired, foreign or mismatched cursor returns `INVALID_CURSOR`. If a new dataset has been committed since the cursor was issued, the tool returns `CURSOR_DATASET_CHANGED` (restart paging from the first page).

## 6. Freshness, stale and partial semantics

| Setting | Default | Effect |
|---|---|---|
| `ReadModel.FreshMinutes` / `Source.FreshMinutes` | 15 | Older than this: `freshness.state` is `stale`, envelope `status` is `partial` |
| `ReadModel.UnavailableMinutes` / `Source.UnavailableMinutes` | 60 (must exceed `FreshMinutes`, maximum 1440) | Older than this: the request fails with `SOURCE_UNAVAILABLE` / `READ_MODEL_EXPIRED` |

- The age is computed from the **oldest** of the inventory, alert and metric domain `asOfUtc` values in the manifest.
- When `WindowsToolsEnabled` is true the `Source` and `ReadModel` freshness values must be equal.
- A new ingestion session makes data unavailable (`READ_MODEL_LATEST_COLLECTION_UNAVAILABLE`) until its commit; the previous committed run is not served meanwhile. The last two committed runs are retained.
- `partial` also results from an incomplete domain (`complete=false` with a `reason`), incomplete expected-device coverage, or tool-added limitations. Consumers should read `status`, `freshness`, `coverage` and `limitations` before drawing conclusions.
- Process liveness (`/healthz`) and the worker task state are not freshness evidence; use `get_source_status`.

## 7. Error codes returned to MCP callers

| Code | Delivered as | Meaning |
|---|---|---|
| `INVALID_ARGUMENT` | JSON-RPC error `-32602` (windows tools and `verify_object_current_state`); `{error,reason}` with `isError` (generic read-model tools) | A parameter failed validation |
| `FORBIDDEN` | Envelope `errorCode`; HTTP 403 for the pre-check | Caller or scope not authorized |
| `NOT_FOUND` | Envelope `errorCode` / `{error}` | Entity or object not in the dataset |
| `INVALID_CURSOR` | Envelope `errorCode` | Cursor invalid, expired or mismatched |
| `CURSOR_DATASET_CHANGED` | Envelope `errorCode` | Dataset changed since the cursor was issued |
| `SOURCE_UNAVAILABLE` | Envelope `errorCode` with a `message` reason | No usable data; reasons below |
| `REQUEST_FAILED` | HTTP 500 body; read-model tool error | Unexpected server error (see log) |
| Targeted-live codes | `{error:<code>}` with `isError` | See table below |

`SOURCE_UNAVAILABLE` reasons (message / reason):

- **Read model and indexed Windows:** `READ_MODEL_DISABLED`, `READ_MODEL_LATEST_COLLECTION_UNAVAILABLE`, `READ_MODEL_EXPIRED`, `INDEXED_WINDOWS_DISABLED`, `WINDOWS_PROJECTION_MISSING`, `REQUEST_COULD_NOT_BE_COMPLETED`, `NO_USABLE_OBSERVATIONS_FOR_REQUESTED_METRIC_WINDOW`.
- **Legacy snapshot reader (only if `Source.SnapshotEnabled`):** `LEGACY_SNAPSHOT_DISABLED`, `LATEST_COLLECTION_FAILED_OR_MISSING`, `COLLECTION_CHANGED_DURING_READ`, `SNAPSHOT_OR_STATUS_INVALID_OR_UNREADABLE`, `SOURCE_SIZE_LIMIT`, `INVALID_SNAPSHOT_JSON`, `DUPLICATE_JSON_PROPERTY`, `SNAPSHOT_SCHEMA_SCOPE_OR_STATUS_INVALID`, `COLLECTOR_STATUS_MISMATCH`, `SOURCE_EXPIRED_OR_INVALID_TIME`, `SOURCE_COLLECTIONS_INVALID`, `DEVICE_INVALID_OR_OUTSIDE_SCOPE`, `AMBIGUOUS_DEVICE_IDENTITY`, `AMBIGUOUS_OR_UNEXPECTED_DEVICE_COVERAGE`, `VOLUME_INVALID_OR_OUTSIDE_SCOPE`, `AMBIGUOUS_VOLUME_PARENT`, `SAMPLE_INVALID_OR_OUTSIDE_SCOPE`, `MEMORY_SAMPLE_EXCEEDS_TOTAL`, `CONFLICTING_SAMPLE_DUPLICATES`.

Targeted-live error codes:

| Code | Meaning |
|---|---|
| `LIVE_NOT_ENABLED` | `TargetedLive.Enabled` is false |
| `LIVE_PROVIDER_UNAVAILABLE` | No provider claimed recently (provider is "ready" for 6 s after a claim) |
| `LIVE_RATE_LIMITED` | Per-caller or global per-minute limit reached |
| `LIVE_QUEUE_FULL` | Queue capacity reached or more than 2 waiters on one entry |
| `LIVE_TIMEOUT` | No result within the request lifetime |
| `LIVE_PROVIDER_UNVERIFIED`, `LIVE_PUBLISHER_FORBIDDEN` | Provider policy hash or publisher identity does not match configuration |
| `LIVE_RESULT_INVALID`, `LIVE_REQUEST_EXPIRED_OR_UNKNOWN`, `LIVE_RESULT_BUDGET`, `LIVE_REPLAY_CONFLICT` | Result rejected by the rendezvous |
| `LIVE_DATASET_CHANGED`, `LIVE_SOURCE_UNAVAILABLE` | Dataset changed during the check or source unavailable |
| `LIVE_PROTOCOL_INVALID` | Provider protocol violation (HTTP 400 on `/ingestion/live/*`) |
| `NOT_FOUND`, `FORBIDDEN` | Object not in the retained dataset, or caller not authorized |

For `/ingestion/live/*`, additional HTTP statuses: 413 (body over 1024 bytes for claim, 32768 for result), 408 (body not received within 5 s), 409 (`{error:<code>}`).

## 8. Size and time limits (receiver)

| Limit | Setting | Default | Valid range |
|---|---|---|---|
| Ingestion batch body | `ReadModel.MaxBatchBytes` | 1048576 | 1024–1048576 |
| Dataset total | `ReadModel.MaxDatasetBytes` | 16777216 | ≥ `MaxBatchBytes`, ≤ 33554432 |
| Records per dataset | `ReadModel.MaxRecords` | 100000 | 1–1000000 |
| SQLite database | `ReadModel.MaxDatabaseBytes` | 134217728 | ≥ 4 × `MaxDatasetBytes`, ≤ 1073741824 |
| Ingestion body read deadline | `ReadModel.RequestBodyTimeoutSeconds` | 30 | 1–120 (HTTP 408 on expiry) |
| Ingestion session lifetime | `ReadModel.SessionMinutes` | 30 | 1–120 |
| Batches per session | fixed | sequence 0–9999 | — |
| Property value length | fixed | 2000 characters | — |
| `/mcp` request body | fixed | 32768 bytes | — |
| Page size | fixed | 20 (default) | 1–50 |
| Used-session ledger | fixed | refuses at 1000000 used sessions (`SESSION_LEDGER_LIMIT`) | — |

## 9. Ingestion contract (publisher to receiver)

The shipped worker implements this protocol; a different publisher must implement it exactly. Authentication: `Authorization: Bearer <app-only token>` (section 2.3). Request bodies use `Content-Type: application/json`; JSON is parsed with maximum depth 32, duplicate property names rejected, unmapped members disallowed.

1. **Begin** — `POST /ingestion/sessions/{sessionId}` where `sessionId` is a canonical GUID chosen by the publisher. Returns `{"sessionId":"..."}`. A repeated Begin for a committed session, or an unexpired staging session of the same publisher, is an idempotent replay. Beginning a new session deletes older staging/failed runs and marks current data unavailable until commit. Only one unexpired staging run may exist.
2. **Append** — `POST .../batches/{sequence}` with a batch body `{"objects":[],"relationships":[],"alerts":[],"metrics":[]}` (all four arrays required) and optionally `windowsInventory:{devices:[],volumes:[]}`. `sequence` starts at 0 and must be gapless. Resending the same sequence with an identical digest is an idempotent replay; a different digest is `BATCH_REPLAY_CONFLICT`. Returns `{"accepted":true}`.
3. **Commit** — `POST .../commit` with the manifest `{managementGroupId, scopeKind, groupId, membershipVerified, expectedObjectIds[], inventory{asOfUtc,complete,reason}, alerts{...}, metrics{...}, alertHistoryStartUtc, expectedComputerIds?}` (at most `MaxBatchBytes`). Returns `{"datasetId":"..."}`. A replay with the same digest returns the same id; a different digest is `COMMIT_REPLAY_CONFLICT`.
4. **Fail** — `POST .../fail` marks the run failed, makes data unavailable and deletes its batches. Returns `{"unavailable":true}`. The shipped worker never calls it after a lost commit response; it reconciles instead.

All rejections are HTTP 409 with `{"error":"INGESTION_REJECTED","reason":"<code>"}`; oversize bodies return 413, slow bodies 408, a second concurrent request 429. Reason codes:

| Phase | Reasons |
|---|---|
| Session | `SESSION_INVALID`, `SESSION_INVALID_OR_EXPIRED`, `SESSION_EXPIRED_EMPTY_RESERVATION`, `SESSION_REPLAY_OR_EXPIRED`, `SESSION_ALREADY_CONSUMED`, `SESSION_LEDGER_LIMIT`, `COLLECTION_ALREADY_IN_PROGRESS`, `PUBLISHER_FORBIDDEN`, `OFFLINE_HOSTING_PREVIEW_ALREADY_CAPTURED` |
| Batch | `BATCH_SEQUENCE_INVALID`, `BATCH_LIMIT`, `BATCH_REPLAY_CONFLICT`, `BATCH_ARRAYS_INVALID`, `JSON_INVALID`, `WINDOWS_BATCH_INVALID_OR_DISABLED`, `DATASET_SIZE_LIMIT`, `RECORD_COUNT_LIMIT` |
| Commit | `MANIFEST_SIZE_LIMIT`, `COMMIT_REPLAY_CONFLICT`, `SOURCE_TIME_REGRESSION`, `SCOPE_UNVERIFIED_OR_MISMATCH`, `MEMBERSHIP_INVALID`, `MEMBERSHIP_DUPLICATE`, `OBJECT_MEMBERSHIP_INVALID`, `COVERAGE_MISSING`, `COVERAGE_INVALID`, `TIME_INVALID`, `ALERT_HISTORY_INVALID` |
| Records | `OBJECT_INVALID`, `PROPERTY_NOT_APPROVED_OR_OVERSIZED`, `RELATIONSHIP_OUTSIDE_SCOPE`, `ALERT_INVALID`, `METRIC_INVALID_OR_DUPLICATE`, `RECORD_DUPLICATE`, `TEXT_INVALID`, `ID_INVALID` |
| Windows projection | `WINDOWS_PROJECTION_DISABLED`, `EXPECTED_COMPUTER_MEMBERSHIP_REQUIRED`, `EXPECTED_COMPUTER_MEMBERSHIP_INVALID`, `WINDOWS_DEVICE_OUTSIDE_SCOPE_OR_HEALTH_MISMATCH`, `WINDOWS_VOLUME_OUTSIDE_SCOPE`, `WINDOWS_COMPLETE_INVENTORY_MISMATCH`, `WINDOWS_METRIC_HOSTING_INVALID`, `WINDOWS_METRIC_HOSTING_AMBIGUOUS`, `WINDOWS_OS_CAPTION_PROPERTY_NOT_APPROVED`, `WINDOWS_OS_CAPTION_SOURCE_MISMATCH`, `WINDOWS_METRIC_UNIT_INVALID`, `WINDOWS_DOMAINS_EXPIRED` |
| Offline hosting preview | `OFFLINE_HOSTING_PREVIEW_MUST_REPORT_UNVERIFIED_COVERAGE`, `OFFLINE_HOSTING_PREVIEW_POLICY_REQUIRED`, `OFFLINE_HOSTING_PREVIEW_MONITORING_ROLE_UNVERIFIED` |

Validation notes: properties must be approved for the object's exact class; a metric's unit must match the approved unit, percent values are 0–100, and metric timestamps must fall within 14 days before the metrics `asOfUtc`. Only the reasons `SESSION_EXPIRED_EMPTY_RESERVATION`, `SESSION_REPLAY_OR_EXPIRED`, `COLLECTION_ALREADY_IN_PROGRESS`, `SESSION_ALREADY_CONSUMED`, `SESSION_LEDGER_LIMIT` and `SESSION_INVALID` are written to the receiver log; other reasons are logged as `IngestionRejected` without the reason. The HTTP response always carries the reason.

Retry guidance implemented by the worker (a custom publisher should do likewise): retry HTTP 408, 429, 502, 503, 504 and transport timeouts up to `publisher.maxAttempts` (default 3) with backoff `min(8000, 250·2^(attempt−1))` ms plus 0–100 ms jitter; a `Retry-After` greater than 30 s defers the whole attempt.

The receiver's SQLite file is pinned to the configured scope and approved property/metric policy; see [configuration-reference.md](configuration-reference.md#4-policy-changes) for what a policy change requires.
