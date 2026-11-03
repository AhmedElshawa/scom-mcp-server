using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ScomMcp;

public static class SetupConfigurationValidation
{
    public static void ValidateJson(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Setup JSON path must be absolute.");
        using var document = Read(path);
    }

    // Validation deliberately does not build a host, open SQLite, load a private
    // key, acquire a token or invoke the source profile.
    public static bool Validate(string serverDirectory, string workerPath)
    {
        if (!Path.IsPathFullyQualified(serverDirectory) || !Path.IsPathFullyQualified(workerPath))
            throw new InvalidOperationException("Setup configuration paths must be absolute.");
        var receiverPath = Path.Combine(serverDirectory, "appsettings.json");
        using var receiverDocument = Read(receiverPath);
        using var workerDocument = Read(workerPath);
        var configuration = new ConfigurationBuilder().SetBasePath(serverDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Production.json", optional: true)
            .AddEnvironmentVariables().Build();
        var fileConfiguration = new ConfigurationBuilder().SetBasePath(serverDirectory)
            .AddJsonFile("appsettings.json", optional: false).Build();
        foreach (var section in new[] { "Auth", "Source", "ReadModel", "Lifecycle", "TargetedLive", "AllowedHosts" })
        {
            var expected = fileConfiguration.GetSection(section).AsEnumerable().Where(p => p.Value is not null)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var effective = configuration.GetSection(section).AsEnumerable().Where(p => p.Value is not null)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (expected.Count != effective.Count || expected.Any(p => !effective.TryGetValue(p.Key, out var value) || value != p.Value))
                throw new InvalidOperationException("Setup requires reviewed configuration without effective environment/Production overrides.");
        }
        var auth = configuration.GetSection("Auth").Get<AuthSettings>() ?? new();
        var source = configuration.GetSection("Source").Get<SourceSettings>() ?? new();
        var model = configuration.GetSection("ReadModel").Get<ReadModelSettings>() ?? new();
        var lifecycle = configuration.GetSection("Lifecycle").Get<LifecycleSettings>() ?? new();
        var live = configuration.GetSection("TargetedLive").Get<TargetedLiveSettings>() ?? new();
        Hosting.AddScomServices(new ServiceCollection(), auth, source, model, lifecycle, live);
        var worker = workerDocument.RootElement.Deserialize<CollectionWorkerHostSettings>(Wire.Json)
            ?? throw new InvalidOperationException("Worker configuration is empty.");
        worker.Identity.Validate();
        worker.Publisher.Validate();
        worker.Reconciliation.Validate();
        worker.Schedule.Validate();
        worker.LiveChecks.Validate();
        _ = new PowerShellNormalizationProfile(worker.Profile);
        var profile = worker.Profile.Configuration;
        var properties = profile.GetProperty("allowedPropertiesByClass").Deserialize<Dictionary<string, string[]>>(Wire.Json)
            ?? throw new InvalidOperationException("Reviewed property exposure is required.");
        if (!model.Enabled || source.SnapshotEnabled || live.Enabled || worker.LiveChecks.Enabled ||
            source.FreshMinutes != model.FreshMinutes || source.UnavailableMinutes != model.UnavailableMinutes ||
            model.ScopeKind != Text("scopeKind") || model.ManagementGroupId != Text("managementGroupId") ||
            model.GroupId != (profile.GetProperty("groupId").ValueKind == JsonValueKind.Null ? null : Text("groupId")) ||
            auth.TenantId != worker.Identity.TenantId || !auth.Audiences.Contains(worker.Identity.Resource) &&
            !auth.Audiences.Contains(TextClientAudience()) ||
            model.IngestionRole != "Scom.Ingest" || auth.RequiredScope != "Scom.Read" ||
            model.AllowedMetricsByClass.Count != 0 || profile.GetProperty("metricMappingsByClass").EnumerateObject().Any() ||
            properties.Count != model.AllowedPropertiesByClass.Count ||
            properties.Any(p => !model.AllowedPropertiesByClass.TryGetValue(p.Key, out var approved) ||
                p.Value is null || !p.Value.Order(StringComparer.Ordinal).SequenceEqual(approved.Order(StringComparer.Ordinal))) ||
            model.IncludeAlertDescriptions != profile.GetProperty("includeAlertDescriptions").GetBoolean() ||
            worker.MaxCheckpointBytes is < 1024 or > 64 * 1024 * 1024 ||
            !Path.IsPathFullyQualified(worker.CheckpointPath) ||
            string.Equals(Path.GetFullPath(worker.CheckpointPath), Path.GetFullPath(model.DatabasePath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Setup requires matching indexed scope/identity, separate storage and disabled metrics/live checks.");
        var ready = profile.GetProperty("scopeVisibilityVerified").GetBoolean() &&
            (model.ScopeKind == "group"
                ? profile.GetProperty("membershipQueryContractVerified").GetBoolean()
                : profile.GetProperty("wholeManagementGroupApproved").GetBoolean() &&
                  profile.GetProperty("enumerationQueryContractVerified").GetBoolean());
        if (model.WindowsToolsEnabled && profile.GetProperty("windowsMapping").ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Windows tools require a separately reviewed source mapping.");
        return ready;

        string Text(string name) => profile.GetProperty(name).GetString() ?? "";
        string TextClientAudience()
        {
            // The API audience is not the publisher client ID. Its value must
            // be supplied in receiver settings; api://GUID can identify v2 APIs.
            var resource = worker.Identity.Resource;
            return resource.StartsWith("api://", StringComparison.Ordinal) &&
                Guid.TryParse(resource[6..], out var id) ? id.ToString("D") : resource;
        }
    }

    private static JsonDocument Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 1024 * 1024)
            throw new InvalidOperationException("Setup configuration is missing or exceeds its byte budget.");
        using var reader = new BinaryReader(info.OpenRead());
        var bytes = reader.ReadBytes(1024 * 1024 + 1);
        if (bytes.Length > 1024 * 1024) throw new InvalidOperationException("Setup JSON exceeds its byte budget.");
        var document = JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0));
        try
        {
            SnapshotReader.RejectDuplicateProperties(document.RootElement);
            return document;
        }
        catch { document.Dispose(); throw; }
    }
}
