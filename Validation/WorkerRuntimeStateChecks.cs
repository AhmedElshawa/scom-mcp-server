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
            catch (WorkerAlreadyRunningException) { assertions++; }
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
        using (var afterPowerLoss = new WorkerRuntimeState(directory))
        {
            var recoveredStatus = ReadStatus(directory);
            Check(afterPowerLoss.Recovery is not null && recoveredStatus.State == "running" &&
                !recoveredStatus.RearmRequired && recoveredStatus.LastFailureCode == "UNEXPECTED_PRIOR_RUNNING_STATE" &&
                recoveredStatus.RearmedUtc is not null,
                "unexpected prior-running state recovers automatically and records the evidence", ref assertions);
            afterPowerLoss.Stop("WORKER_CANCELLED");
        }
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

        assertions += SelfHealingChecks(root);

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

    private static int SelfHealingChecks(string root)
    {
        var assertions = 0;
        var interval = TimeSpan.FromMinutes(5);
        string NewDirectory(string name)
        {
            var path = Path.Combine(root, "self-healing-" + name);
            Directory.CreateDirectory(path);
            return path;
        }
        // Simulates a worker that died without a clean stop: the marker stays "running", the lock is released.
        static ManualClock Crash(string directory, FakeProbe probe, TimeSpan interval)
        {
            var clock = new ManualClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
            new WorkerRuntimeState(directory, interval, probe, clock).Dispose();
            return clock;
        }
        WorkerRecovery? Recover(string directory, FakeProbe fake, ManualClock time)
        {
            using var state = new WorkerRuntimeState(directory, interval, fake, time);
            var status = ReadStatus(directory);
            Check(status.State == "running" && !status.RearmRequired && status.ProcessId == Environment.ProcessId &&
                status.HeartbeatUtc == time.Now && status.BootId == fake.BootId && status.HostName == fake.HostName,
                "recovered worker rearms itself and writes fresh identity", ref assertions);
            return state.Recovery;
        }

        var directory = NewDirectory("identity");
        var probe = new FakeProbe();
        var clock = Crash(directory, probe, interval);
        var marker = ReadStatus(directory);
        Check(marker.State == "running" && marker.ProcessId == Environment.ProcessId &&
            marker.ProcessStartTimeUtc == probe.Start && marker.BootId == probe.BootId &&
            marker.HostName == probe.HostName && marker.HeartbeatUtc == clock.Now,
            "status records pid, process start time, boot id, host and heartbeat", ref assertions);
        var heartbeatDirectory = NewDirectory("heartbeat");
        using (var live = new WorkerRuntimeState(heartbeatDirectory, interval, probe, clock))
        {
            clock.Now += TimeSpan.FromSeconds(30);
            live.Heartbeat();
            Check(ReadStatus(heartbeatDirectory).HeartbeatUtc == clock.Now,
                "heartbeat advances heartbeatUtc", ref assertions);
        }

        // Lock acquisition is authoritative: a live-looking marker with a free lock is still recovered.
        probe = new FakeProbe();
        directory = NewDirectory("lock-free-owner-gone");
        clock = Crash(directory, probe, interval);
        Check(Recover(directory, probe, clock)?.Reason == "LOCK_FREE_NO_OWNER",
            "lock-free marker with a live identical process is recovered without killing anything", ref assertions);

        probe = new FakeProbe();
        directory = NewDirectory("dead-pid");
        clock = Crash(directory, probe, interval);
        probe.Alive = false;
        Check(Recover(directory, probe, clock)?.Reason == "PROCESS_NOT_RUNNING",
            "dead PID marker recovers automatically", ref assertions);

        probe = new FakeProbe();
        directory = NewDirectory("reused-pid");
        clock = Crash(directory, probe, interval);
        probe.StartOffset = TimeSpan.FromHours(3);
        Check(Recover(directory, probe, clock)?.Reason == "PID_REUSED",
            "reused PID marker recovers automatically", ref assertions);

        probe = new FakeProbe();
        directory = NewDirectory("boot-change");
        clock = Crash(directory, probe, interval);
        probe.BootOffset = TimeSpan.FromMinutes(20);
        Check(Recover(directory, probe, clock)?.Reason == "BOOT_ID_CHANGED",
            "boot id change recovers automatically", ref assertions);

        probe = new FakeProbe();
        directory = NewDirectory("host-change");
        clock = Crash(directory, probe, interval);
        probe.Host = "OTHERHOST";
        Check(Recover(directory, probe, clock)?.Reason == "HOST_CHANGED",
            "host change recovers automatically", ref assertions);

        probe = new FakeProbe();
        directory = NewDirectory("boot-jitter");
        clock = Crash(directory, probe, interval);
        probe.BootOffset = TimeSpan.FromSeconds(2);
        Check(Recover(directory, probe, clock)?.Reason == "LOCK_FREE_NO_OWNER",
            "sub-minute boot time jitter is not treated as a reboot", ref assertions);

        probe = new FakeProbe();
        directory = NewDirectory("hung-heartbeat");
        clock = Crash(directory, probe, interval);
        clock.Now += 3 * interval + TimeSpan.FromSeconds(1);
        Check(Recover(directory, probe, clock)?.Reason == "HEARTBEAT_STALE",
            "heartbeat older than 3x the schedule interval recovers automatically", ref assertions);

        // Live concurrent instance: the lock holder stays untouched and the newcomer is told so.
        probe = new FakeProbe();
        directory = NewDirectory("concurrent");
        clock = new ManualClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using (var first = new WorkerRuntimeState(directory, interval, probe, clock))
        {
            var before = File.ReadAllBytes(Path.Combine(directory, "worker.status.json"));
            clock.Now += 3 * interval;
            try
            {
                using var second = new WorkerRuntimeState(directory, interval, probe, clock);
                throw new InvalidOperationException("Concurrent worker instance was accepted.");
            }
            catch (WorkerAlreadyRunningException) { assertions++; }
            Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "worker.status.json"))),
                "concurrent instance leaves the running worker's status untouched", ref assertions);

            clock.Now += TimeSpan.FromSeconds(1);
            try
            {
                using var second = new WorkerRuntimeState(directory, interval, probe, clock);
                throw new InvalidOperationException("Hung lock holder was replaced.");
            }
            catch (WorkerLockHeldException error) when (error.Code == "WORKER_HEARTBEAT_STALE_LOCK_HELD") { assertions++; }

            first.Heartbeat();
            probe.Alive = false;
            try
            {
                using var second = new WorkerRuntimeState(directory, interval, probe, clock);
                throw new InvalidOperationException("Unverified lock holder was replaced.");
            }
            catch (WorkerLockHeldException error) when (error.Code == "WORKER_LOCK_HELD_UNVERIFIED") { assertions++; }
            Check(ReadStatus(directory).State == "running", "refused instance never alters the lock holder's state", ref assertions);
        }

        // Transient failure must not gate the next start; a terminal one must.
        probe = new FakeProbe();
        directory = NewDirectory("transient");
        clock = new ManualClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using (var transient = new WorkerRuntimeState(directory, interval, probe, clock))
            transient.StopTransient("WORKER_TRANSIENT_FAILURE");
        var transientStatus = ReadStatus(directory);
        Check(transientStatus.State == "stopped" && !transientStatus.RearmRequired && transientStatus.FailureCode is null &&
            transientStatus.LastFailureCode == "WORKER_TRANSIENT_FAILURE",
            "transient stop records evidence without an operator gate", ref assertions);
        using (var restarted = new WorkerRuntimeState(directory, interval, probe, clock))
            Check(restarted.Recovery is null, "restart after a transient stop is not a recovery", ref assertions);

        // Status files written before this release carry no identity or heartbeat fields.
        directory = NewDirectory("legacy-status");
        File.WriteAllText(Path.Combine(directory, "worker.status.json"),
            """
            {"state":"running","startedUtc":"2029-12-31T00:00:00+00:00","updatedUtc":"2029-12-31T00:00:00+00:00",
            "lastAttemptUtc":null,"lastPublicationUtc":null,"lastOutcome":null,"consecutiveDeferredAttempts":0,
            "failureCode":null,"processId":4242,"rearmRequired":false,"lastFailureCode":null,"rearmedUtc":null}
            """);
        probe = new FakeProbe { Alive = false };
        clock = new ManualClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using (var upgraded = new WorkerRuntimeState(directory, interval, probe, clock))
            Check(upgraded.Recovery?.Reason == "PROCESS_NOT_RUNNING" && ReadStatus(directory).BootId == probe.BootId,
                "legacy status without identity fields is accepted and recovered", ref assertions);
        return assertions;
    }

    private sealed class FakeProbe : IWorkerProcessProbe
    {
        private static readonly DateTimeOffset BootBase = new(2029, 12, 30, 8, 0, 0, TimeSpan.Zero);
        public string Host { get; set; } = "SYNTHETIC-HOST";
        public bool Alive { get; set; } = true;
        public TimeSpan StartOffset { get; set; }
        public TimeSpan BootOffset { get; set; }
        public DateTimeOffset Start { get; } = new(2029, 12, 31, 12, 0, 0, TimeSpan.Zero);
        public string HostName => Host;
        public string BootId => WorkerProcessProbe.FormatBootId(BootBase + BootOffset);
        public DateTimeOffset OwnStartTimeUtc => Start;
        public DateTimeOffset? ProcessStartTimeUtc(int processId) =>
            Alive && processId == Environment.ProcessId ? Start + StartOffset : null;
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
