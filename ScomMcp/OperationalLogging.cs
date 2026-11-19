using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ScomMcp;

// Setup owns directory creation and ACLs. Runtime never creates or repairs them.
public sealed class OperationalLoggingSettings
{
    public string Directory { get; set; } = "";
    public int MaxFileBytes { get; set; } = 4 * 1024 * 1024;
    public int RetainedFiles { get; set; } = 3;
    public const int MinimumFileBytes = 64 * 1024;
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    public const int MaximumRetainedFiles = 16;

    public void Validate()
    {
        if (MaxFileBytes is < MinimumFileBytes or > MaximumFileBytes ||
            RetainedFiles is < 1 or > MaximumRetainedFiles)
            throw new InvalidOperationException("Operational logging bounds are invalid.");
        ValidatePath(Directory);
    }

    private static void ValidatePath(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) ||
                !Path.IsPathFullyQualified(directory) || directory.Length < 3 ||
                !char.IsAsciiLetter(directory[0]) || directory[1] != ':' ||
                directory[2] != '\\' || directory.Contains('/') ||
                directory[2..].Contains(':') ||
                directory.Split('\\').Any(part => part is "." or ".." ||
                    part.EndsWith(' ') || part.EndsWith('.')))
                throw new InvalidOperationException();
            if (Path.GetFullPath(directory).Length > 240 ||
                directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                directory[3..].IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
                throw new InvalidOperationException();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidOperationException("Operational logging directory must be a bounded absolute local drive path.");
        }
    }

    internal static string ValidateDirectory(string directory)
    {
        try
        {
            ValidatePath(directory);
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException();
            var full = Path.GetFullPath(directory);
            if (!System.IO.Directory.Exists(full)) throw new InvalidOperationException();
            for (var parent = new DirectoryInfo(full); parent is not null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException();
            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            if (drive.DriveType != DriveType.Fixed ||
                !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException();
            return full;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or InvalidOperationException or System.Security.SecurityException)
        {
            throw new InvalidOperationException("Operational logging requires an existing absolute local fixed NTFS directory without reparse points.");
        }
    }
}

public enum OperationalComponent { Server, Worker }
public enum OperationalEvent { Started, Stopped, Failed, RequestCompleted, CollectionCompleted, Diagnostic }
public enum OperationalOutcome { Succeeded, Deferred, Cancelled, Failed }

internal sealed record OperationalState(OperationalEvent Event, OperationalOutcome Outcome, long? DurationMs);

public static class OperationalLog
{
    public static void Write(ILogger logger, OperationalEvent operation, OperationalOutcome outcome,
        long? durationMs = null)
    {
        if (!Enum.IsDefined(operation) || !Enum.IsDefined(outcome) ||
            durationMs is < 0 or > 604800000)
            throw new InvalidOperationException("Operational event is invalid.");
        logger.Log(outcome == OperationalOutcome.Failed ? LogLevel.Error : LogLevel.Information,
            default, new OperationalState(operation, outcome, durationMs), null, static (_, _) => "");
    }

    public static long ElapsedMilliseconds(long start) =>
        Math.Clamp((long)Stopwatch.GetElapsedTime(start).TotalMilliseconds, 0, 604800000);
}

// This is an allowlist projection, not a text logger. Formatter, exception,
// scopes, category, event ID and all unrecognized state fields are never rendered.
internal sealed record SafeOperationalEntry(
    DateTimeOffset Utc, string Component, string Level, string Event,
    string? Outcome = null, long? DurationMs = null, string? Code = null,
    int? Attempt = null, int? MaxAttempts = null, double? DelaySeconds = null,
    long? LineNumber = null, long? BytePositionInLine = null)
{
    public static SafeOperationalEntry? Project<TState>(OperationalComponent component, LogLevel level, TState state)
    {
        if (level is < LogLevel.Information or > LogLevel.Critical) return null;
        var source = component == OperationalComponent.Server ? "server" : "worker";
        if (state is OperationalState operation)
            return new(DateTimeOffset.UtcNow, source, level.ToString(),
                operation.Event.ToString(), operation.Outcome.ToString(), operation.DurationMs);
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> fields || fields.Count > 24)
            return null;
        object? Field(string key)
        {
            foreach (var pair in fields)
                if (pair.Key == key) return pair.Value;
            return null;
        }
        var name = (Field("{OriginalFormat}") as string) switch
        {
            "Worker runtime logging initialized. Event={Event}" => "WorkerStarted",
            "Worker attempt published. Event={Event}" => "WorkerPublished",
            "Worker stopped. Event={Event}" => "WorkerCancelled",
            "Worker stopped. Event={Event} ErrorType={ErrorType} Code={Code}" => "WorkerFailed",
            "Worker stopped. Event={Event} Code={Code} LineNumber={LineNumber} BytePositionInLine={BytePositionInLine}" => "WorkerFailed",
            "Collection loop stopped. Event={Event} ErrorType={ErrorType}" => "CollectionStopped",
            "Collection published. Event={Event}" => "CollectionPublished",
            "Collection deferred. Event={Event} Attempt={Attempt} MaxAttempts={MaxAttempts} DelaySeconds={DelaySeconds}" => "CollectionDeferred",
            "Live verification deferred. Event={Event} ErrorType={ErrorType}" => "LiveDeferred",
            "Targeted source unavailable. RequestId={RequestId} ErrorType={ErrorType}" => "LiveSourceUnavailable",
            "Targeted result delivery ambiguous; retrying same result. RequestId={RequestId}" => "LiveDeliveryDeferred",
            "Targeted check admitted. Event={Event} Utc={Utc} RequestId={RequestId} TraceId={TraceId}" => "LiveAdmitted",
            "Targeted check received. Event={Event} Utc={Utc} RequestId={RequestId} Outcome={Outcome}" => "LiveReceived",
            "Ingestion accepted. Event={Event} Utc={Utc} TraceId={TraceId} Bytes={Bytes}" => "IngestionAccepted",
            "Ingestion rejected. Code={Code} TraceId={TraceId}" => "IngestionRejected",
            "Ingestion body deadline exceeded. TraceId={TraceId}" => "IngestionDeadline",
            "Targeted check rejected. Code={Code} TraceId={TraceId}" => "LiveRejected",
            "Targeted transport rejected. Code={Code} TraceId={TraceId}" => "LiveTransportRejected",
            "Targeted check failed. ErrorType={ErrorType} TraceId={TraceId}" => "LiveFailed",
            "Read model request failed. ErrorType={ErrorType} TraceId={TraceId}" => "ReadModelFailed",
            "SCOM tool request failed. ErrorType={ErrorType} TraceId={TraceId}" => "ToolFailed",
            "Request failed. TraceId={TraceId} ErrorType={ErrorType} SqliteErrorCode={SqliteErrorCode} SqliteExtendedErrorCode={SqliteExtendedErrorCode}" => "RequestFailed",
            _ => null
        };
        if (name is null) return null;
        var code = Field("Code") is string candidate && SafeCode(candidate) ? candidate : null;
        int? Number(string key) => Field(key) is int value && value is >= 0 and <= 100 ? value : null;
        var delay = Field("DelaySeconds") is double seconds && double.IsFinite(seconds) &&
            seconds is >= 0 and <= 86400 ? (double?)seconds : null;
        long? Position(string key) => Field(key) is long value && value is >= 0 and <= 1048576 ? value : null;
        var outcome = name switch
        {
            "WorkerCancelled" => "Cancelled",
            "WorkerStarted" or "WorkerPublished" or "CollectionPublished" or "IngestionAccepted" => "Succeeded",
            "CollectionDeferred" or "LiveDeferred" or "LiveDeliveryDeferred" => "Deferred",
            _ when level >= LogLevel.Warning => "Failed",
            _ => null
        };
        return new(DateTimeOffset.UtcNow, source, level.ToString(), name, Outcome: outcome, Code: code,
            Attempt: Number("Attempt"), MaxAttempts: Number("MaxAttempts"), DelaySeconds: delay,
            LineNumber: Position("LineNumber"), BytePositionInLine: Position("BytePositionInLine"));
    }

    private static bool SafeCode(string code) => code is
        "WORKER_STOP_REQUEST_INVALID" or "WORKER_CONFIGURATION_JSON_INVALID" or
        "WORKER_CONFIGURATION_OR_EXECUTION_FAILED" or "COLLECTION_ATTEMPTS_EXHAUSTED" or
        "SOURCE_UNAVAILABLE" or "LIVE_NOT_ENABLED" or "LIVE_PROVIDER_UNAVAILABLE" or "LIVE_RATE_LIMITED" or
        "LIVE_QUEUE_FULL" or "LIVE_TIMEOUT" or "LIVE_PROVIDER_UNVERIFIED" or "LIVE_RESULT_INVALID" or
        "LIVE_REQUEST_EXPIRED_OR_UNKNOWN" or "LIVE_RESULT_BUDGET" or "LIVE_REPLAY_CONFLICT" or
        "LIVE_PUBLISHER_FORBIDDEN" or "LIVE_DATASET_CHANGED" or "LIVE_SOURCE_UNAVAILABLE" or
        "LIVE_PROTOCOL_INVALID" or "LIVE_WORKER_ALREADY_RUNNING" or "LIVE_TRANSPORT_PROTOCOL" or
        "LIVE_TRANSPORT_BUDGET" or "LIVE_TRANSPORT_REJECTED" or "LIVE_PROFILE_BUDGET_INVALID" or
        "LIVE_SOURCE_BUSY" or "LIVE_INPUT_BUDGET" or "INVALID_ARGUMENT" or "NOT_FOUND" or
        "PUBLISHER_SESSION_INVALID" or "PUBLISHER_SCOPE_UNVERIFIED" or
        "PUBLISHER_ALREADY_RUNNING" or "PUBLISHER_MEMBERSHIP_BUDGET" or "PUBLISHER_MEMBERSHIP_INVALID" or
        "PUBLISHER_BATCH_INVALID" or "PUBLISHER_RECORD_BUDGET" or "PUBLISHER_DATASET_BUDGET" or
        "PUBLISHER_DEADLINE" or "PUBLISHER_TOKEN_INVALID" or "PUBLISHER_RETRY_DEFERRED" or
        "PUBLISHER_RETRY_EXHAUSTED" or "PUBLISHER_RESPONSE_INVALID" or "PUBLISHER_PAYLOAD_BUDGET" ||
        code.Length == 18 && code.StartsWith("PUBLISHER_HTTP_", StringComparison.Ordinal) &&
        int.TryParse(code.AsSpan(15), out var status) && status is >= 100 and <= 599;
}

