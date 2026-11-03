using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ScomMcp;

static class SetupConfigurationChecks
{
    public static void Run(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        var repo = Path.GetFullPath(Path.Combine(root, "..", ".."));
        var directory = Path.Combine(root, "setup-configuration");
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "never-execute.ps1");
        File.WriteAllText(script, "throw 'SETUP_VALIDATOR_MUST_NOT_RUN_SOURCE'");
        var receiverPath = Path.Combine(directory, "appsettings.json");
        var workerPath = Path.Combine(directory, "worker.json");
        var receiver = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "ScomMcp", "appsettings.ReadModel.example.json")))!;
        var worker = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "ScomMcp", "appsettings.Worker.example.json")))!;
        receiver["Auth"]!["Audiences"] = new JsonArray("api://synthetic-scom-ingestion");
        receiver["ReadModel"]!["DatabasePath"] = Path.Combine(directory, "receiver", "read-model.db");
        worker["checkpointPath"] = Path.Combine(directory, "worker", "worker.db");
        worker["identity"]!["certificateThumbprint"] = new string('A', 40);
        worker["profile"]!["scriptPath"] = script;
        worker["profile"]!["scriptSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(script)));
        worker["profile"]!["executablePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var count = 0;
        Save();
        Check(!SetupConfigurationValidation.Validate(directory, workerPath));
        worker["profile"]!["configuration"]!["scopeVisibilityVerified"] = true;
        worker["profile"]!["configuration"]!["membershipQueryContractVerified"] = true;
        Save();
        Check(SetupConfigurationValidation.Validate(directory, workerPath));
        Reject(() => receiver["TargetedLive"]!["Enabled"] = true);
        Reject(() => worker["liveChecks"]!["enabled"] = true);
        Reject(() => receiver["Source"]!["SnapshotEnabled"] = true);
        Reject(() => receiver["ReadModel"]!["GroupId"] = "44444444-4444-4444-8444-444444444444");
        Reject(() => worker["checkpointPath"] = receiver["ReadModel"]!["DatabasePath"]!.GetValue<string>());
        Reject(() => receiver["ReadModel"]!["AllowedMetricsByClass"] = JsonNode.Parse("""{"Example.Class":{"cpu":"percent"}}"""));
        Reject(() => worker["profile"]!["configuration"]!["metricMappingsByClass"] = JsonNode.Parse("""{"Example.Class":{}}"""));
        Reject(() => receiver["ReadModel"]!["WindowsToolsEnabled"] = true);
        Reject(() => receiver["ReadModel"]!["AllowedPropertiesByClass"] = JsonNode.Parse("""{"Example.Class":["Name"]}"""));
        Reject(() => worker["maxCheckpointBytes"] = 0);
        Reject(() => worker["profile"]!["scriptSha256"] = new string('0', 64));
        var production = Path.Combine(directory, "appsettings.Production.json");
        File.WriteAllText(production, """{"Auth":{"Callers":[{"ObjectId":"44444444-4444-4444-8444-444444444444"}]}}""");
        ExpectReject();
        File.Delete(production);
        var prior = Environment.GetEnvironmentVariable("ReadModel__GroupId");
        try
        {
            Environment.SetEnvironmentVariable("ReadModel__GroupId", "44444444-4444-4444-8444-444444444444");
            ExpectReject();
        }
        finally { Environment.SetEnvironmentVariable("ReadModel__GroupId", prior); }
        File.WriteAllText(workerPath, """{"identity":{},"identity":{}}""");
        ExpectReject();
        Save();
        Check(!Directory.Exists(Path.Combine(directory, "receiver")) && !Directory.Exists(Path.Combine(directory, "worker")));
        Console.WriteLine($"PASS: {count} setup configuration assertions; no source, host, certificate or SQLite activation.");

        void Save()
        {
            File.WriteAllText(receiverPath, receiver.ToJsonString());
            File.WriteAllText(workerPath, worker.ToJsonString());
        }
        void Check(bool value)
        {
            if (!value) throw new InvalidOperationException("Setup configuration assertion failed.");
            count++;
        }
        void ExpectReject()
        {
            try { SetupConfigurationValidation.Validate(directory, workerPath); }
            catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException or QueryFault)
            {
                count++;
                return;
            }
            throw new InvalidOperationException("Unsafe setup configuration was accepted.");
        }
        void Reject(Action change)
        {
            var savedReceiver = receiver.DeepClone();
            var savedWorker = worker.DeepClone();
            change();
            Save();
            ExpectReject();
            receiver = savedReceiver;
            worker = savedWorker;
            Save();
        }
    }
}
