using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;

namespace ScomSetup;

internal sealed class SetupPlan
{
    public int SchemaVersion { get; set; } = 1;
    public string PackagePath { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public string PrivatePath { get; set; } = "";
    public string ReceiverStatePath { get; set; } = "";
    public string WorkerStatePath { get; set; } = "";
    public string McpLogPath { get; set; } = "";
    public string CollectorLogPath { get; set; } = "";
    public string SiteName { get; set; } = "";
    public string ApplicationPath { get; set; } = "/";
    public string PoolName { get; set; } = "";
    public string HttpsBaseUrl { get; set; } = "";
    public string TlsThumbprint { get; set; } = "";
    public string WorkerAccount { get; set; } = "";
    public string ManagementServer { get; set; } = "";
    public string ManagementGroupId { get; set; } = "";
    public string OperationsManagerModule { get; set; } = "";
    public string ScopeKind { get; set; } = "group";
    public string? GroupId { get; set; }
    public bool WholeManagementGroupApproved { get; set; }
}

internal sealed class BridgeRequest
{
    public string Action { get; set; } = "";
    public SetupPlan Plan { get; set; } = new();
    public string StateDirectory { get; set; } = "";
    public string IdentityPath { get; set; } = "";
    public string ReceiverSettingsPath { get; set; } = "";
    public string WorkerSettingsPath { get; set; } = "";
    public string PlanPath { get; set; } = "";
    public string? Selection { get; set; }
    public string? Approval { get; set; }
    public bool InstallIis { get; set; }
    public string HostingBundlePath { get; set; } = "";
    public string HostingBundleVersion { get; set; } = "";
    public string HostingBundleSha256 { get; set; } = "";
    public bool AcknowledgeRestart { get; set; }
}

internal sealed class BridgeResponse
{
    public bool Ok { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public BridgeCheck[] Checks { get; set; } = [];
    public GroupItem[] Groups { get; set; } = [];
    public string? Stage { get; set; }
    public string? PendingAction { get; set; }
    public bool ApprovalRequired { get; set; }
    public string? Summary { get; set; }
    public SetupPlan? Plan { get; set; }
    public SetupPlan? Defaults { get; set; }
    public string? StateDirectory { get; set; }
    public SiteOption[] Sites { get; set; } = [];
    public string[] Pools { get; set; } = [];
    public CertificateOption[] Certificates { get; set; } = [];
    public string? CurrentAccount { get; set; }
    public string? ManagementGroupId { get; set; }
}
internal sealed class BridgeCheck
{
    public string? Name { get; set; }
    public bool Passed { get; set; }
    public string? Remedy { get; set; }
}
internal sealed class SiteOption
{
    public string Name { get; set; } = "";
    public string[] Applications { get; set; } = [];
    public HttpsBinding[] HttpsBindings { get; set; } = [];
    public override string ToString() => DisplayText.Clean(Name, 120);
}
internal sealed class HttpsBinding
{
    public string Host { get; set; } = "";
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Port { get; set; }
}
internal sealed class CertificateOption
{
    public string Thumbprint { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string[] DnsNames { get; set; } = [];
    public override string ToString() => $"{DisplayText.Clean(DisplayName, 100)} ({DisplayText.Clean(Thumbprint, 40)})";
}
internal sealed class GroupItem
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public override string ToString() => $"{DisplayText.Clean(DisplayName, 120)} ({Id})";
}

internal static class DisplayText
{
    public static string Clean(string? value, int limit) =>
        new((value ?? "").Where(c => !char.IsControl(c) &&
            char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).Take(limit).ToArray());

