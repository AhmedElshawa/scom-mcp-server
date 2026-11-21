using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace ScomMcp;

public sealed class CollectionWorkerHostSettings
{
    public OperationalLoggingSettings OperationalLogging { get; set; } = new();
    public string CheckpointPath { get; set; } = "";
    public int MaxCheckpointBytes { get; set; } = 32 * 1024 * 1024;
    public PowerShellProfileSettings Profile { get; set; } = new();
    public PublisherIdentitySettings Identity { get; set; } = new();
    public IngestionPublisherSettings Publisher { get; set; } = new();
    public ReconciledSourceSettings Reconciliation { get; set; } = new();
    public CollectionScheduleSettings Schedule { get; set; } = new();
    public LiveWorkerSettings LiveChecks { get; set; } = new();
}

public static class CollectionWorkerHost
{
    // Exit codes: 0 published, cancelled or already running; 1 transient failure (restartable);
    // 2 configuration, attestation or refused-recovery failure (not restartable).
    public const int ExitTransient = 1;
    public const int ExitNotRestartable = 2;

    private static async Task<CollectionWorkerHostSettings> ReadSettingsAsync(string configurationPath,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(configurationPath))
            throw new InvalidOperationException("Worker configuration path must be absolute.");
        var info = new FileInfo(configurationPath);
        if (!info.Exists || info.Length > 1024 * 1024)
            throw new InvalidOperationException("Worker configuration is missing or exceeds its byte budget.");
        using var stream = info.OpenRead();
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (bytes.Length + read > 1024 * 1024)
                throw new InvalidOperationException("Worker configuration exceeds its byte budget.");
            bytes.Write(buffer, 0, read);
        }
        ReadOnlyMemory<byte> configurationBytes = bytes.ToArray();
        if (configurationBytes.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            configurationBytes = configurationBytes[3..];
        using var document = JsonDocument.Parse(configurationBytes);
        SnapshotReader.RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<CollectionWorkerHostSettings>(Wire.Json) ??
            throw new InvalidOperationException("Worker configuration is empty.");
    }

    private static void LogRecovery(ILogger logger, LogLevel level, string code, string? reason) =>
        logger.Log(level, "Worker recovery. Event={Event} Code={Code} Reason={Reason}",
            "SCOM_WORKER_RECOVERY", code, reason);

    // A failure is restartable unless retrying cannot succeed without operator action: configuration,
    // script-hash attestation, publisher identity or authorization.
    private static (string Code, bool Terminal) Classify(Exception error)
    {
        switch (error)
        {
            case CollectionAttemptsExhaustedException:
                return ("COLLECTION_ATTEMPTS_EXHAUSTED", false);
            case IngestionPublicationException publication:
                var terminalPublication = publication.Code.StartsWith("PUBLISHER_CERTIFICATE_", StringComparison.Ordinal) ||
                    publication.Code is "PUBLISHER_SCOPE_UNVERIFIED" or "PUBLISHER_TOKEN_INVALID" or
                        "PUBLISHER_HTTP_401" or "PUBLISHER_HTTP_403";
                return (publication.Code, terminalPublication);
            case QueryFault fault:
                return (fault.Code, fault.Code == "INVALID_ARGUMENT");
            case JsonException:
                return ("WORKER_CONFIGURATION_JSON_INVALID", true);
            case HttpRequestException or TimeoutException:
                return ("WORKER_TRANSIENT_FAILURE", false);
            case IOException when error is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException):
                return ("WORKER_TRANSIENT_FAILURE", false);
            default:
                return ("WORKER_CONFIGURATION_OR_EXECUTION_FAILED", true);
        }
    }

    public static async Task<int> RunAsync(string configurationPath, bool once, CancellationToken cancellationToken,
        string? runtimeStateDirectory = null)
    {
        using var logging = LoggerFactory.Create(builder =>
            builder.ClearProviders().AddProvider(new SafeWorkerConsoleLoggerProvider()));
        var logger = logging.CreateLogger("ScomCollectionWorker");
        WorkerRuntimeState? runtimeState = null;
        CancellationTokenSource? runtimeShutdown = null;
        Task? stopRequestWatcher = null;
        Task? heartbeat = null;
        var workerCancellationToken = cancellationToken;
        var loadingConfiguration = true;
        try
        {
            if (runtimeStateDirectory is not null)
            {
                if (once)
                    throw new InvalidOperationException("Runtime state hosting is available only in long-running worker mode.");
                runtimeState = new WorkerRuntimeState(runtimeStateDirectory);
                runtimeShutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                workerCancellationToken = runtimeShutdown.Token;
                stopRequestWatcher = runtimeState.WatchStopRequestsAsync(runtimeShutdown, workerCancellationToken);
                heartbeat = runtimeState.RunHeartbeatAsync(workerCancellationToken);
                logger.LogInformation("Worker runtime logging initialized. Event={Event}", "SCOM_WORKER_STARTED");
                WorkerEventLog.Write(WorkerEventLog.Started, "WORKER_STARTED");
                if (runtimeState.Recovery is { } recovery)
                {
                    LogRecovery(logger, LogLevel.Warning, "WORKER_STALE_MARKER_RECOVERED", recovery.Reason);
                    WorkerEventLog.Write(WorkerEventLog.Recovered,
                        "WORKER_STALE_MARKER_RECOVERED Reason=" + recovery.Reason);
                }
            }
            var settings = await ReadSettingsAsync(configurationPath, workerCancellationToken);
            loadingConfiguration = false;
            logging.AddProvider(new OperationalFileLoggerProvider(settings.OperationalLogging,
                OperationalComponent.Worker));
            OperationalLog.Write(logger, OperationalEvent.Started, OperationalOutcome.Succeeded);
            if (runtimeState?.Recovery is { } recoveredMarker)
                LogRecovery(logger, LogLevel.Warning, "WORKER_STALE_MARKER_RECOVERED", recoveredMarker.Reason);
            settings.Reconciliation.Validate();
            settings.Schedule.Validate();
            if (runtimeState is not null) runtimeState.ScheduleInterval = settings.Schedule.Interval;
            settings.Publisher.Validate();
            settings.Identity.Validate();
            settings.LiveChecks.Validate();
            var hostingLab = SetupConfigurationValidation.HostingLabPreviewEnabled(settings.Profile.Configuration);
            if (hostingLab && (!once || settings.LiveChecks.Enabled))
                throw new InvalidOperationException("Hosting lab preview requires worker-once with live checks disabled.");
            if (once && settings.LiveChecks.Enabled)
                throw new InvalidOperationException("Live checks require the explicitly enabled long-running worker, not worker-once.");
            var profile = new PowerShellNormalizationProfile(settings.Profile);
            // Endpoint/identity pin prevents resuming a pending dataset against a
            // different ingestion destination or publisher after reconfiguration.
            var policy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Wire.Encode(new
            {
                settings.Profile.ScriptSha256, settings.Profile.Configuration,
                settings.Publisher.Endpoint, settings.Identity.TenantId, settings.Identity.ClientId,
                settings.Identity.Resource
            }))));
            if (hostingLab && File.Exists(settings.CheckpointPath))
                throw new InvalidOperationException("Hosting lab preview requires a new checkpoint.");
            using var checkpoint = new SourceCheckpointStore(settings.CheckpointPath, policy, settings.MaxCheckpointBytes);
            using var identity = new CertificatePublisherToken(settings.Identity);
            using var publisher = new IngestionPublisher(settings.Publisher, identity.AcquireAsync);
            var source = new ReconciledCollectionSource(profile, settings.Reconciliation, TimeProvider.System, checkpoint);
            var worker = new CollectionWorker(source, publisher);
            if (once)
            {
                if (hostingLab)
                {
                    using var attempt = new FileStream(settings.CheckpointPath + ".hosting-preview-attempt",
                        FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    attempt.WriteByte(1);
                    attempt.Flush(flushToDisk: true);
                }
                var start = Stopwatch.GetTimestamp();
                var outcome = await worker.RunOnceAsync(workerCancellationToken);
                OperationalLog.Write(logger, OperationalEvent.CollectionCompleted,
                    outcome == CollectionAttemptOutcome.Published ? OperationalOutcome.Succeeded : OperationalOutcome.Deferred,
                    OperationalLog.ElapsedMilliseconds(start));
                if (outcome == CollectionAttemptOutcome.Published)
                    logger.LogInformation("Worker attempt published. Event={Event}", "SCOM_WORKER_PUBLISHED");
            }
            else
            {
                using var sourceGate = new SemaphoreSlim(1);
                var consecutiveDeferredAttempts = 0;
                async Task<CollectionAttemptOutcome> Collect(CancellationToken ct)
                {
                    var start = Stopwatch.GetTimestamp();
                    await sourceGate.WaitAsync(ct);
                    try
                    {
                        var outcome = await worker.RunOnceAsync(ct);
                        consecutiveDeferredAttempts = outcome == CollectionAttemptOutcome.Deferred
                            ? consecutiveDeferredAttempts + 1
                            : 0;
                        runtimeState?.RecordAttempt(outcome, consecutiveDeferredAttempts);
                        OperationalLog.Write(logger, OperationalEvent.CollectionCompleted,
                            outcome == CollectionAttemptOutcome.Published ? OperationalOutcome.Succeeded : OperationalOutcome.Deferred,
                            OperationalLog.ElapsedMilliseconds(start));
                        return outcome;
                    }
                    finally { sourceGate.Release(); }
                }
                var scheduler = new CollectionScheduler(settings.Schedule, Collect,
                    logging.CreateLogger<CollectionScheduler>());
                if (!settings.LiveChecks.Enabled) await scheduler.RunAsync(workerCancellationToken);
                else
                {
                    var liveProfile = new PowerShellNormalizationProfile(settings.LiveChecks.Profile);
                    using var liveWorker = new TargetedLiveWorker(settings.Publisher, identity.AcquireAsync,
                        new(LiveWorkerSettings.Policy(settings.LiveChecks.Profile), settings.LiveChecks.Source, true),
                        new PowerShellTargetedLiveSource(liveProfile), TimeProvider.System, logging.CreateLogger<TargetedLiveWorker>());
                    using var combined = CancellationTokenSource.CreateLinkedTokenSource(workerCancellationToken);
                    async Task Poll()
                    {
                        var backoff = settings.LiveChecks.PollSeconds;
                        while (true)
                        {
                            combined.Token.ThrowIfCancellationRequested();
                            if (await sourceGate.WaitAsync(0, combined.Token))
                                try
                                {
                                    await liveWorker.RunOnceAsync(combined.Token);
                                    backoff = settings.LiveChecks.PollSeconds;
                                }
                                catch (Exception error) when (!combined.IsCancellationRequested &&
                                    error is QueryFault or HttpRequestException or OperationCanceledException)
                                {
                                    logger.LogWarning("Live verification deferred. Event={Event} ErrorType={ErrorType}",
                                        "SCOM_LIVE_DEFERRED", error.GetType().Name);
                                    backoff = Math.Min(30, backoff * 2);
                                }
                                finally { sourceGate.Release(); }
                            await Task.Delay(TimeSpan.FromSeconds(backoff), combined.Token);
                        }
                    }
                    var collection = scheduler.RunAsync(combined.Token);
                    var live = Poll();
                    try { await await Task.WhenAny(collection, live); }
                    finally
                    {
                        combined.Cancel();
                        // Join both loops before releasing the checkpoint lease or allowing a replacement worker.
                        try { await Task.WhenAll(collection, live); }
                        catch (OperationCanceledException) when (combined.IsCancellationRequested) { }
                    }
                }
            }
            OperationalLog.Write(logger, OperationalEvent.Stopped, OperationalOutcome.Succeeded);
            return 0;
        }
        catch (WorkerAlreadyRunningException)
        {
            LogRecovery(logger, LogLevel.Information, "WORKER_ALREADY_RUNNING", null);
            return 0;
        }
        catch (WorkerLockHeldException held)
        {
            LogRecovery(logger, LogLevel.Warning, held.Code, null);
            WorkerEventLog.Write(WorkerEventLog.Recovered, held.Code);
            return ExitTransient;
        }
        catch (WorkerRearmRequiredException)
        {
            // Best effort: persist the refusal in the operational log. No source or publisher is touched.
            try
            {
                var refused = await ReadSettingsAsync(configurationPath, CancellationToken.None);
                logging.AddProvider(new OperationalFileLoggerProvider(refused.OperationalLogging,
                    OperationalComponent.Worker));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                InvalidOperationException or JsonException)
            {
            }
            LogRecovery(logger, LogLevel.Error, "WORKER_RECOVERY_REFUSED", "PRIOR_TERMINAL_FAILURE");
            WorkerEventLog.Write(WorkerEventLog.Failed, "WORKER_RECOVERY_REFUSED Reason=PRIOR_TERMINAL_FAILURE");
            return ExitNotRestartable;
        }
        catch (OperationCanceledException) when (runtimeState?.StopRequestInvalid == true)
        {
            runtimeState.Stop("WORKER_STOP_REQUEST_INVALID");
            logger.LogError("Worker stopped. Event={Event} ErrorType={ErrorType} Code={Code}",
                "SCOM_WORKER_FAILED", runtimeState.StopRequestErrorType, "WORKER_STOP_REQUEST_INVALID");
            WorkerEventLog.Write(WorkerEventLog.Failed, "WORKER_STOP_REQUEST_INVALID");
            return ExitNotRestartable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested ||
            runtimeState?.StopRequested == true)
        {
            runtimeState?.Stop("WORKER_CANCELLED");
            logger.LogInformation("Worker stopped. Event={Event}", "SCOM_WORKER_CANCELLED");
            return 0;
        }
        catch (JsonException error) when (loadingConfiguration)
        {
            runtimeState?.Stop("WORKER_CONFIGURATION_JSON_INVALID");
            logger.LogError("Worker stopped. Event={Event} Code={Code} LineNumber={LineNumber} BytePositionInLine={BytePositionInLine}",
                "SCOM_WORKER_FAILED", "WORKER_CONFIGURATION_JSON_INVALID", error.LineNumber, error.BytePositionInLine);
            if (runtimeState is not null)
                WorkerEventLog.Write(WorkerEventLog.Failed, "WORKER_CONFIGURATION_JSON_INVALID");
            return ExitNotRestartable;
        }
        catch (Exception error)
        {
            var (code, terminal) = Classify(error);
            if (terminal) runtimeState?.Stop(code);
            else runtimeState?.StopTransient(code);
            // Never log paths, source records, configuration, credentials or
            // arbitrary exception messages from a source-profile subprocess.
            logger.LogError("Worker stopped. Event={Event} ErrorType={ErrorType} Code={Code}",
                "SCOM_WORKER_FAILED", error.GetType().Name, code);
            if (runtimeState is not null)
                WorkerEventLog.Write(terminal ? WorkerEventLog.Failed : WorkerEventLog.Recovered, code);
            return terminal ? ExitNotRestartable : ExitTransient;
        }
        finally
        {
            runtimeShutdown?.Cancel();
            if (stopRequestWatcher is not null)
            {
                try { await stopRequestWatcher; }
                catch (OperationCanceledException) when (runtimeShutdown?.IsCancellationRequested == true) { }
            }
            if (heartbeat is not null)
            {
                try { await heartbeat; }
                catch (OperationCanceledException) { }
            }
            runtimeShutdown?.Dispose();
            runtimeState?.Dispose();
        }
    }
}
