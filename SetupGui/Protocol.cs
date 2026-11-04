using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
            throw new InvalidOperationException("BRIDGE_UNAVAILABLE");
        using var process = new Process { StartInfo = StartInfo(packageRoot) };
        process.Start();
        var output = ReadBoundedAsync(process.StandardOutput, 2 * 1024 * 1024);
        var errors = ReadBoundedAsync(process.StandardError, 0);
        // Once started, never terminate a bridge: journaled effects may already be in progress.
        try
        {
            await process.StandardInput.WriteAsync(Encode(request));
        }
        finally
        {
            // Even a broken stdin must not make an accepted mutation look idle.
            try { process.StandardInput.Close(); }
            finally { await process.WaitForExitAsync(); }
        }
        var json = await output;
        await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException("BRIDGE_FAILED");
        return JsonSerializer.Deserialize<BridgeResponse>(json, Json) ??
            throw new InvalidOperationException("BRIDGE_INVALID_RESPONSE");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        bool oversized = false;
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (limit == 0) continue;
            if (builder.Length + read <= limit) builder.Append(buffer, 0, read);
            else oversized = true;
        }
        if (oversized) throw new InvalidOperationException("BRIDGE_INVALID_RESPONSE");
        return builder.ToString();
    }
}
