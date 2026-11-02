using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using ScomMcp;

static class ReadModelFaultChecks
{
    private const string Publisher = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string Group = "22222222-2222-4222-8222-222222222222";
    private const string ObjectId = "00000000-0000-0000-0000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly Caller Caller = new("synthetic", ImmutableHashSet.Create(Wire.ScopeId));
    private static readonly IngestionBatch Batch = new(
        [new(ObjectId, "Synthetic.Entity", [], "synthetic", "unknown", Now,
            ImmutableDictionary<string, string?>.Empty)], [], [], []);
    private static readonly ReadModelManifest Manifest = new(Group, "managementGroup", null, true,
        [ObjectId], new(Now, true, null), new(Now, true, null), new(Now, true, null), Now);
    private static int assertions;

    public static async Task Run(string root)
    {
        Ownership(root);
        var path = Path.Combine(root, "crash-recovery.db");
        var store = Store(path);
        var first = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, first);
        store.Append(Publisher, first, 0, Bytes(Batch));
        store.Commit(Publisher, first, Bytes(Manifest));
        await Crash("transaction", path, first);
        var recovered = Store(path);
        var page = recovered.Page(Caller, "objects", null, 20, null);
        Check(page.DatasetId == first && page.TotalCount == 1 && page.Rows.Length == 1,
            "process death rolls back uncommitted activation and spilled rows");
        Check(Integrity(path), "SQLite recovery integrity");
        var pending = Guid.NewGuid().ToString("D");
        await Crash("staging", path, pending);
        var staging = Store(path);
        Fault(() => staging.Page(Caller, "objects", null, 20, null), "acknowledged staging stays unavailable after process death");
        staging.Append(Publisher, pending, 0, Bytes(Batch));
        staging.Commit(Publisher, pending, Bytes(Manifest));
        Check(staging.Page(Caller, "objects", null, 20, null).DatasetId == pending,
            "acknowledged batch survives process death and identical retry");
        Check(Integrity(path), "post-retry SQLite integrity");
        var limited = Settings(path);
        limited.MaxBatchBytes = 1024;
        limited.MaxDatasetBytes = 1024;
        limited.MaxDatabaseBytes = 4096;
        Throws(() => new ReadModelStore(limited, new() { SnapshotEnabled = false }, new Clock()),
            "existing database larger than budget is refused");
        Check(Store(path).Page(Caller, "objects", null, 20, null).DatasetId == pending,
            "rejected smaller budget does not destroy activation");
        DiskFull(root);
        Console.WriteLine($"PASS: {assertions} read-model recovery/ownership assertions; foreign refusal, atomic migration, pinned state, killed writer, rollback journal, acknowledged staging and existing disk budget.");
    }

    private static void Ownership(string root)
    {
        const string policy = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const int receiverId = 0x53434F52;
        var foreign = Path.Combine(root, "receiver-foreign.db");
        using (var db = Open(foreign))
            Sql(db, "CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('synthetic');");
        RefusedUnchanged(foreign, () => Store(foreign), "foreign rollback database");
        var empty = Path.Combine(root, "receiver-existing-empty.db");
        using (var db = Open(empty)) Sql(db, "VACUUM;");
        RefusedUnchanged(empty, () => Store(empty), "unclaimed empty database");
        var zero = Path.Combine(root, "receiver-existing-zero.db");
        using (var file = new FileStream(zero, FileMode.CreateNew)) { }
        RefusedUnchanged(zero, () => Store(zero), "preexisting zero-byte file");
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
        {
            var orphan = Path.Combine(root, "receiver-orphan" + suffix + ".db");
            File.WriteAllBytes(orphan + suffix, [1, 2, 3]);
            RefusedUnchanged(orphan, () => Store(orphan), "orphaned " + suffix);
        }
        var wal = Path.Combine(root, "receiver-foreign-wal.db");
        using (var db = Open(wal))
        {
            Sql(db, "PRAGMA journal_mode=WAL; CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('synthetic');");
            RefusedUnchanged(wal, () => Store(wal), "foreign WAL database with live sidecars");
            Check((string?)Value(db, "PRAGMA journal_mode") == "wal", "foreign WAL journal mode is unchanged");
        }
        RefusedUnchanged(wal, () => Store(wal), "foreign WAL database without live sidecars");

        var checkpoint = Path.Combine(root, "receiver-worker.db");
        using (var worker = new SourceCheckpointStore(checkpoint, policy)) { }
        RefusedUnchanged(checkpoint, () => Store(checkpoint), "worker checkpoint");
        using (var db = Open(checkpoint)) Sql(db, "PRAGMA application_id=0;");
        RefusedUnchanged(checkpoint, () => Store(checkpoint), "unmarked worker checkpoint");

        var readModel = Path.Combine(root, "receiver-owned.db");
        var store = Store(readModel);
        var committed = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, committed);
        store.Append(Publisher, committed, 0, Bytes(Batch));
        store.Commit(Publisher, committed, Bytes(Manifest));
        using (var db = Open(readModel))
            Check(Convert.ToInt64(Value(db, "PRAGMA application_id")) == receiverId,
                "receiver application identity is distinct from worker identity");
        RefusedUnchanged(readModel, () => { using var worker = new SourceCheckpointStore(readModel, policy); },
            "worker reciprocally rejects receiver");
        var missing = Path.Combine(root, "receiver-missing-pinned.db");
        File.Copy(readModel, missing);
        var pinned = Store(missing);
        File.Move(missing, missing + ".saved");
        RefusedUnchanged(missing, () => pinned.Page(Caller, "objects", null, 20, null), "missing pinned database");
        File.Move(missing + ".saved", missing);
        Check(pinned.Page(Caller, "objects", null, 20, null).DatasetId == committed,
            "missing-file refusal does not recreate or reset pinned state");

        foreach (var legacyCount in new[] { false, true })
        {
            var prior = Path.Combine(root, $"receiver-prior-{legacyCount}.db");
            File.Copy(readModel, prior);
            var staging = Guid.NewGuid().ToString("D");
            var priorStore = Store(prior);
            priorStore.Begin(Publisher, staging);
            priorStore.Append(Publisher, staging, 0, Bytes(Batch));
            using (var db = Open(prior))
            {
                Sql(db, "PRAGMA application_id=0; PRAGMA user_version=0;");
                if (legacyCount) Sql(db, "ALTER TABLE batches DROP COLUMN record_count;");
            }
            RefusedUnchanged(prior, () => { using var worker = new SourceCheckpointStore(prior, policy); },
                "worker reciprocally rejects an unmarked prior read model");
            var different = Settings(prior);
            different.IncludeAlertDescriptions = true;
            RefusedUnchanged(prior, () => new ReadModelStore(different, new() { SnapshotEnabled = false }, new Clock()),
                "prior read model with a different configured scope");
            using (var db = Open(prior))
                Check((string?)Value(db, "SELECT run FROM current WHERE singleton=1") == committed &&
                    Convert.ToInt64(Value(db, "SELECT available FROM current WHERE singleton=1")) == 0 &&
                    Convert.ToInt64(Value(db, "SELECT count(*) FROM used_sessions")) == 2,
                    "refusal preserves committed identity, unavailable staging and consumed sessions");
            var upgraded = Store(prior);
            Fault(() => upgraded.Page(Caller, "objects", null, 20, null), "migration cannot expose old success over staging");
            using (var db = Open(prior))
            {
                Check(Convert.ToInt64(Value(db, "PRAGMA application_id")) == receiverId &&
                    Convert.ToInt64(Value(db, "PRAGMA user_version")) == 1, "known prior read model is explicitly adopted and versioned");
                Check(Convert.ToInt64(Value(db, "SELECT count(*) FROM batches WHERE record_count IS NULL AND run IN (SELECT id FROM runs WHERE state='staging')")) == (legacyCount ? 1 : 0),
                    "migration retains prior acknowledged payload without inventing record counts");
            }
            upgraded.Append(Publisher, staging, 0, Bytes(Batch));
            upgraded.Append(Publisher, staging, 1, Bytes(new IngestionBatch([], [], [], [])));
            upgraded.Commit(Publisher, staging, Bytes(Manifest));
            Check(Store(prior).Page(Caller, "objects", null, 20, null).DatasetId == staging,
                "prior staging bytes and exact replay remain usable after migration and restart");
            var currentMismatch = Settings(prior);
            currentMismatch.IncludeAlertDescriptions = true;
            RefusedUnchanged(prior, () => new ReadModelStore(currentMismatch, new() { SnapshotEnabled = false }, new Clock()),
                "versioned read model with different configured scope");
            Check(Store(prior).Page(Caller, "objects", null, 20, null).DatasetId == staging,
                "scope refusal preserves activated dataset");
        }

        foreach (var (name, sql) in new[]
        {
            ("future-version", "PRAGMA user_version=42"),
            ("wrong-owner", "PRAGMA application_id=1396920151"),
            ("missing-table", "DROP TABLE batches"),
            ("extra-table", "CREATE TABLE unrelated(value TEXT)"),
            ("missing-scope", "DELETE FROM configuration"),
            ("missing-current", "DELETE FROM current"),
            ("malformed-schema", "ALTER TABLE batches RENAME COLUMN digest TO unexpected")
        })
        {
            var path = Path.Combine(root, $"receiver-{name}.db");
            File.Copy(readModel, path);
            using (var db = Open(path)) Sql(db, sql);
            RefusedUnchanged(path, () => Store(path), name);
        }
        var failedInit = Path.Combine(root, "receiver-atomic-initialization.db");
        var limited = Settings(failedInit);
        limited.MaxBatchBytes = 1024;
        limited.MaxDatasetBytes = 1024;
        limited.MaxDatabaseBytes = 4096;
        try
        {
            _ = new ReadModelStore(limited, new() { SnapshotEnabled = false }, new Clock());
            throw new InvalidOperationException("Insufficient initialization budget accepted.");
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 13) { assertions++; }
        using (var db = Open(failedInit))
            Check(Convert.ToInt64(Value(db, "SELECT count(*) FROM sqlite_master")) == 0 &&
                Convert.ToInt64(Value(db, "PRAGMA application_id")) == 0 &&
                Convert.ToInt64(Value(db, "PRAGMA user_version")) == 0,
                "failed initialization rolls back schema and ownership together without deleting the file");
        MigrationRollback(root, readModel);

        void RefusedUnchanged(string path, Action action, string label)
        {
            var files = new[] { path, path + "-journal", path + "-wal", path + "-shm" };
            var before = files.Select(file => File.Exists(file) ? ReadBytes(file) : null).ToArray();
            Throws(action, label + " must be refused");
            for (var i = 0; i < files.Length; i++)
                Check(before[i] is { } bytes
                    ? File.Exists(files[i]) && bytes.SequenceEqual(ReadBytes(files[i]))
                    : !File.Exists(files[i]), label + " leaves database and journal/sidecar bytes unchanged");
        }

        static byte[] ReadBytes(string path)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }
    }

    private static void MigrationRollback(string root, string source)
    {
        // Vary insignificant schema whitespace to reach a real SQLite schema-page
        // boundary, rather than mocking the migration's transaction failure.
        for (var padding = 0; padding < 512; padding += 16)
        {
            var path = Path.Combine(root, $"receiver-migration-full-{padding}.db");
            File.Copy(source, path);
            long bytes;
            using (var db = Open(path))
            {
                Sql(db, "PRAGMA application_id=0; PRAGMA user_version=0; ALTER TABLE batches DROP COLUMN record_count; PRAGMA page_size=512; VACUUM;");
                Sql(db, $"PRAGMA writable_schema=ON; UPDATE sqlite_master SET sql=replace(sql,'payload BLOB','payload{new string(' ', padding)} BLOB') WHERE name='batches'; PRAGMA writable_schema=OFF; VACUUM;");
                bytes = Convert.ToInt64(Value(db, "PRAGMA page_count")) * 512;
            }
            var before = File.ReadAllBytes(path);
            var limited = Settings(path);
            limited.MaxBatchBytes = 1024;
            limited.MaxDatasetBytes = 1024;
            limited.MaxDatabaseBytes = checked((int)bytes);
            try { _ = new ReadModelStore(limited, new() { SnapshotEnabled = false }, new Clock()); }
            catch (SqliteException error) when (error.SqliteErrorCode == 13)
            {
                Check(before.SequenceEqual(File.ReadAllBytes(path)) && !File.Exists(path + "-journal"),
                    "failed migration restores database bytes and removes its rollback journal");
                using (var db = Open(path))
                    Check(Convert.ToInt64(Value(db, "PRAGMA application_id")) == 0 &&
                        Convert.ToInt64(Value(db, "PRAGMA user_version")) == 0 &&
                        Convert.ToInt64(Value(db, "SELECT count(*) FROM pragma_table_info('batches') WHERE name='record_count'")) == 0,
                        "failed migration rolls back schema and ownership together");
                Check(Store(path).Page(Caller, "objects", null, 20, null).TotalCount == 1,
                    "failed migration preserves pinned dataset for a later adequately budgeted retry");
                return;
            }
        }
        throw new InvalidOperationException("Synthetic schema-page boundary did not exercise migration rollback.");
    }

    private static void DiskFull(string root)
    {
        var path = Path.Combine(root, "disk-full.db");
        var settings = Settings(path);
        settings.MaxBatchBytes = 1024;
        settings.MaxDatasetBytes = 16384;
        settings.MaxDatabaseBytes = 65536;
        var store = new ReadModelStore(settings, new() { SnapshotEnabled = false }, new Clock());
        var first = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, first);
        store.Append(Publisher, first, 0, Bytes(Batch));
        store.Commit(Publisher, first, Bytes(Manifest));
        var failing = Guid.NewGuid().ToString("D");
        store.Begin(Publisher, failing);
        using (var db = Open(path))
        using (var fill = db.CreateCommand())
        {
            fill.CommandText = "CREATE TABLE validation_disk_fill (payload BLOB); PRAGMA max_page_count=16;";
            fill.ExecuteNonQuery();
            fill.CommandText = "INSERT INTO validation_disk_fill VALUES(zeroblob(4096))";
            var full = false;
            for (var i = 0; i < 16; i++)
            {
                try { fill.ExecuteNonQuery(); }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 13) { full = true; break; }
            }
            Check(full, "configured main-file page ceiling produces SQLite full failure");
        }
        var rejected = false;
        var failedSequence = -1;
        var empty = Bytes(new IngestionBatch([], [], [], []));
        var padded = Enumerable.Repeat((byte)' ', 1024).ToArray();
        empty.CopyTo(padded, 0);
        for (var sequence = 0; sequence < 16; sequence++)
        {
            try { store.Append(Publisher, failing, sequence, sequence == 0 ? Bytes(Batch) : padded); }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 13)
            {
                rejected = true;
                failedSequence = sequence;
                break;
            }
        }
        Check(rejected, "ingestion cannot acknowledge a batch when database is full");
        using (var db = Open(path))
        using (var count = db.CreateCommand())
        {
            count.CommandText = "SELECT count(*) FROM batches WHERE run=$id";
            count.Parameters.AddWithValue("$id", failing);
            Check(Convert.ToInt64(count.ExecuteScalar()) == failedSequence, "failed disk-full batch was not persisted");
        }
        Fault(() => store.Page(Caller, "objects", null, 20, null), "disk-full collection never falls back to old success");
        Check(Integrity(path), "disk-full transaction leaves SQLite integrity intact");
        using (var db = Open(path))
        using (var cleanup = db.CreateCommand())
        {
            cleanup.CommandText = "DROP TABLE validation_disk_fill";
            cleanup.ExecuteNonQuery();
        }
        var resumed = new ReadModelStore(settings, new() { SnapshotEnabled = false }, new Clock());
        resumed.Append(Publisher, failing, failedSequence, failedSequence == 0 ? Bytes(Batch) : padded);
        resumed.Commit(Publisher, failing, Bytes(Manifest));
        Check(resumed.Page(Caller, "objects", null, 20, null).DatasetId == failing,
            "same unacknowledged batch retry recovers after disk space is restored");
    }

    public static int Child(string mode, string path, string session)
    {
        if (mode == "checks")
        {
            Run(path).GetAwaiter().GetResult();
            return 0;
        }
        if (mode == "sourceCheckpoint") return SourceCheckpointChecks.Child(path);
        if (mode == "staging")
        {
            var store = Store(path);
            store.Begin(Publisher, session);
            store.Append(Publisher, session, 0, Bytes(Batch));
        }
        else if (mode == "transaction")
        {
            using var db = Open(path);
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL; PRAGMA cache_size=1;";
            command.ExecuteNonQuery();
            using var transaction = db.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = "UPDATE current SET available=0";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO records VALUES($run,'objects',$id,NULL,NULL,$payload)";
            command.Parameters.AddWithValue("$run", session);
            var id = command.Parameters.AddWithValue("$id", "");
            command.Parameters.AddWithValue("$payload", new string('x', 8192));
            for (var i = 0; i < 200; i++)
            {
                id.Value = "uncommitted-" + i;
                command.ExecuteNonQuery();
            }
            ReadyAndWait();
            return 1;
        }
        else throw new ArgumentException("Unknown validation fault mode.");
        ReadyAndWait();
        return 1;
    }

    internal static void ReadyAndWait()
    {
        Console.WriteLine("READY");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
    }

    internal static async Task Crash(string mode, string path, string session)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "--read-model-fault", mode, path, session })
            info.ArgumentList.Add(arg);
        using var child = Process.Start(info) ?? throw new InvalidOperationException("Fault-test child did not start.");
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (ready != "READY")
                throw new InvalidOperationException("Fault-test child failed: " + await child.StandardError.ReadToEndAsync());
            if (mode == "transaction")
                Check(File.Exists(path + "-journal"), "rollback journal exists before killed transaction");
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
    }

    private static bool Integrity(string path)
    {
        using var db = Open(path);
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        return (string?)command.ExecuteScalar() == "ok";
    }
    private static void Sql(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static object? Value(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }
    private static ReadModelSettings Settings(string path) => new()
    {
        Enabled = true, DatabasePath = path, ManagementGroupId = Group, ScopeKind = "managementGroup",
        PublisherObjectIds = [Publisher]
    };
    private static ReadModelStore Store(string path) => new(Settings(path), new() { SnapshotEnabled = false }, new Clock());
    private static byte[] Bytes<T>(T value) => Encoding.UTF8.GetBytes(Wire.Encode(value));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }
    private static void Fault(Action action, string message)
    {
        try { action(); }
        catch (QueryFault) { assertions++; return; }
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
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
