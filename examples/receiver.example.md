# receiver.example.json — field notes

JSON cannot hold comments, so each non-obvious setting is explained here.
Replace every `<PLACEHOLDER>`. Merge reviewed settings into the installed
`appsettings.json`; never overwrite a working file with this example. Full field
reference: [configuration-reference.md](../docs/configuration-reference.md).

| Setting | Value / placeholder | Where to obtain it |
|---|---|---|
| `Auth.TenantId` | `<TENANT_ID>` | Entra tenant (directory) ID; also `tenantId` in `identity.json`. |
| `Auth.Audiences` | `<API_AUDIENCE>` | The API access token's actual `aud`. For v2 tokens this is the API application (client) ID GUID; verify against a real token locally. Not the reader app's ID. |
| `Auth.RequiredScope` | `Scom.Read` | Delegated scope on the API registration. |
| `Auth.Callers[].ObjectId` | `<READER_USER_OBJECT_ID>` | Object ID of each authorized **user** (Entra > Users). Not the reader app or the publisher. |
| `Auth.Callers[].Scopes` | `["scom-lab"]` | Logical tool-scope label. Setup's merge requires exactly this value. |
| `Source.FreshMinutes` / `UnavailableMinutes` | `75` / `180` | Freshness budget. Must equal `ReadModel.FreshMinutes` / `UnavailableMinutes`. `UnavailableMinutes` must exceed `FreshMinutes` and be at most 1440. With the worker's `schedule.interval` of 1 hour, `FreshMinutes` must be larger than the interval or data is reported stale between runs. These two values are derived from the hourly cadence, not lab-measured; tune to your recurrence. |
| `ReadModel.WindowsToolsEnabled` | `true` | Required for management-group scope; serves indexed Windows inventory. |
| `ReadModel.WindowsMetricHosting` | Computer / OperatingSystem / relationship classes | Must name the same classes as the worker's `windowsMapping`. Pinned database policy. |
| `ReadModel.WindowsOsCaptionProperty` | `OSVersionDisplayName` | Enables OS caption (used by the lifecycle tool). Must match the worker's `osCaptionProperty` and be allow-listed. Pinned database policy. |
| `ReadModel.ManagementGroupId` / `GroupId` | `<MG_ID>` / `<GROUP_ID>` | SCOM management group ID and (for group scope) group ID. Use `null` for `GroupId` with `ScopeKind` `managementGroup`. |
| `ReadModel.PublisherObjectIds` | `<PUBLISHER_SP_OBJECT_ID>` | Object ID of the publisher **service principal** (Enterprise applications), not the app registration. |
| `ReadModel.AllowedPropertiesByClass` | most-derived class names | Exact, case-sensitive; base-class entries are not inherited. Must match the worker. Candidate Windows Server 2022 names are shown; confirm them against your own probe output. |
| `AllowedHosts` | `<FQDN>` | The public host name of the receiver. |

Changing `WindowsMetricHosting`, `WindowsOsCaptionProperty` or the allow-lists on an
existing database is a policy change: follow
[operations.md](../docs/operations.md#policy-changes).
