# Configuration reference

This document lists every setting of the SCOM MCP receiver (`appsettings.json`) and of the collection worker (`worker.json`), with type, default, validation rule and purpose, and marks which settings are pinned policy (changing them requires a backup and a new database or checkpoint). Defaults and rules come from the C# options classes, `SetupConfigurationValidation.cs` and the collector scripts in this package; where a bound could not be established it says "Not determined from source". This is an unsigned community release; nothing here is a certification. Related documents: [architecture.md](architecture.md), [install-cli.md](install-cli.md), [entra-setup.md](entra-setup.md), [service-accounts.md](service-accounts.md), [mcp-server-spec.md](mcp-server-spec.md), [operations.md](operations.md), [sizing-and-scope.md](sizing-and-scope.md), [troubleshooting.md](troubleshooting.md), [security.md](security.md), [limitations.md](limitations.md).

Conventions: "Default" is the value used when the key is absent (code default); where the shipped `appsettings.json` differs it is noted. "Policy?" is **Yes** when the value is part of the database pin or the worker checkpoint policy/settings hash (see [Policy changes](#4-policy-changes)). Placeholders: `<TENANT_ID>`, `<API_APP_ID>`, `<PUBLISHER_APP_ID>`, `<MG_ID>`, `<GROUP_ID>`, `<MGMT_SERVER>`, `<FQDN>`, `<CERT_THUMBPRINT>`.

Startup validation fails closed: an invalid receiver setting prevents the host from starting; an invalid worker setting stops the worker with `WORKER_CONFIGURATION_OR_EXECUTION_FAILED` or a specific code (see [troubleshooting.md](troubleshooting.md)). Run `dotnet ScomMcp.dll --validate-setup <server-dir> <worker.json>` before enabling the receiver or worker; see [What `--validate-setup` enforces](#5-what---validate-setup-enforces).

## 1. Receiver (`appsettings.json`)

Top-level sections: `OperationalLogging`, `Auth`, `Source`, `Lifecycle`, `TargetedLive`, `ReadModel`, `Logging`, `AllowedHosts`. Example files: `ScomMcp\appsettings.ReadModel.example.json` (receiver) and `ScomMcp\appsettings.Worker.example.json` (worker). At least one of `Source.SnapshotEnabled` and `ReadModel.Enabled` must be true; the supported partner configuration is `ReadModel.Enabled=true`, `Source.SnapshotEnabled=false`.

### 1.1 `OperationalLogging`

Bounded, allowlisted JSON-lines log (no free text, exceptions or scopes). Same options class is used by the worker (section 2.1).

| Setting | Type | Default | Valid range / rule | Purpose | Policy? |
|---|---|---|---|---|---|
| `Directory` | string | `""` (code); shipped `appsettings.json` has `C:\ProgramData\SCOMMCP\Logs` | Absolute local path `X:\...`; no `/`, `..`, trailing space/dot or wildcards; full path ≤ 240 chars; must already exist on a fixed NTFS drive with no reparse points in ancestors | Where `server.log` is written. Runtime never creates the directory. | No |
| `MaxFileBytes` | int | 4194304 | 65536–16777216 (setup requires exactly 1048576) | Size at which the active file rotates | No |
| `RetainedFiles` | int | 3 | 1–16, counting the active file (setup requires exactly 5) | Rotated generations kept | No |

An unwritable sink stops startup (`OPERATIONAL_LOG_IO_FAILED`).

### 1.2 `Auth`

| Setting | Type | Default | Valid range / rule | Purpose | Policy? |
|---|---|---|---|---|---|
| `TenantId` | string (GUID, `D` format) | `""` (must be set) | Non-empty GUID | Entra tenant `<TENANT_ID>`; token `tid` and issuer | No |
| `Audiences` | string[] | `[]` (must be set) | Non-empty; entries without wildcards or whitespace; token `aud` must equal an entry exactly | Accepted audiences, e.g. `api://<API_APP_ID>` | No |
| `RequiredScope` | string | `""` (must be set) | No whitespace or `*`; setup requires `Scom.Read` | Delegated scope required in `scp` | No |
| `Callers` | object[] | `[]` (must be set) | Non-empty. Each entry has exactly one of `ObjectId` (GUID) or `UserPrincipalName` (contains `@`, no `*` or whitespace), and `Scopes` non-empty, each equal to `scom-lab`. With `ReadModel.Enabled`, `ObjectId` is mandatory | Allow-list of callers (Entra user or app `oid`) | No |

### 1.3 `Source` (legacy snapshot reader and shared freshness)

| Setting | Type | Default | Valid range / rule | Purpose | Policy? |
|---|---|---|---|---|---|
| `SnapshotEnabled` | bool | true (setup requires false) | — | Enable the legacy file-snapshot reader | No |
| `Directory` | string | `""` | Fully qualified when `SnapshotEnabled` | Snapshot directory | No |
| `AllowedHostnames` | string[] | `[]` | Exactly 2 distinct hostnames when `SnapshotEnabled` | Hosts a snapshot may describe | No |
| `MaxSnapshotBytes` | int | 8388608 (shipped `appsettings.json`: 10000000) | 1024–67108864 | Snapshot size cap | No |
| `FreshMinutes` | int | 15 | ≥ 1; must equal `ReadModel.FreshMinutes` when `WindowsToolsEnabled` | Age beyond which data is `stale`/`partial` | No |
| `UnavailableMinutes` | int | 60 | > `FreshMinutes`, ≤ 1440; must equal `ReadModel.UnavailableMinutes` when `WindowsToolsEnabled` | Age beyond which data is unavailable | No |
| `CursorLifetimeSeconds` | int | 300 | 30–900 | Pagination cursor lifetime (also used by read-model cursors) | No |

### 1.4 `Lifecycle`

| Setting | Type | Default | Valid range | Purpose | Policy? |
|---|---|---|---|---|---|
| `ApproachingSupportDays` | int | 365 | 1–3650 | Window for the `approaching` OS assessment | No |
| `MaxCatalogAgeDays` | int | 90 | 1–90 | Catalog age beyond which assessments become `unknown` | No |

See [os-lifecycle-assessment.md](os-lifecycle-assessment.md).

### 1.5 `TargetedLive`

Disabled by default; setup validation requires it disabled. See [targeted-live-verification.md](targeted-live-verification.md).

| Setting | Type | Default | Valid range / rule | Purpose | Policy? |
|---|---|---|---|---|---|
| `Enabled` | bool | false | Requires `ReadModel.Enabled`; cannot combine with `ReadModel.OfflineHostingPreview` | Map `/ingestion/live/*` and enable `verify_object_current_state` | No |
| `PublisherObjectId` | string (GUID) | `""` | GUID that is also in `ReadModel.PublisherObjectIds` | Provider identity allowed to claim/answer | No |
| `ProviderPolicySha256` | string | `""` | 64 hex characters | Pinned hash of the provider profile (must match worker `liveChecks`) | No |
| `RequestSeconds` | int | 15 | 5–30 | Lifetime of a request | No |
| `ExecutionSeconds` | int | 5 | 1–10 and ≤ `RequestSeconds` − 2 | Provider execution budget | No |
| `MaxAlerts` | int | 20 | 1–100 | Alerts returned per check | No |
| `QueueCapacity` | int | 8 | 1–16 | Pending checks | No |
| `RequestsPerCallerMinute` | int | 3 | 1–10 | Per-caller admission limit | No |
| `RequestsPerMinute` | int | 10 | 1–30 | Global admission limit | No |

### 1.6 `ReadModel`

| Setting | Type | Default | Valid range / rule | Purpose | Policy? |
|---|---|---|---|---|---|
| `Enabled` | bool | false | Setup requires true | Enable SQLite read model, `/ingestion/*` and the generic read-model tools | No |
| `DatabasePath` | string | `""` | Fully qualified; not UNC; not a mapped network drive; must differ from the worker `checkpointPath`. Rollback-journal mode only (WAL refused) | SQLite file | No (but the file is pinned) |
| `ManagementGroupId` | string (GUID) | `""` | Non-empty GUID, `D` format; must equal worker `profile.configuration.managementGroupId` | `<MG_ID>` | **Yes** |
| `ScopeKind` | string | `group` | `group` or `managementGroup`; must equal worker value | Scope type | **Yes** |
| `GroupId` | string (GUID) or null | null | Required GUID when `group`; must be null for `managementGroup`. Empty string is normalized to null | `<GROUP_ID>` | **Yes** |
| `PublisherObjectIds` | string[] (GUIDs) | `[]` | Non-empty, de-duplicated GUID list | Service-principal object ids allowed to publish (`<PUBLISHER_APP_ID>`'s enterprise-app object id) | No |
| `IngestionRole` | string | `Scom.Ingest` | No whitespace; setup requires `Scom.Ingest` | App role required in the publisher token | No |
| `AllowedPropertiesByClass` | map class → string[] | `{}` | ≤ 256 classes; class name ≤ 256 chars; ≤ 64 properties per class; property name ≤ 128 chars. Must be set-equal per class to worker `allowedPropertiesByClass` | Approved object properties | **Yes** |
| `AllowedMetricsByClass` | map class → (metric → unit) | `{}` | ≤ 256 classes; ≤ 64 metrics per class; metric name ≤ 128; unit ≤ 64. Setup requires empty | Approved metrics (not used by the supported Windows profile) | **Yes** |
| `IncludeAlertDescriptions` | bool | false | Must equal worker `includeAlertDescriptions` | Store alert descriptions | **Yes** |
| `MaxBatchBytes` | int | 1048576 | 1024–1048576 | Max ingestion batch or manifest body | No |
| `MaxDatasetBytes` | int | 16777216 | ≥ `MaxBatchBytes`, ≤ 33554432 | Max dataset total | No |
| `MaxRecords` | int | 100000 | 1–1000000 | Max records per dataset | No |
| `MaxDatabaseBytes` | int | 134217728 | ≥ 4 × `MaxDatasetBytes`, ≤ 1073741824 | SQLite budget (`max_page_count`) | No |
| `FreshMinutes` | int | 15 | ≥ 1 | Fresh threshold | No |
| `UnavailableMinutes` | int | 60 | > `FreshMinutes`, ≤ 1440 | `READ_MODEL_EXPIRED` threshold | No |
| `SessionMinutes` | int | 30 | 1–120 | Ingestion session lifetime | No |
| `RequestBodyTimeoutSeconds` | int | 30 | 1–120 | Ingestion body read deadline (HTTP 408) | No |
| `WindowsToolsEnabled` | bool | false | Requires `Enabled` and legacy snapshots disabled; `Source`/`ReadModel` freshness must be equal; required when `ScopeKind=managementGroup` (setup) | Serve the Windows tools from the indexed read model | **Yes** |
| `WindowsMetricHosting` | object or null | null | `{ComputerClass, OperatingSystemClass, RelationshipClass}`: each non-empty, ≤ 256 chars, no whitespace, no `*?[]`; `ComputerClass` ≠ `OperatingSystemClass`; requires `WindowsToolsEnabled`. Must match worker `windowsMapping` (see below) | Classes/relationship used to host metrics on computers | **Yes** |
| `WindowsOsCaptionProperty` | string or null | null | Exact property name ≤ 128 chars, no `*?[]`; requires `WindowsMetricHosting`; `""` is treated as null. Must equal worker `windowsMapping.osCaptionProperty` | OS caption source for lifecycle assessment | **Yes** |
| `OfflineHostingPreview` | bool | false | Requires `ScopeKind=managementGroup`, `WindowsToolsEnabled`, `IncludeAlertDescriptions=false`, empty `AllowedMetricsByClass`; must equal worker `hostingLabPreview`. Not combinable with `TargetedLive` | Lab-only preview of hosting evidence; not acceptance evidence | **Yes** |

The database also pins `ApplicationId` 0x53434F52 and schema `user_version` 1. Startup refuses orphaned `-journal`/`-wal`/`-shm` files, an unsupported schema and an existing database larger than `MaxDatabaseBytes`.

### 1.7 `Logging` and `AllowedHosts`

| Setting | Type | Default | Purpose |
|---|---|---|---|
| `Logging.LogLevel.Default` | string | `Warning` (shipped) | Standard ASP.NET Core log levels. The operational log is an allowlisted projection and records Information and above only. |
| `Logging.LogLevel.Microsoft.AspNetCore` | string | `Warning` (shipped) | As above |
| `Logging.LogLevel["ScomMcp.ReadModelStore"]` | string | Not set in `appsettings.json`; `Information` in the read-model example | Category-level override |
| `AllowedHosts` | string | `localhost` (shipped) | ASP.NET Core host filtering. Set to the published host name `<FQDN>`; `--validate-setup` requires the effective value to equal `appsettings.json`. |

Environment variables and `appsettings.Production.json` must not override the `Auth`, `Source`, `ReadModel`, `Lifecycle`, `TargetedLive`, `AllowedHosts` or `OperationalLogging` values for a validated deployment; `--validate-setup` compares the effective configuration with `appsettings.json`.

## 2. Worker (`worker.json`)

The file is read with camelCase, case-insensitive JSON; maximum 1 MB; BOM tolerated; duplicate properties rejected; the path must be absolute. Top-level keys: `operationalLogging`, `liveChecks`, `checkpointPath`, `maxCheckpointBytes`, `profile`, `identity`, `publisher`, `reconciliation`, `schedule`.

### 2.1 `operationalLogging`

Same rules as section 1.1 (code defaults: 4 MiB × 3). Writes `worker.log`. Setup additionally requires `maxFileBytes=1048576`, `retainedFiles=5`, a non-root directory, receiver and worker directories that are separate and not nested, and the worker directory equal to the profile logging directory in `profile.configuration.operationalLogging` (see 2.4).

### 2.2 Top-level and checkpoint

| Setting | Type | Default | Valid range / rule | Purpose | Policy? |
|---|---|---|---|---|---|
| `checkpointPath` | string | none | Absolute; local non-network drive; no reparse points; provisioned directory; differs from `ReadModel.DatabasePath` | Durable publication checkpoint | Pinned file |
| `maxCheckpointBytes` | int | 33554432 (code) | Setup requires 1024–67108864 | Checkpoint budget | **Yes** (settings hash) |
| `liveChecks` | object | disabled | See 2.3 | Targeted-live provider | No |

### 2.3 `liveChecks`

Setup requires `enabled=false`. Live checks need the long-running worker (not `--worker-once`).

| Setting | Type | Default | Valid range / rule | Purpose |
|---|---|---|---|---|
| `enabled` | bool | false | Requires `queryContractVerified=true`, a valid `source` pin, profile `deadlineSeconds` 1–10 and `maxOutputBytes` 1024–32768 | Enable live provider |
| `queryContractVerified` | bool | false | — | Operator attestation of the query contract |
| `source` | `{managementGroupId, scopeKind, groupId}` | — | Must match the receiver scope | Scope pin |
| `profile` | object | — | Same shape as 2.4 | Dedicated profile for live checks |
| `pollSeconds` | int | 2 | 2–5 | Claim polling interval |

The provider policy hash is SHA-256 of the live profile `{ScriptSha256, Configuration}` and must equal `TargetedLive.ProviderPolicySha256`. Claim and result calls use a 3 s deadline and a 32768-byte body cap.

### 2.4 `profile` (PowerShell normalization profile)

| Setting | Type | Default | Valid range / rule | Purpose | Policy? |
|---|---|---|---|---|---|
| `executablePath` | string | `""` | Must equal `%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe` (Windows PowerShell 5.1 Desktop) | Child process | No |
| `scriptPath` | string | `""` | Absolute, local (not UNC/network), no reparse points, `.ps1` | Collector entry script (`Invoke-SCOMGenericGroupProfile.ps1`) | **Yes** (via hash) |
| `scriptSha256` | string | `""` | 64 hex; verified on every run | Script integrity pin | **Yes** |
| `configuration` | JSON object | — | No duplicate properties; serialized size ≤ `maxInputBytes` | Profile configuration (2.5) | **Yes** |
| `maxInputBytes` | int | 65536 | 1024–1048576 | Request budget to the script | No |
| `maxOutputBytes` | int | 16777216 | 1024–33554432 | Script output budget | No |
| `deadlineSeconds` | int | 900 | 1–7200 | Script deadline | No |

The script runs with `-NoLogo -NoProfile -NonInteractive -File`. A non-zero exit code or any stderr output is a failure; the worker surfaces it as `SOURCE_UNAVAILABLE` with a `SOURCE_BRIDGE_*` reason (see [troubleshooting.md](troubleshooting.md)).

### 2.5 `profile.configuration`

| Key | Type | Default | Rule | Must match receiver |
|---|---|---|---|---|
| `operationsManagerModule` | string | none | Rooted `X:\` path to an existing local file; not UNC | — |
| `managementServer` | string | none | Non-empty (`<MGMT_SERVER>`) | — |
| `managementGroupId` | GUID | none | Non-empty; must equal the connected management group | `ReadModel.ManagementGroupId` |
| `scopeKind` | string | none | `group` or `managementGroup` | `ReadModel.ScopeKind` |
| `groupId` | GUID or null | null | Required for `group`; null for `managementGroup` | `ReadModel.GroupId` |
| `scopeVisibilityVerified` | bool | — | Must be true | — |
| `membershipQueryContractVerified` | bool | — | Must be true for `group` | — |
| `wholeManagementGroupApproved` | bool | — | Must be true for `managementGroup` | — |
| `enumerationQueryContractVerified` | bool | — | Must be true for `managementGroup` | — |
| `allowedPropertiesByClass` | map class → string[] | — | Per-class set equals receiver | `ReadModel.AllowedPropertiesByClass` |
| `metricMappingsByClass` | map | — | Setup requires empty | `ReadModel.AllowedMetricsByClass` (empty) |
| `includeAlertDescriptions` | bool | — (required) | — | `ReadModel.IncludeAlertDescriptions` |
| `additionalRelationshipNames` | string[] | — | ≤ 16; each an exact relationship name (no `*?[]`) resolving to exactly one SDK relationship | — |
| `additionalRelationshipContractVerified` | bool | — | Must be true when names are given, unless `hostingLabPreview` | — |
| `inventoryKind` | string | `generic` | `generic` or `supportedWindows` (case-sensitive); setup requires `supportedWindows` | — |
| `hostingLabPreview` | bool | false | Lab only; requires `worker-once`, live checks off, new checkpoint | `ReadModel.OfflineHostingPreview` |
| `windowsMapping` | object or null | null | Required with `verified=true` for `supportedWindows` (2.6) | `ReadModel.WindowsMetricHosting`, `WindowsOsCaptionProperty` |
| `operationalLogging` | `{directory, maxFileBytes, retainedFiles}` | — | Required for `supportedWindows`; collector budgets: `maxFileBytes` 1024–16777216 (default 1048576), `retainedFiles` 1–20 (default 5); setup requires 1048576 and 5 | — |
| `maxGraphQueries` | int | 10000 | Range: Not determined from source | — |
| `maxSeriesSamples` | int | 10000 | Range: Not determined from source | — |
| `maxMetricSamples` | int | 50000 | Range: Not determined from source | — |
| `maxObjects` | int | none (example 1000) | Default and range: Not determined from source | — |
| `maxGroups` | int | none (example 100) | Default and range: Not determined from source | — |
| `maxAlerts` | int | none (example 10000) | Default and range: Not determined from source | — |
| `pageSize` | int | none (example 50) | Default and range: Not determined from source | — |
| `deadlineSeconds` | int | none (example 900) | Default and range: Not determined from source | — |

All of `profile.configuration` is covered by the checkpoint policy hash (Policy: **Yes**). The collector requests 14 days of metric history.

### 2.6 `profile.configuration.windowsMapping`

| Key | Type | Rule |
|---|---|---|
| `verified` | bool | Must be true (`SCOM_WINDOWS_MAPPING_UNVERIFIED` otherwise) |
| `computerClassName`, `operatingSystemClassName`, `volumeClassName` | string | Non-empty exact class names, no `*?[]`, each resolving to exactly one class; all three distinct |
| `hostnameProperty`, `osVersionProperty`, `memoryProperty`, `processorCountProperty`, `volumeNameProperty`, `fileSystemProperty`, `volumeSizeProperty` | string | Non-empty, ≤ 256 chars |
| `computerHostsOsRelationship` | string | Required; ≤ 256 chars; must be in `additionalRelationshipNames` |
| `osHostsVolumeRelationship` / `computerHostsVolumeRelationship` | string | Exactly one; ≤ 256 chars; different from the OS relationship; must be in `additionalRelationshipNames` |
| `memoryToMiB`, `volumeSizeToMiB` | number | Finite, > 0 |
| `osCaptionProperty` | string, optional | ≤ 128 chars, no `*?[]`; must equal `ReadModel.WindowsOsCaptionProperty` |
| `agentMonitoringVerified` | bool | Must be true unless `monitoringRoleMapping` is present |
| `monitoringRoleMapping` | object, optional | `{verified:true, agentClassName, managementServerClassName (distinct), computerHostsApplicationRelationship (≤ 256 chars, in additionalRelationshipNames, different from the OS and disk relationships)}`; not allowed in hosting lab preview |

`ReadModel.WindowsMetricHosting` must carry the mapping's `computerClassName`, `operatingSystemClassName` and `computerHostsOsRelationship`.

**Generating this section.** `setup\New-ScomMappingProfile.ps1` writes `windowsMapping`, `allowedPropertiesByClass` (keyed by the observed most-derived classes), `additionalRelationshipNames` (the two observed relationships), the scope fields, and `memoryToMiB` / `volumeSizeToMiB` proven by plausibility (memory must land in 0.5-4096 GiB; disk uses `SizeInMBs` when present, otherwise `Size` / `SizeNumeric` as bytes). It leaves all six attestation flags `false`, writes `<worker>.evidence.json`, and exits non-zero with `null` units rather than guess. `setup\Approve-ScomMappingProfile.ps1` then sets exactly `windowsMapping.verified`, `windowsMapping.agentMonitoringVerified`, `scopeVisibilityVerified`, `membershipQueryContractVerified`, `enumerationQueryContractVerified` and `additionalRelationshipContractVerified` to `true` and writes `<worker>.attestation.txt`. The generator does not fill identity, publisher, script-hash or state-path placeholders, and it does not set the receiver: copy `allowedPropertiesByClass` into `ReadModel.AllowedPropertiesByClass`. See [deployment-guide.md](deployment-guide.md#6c-workerjson).

### 2.7 `identity`

| Setting | Type | Rule | Purpose | Policy? |
|---|---|---|---|---|
| `tenantId` | GUID | Non-empty; must equal `Auth.TenantId` | `<TENANT_ID>` | **Yes** |
| `clientId` | GUID | Non-empty | Publisher application `<PUBLISHER_APP_ID>` | **Yes** |
| `resource` | string | Absolute `api://` or `https://` URI; no trailing `/` and no `/.default`; receiver `Auth.Audiences` must contain it (or the GUID of `api://<API_APP_ID>`) | Token resource; scope requested is `<resource>/.default` | **Yes** |
| `certificateThumbprint` | string | 40 hex characters; one certificate in `LocalMachine\My` with an RSA private key (≥ 2048 bits), currently valid | `<CERT_THUMBPRINT>`; client-assertion signing | No |

Token endpoint: `https://login.microsoftonline.com/<TENANT_ID>/oauth2/v2.0/token`, `client_credentials`.

### 2.8 `publisher`

| Setting | Type | Default | Valid range / rule | Policy? |
|---|---|---|---|---|
| `endpoint` | string | none | `https` URL, no user info, query or fragment; the receiver's app base URL (`https://<FQDN>/`) | **Yes** |
| `maxBatchBytes` | int | 1048576 | 1024–1048576 | No |
| `maxDatasetBytes` | int | 16777216 | ≥ `maxBatchBytes`, ≤ 33554432 | No |
| `maxRecords` | int | 100000 | 1–1000000 | No |
| `maxAttempts` | int | 3 | 1–5 | No |
| `requestTimeoutSeconds` | int | 30 | 1–120 | No |
| `publicationTimeoutSeconds` | int | 1200 | ≥ `requestTimeoutSeconds`, ≤ 7200 | No |

### 2.9 `reconciliation`

All values are part of the checkpoint settings hash (Policy: **Yes**).

| Setting | Type | Default | Valid range |
|---|---|---|---|
| `maxRecords` | int | 100000 | 1–1000000 |
| `batchRecords` | int | 100 | 1–1000, ≤ `maxRecords` |
| `maxAlerts` | int | 10000 | 1–100000 |
| `maxSeriesSamples` | int | 10000 | 1–100000 |
| `maxMetricSamples` | int | 50000 | ≥ `maxSeriesSamples`, ≤ 1000000 |
| `resolvedAlertRetention` | timespan | 7.00:00:00 | 0–365 days |

### 2.10 `schedule`

| Setting | Type | Default | Valid range | Purpose |
|---|---|---|---|---|
| `interval` | timespan | 00:05:00 | 1 s–1 day | Fixed delay after a completed (published) attempt |
| `attemptDeadline` | timespan | 00:20:00 (example file: 00:35:00) | 1 s–2 h | Per-attempt deadline |
| `failureBackoff` | timespan | 00:01:00 | ≥ 1 s | First retry delay after a deferred attempt; doubles each time |
| `maxFailureBackoff` | timespan | 00:30:00 | ≥ `failureBackoff`, ≤ 1 day | Backoff ceiling |
| `maxConsecutiveDeferredAttempts` | int | 5 | 1–100 | Reaching it stops the worker with `COLLECTION_ATTEMPTS_EXHAUSTED` |

### 2.11 Worker command line and runtime-state directory

| Argument | Purpose |
|---|---|
| `--worker <abs path> [--runtime-state-dir <dir>]` | Long-running worker |
| `--worker-once <path>` | Single attempt |
| `--worker-stop-request <dir>` / `--worker-rearm <dir>` | Cooperative stop; operator rearm after a gated stop |
| `--validate-setup <serverDir> <workerPath>` / `--validate-setup-json <path>` | Offline configuration checks |

`--runtime-state-dir` is a separate local, non-network, non-reparse directory holding `worker.status.json` (≤ 8192 bytes), `worker.lock` (held exclusively for the process lifetime) and `worker.stop.request`. It is not configuration. A stale `running` state left by a crash or reboot is recovered automatically at the next start; only `rearmRequired=true` after a terminal failure (`WORKER_RECOVERY_REFUSED`, exit 2) blocks the start until an explicit rearm (see [operations.md](operations.md) and `Operations\DurableIndexedRefresh\README.md`). Worker exit codes: `0` published or already running, `1` transient failure (restartable), `2` configuration or attestation failure.

## 3. Settings that must match between receiver and worker

| Receiver | Worker | Notes |
|---|---|---|
| `ReadModel.ManagementGroupId` | `profile.configuration.managementGroupId` | `<MG_ID>` |
| `ReadModel.ScopeKind` | `profile.configuration.scopeKind` | |
| `ReadModel.GroupId` | `profile.configuration.groupId` | `<GROUP_ID>` or both null |
| `ReadModel.AllowedPropertiesByClass` | `profile.configuration.allowedPropertiesByClass` | Per-class set equality |
| `ReadModel.AllowedMetricsByClass` | `profile.configuration.metricMappingsByClass` | Both empty for the supported profile |
| `ReadModel.IncludeAlertDescriptions` | `profile.configuration.includeAlertDescriptions` | |
| `ReadModel.WindowsToolsEnabled` | `inventoryKind=supportedWindows` + `windowsMapping` | Required for management-group scope |
| `ReadModel.WindowsMetricHosting` | `windowsMapping` class and relationship names | Three values must match |
| `ReadModel.WindowsOsCaptionProperty` | `windowsMapping.osCaptionProperty` | |
| `ReadModel.OfflineHostingPreview` | `profile.configuration.hostingLabPreview` | |
| `Auth.TenantId` | `identity.tenantId` | |
| `Auth.Audiences` | `identity.resource` | Must contain the resource or its `api://` GUID |
| `ReadModel.IngestionRole` (`Scom.Ingest`) | App role granted to `<PUBLISHER_APP_ID>` | See [entra-setup.md](entra-setup.md) |
| `ReadModel.PublisherObjectIds` | Enterprise-app object id of `identity.clientId` | |
| Receiver base URL (`https://<FQDN>/`) | `publisher.endpoint` | |
| `Source.Fresh/UnavailableMinutes` | `ReadModel.Fresh/UnavailableMinutes` | Equal when `WindowsToolsEnabled` |
| `TargetedLive.*` (disabled) | `liveChecks.enabled` (false) | Both must be disabled for setup |

## 4. Policy changes

A "policy change" is any change to a pinned value. The receiver and worker refuse to continue with mismatching state; do not edit the database, checkpoint or status files to bypass this. Procedure: stop the worker task, back up the receiver database and the worker checkpoint, change both sides consistently, run `--validate-setup`, then reinitialize (new database file and new checkpoint) and collect again. The deployment runbook calls these "pinned policy changes, not an in-place database edit" ([indexed-deployment-runbook.md](indexed-deployment-runbook.md), [guided-setup.md](guided-setup.md)).

| Pinned in | Covers | Failure when changed |
|---|---|---|
| Receiver database `configuration.scope` | `ManagementGroupId`, `ScopeKind`, `GroupId`, sorted `AllowedPropertiesByClass`, sorted `AllowedMetricsByClass`, `IncludeAlertDescriptions`; wrapped in schema 2 for `WindowsToolsEnabled`, 3 for `WindowsMetricHosting`, 4 for `WindowsOsCaptionProperty`, 5 for `OfflineHostingPreview` | Startup error "Read model database belongs to a different configured scope or lacks pinned state" |
| Worker checkpoint policy hash | `profile.scriptSha256`, `profile.configuration`, `publisher.endpoint`, `identity` tenant/client/resource | "Worker checkpoint ... policy changed" and similar checkpoint errors |
| Worker checkpoint settings hash | `reconciliation` settings and `maxCheckpointBytes` | "Worker checkpoint version, profile policy, settings or byte budget differs" |

Not pinned (safe to change with a restart and re-validation): `FreshMinutes`, `UnavailableMinutes`, size and record limits, `PublisherObjectIds`, `IngestionRole`, `Lifecycle`, `Source`, `TargetedLive` tuning, `schedule`, `publisher` budgets and retry settings, `operationalLogging`, `certificateThumbprint` (rotate the certificate in `LocalMachine\My` and update the thumbprint).

## 5. What `--validate-setup` enforces

`--validate-setup` is offline: it does not build the host, open SQLite, load keys, acquire tokens or run the collector profile. Outcomes: `SCOM_SETUP_CONFIGURATION_READY` (exit 0), `SCOM_SETUP_SOURCE_EVIDENCE_REQUIRED` (exit 2: the operator attestations `scopeVisibilityVerified` and the membership/enumeration contract flags are not all true), `SCOM_SETUP_CONFIGURATION_INVALID ErrorType=<T>` (exit 1).

Checks: receiver effective configuration for `Auth`, `Source`, `ReadModel`, `Lifecycle`, `TargetedLive`, `AllowedHosts` and `OperationalLogging` equals `appsettings.json` (no environment overrides); `ReadModel.Enabled` true; `Source.SnapshotEnabled` false; `TargetedLive` and `liveChecks` disabled; equal freshness; scope, tenant, audience, `RequiredScope=Scom.Read`, `IngestionRole=Scom.Ingest`; empty metric policy; property and alert-description policy equal; `maxCheckpointBytes` 1024–67108864; `checkpointPath` absolute and different from `DatabasePath`; three distinct, non-nested operational log directories (receiver, worker, profile) with 1048576/5 budgets; `inventoryKind=supportedWindows` with a `windowsMapping` object; for `managementGroup` scope `WindowsToolsEnabled` and `wholeManagementGroupApproved`. For hosting lab preview it additionally requires `additionalRelationshipContractVerified=false`, no `monitoringRoleMapping`, exactly two relationships (`computerHostsOsRelationship` plus one volume relationship) equal to `additionalRelationshipNames`, and matching `WindowsMetricHosting`.
