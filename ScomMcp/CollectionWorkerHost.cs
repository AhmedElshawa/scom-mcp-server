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
}

public static class CollectionWorkerHost
{
    public static async Task<int> RunAsync(string configurationPath, bool once, CancellationToken cancellationToken)
    {
        using var logging = LoggerFactory.Create(builder => builder.AddSimpleConsole(options => options.SingleLine = true));
        var logger = logging.CreateLogger("ScomCollectionWorker");
        var loadingConfiguration = true;
        try
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
            var settings = document.RootElement.Deserialize<CollectionWorkerHostSettings>(Wire.Json) ??
                throw new InvalidOperationException("Worker configuration is empty.");
            loadingConfiguration = false;
            settings.Reconciliation.Validate();
            settings.Schedule.Validate();
            settings.Publisher.Validate();
            settings.Identity.Validate();
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
                var scheduler = new CollectionScheduler(settings.Schedule, worker.RunOnceAsync,
                    logging.CreateLogger<CollectionScheduler>());
                await scheduler.RunAsync(cancellationToken);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Worker stopped. Event={Event}", "SCOM_WORKER_CANCELLED");
            return 0;
        }
        catch (JsonException error) when (loadingConfiguration)
        {
            logger.LogError("Worker stopped. Event={Event} Code={Code} LineNumber={LineNumber} BytePositionInLine={BytePositionInLine}",
                "SCOM_WORKER_FAILED", "WORKER_CONFIGURATION_JSON_INVALID", error.LineNumber, error.BytePositionInLine);
            return 1;
        }
        catch (Exception error)
        {
            // Never log paths, source records, configuration, credentials or
            // arbitrary exception messages from a source-profile subprocess.
            logger.LogError("Worker stopped. Event={Event} ErrorType={ErrorType} Code={Code}",
                "SCOM_WORKER_FAILED", error.GetType().Name,
                error is IngestionPublicationException publication ? publication.Code :
                error is QueryFault query ? query.Code : "WORKER_CONFIGURATION_OR_EXECUTION_FAILED");
            return 1;
        }
    }
}
