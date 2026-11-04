using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    int ProcessId,
    bool RearmRequired,
    string? LastFailureCode,
    DateTimeOffset? RearmedUtc);

public sealed class WorkerRearmRequiredException() :
    InvalidOperationException("Worker runtime state requires explicit operator rearm before collection can resume.");

public sealed class WorkerRuntimeState : IDisposable
{
    private const int MaxStatusBytes = 8192;
    private const string StatusFileName = "worker.status.json";
    private const string LeaseFileName = "worker.runtime.lock";
    private const string StopRequestFileName = "worker.stop.request";
    private const string StopRequestContent = "SCOM_WORKER_STOP_REQUEST";
    private readonly string statusPath;
    private readonly string stopRequestPath;
    private readonly FileStream lease;
    private WorkerRuntimeStatus status;
    private bool disposed;
    private int stopRequested;
    private int stopRequestInvalid;
    private string? stopRequestErrorType;

    public WorkerRuntimeState(string directory)
    {
        directory = ValidateDirectory(directory);
        statusPath = Path.Combine(directory, StatusFileName);
        var leasePath = Path.Combine(directory, LeaseFileName);
        stopRequestPath = Path.Combine(directory, StopRequestFileName);
        RejectReparsePoint(statusPath);
        RejectReparsePoint(leasePath);
        RejectReparsePoint(stopRequestPath);
        lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var now = DateTimeOffset.UtcNow;
            status = File.Exists(statusPath)
                ? ReadStatus()
                : new WorkerRuntimeStatus("stopped", now, now, null, null, null, 0, null, 0,
                    false, null, null);
            if (status.RearmRequired || status.State == "running")
                throw new WorkerRearmRequiredException();
            if (File.Exists(stopRequestPath))
                File.Delete(stopRequestPath);
            status = status with
            {
                State = "running",
                StartedUtc = now,
                UpdatedUtc = now,
                FailureCode = null,
                RearmRequired = false,
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
        var cancelled = failureCode == "WORKER_CANCELLED";
        status = status with
        {
            State = "stopped",
            UpdatedUtc = DateTimeOffset.UtcNow,
            FailureCode = cancelled ? null : failureCode,
            RearmRequired = !cancelled,
            LastFailureCode = cancelled ? status.LastFailureCode : failureCode,
            ProcessId = 0
        };
        WriteStatus();
    }

    public bool StopRequested => Volatile.Read(ref stopRequested) != 0;
    public bool StopRequestInvalid => Volatile.Read(ref stopRequestInvalid) != 0;
    public string StopRequestErrorType => Volatile.Read(ref stopRequestErrorType) ?? "InvalidDataException";

    public async Task WatchStopRequestsAsync(CancellationTokenSource shutdown, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                RejectReparsePoint(stopRequestPath);
                if (!File.Exists(stopRequestPath)) continue;
                using (var stream = new FileStream(stopRequestPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (stream.Length is 0 or > 128)
                    {
                        Interlocked.Exchange(ref stopRequestInvalid, 1);
                        shutdown.Cancel();
                        return;
                    }
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 128, leaveOpen: true);
                    if (!string.Equals(await reader.ReadToEndAsync(cancellationToken), StopRequestContent,
                        StringComparison.Ordinal))
                    {
                        Interlocked.Exchange(ref stopRequestInvalid, 1);
                        shutdown.Cancel();
                        return;
                    }
                }
                File.Delete(stopRequestPath);
                Interlocked.Exchange(ref stopRequested, 1);
                shutdown.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.Security.SecurityException)
        {
            stopRequestErrorType = error.GetType().Name;
            Interlocked.Exchange(ref stopRequestInvalid, 1);
            shutdown.Cancel();
        }
    }

