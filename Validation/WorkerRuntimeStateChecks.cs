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
            Check(ReadStatus(directory).State == "running" && !ReadStatus(directory).RearmRequired,
                "healthy startup status is persisted", ref assertions);
            try
            {
                using var duplicate = new WorkerRuntimeState(directory);
                throw new InvalidOperationException("Duplicate worker runtime lease accepted.");
            }
            catch (IOException) { assertions++; }
            try
            {
                WorkerRuntimeState.Rearm(directory);
                throw new InvalidOperationException("Rearm acquired an active worker lease.");
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
            var failure = ReadStatus(directory);
            Check(failure.State == "stopped" && failure.RearmRequired &&
                failure.FailureCode == "SYNTHETIC_FAILURE" && failure.LastFailureCode == "SYNTHETIC_FAILURE",
                "terminal failure persists an explicit rearm gate and evidence", ref assertions);
        }

        var terminalStatusBytes = File.ReadAllBytes(Path.Combine(directory, "worker.status.json"));
        try
        {
            using var blocked = new WorkerRuntimeState(directory);
            throw new InvalidOperationException("Terminal failure did not survive a fresh worker/reboot simulation.");
        }
        catch (WorkerRearmRequiredException) { assertions++; }
        Check(terminalStatusBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "worker.status.json"))),
            "blocked startup preserves the terminal status unchanged", ref assertions);

        WorkerRuntimeState.Rearm(directory);
        var rearmedStatus = ReadStatus(directory);
        Check(rearmedStatus.State == "stopped" && !rearmedStatus.RearmRequired &&
            rearmedStatus.FailureCode is null && rearmedStatus.LastFailureCode == "SYNTHETIC_FAILURE" &&
            rearmedStatus.RearmedUtc is not null && rearmedStatus.LastPublicationUtc == published,
            "explicit rearm preserves publication and failure evidence", ref assertions);
        using (var recovered = new WorkerRuntimeState(directory))
        {
            var restarted = ReadStatus(directory);
            Check(restarted.State == "running" && restarted.LastPublicationUtc == published,
                "explicitly rearmed healthy startup preserves prior publication evidence", ref assertions);
            recovered.Stop("WORKER_CANCELLED");
        }

        using (var graceful = new WorkerRuntimeState(directory))
        using (var shutdown = new CancellationTokenSource())
        {
            var watcher = graceful.WatchStopRequestsAsync(shutdown, shutdown.Token);
            WorkerRuntimeState.RequestStop(directory);
            watcher.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Check(shutdown.IsCancellationRequested && graceful.StopRequested,
                "explicit stop request is cooperatively observed", ref assertions);
            try
            {
                WorkerRuntimeState.Rearm(directory);
                throw new InvalidOperationException("Rearm was allowed while the worker held its lease.");
            }
            catch (IOException) { assertions++; }
            graceful.Stop("WORKER_CANCELLED");
        }

        using (var interrupted = new WorkerRuntimeState(directory))
        {
            Check(ReadStatus(directory).State == "running", "clean stop permits healthy restart", ref assertions);
        }
        try
        {
            using var afterPowerLoss = new WorkerRuntimeState(directory);
            throw new InvalidOperationException("Unexpected prior-running state did not gate startup.");
        }
        catch (WorkerRearmRequiredException) { assertions++; }
        WorkerRuntimeState.Rearm(directory);
        Check(ReadStatus(directory).LastFailureCode == "UNEXPECTED_PRIOR_RUNNING_STATE",
            "power-loss rearm records the unknown prior-running outcome", ref assertions);

        var malformedPath = Path.Combine(directory, "worker.status.json");
        var validStatus = File.ReadAllText(malformedPath);
        try
        {
            var malformedStatus = System.Text.RegularExpressions.Regex.Replace(validStatus,
                "\"state\"\\s*:\\s*\"stopped\"", "\"state\":\"mystery\"");
            if (malformedStatus == validStatus)
                throw new InvalidOperationException("Synthetic status mutation did not locate the state field.");
            File.WriteAllText(malformedPath, malformedStatus);
            var malformedBytes = File.ReadAllBytes(malformedPath);
            try
            {
                WorkerRuntimeState.Rearm(directory);
                throw new InvalidOperationException("Malformed status was accepted by rearm.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            {
                assertions++;
            }
            Check(malformedBytes.SequenceEqual(File.ReadAllBytes(malformedPath)),
                "malformed state is not overwritten by rearm", ref assertions);
        }
        finally { File.WriteAllText(malformedPath, validStatus); }

        var malformedStopDirectory = Path.Combine(root, "malformed-stop-request");
        Directory.CreateDirectory(malformedStopDirectory);
        using (var state = new WorkerRuntimeState(malformedStopDirectory))
        using (var shutdown = new CancellationTokenSource())
        {
            File.WriteAllText(Path.Combine(malformedStopDirectory, "worker.stop.request"), "unknown");
            state.WatchStopRequestsAsync(shutdown, shutdown.Token).WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter().GetResult();
            Check(state.StopRequestInvalid && shutdown.IsCancellationRequested,
                "malformed stop request fails closed", ref assertions);
            state.Stop("WORKER_STOP_REQUEST_INVALID");
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
        Console.WriteLine($"PASS: {assertions} worker runtime-state assertions; reboot gate, explicit rearm, healthy recovery, stop and bounded logs.");
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
