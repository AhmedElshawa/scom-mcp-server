using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using ScomMcp;

static class SourceCheckpointChecks
{
    public static async Task Run(string root)
    {
        var count = 0;
        const string policy = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string mg = "22222222-2222-4222-8222-222222222222";
        const string id = "00000000-0000-0000-0000-000000000001";
        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new Clock { Now = now };
        var entity = new ObservedObject(id, "Synthetic.Entity", [], "synthetic", "unknown", now,
            ImmutableDictionary<string, string?>.Empty);
        var alert = new ObservedAlert("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", id, "synthetic alert", null, "warning", 0, now);
        var point = new ObservedMetric("cccccccc-cccc-4ccc-8ccc-cccccccccccc", id, "count", "count", now, 2, "rule", false);
        var series = new MetricSeriesScan(id, "count", "count", "rule", now.AddDays(-14), now, true, null, [point]);
        var scan = new NormalizedSourceScan(mg, "managementGroup", null, true, [id], [entity], [],
            new(now, true, null), [alert], new(now, true, null), [series], now);
        var profile = new Profile { Scan = scan };
        var path = Path.Combine(root, "worker-checkpoint.db");
        string reserved;
        using (var store = new SourceCheckpointStore(path, policy))
        {
            var source = new ReconciledCollectionSource(profile, new(), clock, store);
            reserved = await source.ReserveSessionAsync(CancellationToken.None);
            Check(profile.Calls == 0, "reservation is durable before source access");
            Reject(() => { using var duplicate = new SourceCheckpointStore(path, policy); },
                "two worker owners cannot open the same checkpoint");
        }
        PreparedCollection prepared;
        string[] bytes;
        using (var store = new SourceCheckpointStore(path, policy))
        {
            var source = new ReconciledCollectionSource(profile, new(), clock, store);
            Check(await source.ReserveSessionAsync(CancellationToken.None) == reserved, "reservation survives restart");
            prepared = await source.PrepareAsync(CancellationToken.None);
            bytes = await Encoded(prepared);
            Check(prepared.SessionId == reserved && profile.Calls == 1, "preparation retains reserved identity");
        }
        await ReadModelFaultChecks.Crash("sourceCheckpoint", path, reserved);
        Check(File.Exists(path + "-journal"), "killed checkpoint writer leaves a real rollback journal");
        profile.Fail = true;
        using (var store = new SourceCheckpointStore(path, policy))
        {
            var source = new ReconciledCollectionSource(profile, new(), clock, store);
            var replay = await source.PrepareAsync(CancellationToken.None);
            Check(replay.SessionId == reserved && Wire.Encode(replay.Manifest) == Wire.Encode(prepared.Manifest) &&
                (await Encoded(replay)).SequenceEqual(bytes) && profile.Calls == 1,
                "restart replays exact pending identity, manifest and bytes without source queries");
            Sql("CREATE TRIGGER checkpoint_fault BEFORE UPDATE ON source_checkpoint BEGIN SELECT RAISE(ABORT,'synthetic checkpoint failure'); END;");
            try { await source.AcknowledgeAsync(reserved, CancellationToken.None); throw new Exception("Failed checkpoint acknowledged."); }
            catch (SqliteException) { count++; }
            Check((await source.PrepareAsync(CancellationToken.None)).SessionId == reserved && profile.Calls == 1,
                "failed acknowledgement retains the pending in-memory preparation");
            Sql("DROP TRIGGER checkpoint_fault;");
        }
        using (var store = new SourceCheckpointStore(path, policy))
        {
            var source = new ReconciledCollectionSource(profile, new(), clock, store);
            var replay = await source.PrepareAsync(CancellationToken.None);
            Check(replay.SessionId == reserved && (await Encoded(replay)).SequenceEqual(bytes) && profile.Calls == 1,
                "failed checkpoint transaction retains exact pending replay after restart");
            await source.AcknowledgeAsync(reserved, CancellationToken.None);
            await source.AcknowledgeAsync(reserved, CancellationToken.None);
        }
        profile.Fail = false;
        var later = now.AddMinutes(1);
        clock.Now = later;
        profile.Scan = scan with
        {
            Inventory = new(later, true, null), Alerts = new(later, true, null), AlertObservations = [],
            MetricsAsOfUtc = later, MetricSeries = [series with { StartUtc = now, EndUtc = later, Observations = [point with { Value = 3 }] }]
        };
        using (var store = new SourceCheckpointStore(path, policy))
        {
            var source = new ReconciledCollectionSource(profile, new(), clock, store);
            await source.AcknowledgeAsync(reserved, CancellationToken.None);
            var nextId = await source.ReserveSessionAsync(CancellationToken.None);
            var next = await source.PrepareAsync(CancellationToken.None);
            Check(nextId != reserved && next.SessionId == nextId && profile.Calls == 2,
                "acknowledgement survives restart and next collection reserves a new identity");
            Check(profile.Request is { } context && context.PriorUnresolvedAlerts.Single() ==
                new AlertReassessment(alert.Id, id) && context.MetricHistoryStartUtc == later.AddDays(-14),
                "restarted profile receives durable alert reassessment and raw-history context");
            Check(next.Manifest.Alerts.Reason == "PRIOR_ALERTS_NOT_REASSESSED" && next.Manifest.AlertHistoryStartUtc == now,
                "unresolved alert reconciliation and honest history start survive restart");
            var nextBatches = await Batches(next);
            Check(nextBatches.SelectMany(b => b.Alerts).Single().Id == alert.Id &&
                nextBatches.SelectMany(b => b.Metrics).Single().Value == 3,
                "retained unresolved alert and raw metric correction survive restart");
        }
        using (var store = new SourceCheckpointStore(path, policy))
            Reject(() => _ = new ReconciledCollectionSource(profile, new() { BatchRecords = 10 }, clock, store),
                "changed reconciliation or serialization settings cannot replay old checkpoint");
        using (var store = new SourceCheckpointStore(path, new string('b', 64)))
            Reject(() => _ = new ReconciledCollectionSource(profile, new(), clock, store),
                "changed source profile policy cannot reuse customer state");
        var smallPath = Path.Combine(root, "worker-small-checkpoint.db");
        using (var store = new SourceCheckpointStore(smallPath, policy, 1024))
        {
            var source = new ReconciledCollectionSource(profile, new(), clock, store);
            var session = await source.ReserveSessionAsync(CancellationToken.None);
            try { await source.PrepareAsync(CancellationToken.None); throw new Exception("Checkpoint byte overflow accepted."); }
            catch (IOException) { count++; }
            Check(await source.ReserveSessionAsync(CancellationToken.None) == session,
                "failed durable preparation keeps reserved identity without checkpoint advancement");
        }
        using (var store = new SourceCheckpointStore(smallPath, policy, 1024))
        {
            profile.Fail = true;
            var source = new ReconciledCollectionSource(profile, new(), clock, store);
            try { await source.PrepareAsync(CancellationToken.None); throw new Exception("Failed preparation persisted as success."); }
            catch (QueryFault) { count++; }
            profile.Fail = false;
        }
        Sql("UPDATE source_checkpoint SET digest=zeroblob(32);");
        using (var store = new SourceCheckpointStore(path, policy))
            Reject(() => _ = new ReconciledCollectionSource(profile, new(), clock, store),
                "corrupt checkpoint rejects startup without bootstrap fallback");
        Reject(() => { using var store = new SourceCheckpointStore(@"\\synthetic.invalid\share\state.db", policy); },
            "network checkpoint path denied");
        var foreignPath = Path.Combine(root, "not-a-worker-checkpoint.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = foreignPath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('synthetic');";
            command.ExecuteNonQuery();
            Reject(() => { using var store = new SourceCheckpointStore(foreignPath, policy); },
                "worker checkpoint refuses another application's database");
            command.CommandText = "PRAGMA journal_mode;";
            Check((string?)command.ExecuteScalar() == "wal", "rejected foreign database journal mode stays unchanged");
            command.CommandText = "SELECT value FROM unrelated;";
            Check((string?)command.ExecuteScalar() == "synthetic", "rejected foreign database records stay unchanged");
        }
        Console.WriteLine($"PASS: {count} durable worker checkpoint assertions; restart/replay, history, policy, single ownership, byte limits and corruption.");

        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++;
        }
        void Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidOperationException or IOException) { count++; return; }
            throw new InvalidOperationException(label);
        }
        void Sql(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private static async Task<List<IngestionBatch>> Batches(PreparedCollection prepared)
    {
        var batches = new List<IngestionBatch>();
        await foreach (var batch in prepared.Batches) batches.Add(batch);
        return batches;
    }

    private static async Task<string[]> Encoded(PreparedCollection prepared) => (await Batches(prepared)).Select(Wire.Encode).ToArray();

    public static int Child(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA cache_size=1;";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "UPDATE source_checkpoint SET payload=zeroblob(262144), digest=zeroblob(32);";
        command.ExecuteNonQuery();
        ReadModelFaultChecks.ReadyAndWait();
        return 1;
    }

    private sealed class Profile : IScomNormalizationProfile
    {
        public required NormalizedSourceScan Scan;
        public bool Fail;
        public int Calls;
        public SourceCollectionRequest? Request;
        public Task<NormalizedSourceScan> CollectAsync(SourceCollectionRequest request, CancellationToken cancellationToken)
        {
            if (Fail) throw new QueryFault("SOURCE_UNAVAILABLE", "SYNTHETIC_SOURCE_FAILURE");
            Request = request;
            Calls++;
            return Task.FromResult(Scan);
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
