using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScomMcp;

public sealed class CollectionWorkerHostSettings
{
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
    public static async Task<int> RunAsync(string configurationPath, bool once, CancellationToken cancellationToken,
        string? runtimeStateDirectory = null)
    {
        using var logging = LoggerFactory.Create(builder => builder.AddSimpleConsole(options => options.SingleLine = true));
        var logger = logging.CreateLogger("ScomCollectionWorker");
        WorkerRuntimeState? runtimeState = null;
        var loadingConfiguration = true;
        try
        {
            if (runtimeStateDirectory is not null)
            {
                if (once)
                    throw new InvalidOperationException("Runtime state hosting is available only in long-running worker mode.");
                runtimeState = new WorkerRuntimeState(runtimeStateDirectory);
                logging.AddProvider(new BoundedWorkerFileLoggerProvider(runtimeStateDirectory));
                logger.LogInformation("Worker runtime logging initialized. Event={Event}", "SCOM_WORKER_STARTED");
            }
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
            var settings = document.RootElement.Deserialize<CollectionWorkerHostSettings>(Wire.Json) ??
                throw new InvalidOperationException("Worker configuration is empty.");
            loadingConfiguration = false;
            settings.Reconciliation.Validate();
            settings.Schedule.Validate();
            settings.Publisher.Validate();
            settings.Identity.Validate();
            settings.LiveChecks.Validate();
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
            using var checkpoint = new SourceCheckpointStore(settings.CheckpointPath, policy, settings.MaxCheckpointBytes);
            using var identity = new CertificatePublisherToken(settings.Identity);
            using var publisher = new IngestionPublisher(settings.Publisher, identity.AcquireAsync);
            var source = new ReconciledCollectionSource(profile, settings.Reconciliation, TimeProvider.System, checkpoint);
            var worker = new CollectionWorker(source, publisher);
            if (once)
            {
                await worker.RunOnceAsync(cancellationToken);
                logger.LogInformation("Worker attempt published. Event={Event}", "SCOM_WORKER_PUBLISHED");
            }
            else
            {
                using var sourceGate = new SemaphoreSlim(1);
                var consecutiveDeferredAttempts = 0;
                async Task<CollectionAttemptOutcome> Collect(CancellationToken ct)
                {
                    await sourceGate.WaitAsync(ct);
                    try
                    {
                        var outcome = await worker.RunOnceAsync(ct);
                        consecutiveDeferredAttempts = outcome == CollectionAttemptOutcome.Deferred
                            ? consecutiveDeferredAttempts + 1
                            : 0;
                        runtimeState?.RecordAttempt(outcome, consecutiveDeferredAttempts);
                        return outcome;
                    }
                    finally { sourceGate.Release(); }
                }
                var scheduler = new CollectionScheduler(settings.Schedule, Collect,
                    logging.CreateLogger<CollectionScheduler>());
                if (!settings.LiveChecks.Enabled) await scheduler.RunAsync(cancellationToken);
                else
                {
                    var liveProfile = new PowerShellNormalizationProfile(settings.LiveChecks.Profile);
                    using var liveWorker = new TargetedLiveWorker(settings.Publisher, identity.AcquireAsync,
                        new(LiveWorkerSettings.Policy(settings.LiveChecks.Profile), settings.LiveChecks.Source, true),
                        new PowerShellTargetedLiveSource(liveProfile), TimeProvider.System, logging.CreateLogger<TargetedLiveWorker>());
                    using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            return 1;
        }
        catch (Exception error)
        {
            runtimeState?.Stop(error is CollectionAttemptsExhaustedException
                ? "COLLECTION_ATTEMPTS_EXHAUSTED"
                : error is IngestionPublicationException publicationFailure ? publicationFailure.Code :
                error is QueryFault queryFailure ? queryFailure.Code :
                error is JsonException ? "WORKER_CONFIGURATION_JSON_INVALID" :
                "WORKER_CONFIGURATION_OR_EXECUTION_FAILED");
            // Never log paths, source records, configuration, credentials or
            // arbitrary exception messages from a source-profile subprocess.
            logger.LogError("Worker stopped. Event={Event} ErrorType={ErrorType} Code={Code}",
                "SCOM_WORKER_FAILED", error.GetType().Name,
                error is CollectionAttemptsExhaustedException ? "COLLECTION_ATTEMPTS_EXHAUSTED" :
                error is IngestionPublicationException publication ? publication.Code :
                error is QueryFault query ? query.Code : "WORKER_CONFIGURATION_OR_EXECUTION_FAILED");
            return 1;
        }
        finally
        {
            runtimeState?.Dispose();
        }
    }
}