public sealed class OperationalFileLoggerProvider : ILoggerProvider
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly string path;
    private readonly string leasePath;
    private readonly FileStream lease;
    private readonly OperationalComponent component;
    private readonly int maxFileBytes;
    private readonly int retainedFiles;
    private readonly bool console;
    private bool disposed;
    private bool failed;
    public bool HasFailed => Volatile.Read(ref failed);
    public Action? OnFailure { get; set; }

    public OperationalFileLoggerProvider(OperationalLoggingSettings settings, OperationalComponent component,
        bool writeSafeConsole = false)
    {
        settings.Validate();
        if (!Enum.IsDefined(component)) throw new InvalidOperationException("Operational logging component is invalid.");
        directory = OperationalLoggingSettings.ValidateDirectory(settings.Directory);
        this.component = component;
        maxFileBytes = settings.MaxFileBytes;
        retainedFiles = settings.RetainedFiles;
        console = writeSafeConsole;
        path = Path.Combine(directory, component == OperationalComponent.Server ? "server.log" : "worker.log");
        leasePath = Path.Combine(directory, "operational.lock");
        try
        {
            CheckFile(leasePath);
            lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var role = component == OperationalComponent.Server ? (byte)1 : (byte)2;
                if (lease.Length == 0) { lease.WriteByte(role); lease.Flush(true); }
                else if (lease.Length != 1 || lease.ReadByte() != role)
                    throw new InvalidOperationException("Operational log directories must be separate for server and worker.");
                ValidateFiles();
                // Open now so startup refuses a read-only or inaccessible sink.
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                stream.Flush(true);
            }
            catch { lease.Dispose(); throw; }
        }
        catch (Exception)
        {
            throw new InvalidOperationException("OPERATIONAL_LOG_IO_FAILED");
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this);

    private void Write(SafeOperationalEntry entry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, Wire.Json);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                if (failed || bytes.Length + 1 > maxFileBytes) throw new IOException();
                OperationalLoggingSettings.ValidateDirectory(directory);
                ValidateFiles();
                if (new FileInfo(path) is { Exists: true } file && file.Length + bytes.Length + 1 > maxFileBytes)
                    Rotate();
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                stream.WriteByte((byte)'\n');
                stream.Flush(true);
                if (console) Console.Error.WriteLine(Encoding.UTF8.GetString(bytes));
            }
            catch (Exception)
            {
                failed = true;
                OnFailure?.Invoke();
                throw new InvalidOperationException("OPERATIONAL_LOG_IO_FAILED");
            }
        }
    }

    private void ValidateFiles()
    {
        CheckFile(leasePath);
        // Check every supported generation, including leftovers from larger settings.
        for (var index = 0; index <= OperationalLoggingSettings.MaximumRetainedFiles; index++)
        {
            var file = index == 0 ? path : path + "." + index;
            CheckFile(file);
            if (!File.Exists(file)) continue;
            if (index >= retainedFiles) File.Delete(file);
            else if (new FileInfo(file).Length > maxFileBytes)
                throw new InvalidOperationException("Operational log exceeds its configured byte budget.");
        }
    }

    private static void CheckFile(string file)
    {
        try
        {
            if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidOperationException();
        }
        catch (FileNotFoundException) { }
    }

    private void Rotate()
    {
        if (retainedFiles == 1) { File.Delete(path); return; }
        var last = path + "." + (retainedFiles - 1);
        if (File.Exists(last)) File.Delete(last);
        for (var index = retainedFiles - 2; index >= 1; index--)
            if (File.Exists(path + "." + index)) File.Move(path + "." + index, path + "." + (index + 1));
        if (File.Exists(path)) File.Move(path, path + ".1");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lease.Dispose();
        }
    }

    private sealed class FileLogger(OperationalFileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and <= LogLevel.Critical;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = SafeOperationalEntry.Project(owner.component, logLevel, state);
            if (entry is not null) owner.Write(entry);
        }
    }
}

internal sealed class SafeWorkerConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger();
    public void Dispose() { }
    private sealed class ConsoleLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and <= LogLevel.Critical;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = SafeOperationalEntry.Project(OperationalComponent.Worker, logLevel, state);
            if (entry is null) return;
            Console.Error.WriteLine(JsonSerializer.Serialize(entry, Wire.Json) +
                (entry.Event == "WorkerPublished" ? " Event=SCOM_WORKER_PUBLISHED" : "") +
                (entry.Code is null ? "" : $" Code={entry.Code}") +
                (entry.LineNumber is null ? "" : $" LineNumber={entry.LineNumber} BytePositionInLine={entry.BytePositionInLine}"));
        }
    }
}
