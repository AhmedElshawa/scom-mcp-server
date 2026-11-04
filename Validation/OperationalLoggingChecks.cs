using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ScomMcp;

static class OperationalLoggingChecks
{
    public static void Run(string root)
    {
        var assertions = 0;
        var directory = Path.Combine(root, "operational-worker");
        Directory.CreateDirectory(directory);
        var settings = new OperationalLoggingSettings
        {
            Directory = directory, MaxFileBytes = OperationalLoggingSettings.MinimumFileBytes, RetainedFiles = 3
        };
        const string secret = "PRIVATE-CREDENTIAL-token-connection-string-raw-record-identifier-path";
        using (var provider = new OperationalFileLoggerProvider(settings, OperationalComponent.Worker))
        {
            var logger = provider.CreateLogger(secret);
            using var scope = logger.BeginScope(secret);
            logger.Log(LogLevel.Error, new EventId(99, secret), new HostileState(),
                new InvalidOperationException(secret), static (_, _) =>
                    throw new InvalidOperationException("Formatter must never run."));
            logger.LogError(new InvalidOperationException(secret), "{Private}", secret);
            logger.LogError("Worker stopped. Event={Event} ErrorType={ErrorType} Code={Code}",
                secret, secret, secret);
            logger.LogError("Worker stopped. Event={Event} ErrorType={ErrorType} Code={Code}",
                secret, secret, "PUBLISHER_HTTP_403");
            logger.LogError("Worker stopped. Event={Event} ErrorType={ErrorType} Code={Code}",
                secret, secret, "SOURCE_UNAVAILABLE");
            var initial = File.ReadAllText(Path.Combine(directory, "worker.log"));
            Check(!initial.Contains(secret) && initial.Contains("PUBLISHER_HTTP_403") &&
                initial.Contains("SOURCE_UNAVAILABLE"), "allowlisted codes survive without raw state or exception content");
            for (var index = 0; index < 1800; index++)
                OperationalLog.Write(logger, OperationalEvent.CollectionCompleted, OperationalOutcome.Succeeded, index);
            Throws(() => new OperationalFileLoggerProvider(settings, OperationalComponent.Worker),
                "second writer cannot bypass rotation bounds");
        }
        var files = Directory.GetFiles(directory, "worker.log*");
        Check(files.Length == settings.RetainedFiles &&
            files.All(file => new FileInfo(file).Length <= settings.MaxFileBytes) &&
            files.Sum(file => new FileInfo(file).Length) <= (long)settings.MaxFileBytes * settings.RetainedFiles,
            "retention includes the active file and bounds bytes exactly");
        var allowed = new HashSet<string>
        {
            "utc", "component", "level", "event", "outcome", "durationMs", "code", "attempt",
            "maxAttempts", "delaySeconds", "lineNumber", "bytePositionInLine"
        };
        foreach (var file in files)
            foreach (var line in File.ReadLines(file))
            {
                using var document = JsonDocument.Parse(line);
                Check(document.RootElement.EnumerateObject().All(field => allowed.Contains(field.Name)) &&
                    !line.Contains(secret) && document.RootElement.GetProperty("component").GetString() == "worker",
                    "each rotated record is strictly structured and safe");
            }
        using (var reopened = new OperationalFileLoggerProvider(settings, OperationalComponent.Worker))
            OperationalLog.Write(reopened.CreateLogger("ignored"), OperationalEvent.Started, OperationalOutcome.Succeeded);
        Throws(() => new OperationalFileLoggerProvider(settings, OperationalComponent.Server),
            "server cannot reuse a worker directory after the worker exits");

        var serverDirectory = Path.Combine(root, "operational-server");
        Directory.CreateDirectory(serverDirectory);
        settings.Directory = serverDirectory;
        settings.RetainedFiles = 1;
        using (var provider = new OperationalFileLoggerProvider(settings, OperationalComponent.Server))
        {
            var logger = provider.CreateLogger(secret);
            for (var index = 0; index < 800; index++)
                OperationalLog.Write(logger, OperationalEvent.RequestCompleted, OperationalOutcome.Failed, 123);
        }
        Check(Directory.GetFiles(serverDirectory, "server.log*").Length == 1 &&
            new FileInfo(Path.Combine(serverDirectory, "server.log")).Length <= settings.MaxFileBytes,
            "one retained file rotates without an archive");

        var missing = Path.Combine(root, "not-created-by-validation");
        new OperationalLoggingSettings { Directory = missing }.Validate();
        Check(!Directory.Exists(missing), "settings validation is pure and does not create directories");
        Throws(() => new OperationalFileLoggerProvider(new() { Directory = missing }, OperationalComponent.Server),
            "runtime refuses a missing directory");
        foreach (var bad in new[] { "", "relative", @"\\host\share", @"\\?\C:\logs", @"C:\logs:stream",
            @"C:\logs\..\other", @"C:\logs.", @"C:\logs\bad?", @"C:\logs\bad " })
            Throws(() => new OperationalLoggingSettings { Directory = bad }.Validate(), "invalid path rejected");
        foreach (var bytes in new[] { int.MinValue, 0, OperationalLoggingSettings.MinimumFileBytes - 1,
            OperationalLoggingSettings.MaximumFileBytes + 1 })
            Throws(() => new OperationalLoggingSettings { Directory = directory, MaxFileBytes = bytes }.Validate(),
                "unbounded file size rejected");
        foreach (var retained in new[] { -1, 0, OperationalLoggingSettings.MaximumRetainedFiles + 1 })
            Throws(() => new OperationalLoggingSettings { Directory = directory, RetainedFiles = retained }.Validate(),
                "unbounded retention rejected");

        var ioDirectory = Path.Combine(root, "operational-io");
        Directory.CreateDirectory(ioDirectory);
        var ioSettings = new OperationalLoggingSettings { Directory = ioDirectory };
        using (var provider = new OperationalFileLoggerProvider(ioSettings, OperationalComponent.Worker))
        {
            var failed = false;
            provider.OnFailure = () => failed = true;
            File.Delete(Path.Combine(ioDirectory, "worker.log"));
            Directory.CreateDirectory(Path.Combine(ioDirectory, "worker.log"));
            var logger = provider.CreateLogger("ignored");
            Throws(() => OperationalLog.Write(logger, OperationalEvent.Started, OperationalOutcome.Succeeded),
                "log I/O failure is explicit");
            Check(failed, "log failure notifies the owning host");
            Directory.Delete(Path.Combine(ioDirectory, "worker.log"));
            Throws(() => OperationalLog.Write(logger, OperationalEvent.Started, OperationalOutcome.Succeeded),
                "failed sink cannot silently resume");
        }
        var oversizedDirectory = Path.Combine(root, "operational-oversized");
        Directory.CreateDirectory(oversizedDirectory);
        File.WriteAllBytes(Path.Combine(oversizedDirectory, "worker.log"),
            new byte[OperationalLoggingSettings.MinimumFileBytes + 1]);
        Throws(() => new OperationalFileLoggerProvider(new()
        {
            Directory = oversizedDirectory, MaxFileBytes = OperationalLoggingSettings.MinimumFileBytes
        }, OperationalComponent.Worker), "preexisting oversized logs fail closed");

        var junction = Path.Combine(root, "operational-junction");
        var junctionStart = new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{junction}\" \"{directory}\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var process = System.Diagnostics.Process.Start(junctionStart)!)
        {
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("Synthetic junction creation failed.");
        }
        try
        {
            Throws(() => new OperationalFileLoggerProvider(new() { Directory = junction }, OperationalComponent.Worker),
                "directory reparse point rejected");
        }
        finally { Directory.Delete(junction); }

        var worker = JsonSerializer.Deserialize<CollectionWorkerHostSettings>(
            """{"operationalLogging":{"directory":"C:\\synthetic\\worker","maxFileBytes":1048576,"retainedFiles":5}}""",
            Wire.Json)!;
        worker.OperationalLogging.Validate();
        var receiver = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperationalLogging:Directory"] = @"C:\synthetic\receiver",
            ["OperationalLogging:MaxFileBytes"] = "1048576",
            ["OperationalLogging:RetainedFiles"] = "5"
        }).Build().GetSection("OperationalLogging").Get<OperationalLoggingSettings>()!;
        receiver.Validate();
        Check(worker.OperationalLogging.MaxFileBytes == 1048576 && worker.OperationalLogging.RetainedFiles == 5 &&
            receiver.MaxFileBytes == 1048576 && receiver.RetainedFiles == 5,
            "worker camelCase JSON and receiver configuration section bind the shared contract");
        Console.WriteLine($"PASS: {assertions} operational logging assertions; bounded JSON files, safe projection, I/O failure and settings.");

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        void Throws(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException error) when (!error.Message.Contains(secret)) { assertions++; return; }
            throw new InvalidOperationException(message);
        }
    }

    private sealed class HostileState
    {
        public override string ToString() => throw new InvalidOperationException("State must never be formatted.");
    }
}
