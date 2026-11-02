using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ScomMcp;

static class IndexedWindowsChecks
{
    private const string Publisher = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private static int assertions;

    public static async Task Run(string root, Dataset baseline)
    {
        baseline = baseline with { DuplicateDevices = 0, DuplicateVolumes = 0, DuplicateSamples = 0 };
        var now = baseline.Snapshot.CollectedAtUtc;
        var clock = new Clock { Now = now };
        var settings = new ReadModelSettings
        {
            Enabled = true, WindowsToolsEnabled = true, DatabasePath = Path.Combine(root, "indexed-windows.db"),
            ManagementGroupId = baseline.Snapshot.ManagementGroup.Id, ScopeKind = "managementGroup",
            PublisherObjectIds = [Publisher],
            AllowedMetricsByClass = new()
            {
                ["Synthetic.WindowsComputer"] = new()
                {
                    ["cpuPercent"] = "percent", ["memoryUsedPercent"] = "percent", ["memoryAvailableMiB"] = "MiB"
                },
                ["Synthetic.WindowsVolume"] = new() { ["diskFreePercent"] = "percent" }
            }
        };
        var source = new SourceSettings { SnapshotEnabled = false };
        var auth = new AuthSettings
        {
            TenantId = "11111111-1111-4111-8111-111111111111", Audiences = ["api://synthetic-windows"],
            RequiredScope = "Scom.Read",
            Callers = [new() { ObjectId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", Scopes = [Wire.ScopeId] }]
        };
        Throws(() => Hosting.AddScomServices(new ServiceCollection(), auth, new()
        {
            SnapshotEnabled = true, Directory = root,
            AllowedHostnames = baseline.Snapshot.Devices.Select(d => d.Hostname).ToArray()
        }, settings), "mixed legacy/indexed Windows startup rejected");
        Throws(() => Hosting.AddScomServices(new ServiceCollection(), auth,
            new() { SnapshotEnabled = false, FreshMinutes = 10 }, settings),
            "different legacy engine/indexed freshness policies rejected");
        var store = new ReadModelStore(settings, source, clock);
        var manifest = new ReadModelManifest(settings.ManagementGroupId, "managementGroup", null, true,
            baseline.Snapshot.Devices.Select(d => d.EntityId).Concat(baseline.Snapshot.Volumes.Select(v => v.EntityId)).ToImmutableArray(),
            new(now, true, null), new(now, true, null), new(now, true, null), now,
            baseline.Snapshot.Devices.Select(d => d.EntityId).ToImmutableArray());
        var objects = baseline.Snapshot.Devices.Select(d => new ObservedObject(d.EntityId, "Synthetic.WindowsComputer", [],
            d.Hostname, d.Health, now, ImmutableDictionary<string, string?>.Empty))
            .Concat(baseline.Snapshot.Volumes.Select(v => new ObservedObject(v.EntityId, "Synthetic.WindowsVolume", [],
                v.DeviceId, "unknown", now, ImmutableDictionary<string, string?>.Empty))).ToImmutableArray();
        var metrics = baseline.Snapshot.Samples.Select(s => new ObservedMetric(Guid.NewGuid().ToString("D"), s.EntityId,
            s.Metric, s.Metric == "memoryAvailableMiB" ? "MiB" : "percent", s.ObservedAtUtc, s.Value, s.CollectionRuleId, s.Optimized))
            .ToImmutableArray();
        var batch = new IngestionBatch(objects, [], [], metrics,
            new(baseline.Snapshot.Devices, baseline.Snapshot.Volumes));
        string Activate(IngestionBatch value, ReadModelManifest provenance)
        {
            var id = Guid.NewGuid().ToString("D");
            store.Begin(Publisher, id);
            store.Append(Publisher, id, 0, Bytes(value));
            store.Commit(Publisher, id, Bytes(provenance));
            return id;
        }
        var firstId = Activate(batch, manifest);
        await ReadModelChecks.Http(root, batch, manifest, windowsTools: true);
        var projected = store.ReadWindowsDataset();
        Check(projected.StorageMode == "indexed" && projected.Snapshot.DatasetId == firstId, "indexed reader returns activated identity");
        Check(ReferenceEquals(projected, store.ReadWindowsDataset()), "validated Windows view cached per dataset");
        var engine = new QueryEngine(source, clock, new CursorCodec(clock, source));
        var caller = new Caller("synthetic", ImmutableHashSet.Create(Wire.ScopeId));
        Check(Json(engine.Inventory(projected, new()).Data) == Json(engine.Inventory(baseline, new()).Data),
            "legacy inventory counts/filters retain exact contract");
        foreach (var metric in new[] { "cpuPercent", "memoryUsedPercent", "diskFreePercent" })
        foreach (var window in new[] { "1h", "24h", "7d" })
        foreach (var statistic in new[] { "latest", "mean", "max" })
        {
            var original = engine.Utilization(baseline, metric, null, window, statistic, 20, true, null, null, null);
            var indexed = engine.Utilization(projected, metric, null, window, statistic, 20, true, null, null, null);
            Check(Json(original.Data) == Json(indexed.Data), "indexed utilization math/window/previous/native fallback parity");
        }
        foreach (var kind in new[] { "memory", "disk" })
            Check(Json(engine.Capacity(baseline, caller, Wire.ScopeId, null, kind, null, null, null, 50, null).Data) ==
                Json(engine.Capacity(projected, caller, Wire.ScopeId, null, kind, null, null, null, 50, null).Data),
                "indexed capacity totals and missing-data parity");
        var first = JsonSerializer.SerializeToElement(engine.ListDevices(projected, caller, Wire.ScopeId, new(), 1, null).Data, Wire.Json);
        var cursor = first.GetProperty("nextCursor").GetString();
        var recovered = new ReadModelStore(settings, source, clock);
        Check(recovered.ReadWindowsDataset().Fingerprint == projected.Fingerprint, "persisted projection fingerprint stable across reopen");
        Activate(batch, manifest);
        Fault(() => engine.ListDevices(store.ReadWindowsDataset(), caller, Wire.ScopeId, new(), 1, cursor),
            "CURSOR_DATASET_CHANGED", "Windows paging invalidates after activation");
        var third = Guid.NewGuid().ToString("D");
        var expectedThree = manifest with
        {
            ExpectedObjectIds = manifest.ExpectedObjectIds.Add(third),
            ExpectedComputerIds = manifest.ExpectedComputerIds!.Value.Add(third),
            Inventory = new(now, false, "MISSING_COMPUTER")
        };
        Activate(batch, expectedThree);
        var partial = engine.SourceStatus(store.ReadWindowsDataset());
        Check(partial.Coverage!.ExpectedDeviceCount == 3 && partial.Coverage.UniqueDeviceCount == 2 &&
            !partial.Coverage.Complete && partial.Status == "partial", "variable expected membership remains explicit partial");
        var threeBatch = batch with
        {
            Objects = batch.Objects.Add(new(third, "Synthetic.WindowsComputer", [], "third.synthetic.invalid", "unknown", now,
                ImmutableDictionary<string, string?>.Empty)),
            WindowsInventory = batch.WindowsInventory! with
            {
                Devices = batch.WindowsInventory!.Devices.Add(baseline.Snapshot.Devices[0] with
                {
                    EntityId = third, SourceObjectId = third, Hostname = "third.synthetic.invalid", Health = "unknown"
                })
            }
        };
        Activate(threeBatch, expectedThree with { Inventory = new(now, true, null) });
        Check(store.ReadWindowsDataset().Snapshot.Devices.Length == 3, "indexed path is not limited to two computers");
        var pending = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, pending);
        Fault(() => store.ReadWindowsDataset(), "SOURCE_UNAVAILABLE", "staging invalidates cached Windows success");
        store.Append(Publisher, pending, 0, Bytes(batch with
        {
            WindowsInventory = batch.WindowsInventory! with
            {
                Volumes = batch.WindowsInventory!.Volumes.SetItem(0,
                    batch.WindowsInventory.Volumes[0] with { ParentEntityId = third })
            }
        }));
        Fault(() => store.Commit(Publisher, pending, Bytes(manifest)), "SOURCE_UNAVAILABLE", "out-of-projection volume parent rejected");
        store.Fail(Publisher, pending);
        var native = batch with
        {
            Metrics = batch.Metrics.Add(new(Guid.NewGuid().ToString("D"), baseline.Snapshot.Devices[0].EntityId,
                "memoryUsedPercent", "percent", now.AddMinutes(-1), 12, "syntheticNative", true))
        };
        Activate(native, manifest);
        Check(new ReadModelStore(settings, source, clock).ReadWindowsDataset().Fingerprint ==
            store.ReadWindowsDataset().Fingerprint, "normalization preserves projection fingerprint across native-metric reopen");
        var nativeRows = JsonSerializer.SerializeToElement(engine.Utilization(store.ReadWindowsDataset(), "memoryUsedPercent",
            baseline.Snapshot.Devices[0].EntityId, "1h", "latest", 1, false, null, null, null).Data, Wire.Json);
        Check(nativeRows.GetProperty("rows")[0].GetProperty("value").GetDouble() == 12 &&
            nativeRows.GetProperty("rows")[0].GetProperty("sourceMetric").GetString() == "memoryUsedPercent",
            "indexed native memory takes precedence over available-MiB fallback");
        var mismatch = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, mismatch);
        store.Append(Publisher, mismatch, 0, Bytes(batch with { Objects = threeBatch.Objects }));
        Fault(() => store.Commit(Publisher, mismatch, Bytes(expectedThree with { Inventory = new(now, true, null) })),
            "INGESTION_REJECTED", "generic complete membership cannot hide missing Windows inventory");
        store.Fail(Publisher, mismatch);
        var staleSettings = new ReadModelSettings
        {
            Enabled = true, WindowsToolsEnabled = true, DatabasePath = Path.Combine(root, "indexed-stale-domain.db"),
            ManagementGroupId = settings.ManagementGroupId, ScopeKind = "managementGroup",
            PublisherObjectIds = [Publisher], AllowedMetricsByClass = settings.AllowedMetricsByClass
        };
        var staleStore = new ReadModelStore(staleSettings, source, clock);
        var staleId = Guid.NewGuid().ToString("D");
        staleStore.Begin(Publisher, staleId);
        staleStore.Append(Publisher, staleId, 0, Bytes(batch with
        {
            Objects = batch.Objects.Select(o => o with { ObservedAtUtc = now.AddMinutes(-20) }).ToImmutableArray()
        }));
        staleStore.Commit(Publisher, staleId, Bytes(manifest with { Inventory = new(now.AddMinutes(-20), true, null) }));
        var staleStatus = engine.SourceStatus(staleStore.ReadWindowsDataset());
        Check(staleStatus.Status == "partial" && staleStatus.Limitations.Contains("INDEXED_DOMAIN_STALE"),
            "fresh metrics never mask a stale inventory domain");
        Activate(native, manifest);
        clock.Now = now.AddMinutes(61);
        Fault(() => store.ReadWindowsDataset(), "SOURCE_UNAVAILABLE", "cached projection expires with source");
        Console.WriteLine($"PASS: {assertions} indexed Windows assertions; six-tool math parity, dynamic membership, persistence, scope and cursors.");
    }
    private static byte[] Bytes<T>(T value) => Encoding.UTF8.GetBytes(Json(value));
    private static string Json<T>(T value) => Wire.Encode(value);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }
    private static void Fault(Action action, string code, string message)
    {
        try { action(); }
        catch (QueryFault fault) when (fault.Code == code) { assertions++; return; }
        throw new InvalidOperationException(message);
    }
    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { assertions++; return; }
        throw new InvalidOperationException(message);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
