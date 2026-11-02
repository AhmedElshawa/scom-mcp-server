using System.Collections.Immutable;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace ScomMcp;

public sealed record InventoryFilter(string? EntityKind = null, string? OsFamily = null,
    string? OsVersion = null, string? Health = null, string? Virtualization = null, string? GroupId = null)
{
    public InventoryFilter Normalize()
    {
        static string? Norm(string? s) => s?.ToLowerInvariant();
        Guard.Argument(OsVersion is null || OsVersion.Length is > 0 and <= 256, "osVersion must be an exact caption, at most 256 characters.");
        Guard.Argument(GroupId is null || GroupId.Length is > 0 and <= 100, "groupId must be at most 100 characters.");
        var f = this with { EntityKind = Norm(EntityKind), OsFamily = Norm(OsFamily), OsVersion = Norm(OsVersion),
            Health = Norm(Health), Virtualization = Norm(Virtualization), GroupId = Norm(GroupId) };
        Guard.Argument(f.EntityKind is null or "computer" or "networkdevice", "entityKind must be computer or networkDevice.");
        Guard.Argument(f.OsFamily is null or "windows" or "linux" or "unknown", "osFamily must be windows, linux, or unknown.");
        Guard.Argument(f.Health is null or "healthy" or "warning" or "critical" or "unknown", "Invalid health.");
        Guard.Argument(f.Virtualization is null or "physical" or "virtual" or "unknown", "Invalid virtualization.");
        return f;
    }

    public bool Matches(Device d) =>
        (EntityKind is null || EntityKind == d.EntityKind) &&
        (OsFamily is null || OsFamily == d.OsFamily) &&
        (OsVersion is null || string.Equals(OsVersion, d.OsVersion, StringComparison.OrdinalIgnoreCase)) &&
        (Health is null || Health == d.Health) &&
        (Virtualization is null || Virtualization == d.Virtualization) &&
        (GroupId is null || d.GroupIds.Contains(GroupId));
}

public sealed record SampleCoverage(DateTimeOffset? FirstSampleUtc, DateTimeOffset? LastSampleUtc, int SampleCount,
    double? AgeSeconds, bool FullWindowCovered, int OptimizedSampleCount, int NonOptimizedSampleCount);
public sealed record UtilizationRow(string EntityId, string? ParentEntityId, string EntityKind, string? DeviceId,
    string Metric, double? Value, double? PreviousValue, double? ChangePercentagePoints,
    SampleCoverage Coverage, SampleCoverage? PreviousCoverage, string? SourceMetric, string? PreviousSourceMetric);
public sealed record CapacityRow(string EntityId, string? ParentEntityId, string EntityKind, string? DeviceId,
    double? TotalMiB, double? UsedMiB, double? FreeMiB, double? FreePercent, DateTimeOffset? ObservedAtUtc,
    string? SourceMetric, bool? Optimized);
public sealed record PageData<T>(ImmutableArray<T> Rows, int TotalCount, int PageSize, string? NextCursor);

public sealed class QueryEngine(SourceSettings settings, TimeProvider clock, CursorCodec cursors)
{
    private static readonly ConditionalWeakTable<Snapshot, Lazy<ObservationIndex>> ObservationIndexes = new();
    private static readonly string[] BaseLimitations =
    [
        "ONLY_APPROVED_WINDOWS_COMPUTERS_NOT_AN_ENTERPRISE_TOTAL",
        "NETWORK_DEVICES_AND_LICENSE_ENTITLEMENTS_UNSUPPORTED",
        "NO_HISTORY_BEFORE_RETAINED_SCOM_OBSERVATIONS_IS_ASSUMED"
    ];

    public Envelope SourceStatus(Dataset ds) => Wrap(ds, new
    {
        mode = "scom", capabilities = ds.Snapshot.Capabilities,
        collectionStartedAtUtc = ds.Snapshot.CollectionStartedAtUtc,
        collectedAtUtc = ds.Snapshot.CollectedAtUtc,
        expectedDeviceCount = ds.Snapshot.ExpectedDeviceCount, uniqueComputerCount = ds.Snapshot.Devices.Length,
        metricObservationCount = ds.Snapshot.Samples.Length,
        optimizedObservationCount = ds.Snapshot.Samples.Count(s => s.Optimized),
        nonOptimizedObservationCount = ds.Snapshot.Samples.Count(s => !s.Optimized),
        duplicateDevicesResolved = ds.DuplicateDevices, duplicateVolumesResolved = ds.DuplicateVolumes,
        duplicateSamplesRemoved = ds.DuplicateSamples
    });

