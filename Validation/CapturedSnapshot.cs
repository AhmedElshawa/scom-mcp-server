using System.Collections.Immutable;
using ScomMcp;

static class CapturedSnapshot
{
    public static async Task<int> Run(string[] args, TimeProvider clock, TextWriter output, TextWriter error)
    {
        if (args.Length != 4 || args[0] != "--snapshot-directory")
        {
            await error.WriteLineAsync("Usage: --snapshot-directory <absolute-directory> <approved-hostname-1> <approved-hostname-2>");
            return 2;
        }
        var settings = new SourceSettings
        {
            Directory = args[1], AllowedHostnames = [args[2], args[3]], MaxSnapshotBytes = 10000000
        };
        try { settings.Validate(); }
        catch (InvalidOperationException)
        {
            await error.WriteLineAsync("Invalid source configuration: require an absolute directory and two distinct exact approved hostnames.");
            return 2;
        }
        try
        {
            var dataset = await new SnapshotReader(settings, clock).ReadAsync();
            var engine = new QueryEngine(settings, clock, new CursorCodec(clock, settings));
            // A local query binding only; this mode neither starts HTTP nor tests authentication.
            var caller = new Caller("captured-snapshot-validation", ImmutableHashSet.Create(Wire.ScopeId));
            ToolResponse Query(Func<Envelope> action)
            {
                try { return new(action()); }
                catch (QueryFault fault) { return new(engine.Error(fault, dataset), true); }
            }
            var queries = new Dictionary<string, ToolResponse>
            {
                ["get_source_status"] = Query(() => engine.SourceStatus(dataset)),
                ["get_inventory_summary"] = Query(() => engine.Inventory(dataset, new())),
                ["list_devices"] = Query(() => engine.ListDevices(dataset, caller, Wire.ScopeId, new(), 50, null))
            };
            foreach (var device in dataset.Snapshot.Devices)
                queries["get_device_details:" + device.EntityId] = Query(() => engine.Details(dataset, device.EntityId));
            foreach (var metric in new[] { "cpuPercent", "memoryUsedPercent", "diskFreePercent" })
                queries["get_utilization:" + metric] = Query(() =>
                    engine.Utilization(dataset, metric, null, "1h", "latest", 20, true, null, null, null));
            foreach (var resource in new[] { "memory", "disk" })
                queries["get_capacity_summary:" + resource] = Query(() =>
                    engine.Capacity(dataset, caller, Wire.ScopeId, null, resource, null, null, null, 50, null));
            await output.WriteLineAsync(Wire.Encode(new
            {
                mode = "captured-snapshot-read-only",
                validatedAtUtc = clock.GetUtcNow(),
                snapshotSha256 = dataset.Fingerprint,
                approvedHostnames = settings.AllowedHostnames,
                maxSnapshotBytes = settings.MaxSnapshotBytes,
                freshMinutes = settings.FreshMinutes,
                unavailableMinutes = settings.UnavailableMinutes,
                authenticationValidated = false,
                liveScomConnectivityValidated = false,
                observationsByRule = dataset.Snapshot.Samples
                    .GroupBy(s => new { s.EntityId, s.Metric, s.CollectionRuleId, s.Optimized })
                    .OrderBy(g => g.Key.EntityId, StringComparer.Ordinal)
                    .ThenBy(g => g.Key.Metric, StringComparer.Ordinal)
                    .ThenBy(g => g.Key.CollectionRuleId, StringComparer.Ordinal)
                    .ThenBy(g => g.Key.Optimized)
                    .Select(g => new
                    {
                        g.Key.EntityId, g.Key.Metric, g.Key.CollectionRuleId, g.Key.Optimized,
                        sampleCount = g.Count(),
                        firstObservedAtUtc = g.Min(s => s.ObservedAtUtc),
                        lastObservedAtUtc = g.Max(s => s.ObservedAtUtc)
                    }).ToArray(),
                queries
            }));
            return 0;
        }
        catch (QueryFault fault)
        {
            await error.WriteLineAsync(Wire.Encode(new { errorCode = fault.Code, reason = fault.Message }));
            return 1;
        }
    }
}
