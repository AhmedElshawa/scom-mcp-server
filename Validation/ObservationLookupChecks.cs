using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using ScomMcp;

static class ObservationLookupChecks
{
    public static void Run(Dataset baseline, SourceSettings source, TimeProvider clock)
    {
        var assertions = 0;
        var engine = new QueryEngine(source, clock, new CursorCodec(clock, source));
        foreach (var metric in new[] { "cpuPercent", "memoryUsedPercent", "diskFreePercent" })
        foreach (var window in new[] { "1h", "24h", "7d" })
        foreach (var statistic in new[] { "latest", "mean", "max" })
        {
            var data = JsonSerializer.SerializeToElement(engine.Utilization(baseline, metric, null, window,
                statistic, 20, true, null, null, null).Data, Wire.Json);
            var span = window == "1h" ? TimeSpan.FromHours(1) : window == "24h" ?
                TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
            var end = baseline.Snapshot.CollectedAtUtc;
            foreach (var row in data.GetProperty("rows").EnumerateArray())
            {
                var entity = row.GetProperty("entityId").GetString()!;
                var current = Reference(entity, metric, end - span, end);
                var prior = Reference(entity, metric, end - span - span, end - span);
                Equal(Aggregate(current, statistic), Number(row, "value"), "raw current arithmetic");
                Equal(Aggregate(prior, statistic), Number(row, "previousValue"), "raw previous arithmetic");
                Equal(current.FirstOrDefault()?.Metric, row.GetProperty("sourceMetric").GetString(), "native/fallback selection");
                Equal(current.Length, row.GetProperty("coverage").GetProperty("sampleCount").GetInt32(), "raw sample count");
                Equal(current.Count(s => s.Optimized),
                    row.GetProperty("coverage").GetProperty("optimizedSampleCount").GetInt32(), "optimization flags retained");
            }
        }

        var now = baseline.Snapshot.CollectedAtUtc;
        var devices = Enumerable.Range(1, 10000).Select(i => baseline.Snapshot.Devices[0] with
        {
            EntityId = $"10000000-0000-0000-0000-{i:D12}",
            SourceObjectId = $"10000000-0000-0000-0000-{i:D12}",
            Hostname = $"synthetic-{i:D5}.invalid"
        }).ToImmutableArray();
        var samples = devices.SelectMany(d => new[]
        {
            new Sample(d.EntityId, "cpuPercent", now.AddMinutes(-90), 10, "syntheticPrior", false),
            new Sample(d.EntityId, "cpuPercent", now.AddMinutes(-60), 20, "syntheticBoundary", true),
            new Sample(d.EntityId, "cpuPercent", now.AddMinutes(-30), 0, "syntheticZero", false),
            new Sample(d.EntityId, "cpuPercent", now, 40, "syntheticLatest", true),
            new Sample(d.EntityId, "cpuPercent", now.AddTicks(1), 99, "syntheticFuture", false)
        }).ToImmutableArray();
        var large = baseline with
        {
            Snapshot = baseline.Snapshot with { Devices = devices, Volumes = [], Samples = samples, ExpectedDeviceCount = devices.Length }
        };
        var watch = Stopwatch.StartNew();
        var largeData = JsonSerializer.SerializeToElement(engine.Utilization(large, "cpuPercent", null,
            "1h", "mean", 20, true, null, null, null).Data, Wire.Json);
        watch.Stop();
        Equal(10000, largeData.GetProperty("availableCount").GetInt32(), "all computers considered before top");
        foreach (var row in largeData.GetProperty("rows").EnumerateArray())
        {
            Equal(20d, Number(row, "value"), "exclusive start and inclusive end preserve zero");
            Equal(15d, Number(row, "previousValue"), "boundary belongs only to prior window");
        }
        if (watch.Elapsed > TimeSpan.FromSeconds(5))
            throw new InvalidOperationException("Synthetic 10,000-computer/50,000-sample utilization exceeded five seconds.");
        assertions++;
        var replaced = large with { Snapshot = large.Snapshot with { Samples = samples.Select(s => s with { Value = 50 }).ToImmutableArray() } };
        var replacedData = JsonSerializer.SerializeToElement(engine.Utilization(replaced, "cpuPercent", null,
            "1h", "mean", 20, false, null, null, null).Data, Wire.Json);
        Equal(50d, Number(replacedData.GetProperty("rows")[0], "value"), "replacement snapshot never reuses prior index");
        var total = devices[0].MemoryTotalMiB ??
            throw new InvalidOperationException("Synthetic memory scale test requires known capacity.");
        var memory = large with
        {
            Snapshot = large.Snapshot with
            {
                Samples = devices.Select(d => new Sample(d.EntityId, "memoryAvailableMiB", now,
                    total / 4, "syntheticAvailable", false)).Append(new Sample(devices[0].EntityId,
                    "memoryUsedPercent", now, 10, "syntheticNative", true)).ToImmutableArray()
            }
        };
        var memoryData = JsonSerializer.SerializeToElement(engine.Utilization(memory, "memoryUsedPercent", null,
            "1h", "mean", 20, false, "asc", null, null).Data, Wire.Json);
        Equal(10d, Number(memoryData.GetProperty("rows")[0], "value"), "native memory wins over available MiB");
        Equal(75d, Number(memoryData.GetProperty("rows")[1], "value"), "other entities retain derived fallback");
        var caller = new Caller("synthetic-observation-lookup", ImmutableHashSet.Create(Wire.ScopeId));
        var capacity = JsonSerializer.SerializeToElement(engine.Capacity(memory, caller, Wire.ScopeId,
            null, "memory", null, null, null, 50, null).Data, Wire.Json);
        Equal(10000, capacity.GetProperty("consideredMachineCount").GetInt32(), "capacity examines all entities");
        Equal(total * devices.Length, Number(capacity, "knownTotalMiB"), "capacity keeps known totals");
        Console.WriteLine($"PASS: {assertions} observation lookup assertions; raw-reference parity and 10,000 computers/50,000 samples in {watch.Elapsed.TotalMilliseconds:F1} ms (synthetic, not a production SLA).");

        Sample[] Reference(string entity, string metric, DateTimeOffset start, DateTimeOffset end)
        {
            var selected = baseline.Snapshot.Samples.Where(s => s.EntityId == entity && s.Metric == metric &&
                s.ObservedAtUtc > start && s.ObservedAtUtc <= end).ToArray();
            if (metric != "memoryUsedPercent" || selected.Length != 0) return selected;
            var total = baseline.Snapshot.Devices.SingleOrDefault(d => d.EntityId == entity)?.MemoryTotalMiB;
            return total is null ? [] : baseline.Snapshot.Samples.Where(s => s.EntityId == entity &&
                s.Metric == "memoryAvailableMiB" && s.ObservedAtUtc > start && s.ObservedAtUtc <= end)
                .Select(s => s with { Value = (total.Value - s.Value) / total.Value * 100 }).ToArray();
        }
        void Equal<T>(T expected, T actual, string reason)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(reason);
            assertions++;
        }
    }

    private static double? Number(JsonElement row, string name) =>
        row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetDouble();
    private static double? Aggregate(Sample[] values, string statistic) =>
        values.Length == 0 ? null : statistic == "mean" ? values.Average(s => s.Value) :
        statistic == "max" ? values.Max(s => s.Value) :
        values.OrderByDescending(s => s.ObservedAtUtc).ThenBy(s => s.CollectionRuleId, StringComparer.Ordinal).First().Value;
}
