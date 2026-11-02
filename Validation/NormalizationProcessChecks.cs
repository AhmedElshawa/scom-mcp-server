using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ScomMcp;

static class NormalizationProcessChecks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private const string Input = "{\"protocolVersion\":1,\"text\":\"測定 ü\"}";
    private static NormalizedSourceScan Scan => new("22222222-2222-4222-8222-222222222222", "managementGroup",
        null, true, [], [], [], new(Now, true, null), [], new(Now, true, null), [], Now);

    public static async Task Run()
    {
        var count = 0;
        using var configuration = JsonDocument.Parse("{}");
        var request = new SourceCollectionRequest(Now, Now.AddDays(-14),
            [new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")]);
        using (var encoded = JsonDocument.Parse(NormalizationProcess.EncodeRequest(configuration.RootElement, request, 1024)))
        {
            if (encoded.RootElement.GetProperty("request").GetProperty("priorUnresolvedAlerts").GetArrayLength() != 1 ||
                encoded.RootElement.GetProperty("protocolVersion").GetInt32() != 1)
                throw new InvalidOperationException("Reconciliation context missing from bridge request.");
            count++;
        }
        foreach (var invalid in new[]
        {
            request with { MetricHistoryStartUtc = Now },
            request with { PriorUnresolvedAlerts = request.PriorUnresolvedAlerts.Add(request.PriorUnresolvedAlerts[0]) },
            request with { PriorUnresolvedAlerts = default }
        })
        {
            try { NormalizationProcess.EncodeRequest(configuration.RootElement, invalid, 1024); throw new Exception("Invalid bridge request accepted."); }
            catch (QueryFault) { count++; }
        }
        using (var large = JsonDocument.Parse("{\"value\":\"" + new string('x', 2000) + "\"}"))
        {
            try { NormalizationProcess.EncodeRequest(large.RootElement, request, 1024); throw new Exception("Oversized bridge request accepted."); }
            catch (QueryFault) { count++; }
        }
        var scan = await RunMode("valid");
        if (scan.ManagementGroupId != Scan.ManagementGroupId || !scan.MembershipVerified)
            throw new InvalidOperationException("Normalized subprocess output differs.");
        count++;
        if ((await RunMode("utf8")).GroupId != "測定 ü")
            throw new InvalidOperationException("UTF8 subprocess protocol was corrupted.");
        count++;
        await Reject("error", "SOURCE_BRIDGE_SCRIPT_FAILED");
        await Reject("stderr", "SOURCE_BRIDGE_SCRIPT_FAILED");
        await Reject("malformed", "SOURCE_BRIDGE_PROTOCOL_INVALID");
        await Reject("duplicate", "SOURCE_BRIDGE_PROTOCOL_INVALID");
        await Reject("version", "SOURCE_BRIDGE_PROTOCOL_INVALID");
        await Reject("unknown", "SOURCE_BRIDGE_PROTOCOL_INVALID");
        await Reject("missing", "SOURCE_BRIDGE_PROTOCOL_INVALID");
        await Reject("overflow", "SOURCE_BRIDGE_OUTPUT_BUDGET");
        await Reject("errorOverflow", "SOURCE_BRIDGE_OUTPUT_BUDGET");
        await Reject("stall", "SOURCE_BRIDGE_DEADLINE", TimeSpan.FromSeconds(1));
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            try { await RunMode("stall", token: cancel.Token); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) { count++; }
        }
        // Repeated successful start proves cancelled children/pipe tasks were
        // fully joined, rather than leaving work to a future collection attempt.
        await RunMode("valid");
        count++;
        if (OperatingSystem.IsWindows())
        {
            await CheckJobLifetime();
            count += 4;
        }
        Console.WriteLine($"PASS: {count} normalization process assertions; bounded pipes, strict protocol, failure, deadline and cancellation" +
            (OperatingSystem.IsWindows() ? "; Windows worker crash and descendant lifetime." : "; Windows crash checks skipped on non-Windows."));

        async Task Reject(string mode, string code, TimeSpan? deadline = null)
        {
            try { await RunMode(mode, deadline); }
            catch (QueryFault error) when (error.Message == code) { count++; return; }
            throw new InvalidOperationException("Bridge mode did not fail with safe code: " + mode);
        }
    }

    private static Task<NormalizedSourceScan> RunMode(string mode, TimeSpan? deadline = null, CancellationToken token = default)
    {
        return NormalizationProcess.RunAsync(StartInfo(mode), Encoding.UTF8.GetBytes(Input), 4096,
            deadline ?? TimeSpan.FromSeconds(15), token);
    }

    private static ProcessStartInfo StartInfo(string mode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath is { } path &&
            string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase) ? path : "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, "--normalization-child", mode })
            start.ArgumentList.Add(argument);
        return start;
    }

    public static int Child(string mode)
    {
        if (mode == "checks") { Run().GetAwaiter().GetResult(); return 0; }
        if (mode.StartsWith("worker:", StringComparison.Ordinal))
        {
            RunMode("collector:" + mode["worker:".Length..], TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            return 0;
        }
        if (mode.StartsWith("descendant:", StringComparison.Ordinal))
        {
            WritePid(Path.Combine(mode["descendant:".Length..], "descendant.pid"));
            Thread.Sleep(Timeout.Infinite);
        }
        Console.InputEncoding = new UTF8Encoding(false);
        var input = Console.In.ReadToEnd();
        if (input != Input) return 2;
        if (mode.StartsWith("collector:", StringComparison.Ordinal) || mode.StartsWith("complete-tree:", StringComparison.Ordinal))
        {
            var root = mode[(mode.IndexOf(':') + 1)..];
            using var exclusive = new FileStream(Path.Combine(root, "collector.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            WritePid(Path.Combine(root, "collector.pid"));
            using var descendant = Process.Start(StartInfo("descendant:" + root))!;
            descendant.StandardInput.Close();
            WaitForFile(Path.Combine(root, "descendant.pid")).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(root, "ready"), "ready");
            if (mode.StartsWith("collector:", StringComparison.Ordinal)) Thread.Sleep(Timeout.Infinite);
            Console.Write(Wire.Encode(new NormalizationBridgeResult(1, Scan)));
            return 0;
        }
        switch (mode)
        {
            case "valid": Console.Write(Wire.Encode(new NormalizationBridgeResult(1, Scan))); break;
            case "utf8":
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new NormalizationBridgeResult(1, Scan with { GroupId = "測定 ü" }),
                    new JsonSerializerOptions(Wire.Json) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                Console.OpenStandardOutput().Write(bytes);
                break;
            case "error": return 3;
            case "stderr":
                Console.Error.Write("synthetic private diagnostic");
                Console.Write(Wire.Encode(new NormalizationBridgeResult(1, Scan))); break;
            case "malformed": Console.Write("not-json"); break;
            case "duplicate": Console.Write("{\"protocolVersion\":1,\"protocolVersion\":1,\"scan\":{}}"); break;
            case "version": Console.Write(Wire.Encode(new NormalizationBridgeResult(2, Scan))); break;
            case "unknown": Console.Write("{\"protocolVersion\":1,\"scan\":{},\"unexpected\":true}"); break;
            case "missing": Console.Write("{\"protocolVersion\":1}"); break;
            case "overflow": Console.Write(new string('x', 65536)); break;
            case "errorOverflow": Console.Error.Write(new string('x', 131072)); break;
            case "stall": Thread.Sleep(Timeout.Infinite); break;
            default: return 4;
        }
        return 0;
    }

    private static async Task CheckJobLifetime()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".validation-data", "normalization lifetime ü " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Kill only the worker, not its process tree: managed finally cannot
            // run. Both collector and its descendant must die via the job.
            using (var worker = Process.Start(StartInfo("worker:" + root))!)
            {
                worker.StandardInput.Close();
                try
                {
                    await WaitForFile(Path.Combine(root, "ready"));
                    worker.Kill();
                    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    await AssertTreeGone(root);
                }
                finally
                {
                    if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); }
                }
            }
            // Reuse the same exclusive collector resource only after proving the
            // old processes exited. This is the replacement checkpoint worker.
            ClearMarkers(root);
            await RunMode("complete-tree:" + root);
            await AssertTreeGone(root);
            ClearMarkers(root);
            try { await RunMode("collector:" + root, TimeSpan.FromSeconds(2)); throw new Exception("Deadline ignored."); }
            catch (QueryFault error) when (error.Message == "SOURCE_BRIDGE_DEADLINE") { }
            await AssertTreeGone(root);
            ClearMarkers(root);
            using var cancel = new CancellationTokenSource();
            var collection = RunMode("collector:" + root, token: cancel.Token);
            try { await WaitForFile(Path.Combine(root, "ready")); }
            finally { cancel.Cancel(); }
            try { await collection; throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            await AssertTreeGone(root);
        }
        finally
        {
            // On a regression, don't leave synthetic processes running.
            foreach (var name in new[] { "collector.pid", "descendant.pid" })
                if (File.Exists(Path.Combine(root, name)) && int.TryParse(File.ReadAllText(Path.Combine(root, name)), out var pid))
                    try { using var process = Process.GetProcessById(pid); if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
                    catch (ArgumentException) { }
            Directory.Delete(root, true);
        }
    }

    private static async Task AssertTreeGone(string root)
    {
        foreach (var name in new[] { "collector.pid", "descendant.pid" })
        {
            await WaitForFile(Path.Combine(root, name));
            var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(root, name)));
            try
            {
                using var process = Process.GetProcessById(pid);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (!process.HasExited) throw new InvalidOperationException("Synthetic collector survived its worker.");
            }
            catch (ArgumentException) { /* An exited process may already be reaped. */ }
        }
        using var exclusive = new FileStream(Path.Combine(root, "collector.lock"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task WaitForFile(string path)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Synthetic process did not become ready.");
            await Task.Delay(20);
        }
    }

    private static void ClearMarkers(string root)
    {
        foreach (var name in new[] { "collector.pid", "descendant.pid", "ready" }) File.Delete(Path.Combine(root, name));
    }

    private static void WritePid(string path)
    {
        File.WriteAllText(path + ".writing", Environment.ProcessId.ToString());
        File.Move(path + ".writing", path, true);
    }
}
