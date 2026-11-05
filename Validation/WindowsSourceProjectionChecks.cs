using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ScomMcp;

static class WindowsSourceProjectionChecks
{
    private const string Publisher = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    public static async Task Run(string root)
    {
        var count = 0;
        foreach (var mode in new[] { "complete", "direct", "partial", "managementServer", "unknownRole",
            "legacy", "missingCaption", "blankCaption", "unmappedCaption", "buildOnly" })
        {
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-File",
                Path.Combine(Directory.GetCurrentDirectory(), "Collector", "Validate-ScomWindowsProjection.ps1"),
                "-BridgeMode", mode })
                start.ArgumentList.Add(argument);
            var scan = await NormalizationProcess.RunAsync(start, Encoding.UTF8.GetBytes("{}"), 131072,
                TimeSpan.FromSeconds(60), CancellationToken.None);
            Check(scan.WindowsInventory is not null && scan.ExpectedComputerIds is { Length: 1 },
                "SDK projection deserializes explicit expected membership", ref count);
            var metricId = Guid.NewGuid().ToString("D");
            var os = scan.Objects.Single(o => o.ClassName == "Synthetic.Os");
            var at = scan.MetricsAsOfUtc;
            scan = scan with
            {
                MetricSeries = [new(os.Id, "cpuPercent", "percent", "Synthetic.Rule", at.AddDays(-14), at,
                    true, null, [new(metricId, os.Id, "cpuPercent", "percent", at, 12, "Synthetic.Rule", false)])],
                MetricAssessment = new(at, true, null)
            };
            var settings = new ReadModelSettings
            {
                Enabled = true, WindowsToolsEnabled = true, DatabasePath = Path.Combine(root, $"windows-source-{mode}.db"),
                ManagementGroupId = scan.ManagementGroupId, ScopeKind = "group", GroupId = scan.GroupId,
                PublisherObjectIds = [Publisher],
                WindowsMetricHosting = new()
                {
                    ComputerClass = "Synthetic.Computer", OperatingSystemClass = "Synthetic.Os",
                    RelationshipClass = "Synthetic.ComputerHostsOs"
                },
                WindowsOsCaptionProperty = mode == "legacy" ? null : "Caption",
                AllowedPropertiesByClass = mode == "legacy" ? new() : new() { ["Synthetic.Os"] = ["Caption"] },
                AllowedMetricsByClass = new() { ["Synthetic.Os"] = new() { ["cpuPercent"] = "percent" } }
            };
            var sourceSettings = new SourceSettings { SnapshotEnabled = false };
            var store = new ReadModelStore(settings, sourceSettings, TimeProvider.System);
            var source = new ReconciledCollectionSource(new Profile(scan), new(), TimeProvider.System);
            var prepared = await source.PrepareAsync(CancellationToken.None);
            store.Begin(Publisher, prepared.SessionId);
            var sequence = 0;
            await foreach (var batch in prepared.Batches)
                store.Append(Publisher, prepared.SessionId, sequence++, Encoding.UTF8.GetBytes(Wire.Encode(batch)));
            store.Commit(Publisher, prepared.SessionId, Encoding.UTF8.GetBytes(Wire.Encode(prepared.Manifest)));
            var dataset = store.ReadWindowsDataset();
            Check(dataset.Snapshot.ExpectedDeviceCount == 1 &&
                dataset.Snapshot.Devices.Length == (mode != "partial" ? 1 : 0),
                "real bridge to reconciliation and activation preserves independent coverage", ref count);
            if (mode != "partial")
            {
                var device = dataset.Snapshot.Devices.Single();
                var expectedCaption = mode switch
                {
                    "legacy" or "missingCaption" or "blankCaption" => null,
                    "unmappedCaption" => "Windows Server 2022 Datacenter: Azure Edition",
                    "buildOnly" => "10.0.26100",
                    _ => "Microsoft Windows Server 2022 Datacenter"
                };
                Check(device.OsCaption == expectedCaption &&
                    device.OsVersion == (mode == "buildOnly" ? "10.0.26100" : "10.0.20348"),
                    "caption stays independently observed; missing caption never derives from build", ref count);
                var reopened = new ReadModelStore(settings, sourceSettings, TimeProvider.System).ReadWindowsDataset();
                Check(reopened.Snapshot.Devices.Single().OsCaption == expectedCaption &&
                    reopened.Fingerprint == dataset.Fingerprint, "caption persists through database reopen", ref count);
                var assessment = OsLifecycleCatalog.Assess(device, new(2026, 11, 5), true, 365);
                Check(assessment.Assessment == (mode is "legacy" or "missingCaption" or "blankCaption" or
                    "unmappedCaption" or "buildOnly" ? "unknown" : "extended"),
                    "only exact observed captions enable existing lifecycle classification", ref count);
                var expectedMode = mode == "unknownRole" ? "unknown" :
                    mode == "managementServer" ? "managementServer" : "agent";
                Check(device.MonitoringMode == expectedMode &&
                    dataset.Snapshot.Status == (mode == "unknownRole" ? "partial" : "ok"),
                    "indexed view preserves typed role and missing-role partial coverage", ref count);
                Check(device.MemoryTotalMiB == 8192 && dataset.Snapshot.Volumes.Single().ParentEntityId == device.EntityId,
                    "activated capacity/typed volume parent preserved", ref count);
                Check(dataset.Snapshot.Samples.Single().EntityId == device.EntityId &&
                    dataset.Snapshot.Samples.Single().Value == 12,
                    "OS observations project to typed computer without changing raw arithmetic", ref count);
                var caller = new Caller("synthetic", ImmutableHashSet.Create(Wire.ScopeId));
                var engine = new QueryEngine(sourceSettings, TimeProvider.System, new CursorCodec(TimeProvider.System, sourceSettings));
                var details = JsonSerializer.SerializeToElement(engine.Details(dataset, device.EntityId).Data, Wire.Json);
                var page = JsonSerializer.SerializeToElement(engine.ListDevices(dataset, caller, Wire.ScopeId, new(), 20, null).Data, Wire.Json);
                Check(details.GetProperty("device").GetProperty("osCaption").GetString() == expectedCaption &&
                    page.GetProperty("rows")[0].GetProperty("osCaption").GetString() == expectedCaption,
                    "read tools expose the persisted nullable caption", ref count);
                Check(store.Page(caller, "metrics", null, 50, null).TotalCount == 1,
                    "generic metric retains original OS observation", ref count);
                Check(store.Page(caller, "metrics", os.Id, 50, null).TotalCount == 1 &&
                    store.Page(caller, "metrics", device.EntityId, 50, null).TotalCount == 0,
                    "compatibility projection does not rewrite generic observation parent", ref count);
                if (mode == "complete")
                {
                    var httpRoot = Directory.CreateDirectory(Path.Combine(root, "caption-http")).FullName;
                    await ReadModelChecks.Http(httpRoot, new(scan.Objects, scan.Relationships, [],
                        scan.MetricSeries.SelectMany(s => s.Observations).ToImmutableArray(), scan.WindowsInventory),
                        prepared.Manifest, windowsTools: true, settingsOverride: settings);
                }
                var changed = new ReadModelSettings
                {
                    Enabled = true, WindowsToolsEnabled = true, DatabasePath = settings.DatabasePath,
                    ManagementGroupId = settings.ManagementGroupId, ScopeKind = "group", GroupId = scan.GroupId,
                    PublisherObjectIds = [Publisher], AllowedMetricsByClass = settings.AllowedMetricsByClass
                };
                try { _ = new ReadModelStore(changed, sourceSettings, TimeProvider.System); throw new Exception("Hosting policy change reused database."); }
                catch (InvalidOperationException) { count++; }
                changed.WindowsMetricHosting = settings.WindowsMetricHosting;
                changed.AllowedPropertiesByClass = settings.AllowedPropertiesByClass;
                changed.WindowsOsCaptionProperty = mode == "legacy" ? "Caption" : null;
                try { _ = new ReadModelStore(changed, sourceSettings, TimeProvider.System); throw new Exception("Caption policy change reused database."); }
                catch (InvalidOperationException) { count++; }
                var invalidSession = Guid.NewGuid().ToString("D");
                store.Begin(Publisher, invalidSession);
                var duplicated = false;
                var invalidSequence = 0;
                await foreach (var batch in prepared.Batches)
                {
                    var value = batch;
                    var host = batch.Relationships.FirstOrDefault(e => e.ClassName == "Synthetic.ComputerHostsOs");
                    if (host is not null)
                    {
                        value = batch with { Relationships = batch.Relationships.Add(host with { Id = Guid.NewGuid().ToString("D") }) };
                        duplicated = true;
                    }
                    store.Append(Publisher, invalidSession, invalidSequence++, Encoding.UTF8.GetBytes(Wire.Encode(value)));
                }
                Check(duplicated, "synthetic scan contains independently observed typed hosting", ref count);
                try
                {
                    store.Commit(Publisher, invalidSession, Encoding.UTF8.GetBytes(Wire.Encode(prepared.Manifest)));
                    throw new InvalidOperationException("Ambiguous OS hosting activated.");
                }
                catch (QueryFault error) when (error.Message == "WINDOWS_METRIC_HOSTING_AMBIGUOUS") { count++; }
                try { _ = store.ReadWindowsDataset(); throw new InvalidOperationException("Invalid hosting served previous dataset."); }
                catch (QueryFault error) when (error.Code == "SOURCE_UNAVAILABLE") { count++; }
                store.Fail(Publisher, invalidSession);
                var forgedSession = Guid.NewGuid().ToString("D");
                store.Begin(Publisher, forgedSession);
                var forgedSequence = 0;
                await foreach (var batch in prepared.Batches)
                {
                    var forged = batch.WindowsInventory is { } windows
                        ? batch with { WindowsInventory = windows with
                        {
                            Devices = windows.Devices.Select(d => d with
                            { OsCaption = "Microsoft Windows Server 2025 Datacenter" }).ToImmutableArray()
                        } } : batch;
                    store.Append(Publisher, forgedSession, forgedSequence++, Encoding.UTF8.GetBytes(Wire.Encode(forged)));
                }
                try
                {
                    store.Commit(Publisher, forgedSession, Encoding.UTF8.GetBytes(Wire.Encode(prepared.Manifest)));
                    throw new InvalidOperationException("Unobserved or unconfigured caption activated.");
                }
                catch (QueryFault error) when (error.Message == "WINDOWS_OS_CAPTION_SOURCE_MISMATCH") { count++; }
                store.Fail(Publisher, forgedSession);
            }
            else
            {
                Check(dataset.Snapshot.Status == "partial" && dataset.Snapshot.Volumes.IsEmpty &&
                    dataset.Snapshot.Samples.IsEmpty,
                    "missing expected computer never manufactures a parent or metric", ref count);
            }
        }
        Console.WriteLine($"PASS: {count} Windows source bridge/ingestion assertions; observed captions, persistence, lifecycle and typed hosting.");
    }

    private static void Check(bool value, string message, ref int count)
    {
        if (!value) throw new InvalidOperationException(message);
        count++;
    }
    private sealed class Profile(NormalizedSourceScan scan) : IScomNormalizationProfile
    {
        public Task<NormalizedSourceScan> CollectAsync(SourceCollectionRequest request, CancellationToken token) =>
            Task.FromResult(scan);
    }
}