    // Approval is never truncated or rewritten: unsafe text is rejected, not silently approved.
    public static bool IsExactSummarySafe(string? text) => !string.IsNullOrWhiteSpace(text) &&
        text.Length <= 32000 && text.All(c => (!char.IsControl(c) || c is '\r' or '\n' or '\t') &&
        char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format);
}

internal interface ISetupBridge
{
    Task<BridgeResponse> InvokeAsync(BridgeRequest request);
}

internal sealed class BridgeFailureException(string code) : Exception(BridgeDiagnostics.SafeCode(code))
{
    public string Code { get; } = BridgeDiagnostics.SafeCode(code);
}

internal static class BridgeDiagnostics
{
    private static readonly Dictionary<string, string> Failures = new()
    {
        ["SETUP_GUI_BRIDGE_MISSING"] = "The setup bridge is missing. Re-extract the complete reviewed package with its setup folder beside ScomSetup.exe.",
        ["SETUP_GUI_POWERSHELL_MISSING"] = "Windows PowerShell 5.1 was not found in Windows System32. Enable or repair the supported Windows PowerShell component.",
        ["SETUP_GUI_START_DENIED"] = "Windows denied launching the setup bridge. Ask the administrator to inspect application-control and process-launch policy. Execution-policy blocking has not been established.",
        ["SETUP_GUI_START_FAILED"] = "Windows PowerShell could not start. Check the Windows PowerShell component and approved application-control configuration.",
        ["SETUP_GUI_POLICY_BLOCKED"] = "PowerShell reported an execution-policy/security block. Review the script signature, zone marking and effective customer execution policy with your administrator. Do not bypass policy.",
        ["SETUP_GUI_CHILD_EXIT"] = "The bridge exited unsuccessfully without a usable error response. Run the approved read-only bridge diagnostic to inspect the failure privately.",
        ["SETUP_GUI_NO_RESPONSE"] = "The bridge returned no JSON response. Verify the complete reviewed setup files and run the approved read-only bridge diagnostic.",
        ["SETUP_GUI_INVALID_JSON"] = "The bridge returned invalid JSON. Verify matching GUI and setup files from the same package and run the approved read-only diagnostic.",
        ["SETUP_GUI_INVALID_SCHEMA"] = "The bridge response does not match the GUI contract. Use matching GUI and setup files from the same reviewed package.",
        ["SETUP_GUI_RESPONSE_TOO_LARGE"] = "The bridge response exceeded its safe limit. Run the approved read-only diagnostic privately; no response content was logged.",
        ["SETUP_GUI_IO_FAILED"] = "Bridge input/output failed. Accepted effects may still have completed. Recheck status explicitly before retrying an effect.",
        ["SETUP_GUI_UNKNOWN_FAILURE"] = "Setup communication failed for an unclassified reason. Run the approved read-only bridge diagnostic; no raw details were logged.",
        ["SETUP_GUI_BRIDGE_BLOCKED"] = "The bridge reported a startup failure. Check the complete setup files and run the approved read-only bridge diagnostic. A signing-policy cause has not been established.",
        ["SETUP_GUI_REQUEST_INVALID"] = "The bridge could not decode its request. Use matching GUI and setup files from the same package and run the read-only diagnostic.",
        ["SETUP_GUI_MODULE_LOAD_FAILED"] = "The bridge could not load its setup module. Verify all setup modules are present and ask the administrator to inspect effective policy and module requirements. A signing cause is not established by this code alone.",
        ["SETUP_GUI_DISPATCH_FAILED"] = "The bridge failed while dispatching the read-only request. Run the approved read-only diagnostic and privately inspect the matching setup modules.",
        ["SETUP_GUI_RESPONSE_SERIALIZATION_FAILED"] = "The bridge could not serialize its response. Use matching reviewed setup modules and collect the read-only diagnostic.",
        ["SETUP_GUI_FAILED"] = "The setup action reported an unclassified backend failure. Run the read-only diagnostic; for an accepted effect, inspect its journal before retrying.",
        ["SETUP_GUI_BOOTSTRAP_FAILED"] = "Local bootstrap probes failed before returning checks. Run the approved read-only diagnostic and inspect Windows component availability privately.",
        ["SETUP_GUI_BOOTSTRAP_PROBE_FAILED"] = "Local bootstrap probes failed before returning checks. Run the read-only diagnostic; no missing prerequisite has been established.",
        ["SETUP_GUI_PREFLIGHT_PROBE_FAILED"] = "Selected-plan local probes could not complete. Run the read-only diagnostic and privately review component access; do not install based on this code alone.",
        ["SETUP_PREREQUISITES_BLOCKED"] = "Review the actual prerequisite checks and remedies on the first screen.",
        ["SETUP_GUI_BOOTSTRAP_READY"] = "Local bootstrap completed.",
        ["SETUP_GUI_PREFLIGHT_PASSED"] = "Selected-plan prerequisite checks completed.",
        ["SETUP_GUI_APPROVAL_REQUIRED"] = "An exact effect approval is required.",
        ["SETUP_GUI_WORKER_PLAN_READY"] = "Installed worker plan loaded; host journal was not read.",
        ["SETUP_GUI_STATUS"] = "Explicit status read completed.",
        ["SETUP_GUI_JOURNAL_READY"] = "Private journal is ready; explicitly rerun prerequisite installation for the next effect.",
        ["SETUP_GUI_WORKER_ACTION_COMPLETE"] = "Explicit worker action completed.",
        ["SETUP_EXACT_WORKER_ACCOUNT_REQUIRED_NO_IMPERSONATION"] = "Use an approved session signed in as the exact selected worker. No impersonation is offered.",
        ["SETUP_ADMINISTRATOR_REQUIRED_FOR_HOST_LANE"] = "Use the explicit administrator restart for host actions."
    };

