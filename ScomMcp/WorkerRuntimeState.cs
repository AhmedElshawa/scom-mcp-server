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
    DateTimeOffset? RearmedUtc,
    DateTimeOffset? ProcessStartTimeUtc = null,
    string? BootId = null,
    string? HostName = null,
    DateTimeOffset? HeartbeatUtc = null);

// Terminal failure recorded earlier; automatic recovery is deliberately refused.
public sealed class WorkerRearmRequiredException() :
    InvalidOperationException("Worker runtime state requires explicit operator rearm before collection can resume.");

// Another live worker verifiably owns the lock. Not an error: the caller exits 0.
public sealed class WorkerAlreadyRunningException() :
    InvalidOperationException("Worker is already running.");

// The lock is held but the owner cannot be confirmed healthy. Never kills the owner; restartable.
public sealed class WorkerLockHeldException(string code) :
    InvalidOperationException("Worker lock is held by a process that could not be verified.")
{
    public string Code { get; } = code;
}

// Why an unclean-exit marker was recovered automatically.
public sealed record WorkerRecovery(string Reason);

public sealed class WorkerRuntimeState : IDisposable
{
    private const int MaxStatusBytes = 8192;
    private const string StatusFileName = "worker.status.json";
    private const string LockFileName = "worker.lock";
    private const string LegacyLeaseFileName = "worker.runtime.lock";
    private const string StopRequestFileName = "worker.stop.request";
    private const string StopRequestContent = "SCOM_WORKER_STOP_REQUEST";
    private const int HeartbeatMissFactor = 3;
    public static readonly TimeSpan DefaultScheduleInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BootTimeTolerance = TimeSpan.FromSeconds(60);
    private readonly object gate = new();
    private readonly string statusPath;
    private readonly string stopRequestPath;
    private readonly FileStream lease;
    private readonly IWorkerProcessProbe probe;
    private readonly TimeProvider clock;
    private readonly TimeSpan heartbeatInterval;
    private TimeSpan scheduleInterval;
    private WorkerRuntimeStatus status;
    private bool disposed;
    private int stopRequested;
    private int stopRequestInvalid;
    private string? stopRequestErrorType;