    public Envelope Inventory(Dataset ds, InventoryFilter filter)
    {
        var f = filter.Normalize();
        var rows = Filter(ds, f);
        static object Breakdown(IEnumerable<Device> devices, Func<Device, string?> key) =>
            devices.GroupBy(d => key(d) ?? "unknown", StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new { value = g.Key, count = g.Count() }).ToArray();
        return Wrap(ds, new
        {
            uniqueComputerCount = rows.Length,
            countsAreCompleteForFilteredDataset = true,
            health = Breakdown(rows, d => d.Health), osFamily = Breakdown(rows, d => d.OsFamily),
            osVersion = Breakdown(rows, d => d.OsVersion), virtualization = Breakdown(rows, d => d.Virtualization),
            unknownMemoryCount = rows.Count(d => d.MemoryTotalMiB is null),
            unknownProcessorCount = rows.Count(d => d.ProcessorCount is null),
            duplicatesResolved = ds.DuplicateDevices, volumesExcludedFromComputerCounts = true
        }, FilterLimitations(f));
    }

    public Envelope ListDevices(Dataset ds, Caller caller, string scope, InventoryFilter filter, int pageSize, string? cursor)
    {
        var f = filter.Normalize();
        var rows = Filter(ds, f);
        var binding = Wire.Encode(new { tool = "list_devices", filter = f, pageSize });
        return Wrap(ds, Page(rows, ds, caller, scope, pageSize, cursor, binding), FilterLimitations(f));
    }

    public Envelope Details(Dataset ds, string entityId)
    {
        var id = Guard.Entity(entityId);
        var device = ds.Snapshot.Devices.SingleOrDefault(d => d.EntityId == id);
        if (device is null) throw new QueryFault("NOT_FOUND", "Entity not found.");
        return Wrap(ds, new { device, volumes = ds.Snapshot.Volumes.Where(v => v.ParentEntityId == id).ToArray(),
            managementGroup = ds.Snapshot.ManagementGroup });
    }