    internal static string SafeCode(string? code) => code is not null && Failures.ContainsKey(code) ? code : "SETUP_GUI_UNKNOWN_FAILURE";
    internal static string Remedy(string? code) => Failures[SafeCode(code)];
    internal static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScomSetup", "Logs", "bootstrap.jsonl");
    internal static string Record(string? code) => JsonSerializer.Serialize(new { code = SafeCode(code) }) + "\n";

    internal static bool TryLog(string? code)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // Only fixed codes, with a bounded file. Never include requests, paths, raw responses or stderr.
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= 65536) File.WriteAllText(LogPath, "");
            File.AppendAllText(LogPath, Record(code), new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }
}

internal sealed class SetupBridge(string packageRoot) : ISetupBridge
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    internal static string Encode(BridgeRequest request) => JsonSerializer.Serialize(request, Json) + "\n";

    internal static ProcessStartInfo StartInfo(string root)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var info = new ProcessStartInfo(Path.Combine(windows, @"System32\WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = root
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(Path.Combine(root, @"setup\Invoke-ScomSetupGui.ps1"));
        // Do not inherit application configuration, PS module paths or process execution-policy overrides.
        string[] retained = ["SystemRoot", "WINDIR", "SystemDrive", "COMSPEC", "TEMP", "TMP",
            "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles",
            "ProgramFiles(x86)", "ProgramW6432", "USERNAME", "USERDOMAIN", "HOMEDRIVE", "HOMEPATH"];
        info.Environment.Clear();
        foreach (var name in retained)
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        info.Environment["PATH"] = Path.Combine(windows, "System32") + ";" + windows + ";" +
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        return info;
    }

    public async Task<BridgeResponse> InvokeAsync(BridgeRequest request)
    {
        if (!File.Exists(Path.Combine(packageRoot, @"setup\Invoke-ScomSetupGui.ps1")))
            throw new BridgeFailureException("SETUP_GUI_BRIDGE_MISSING");
        using var process = new Process { StartInfo = StartInfo(packageRoot) };
        if (!File.Exists(process.StartInfo.FileName)) throw new BridgeFailureException("SETUP_GUI_POWERSHELL_MISSING");
        try { process.Start(); }
        catch (Win32Exception error)
        {
            throw new BridgeFailureException(error.NativeErrorCode == 5 ? "SETUP_GUI_START_DENIED" : "SETUP_GUI_START_FAILED");
        }
        catch { throw new BridgeFailureException("SETUP_GUI_START_FAILED"); }
        var output = ReadBoundedAsync(process.StandardOutput, 2 * 1024 * 1024);
        var errors = ReadBoundedAsync(process.StandardError, 16384);
        bool writeFailed = false;
        // Once started, never terminate a bridge: journaled effects may already be in progress.
        try
        {
            await process.StandardInput.WriteAsync(Encode(request));
        }
        catch { writeFailed = true; }
        finally
        {
            // Even a broken stdin must not make an accepted mutation look idle.
            try { process.StandardInput.Close(); } catch { writeFailed = true; }
            try { await process.WaitForExitAsync(); }
            catch { throw new BridgeFailureException("SETUP_GUI_IO_FAILED"); }
        }
        CapturedText stdout;
        CapturedText stderr;
        try { stdout = await output; stderr = await errors; }
        catch { throw new BridgeFailureException("SETUP_GUI_IO_FAILED"); }
        // A reliable structured error survives an early stdin close/nonzero exit.
        return Decode(stdout.Text, stderr.Text, process.ExitCode, stdout.Oversized, writeFailed);
    }

    internal static BridgeResponse Decode(string json, string stderr, int exitCode, bool oversized = false, bool writeFailed = false)
    {
        bool policy = HasPolicyFailure(stderr);
        if (oversized) throw new BridgeFailureException("SETUP_GUI_RESPONSE_TOO_LARGE");
        if (string.IsNullOrWhiteSpace(json))
            throw new BridgeFailureException(policy ? "SETUP_GUI_POLICY_BLOCKED" : writeFailed ? "SETUP_GUI_IO_FAILED" :
                exitCode != 0 ? "SETUP_GUI_CHILD_EXIT" : "SETUP_GUI_NO_RESPONSE");
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { throw new BridgeFailureException(policy ? "SETUP_GUI_POLICY_BLOCKED" : "SETUP_GUI_INVALID_JSON"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
                (root.TryGetProperty("approvalRequired", out var approval) &&
                    approval.ValueKind is not JsonValueKind.True and not JsonValueKind.False))
                throw new BridgeFailureException("SETUP_GUI_INVALID_SCHEMA");
            if (root.TryGetProperty("checks", out var checks) && checks.ValueKind != JsonValueKind.Null)
            {
                if (checks.ValueKind != JsonValueKind.Array) throw new BridgeFailureException("SETUP_GUI_INVALID_SCHEMA");
                foreach (var check in checks.EnumerateArray())
                    if (check.ValueKind != JsonValueKind.Object || !check.TryGetProperty("passed", out var passed) ||
                        passed.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                        throw new BridgeFailureException("SETUP_GUI_INVALID_SCHEMA");
            }

            BridgeResponse response;
            try { response = root.Deserialize<BridgeResponse>(Json) ?? throw new JsonException(); }
            catch (Exception error) when (error is JsonException or NotSupportedException)
            { throw new BridgeFailureException("SETUP_GUI_INVALID_SCHEMA"); }
            if (response.Ok && response.ApprovalRequired ||
                response.ApprovalRequired && !DisplayText.IsExactSummarySafe(response.Summary) ||
                (response.Checks ?? []).Any(check => check is null) ||
                (response.Sites ?? []).Any(site => site is null) ||
                (response.Groups ?? []).Any(group => group is null) ||
                (response.Certificates ?? []).Any(certificate => certificate is null))
                throw new BridgeFailureException("SETUP_GUI_INVALID_SCHEMA");
            if (exitCode != 0 && (response.Ok || response.ApprovalRequired))
                throw new BridgeFailureException("SETUP_GUI_CHILD_EXIT");
            if (writeFailed && response.Ok) throw new BridgeFailureException("SETUP_GUI_IO_FAILED");
            return response;
        }
    }

    internal static bool HasPolicyFailure(string stderr)
    {
        static bool Token(string text, string value) =>
            System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![A-Za-z0-9_])" +
                System.Text.RegularExpressions.Regex.Escape(value) + @"(?![A-Za-z0-9_])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return Token(stderr, "PSSecurityException") ||
            Token(stderr, "SecurityError") && Token(stderr, "UnauthorizedAccess") &&
            (stderr.Contains("not digitally signed", StringComparison.OrdinalIgnoreCase) ||
             stderr.Contains("running scripts is disabled", StringComparison.OrdinalIgnoreCase) ||
             Token(stderr, "about_Execution_Policies"));
    }

    private sealed record CapturedText(string Text, bool Oversized);
    private static async Task<CapturedText> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        bool oversized = false;
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            int remaining = limit - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining) oversized = true;
        }
        return new CapturedText(builder.ToString(), oversized);
    }
}