    public static void RequestStop(string directory)
    {
        directory = ValidateDirectory(directory);
        var stopRequestPath = Path.Combine(directory, StopRequestFileName);
        RejectReparsePoint(stopRequestPath);
        using var stream = new FileStream(stopRequestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = Encoding.UTF8.GetBytes(StopRequestContent);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private WorkerRuntimeStatus ReadStatus()
    {
        var prior = ReadStatus(statusPath);
        if (prior.State is not ("running" or "stopped") || prior.StartedUtc == default ||
            prior.UpdatedUtc == default || prior.ConsecutiveDeferredAttempts < 0 ||
            prior.ProcessId < 0 || prior.LastOutcome is not (null or "published" or "deferred") ||
            !ValidCode(prior.FailureCode) || !ValidCode(prior.LastFailureCode) ||
            prior.RearmRequired && prior.State != "stopped" ||
            prior.State == "running" && (prior.ProcessId == 0 || prior.FailureCode is not null) ||
            prior.State == "stopped" && (prior.ProcessId != 0 ||
                prior.RearmRequired != (prior.FailureCode is not null) ||
                prior.RearmRequired && prior.LastFailureCode != prior.FailureCode))
            throw new InvalidOperationException("Worker runtime status is invalid.");
        var rearmRequired = prior.RearmRequired || prior.State == "running" ||
            prior.FailureCode is not null && prior.FailureCode != "WORKER_CANCELLED";
        return prior with
        {
            RearmRequired = rearmRequired,
            LastFailureCode = prior.LastFailureCode ?? (rearmRequired ? prior.FailureCode ??
                "UNEXPECTED_PRIOR_RUNNING_STATE" : null)
        };
    }

    public static void Rearm(string directory)
    {
        directory = ValidateDirectory(directory);
        var statusPath = Path.Combine(directory, StatusFileName);
        var leasePath = Path.Combine(directory, LeaseFileName);
        RejectReparsePoint(statusPath);
        RejectReparsePoint(leasePath);
        using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!File.Exists(statusPath))
            throw new InvalidOperationException("Worker runtime status is missing; rearm is refused.");
        var status = ReadStatus(statusPath);
        var rearmRequired = status.RearmRequired || status.State == "running" ||
            status.FailureCode is not null && status.FailureCode != "WORKER_CANCELLED";
        if (!rearmRequired)
            throw new InvalidOperationException("Worker runtime has no terminal or unknown state requiring rearm.");
        var rearmed = status with
        {
            State = "stopped",
            UpdatedUtc = DateTimeOffset.UtcNow,
            FailureCode = null,
            RearmRequired = false,
            LastFailureCode = status.State == "running" && status.FailureCode is null
                ? "UNEXPECTED_PRIOR_RUNNING_STATE"
                : status.LastFailureCode ?? status.FailureCode,
            RearmedUtc = DateTimeOffset.UtcNow,
            ProcessId = 0
        };
        WriteStatus(statusPath, rearmed);
    }

    private static WorkerRuntimeStatus ReadStatus(string path)
    {
        var info = new FileInfo(path);
        if (info.Length is 0 or > MaxStatusBytes)
            throw new InvalidOperationException("Worker runtime status is empty or exceeds its byte budget.");
        using var stream = info.OpenRead();
        using var document = JsonDocument.Parse(stream);
        SnapshotReader.RejectDuplicateProperties(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Worker runtime status is not a JSON object.");
        var expectedProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "state", "startedUtc", "updatedUtc", "lastAttemptUtc", "lastPublicationUtc",
            "lastOutcome", "consecutiveDeferredAttempts", "failureCode", "processId",
            "rearmRequired", "lastFailureCode", "rearmedUtc"
        };
        foreach (var property in document.RootElement.EnumerateObject())
            if (!expectedProperties.Remove(property.Name))
                throw new InvalidOperationException("Worker runtime status contains an unknown property.");
        if (expectedProperties.Count != 0)
            throw new InvalidOperationException("Worker runtime status is missing required properties.");
        var status = document.RootElement.Deserialize<WorkerRuntimeStatus>(Wire.Json) ??
            throw new InvalidOperationException("Worker runtime status is empty.");
        if (status.State is not ("running" or "stopped") || status.StartedUtc == default ||
            status.UpdatedUtc == default || status.ConsecutiveDeferredAttempts < 0 ||
            status.ProcessId < 0 || status.LastOutcome is not (null or "published" or "deferred") ||
            !ValidCode(status.FailureCode) || !ValidCode(status.LastFailureCode) ||
            status.RearmRequired && status.State != "stopped" ||
            status.State == "running" && (status.ProcessId == 0 || status.FailureCode is not null) ||
            status.State == "stopped" && (status.ProcessId != 0 ||
                status.RearmRequired != (status.FailureCode is not null) ||
                status.RearmRequired && status.LastFailureCode != status.FailureCode))
            throw new InvalidOperationException("Worker runtime status is invalid.");
        return status;
    }

    private static bool ValidCode(string? code) => code is null ||
        code.Length is > 0 and <= 80 && code.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string ValidateDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
            directory.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(directory))
            throw new InvalidOperationException("Worker runtime state requires an existing absolute local directory.");
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Worker runtime state directories must not traverse reparse points.");
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(directory)!).DriveType == DriveType.Network)
            throw new InvalidOperationException("Worker runtime state must not use a network drive.");
        return Path.GetFullPath(directory);
    }

    private void WriteStatus()
    {
        WriteStatus(statusPath, status);
    }

    private static void WriteStatus(string statusPath, WorkerRuntimeStatus status)
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
    // Compatibility for callers predating OperationalLogging configuration.
    // Legacy text events become content-free diagnostics, never formatted text.
    private readonly OperationalFileLoggerProvider provider;
    public BoundedWorkerFileLoggerProvider(string directory) =>
        provider = new(new OperationalLoggingSettings
        {
            Directory = directory, MaxFileBytes = OperationalLoggingSettings.MinimumFileBytes, RetainedFiles = 3
        }, OperationalComponent.Worker);
    public ILogger CreateLogger(string categoryName) => new WorkerFileLogger(provider.CreateLogger(categoryName));
    public void Dispose() => provider.Dispose();

    private sealed class WorkerFileLogger(ILogger logger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                OperationalLog.Write(logger, OperationalEvent.Diagnostic, OperationalOutcome.Succeeded);
        }
    }
}
