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
        Console.WriteLine($"PASS: {assertions} read-model recovery assertions; killed writer, rollback journal, acknowledged staging and existing disk budget.");
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
        Fault(() => Store(path).Page(Caller, "objects", null, 20, null), "disk-full collection never falls back to old success");
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
