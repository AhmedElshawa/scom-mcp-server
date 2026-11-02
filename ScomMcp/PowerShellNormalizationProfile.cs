using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScomMcp;

public sealed class PowerShellProfileSettings
{
    public string ExecutablePath { get; set; } = "";
    public string ScriptPath { get; set; } = "";
    public string ScriptSha256 { get; set; } = "";
    public JsonElement Configuration { get; set; }
    public int MaxInputBytes { get; set; } = 64 * 1024;
    public int MaxOutputBytes { get; set; } = 16 * 1024 * 1024;
    public int DeadlineSeconds { get; set; } = 900;
}

public sealed record NormalizationBridgeResult([property: JsonRequired] int ProtocolVersion,
    [property: JsonRequired] NormalizedSourceScan Scan);

// Only the explicitly installed and hash-pinned script runs on the SDK host.
// stdin/stdout carry operational data; neither is an application diagnostic log.
public sealed class PowerShellNormalizationProfile : IScomNormalizationProfile
{
    private readonly string executable;
    private readonly string script;
    private readonly byte[] scriptHash;
    private readonly JsonElement configuration;
    private readonly int inputLimit;
    private readonly int outputLimit;
    private readonly TimeSpan deadline;
    private int running;

    public PowerShellNormalizationProfile(PowerShellProfileSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The Desktop PowerShell SDK bridge requires Windows.");
        if (!Path.IsPathFullyQualified(settings.ExecutablePath) || !Path.IsPathFullyQualified(settings.ScriptPath) ||
            settings.ScriptSha256 is null || settings.ScriptSha256.Length != 64 ||
            settings.ScriptSha256.Any(c => !char.IsAsciiHexDigit(c)) ||
            settings.MaxInputBytes is < 1024 or > 1024 * 1024 ||
            settings.MaxOutputBytes is < 1024 or > 32 * 1024 * 1024 ||
            settings.DeadlineSeconds is < 1 or > 7200 ||
            settings.Configuration.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("SDK bridge requires installed paths, script SHA256 and bounded configuration.");
        executable = Path.GetFullPath(settings.ExecutablePath);
        script = Path.GetFullPath(settings.ScriptPath);
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!string.Equals(executable, desktop, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(script), ".ps1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SDK bridge requires the system Desktop PowerShell and an installed .ps1 profile.");
        ValidateLocalFile(executable);
        ValidateLocalFile(script);
        scriptHash = Convert.FromHexString(settings.ScriptSha256);
        VerifyScript();
        SnapshotReader.RejectDuplicateProperties(settings.Configuration);
        var configurationBytes = JsonSerializer.SerializeToUtf8Bytes(settings.Configuration, Wire.Json);
        if (configurationBytes.Length > settings.MaxInputBytes)
            throw new InvalidOperationException("SDK bridge configuration exceeds its input byte budget.");
        configuration = settings.Configuration.Clone();
        inputLimit = settings.MaxInputBytes;
        outputLimit = settings.MaxOutputBytes;
        deadline = TimeSpan.FromSeconds(settings.DeadlineSeconds);
    }

    public async Task<NormalizedSourceScan> CollectAsync(SourceCollectionRequest request, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("SDK bridge collection is already running.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = NormalizationProcess.EncodeRequest(configuration, request, inputLimit);
            VerifyScript();
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script })
                start.ArgumentList.Add(argument);
            return await NormalizationProcess.RunAsync(start, input, outputLimit, deadline, cancellationToken);
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private void VerifyScript()
    {
        ValidateLocalFile(script);
        using var file = File.OpenRead(script);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Security.Cryptography.SHA256.HashData(file), scriptHash))
            throw new InvalidOperationException("SDK bridge installed script differs from the reviewed SHA256.");
    }

    private static void ValidateLocalFile(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(path) ||
            new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            throw new InvalidOperationException("SDK bridge files must be installed on local storage.");
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("SDK bridge files must not be reparse points.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
            if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("SDK bridge paths must not traverse reparse points.");
    }
}

