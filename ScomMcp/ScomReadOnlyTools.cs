using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ScomMcp;

[McpServerToolType]
public sealed class ScomReadOnlyTools(ISnapshotReader reader, QueryEngine engine,
    IHttpContextAccessor http, AuthSettings auth)
{
    [McpServerTool(Name = "get_source_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read SCOM snapshot capabilities, timestamps and coverage for authorized scom-lab only. Healthz does not prove metrics exist. Failed/latest missing collection returns SOURCE_UNAVAILABLE; no fixture fallback.")]
    public Task<CallToolResult> SourceStatus(
        [Description("Optional authorized scope; omitted means scom-lab. No other scopes are supported.")] string? scopeId = null,
        CancellationToken cancellationToken = default) =>
        Execute(scopeId, (ds, _, _) => engine.SourceStatus(ds), cancellationToken);

    [McpServerTool(Name = "get_inventory_summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Complete filtered distinct SCOM COMPUTER count, never volumes or enterprise total. Optional filters are exact case-insensitive matches: entityKind computer/networkDevice (network unsupported), osFamily windows/linux/unknown, osVersion exact caption, health healthy/warning/critical/unknown, virtualization physical/virtual/unknown, groupId exact group. Null omits a filter; unknown fields are never zero.")]
    public Task<CallToolResult> Inventory(string? scopeId = null, string? entityKind = null,
        string? osFamily = null, string? osVersion = null, string? health = null, string? virtualization = null,
        string? groupId = null, CancellationToken cancellationToken = default) =>
        Execute(scopeId, (ds, _, _) => engine.Inventory(ds,
            new(entityKind, osFamily, osVersion, health, virtualization, groupId)), cancellationToken,
            () => new InventoryFilter(entityKind, osFamily, osVersion, health, virtualization, groupId).Normalize());

    [McpServerTool(Name = "list_devices", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Deterministic hostname/ID-sorted distinct computer page. Same exact filters as get_inventory_summary. pageSize 1..50 (default 20). Opaque cursor is bound to caller, scope, dataset, filters and page size for five minutes by default; restart paging after dataset change, expiry or app recycle.")]
    public Task<CallToolResult> Devices(string? scopeId = null, string? entityKind = null,
        string? osFamily = null, string? osVersion = null, string? health = null, string? virtualization = null,
        string? groupId = null, int pageSize = 20, string? cursor = null, CancellationToken cancellationToken = default) =>
        Execute(scopeId, (ds, caller, scope) => engine.ListDevices(ds, caller, scope,
            new(entityKind, osFamily, osVersion, health, virtualization, groupId), pageSize, cursor), cancellationToken,
            () => { new InventoryFilter(entityKind, osFamily, osVersion, health, virtualization, groupId).Normalize(); QueryEngine.ValidatePageSize(pageSize); });

    [McpServerTool(Name = "get_device_details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Source-backed authorized computer GUID details and its volumes/provenance. Missing or unauthorized entities both return NOT_FOUND. Unavailable attributes remain null.")]
    public Task<CallToolResult> Details(string entityId, string? scopeId = null, CancellationToken cancellationToken = default) =>
        Execute(scopeId, (ds, _, _) => engine.Details(ds, entityId), cancellationToken, () => Guard.Entity(entityId));

    [McpServerTool(Name = "get_utilization", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read retained SCOM observations, optimized or non-optimized. metric: cpuPercent|memoryUsedPercent|diskFreePercent; window: 1h|24h|7d; statistic: mean|max|latest. Defaults 1h/latest/top20. Mean is arithmetic observation mean NOT time-weighted/full-window utilization. Coverage reports optimized/non-optimized counts; neither implies complete time coverage. Memory prefers native memoryUsedPercent per entity/window; only if absent derives from memoryAvailableMiB with known total. Native percent needs no total; sourceMetric/previousSourceMetric report selection, never blended. Disk rows are individual volumes. entityId optional computer/volume GUID; scopeId omitted means scom-lab. top 1..20; sortOrder asc|desc (default desc). thresholdPercent 0..100 must accompany comparison lt|gt (strict). comparePrevious compares same preceding window; missing previous is null. Counts precede top truncation; all missing metrics produce SOURCE_UNAVAILABLE.")]
    public Task<CallToolResult> Utilization(string metric, string? entityId = null, string? scopeId = null,
        string window = "1h", string statistic = "latest", int top = 20, bool comparePrevious = false,
        string? sortOrder = null, double? thresholdPercent = null, string? comparison = null,
        CancellationToken cancellationToken = default) =>
        Execute(scopeId, (ds, _, _) => engine.Utilization(ds, metric, entityId, window, statistic,
            top, comparePrevious, sortOrder, thresholdPercent, comparison), cancellationToken,
            () => QueryEngine.ValidateUtilization(metric, entityId, window, statistic, top, sortOrder, thresholdPercent, comparison));

    [McpServerTool(Name = "get_capacity_summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Scoped memory|disk capacity in MiB AS OF SNAPSHOT, not current live capacity; disk counts volumes separately from machines. Memory prefers native used-percent and known total for used/free MiB; available-MiB is fallback only when native absent in the snapshot freshness interval. sourceMetric and observedAtUtc identify each selected observation; percent alone never fabricates unknown absolute capacity. entityId optional GUID; groupId optional exact group; minFreePercent/maxFreePercent optional inclusive 0..100 bounds, min<=max. Unknown free values cannot match percent filters. Sample selection is snapshot-anchored for deterministic paging; stale-at-request observations are explicitly partial. usageStatus unavailable marks missing metrics; unavailable usage remains null. Comparable sums use only rows with all capacity components. pageSize 1..50 (default20); cursor bound to caller/dataset/query. Never infer capacity or licenses from processor counts.")]
    public Task<CallToolResult> Capacity(string resource, string? scopeId = null, string? entityId = null,
        string? groupId = null, double? minFreePercent = null, double? maxFreePercent = null,
        int pageSize = 20, string? cursor = null, CancellationToken cancellationToken = default) =>
        Execute(scopeId, (ds, caller, scope) => engine.Capacity(ds, caller, scope, entityId, resource, groupId,
            minFreePercent, maxFreePercent, pageSize, cursor), cancellationToken,
            () => QueryEngine.ValidateCapacity(entityId, resource, groupId, minFreePercent, maxFreePercent, pageSize));

    private async Task<CallToolResult> Execute(string? scopeId, Func<Dataset, Caller, string, Envelope> run,
        CancellationToken ct, Action? validate = null)
    {
        Dataset? ds = null;
        try
        {
            var caller = http.HttpContext is { } context ? CallerAuthorization.Resolve(context.User, auth) : null;
            if (caller is null) throw new QueryFault("FORBIDDEN", "Caller is not authorized.");
            var scope = CallerAuthorization.AuthorizeScope(caller, scopeId);
            validate?.Invoke();
            ds = await reader.ReadAsync(ct);
            return Result(run(ds, caller, scope), false);
        }
        catch (QueryFault ex)
        {
            if (ex.Code == "INVALID_ARGUMENT") throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
            return Result(engine.Error(ex, ds), true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Result(engine.Error(new QueryFault("SOURCE_UNAVAILABLE", "REQUEST_COULD_NOT_BE_COMPLETED")), true);
        }
    }

    private static CallToolResult Result(Envelope envelope, bool error) => new()
    {
        IsError = error,
        Content = [new TextContentBlock { Text = Wire.Encode(envelope) }]
    };
}
