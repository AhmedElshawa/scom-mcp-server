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

internal sealed class BridgeFailureException : Exception
{
    public string Code { get; }
    public string? ContractField { get; }
    public string? ExpectedType { get; }
    public string? ActualType { get; }

    internal BridgeFailureException(string code, string? contractField = null, string? expectedType = null, string? actualType = null)
        : base(BridgeDiagnostics.SafeCode(code))
    {
        Code = BridgeDiagnostics.SafeCode(code);
        if (Code != "SETUP_GUI_INVALID_SCHEMA") return;
        ContractField = SchemaContract.SafeField(contractField);
        ExpectedType = SchemaContract.SafeType(expectedType);
        ActualType = SchemaContract.SafeType(actualType);
    }
}

internal static class SchemaContract
{
    private static readonly HashSet<string> Fields = BuildFields(typeof(BridgeResponse), "");
    private static readonly HashSet<string> Types = ["object", "array", "string", "boolean", "int32",
        "int32-or-numeric-string", "null", "number", "missing", "unknown", "safe-approval-summary", "false"];

    internal static string SafeField(string? field) => field is not null && Fields.Contains(field) ? field : "response";
    internal static string SafeType(string? type) => type is not null && Types.Contains(type) ? type : "unknown";
    private static string Path(string parent, string child) => parent.Length == 0 ? child : parent + "." + child;
    private static string PropertyName(System.Reflection.PropertyInfo property) =>
        char.ToLowerInvariant(property.Name[0]) + property.Name[1..];

    private static HashSet<string> BuildFields(Type type, string path)
    {
        var result = new HashSet<string> { path.Length == 0 ? "response" : path };
        if (type.IsArray)
        {
            foreach (var field in BuildFields(type.GetElementType()!, path + "[]")) result.Add(field);
        }
        else if (type != typeof(string) && type != typeof(bool) && type != typeof(int))
            foreach (var property in type.GetProperties())
                foreach (var field in BuildFields(property.PropertyType, Path(path, PropertyName(property)))) result.Add(field);
        return result;
    }

