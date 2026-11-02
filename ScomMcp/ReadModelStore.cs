using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ScomMcp;

// All store operations serialize through this process; SQLite transactions are the
// durable boundary. Staging is never visible to readers.
public sealed class ReadModelStore
{
    private readonly ReadModelSettings settings;
    private readonly SourceSettings source;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    private (string Dataset, ReadModelManifest Manifest)? cachedManifest;
    private Dataset? cachedWindowsDataset;

    public ReadModelStore(ReadModelSettings settings, SourceSettings source, TimeProvider clock)
    {
        this.settings = settings;
        this.source = source;
        this.clock = clock;
        settings.Validate();
        if (!settings.Enabled) return;
        using var db = Open();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS runs (
                id TEXT PRIMARY KEY, publisher TEXT NOT NULL, expires TEXT NOT NULL,
                state TEXT NOT NULL, manifest TEXT, digest TEXT);
            CREATE TABLE IF NOT EXISTS batches (
                run TEXT NOT NULL, sequence INTEGER NOT NULL, digest TEXT NOT NULL, payload BLOB NOT NULL,
                PRIMARY KEY(run, sequence));
            CREATE TABLE IF NOT EXISTS records (
                run TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL, parent TEXT,
                target TEXT, payload TEXT NOT NULL, PRIMARY KEY(run, kind, id));
            CREATE INDEX IF NOT EXISTS record_parent ON records(run, kind, parent);
            CREATE TABLE IF NOT EXISTS current (singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                run TEXT, available INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS configuration (singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                scope TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS used_sessions (id TEXT PRIMARY KEY);
            INSERT OR IGNORE INTO current VALUES(1, NULL, 0);
            """);
        var scope = Wire.Encode(new
        {
            schema = 1, settings.ManagementGroupId, settings.ScopeKind, settings.GroupId,
            properties = settings.AllowedPropertiesByClass.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new { p.Key, names = p.Value.Order(StringComparer.Ordinal).ToArray() }).ToArray(),
            metrics = settings.AllowedMetricsByClass.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new { p.Key, values = p.Value.OrderBy(v => v.Key, StringComparer.Ordinal).ToArray() }).ToArray(),
            settings.IncludeAlertDescriptions
        });
        if (settings.WindowsToolsEnabled) scope = Wire.Encode(new { schema = 2, policy = scope, windowsTools = true });
        if (settings.WindowsMetricHosting is { } hostingPolicy)
            scope = Wire.Encode(new { schema = 3, policy = scope, windowsMetricHosting = hostingPolicy });
        using var tx = db.BeginTransaction();
        var storedScope = Scalar(db, tx, "SELECT scope FROM configuration WHERE singleton=1");
        if (storedScope is not null && !string.Equals((string)storedScope, scope, StringComparison.Ordinal))
            throw new InvalidOperationException("Read model database belongs to a different configured scope.");
        using (var columns = Command(db, tx, "PRAGMA table_info(batches)"))
        {
            var hasRecordCount = false;
            using (var reader = columns.ExecuteReader())
                while (reader.Read()) hasRecordCount |= reader.GetString(1) == "record_count";
            if (!hasRecordCount)
                Execute(db, tx, "ALTER TABLE batches ADD COLUMN record_count INTEGER");
        }
        Execute(db, tx, "INSERT OR IGNORE INTO configuration VALUES(1,$scope)", ("$scope", scope));
        tx.Commit();
    }

    private SqliteConnection Open()
    {
        if (!settings.Enabled) throw new QueryFault("SOURCE_UNAVAILABLE", "READ_MODEL_DISABLED");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = settings.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
            db.Open();
            Execute(db, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;");
            using var size = db.CreateCommand();
            size.CommandText = "PRAGMA page_size";
            var pageLimit = settings.MaxDatabaseBytes / Convert.ToInt64(size.ExecuteScalar());
            size.CommandText = $"PRAGMA max_page_count={pageLimit}";
            if (Convert.ToInt64(size.ExecuteScalar()) > pageLimit)
                throw new InvalidOperationException("Existing read model exceeds the configured database budget.");
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public string Begin(string publisher, string sessionId)
    {
        lock (gate)
        {
            Id(sessionId);
            Publisher(publisher);
            using var db = Open();
            using var tx = db.BeginTransaction();
            var existing = Run(db, tx, sessionId);
            if (existing is not null)
            {
                if (existing.State == "committed")
                {
                    Require(existing.Publisher == publisher, "SESSION_REPLAY_OR_EXPIRED");
                    tx.Commit();
                    return sessionId;
                }
                Require(existing.Publisher == publisher && existing.State == "staging" &&
                    existing.Expires > clock.GetUtcNow(), "SESSION_REPLAY_OR_EXPIRED");
                tx.Commit();
                return sessionId;
            }
            var active = Scalar(db, tx, "SELECT count(*) FROM runs WHERE state='staging' AND expires > $now",
                ("$now", clock.GetUtcNow().ToString("O")));
            Require(Convert.ToInt64(active) == 0, "COLLECTION_ALREADY_IN_PROGRESS");
            Require(Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM used_sessions WHERE id=$id", ("$id", sessionId))) == 0,
                "SESSION_ALREADY_CONSUMED");
            Require(Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM used_sessions")) < 1000000,
                "SESSION_LEDGER_LIMIT");
            Execute(db, tx, """
                DELETE FROM batches WHERE run IN (SELECT id FROM runs WHERE state='staging');
                DELETE FROM runs WHERE state='staging';
                DELETE FROM runs WHERE state='failed';
                INSERT INTO runs(id,publisher,expires,state) VALUES($id,$publisher,$expires,'staging');
                INSERT INTO used_sessions VALUES($id);
                UPDATE current SET available=0;
                """, ("$id", sessionId), ("$publisher", publisher),
                ("$expires", clock.GetUtcNow().AddMinutes(settings.SessionMinutes).ToString("O")));
            tx.Commit();
            return sessionId;
        }
    }

    public void Append(string publisher, string sessionId, int sequence, byte[] payload)
    {
        lock (gate)
        {
            Require(sequence is >= 0 and < 10000 && payload.Length <= settings.MaxBatchBytes, "BATCH_LIMIT");
            Id(sessionId);
            Publisher(publisher);
            var batch = ParseBatch(payload);
            var recordCount = CountRecords(batch);
            var digest = Hash(payload);
            using var db = Open();
            using var tx = db.BeginTransaction();
            var run = Run(db, tx, sessionId);
            Require(run is not null && run.Publisher == publisher, "SESSION_INVALID");
            var old = Scalar(db, tx, "SELECT digest FROM batches WHERE run=$id AND sequence=$sequence",
                ("$id", sessionId), ("$sequence", sequence));
            if (old is string oldDigest)
            {
                Require(oldDigest == digest, "BATCH_REPLAY_CONFLICT");
                Require(run!.State == "committed" ||
                    run.State == "staging" && run.Expires > clock.GetUtcNow(), "SESSION_INVALID_OR_EXPIRED");
                tx.Commit();
                return;
            }
            Staging(db, tx, sessionId, publisher);
            var next = Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM batches WHERE run=$id", ("$id", sessionId)));
            Require(sequence == next, "BATCH_SEQUENCE_INVALID");
            var size = Convert.ToInt64(Scalar(db, tx, "SELECT coalesce(sum(length(payload)),0) FROM batches WHERE run=$id",
                ("$id", sessionId)));
            Require(size + payload.Length <= settings.MaxDatasetBytes, "DATASET_SIZE_LIMIT");
            // Older staged batches have no stored count; validate and backfill them
            // transactionally before accepting a new sequence.
            var legacyCounts = new List<(int Sequence, long Count)>();
            using (var command = Command(db, tx,
                "SELECT sequence,payload FROM batches WHERE run=$id AND record_count IS NULL", ("$id", sessionId)))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    legacyCounts.Add((reader.GetInt32(0), CountRecords(ParseBatch((byte[])reader[1]))));
            foreach (var legacy in legacyCounts)
                Execute(db, tx, "UPDATE batches SET record_count=$count WHERE run=$id AND sequence=$sequence",
                    ("$count", legacy.Count), ("$id", sessionId), ("$sequence", legacy.Sequence));
            var stagedRecords = Convert.ToInt64(Scalar(db, tx,
                "SELECT coalesce(sum(record_count),0) FROM batches WHERE run=$id", ("$id", sessionId)));
            Require(stagedRecords + recordCount <= settings.MaxRecords, "RECORD_COUNT_LIMIT");
            Execute(db, tx, "INSERT INTO batches(run,sequence,digest,payload,record_count) VALUES($id,$sequence,$digest,$payload,$count)",
                ("$id", sessionId), ("$sequence", sequence), ("$digest", digest), ("$payload", payload),
                ("$count", recordCount));
            tx.Commit();
        }
    }

    public string Commit(string publisher, string sessionId, byte[] manifestBytes)
    {
        lock (gate)
        {
            Id(sessionId);
            Publisher(publisher);
            Require(manifestBytes.Length <= settings.MaxBatchBytes, "MANIFEST_SIZE_LIMIT");
            var manifest = Deserialize<ReadModelManifest>(manifestBytes);
            Domain(manifest.Inventory); Domain(manifest.Alerts); Domain(manifest.Metrics);
            var digest = Hash(manifestBytes);
            using var db = Open();
            using var tx = db.BeginTransaction();
            var run = Run(db, tx, sessionId);
            Require(run is not null && run.Publisher == publisher, "SESSION_INVALID");
            if (run!.State == "committed")
            {
                Require(run.Digest == digest, "COMMIT_REPLAY_CONFLICT");
                tx.Commit();
                return sessionId;
            }
            var previousText = Scalar(db, tx, """
                SELECT manifest FROM runs JOIN current ON current.run=runs.id
                WHERE runs.state='committed'
                """);
            if (previousText is string previousJson)
            {
                var previous = Deserialize<ReadModelManifest>(Encoding.UTF8.GetBytes(previousJson));
                Require(manifest.Inventory.AsOfUtc >= previous.Inventory.AsOfUtc &&
                    manifest.Alerts.AsOfUtc >= previous.Alerts.AsOfUtc &&
                    manifest.Metrics.AsOfUtc >= previous.Metrics.AsOfUtc &&
                    manifest.AlertHistoryStartUtc >= previous.AlertHistoryStartUtc, "SOURCE_TIME_REGRESSION");
            }
            Staging(db, tx, sessionId, publisher);
            var bytes = Convert.ToInt64(Scalar(db, tx, "SELECT coalesce(sum(length(payload)),0) FROM batches WHERE run=$id",
                ("$id", sessionId)));
            Require(bytes + manifestBytes.Length <= settings.MaxDatasetBytes, "DATASET_SIZE_LIMIT");
            var objects = new List<ObservedObject>();
            var relationships = new List<ObservedRelationship>();
            var alerts = new List<ObservedAlert>();
            var metrics = new List<ObservedMetric>();
            var devices = new List<Device>();
            var volumes = new List<Volume>();
            using (var command = Command(db, tx, "SELECT payload FROM batches WHERE run=$id ORDER BY sequence", ("$id", sessionId)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var batch = ParseBatch((byte[])reader[0]);
                    Require((long)objects.Count + relationships.Count + alerts.Count + metrics.Count + devices.Count + volumes.Count +
                        CountRecords(batch) <= settings.MaxRecords,
                        "RECORD_COUNT_LIMIT");
                    objects.AddRange(batch.Objects);
                    relationships.AddRange(batch.Relationships);
                    alerts.AddRange(batch.Alerts);
                    metrics.AddRange(batch.Metrics);
                    if (batch.WindowsInventory is { } windows)
                    {
                        devices.AddRange(windows.Devices);
                        volumes.AddRange(windows.Volumes);
                    }
                }
            }
            Validate(manifest, objects, relationships, alerts, metrics);
            Dataset? windowsDataset = null;
            if (settings.WindowsToolsEnabled)
                windowsDataset = BuildWindowsDataset(sessionId, manifest, objects, relationships, metrics, devices, volumes);
            else
                Require(devices.Count == 0 && volumes.Count == 0 && manifest.ExpectedComputerIds is null,
                    "WINDOWS_PROJECTION_DISABLED");
            foreach (var value in objects) Insert("objects", value.Id, null, null, value);
            foreach (var value in relationships) Insert("relationships", value.Id, value.SourceId, value.TargetId, value);
            foreach (var value in alerts) Insert("alerts", value.Id, value.ObjectId, null, value);
            foreach (var value in metrics) Insert("metrics", value.Id, value.ObjectId, null, value);
            if (windowsDataset is not null)
                Insert("windowsSnapshot", "view", null, null, windowsDataset.Snapshot);
            Execute(db, tx, """
                UPDATE runs SET state='committed',manifest=$manifest,digest=$digest WHERE id=$id;
                UPDATE current SET run=$id,available=1;
                DELETE FROM records WHERE run NOT IN (
                    SELECT id FROM runs WHERE state='committed' ORDER BY rowid DESC LIMIT 2);
                DELETE FROM batches WHERE run NOT IN (
                    SELECT id FROM runs WHERE state='committed' ORDER BY rowid DESC LIMIT 2);
                DELETE FROM runs WHERE state='committed' AND id NOT IN (
                    SELECT id FROM runs WHERE state='committed' ORDER BY rowid DESC LIMIT 2);
                """, ("$id", sessionId), ("$manifest", Wire.Encode(manifest)), ("$digest", digest));
            tx.Commit();
            cachedManifest = (sessionId, manifest);
            cachedWindowsDataset = windowsDataset;
            return sessionId;

            void Insert<T>(string kind, string id, string? parent, string? target, T value) =>
                Execute(db, tx, "INSERT INTO records VALUES($run,$kind,$id,$parent,$target,$payload)",
                    ("$run", sessionId), ("$kind", kind), ("$id", id), ("$parent", parent),
                    ("$target", target), ("$payload", Wire.Encode(value)));
        }
    }

    public void Fail(string publisher, string sessionId)
    {
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            Staging(db, tx, sessionId, publisher);
            Execute(db, tx, "UPDATE runs SET state='failed' WHERE id=$id; UPDATE current SET available=0;",
                ("$id", sessionId));
            Execute(db, tx, "DELETE FROM batches WHERE run=$id", ("$id", sessionId));
            tx.Commit();
        }
    }

    public ReadModelPage Page(Caller caller, string kind, string? objectId, int pageSize, string? cursor)
    {
        lock (gate)
        {
            CallerAuthorization.AuthorizeScope(caller, Wire.ScopeId);
            Guard.Argument(kind is "objects" or "relationships" or "alerts" or "metrics", "Invalid read model kind.");
            QueryEngine.ValidatePageSize(pageSize);
            if (objectId is not null) Id(objectId);
            using var db = Open();
            using var tx = db.BeginTransaction();
            using var command = Command(db, tx, """
                SELECT runs.id FROM current JOIN runs ON runs.id=current.run
                WHERE current.available=1 AND runs.state='committed'
                """);
            string dataset;
            ReadModelManifest manifest;
            using (var reader = command.ExecuteReader())
            {
                Guard.Source(reader.Read(), "READ_MODEL_LATEST_COLLECTION_UNAVAILABLE");
                dataset = reader.GetString(0);
            }

            manifest = ReadManifest(db, tx, dataset);
            var oldest = new[] { manifest.Inventory.AsOfUtc, manifest.Alerts.AsOfUtc, manifest.Metrics.AsOfUtc }.Min();
            Guard.Source(clock.GetUtcNow() - oldest <= TimeSpan.FromMinutes(settings.UnavailableMinutes),
                "READ_MODEL_EXPIRED");
            var binding = Wire.Encode(new { caller.Binding, kind, objectId, pageSize });
            var offset = ReadCursor(cursor, dataset, binding);
            const string filter = """
                run=$run AND kind=$kind AND ($object IS NULL OR
                (kind='objects' AND id=$object) OR
                (kind<>'objects' AND parent=$object) OR
                (kind='relationships' AND target=$object))
                """;
            var parameters = new (string, object?)[] { ("$run", dataset), ("$kind", kind), ("$object", objectId) };
            var count = Convert.ToInt32(Scalar(db, tx, "SELECT count(*) FROM records WHERE " + filter, parameters));
            Guard.Argument(offset <= count, "Cursor offset is invalid.");
            using var rowsCommand = Command(db, tx, "SELECT payload FROM records WHERE " + filter +
                " ORDER BY id LIMIT $limit OFFSET $offset", [.. parameters, ("$limit", pageSize), ("$offset", offset)]);
            var rows = new List<object>();
            using (var reader = rowsCommand.ExecuteReader())
                while (reader.Read()) rows.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
            var complete = manifest.Inventory.Complete && manifest.Alerts.Complete && manifest.Metrics.Complete;
            var fresh = clock.GetUtcNow() - oldest <= TimeSpan.FromMinutes(settings.FreshMinutes);
            var provenance = new ReadModelProvenance(manifest.ManagementGroupId, manifest.ScopeKind, manifest.GroupId,
                manifest.MembershipVerified, manifest.ExpectedObjectIds.Length, manifest.Inventory,
                manifest.Alerts, manifest.Metrics, manifest.AlertHistoryStartUtc);
            return new(dataset, complete && fresh ? "ok" : "partial", provenance, count, pageSize,
                offset + rows.Count < count ? Cursor(dataset, binding, offset + rows.Count) : null, rows.ToArray());
        }
    }

    public Dataset ReadWindowsDataset()
    {
        lock (gate)
        {
            Guard.Source(settings.WindowsToolsEnabled, "INDEXED_WINDOWS_DISABLED");
            using var db = Open();
            using var tx = db.BeginTransaction();
            var datasetId = Scalar(db, tx, """
                SELECT runs.id FROM current JOIN runs ON runs.id=current.run
                WHERE current.available=1 AND runs.state='committed'
                """) as string;
            Guard.Source(datasetId is not null, "READ_MODEL_LATEST_COLLECTION_UNAVAILABLE");
            var manifest = ReadManifest(db, tx, datasetId);
            var oldest = new[] { manifest.Inventory.AsOfUtc, manifest.Alerts.AsOfUtc, manifest.Metrics.AsOfUtc }.Min();
            Guard.Source(clock.GetUtcNow() - oldest <= TimeSpan.FromMinutes(settings.UnavailableMinutes), "READ_MODEL_EXPIRED");
            if (cachedWindowsDataset is { } cached && cached.Snapshot.DatasetId == datasetId)
                return WindowsFreshness(cached, oldest);
            var text = Scalar(db, tx, "SELECT payload FROM records WHERE run=$run AND kind='windowsSnapshot' AND id='view'",
                ("$run", datasetId)) as string;
            Guard.Source(text is not null && manifest.ExpectedComputerIds is { IsDefault: false }, "WINDOWS_PROJECTION_MISSING");
            var dataset = SnapshotReader.ValidateIndexed(Encoding.UTF8.GetBytes(text),
                new("ok", manifest.Inventory.AsOfUtc, null, datasetId), ProjectionSourceSettings(), clock.GetUtcNow(),
                manifest.ExpectedComputerIds.Value.ToHashSet(StringComparer.Ordinal));
            cachedWindowsDataset = dataset;
            return WindowsFreshness(dataset, oldest);
        }
    }

    private ReadModelManifest ReadManifest(SqliteConnection db, SqliteTransaction tx, string id)
    {
        if (cachedManifest is { } cached && cached.Dataset == id) return cached.Manifest;
        var text = (string)Scalar(db, tx, "SELECT manifest FROM runs WHERE id=$id", ("$id", id))!;
        var manifest = Deserialize<ReadModelManifest>(Encoding.UTF8.GetBytes(text));
        cachedManifest = (id, manifest);
        return manifest;
    }

    private SourceSettings ProjectionSourceSettings() => new()
    {
        SnapshotEnabled = false, MaxSnapshotBytes = settings.MaxDatasetBytes,
        FreshMinutes = settings.FreshMinutes, UnavailableMinutes = settings.UnavailableMinutes,
        CursorLifetimeSeconds = source.CursorLifetimeSeconds
    };

    private Dataset WindowsFreshness(Dataset dataset, DateTimeOffset oldest) =>
        clock.GetUtcNow() - oldest > TimeSpan.FromMinutes(settings.FreshMinutes)
            ? dataset with { ValidationLimitations = dataset.ValidationLimitations.Add("INDEXED_DOMAIN_STALE") }
            : dataset;

    private static bool IsClass(ObservedObject value, string name) =>
        value.ClassName == name || value.BaseClasses.Contains(name, StringComparer.Ordinal);

    private Dataset BuildWindowsDataset(string id, ReadModelManifest manifest, List<ObservedObject> objects,
        List<ObservedRelationship> relationships, List<ObservedMetric> metrics, List<Device> devices, List<Volume> volumes)
    {
        Require(manifest.ExpectedComputerIds is { IsDefault: false }, "EXPECTED_COMPUTER_MEMBERSHIP_REQUIRED");
        var expectedArray = manifest.ExpectedComputerIds!.Value;
        foreach (var expectedId in expectedArray) Id(expectedId);
        var expected = expectedArray.ToHashSet(StringComparer.Ordinal);
        Require(expected.Count == expectedArray.Length && expected.IsSubsetOf(manifest.ExpectedObjectIds),
            "EXPECTED_COMPUTER_MEMBERSHIP_INVALID");
        var served = objects.ToDictionary(o => o.Id, StringComparer.Ordinal);
        foreach (var device in devices) Id(device.EntityId);
        foreach (var volume in volumes) Id(volume.EntityId);
        Unique(devices.Select(d => d.EntityId)); Unique(volumes.Select(v => v.EntityId));
        foreach (var device in devices)
            Require(served.TryGetValue(device.EntityId, out var observed) && expected.Contains(device.EntityId) &&
                device.Health == observed.Health, "WINDOWS_DEVICE_OUTSIDE_SCOPE_OR_HEALTH_MISMATCH");
        foreach (var volume in volumes)
            Require(served.ContainsKey(volume.EntityId), "WINDOWS_VOLUME_OUTSIDE_SCOPE");
        var deviceIds = devices.Select(d => d.EntityId).ToHashSet(StringComparer.Ordinal);
        var volumeIds = volumes.Select(v => v.EntityId).ToHashSet(StringComparer.Ordinal);
        Require(!manifest.Inventory.Complete || deviceIds.SetEquals(expected), "WINDOWS_COMPLETE_INVENTORY_MISMATCH");
        var osParents = new Dictionary<string, string>(StringComparer.Ordinal);
        if (settings.WindowsMetricHosting is { } hosting)
        {
            var computerChildren = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in relationships.Where(e => e.ClassName == hosting.RelationshipClass))
            {
                Require(deviceIds.Contains(edge.SourceId) && served.TryGetValue(edge.SourceId, out var computer) &&
                    IsClass(computer, hosting.ComputerClass) && served.TryGetValue(edge.TargetId, out var os) &&
                    IsClass(os, hosting.OperatingSystemClass) && !deviceIds.Contains(edge.TargetId) &&
                    !volumeIds.Contains(edge.TargetId), "WINDOWS_METRIC_HOSTING_INVALID");
                Require(computerChildren.Add(edge.SourceId) && osParents.TryAdd(edge.TargetId, edge.SourceId),
                    "WINDOWS_METRIC_HOSTING_AMBIGUOUS");
            }
        }
        var samples = new List<Sample>();
        foreach (var metric in metrics)
        {
            if (metric.Metric is not ("cpuPercent" or "memoryUsedPercent" or "memoryAvailableMiB" or "diskFreePercent")) continue;
            var parent = metric.ObjectId;
            if (metric.Metric != "diskFreePercent" && osParents.TryGetValue(parent, out var computerId))
                parent = computerId;
            var eligible = metric.Metric == "diskFreePercent" ? volumeIds : deviceIds;
            if (!eligible.Contains(parent)) continue;
            Require(metric.Unit == (metric.Metric == "memoryAvailableMiB" ? "MiB" : "percent"), "WINDOWS_METRIC_UNIT_INVALID");
            samples.Add(new(parent, metric.Metric, metric.ObservedAtUtc, metric.Value, metric.RuleId, metric.Optimized));
        }
        var oldest = new[] { manifest.Inventory.AsOfUtc, manifest.Alerts.AsOfUtc, manifest.Metrics.AsOfUtc }.Min();
        var limits = new List<string> { "INDEXED_WINDOWS_VIEW_NOT_ALL_MONITORED_OBJECTS", "MONITORING_HEALTH_DOES_NOT_PROVE_CONNECTIVITY" };
        if (!manifest.Inventory.Complete) limits.Add("INVENTORY_DOMAIN_INCOMPLETE");
        if (!manifest.Alerts.Complete) limits.Add("ALERT_DOMAIN_INCOMPLETE");
        if (!manifest.Metrics.Complete) limits.Add("METRIC_DOMAIN_INCOMPLETE");
        var snapshot = new Snapshot("1.0", id, "scom",
            manifest.Inventory.Complete && manifest.Alerts.Complete && manifest.Metrics.Complete ? "ok" : "partial",
            new(settings.ManagementGroupId, "configured-source"), new(Wire.ScopeId, "approved-scope"),
            manifest.Metrics.AsOfUtc, oldest, expected.Count, devices.ToImmutableArray(), volumes.ToImmutableArray(),
            samples.ToImmutableArray(), new(["WindowsComputer", "OperatingSystem", "LogicalDisk"],
                samples.Select(s => s.Metric).Distinct(StringComparer.Ordinal).ToImmutableArray(), false, false),
            limits.ToImmutableArray(), ["SCOM:" + settings.ManagementGroupId]);
        Require(clock.GetUtcNow() - oldest <= TimeSpan.FromMinutes(settings.UnavailableMinutes), "WINDOWS_DOMAINS_EXPIRED");
        var validated = SnapshotReader.ValidateIndexed(Encoding.UTF8.GetBytes(Wire.Encode(snapshot)),
            new("ok", manifest.Inventory.AsOfUtc, null, id), ProjectionSourceSettings(), clock.GetUtcNow(), expected);
        return validated with { Fingerprint = Hash(Encoding.UTF8.GetBytes(Wire.Encode(validated.Snapshot))) };
    }

    private void Validate(ReadModelManifest m, List<ObservedObject> objects, List<ObservedRelationship> edges,
        List<ObservedAlert> alerts, List<ObservedMetric> metrics)
    {
        Require(m.ManagementGroupId == settings.ManagementGroupId && m.ScopeKind == settings.ScopeKind &&
            m.GroupId == settings.GroupId && m.MembershipVerified, "SCOPE_UNVERIFIED_OR_MISMATCH");
        Require(!m.ExpectedObjectIds.IsDefault && m.ExpectedObjectIds.Length <= settings.MaxRecords, "MEMBERSHIP_INVALID");
        foreach (var id in m.ExpectedObjectIds) Id(id);
        var expected = m.ExpectedObjectIds.ToHashSet(StringComparer.Ordinal);
        Require(expected.Count == m.ExpectedObjectIds.Length, "MEMBERSHIP_DUPLICATE");
        Domain(m.Inventory); Domain(m.Alerts); Domain(m.Metrics);
        Time(m.AlertHistoryStartUtc);
        Require(m.AlertHistoryStartUtc <= m.Alerts.AsOfUtc, "ALERT_HISTORY_INVALID");
        foreach (var o in objects)
        {
            Id(o.Id); Text(o.ClassName, 256); Text(o.DisplayName, 512); Time(o.ObservedAtUtc);
            Require(o.ObservedAtUtc <= m.Inventory.AsOfUtc && o.Health is "healthy" or "warning" or "critical" or "unknown" &&
                !o.BaseClasses.IsDefault && o.BaseClasses.Length <= 32 && o.Properties is not null &&
                o.Properties.Count <= 64, "OBJECT_INVALID");
            foreach (var name in o.BaseClasses) Text(name, 256);
            foreach (var property in o.Properties!)
            {
                Text(property.Key, 128);
                Require(settings.AllowedPropertiesByClass.TryGetValue(o.ClassName, out var allowed) &&
                    allowed.Contains(property.Key, StringComparer.Ordinal) &&
                    (property.Value is null || property.Value.Length <= 2000), "PROPERTY_NOT_APPROVED_OR_OVERSIZED");
            }
        }
        var ids = objects.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        Require(ids.Count == objects.Count && ids.IsSubsetOf(expected) &&
            (!m.Inventory.Complete || ids.SetEquals(expected)), "OBJECT_MEMBERSHIP_INVALID");
        var classes = objects.ToDictionary(o => o.Id, o => o.ClassName, StringComparer.Ordinal);
        Unique(edges.Select(e => e.Id)); Unique(alerts.Select(a => a.Id)); Unique(metrics.Select(v => v.Id));
        foreach (var e in edges)
        {
            Id(e.Id); Text(e.ClassName, 256);
            Require(ids.Contains(e.SourceId) && ids.Contains(e.TargetId), "RELATIONSHIP_OUTSIDE_SCOPE");
        }
        foreach (var a in alerts)
        {
            Id(a.Id); Text(a.Name, 512); Time(a.ObservedAtUtc);
            Require(ids.Contains(a.ObjectId) && a.ObservedAtUtc <= m.Alerts.AsOfUtc &&
                a.Severity is "information" or "warning" or "critical" && a.ResolutionState is >= 0 and <= 255 &&
                (a.Description is null || settings.IncludeAlertDescriptions && a.Description.Length <= 2000), "ALERT_INVALID");
        }
        var samples = new HashSet<(string, string, DateTimeOffset, string)>();
        foreach (var v in metrics)
        {
            Id(v.Id); Text(v.Metric, 128); Text(v.Unit, 64); Text(v.RuleId, 500); Time(v.ObservedAtUtc);
            Require(ids.Contains(v.ObjectId) && double.IsFinite(v.Value) &&
                settings.AllowedMetricsByClass.TryGetValue(classes[v.ObjectId], out var approved) &&
                approved.TryGetValue(v.Metric, out var unit) && unit == v.Unit &&
                (v.Unit != "percent" || v.Value is >= 0 and <= 100) &&
                v.ObservedAtUtc <= m.Metrics.AsOfUtc &&
                v.ObservedAtUtc >= m.Metrics.AsOfUtc.AddDays(-14) &&
                samples.Add((v.ObjectId, v.Metric, v.ObservedAtUtc, v.RuleId)), "METRIC_INVALID_OR_DUPLICATE");
        }
    }

    private void Domain(DomainObservation? d)
    {
        Require(d is not null, "COVERAGE_MISSING");
        Time(d!.AsOfUtc);
        Require(d.Complete ? d.Reason is null : !string.IsNullOrWhiteSpace(d.Reason) && d.Reason.Length <= 512,
            "COVERAGE_INVALID");
    }
    private void Time(DateTimeOffset time) => Require(time != default && time.Offset == TimeSpan.Zero &&
        time <= clock.GetUtcNow().AddMinutes(2), "TIME_INVALID");
    private static void Unique(IEnumerable<string> ids) =>
        Require(ids.Distinct(StringComparer.Ordinal).Count() == ids.Count(), "RECORD_DUPLICATE");
    private static void Text(string? value, int max) =>
        Require(!string.IsNullOrWhiteSpace(value) && value.Length <= max, "TEXT_INVALID");
    private static void Id(string id) => Require(ReadModelSettings.ValidId(id) &&
        id == Guid.Parse(id).ToString("D"), "ID_INVALID");
    private void Publisher(string publisher) => Require(settings.PublisherObjectIds.Contains(publisher), "PUBLISHER_FORBIDDEN");
    private static void Require(bool condition, string code)
    {
        if (!condition) throw new QueryFault("INGESTION_REJECTED", code);
    }
    private IngestionBatch ParseBatch(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var count = 0L;
            foreach (var name in new[] { "objects", "relationships", "alerts", "metrics" })
            {
                Require(document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array,
                    "BATCH_ARRAYS_INVALID");
                count += document.RootElement.GetProperty(name).GetArrayLength();
            }
            if (document.RootElement.TryGetProperty("windowsInventory", out var windowsJson) && windowsJson.ValueKind != JsonValueKind.Null)
            {
                Require(windowsJson.ValueKind == JsonValueKind.Object, "WINDOWS_BATCH_INVALID_OR_DISABLED");
                foreach (var name in new[] { "devices", "volumes" })
                {
                    Require(windowsJson.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array,
                        "WINDOWS_BATCH_INVALID_OR_DISABLED");
                    count += array.GetArrayLength();
                }
            }
            Require(count <= settings.MaxRecords, "RECORD_COUNT_LIMIT");
        }
        catch (JsonException) { throw new QueryFault("INGESTION_REJECTED", "JSON_INVALID"); }
        var b = Deserialize<IngestionBatch>(bytes);
        Require(!b.Objects.IsDefault && !b.Relationships.IsDefault && !b.Alerts.IsDefault && !b.Metrics.IsDefault &&
            b.Objects.All(o => o is not null) && b.Relationships.All(e => e is not null) &&
            b.Alerts.All(a => a is not null) && b.Metrics.All(m => m is not null), "BATCH_ARRAYS_INVALID");
        if (b.WindowsInventory is { } windows)
            Require(settings.WindowsToolsEnabled && !windows.Devices.IsDefault && !windows.Volumes.IsDefault &&
                windows.Devices.All(d => d is not null) && windows.Volumes.All(v => v is not null), "WINDOWS_BATCH_INVALID_OR_DISABLED");
        return b;
    }
    private static long CountRecords(IngestionBatch batch) =>
        (long)batch.Objects.Length + batch.Relationships.Length + batch.Alerts.Length + batch.Metrics.Length +
        (batch.WindowsInventory?.Devices.Length ?? 0) + (batch.WindowsInventory?.Volumes.Length ?? 0);
    private static T Deserialize<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            SnapshotReader.RejectDuplicateProperties(document.RootElement);
            return document.RootElement.Deserialize<T>(Wire.Json) ?? throw new JsonException();
        }
        catch (JsonException) { throw new QueryFault("INGESTION_REJECTED", "JSON_INVALID"); }
    }
    private void Staging(SqliteConnection db, SqliteTransaction tx, string id, string publisher)
    {
        Publisher(publisher);
        var run = Run(db, tx, id);
        Require(run is not null && run.State == "staging" && run.Publisher == publisher &&
            run.Expires > clock.GetUtcNow(), "SESSION_INVALID_OR_EXPIRED");
    }
    private sealed record RunInfo(string Publisher, string State, DateTimeOffset Expires, string? Digest);
    private static RunInfo? Run(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var command = Command(db, tx, "SELECT publisher,state,expires,digest FROM runs WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? null : reader.GetString(3)) : null;
    }
    private sealed record CursorData(string Dataset, string Binding, int Offset, long Expires);
    private string Cursor(string dataset, string binding, int offset)
    {
        var bytes = Encoding.UTF8.GetBytes(Wire.Encode(new CursorData(dataset, Hash(Encoding.UTF8.GetBytes(binding)),
            offset, clock.GetUtcNow().AddSeconds(source.CursorLifetimeSeconds).ToUnixTimeSeconds())));
        return Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(cursorKey, bytes));
    }
    private int ReadCursor(string? cursor, string dataset, string binding)
    {
        if (cursor is null) return 0;
        try
        {
            if (cursor.Length > 4096) throw new FormatException();
            var parts = cursor.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var bytes = Convert.FromBase64String(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(parts[1]), HMACSHA256.HashData(cursorKey, bytes)))
                throw new FormatException();
            var data = JsonSerializer.Deserialize<CursorData>(bytes, Wire.Json) ?? throw new FormatException();
            if (data.Binding != Hash(Encoding.UTF8.GetBytes(binding)) || data.Offset < 0 ||
                data.Expires <= clock.GetUtcNow().ToUnixTimeSeconds()) throw new FormatException();
            if (data.Dataset != dataset) throw new QueryFault("CURSOR_DATASET_CHANGED", "Dataset changed; restart paging.");
            return data.Offset;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        { throw new QueryFault("INVALID_CURSOR", "Cursor is invalid or expired."); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        return command;
    }
    private static void Execute(SqliteConnection db, string sql) => Execute(db, null, sql);
    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(db, tx, sql, parameters);
        command.ExecuteNonQuery();
    }
    private static object? Scalar(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(db, tx, sql, parameters);
        return command.ExecuteScalar();
    }
}