    public Envelope Utilization(Dataset ds, string metric, string? entityId, string window, string statistic,
        int top, bool comparePrevious, string? sortOrder, double? thresholdPercent, string? comparison)
    {
        ValidateUtilization(metric, entityId, window, statistic, top, sortOrder, thresholdPercent, comparison);
        var id = entityId is null ? null : Guard.Entity(entityId);
        EnsureEntity(ds, id);
        var span = Window(window);
        var end = ds.Snapshot.CollectedAtUtc;
        var start = end - span;
        var requested = metric == "diskFreePercent"
            ? ds.Snapshot.Volumes.Where(v => id is null || v.EntityId == id || v.ParentEntityId == id)
                .Select(v => (v.EntityId, Parent: (string?)v.ParentEntityId, Kind: "volume", DeviceId: (string?)v.DeviceId)).ToArray()
            : ds.Snapshot.Devices.Where(d => id is null || d.EntityId == id)
                .Select(d => (d.EntityId, Parent: (string?)null, Kind: "computer", DeviceId: (string?)null)).ToArray();
        var rows = requested.Select(e =>
        {
            var current = Observations(ds, e.EntityId, metric, start, end);
            var prior = comparePrevious ? Observations(ds, e.EntityId, metric, start - span, start) : [];
            var value = Aggregate(current, statistic);
            var previous = comparePrevious ? Aggregate(prior, statistic) : null;
            return new UtilizationRow(e.EntityId, e.Parent, e.Kind, e.DeviceId, metric, value, previous,
                value is not null && previous is not null ? value - previous : null,
                SampleCoverageOf(current, start, end), comparePrevious ? SampleCoverageOf(prior, start - span, start) : null,
                current.FirstOrDefault()?.Metric, comparePrevious ? prior.FirstOrDefault()?.Metric : null);
        }).ToArray();
        var available = rows.Where(r => r.Value is not null).ToArray();
        if (available.Length == 0) throw new QueryFault("SOURCE_UNAVAILABLE", "NO_USABLE_OBSERVATIONS_FOR_REQUESTED_METRIC_WINDOW");
        var matching = available.Where(r => thresholdPercent is null ||
            (comparison == "lt" ? r.Value < thresholdPercent : r.Value > thresholdPercent)).ToArray();
        var ordered = sortOrder == "asc"
            ? matching.OrderBy(r => r.Value).ThenBy(r => r.EntityId, StringComparer.Ordinal)
            : matching.OrderByDescending(r => r.Value).ThenBy(r => r.EntityId, StringComparer.Ordinal);
        var limitations = new List<string>
        {
            "MEAN_IS_ARITHMETIC_MEAN_OF_RETAINED_SCOM_OBSERVATIONS_NOT_TIME_WEIGHTED",
            "RETAINED_OBSERVATIONS_DO_NOT_ESTABLISH_CONTINUOUS_FULL_WINDOW_COVERAGE"
        };
        if (rows.Any(r => r.Coverage.FirstSampleUtc > start || r.Value is null))
            limitations.Add("PARTIAL_WINDOW_OR_MISSING_ENTITY_OBSERVATIONS");
        if (rows.Any(r => r.Coverage.AgeSeconds > settings.FreshMinutes * 60))
            limitations.Add("LATEST_METRIC_OBSERVATION_IS_STALE");
        if (comparePrevious && rows.Any(r => r.PreviousValue is null))
            limitations.Add("PREVIOUS_WINDOW_UNAVAILABLE_FOR_SOME_ENTITIES");
        if (rows.Any(r => r.SourceMetric == "memoryAvailableMiB" || r.PreviousSourceMetric == "memoryAvailableMiB"))
            limitations.Add("MEMORY_USED_PERCENT_DERIVED_FROM_AVAILABLE_MIB_WHERE_NATIVE_PERCENT_ABSENT");
        if (comparePrevious && rows.Any(r => r.SourceMetric is not null && r.PreviousSourceMetric is not null &&
            r.SourceMetric != r.PreviousSourceMetric))
            limitations.Add("CURRENT_AND_PREVIOUS_MEMORY_WINDOWS_USE_DIFFERENT_SOURCE_METRICS");
        return Wrap(ds, new
        {
            metric, window, statistic, windowStartUtc = start, windowEndUtc = end,
            previousWindowStartUtc = comparePrevious ? (DateTimeOffset?)(start - span) : null,
            consideredCount = rows.Length, availableCount = available.Length, missingCount = rows.Length - available.Length,
            matchedCount = matching.Length, returnedCount = Math.Min(top, matching.Length),
            missingEntityIds = rows.Where(r => r.Value is null).Select(r => r.EntityId).ToArray(),
            rows = ordered.Take(top).ToArray(), top, sortOrder = sortOrder ?? "desc"
        }, limitations, forcePartial: true);
    }

