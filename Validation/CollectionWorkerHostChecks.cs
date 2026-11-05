using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ScomMcp;

static class CollectionWorkerHostChecks
{
    public static async Task Run(string root)
    {
        var count = 0;
        var example = Path.GetFullPath(Path.Combine(root, "..", "..", "ScomMcp", "appsettings.Worker.example.json"));
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(example)))
        {
            var settings = document.RootElement.Deserialize<CollectionWorkerHostSettings>(Wire.Json)!;
            settings.Publisher.Validate();
            settings.Reconciliation.Validate();
            settings.Schedule.Validate();
            if (settings.Schedule.AttemptDeadline < TimeSpan.FromSeconds(settings.Profile.DeadlineSeconds +
                settings.Publisher.PublicationTimeoutSeconds))
                throw new InvalidOperationException("Example worker deadline cannot cover source plus publication.");
            count++;
        }
        await Reject(["--worker"]);
        await Reject(["--worker-once"]);
        await Reject(["--worker", Path.Combine(root, "does-not-exist.json")]);
        await Reject(["--worker-once", "relative.json"]);
        await Reject(["--worker-once", Path.Combine(root, "does-not-exist.json"), "--runtime-state-dir", root]);
        var failedRuntimeState = Path.Combine(root, "failed-runtime-state");
        Directory.CreateDirectory(failedRuntimeState);
        await Reject(["--worker", Path.Combine(root, "does-not-exist.json"),
            "--runtime-state-dir", failedRuntimeState],
            requiredOutput: "WORKER_CONFIGURATION_OR_EXECUTION_FAILED");
        using (var status = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(failedRuntimeState, "worker.status.json"))))
        {
            if (status.RootElement.GetProperty("state").GetString() != "stopped" ||
                status.RootElement.GetProperty("failureCode").GetString() != "WORKER_CONFIGURATION_OR_EXECUTION_FAILED")
                throw new InvalidOperationException("Failed worker host did not report a stopped status.");
            count++;
        }
        var terminalRuntimeState = Path.Combine(root, "terminal-runtime-state");
        Directory.CreateDirectory(terminalRuntimeState);
        using (var priorWorker = new WorkerRuntimeState(terminalRuntimeState))
            priorWorker.Stop("SYNTHETIC_TERMINAL_FAILURE");
        var priorFailureStatus = await File.ReadAllBytesAsync(
            Path.Combine(terminalRuntimeState, "worker.status.json"));
        var gatedResult = await CollectionWorkerHost.RunAsync(Path.Combine(root, "invalid-before-source.json"),
            false, CancellationToken.None, terminalRuntimeState);
        if (gatedResult != 1 || !priorFailureStatus.SequenceEqual(await File.ReadAllBytesAsync(
            Path.Combine(terminalRuntimeState, "worker.status.json"))))
            throw new InvalidOperationException("Gated startup changed failure state or continued toward source configuration.");
        count++;
        var rearmOutput = await Accept(["--worker-rearm", terminalRuntimeState]);
        using (var status = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(terminalRuntimeState, "worker.status.json"))))
        {
            var rootElement = status.RootElement;
            if (!rearmOutput.Contains("runtime state rearmed", StringComparison.Ordinal) ||
                rootElement.GetProperty("rearmRequired").GetBoolean() ||
                rootElement.GetProperty("failureCode").ValueKind != JsonValueKind.Null ||
                rootElement.GetProperty("lastFailureCode").GetString() != "SYNTHETIC_TERMINAL_FAILURE")
                throw new InvalidOperationException("Worker rearm CLI did not preserve the failure evidence.");
            count++;
        }
        var stopRequestDirectory = Path.Combine(root, "stop-request-cli");
        Directory.CreateDirectory(stopRequestDirectory);
        var stopRequestOutput = await Accept(["--worker-stop-request", stopRequestDirectory]);
        if (!stopRequestOutput.Contains("stop request recorded", StringComparison.Ordinal) ||
            await File.ReadAllTextAsync(Path.Combine(stopRequestDirectory, "worker.stop.request")) !=
            "SCOM_WORKER_STOP_REQUEST")
            throw new InvalidOperationException("Worker stop-request CLI did not create its exact cooperative request.");
        count++;
        var invalidJson = Path.Combine(root, "invalid-worker.json");
        try
        {
            const string invalidValuePrefix = "\"private-sensitive-marker\": ";
            await File.WriteAllTextAsync(invalidJson, "{\n" + invalidValuePrefix + "!\n}");
            await Reject(["--worker-once", invalidJson], "WORKER_CONFIGURATION_JSON_INVALID",
                requiredOutput: $"LineNumber=1 BytePositionInLine={Encoding.UTF8.GetByteCount(invalidValuePrefix)}");
            await File.WriteAllTextAsync(invalidJson, "{\"maxCheckpointBytes\":\"private-sensitive-marker\"}");
            await Reject(["--worker-once", invalidJson], "WORKER_CONFIGURATION_JSON_INVALID");
            await File.WriteAllBytesAsync(invalidJson, [0xEF, 0xBB, 0xBF, .. "{}"u8.ToArray()]);
            await Reject(["--worker-once", invalidJson], forbiddenCode: "WORKER_CONFIGURATION_JSON_INVALID",
                requiredOutput: "WORKER_CONFIGURATION_OR_EXECUTION_FAILED");
            await File.WriteAllBytesAsync(invalidJson, "{}"u8.ToArray());
            await Reject(["--worker-once", invalidJson], forbiddenCode: "WORKER_CONFIGURATION_JSON_INVALID",
                requiredOutput: "WORKER_CONFIGURATION_OR_EXECUTION_FAILED");
            await File.WriteAllBytesAsync(invalidJson, [0xEF, 0xBB, 0xBF, .. "{!"u8.ToArray()]);
            await Reject(["--worker-once", invalidJson], "WORKER_CONFIGURATION_JSON_INVALID");
            await File.WriteAllBytesAsync(invalidJson, [0xFF, 0xFE, 0x7B, 0x00, 0x7D, 0x00]);
            await Reject(["--worker-once", invalidJson], "WORKER_CONFIGURATION_JSON_INVALID");
        }
        finally { File.Delete(invalidJson); }
        Console.WriteLine($"PASS: {count} worker host assertions; example budgets and real CLI rejects without starting MCP.");

        async Task Reject(string[] args, string? expectedCode = null, string? forbiddenCode = null,
            string? requiredOutput = null)
        {
            var assembly = Assembly.GetExecutingAssembly().Location;
            var directory = Path.GetDirectoryName(assembly)!;
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), Path.Combine(directory, "ScomMcp.dll") }.Concat(args))
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Worker CLI test could not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var output = await stdout + await stderr;
                if (process.ExitCode != 1 || output.Length > 4096 ||
                    output.Contains("Now listening", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("private-sensitive-marker", StringComparison.Ordinal) ||
                    forbiddenCode is not null && output.Contains(forbiddenCode, StringComparison.Ordinal) ||
                    requiredOutput is not null && !output.Contains(requiredOutput, StringComparison.Ordinal) ||
                    expectedCode is not null && (!output.Contains(expectedCode, StringComparison.Ordinal) ||
                        !output.Contains("LineNumber=", StringComparison.Ordinal) ||
                        !output.Contains("BytePositionInLine=", StringComparison.Ordinal)) ||
                    args.Length == 2 && output.Contains(args[1], StringComparison.Ordinal))
                    throw new InvalidOperationException("Invalid worker CLI mode did not fail safely.");
                count++;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        async Task<string> Accept(string[] args)
        {
            var assembly = Assembly.GetExecutingAssembly().Location;
            var directory = Path.GetDirectoryName(assembly)!;
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), Path.Combine(directory, "ScomMcp.dll") }.Concat(args))
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Worker CLI test could not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var output = await stdout + await stderr;
            if (process.ExitCode != 0 || output.Length > 4096 ||
                output.Contains("Now listening", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Worker control CLI did not complete successfully.");
            return output;
        }
    }
}
