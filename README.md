# SCOM MCP server

An experimental, read-only Model Context Protocol (MCP) server for a
**two-computer Windows System Center Operations Manager (SCOM) pilot**. A
PowerShell collector reads a narrowly scoped SCOM view and publishes a
snapshot. The ASP.NET Core server validates the snapshot and exposes inventory
and performance tools over authenticated HTTP.

This is not an enterprise-wide SCOM integration or a production-ready security
boundary. Review the limitations below before deploying it. The public source
does not contain the original deployment's settings, data, runbooks, or
environment-specific deployment scripts.

## What it provides

- `get_source_status`: snapshot freshness, coverage, and capabilities.
- `get_inventory_summary`, `list_devices`, and `get_device_details`: the two
  approved Windows computers and their discovered logical volumes.
- `get_utilization` and `get_capacity_summary`: available CPU, memory, and disk
  observations, with timestamps and missing-data limitations.

Responses describe the retained snapshot, not a live SCOM query. Optimized
observations may not cover an entire time window. A successful `/healthz`
response proves only that the process is running; it does not prove that the
source is available or current.

## Prerequisites

- Windows, a SCOM management group, and the OperationsManager PowerShell
  module on the collector host.
- The .NET 8 runtime and a compatible .NET SDK for building the server.
- A Microsoft Entra tenant, an API registration with a delegated scope, and an
  MCP client able to obtain a bearer token for that API.
- Exactly two approved Windows computer FQDNs and their SCOM computer object
  IDs. The server and collector must use the same hostnames.
- A read-only SCOM role for the collector, scoped so its class queries can see
  only the approved computers and their OS and logical-disk instances. The
  collector refuses out-of-scope instances; it is not designed to enumerate an
  entire management group.

## Build and validate

From the repository root in PowerShell:

```powershell
dotnet build .\ScomMcp\ScomMcp.csproj --configuration Release
pwsh -NoProfile -File .\validate.ps1
```

The validation harness uses synthetic fixtures and a loopback HTTP server; it
does not access SCOM, publish snapshots, or deploy IIS. Follow your
organization's PowerShell execution and signing policy.

## Configure the server

Copy `ScomMcp\appsettings.example.json` to
`ScomMcp\appsettings.Local.json` and replace every example value. The checked-in
`appsettings.json` is deliberately incomplete, so the server refuses to start
until configured. The example uses reserved `.invalid` hostnames and synthetic
IDs; they are not usable deployment settings.

Set:

- `Auth:TenantId` to the verified tenant GUID; `Auth:Audiences` to the exact API
  audience in the access token; and `Auth:RequiredScope` to the delegated
  `scp` claim value.
- `Auth:Callers` to explicit **object-ID** grants with
  `Scopes: ["scom-lab"]`. Use the caller's canonical GUID, not an API or client
  application's object ID. Avoid UPN-based grants; see the security limitations.
- `Source:Directory` to the absolute path where the collector's published
  `snapshot.json` and `collector-status.json` will be accessible. Give the
  server identity read-only access.
- `Source:AllowedHostnames` to exactly the two approved FQDNs. Set
  `AllowedHosts` to the intended HTTP host, and review freshness and size
  limits for your deployment.

For a local-only startup, from `ScomMcp`:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Local'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5000'
dotnet run --configuration Release
```

The MCP endpoint is `/mcp`; `/healthz` is an unauthenticated process-health
endpoint. For remote use, provide TLS, restrict network access, configure the
correct host, and independently review your reverse proxy or IIS deployment.
Do not expose the local HTTP example to an untrusted network. A client must
obtain a valid, signed delegated token with the configured audience, tenant,
scope, and authorized user object ID.

## Configure the collector

Copy `Collector\collector.example.json` to
`Collector\collector.config.json`, then replace the example values:

- `CollectorHost`: the collector machine's short Windows computer name.
- `ManagementServer` and `ManagementGroup`: the explicitly approved SCOM
  management server and exact management-group name.
- `OperationsManagerModule`: the installed module name or manifest path.
- `LocalDataDirectory` and `PublishDirectory`: distinct existing absolute
  Windows paths. The latter is typically a protected UNC share pointing to the
  server's source directory.
- `Computers`: exactly two distinct approved hostnames and their SCOM computer
  object GUIDs.

Configure a scheduled task on the approved collector host to run as
`NT AUTHORITY\NETWORK SERVICE`, with a command equivalent to:

```powershell
pwsh -NoProfile -File C:\path\to\Collector\Collect-SCOMSnapshot.ps1 -ConfigurationPath C:\path\to\Collector\collector.config.json
```

Replace both paths and follow local signing requirements. Grant the collector
identity only the SCOM read permissions and local/share write permissions it
needs; restrict the share and NTFS ACLs. The collector does not create its data
directories. Verify SCOM class visibility, module location, and the management
pack's memory units before using the results.

The scope ID in this public version is `scom-lab`. Snapshots from earlier
environment-specific versions using a different scope ID are not compatible;
run this version of the collector and server together.

## Optional supervised indexed-run wrapper

The repository also contains an experimental, synthetic-test-only supervised
one-shot wrapper under `Operations\SupervisedIndexedRun`. It does not expand
the collector's source scope, provide recurrence, monitor SCOM/SQL, or establish
production acceptance. Review its limitations and provenance before use.

## Security and known limitations

- Keep real configuration, snapshots, logs, tokens, credentials, deployment
  notes, and operational identifiers out of source control. `.gitignore` is
  only a convenience, not a secret-scanning or access-control guarantee.
- The optional UPN-based authorization mode follows a mutable username; a
  reassigned UPN can grant a different identity access. Use object-ID grants
  instead. Real network exposure, IIS permissions, Entra assignments, and
  live SCOM behavior require an independent deployment review.
- A prior review found that a snapshot sample missing its `value` property can
  deserialize as zero. Treat malformed snapshot data as untrusted and do not
  rely on the server for critical decisions until this is addressed.
- The collector requests 14 days of history but rejects a series with more
  than 10,000 observations, which can make collection unavailable for frequent
  sampling. Unexpected server exceptions return a sanitized error without
  detailed server-side logging. Noncanonical caller object-ID formats may be
  accepted by configuration but fail authorization.
- Only the configured two Windows computers and supported SCOM classes are in
  scope. Network devices, enterprise totals, license entitlement, and
  guaranteed full-window performance coverage are not provided.

## License

The source is available under the [MIT License](LICENSE). Dependencies have
their own licenses.