    public Envelope Capacity(Dataset ds, Caller caller, string scope, string? entityId, string resource,
        string? groupId, double? minFreePercent, double? maxFreePercent, int pageSize, string? cursor)
    {
        ValidateCapacity(entityId, resource, groupId, minFreePercent, maxFreePercent, pageSize);
        var id = entityId is null ? null : Guard.Entity(entityId);
        EnsureEntity(ds, id);
        var computers = ds.Snapshot.Devices.Where(d => groupId is null || d.GroupIds.Contains(groupId)).ToArray();
        var computerIds = computers.Select(d => d.EntityId).ToHashSet();
        var end = ds.Snapshot.CollectedAtUtc;
        // Anchor data selection to the immutable snapshot, not a moving paging-request clock.
        var start = end.AddMinutes(-settings.FreshMinutes);
        var rows = resource == "memory"
            ? computers.Where(d => id is null || d.EntityId == id).Select(d =>
            {
                var total = d.MemoryTotalMiB;
                var native = LatestObservation(RawObservations(ds, d.EntityId, "memoryUsedPercent", start, end));
                if (native is not null)
                {
                    var used = total * native.Value / 100;
                    return new CapacityRow(d.EntityId, null, "computer", null, total,
                        used, total - used, 100 - native.Value, native.ObservedAtUtc, native.Metric, native.Optimized);
                }
                var available = LatestObservation(RawObservations(ds, d.EntityId, "memoryAvailableMiB", start, end));
                var free = available?.Value;
                return new CapacityRow(d.EntityId, null, "computer", null, total,
                    total is not null && free is not null ? total - free : null, free,
                    total is not null && free is not null ? free / total * 100 : null,
                    available?.ObservedAtUtc, available?.Metric, available?.Optimized);
            }).ToImmutableArray()
            : ds.Snapshot.Volumes.Where(v => computerIds.Contains(v.ParentEntityId) &&
                    (id is null || v.EntityId == id || v.ParentEntityId == id))
                .Select(v =>
                {
                    var sample = LatestObservation(RawObservations(ds, v.EntityId, "diskFreePercent", start, end));
                    var percent = sample?.Value;
                    var free = percent * v.SizeMiB / 100;
                    return new CapacityRow(v.EntityId, v.ParentEntityId, "volume", v.DeviceId, v.SizeMiB,
                        v.SizeMiB - free, free, percent, sample?.ObservedAtUtc, sample?.Metric, sample?.Optimized);
                }).ToImmutableArray();
        var matched = rows.Where(r => (minFreePercent is null || r.FreePercent >= minFreePercent) &&
            (maxFreePercent is null || r.FreePercent <= maxFreePercent)).OrderBy(r => r.EntityId, StringComparer.Ordinal).ToImmutableArray();
        var complete = matched.Where(r => r.TotalMiB is not null && r.UsedMiB is not null && r.FreeMiB is not null).ToArray();
        var binding = Wire.Encode(new { tool = "get_capacity_summary", id, resource, groupId, minFreePercent, maxFreePercent, pageSize });
        static double? Sum(IEnumerable<double?> values)
        {
            var known = values.Where(v => v is not null).Select(v => v!.Value).ToArray();
            return known.Length == 0 ? null : known.Sum();
        }
        var usageAvailable = rows.Count(r => r.ObservedAtUtc is not null);
        var staleUsage = rows.Any(r => r.ObservedAtUtc is not null &&
            clock.GetUtcNow() - r.ObservedAtUtc.Value > TimeSpan.FromMinutes(settings.FreshMinutes));
        var capacityLimitations = new List<string>();
        if (rows.Any(r => r.UsedMiB is null || r.FreeMiB is null))
            capacityLimitations.Add("CAPACITY_USAGE_UNKNOWN_WHERE_TOTAL_OR_RECENT_SAMPLE_MISSING");
        if (staleUsage) capacityLimitations.Add("CAPACITY_OBSERVATIONS_STALE_AT_REQUEST_TIME_VALUES_ARE_AS_OF_SNAPSHOT");
        return Wrap(ds, new
        {
            resource, consideredRowCount = rows.Length, matchingRowCount = matched.Length,
            observationsAsOfUtc = end, observationCutoffUtc = start,
            usageStatus = usageAvailable == 0 ? "unavailable" : usageAvailable < rows.Length || staleUsage ? "partial" : "ok",
            usageObservationAvailableCount = usageAvailable, usageObservationMissingCount = rows.Length - usageAvailable,
            matchingVolumeCount = resource == "disk" ? matched.Length : 0,
            distinctMachineCount = matched.Select(r => r.ParentEntityId ?? r.EntityId).Distinct().Count(),
            consideredMachineCount = rows.Select(r => r.ParentEntityId ?? r.EntityId).Distinct().Count(),
            unknownTotalCount = rows.Count(r => r.TotalMiB is null),
            unknownFreePercentCount = rows.Count(r => r.FreePercent is null),
            unknownUsedOrFreeCount = rows.Count(r => r.UsedMiB is null || r.FreeMiB is null),
            knownTotalMiB = Sum(matched.Select(r => r.TotalMiB)), knownTotalRowCount = matched.Count(r => r.TotalMiB is not null),
            comparableCapacity = new { rowCount = complete.Length, totalMiB = Sum(complete.Select(r => r.TotalMiB)),
                usedMiB = Sum(complete.Select(r => r.UsedMiB)), freeMiB = Sum(complete.Select(r => r.FreeMiB)) },
            page = Page(matched, ds, caller, scope, pageSize, cursor, binding)
        }, capacityLimitations, forcePartial: rows.IsEmpty || rows.Any(r => r.UsedMiB is null || r.FreeMiB is null));
    }

