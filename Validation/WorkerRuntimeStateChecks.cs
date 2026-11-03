using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScomMcp;

static class WorkerRuntimeStateChecks
{
    public static void Run(string root)
    {
        var assertions = 0;
        var directory = Path.Combine(root, "runtime-state");
        Directory.CreateDirectory(directory);
        DateTimeOffset? published;
        using (var state = new WorkerRuntimeState(directory))
        {
            Check(ReadStatus(directory).State == "running", "startup status is persisted", ref assertions);
            try
            {
                using var duplicate = new WorkerRuntimeState(directory);
                throw new InvalidOperationException("Duplicate worker runtime lease accepted.");
            }
            catch (IOException) { assertions++; }
            state.RecordAttempt(CollectionAttemptOutcome.Deferred, 1);
            var deferred = ReadStatus(directory);
            Check(deferred.LastOutcome == "deferred" && deferred.ConsecutiveDeferredAttempts == 1,
                "deferred attempt remains distinct from publication", ref assertions);
            state.RecordAttempt(CollectionAttemptOutcome.Published, 0);
            var success = ReadStatus(directory);
            published = success.LastPublicationUtc;
            Check(success.LastOutcome == "published" && published is not null,
                "publication timestamp is persisted", ref assertions);
            state.Stop("SYNTHETIC_FAILURE");
            Check(ReadStatus(directory).State == "stopped", "failure status is actionable", ref assertions);
        }

        using (var recovered = new WorkerRuntimeState(directory))
        {
            var restarted = ReadStatus(directory);
            Check(restarted.State == "running" && restarted.LastPublicationUtc == published,
                "restart preserves prior publication evidence", ref assertions);
            recovered.Stop("WORKER_CANCELLED");
        }

        using (var provider = new BoundedWorkerFileLoggerProvider(directory))
        {
            var logger = provider.CreateLogger("ScomCollectionWorker");
            var line = new string('x', 5000);
            for (var index = 0; index < 1100; index++)
                logger.LogInformation(new EventId(index), "{Synthetic}", line);
        }
        var logs = Directory.GetFiles(directory, "worker.log*");
        Check(logs.Length >= 2 && logs.All(path => new FileInfo(path).Length <= 4 * 1024 * 1024) &&
            logs.Sum(path => new FileInfo(path).Length) <= 12 * 1024 * 1024,
            "persistent logs rotate within fixed per-file and total byte bounds", ref assertions);
        Console.WriteLine($"PASS: {assertions} worker runtime-state assertions; lease, restart evidence and bounded protected-log format.");
    }

    private static WorkerRuntimeStatus ReadStatus(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "worker.status.json")));
        return document.RootElement.Deserialize<WorkerRuntimeStatus>(Wire.Json) ??
            throw new InvalidOperationException("Worker runtime status was not readable.");
    }

    private static void Check(bool condition, string message, ref int count)
    {
        if (!condition) throw new InvalidOperationException(message);
        count++;
    }
}
