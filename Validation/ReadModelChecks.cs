using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Data.Sqlite;
using ScomMcp;

static class ReadModelChecks
{
    private const string Publisher = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string Reader = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private const string Group = "22222222-2222-4222-8222-222222222222";
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly Caller Caller = new("oid:" + Reader, ImmutableHashSet.Create(Wire.ScopeId));
    private static int assertions;

    public static async Task Run(string root)
    {
        var bindingSettings = Settings(Path.Combine(root, "binding-settings.db"));
        using (var json = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"ReadModel":{"ScopeKind":"managementGroup","GroupId":null}}
            """)))
        {
            var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
            configuration.GetSection("ReadModel").Bind(bindingSettings);
            Console.WriteLine("Read-model JSON-null binding: " +
                (bindingSettings.GroupId is null ? "null" : bindingSettings.GroupId == "" ? "empty-string" : "other"));
            bindingSettings.Validate();
            if (bindingSettings.GroupId is not null)
                throw new Exception("Management-group configuration must normalize an absent group.");
            assertions++;
        }
        foreach (var groupId in new string?[] { null, "", " ", "null", Group })
        {
            foreach (var scopeKind in new[] { "group", "managementGroup" })
            {
                var candidate = Settings(Path.Combine(root, "scope-binding.db"));
                candidate.ScopeKind = scopeKind;
                candidate.GroupId = groupId;
                var accepted = scopeKind == "managementGroup" ? groupId is null or "" : groupId == Group;
                try
                {
                    candidate.Validate();
                    if (!accepted) throw new Exception("Invalid scope configuration was accepted.");
                }
                catch (InvalidOperationException) when (!accepted) { }
                assertions++;
            }
        }
        var invalidSettings = Settings(Path.Combine(root, "invalid-settings.db"));
        foreach (var property in new[] { "Caption", " ", "Cap*", new string('x', 129) })
        {
            var candidate = Settings(Path.Combine(root, "caption-settings.db"));
            candidate.WindowsOsCaptionProperty = property;
            try { candidate.Validate(); throw new Exception("Caption policy without typed hosting was accepted."); }
            catch (InvalidOperationException) { assertions++; }
        }
        var captionSettings = Settings(Path.Combine(root, "caption-settings.db"));
        captionSettings.WindowsToolsEnabled = true;
        captionSettings.WindowsMetricHosting = new()
        {
            ComputerClass = "Synthetic.Computer", OperatingSystemClass = "Synthetic.Os",
            RelationshipClass = "Synthetic.Hosts"
        };
        foreach (var property in new[] { " ", "Cap*", new string('x', 129) })
        {
            captionSettings.WindowsOsCaptionProperty = property;
            try { captionSettings.Validate(); throw new Exception("Invalid caption property was accepted."); }
            catch (InvalidOperationException) { assertions++; }
        }
        using (var json = new MemoryStream(Encoding.UTF8.GetBytes("""{"ReadModel":{"WindowsOsCaptionProperty":null}}""")))
        {
            new ConfigurationBuilder().AddJsonStream(json).Build().GetSection("ReadModel").Bind(captionSettings);
            captionSettings.Validate();
            Check(captionSettings.WindowsOsCaptionProperty is null, "JSON-null caption policy is disabled");
        }
        captionSettings.WindowsOsCaptionProperty = "Caption";
        captionSettings.Validate();
        assertions++;
        invalidSettings.AllowedPropertiesByClass = new() { [""] = ["private-sensitive-marker"] };
        invalidSettings.AllowedMetricsByClass = new() { [""] = new() };
        invalidSettings.MaxDatabaseBytes = 1;
        try
        {
            invalidSettings.Validate();
            throw new Exception("Invalid settings were accepted.");
        }
        catch (InvalidOperationException error)
        {
            foreach (var name in new[] { "AllowedPropertiesByClass", "AllowedMetricsByClass", "MaxDatabaseBytes" })
            {
                if (!error.Message.Contains(name, StringComparison.Ordinal))
                    throw new Exception("Configuration diagnostic omitted a rejected setting.");
                assertions++;
            }
            if (error.Message.Contains("private-sensitive-marker", StringComparison.Ordinal))
                throw new Exception("Configuration diagnostic exposed a private value.");
            assertions++;
        }
        var settings = Settings(Path.Combine(root, "read-model.db"));
        var clock = new Clock { Now = Now };
        var source = new SourceSettings { SnapshotEnabled = false };
        var store = new ReadModelStore(settings, source, clock);
        Fault(() => store.Page(Caller, "objects", null, 20, null), "empty store unavailable");
        var objects = Enumerable.Range(1, 70).Select(i => new ObservedObject(
            $"00000000-0000-0000-0000-{i:D12}", i % 2 == 0 ? "Synthetic.Network" : "Synthetic.Computer",
            ["Synthetic.Entity"], "synthetic-" + i, "unknown", Now,
            ImmutableDictionary<string, string?>.Empty)).ToImmutableArray();
        var batch = new IngestionBatch(objects,
            [new(Guid.NewGuid().ToString("D"), "Synthetic.Hosts", objects[0].Id, objects[1].Id)],
            [new(Guid.NewGuid().ToString("D"), objects[0].Id, "Synthetic alert", null, "warning", 0, Now)],
            [new(Guid.NewGuid().ToString("D"), objects[0].Id, "syntheticMetric", "count", Now, 0, "syntheticRule", false)]);
        var manifest = new ReadModelManifest(Group, "managementGroup", null, true, objects.Select(o => o.Id).ToImmutableArray(),
            new(Now, true, null), new(Now, true, null), new(Now, true, null), Now);
        var first = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, first);
        store.Begin(Publisher, first);
        Fault(() => store.Begin(Publisher, Guid.NewGuid().ToString("D")), "single active writer");
        Fault(() => store.Page(Caller, "objects", null, 20, null), "staging hidden");
        var bytes = Bytes(batch);
        store.Append(Publisher, first, 0, bytes);
        store.Append(Publisher, first, 0, bytes);
        var stagingRestart = new ReadModelStore(settings, source, clock);
        Fault(() => stagingRestart.Page(Caller, "objects", null, 20, null), "restart never exposes staged rows");
        stagingRestart.Append(Publisher, first, 0, bytes);
        Fault(() => store.Append(Publisher, first, 0, Bytes(batch with { Alerts = [] })), "conflicting replay");
        Fault(() => store.Append(Publisher, first, 2, bytes), "sequence gap");
        Fault(() => store.Commit(Publisher, first, Bytes(manifest with { MembershipVerified = false })), "unverified membership denied");
        Fault(() => store.Page(Caller, "objects", null, 20, null), "invalid commit unavailable");
        store.Commit(Publisher, first, Bytes(manifest));
        store.Commit(Publisher, first, Bytes(manifest));
        store.Begin(Publisher, first);
        store.Append(Publisher, first, 0, bytes);
        Fault(() => store.Append(Publisher, first, 1, Bytes(new IngestionBatch([], [], [], []))),
            "committed replay cannot append new sequence");
        var page = store.Page(Caller, "objects", null, 50, null);
        Check(page.TotalCount == 70 && page.Rows.Length == 50 && page.NextCursor is not null, "generic count above pilot cap");
        Check(page.Manifest.ExpectedObjectCount == 70, "provenance distinguishes expected membership from discovered count");
        Check(!Wire.Encode(page).Contains("expectedObjectIds", StringComparison.Ordinal),
            "query response does not expose or repeat private membership list");
        var next = store.Page(Caller, "objects", null, 50, page.NextCursor);
        Check(next.Rows.Length == 20 && next.NextCursor is null, "pagination exact");
        Fault(() => store.Page(Caller, "objects", null, 20, page.NextCursor), "page size bound");
        Fault(() => store.Page(Caller with { Binding = "different" }, "objects", null, 50, page.NextCursor), "caller bound");
        Fault(() => store.Page(new("none", ImmutableHashSet<string>.Empty), "objects", null, 20, null), "scope enforced");
        Check(store.Page(Caller, "alerts", objects[0].Id, 20, null).TotalCount == 1, "alert parent filter");
        Check(store.Page(Caller, "relationships", objects[1].Id, 20, null).TotalCount == 1, "relationship target filter");
        Check(store.Page(Caller, "metrics", objects[0].Id, 20, null).TotalCount == 1, "zero sample retained");
        Check(store.Page(Caller, "metrics", batch.Metrics[0].Id, 20, null).TotalCount == 0,
            "metric identity is not mistaken for requested parent object");
        Check(store.Page(Caller, "alerts", batch.Alerts[0].Id, 20, null).TotalCount == 0,
            "alert identity is not mistaken for requested parent object");
        var reopened = new ReadModelStore(settings, source, clock);
        Check(reopened.Page(Caller, "objects", null, 50, null).TotalCount == 70, "activation survives reopen");
        Fault(() => reopened.Page(Caller, "objects", null, 50, page.NextCursor), "restart cursor invalid");
        var second = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, second);
        Fault(() => store.Page(Caller, "objects", null, 20, null), "latest collection invalidates old success");
        var partial = manifest with { Inventory = new(Now, false, "DISCOVERY_INCOMPLETE") };
        store.Append(Publisher, second, 0, Bytes(batch with { Objects = objects[..69] }));
        store.Commit(Publisher, second, Bytes(partial));
        Check(store.Page(Caller, "objects", null, 20, null).Status == "partial", "partial explicit");
        Fault(() => store.Page(Caller, "objects", null, 50, page.NextCursor), "new dataset cursor rejected");
        var failed = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, failed);
        store.Append(Publisher, failed, 0, Bytes(batch with
        {
            Relationships = [new(Guid.NewGuid().ToString("D"), "Synthetic.Hosts", objects[0].Id, Guid.NewGuid().ToString("D"))]
        }));
        Fault(() => store.Commit(Publisher, failed, Bytes(manifest)), "outside graph denied");
        store.Fail(Publisher, failed);
        Fault(() => store.Page(Caller, "objects", null, 20, null), "failure unavailable");
        var corrected = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, corrected);
        var missingValue = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"value\":0,", ""));
        Fault(() => store.Append(Publisher, corrected, 0, missingValue), "missing metric value rejected");
        var duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"value\":0,", "\"value\":0,\"value\":0,"));
        Fault(() => store.Append(Publisher, corrected, 0, duplicate), "duplicate JSON property rejected");
        Fault(() => store.Append(Publisher, corrected, 0, new byte[settings.MaxBatchBytes + 1]), "batch limit");
        store.Append(Publisher, corrected, 0, bytes);
        store.Commit(Publisher, corrected, Bytes(manifest));
        var missingResolution = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"resolutionState\":0,", ""));
        var malformedAlert = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, malformedAlert);
        Fault(() => store.Append(Publisher, malformedAlert, 0, missingResolution), "missing resolution state never active by default");
        store.Fail(Publisher, malformedAlert);
        RejectCommit(batch, manifest with { Inventory = null! }, "null domain rejected without server exception");
        RejectCommit(batch with { Objects = objects.SetItem(0, objects[0] with { Id = null! }) }, manifest,
            "null object ID rejected without server exception");
        RejectCommit(batch with { Metrics = [batch.Metrics[0] with { Unit = "percent" }] }, manifest,
            "metric units require approved mapping");
        RejectCommit(batch with { Alerts = [batch.Alerts[0] with { Description = "Synthetic description" }] }, manifest,
            "alert descriptions denied by default");
        RejectCommit(batch, manifest with { ManagementGroupId = Guid.NewGuid().ToString("D") }, "manifest scope mismatch denied");
        var restored = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, restored);
        store.Append(Publisher, restored, 0, bytes);
        store.Commit(Publisher, restored, Bytes(manifest));
        clock.Now = Now.AddMinutes(16);
        Check(store.Page(Caller, "objects", null, 20, null).Status == "partial", "stale partial");
        clock.Now = Now.AddMinutes(61);
        Fault(() => store.Page(Caller, "objects", null, 20, null), "expired unavailable");
        clock.Now = Now;
        var properties = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, properties);
        store.Append(Publisher, properties, 0, Bytes(batch with
        {
            Objects = objects.SetItem(0, objects[0] with { Properties = ImmutableDictionary<string, string?>.Empty.Add("Unapproved", "value") })
        }));
        Fault(() => store.Commit(Publisher, properties, Bytes(manifest)), "properties require explicit allowlist");
        store.Fail(Publisher, properties);
        var wrongScope = Settings(settings.DatabasePath);
        wrongScope.ManagementGroupId = Guid.NewGuid().ToString("D");
        Throws(() => new ReadModelStore(wrongScope, source, clock), "database scope pinned");
        var wrongProperties = Settings(settings.DatabasePath);
        wrongProperties.AllowedPropertiesByClass.Add("Synthetic.Computer", ["NewProperty"]);
        Throws(() => new ReadModelStore(wrongProperties, source, clock), "stored property policy pinned");
        var limits = Settings(Path.Combine(root, "limits.db"));
        limits.MaxRecords = 1;
        limits.MaxBatchBytes = 1024;
        limits.MaxDatasetBytes = 1024;
        var bounded = new ReadModelStore(limits, source, clock);
        var session = Guid.NewGuid().ToString("D");
        bounded.Begin(Publisher, session);
        var empty = Bytes(new IngestionBatch([], [], [], []));
        var atLimit = Enumerable.Repeat((byte)' ', 1024).ToArray();
        empty.CopyTo(atLimit, 0);
        bounded.Append(Publisher, session, 0, atLimit);
        bounded.Append(Publisher, session, 0, atLimit);
        Fault(() => bounded.Append(Publisher, session, 1, empty), "aggregate byte bound at limit");
        Fault(() => bounded.Commit(Publisher, session, Bytes(manifest)), "manifest included in byte budget");
        bounded.Fail(Publisher, session);
        Fault(() => bounded.Begin(Publisher, session), "consumed session cannot replay");
        var expiring = Guid.NewGuid().ToString("D");
        bounded.Begin(Publisher, expiring);
        clock.Now = Now.AddMinutes(31);
        Fault(() => bounded.Append(Publisher, expiring, 0, empty), "expired staging cannot accept");
        clock.Now = Now;
        var recordLimits = Settings(Path.Combine(root, "record-limits.db"));
        recordLimits.MaxRecords = 1;
        recordLimits.MaxBatchBytes = 1024;
        recordLimits.MaxDatasetBytes = 4096;
        var recordStore = new ReadModelStore(recordLimits, source, clock);
        var recordSession = Guid.NewGuid().ToString("D");
        recordStore.Begin(Publisher, recordSession);
        Fault(() => recordStore.Append(Publisher, recordSession, 0,
            Bytes(new IngestionBatch(objects[..2], [], [], []))), "per-batch record count above boundary");
        var oneObject = Bytes(new IngestionBatch(objects[..1], [], [], []));
        recordStore.Append(Publisher, recordSession, 0, oneObject);
        var oneManifest = manifest with { ExpectedObjectIds = [objects[0].Id] };
        recordStore.Commit(Publisher, recordSession, Bytes(oneManifest));
        Check(recordStore.Page(Caller, "objects", null, 20, null).TotalCount == 1, "exact record count boundary");
        var overRecords = Guid.NewGuid().ToString("D");
        recordStore.Begin(Publisher, overRecords);
        recordStore.Append(Publisher, overRecords, 0, oneObject);
        recordStore.Append(Publisher, overRecords, 0, oneObject);
        Fault(() => recordStore.Append(Publisher, overRecords, 1, oneObject), "aggregate record count rejected during upload");
        Fault(() => recordStore.Page(Caller, "objects", null, 20, null), "rejected upload cannot expose old success");
        recordStore.Append(Publisher, overRecords, 1, empty);
        recordStore.Commit(Publisher, overRecords, Bytes(oneManifest));
        Check(recordStore.Page(Caller, "objects", null, 20, null).TotalCount == 1,
            "rejected upload does not consume sequence or double-count retry");
        var oldStaging = Guid.NewGuid().ToString("D");
        recordStore.Begin(Publisher, oldStaging);
        recordStore.Append(Publisher, oldStaging, 0, oneObject);
        using (var legacyDb = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = recordLimits.DatabasePath, Pooling = false }.ToString()))
        {
            legacyDb.Open();
            using var legacy = legacyDb.CreateCommand();
            legacy.CommandText = "ALTER TABLE batches DROP COLUMN record_count";
            legacy.ExecuteNonQuery();
        }
        var upgraded = new ReadModelStore(recordLimits, source, clock);
        upgraded.Append(Publisher, oldStaging, 0, oneObject);
        Fault(() => upgraded.Append(Publisher, oldStaging, 1, oneObject),
            "old staged records cannot evade aggregate budget after schema upgrade");
        upgraded.Append(Publisher, oldStaging, 1, empty);
        upgraded.Commit(Publisher, oldStaging, Bytes(oneManifest));
        Check(upgraded.Page(Caller, "objects", null, 20, null).DatasetId == oldStaging,
            "old acknowledged staging remains recoverable after count upgrade");
        Fault(() => recordStore.Begin(Reader, Guid.NewGuid().ToString("D")), "unapproved store publisher");
        await Scale(root);
        DenseHistory(root);
        await Http(root, batch, manifest);
        Console.WriteLine($"PASS: {assertions} read-model assertions; ingestion, persistence, auth, partial coverage and cursor isolation.");

        void RejectCommit(IngestionBatch rejectedBatch, ReadModelManifest rejectedManifest, string message)
        {
            var id = Guid.NewGuid().ToString("D");
            store.Begin(Publisher, id);
            store.Append(Publisher, id, 0, Bytes(rejectedBatch));
            Fault(() => store.Commit(Publisher, id, Bytes(rejectedManifest)), message);
            Fault(() => store.Page(Caller, "objects", null, 20, null), message + " leaves source unavailable");
            store.Fail(Publisher, id);
        }
    }

    private static async Task Scale(string root)
    {
        var settings = Settings(Path.Combine(root, "scale.db"));
        var store = new ReadModelStore(settings, new() { SnapshotEnabled = false }, new Clock { Now = Now });
        var objects = Enumerable.Range(1, 5000).Select(i => new ObservedObject(
            $"00000000-0000-0000-0000-{i:D12}", "Synthetic.Entity", [], "synthetic-" + i,
            "unknown", Now, ImmutableDictionary<string, string?>.Empty)).ToImmutableArray();
        var id = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, id);
        for (var i = 0; i < 5; i++)
            store.Append(Publisher, id, i, Bytes(new IngestionBatch(objects[(i * 1000)..((i + 1) * 1000)], [], [], [])));
        var manifest = new ReadModelManifest(Group, "managementGroup", null, true,
            objects.Select(o => o.Id).ToImmutableArray(), new(Now, true, null),
            new(Now, false, "ALERTS_NOT_ASSESSED"), new(Now, false, "METRICS_NOT_ASSESSED"), Now);
        store.Commit(Publisher, id, Bytes(manifest));
        var timings = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var page = store.Page(Caller, "objects", null, 50, null);
            Check(page.TotalCount == 5000 && page.Rows.Length == 50 && page.Status == "partial",
                "scale count and coverage");
            Check(page.Manifest.ExpectedObjectCount == 5000 && Bytes(page).Length <= 32768,
                "5,000 member query response is bounded independently of membership list");
            return watch.Elapsed.TotalMilliseconds;
        })));
        var p95 = timings.Order().ElementAt(9);
        Check(p95 <= 2000, "5,000 object / 10 concurrent query p95 target");
        Check(new FileInfo(settings.DatabasePath).Length <= settings.MaxDatabaseBytes, "configured database page budget");
        Console.WriteLine($"Read-model synthetic scale: 5,000 objects; 10 concurrent 50-row queries; p95 {p95:F1} ms.");
    }

    private static void DenseHistory(string root)
    {
        var settings = Settings(Path.Combine(root, "dense-history.db"));
        var store = new ReadModelStore(settings, new() { SnapshotEnabled = false }, new Clock { Now = Now });
        const string objectId = "00000000-0000-0000-0000-000000000001";
        var entity = new ObservedObject(objectId, "Synthetic.Computer", [], "synthetic-dense-history", "unknown", Now,
            ImmutableDictionary<string, string?>.Empty);
        var points = Enumerable.Range(0, 14 * 24 * 60 + 1).Select(i => new ObservedMetric(
            $"10000000-0000-0000-0000-{i:D12}", objectId, "syntheticMetric", "count",
            Now.AddDays(-14).AddMinutes(i), i % 101, "syntheticRule", i % 2 == 0)).ToImmutableArray();
        var manifest = new ReadModelManifest(Group, "managementGroup", null, true, [objectId],
            new(Now, true, null), new(Now, true, null), new(Now, true, null), Now);
        var session = Guid.NewGuid().ToString("D");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        store.Begin(Publisher, session);
        store.Append(Publisher, session, 0, Bytes(new IngestionBatch([entity], [], [], [])));
        long encodedBytes = Bytes(manifest).Length;
        var sequence = 1;
        for (var i = 0; i < points.Length; i += 1000)
        {
            var payload = Bytes(new IngestionBatch([], [], [], points[i..Math.Min(i + 1000, points.Length)]));
            Check(payload.Length <= settings.MaxBatchBytes, "dense-history batch respects byte budget");
            encodedBytes += payload.Length;
            store.Append(Publisher, session, sequence++, payload);
        }
        store.Commit(Publisher, session, Bytes(manifest));
        var first = store.Page(Caller, "metrics", objectId, 50, null);
        Check(first.TotalCount == points.Length && first.Rows.Length == 50, "14-day minute observations retained, not preaggregated");
        using var row = System.Text.Json.JsonDocument.Parse(Wire.Encode(first.Rows[0]));
        Check(row.RootElement.GetProperty("observedAtUtc").GetDateTimeOffset() == Now.AddDays(-14) &&
            row.RootElement.GetProperty("value").GetDouble() == 0 &&
            row.RootElement.GetProperty("optimized").GetBoolean(), "inclusive oldest sample and real zero/optimized flag retained");
        var readCount = first.Rows.Length;
        var observed = new List<ObservedMetric>();
        Record(first.Rows);
        var lastRow = first.Rows[^1];
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = store.Page(Caller, "metrics", objectId, 50, cursor);
            readCount += page.Rows.Length;
            Record(page.Rows);
            lastRow = page.Rows[^1];
            cursor = page.NextCursor;
        }
        Check(readCount == points.Length, "dense-history paging retains every observation exactly once");
        Check(observed.Select(p => p.Id).ToHashSet().SetEquals(points.Select(p => p.Id)) &&
            observed.Select(p => p.Id).Distinct().Count() == points.Length &&
            observed.Sum(p => p.Value) == points.Sum(p => p.Value) &&
            observed.Count(p => p.Optimized) == points.Count(p => p.Optimized),
            "dense paging preserves exact sample identities, arithmetic values and optimization flags");
        using var last = System.Text.Json.JsonDocument.Parse(Wire.Encode(lastRow));
        Check(last.RootElement.GetProperty("observedAtUtc").GetDateTimeOffset() == Now, "inclusive latest sample retained");
        Check(encodedBytes <= settings.MaxDatasetBytes && new FileInfo(settings.DatabasePath).Length <= settings.MaxDatabaseBytes,
            "dense-history encoded and database budgets");
        var outOfWindow = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, outOfWindow);
        store.Append(Publisher, outOfWindow, 0, Bytes(new IngestionBatch([entity], [], [],
            [points[0] with { ObservedAtUtc = Now.AddDays(-14).AddTicks(-1) }])));
        Fault(() => store.Commit(Publisher, outOfWindow, Bytes(manifest)), "observation immediately before 14-day boundary refused");
        store.Fail(Publisher, outOfWindow);
        Console.WriteLine($"Read-model synthetic history: {points.Length:N0} minute observations / 14 days; " +
            $"{encodedBytes:N0} metric/manifest encoded bytes; elapsed {watch.Elapsed.TotalSeconds:F1}s.");

        void Record(object[] rows)
        {
            foreach (var value in rows)
            {
                if (value is not JsonElement element)
                    throw new InvalidOperationException("Metric row must be a JSON object.");
                observed.Add(element.Deserialize<ObservedMetric>(Wire.Json) ??
                    throw new InvalidOperationException("Metric row must not be null."));
            }
        }
    }

    internal static async Task Http(string root, IngestionBatch batch, ReadModelManifest manifest, bool windowsTools = false,
        ReadModelSettings? settingsOverride = null)
    {
        var startedAssertions = assertions;
        var settings = Settings(Path.Combine(root, windowsTools ? "http-indexed-windows.db" : "http-read-model.db"));
        if (settingsOverride is not null)
        {
            settings.WindowsMetricHosting = settingsOverride.WindowsMetricHosting;
            settings.WindowsOsCaptionProperty = settingsOverride.WindowsOsCaptionProperty;
            settings.AllowedPropertiesByClass = settingsOverride.AllowedPropertiesByClass;
        }
        settings.WindowsToolsEnabled = windowsTools;
        if (windowsTools)
        {
            settings.ManagementGroupId = manifest.ManagementGroupId;
            settings.ScopeKind = manifest.ScopeKind;
            settings.GroupId = manifest.GroupId;
            var classes = batch.Objects.ToDictionary(o => o.Id, o => o.ClassName);
            settings.AllowedMetricsByClass = batch.Metrics.GroupBy(m => classes[m.ObjectId]).ToDictionary(g => g.Key,
                g => g.GroupBy(m => m.Metric).ToDictionary(m => m.Key, m => m.First().Unit));
        }
        settings.RequestBodyTimeoutSeconds = 1;
        var auth = new AuthSettings
        {
            TenantId = Tenant, Audiences = ["api://synthetic-read-model"], RequiredScope = "Scom.Read",
            Callers = [new() { ObjectId = Reader, Scopes = [Wire.ScopeId] }]
        };
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "synthetic-model" };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var liveSettings = new TargetedLiveSettings { Enabled = true, PublisherObjectId = Publisher,
            ProviderPolicySha256 = new('a', 64), ExecutionSeconds = 1 };
        Hosting.AddScomServices(builder.Services, auth, new() { SnapshotEnabled = false }, settings, targetedLive: liveSettings);
        var observedAt = new[] { manifest.Inventory.AsOfUtc, manifest.Alerts.AsOfUtc, manifest.Metrics.AsOfUtc }.Max();
        var httpClock = new Clock { Now = observedAt };
        builder.Services.AddSingleton<TimeProvider>(httpClock);
        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Authority = null;
            options.Configuration = new OpenIdConnectConfiguration { Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0" };
            options.Configuration.SigningKeys.Add(key);
        });
        await using var app = builder.Build();
        Hosting.MapScom(app);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapPost("/validation/hold", async () =>
        {
            held.TrySetResult();
            await release.Task;
            return new { accepted = true };
        }).RequireAuthorization("ScomIngest").RequireRateLimiting("ScomIngestion");
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new(address) };
            var id = Guid.NewGuid().ToString("D");
            var route = "/ingestion/sessions/" + id;
            using var anonymous = await client.PostAsync(route, null);
            Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "anonymous ingestion denied");
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token(Reader, delegated: true));
            using var delegated = await client.PostAsync(route, null);
            Check(delegated.StatusCode == HttpStatusCode.Forbidden, "delegated reader cannot ingest");
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token(Reader, delegated: false));
            using var unapprovedPublisher = await client.PostAsync(route, null);
            Check(unapprovedPublisher.StatusCode == HttpStatusCode.Forbidden, "app role alone cannot publish");
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token(Publisher, delegated: false));
            var holding = client.PostAsync("/validation/hold", null);
            try
            {
                await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using var throttled = await client.PostAsync(route, null);
                Check(throttled.StatusCode == HttpStatusCode.TooManyRequests, "ingestion concurrency has no unbounded waiting queue");
            }
            finally { release.TrySetResult(); }
            using var heldResponse = await holding;
            Check(heldResponse.IsSuccessStatusCode, "admitted publisher request completes");
            using var began = await client.PostAsync(route, null);
            Check(began.IsSuccessStatusCode, "publisher begins");
            using var appended = await client.PostAsync(route + "/batches/0", Content(Bytes(batch)));
            Check(appended.IsSuccessStatusCode, "publisher batch");
            using var committed = await client.PostAsync(route + "/commit", Content(Bytes(manifest)));
            Check(committed.IsSuccessStatusCode, "publisher commit");
            using (var socket = new TcpClient())
            {
                var endpoint = new Uri(address);
                await socket.ConnectAsync(endpoint.Host, endpoint.Port);
                await using var stream = socket.GetStream();
                var header = $"POST {route}/batches/1 HTTP/1.1\r\nHost: localhost\r\n" +
                    $"Authorization: Bearer {Token(Publisher, delegated: false)}\r\n" +
                    "Content-Type: application/json\r\nContent-Length: 1024\r\nConnection: close\r\n\r\n{";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                var received = new byte[4096];
                var length = await stream.ReadAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Check(Encoding.ASCII.GetString(received, 0, length).StartsWith("HTTP/1.1 408", StringComparison.Ordinal),
                    "incomplete publisher body receives bounded deadline response");
            }
            using var retryCommit = await client.PostAsync(route + "/commit", Content(Bytes(manifest)));
            Check(retryCommit.IsSuccessStatusCode, "deadline releases ingestion slot and preserves committed replay");
            using var publisherMcp = await client.PostAsync("/mcp", Content(Bytes(new { jsonrpc = "2.0", id = 1, method = "tools/list" })));
            Check(publisherMcp.StatusCode == HttpStatusCode.Forbidden, "publisher cannot call delegated MCP");
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token(Reader, delegated: true));
            client.DefaultRequestHeaders.Accept.Add(new("application/json"));
            client.DefaultRequestHeaders.Accept.Add(new("text/event-stream"));
            using var called = await client.PostAsync("/mcp", Content(Bytes(new
            {
                jsonrpc = "2.0", id = 2, method = "tools/call",
                @params = new { name = "list_monitored_objects", arguments = new { pageSize = 50 } }
            })));
            var response = await called.Content.ReadAsStringAsync();
            if (response.StartsWith("event:", StringComparison.Ordinal) || response.StartsWith("data:", StringComparison.Ordinal))
                response = string.Join("", response.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(line => line[5..].Trim()));
            using var rpc = JsonDocument.Parse(response);
            Check(called.IsSuccessStatusCode && rpc.RootElement.TryGetProperty("result", out _),
                "authenticated generic MCP succeeds: " + response);
            using var result = JsonDocument.Parse(rpc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
            Check(result.RootElement.GetProperty("totalCount").GetInt32() == batch.Objects.Length, "authenticated generic MCP queries committed data");
            Check(result.RootElement.GetProperty("manifest").GetProperty("expectedObjectCount").GetInt32() == manifest.ExpectedObjectIds.Length &&
                !result.RootElement.GetProperty("manifest").TryGetProperty("expectedObjectIds", out _),
                "authenticated MCP returns bounded membership provenance");
            var publishedSession = Guid.NewGuid().ToString("D");
            using var publisher = new IngestionPublisher(new() { Endpoint = new("https://synthetic.invalid") },
                _ => Task.FromResult(Token(Publisher, delegated: false)), new LoopbackPublisherHandler(new Uri(address)));
            Check(await publisher.PublishAsync(publishedSession, manifest, PublisherBatches()) == publishedSession,
                "worker publisher completes real loopback app-only HTTP ingestion");
            var published = app.Services.GetRequiredService<ReadModelStore>().Page(Caller, "objects", null, 50, null);
            Check(published.DatasetId == publishedSession && published.TotalCount == batch.Objects.Length,
                "worker-to-HTTP-to-indexed-store activation is end-to-end");
            Check(await publisher.PublishAsync(publishedSession, manifest, PublisherBatches()) == publishedSession,
                "whole prepared session can replay after acknowledged commit before source checkpoint");
            var liveSource = new SyntheticLiveSource(httpClock);
            using (var liveWorker = new TargetedLiveWorker(new() { Endpoint = new Uri("https://synthetic.invalid/") },
                _ => Task.FromResult(Token(Publisher, delegated: false)),
                new(liveSettings.ProviderPolicySha256, app.Services.GetRequiredService<TargetedLiveChecks>().Source, true),
                liveSource, httpClock, Microsoft.Extensions.Logging.Abstractions.NullLogger<TargetedLiveWorker>.Instance,
                new LoopbackPublisherHandler(new Uri(address), dropLiveResultOnce: true)))
            {
                Check(!await liveWorker.RunOnceAsync(default), "live poll readiness has zero source work");
                var target = batch.Objects[0].Id;
                async Task<JsonElement> LiveCall(object arguments)
                {
                    using var response = await client.PostAsync("/mcp", Content(Bytes(new
                    {
                        jsonrpc = "2.0", id = 19, method = "tools/call",
                        @params = new { name = "verify_object_current_state", arguments }
                    })));
                    var text = await response.Content.ReadAsStringAsync();
                    if (text.StartsWith("event:", StringComparison.Ordinal) || text.StartsWith("data:", StringComparison.Ordinal))
                        text = string.Join("", text.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                            .Select(line => line[5..].Trim()));
                    using var rpc = JsonDocument.Parse(text);
                    return rpc.RootElement.Clone();
                }
                static JsonElement LiveData(JsonElement rpc)
                {
                    using var document = JsonDocument.Parse(rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
                    return document.RootElement.Clone();
                }
                var missingTarget = await LiveCall(new { objectId = "ffffffff-ffff-4fff-8fff-ffffffffffff" });
                Check(LiveData(missingTarget).GetProperty("error").GetString() == "NOT_FOUND" && liveSource.Calls == 0,
                    "unknown retained ID never causes source access");
                using var deniedClaim = await client.PostAsync("/ingestion/live/claim", Content(Bytes(new
                {
                    providerPolicySha256 = liveSettings.ProviderPolicySha256,
                    source = app.Services.GetRequiredService<TargetedLiveChecks>().Source, contractVerified = true
                })));
                Check(deniedClaim.StatusCode == HttpStatusCode.Forbidden, "delegated reader cannot fulfill source requests");
                var liveResponse = LiveCall(new { objectId = target });
                using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    while (!await liveWorker.RunOnceAsync(bound.Token)) await Task.Delay(10, bound.Token);
                var liveData = LiveData(await liveResponse);
                Check(liveData.GetProperty("mode").GetString() == "targetedLive" &&
                    liveData.GetProperty("observation").GetProperty("scopeVerified").GetBoolean(), "fake-source authenticated live round trip");
                Check(liveSource.Calls == 1 && liveData.GetProperty("observation").GetProperty("objectId").GetString() == target,
                    "one admitted target yields one source invocation");
                httpClock.Now = httpClock.Now.AddSeconds(2);
                liveSource.Outside = true;
                var removedResponse = LiveCall(new { objectId = target });
                using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    while (!await liveWorker.RunOnceAsync(bound.Token)) await Task.Delay(10, bound.Token);
                Check(LiveData(await removedResponse).GetProperty("error").GetString() == "NOT_FOUND",
                    "source membership removal never serves historical or live attributes");
                httpClock.Now = httpClock.Now.AddSeconds(2);
                liveSource.Outside = false;
                liveSource.DelayUntilCancelled = true;
                var timedResponse = LiveCall(new { objectId = target });
                using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    while (!await liveWorker.RunOnceAsync(bound.Token)) await Task.Delay(10, bound.Token);
                Check(LiveData(await timedResponse).GetProperty("error").GetString() == "LIVE_SOURCE_UNAVAILABLE" &&
                    liveSource.Active == 0, "source deadline cancels and joins fake provider without retained fallback");
                httpClock.Now = httpClock.Now.AddMinutes(1);
                liveSource.DelayUntilCancelled = false;
                var invalidatingSession = Guid.NewGuid().ToString("D");
                liveSource.DuringRead = () => app.Services.GetRequiredService<ReadModelStore>().Begin(Publisher, invalidatingSession);
                Check(!await liveWorker.RunOnceAsync(default), "readiness restored without reading source");
                var invalidatedResponse = LiveCall(new { objectId = target });
                using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    while (!await liveWorker.RunOnceAsync(bound.Token)) await Task.Delay(10, bound.Token);
                Check(LiveData(await invalidatedResponse).GetProperty("error").GetString() == "SOURCE_UNAVAILABLE",
                    "retained scope availability is rechecked after source result before delivery");
                liveSource.DuringRead = null;
                app.Services.GetRequiredService<ReadModelStore>().Fail(Publisher, invalidatingSession);
                publishedSession = Guid.NewGuid().ToString("D");
                Check(await publisher.PublishAsync(publishedSession, manifest, PublisherBatches()) == publishedSession,
                    "explicit fresh publication recovers after invalidation, not live-result fallback");
            }
            if (windowsTools)
            {
                Check(app.Services.GetRequiredService<ISnapshotReader>() is IndexedWindowsReader,
                    "production DI selects indexed reader without fixture override");
                var requests = new (string Name, object Arguments)[]
                {
                    ("get_source_status", new { }),
                    ("get_inventory_summary", new { }),
                    ("list_devices", new { pageSize = 50 }),
                    ("get_device_details", new { entityId = batch.WindowsInventory!.Devices[0].EntityId }),
                    ("get_utilization", new { metric = "cpuPercent", statistic = "mean", comparePrevious = true }),
                    ("get_capacity_summary", new { resource = "disk", pageSize = 50 }),
                    ("list_os_support_assessments", new { pageSize = 1 })
                };
                foreach (var (name, arguments) in requests)
                {
                    using var tool = await client.PostAsync("/mcp", Content(Bytes(new
                    {
                        jsonrpc = "2.0", id = 10, method = "tools/call",
                        @params = new { name, arguments }
                    })));
                    var body = await tool.Content.ReadAsStringAsync();
                    if (body.StartsWith("event:", StringComparison.Ordinal) || body.StartsWith("data:", StringComparison.Ordinal))
                        body = string.Join("", body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                            .Select(line => line[5..].Trim()));
                    using var envelope = JsonDocument.Parse(body);
                    var toolResult = envelope.RootElement.GetProperty("result");
                    Check(tool.IsSuccessStatusCode &&
                        (!toolResult.TryGetProperty("isError", out var error) || !error.GetBoolean()),
                        "indexed six-tool authenticated call succeeds: " + name);
                    using var data = JsonDocument.Parse(toolResult.GetProperty("content")[0].GetProperty("text").GetString()!);
                    Check(data.RootElement.GetProperty("datasetId").GetString() == publishedSession,
                        "indexed tool uses same activated dataset as generic tools: " + name);
                    if (name is "list_devices" or "get_device_details")
                    {
                        var payload = data.RootElement.GetProperty("data");
                        var row = name == "list_devices" ? payload.GetProperty("rows")[0] : payload.GetProperty("device");
                        var expected = batch.WindowsInventory!.Devices.Single(d =>
                            d.EntityId == row.GetProperty("entityId").GetString());
                        Check(row.GetProperty("osCaption").GetString() == expected.OsCaption &&
                            row.GetProperty("osVersion").GetString() == expected.OsVersion,
                            "authenticated read preserves separate observed caption and version");
                    }
                    if (name == "list_os_support_assessments")
                    {
                        Check(data.RootElement.GetProperty("data").GetProperty("scopeComputerCount").GetInt32() ==
                            batch.WindowsInventory!.Devices.Length, "indexed lifecycle counts actual computers not generic objects");
                        Check(data.RootElement.GetProperty("data").GetProperty("rows").GetArrayLength() <= 1,
                            "indexed lifecycle bounded page");
                        var lifecycleRow = data.RootElement.GetProperty("data").GetProperty("rows")[0];
                        var expected = batch.WindowsInventory!.Devices.Single(d =>
                            d.EntityId == lifecycleRow.GetProperty("entityId").GetString());
                        Check(lifecycleRow.GetProperty("observedOsCaption").GetString() == (expected.OsCaption ?? expected.OsVersion),
                            "authenticated lifecycle uses observed caption with legacy fallback");
                    }
                }
            }

            var profile = new CheckpointProfile(new(manifest.ManagementGroupId, manifest.ScopeKind, manifest.GroupId,
                true, manifest.ExpectedObjectIds, batch.Objects, batch.Relationships, manifest.Inventory,
                batch.Alerts, manifest.Alerts,
                batch.Metrics.GroupBy(m => (m.ObjectId, m.Metric, m.RuleId)).Select(g =>
                    new MetricSeriesScan(g.Key.ObjectId, g.Key.Metric, g.First().Unit, g.Key.RuleId,
                        g.Min(m => m.ObservedAtUtc), manifest.Metrics.AsOfUtc, true, null, g.ToImmutableArray())).ToImmutableArray(),
                manifest.Metrics.AsOfUtc, batch.WindowsInventory, manifest.ExpectedComputerIds));
            var checkpointPath = Path.Combine(root, windowsTools ? "http-windows-worker-state.db" : "http-worker-state.db");
            var workerClock = new Clock { Now = observedAt };
            string checkpointSession;
            using (var checkpoint = new SourceCheckpointStore(checkpointPath, new string('a', 64)))
            {
                var source = new ReconciledCollectionSource(profile, new(), workerClock, checkpoint);
                checkpointSession = await source.ReserveSessionAsync(CancellationToken.None);
                var durableWorker = new CollectionWorker(new RejectCheckpoint(source), publisher);
                try
                {
                    await durableWorker.RunOnceAsync(CancellationToken.None);
                    throw new InvalidOperationException("Local acknowledgement failure was swallowed.");
                }
                catch (IOException)
                {
                    Check(app.Services.GetRequiredService<ReadModelStore>().Page(Caller, "objects", null, 50, null).DatasetId == checkpointSession,
                        "server activation survives subsequent local source acknowledgement failure");
                }
            }
            profile.Fail = true;
            using (var checkpoint = new SourceCheckpointStore(checkpointPath, new string('a', 64)))
            {
                var source = new ReconciledCollectionSource(profile, new(), workerClock, checkpoint);
                var durableWorker = new CollectionWorker(source, publisher);
                Check(await durableWorker.RunOnceAsync(CancellationToken.None) == CollectionAttemptOutcome.Published && profile.Calls == 1,
                    "restarted durable worker replays acknowledged server commit without recollecting source");
                Check(app.Services.GetRequiredService<ReadModelStore>().Page(Caller, "objects", null, 50, null).DatasetId == checkpointSession,
                    "durable committed replay preserves the same server dataset");
            }

            var failingSource = new FailingCollectionSource();
            var worker = new CollectionWorker(failingSource, publisher);
            try
            {
                await worker.RunOnceAsync(CancellationToken.None);
                throw new InvalidOperationException("Failed scope refresh was swallowed.");
            }
            catch (QueryFault error) when (error.Code == "SOURCE_UNAVAILABLE")
            {
                Check(failingSource.PreparationAttempted && !failingSource.Acknowledged,
                    "real worker begins ingestion before failed source preparation without checkpointing");
            }
            try
            {
                app.Services.GetRequiredService<ReadModelStore>().Page(Caller, "objects", null, 50, null);
                throw new InvalidOperationException("Failed scope refresh served the prior activated dataset.");
            }
            catch (QueryFault error) when (error.Code == "SOURCE_UNAVAILABLE")
            {
                Check(true, "real worker-to-HTTP begin invalidates prior generic dataset after failed scope refresh");
            }
            if (windowsTools)
            {
                try
                {
                    app.Services.GetRequiredService<ReadModelStore>().ReadWindowsDataset();
                    throw new InvalidOperationException("Failed scope refresh served the cached Windows projection.");
                }
                catch (QueryFault error) when (error.Code == "SOURCE_UNAVAILABLE")
                {
                    Check(true, "failed scope refresh also denies the cached six-tool Windows projection");
                }
            }

            async IAsyncEnumerable<IngestionBatch> PublisherBatches()
            {
                yield return batch;
                await Task.CompletedTask;
            }
        }
        finally { await app.StopAsync(); }
        if (windowsTools)
            Console.WriteLine($"PASS: {assertions - startedAssertions} indexed Windows HTTP assertions; real publisher, DI, six existing tools and additive lifecycle.");

        string Token(string oid, bool delegated)
        {
            var claims = new List<Claim> { new("tid", Tenant), new("oid", oid) };
            claims.Add(delegated ? new("scp", "Scom.Read") : new("roles", "Scom.Ingest"));
            var now = DateTime.UtcNow;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                $"https://login.microsoftonline.com/{Tenant}/v2.0", auth.Audiences[0], claims,
                now.AddMinutes(-1), now.AddMinutes(10), new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
        }
    }
    private static ByteArrayContent Content(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
    private static ReadModelSettings Settings(string path) => new()
    {
        Enabled = true, DatabasePath = path, ManagementGroupId = Group, ScopeKind = "managementGroup",
        PublisherObjectIds = [Publisher],
        AllowedMetricsByClass = new() { ["Synthetic.Computer"] = new() { ["syntheticMetric"] = "count" } }
    };
    private static byte[] Bytes<T>(T value) => Encoding.UTF8.GetBytes(Wire.Encode(value));
    private static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref assertions);
        if (!condition) throw new InvalidOperationException("Read model validation: " + message);
    }
    private static void Fault(Action action, string message)
    {
        try { action(); } catch (QueryFault) { assertions++; return; }
        throw new InvalidOperationException("Expected query fault: " + message);
    }
    private static void Throws(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { assertions++; return; }
        throw new InvalidOperationException("Expected invalid configuration: " + message);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class CheckpointProfile(NormalizedSourceScan scan) : IScomNormalizationProfile
    {
        public bool Fail;
        public int Calls;
        public Task<NormalizedSourceScan> CollectAsync(SourceCollectionRequest request, CancellationToken cancellationToken)
        {
            if (Fail) throw new QueryFault("SOURCE_UNAVAILABLE", "SYNTHETIC_RECOLLECTION_FORBIDDEN");
            Calls++;
            return Task.FromResult(scan);
        }
    }

    private sealed class RejectCheckpoint(IScomCollectionSource source) : IScomCollectionSource
    {
        public Task<string> ReserveSessionAsync(CancellationToken cancellationToken) => source.ReserveSessionAsync(cancellationToken);
        public Task<PreparedCollection> PrepareAsync(CancellationToken cancellationToken) => source.PrepareAsync(cancellationToken);
        public Task AcknowledgeAsync(string sessionId, CancellationToken cancellationToken) =>
            throw new IOException("Synthetic local checkpoint acknowledgement failure after server commit.");
    }

    private sealed class FailingCollectionSource : IScomCollectionSource
    {
        private readonly string sessionId = Guid.NewGuid().ToString("D");
        public bool PreparationAttempted;
        public bool Acknowledged;
        public Task<string> ReserveSessionAsync(CancellationToken cancellationToken) => Task.FromResult(sessionId);
        public Task<PreparedCollection> PrepareAsync(CancellationToken cancellationToken)
        {
            PreparationAttempted = true;
            throw new QueryFault("SOURCE_UNAVAILABLE", "SYNTHETIC_SCOPE_UNVERIFIED");
        }
        public Task AcknowledgeAsync(string sessionId, CancellationToken cancellationToken)
        {
            Acknowledged = true;
            throw new InvalidOperationException("Failed preparation cannot be acknowledged.");
        }
    }

    private sealed class SyntheticLiveSource(Clock clock) : ITargetedLiveSource
    {
        public int Calls;
        public bool Outside;
        public bool DelayUntilCancelled;
        public Action? DuringRead;
        public int Active;
        public async Task<LiveCheckObservation> VerifyAsync(LiveCheckRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Interlocked.Increment(ref Active);
            try
            {
                if (DelayUntilCancelled) await Task.Delay(Timeout.Infinite, cancellationToken);
                DuringRead?.Invoke();
                return new LiveCheckObservation(request.RequestId, request.ObjectId, request.Source,
                Outside ? "outOfScope" : "ok", !Outside, clock.Now, clock.Now,
                    Outside ? null : "critical", Outside ? null : clock.Now, !Outside, [], 3);
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    private sealed class LoopbackPublisherHandler(Uri origin, bool dropLiveResultOnce = false) : HttpMessageHandler
    {
        private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        private bool dropped;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!origin.IsLoopback || origin.Scheme != "http")
                throw new InvalidOperationException("Validation transport must target the synthetic loopback app only.");
            using var forwarded = new HttpRequestMessage(request.Method, new Uri(origin, request.RequestUri!.AbsolutePath));
            forwarded.Headers.Authorization = request.Headers.Authorization;
            if (request.Content is not null)
            {
                forwarded.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                forwarded.Content.Headers.ContentType = request.Content.Headers.ContentType;
            }
            var response = await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (dropLiveResultOnce && !dropped && request.RequestUri!.AbsolutePath.EndsWith("/live/result", StringComparison.Ordinal))
            {
                dropped = true;
                response.Dispose();
                throw new HttpRequestException("Synthetic lost accepted result.");
            }
            return response;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) client.Dispose();
            base.Dispose(disposing);
        }
    }
}