    public Envelope Error(QueryFault error, Dataset? ds = null)
    {
        if (ds is not null)
            return Wrap(ds, new { errorCode = error.Code, message = error.Message }) with { Status = "unavailable" };
        return new Envelope("1.0", "unavailable", "scom", null, new ScopeInfo(Wire.ScopeId, "Approved SCOM computers"),
            new { errorCode = error.Code, message = error.Message }, null, null, null,
            BaseLimitations.ToImmutableArray(), [], Guid.NewGuid().ToString("D"));
    }

    public static void ValidateUtilization(string metric, string? entityId, string window, string statistic,
        int top, string? sortOrder, double? thresholdPercent, string? comparison)
    {
        Guard.Argument(metric is "cpuPercent" or "memoryUsedPercent" or "diskFreePercent", "Invalid metric.");
        Guard.Argument(window is "1h" or "24h" or "7d", "window must be 1h, 24h, or 7d.");
        Guard.Argument(statistic is "mean" or "max" or "latest", "statistic must be mean, max, or latest.");
        Guard.Argument(top is >= 1 and <= 20, "top must be 1..20.");
        Guard.Argument(sortOrder is null or "asc" or "desc", "sortOrder must be asc or desc.");
        Guard.Argument(thresholdPercent is null || double.IsFinite(thresholdPercent.Value) &&
            thresholdPercent is >= 0 and <= 100, "thresholdPercent must be 0..100.");
        Guard.Argument((thresholdPercent is null && comparison is null) ||
            (thresholdPercent is not null && comparison is "lt" or "gt"), "Supply thresholdPercent and comparison lt/gt together.");
        if (entityId is not null) Guard.Entity(entityId);
    }

    public static void ValidateCapacity(string? entityId, string resource, string? groupId,
        double? minFreePercent, double? maxFreePercent, int pageSize)
    {
        Guard.Argument(resource is "memory" or "disk", "resource must be memory or disk.");
        Guard.Argument(groupId is null || groupId.Length is > 0 and <= 100, "Invalid groupId.");
        Guard.Argument((minFreePercent is null || double.IsFinite(minFreePercent.Value) && minFreePercent is >= 0 and <= 100) &&
            (maxFreePercent is null || double.IsFinite(maxFreePercent.Value) && maxFreePercent is >= 0 and <= 100) &&
            (minFreePercent is null || maxFreePercent is null || minFreePercent <= maxFreePercent),
            "Free-percent bounds must be 0..100 and min <= max; bounds are inclusive.");
        ValidatePageSize(pageSize);
        if (entityId is not null) Guard.Entity(entityId);
    }

    public static void ValidatePageSize(int pageSize) => Guard.Argument(pageSize is >= 1 and <= 50, "pageSize must be 1..50.");

    private Envelope Wrap(Dataset ds, object data, IEnumerable<string>? extra = null, bool forcePartial = false)
    {
        var s = ds.Snapshot;
        var age = Math.Max(0, (clock.GetUtcNow() - s.CollectedAtUtc).TotalSeconds);
        var fresh = age <= settings.FreshMinutes * 60;
        var baseLimitations = ds.StorageMode == "indexed"
            ? BaseLimitations.Skip(1).Prepend("ONLY_APPROVED_WINDOWS_VIEW_NOT_ALL_MONITORED_OBJECTS")
            : BaseLimitations.AsEnumerable();
        var limitations = baseLimitations.Concat(s.Limitations).Concat(ds.ValidationLimitations)
            .Concat(extra ?? []).Concat(fresh ? [] : new[] { "SNAPSHOT_STALE" }).Distinct().ToImmutableArray();
        var partial = forcePartial || !fresh || s.Status == "partial" || s.Devices.Length != s.ExpectedDeviceCount ||
            ds.ValidationLimitations.Length != 0 || (extra?.Any() ?? false);
        return new Envelope("1.0", partial ? "partial" : "ok", "scom", s.DatasetId, s.Scope, data,
            s.CollectedAtUtc, new Freshness(age, fresh ? "fresh" : "stale"),
            new Coverage(s.ExpectedDeviceCount, s.Devices.Length, s.Devices.Length == s.ExpectedDeviceCount, s.CollectionStartedAtUtc),
            limitations, s.SourceRefs, Guid.NewGuid().ToString("D"));
    }