    internal static BridgeFailureException Failure(string field, string expected, string actual) =>
        new("SETUP_GUI_INVALID_SCHEMA", field, expected, actual);

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string",
        JsonValueKind.True or JsonValueKind.False => "boolean", JsonValueKind.Number => "number",
        JsonValueKind.Null => "null", _ => "unknown"
    };

    internal static void Validate(JsonElement root) => ValidateValue(root, typeof(BridgeResponse), "", false);

    private static void ValidateValue(JsonElement value, Type type, string path, bool arrayItem)
    {
        string field = path.Length == 0 ? "response" : path;
        bool numericString = path == "sites[].httpsBindings[].port";
        string expected = type == typeof(string) ? "string" : type == typeof(bool) ? "boolean" :
            type == typeof(int) ? numericString ? "int32-or-numeric-string" : "int32" : type.IsArray ? "array" : "object";
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!arrayItem && (type == typeof(string) || type.IsArray || type == typeof(SetupPlan))) return;
            throw Failure(field, expected, "null");
        }
        if (type == typeof(string))
        {
            if (value.ValueKind != JsonValueKind.String) throw Failure(field, expected, Kind(value));
            return;
        }
        if (type == typeof(bool))
        {
            if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw Failure(field, expected, Kind(value));
            return;
        }
        if (type == typeof(int))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _)) return;
            if (numericString && value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out _)) return;
            throw Failure(field, expected, Kind(value));
        }
        if (type.IsArray)
        {
            if (value.ValueKind != JsonValueKind.Array) throw Failure(field, expected, Kind(value));
            foreach (var item in value.EnumerateArray()) ValidateValue(item, type.GetElementType()!, path + "[]", true);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) throw Failure(field, expected, Kind(value));
        var properties = type.GetProperties().ToDictionary(PropertyName, StringComparer.OrdinalIgnoreCase);
        bool requiredFound = false;
        string? required = type == typeof(BridgeResponse) ? "ok" : type == typeof(BridgeCheck) ? "passed" : null;
        foreach (var member in value.EnumerateObject())
        {
            if (!properties.TryGetValue(member.Name, out var property)) continue;
            string canonicalName = PropertyName(property);
            if (canonicalName == required) requiredFound = true;
            ValidateValue(member.Value, property.PropertyType, Path(path, canonicalName), false);
        }
        if (required is not null && !requiredFound) throw Failure(Path(path, required), "boolean", "missing");
    }
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
        ["SETUP_GUI_AUTHORIZATION_BLOCKED"] = "PowerShell AuthorizationManager rejected the setup script before a usable response. Ask your administrator to review effective policy, downloaded-file zone/trust prompts, signatures and application-control policy for the bridge and modules. Unrestricted can still warn about Internet-zone files; signing is not always mandatory. The exact cause is not established. Do not bypass warnings or policy.",
        ["SETUP_GUI_CHILD_EXIT"] = "The bridge exited unsuccessfully without a usable error response. Run the approved read-only bridge diagnostic to inspect the failure privately.",
        ["SETUP_GUI_NO_RESPONSE"] = "The bridge returned no JSON response. Verify the complete reviewed setup files and run the approved read-only bridge diagnostic.",
        ["SETUP_GUI_INVALID_JSON"] = "The bridge returned invalid JSON. Verify matching GUI and setup files from the same package and run the approved read-only diagnostic.",
        ["SETUP_GUI_INVALID_SCHEMA"] = "A bridge response field has an unexpected JSON type or shape. Collect the safe contract field/type diagnostic; the cause is not yet established.",
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
        bool authorization = exitCode != 0 && HasAuthorizationFailure(stderr);
        if (oversized) throw new BridgeFailureException("SETUP_GUI_RESPONSE_TOO_LARGE");
        if (string.IsNullOrWhiteSpace(json))
            throw new BridgeFailureException(authorization ? "SETUP_GUI_AUTHORIZATION_BLOCKED" : policy ? "SETUP_GUI_POLICY_BLOCKED" : writeFailed ? "SETUP_GUI_IO_FAILED" :
                exitCode != 0 ? "SETUP_GUI_CHILD_EXIT" : "SETUP_GUI_NO_RESPONSE");
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { throw new BridgeFailureException(authorization ? "SETUP_GUI_AUTHORIZATION_BLOCKED" : policy ? "SETUP_GUI_POLICY_BLOCKED" : "SETUP_GUI_INVALID_JSON"); }
        using (document)
        {
            var root = document.RootElement;
            try { SchemaContract.Validate(root); }
            catch (BridgeFailureException) when (exitCode != 0 && HasAuthorizationFailure(stderr))
            {
                // A denied -File launch can echo stdin JSON without ever running the bridge.
                throw new BridgeFailureException("SETUP_GUI_AUTHORIZATION_BLOCKED");
            }
            catch (BridgeFailureException) when (exitCode != 0 && policy)
            {
                throw new BridgeFailureException("SETUP_GUI_POLICY_BLOCKED");
            }

            BridgeResponse response;
            try { response = root.Deserialize<BridgeResponse>(Json) ?? throw new JsonException(); }
            catch (Exception error) when (error is JsonException or NotSupportedException)
            { throw new BridgeFailureException("SETUP_GUI_INVALID_SCHEMA"); }
            if (response.Ok && response.ApprovalRequired)
                throw SchemaContract.Failure("ok", "false", "boolean");
            if (response.ApprovalRequired && !DisplayText.IsExactSummarySafe(response.Summary))
                throw SchemaContract.Failure("summary", "safe-approval-summary", response.Summary is null ? "null" : "string");
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

    internal static bool HasAuthorizationFailure(string stderr) =>
        stderr.Contains("AuthorizationManager check failed", StringComparison.OrdinalIgnoreCase) &&
        System.Text.RegularExpressions.Regex.IsMatch(stderr, @"\bSecurityError\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
        System.Text.RegularExpressions.Regex.IsMatch(stderr, @"\bUnauthorizedAccess\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
