using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ScomMcp;

static class NormalizationProcessChecks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
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
        Console.WriteLine($"PASS: {count} normalization process assertions; bounded pipes, strict protocol, failure, deadline and cancellation.");

        async Task Reject(string mode, string code, TimeSpan? deadline = null)
        {
            try { await RunMode(mode, deadline); }
            catch (QueryFault error) when (error.Message == code) { count++; return; }
            throw new InvalidOperationException("Bridge mode did not fail with safe code: " + mode);
        }
    }

    private static Task<NormalizedSourceScan> RunMode(string mode, TimeSpan? deadline = null, CancellationToken token = default)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, "--normalization-child", mode })
            start.ArgumentList.Add(argument);
        return NormalizationProcess.RunAsync(start, Encoding.UTF8.GetBytes("{\"protocolVersion\":1}"), 4096,
            deadline ?? TimeSpan.FromSeconds(15), token);
    }

    public static int Child(string mode)
    {
        var input = Console.In.ReadToEnd();
        if (input != "{\"protocolVersion\":1}") return 2;
        switch (mode)
        {
            case "valid": Console.Write(Wire.Encode(new NormalizationBridgeResult(1, Scan))); break;
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
}
