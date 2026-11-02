using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ScomMcp;

// The worker owns this store; it must never share the MCP read-model database.
public sealed class SourceCheckpointStore : IDisposable
{
    private readonly FileStream lease;
    private readonly SqliteConnection connection;
    private readonly int maxBytes;
    private readonly string policy;
    private bool disposed;

    public SourceCheckpointStore(string path, string profilePolicyHash, int maxBytes = 32 * 1024 * 1024)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            profilePolicyHash is null || profilePolicyHash.Length != 64 ||
            profilePolicyHash.Any(c => !char.IsAsciiHexDigit(c)) ||
            maxBytes is < 1024 or > 64 * 1024 * 1024)
            throw new InvalidOperationException("Worker checkpoint requires local storage, bounded bytes and a profile policy SHA256.");
        var root = Path.GetPathRoot(path)!;
        if (OperatingSystem.IsWindows() && new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidOperationException("Worker checkpoint must not use a network drive.");
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
            throw new InvalidOperationException("Provision a protected worker checkpoint directory before starting.");
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Worker checkpoint directories must not traverse reparse points.");
        foreach (var file in new[] { path, path + ".lock", path + "-journal", path + "-wal", path + "-shm" })
            if (File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Worker checkpoint files must not be reparse points.");
        this.maxBytes = maxBytes;
        policy = profilePolicyHash.ToLowerInvariant();
        lease = new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            var applicationId = Convert.ToInt64(Scalar("PRAGMA application_id;"));
            if (applicationId != 0x53434F57 &&
                (applicationId != 0 || Convert.ToInt64(Scalar("SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';")) != 0))
                throw new InvalidOperationException("Worker checkpoint path belongs to another database.");
            Execute("PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
            var pageSize = Convert.ToInt64(Scalar("PRAGMA page_size;"));
            var maxPages = (64 * 1024 + 4L * maxBytes + pageSize - 1) / pageSize;
            if (Convert.ToInt64(Scalar("PRAGMA page_count;")) > maxPages)
                throw new InvalidOperationException("Worker checkpoint exceeds its physical storage budget.");
            Execute($"PRAGMA max_page_count={maxPages};");
            if (!string.Equals(Convert.ToString(Scalar("PRAGMA quick_check;")), "ok", StringComparison.Ordinal))
                throw new InvalidOperationException("Worker checkpoint integrity check failed.");
            Execute($"PRAGMA application_id={0x53434F57};");
            Execute("CREATE TABLE IF NOT EXISTS source_checkpoint (id INTEGER PRIMARY KEY CHECK(id=1), " +
                "version INTEGER NOT NULL, policy TEXT NOT NULL, settings TEXT NOT NULL, digest BLOB NOT NULL, payload BLOB NOT NULL);");
        }
        catch
        {
            connection.Dispose();
            lease.Dispose();
            throw;
        }
    }

    internal ReconciledSourceState? Load(string settingsHash)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, policy, settings, length(payload), digest, payload FROM source_checkpoint WHERE id=1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetInt32(0) != 1 || reader.GetString(1) != policy || reader.GetString(2) != settingsHash ||
            reader.GetInt64(3) > maxBytes)
            throw new InvalidOperationException("Worker checkpoint version, profile policy, settings or byte budget differs.");
        var payload = (byte[])reader[5];
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), (byte[])reader[4]))
            throw new InvalidOperationException("Worker checkpoint digest mismatch.");
        using var document = JsonDocument.Parse(payload);
        SnapshotReader.RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<ReconciledSourceState>(Wire.Json) ??
            throw new InvalidOperationException("Worker checkpoint is empty.");
    }

    internal void Save(ReconciledSourceState state, string settingsHash)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var stream = new LimitedBuffer(maxBytes);
        JsonSerializer.Serialize(stream, state, Wire.Json);
        var bytes = stream.ToArray();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO source_checkpoint VALUES(1,1,$policy,$settings,$digest,$payload) " +
            "ON CONFLICT(id) DO UPDATE SET settings=excluded.settings, digest=excluded.digest, payload=excluded.payload " +
            "WHERE source_checkpoint.version=1 AND source_checkpoint.policy=excluded.policy AND source_checkpoint.settings=excluded.settings;";
        command.Parameters.AddWithValue("$policy", policy);
        command.Parameters.AddWithValue("$settings", settingsHash);
        command.Parameters.AddWithValue("$digest", SHA256.HashData(bytes));
        command.Parameters.AddWithValue("$payload", bytes);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Worker checkpoint policy changed.");
        transaction.Commit();
    }

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        connection.Dispose();
        lease.Dispose();
    }

    private sealed class LimitedBuffer(int limit) : Stream
    {
        private readonly MemoryStream buffer = new();
        public byte[] ToArray() => buffer.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => buffer.Length;
        public override long Position { get => buffer.Position; set => throw new NotSupportedException(); }
        public override void Flush() => buffer.Flush();
        public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            if (Length + bytes.Length > limit)
                throw new IOException("Worker checkpoint encoded-byte budget exceeded.");
            buffer.Write(bytes);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) buffer.Dispose();
            base.Dispose(disposing);
        }
    }
}