    private PageData<T> Page<T>(ImmutableArray<T> rows, Dataset ds, Caller caller, string scope, int pageSize, string? cursor, string filter)
    {
        ValidatePageSize(pageSize);
        var offset = cursors.Read(cursor, caller, scope, ds, filter);
        if (offset > rows.Length) throw new QueryFault("INVALID_CURSOR", "Cursor offset is invalid.");
        var page = rows.Skip(offset).Take(pageSize).ToImmutableArray();
        var next = offset + page.Length < rows.Length
            ? cursors.Create(caller, scope, ds, filter, offset + page.Length) : null;
        return new PageData<T>(page, rows.Length, pageSize, next);
    }

    private static ImmutableArray<Device> Filter(Dataset ds, InventoryFilter f) =>
        ds.Snapshot.Devices.Where(f.Matches).OrderBy(d => d.Hostname, StringComparer.Ordinal)
            .ThenBy(d => d.EntityId, StringComparer.Ordinal).ToImmutableArray();
    private static string[] FilterLimitations(InventoryFilter f) =>
        f.EntityKind == "networkdevice" || f.OsFamily == "linux"
            ? ["REQUESTED_ENTITY_CLASS_OR_OS_IS_NOT_SUPPORTED_BY_THIS_SCOPE"] : [];
    private static TimeSpan Window(string window) => window == "1h" ? TimeSpan.FromHours(1) :
        window == "24h" ? TimeSpan.FromHours(24) : TimeSpan.FromDays(7);
    private static void EnsureEntity(Dataset ds, string? id)
    {
        if (id is not null && !ds.Snapshot.Devices.Any(d => d.EntityId == id) &&
            !ds.Snapshot.Volumes.Any(v => v.EntityId == id))
            throw new QueryFault("NOT_FOUND", "Entity not found.");
    }

    private static Sample[] Observations(Dataset ds, string entity, string metric, DateTimeOffset start, DateTimeOffset end)
    {
        var samples = RawObservations(ds, entity, metric, start, end);
        // Prefer the native series per entity/window; never blend it with derived fallback observations.
        if (metric != "memoryUsedPercent" || samples.Length != 0) return samples;
        var index = Index(ds);
        var total = index.Devices.GetValueOrDefault(entity)?.MemoryTotalMiB;
        return total is null ? [] : RawObservations(ds, entity, "memoryAvailableMiB", start, end)
            .Select(s => s with { Value = (total.Value - s.Value) / total.Value * 100 }).ToArray();
    }
    private static Sample[] RawObservations(Dataset ds, string entity, string metric, DateTimeOffset start, DateTimeOffset end) =>
        Index(ds).Series.GetValueOrDefault((entity, metric), []).Where(s =>
            s.ObservedAtUtc > start && s.ObservedAtUtc <= end).ToArray();
    private static ObservationIndex Index(Dataset ds) =>
        ObservationIndexes.GetValue(ds.Snapshot, snapshot => new(() => new(snapshot))).Value;

    private sealed class ObservationIndex(Snapshot snapshot)
    {
        public FrozenDictionary<(string Entity, string Metric), ImmutableArray<Sample>> Series { get; } =
            snapshot.Samples.GroupBy(s => (s.EntityId, s.Metric))
                .ToFrozenDictionary(group => group.Key, group => group.ToImmutableArray());
        public FrozenDictionary<string, Device> Devices { get; } =
            snapshot.Devices.ToFrozenDictionary(device => device.EntityId, StringComparer.Ordinal);
    }
    private static double? Aggregate(Sample[] samples, string statistic) => samples.Length == 0 ? null :
        statistic == "max" ? samples.Max(s => s.Value) :
        statistic == "mean" ? samples.Average(s => s.Value) :
        LatestObservation(samples)!.Value;
    private static Sample? LatestObservation(Sample[] samples) =>
        samples.OrderByDescending(s => s.ObservedAtUtc).ThenBy(s => s.CollectionRuleId, StringComparer.Ordinal).FirstOrDefault();
    private SampleCoverage SampleCoverageOf(Sample[] samples, DateTimeOffset start, DateTimeOffset end) =>
        samples.Length == 0 ? new(null, null, 0, null, false, 0, 0) :
        new(samples.Min(s => s.ObservedAtUtc), samples.Max(s => s.ObservedAtUtc), samples.Length,
            Math.Max(0, (clock.GetUtcNow() - samples.Max(s => s.ObservedAtUtc)).TotalSeconds), false,
            samples.Count(s => s.Optimized), samples.Count(s => !s.Optimized));
}