public static class NormalizationProcess
{
    public static byte[] EncodeRequest(JsonElement configuration, SourceCollectionRequest request, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (configuration.ValueKind != JsonValueKind.Object || maxBytes is < 1024 or > 1024 * 1024 ||
            request.RequestedAtUtc == default || request.RequestedAtUtc.Offset != TimeSpan.Zero ||
            request.MetricHistoryStartUtc != request.RequestedAtUtc.AddDays(-14) ||
            request.PriorUnresolvedAlerts.IsDefault || request.PriorUnresolvedAlerts.Length > 100000 ||
            request.PriorUnresolvedAlerts.Any(a => a is null || !ReadModelSettings.ValidId(a.AlertId) ||
                !ReadModelSettings.ValidId(a.ObjectId)) ||
            request.PriorUnresolvedAlerts.Select(a => a.AlertId).Distinct(StringComparer.Ordinal).Count() !=
                request.PriorUnresolvedAlerts.Length)
            throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_BRIDGE_REQUEST_INVALID");
        SnapshotReader.RejectDuplicateProperties(configuration);
        // Bound string sizes before serialization; prior-alert pairs have fixed
        // GUID lengths and are independently capped by the source checkpoint.
        if (request.PriorUnresolvedAlerts.Length > maxBytes / 72)
            throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_BRIDGE_INPUT_BUDGET");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, configuration, request }, Wire.Json);
        if (bytes.Length > maxBytes)
            throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_BRIDGE_INPUT_BUDGET");
        return bytes;
    }

    public static async Task<NormalizedSourceScan> RunAsync(ProcessStartInfo start, byte[] input, int maxOutputBytes,
        TimeSpan deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(input);
        if (start.UseShellExecute || !start.RedirectStandardInput || !start.RedirectStandardOutput ||
            !start.RedirectStandardError || input.Length > 1024 * 1024 ||
            maxOutputBytes is < 1024 or > 32 * 1024 * 1024 ||
            deadline < TimeSpan.FromSeconds(1) || deadline > TimeSpan.FromHours(2))
            throw new InvalidOperationException("Normalization process requires redirected streams and bounded execution.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_BRIDGE_START_FAILED");
        var stdout = ReadBounded(process.StandardOutput.BaseStream, maxOutputBytes, timeout.Token);
        // Never surface script error text, which could contain customer data.
        var stderr = ReadBounded(process.StandardError.BaseStream, 64 * 1024, timeout.Token);
        var write = WriteInput();
        var exit = process.WaitForExitAsync(timeout.Token);
        var tasks = new List<Task> { stdout, stderr, write, exit };
        try
        {
            while (tasks.Count != 0)
            {
                var completed = await Task.WhenAny(tasks);
                await completed;
                tasks.Remove(completed);
            }
            if (process.ExitCode != 0 || stderr.Result.Length != 0)
                throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_BRIDGE_SCRIPT_FAILED");
            try
            {
                using var document = JsonDocument.Parse(stdout.Result);
                SnapshotReader.RejectDuplicateProperties(document.RootElement);
                var result = document.RootElement.Deserialize<NormalizationBridgeResult>(Wire.Json);
                if (result is null || result.ProtocolVersion != 1 || result.Scan is null)
                    throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_BRIDGE_PROTOCOL_INVALID");
                return result.Scan;
            }
            catch (JsonException)
            {
                throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_BRIDGE_PROTOCOL_INVALID");
            }
            catch (QueryFault error) when (error.Message == "DUPLICATE_JSON_PROPERTY")
            {
                throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_BRIDGE_PROTOCOL_INVALID");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_BRIDGE_DEADLINE");
        }
        finally
        {
            timeout.Cancel();
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync();
            // Observe stream failures after terminating the tracked child. Do not
            // abandon a pipe task or permit a second SDK process to overlap it.
            foreach (var task in new Task[] { stdout, stderr, write, exit })
                if (!task.IsCompletedSuccessfully)
                    try { await task; }
                    catch (Exception error) when (error is OperationCanceledException or IOException or QueryFault) { }
        }

        async Task WriteInput()
        {
            await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
            process.StandardInput.Close();
        }
    }

    private static async Task<byte[]> ReadBounded(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > limit)
                throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_BRIDGE_OUTPUT_BUDGET");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