    public WorkerRuntimeState(string directory, TimeSpan? scheduleInterval = null,
        IWorkerProcessProbe? probe = null, TimeProvider? clock = null, TimeSpan? heartbeatInterval = null)
    {
        directory = ValidateDirectory(directory);
        this.probe = probe ?? WorkerProcessProbe.Default;
        this.clock = clock ?? TimeProvider.System;
        this.scheduleInterval = scheduleInterval ?? DefaultScheduleInterval;
        this.heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        if (this.scheduleInterval <= TimeSpan.Zero || this.heartbeatInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Worker runtime intervals must be positive.");
        statusPath = Path.Combine(directory, StatusFileName);
        var lockPath = Path.Combine(directory, LockFileName);
        var legacyLeasePath = Path.Combine(directory, LegacyLeaseFileName);
        stopRequestPath = Path.Combine(directory, StopRequestFileName);
        RejectReparsePoint(statusPath);
        RejectReparsePoint(lockPath);
        RejectReparsePoint(legacyLeasePath);
        RejectReparsePoint(stopRequestPath);
        // Lock acquisition is the authoritative "am I alone" check; the marker only explains it.
        lease = TryAcquire(lockPath) ?? throw DescribeHeldLock();
        try
        {
            // A worker from before the lock was renamed would not hold worker.lock.
            RefuseLiveLegacyWorker(legacyLeasePath);
            var now = this.clock.GetUtcNow();
            var prior = File.Exists(statusPath)
                ? ReadStatus(statusPath)
                : new WorkerRuntimeStatus("stopped", now, now, null, null, null, 0, null, 0,
                    false, null, null);
            if (prior.RearmRequired)
                throw new WorkerRearmRequiredException();
            if (prior.State == "running")
            {
                // We own the lock, so the recorded owner is gone by definition.
                Recovery = new WorkerRecovery(ClassifyStaleMarker(prior, now));
                prior = prior with
                {
                    LastFailureCode = "UNEXPECTED_PRIOR_RUNNING_STATE",
                    RearmedUtc = now
                };
            }
            if (File.Exists(stopRequestPath))
                File.Delete(stopRequestPath);
            status = prior with
            {
                State = "running",
                StartedUtc = now,
                UpdatedUtc = now,
                FailureCode = null,
                RearmRequired = false,
                ProcessId = Environment.ProcessId,
                ProcessStartTimeUtc = this.probe.OwnStartTimeUtc,
                BootId = this.probe.BootId,
                HostName = this.probe.HostName,
                HeartbeatUtc = now
            };
            WriteStatus();
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public WorkerRecovery? Recovery { get; }

    // Set once the schedule is known; the heartbeat-staleness threshold is a multiple of it.
    public TimeSpan ScheduleInterval
    {
        get { lock (gate) return scheduleInterval; }
        set
        {
            if (value <= TimeSpan.Zero) throw new InvalidOperationException("Worker schedule interval must be positive.");
            lock (gate) scheduleInterval = value;
        }
    }

    public void RecordAttempt(CollectionAttemptOutcome outcome, int consecutiveDeferredAttempts)
    {
        if (outcome is not (CollectionAttemptOutcome.Published or CollectionAttemptOutcome.Deferred) ||
            consecutiveDeferredAttempts < 0 ||
            outcome == CollectionAttemptOutcome.Published && consecutiveDeferredAttempts != 0 ||
            outcome == CollectionAttemptOutcome.Deferred && consecutiveDeferredAttempts < 1)
            throw new InvalidOperationException("Worker runtime attempt status is inconsistent.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var now = clock.GetUtcNow();
            status = status with
            {
                State = "running",
                UpdatedUtc = now,
                HeartbeatUtc = now,
                LastAttemptUtc = now,
                LastPublicationUtc = outcome == CollectionAttemptOutcome.Published ? now : status.LastPublicationUtc,
                LastOutcome = outcome == CollectionAttemptOutcome.Published ? "published" : "deferred",
                ConsecutiveDeferredAttempts = consecutiveDeferredAttempts,
                FailureCode = null
            };
            WriteStatus();
        }
    }

    // Terminal stop: any code other than WORKER_CANCELLED gates the next start behind an operator rearm.
    public void Stop(string failureCode) => Stop(failureCode, terminal: true);

    // Restartable stop: records the cause as evidence but does not gate the next start.
    public void StopTransient(string failureCode) => Stop(failureCode, terminal: false);

    private void Stop(string failureCode, bool terminal)
    {
        if (!ValidCode(failureCode) || failureCode is null)
            throw new InvalidOperationException("Worker runtime failure code is invalid.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var cancelled = failureCode == "WORKER_CANCELLED";
            var gated = terminal && !cancelled;
            status = status with
            {
                State = "stopped",
                UpdatedUtc = clock.GetUtcNow(),
                FailureCode = gated ? failureCode : null,
                RearmRequired = gated,
                LastFailureCode = cancelled ? status.LastFailureCode : failureCode,
                ProcessId = 0,
                ProcessStartTimeUtc = null
            };
            WriteStatus();
        }
    }

    // Refreshes heartbeatUtc. A stopped or disposed state never writes a running heartbeat.
    public void Heartbeat()
    {
        lock (gate)
        {
            if (disposed || status.State != "running") return;
            status = status with { HeartbeatUtc = clock.GetUtcNow() };
            WriteStatus();
        }
    }

    public async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(heartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try { Heartbeat(); }
                // A missed heartbeat is retried on the next tick; only a sustained gap marks the worker stale.
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static FileStream? TryAcquire(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when (IsSharingViolation(error))
        {
            return null;
        }
    }

    private static bool IsSharingViolation(IOException error) =>
        (error.HResult & 0xFFFF) is 32 or 33;

    private static void RefuseLiveLegacyWorker(string legacyLeasePath)
    {
        if (!File.Exists(legacyLeasePath)) return;
        try
        {
            using var legacy = new FileStream(legacyLeasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when (IsSharingViolation(error))
        {
            throw new WorkerLockHeldException("WORKER_LOCK_HELD_UNVERIFIED");
        }
    }

    // The lock is held by someone else. Decide whether that is a healthy worker already running.
    private Exception DescribeHeldLock()
    {
        WorkerRuntimeStatus? marker = null;
        try
        {
            if (File.Exists(statusPath)) marker = ReadStatus(statusPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or JsonException)
        {
        }
        var now = clock.GetUtcNow();
        if (marker is { State: "running" } && MarkerIdentifiesLiveProcess(marker))
            return HeartbeatStale(marker, now)
                ? new WorkerLockHeldException("WORKER_HEARTBEAT_STALE_LOCK_HELD")
                : new WorkerAlreadyRunningException();
        return new WorkerLockHeldException("WORKER_LOCK_HELD_UNVERIFIED");
    }

    private bool MarkerIdentifiesLiveProcess(WorkerRuntimeStatus marker) =>
        marker.ProcessStartTimeUtc is { } recordedStart &&
        string.Equals(marker.HostName, probe.HostName, StringComparison.OrdinalIgnoreCase) &&
        SameBoot(marker.BootId) &&
        probe.ProcessStartTimeUtc(marker.ProcessId) is { } actualStart &&
        (actualStart - recordedStart).Duration() <= StartTimeTolerance;

    private bool SameBoot(string? recordedBootId)
    {
        var current = probe.BootId;
        if (string.Equals(recordedBootId, current, StringComparison.Ordinal)) return true;
        return WorkerProcessProbe.TryParseBootId(recordedBootId, out var recorded) &&
            WorkerProcessProbe.TryParseBootId(current, out var actual) &&
            (recorded - actual).Duration() <= BootTimeTolerance;
    }

    private bool HeartbeatStale(WorkerRuntimeStatus marker, DateTimeOffset now) =>
        marker.HeartbeatUtc is not { } heartbeat ||
        now - heartbeat > HeartbeatMissFactor * ScheduleInterval;

    private string ClassifyStaleMarker(WorkerRuntimeStatus marker, DateTimeOffset now)
    {
        if (marker.HostName is not null &&
            !string.Equals(marker.HostName, probe.HostName, StringComparison.OrdinalIgnoreCase))
            return "HOST_CHANGED";
        if (marker.BootId is not null && !SameBoot(marker.BootId))
            return "BOOT_ID_CHANGED";
        if (probe.ProcessStartTimeUtc(marker.ProcessId) is not { } actualStart)
            return "PROCESS_NOT_RUNNING";
        if (marker.ProcessStartTimeUtc is { } recordedStart &&
            (actualStart - recordedStart).Duration() > StartTimeTolerance)
            return "PID_REUSED";
        if (marker.ProcessStartTimeUtc is null)
            return "IDENTITY_UNAVAILABLE";
        return HeartbeatStale(marker, now) ? "HEARTBEAT_STALE" : "LOCK_FREE_NO_OWNER";
    }

    internal static WorkerRuntimeStatus ReadStatusFile(string directory) =>
        ReadStatus(Path.Combine(ValidateDirectory(directory), StatusFileName));

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

    public static void Rearm(string directory)
    {
        directory = ValidateDirectory(directory);
        var statusPath = Path.Combine(directory, StatusFileName);
        var leasePath = Path.Combine(directory, LockFileName);
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
            ProcessId = 0,
            ProcessStartTimeUtc = null
        };
        WriteStatus(statusPath, rearmed);
    }

    private static WorkerRuntimeStatus ReadStatus(string path)
    {
        // Readers share the file so a heartbeat replace never fails a concurrent status read, and a
        // read never blocks the worker's atomic replace.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is 0 or > MaxStatusBytes)
            throw new InvalidOperationException("Worker runtime status is empty or exceeds its byte budget.");
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
        // Written by workers from this release on; absent in status files from earlier releases.
        var optionalProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "processStartTimeUtc", "bootId", "hostName", "heartbeatUtc"
        };
        foreach (var property in document.RootElement.EnumerateObject())
            if (!expectedProperties.Remove(property.Name) && !optionalProperties.Contains(property.Name))
                throw new InvalidOperationException("Worker runtime status contains an unknown property.");
        if (expectedProperties.Count != 0)
            throw new InvalidOperationException("Worker runtime status is missing required properties.");
        var status = document.RootElement.Deserialize<WorkerRuntimeStatus>(Wire.Json) ??
            throw new InvalidOperationException("Worker runtime status is empty.");
        if (status.State is not ("running" or "stopped") || status.StartedUtc == default ||
            status.UpdatedUtc == default || status.ConsecutiveDeferredAttempts < 0 ||
            status.ProcessId < 0 || status.LastOutcome is not (null or "published" or "deferred") ||
            !ValidCode(status.FailureCode) || !ValidCode(status.LastFailureCode) ||
            !ValidIdentityText(status.BootId, 64) || !ValidIdentityText(status.HostName, 255) ||
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

    private static bool ValidIdentityText(string? value, int maxLength) => value is null ||
        value.Length is > 0 && value.Length <= maxLength && value.All(character => !char.IsControl(character));

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
            // An operator tool or scanner can briefly hold the status file open without delete sharing.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, statusPath, true);
                    break;
                }
                catch (Exception error) when (attempt < 5 &&
                    error is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(50 * attempt);
                }
            }
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
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lease.Dispose();
        }
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
