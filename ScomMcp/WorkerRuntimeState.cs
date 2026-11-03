using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ScomMcp;

public sealed record WorkerRuntimeStatus(
    string State,
    DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastPublicationUtc,
    string? LastOutcome,
    int ConsecutiveDeferredAttempts,
    string? FailureCode,
    int ProcessId);

public sealed class WorkerRuntimeState : IDisposable
{
    private const int MaxStatusBytes = 8192;
    private readonly string statusPath;
    private readonly FileStream lease;
    private WorkerRuntimeStatus status;
    private bool disposed;

    public WorkerRuntimeState(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
            directory.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(directory))
            throw new InvalidOperationException("Worker runtime state requires an existing absolute local directory.");
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Worker runtime state directories must not traverse reparse points.");
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(directory)!).DriveType == DriveType.Network)
            throw new InvalidOperationException("Worker runtime state must not use a network drive.");

        statusPath = Path.Combine(directory, "worker.status.json");
        var leasePath = Path.Combine(directory, "worker.runtime.lock");
        RejectReparsePoint(statusPath);
        RejectReparsePoint(leasePath);
        lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var now = DateTimeOffset.UtcNow;
            status = File.Exists(statusPath)
                ? ReadStatus()
                : new WorkerRuntimeStatus("stopped", now, now, null, null, null, 0, null, 0);
            status = status with
            {
                State = "running",
                StartedUtc = now,
                UpdatedUtc = now,
                FailureCode = null,
                ProcessId = Environment.ProcessId
            };
            WriteStatus();
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void RecordAttempt(CollectionAttemptOutcome outcome, int consecutiveDeferredAttempts)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (outcome is not (CollectionAttemptOutcome.Published or CollectionAttemptOutcome.Deferred) ||
            consecutiveDeferredAttempts < 0 ||
            outcome == CollectionAttemptOutcome.Published && consecutiveDeferredAttempts != 0 ||
            outcome == CollectionAttemptOutcome.Deferred && consecutiveDeferredAttempts < 1)
            throw new InvalidOperationException("Worker runtime attempt status is inconsistent.");
        var now = DateTimeOffset.UtcNow;
        status = status with
        {
            State = "running",
            UpdatedUtc = now,
            LastAttemptUtc = now,
            LastPublicationUtc = outcome == CollectionAttemptOutcome.Published ? now : status.LastPublicationUtc,
            LastOutcome = outcome == CollectionAttemptOutcome.Published ? "published" : "deferred",
            ConsecutiveDeferredAttempts = consecutiveDeferredAttempts,
            FailureCode = null
        };
        WriteStatus();
    }

    public void Stop(string failureCode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrWhiteSpace(failureCode) || failureCode.Length > 80 ||
            failureCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new InvalidOperationException("Worker runtime failure code is invalid.");
        status = status with { State = "stopped", UpdatedUtc = DateTimeOffset.UtcNow, FailureCode = failureCode };
        WriteStatus();
    }

    private WorkerRuntimeStatus ReadStatus()
    {
        var info = new FileInfo(statusPath);
        if (info.Length > MaxStatusBytes)
            throw new InvalidOperationException("Worker runtime status exceeds its byte budget.");
        using var stream = info.OpenRead();
        using var document = JsonDocument.Parse(stream);
        SnapshotReader.RejectDuplicateProperties(document.RootElement);
        var prior = document.RootElement.Deserialize<WorkerRuntimeStatus>(Wire.Json) ??
            throw new InvalidOperationException("Worker runtime status is empty.");
        if (prior.State is not ("running" or "stopped") || prior.StartedUtc == default ||
            prior.UpdatedUtc == default || prior.ConsecutiveDeferredAttempts < 0 ||
            prior.ProcessId < 0 || prior.LastOutcome is not (null or "published" or "deferred") ||
            prior.FailureCode is not null &&
                (prior.FailureCode.Length > 80 || prior.FailureCode.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '_')))
            throw new InvalidOperationException("Worker runtime status is invalid.");
        return prior;
    }

    private void WriteStatus()
    {
        RejectReparsePoint(statusPath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(status, Wire.Json);
        if (bytes.Length > MaxStatusBytes)
            throw new InvalidOperationException("Worker runtime status exceeds its byte budget.");
        var temporary = statusPath + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, statusPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Worker runtime state files must not be reparse points.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lease.Dispose();
    }
}

public sealed class BoundedWorkerFileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int MaxGenerations = 3;
    private const int MaxMessageCharacters = 4096;
    private readonly object gate = new();
    private readonly string path;
    private bool disposed;

    public BoundedWorkerFileLoggerProvider(string directory)
    {
        path = Path.Combine(directory, "worker.log");
    }

    public ILogger CreateLogger(string categoryName) => new WorkerFileLogger(this, categoryName);

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        var safeMessage = message.Length > MaxMessageCharacters
            ? message[..MaxMessageCharacters] + " [truncated]"
            : message;
        var line = string.Concat(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), " ",
            level, " ", category, " EventId=", eventId.Id.ToString(CultureInfo.InvariantCulture), " ", safeMessage,
            exception is null ? "" : " ErrorType=" + exception.GetType().Name);
        var bytes = Encoding.UTF8.GetByteCount(line) + 1L;
        if (bytes > MaxFileBytes)
            throw new InvalidOperationException("Worker log entry exceeds its byte budget.");

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var generation in Enumerable.Range(0, MaxGenerations + 1))
                RejectReparsePoint(generation == 0 ? path : path + "." + generation);
            if (File.Exists(path) && new FileInfo(path).Length + bytes > MaxFileBytes)
                Rotate();
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(line);
            writer.Flush();
            stream.Flush(true);
        }
    }

    private void Rotate()
    {
        for (var generation = MaxGenerations - 1; generation >= 1; generation--)
        {
            var source = path + "." + generation;
            var destination = path + "." + (generation + 1);
            if (File.Exists(destination)) File.Delete(destination);
            if (File.Exists(source)) File.Move(source, destination);
        }
        var firstGeneration = path + ".1";
        if (File.Exists(firstGeneration)) File.Delete(firstGeneration);
        if (File.Exists(path)) File.Move(path, firstGeneration);
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Worker log files must not be reparse points.");
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
    }

    private sealed class WorkerFileLogger(BoundedWorkerFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information &&
            (category is "ScomCollectionWorker" || category.EndsWith(".CollectionScheduler", StringComparison.Ordinal) ||
                category.EndsWith(".TargetedLiveWorker", StringComparison.Ordinal));
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Write(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}
