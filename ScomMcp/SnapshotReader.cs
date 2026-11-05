using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScomMcp;

public sealed class SourceSettings
{
    public bool SnapshotEnabled { get; set; } = true;
    public string Directory { get; set; } = "";
    public string[] AllowedHostnames { get; set; } = [];
    public int MaxSnapshotBytes { get; set; } = 8 * 1024 * 1024;
    public int FreshMinutes { get; set; } = 15;
    public int UnavailableMinutes { get; set; } = 60;
    public int CursorLifetimeSeconds { get; set; } = 300;
    public void Validate()
    {
        if (SnapshotEnabled && (!Path.IsPathFullyQualified(Directory) ||
            AllowedHostnames.Length != 2 ||
            AllowedHostnames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 ||
            AllowedHostnames.Any(h => string.IsNullOrWhiteSpace(h) || h.Contains('*') || h.Any(char.IsWhiteSpace))) ||
            MaxSnapshotBytes < 1024 || MaxSnapshotBytes > 64 * 1024 * 1024 ||
            FreshMinutes < 1 || UnavailableMinutes <= FreshMinutes || UnavailableMinutes > 1440 ||
            CursorLifetimeSeconds < 30 || CursorLifetimeSeconds > 900)
            throw new InvalidOperationException("Source configuration is missing or invalid. Refusing to start.");
    }
}

public interface ISnapshotReader
{
    Task<Dataset> ReadAsync(CancellationToken ct = default);
}

public sealed class SnapshotReader(SourceSettings settings, TimeProvider clock) : ISnapshotReader
{
    public async Task<Dataset> ReadAsync(CancellationToken ct = default)
    {
        Guard.Source(settings.SnapshotEnabled, "LEGACY_SNAPSHOT_DISABLED");
        try
        {
            var statusPath = Path.Combine(settings.Directory, "collector-status.json");
            var before = await ReadBoundedAsync(statusPath, 16384, ct);
            var status = JsonSerializer.Deserialize<CollectorStatus>(before, Wire.Json);
            Guard.Source(status is { Status: "ok", ErrorCode: null, DatasetId: not null },
                "LATEST_COLLECTION_FAILED_OR_MISSING");
            var bytes = await ReadBoundedAsync(Path.Combine(settings.Directory, "snapshot.json"),
                settings.MaxSnapshotBytes, ct);
            var after = await ReadBoundedAsync(statusPath, 16384, ct);
            Guard.Source(before.AsSpan().SequenceEqual(after), "COLLECTION_CHANGED_DURING_READ");
            return Validate(bytes, status!, settings, clock.GetUtcNow());
        }
        catch (QueryFault) { throw; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new QueryFault("SOURCE_UNAVAILABLE", "SNAPSHOT_OR_STATUS_INVALID_OR_UNREADABLE");
        }
    }

    public static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Guard.Source(stream.Length <= limit, "SOURCE_SIZE_LIMIT");
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(block, ct)) != 0)
        {
            Guard.Source(buffer.Length + count <= limit, "SOURCE_SIZE_LIMIT");
            buffer.Write(block, 0, count);
        }
        return buffer.ToArray();
    }

    public static Dataset Validate(byte[] bytes, CollectorStatus status, SourceSettings settings, DateTimeOffset now)
        => ValidateCore(bytes, status, settings, now, null);

    internal static Dataset ValidateIndexed(byte[] bytes, CollectorStatus status, SourceSettings settings,
        DateTimeOffset now, IReadOnlySet<string> expectedComputerIds)
        => ValidateCore(bytes, status, settings, now, expectedComputerIds) with { StorageMode = "indexed" };

    private static Dataset ValidateCore(byte[] bytes, CollectorStatus status, SourceSettings settings, DateTimeOffset now,
        IReadOnlySet<string>? expectedComputerIds)
    {
        Guard.Source(bytes.Length <= settings.MaxSnapshotBytes, "SOURCE_SIZE_LIMIT");
        Snapshot s;
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateProperties(json.RootElement);
            s = json.RootElement.Deserialize<Snapshot>(Wire.Json)!;
        }
        catch (JsonException) { throw new QueryFault("SOURCE_UNAVAILABLE", "INVALID_SNAPSHOT_JSON"); }
        Guard.Source(s is not null && s.SchemaVersion == "1.0" && s.SourceMode == "scom" &&
            s.Status is "ok" or "partial" && s.Scope is { Id: Wire.ScopeId, Label: not null } &&
            s.ExpectedDeviceCount == (expectedComputerIds?.Count ?? 2) &&
            !string.IsNullOrWhiteSpace(s.DatasetId) && s.DatasetId.Length <= 200,
            "SNAPSHOT_SCHEMA_SCOPE_OR_STATUS_INVALID");
        Guard.Source(status.Status == "ok" && status.ErrorCode is null && status.DatasetId == s!.DatasetId &&
            IsUtc(status.AttemptedAtUtc) && status.AttemptedAtUtc >= s.CollectionStartedAtUtc &&
            status.AttemptedAtUtc <= now.AddMinutes(2), "COLLECTOR_STATUS_MISMATCH");
        Guard.Source(IsUtc(s.CollectedAtUtc) && IsUtc(s.CollectionStartedAtUtc) &&
            s.CollectionStartedAtUtc <= s.CollectedAtUtc && s.CollectedAtUtc <= now.AddMinutes(2) &&
            now - s.CollectedAtUtc <= TimeSpan.FromMinutes(settings.UnavailableMinutes), "SOURCE_EXPIRED_OR_INVALID_TIME");
        Guard.Source(s.ManagementGroup is not null && Guid.TryParse(s.ManagementGroup.Id, out _) &&
            s.Capabilities is not null && !s.Capabilities.NetworkDevices && !s.Capabilities.LicenseEntitlements &&
            !s.Devices.IsDefault && !s.Volumes.IsDefault && !s.Samples.IsDefault &&
            !s.Limitations.IsDefault && !s.SourceRefs.IsDefault && !s.Capabilities.Classes.IsDefault &&
            !s.Capabilities.Metrics.IsDefault && ValidRefs(s.SourceRefs) &&
            s.Devices.Length <= (expectedComputerIds?.Count ?? 64) &&
            s.Volumes.Length <= (expectedComputerIds is null ? 256 : 100000) && s.Samples.Length <= 100000 &&
            s.Capabilities.Classes.All(c => c is "WindowsComputer" or "OperatingSystem" or "LogicalDisk") &&
            s.Capabilities.Metrics.All(m => m is "cpuPercent" or "memoryUsedPercent" or "memoryAvailableMiB" or "diskFreePercent") &&
            s.Limitations.All(l => l is not null && l.Length <= 2000), "SOURCE_COLLECTIONS_INVALID");
        var devices = s.Devices;
        foreach (var d in devices)
        {
            Guard.Source(d is not null && Guid.TryParse(d.EntityId, out _) &&
                string.Equals(d.EntityId, d.SourceObjectId, StringComparison.OrdinalIgnoreCase) &&
                (expectedComputerIds is null
                    ? settings.AllowedHostnames.Contains(d.Hostname, StringComparer.OrdinalIgnoreCase)
                    : expectedComputerIds.Contains(Canonical(d.EntityId)) && !string.IsNullOrWhiteSpace(d.Hostname) &&
                        d.Hostname.Length <= 256 && !d.Hostname.Any(char.IsWhiteSpace) && !d.Hostname.Contains('*')) &&
                d.EntityKind == "computer" && d.OsFamily == "windows" && d.RawHealth is not null &&
                (d.OsVersion is null || d.OsVersion.Length <= 256) &&
                (d.OsCaption is null || !string.IsNullOrWhiteSpace(d.OsCaption) && d.OsCaption.Length <= 256) &&
                d.Health is "healthy" or "warning" or "critical" or "unknown" &&
                d.Virtualization is "physical" or "virtual" or "unknown" &&
                (d.MonitoringMode == "agent" || expectedComputerIds is not null &&
                    d.MonitoringMode is "managementServer" or "unknown") && PositiveOrNull(d.MemoryTotalMiB) &&
                (d.ProcessorCount is null or > 0) &&
                (d.DiscoveredAtUtc is null || IsUtc(d.DiscoveredAtUtc.Value)) &&
                !d.GroupIds.IsDefaultOrEmpty && d.GroupIds.All(g => g == Wire.ScopeId) && ValidRefs(d.SourceRefs),
                "DEVICE_INVALID_OR_OUTSIDE_SCOPE");
        }
        devices = devices.Select(d => d with { EntityId = Canonical(d.EntityId), SourceObjectId = Canonical(d.SourceObjectId),
                Hostname = d.Hostname.ToLowerInvariant() })
            .GroupBy(d => d.EntityId, StringComparer.Ordinal)
            .Select(g =>
            {
                Guard.Source(g.Select(d => d.Hostname).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
                    "AMBIGUOUS_DEVICE_IDENTITY");
                return g.OrderByDescending(d => (d.MemoryTotalMiB is not null ? 1 : 0) +
                (d.OsVersion is not null ? 1 : 0) + (d.ProcessorCount is not null ? 1 : 0))
                .ThenBy(Wire.Encode, StringComparer.Ordinal).First();
            })
            .OrderBy(d => d.EntityId, StringComparer.Ordinal).ToImmutableArray();
        Guard.Source(devices.Length <= (expectedComputerIds?.Count ?? 2) &&
            devices.Select(d => d.Hostname).Distinct(StringComparer.OrdinalIgnoreCase).Count() == devices.Length,
            "AMBIGUOUS_OR_UNEXPECTED_DEVICE_COVERAGE");
        var computerIds = devices.Select(d => d.EntityId).ToHashSet(StringComparer.Ordinal);
        foreach (var v in s.Volumes)
            Guard.Source(v is not null && Guid.TryParse(v.EntityId, out _) &&
                Guid.TryParse(v.ParentEntityId, out _) && computerIds.Contains(Canonical(v.ParentEntityId)) &&
                !computerIds.Contains(Canonical(v.EntityId)) && !string.IsNullOrWhiteSpace(v.DeviceId) &&
                PositiveOrNull(v.SizeMiB) && ValidRefs(v.SourceRefs), "VOLUME_INVALID_OR_OUTSIDE_SCOPE");
        var volumes = s.Volumes.Select(v => v with { EntityId = Canonical(v.EntityId), ParentEntityId = Canonical(v.ParentEntityId) })
            .GroupBy(v => v.EntityId, StringComparer.Ordinal).Select(g =>
            {
                Guard.Source(g.Select(v => v.ParentEntityId).Distinct().Count() == 1, "AMBIGUOUS_VOLUME_PARENT");
                return g.OrderByDescending(v => v.SizeMiB is not null).ThenBy(Wire.Encode, StringComparer.Ordinal).First();
            }).OrderBy(v => v.EntityId, StringComparer.Ordinal).ToImmutableArray();
        var volumeIds = volumes.Select(v => v.EntityId).ToHashSet(StringComparer.Ordinal);
        foreach (var m in s.Samples)
        {
            Guard.Source(m is not null && Guid.TryParse(m.EntityId, out _) &&
                m.Metric is "cpuPercent" or "memoryUsedPercent" or "memoryAvailableMiB" or "diskFreePercent" &&
                s.Capabilities.Metrics.Contains(m.Metric) &&
                double.IsFinite(m.Value) && m.Value is >= 0 and <= 1e15 &&
                (m.Metric == "memoryAvailableMiB" || m.Value <= 100) &&
                IsUtc(m.ObservedAtUtc) && m.ObservedAtUtc <= s.CollectedAtUtc &&
                !string.IsNullOrWhiteSpace(m.CollectionRuleId) && m.CollectionRuleId.Length <= 500 &&
                (m.Metric == "diskFreePercent" ? volumeIds : computerIds).Contains(Canonical(m.EntityId)),
                "SAMPLE_INVALID_OR_OUTSIDE_SCOPE");
            if (m.Metric == "memoryAvailableMiB")
            {
                var total = devices.Single(d => d.EntityId == Canonical(m.EntityId)).MemoryTotalMiB;
                Guard.Source(total is null || m.Value <= total, "MEMORY_SAMPLE_EXCEEDS_TOTAL");
            }
        }
        var samples = s.Samples.Select(m => m with { EntityId = Canonical(m.EntityId) })
            .GroupBy(m => (m.EntityId, m.Metric, m.ObservedAtUtc, m.CollectionRuleId)).Select(g =>
            {
                Guard.Source(g.Select(m => (m.Value, m.Optimized)).Distinct().Count() == 1, "CONFLICTING_SAMPLE_DUPLICATES");
                return g.First();
            }).OrderBy(m => m.EntityId, StringComparer.Ordinal).ThenBy(m => m.Metric, StringComparer.Ordinal)
            .ThenBy(m => m.ObservedAtUtc).ThenBy(m => m.CollectionRuleId, StringComparer.Ordinal).ToImmutableArray();
        var limitations = ImmutableArray.CreateBuilder<string>();
        if (devices.Length < s.ExpectedDeviceCount) limitations.Add("EXPECTED_DEVICE_COVERAGE_INCOMPLETE");
        if (s.Samples.IsEmpty) limitations.Add("NO_METRIC_OBSERVATIONS_AVAILABLE");
        if (s.Devices.Length != devices.Length || s.Volumes.Length != volumes.Length)
            limitations.Add("DUPLICATES_RESOLVED_BY_KNOWN_FIELD_COUNT_THEN_ORDINAL_CANONICAL_JSON");
        return new Dataset(s with { Devices = devices, Volumes = volumes, Samples = samples },
            Convert.ToHexString(SHA256.HashData(bytes)), s.Devices.Length - devices.Length,
            s.Volumes.Length - volumes.Length, s.Samples.Length - samples.Length, limitations.ToImmutable());
    }

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    internal static void RejectDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                Guard.Source(seen.Add(property.Name), "DUPLICATE_JSON_PROPERTY");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) RejectDuplicateProperties(child);
    }
    private static bool PositiveOrNull(double? value) => value is null || double.IsFinite(value.Value) && value is > 0 and <= 1e15;
    private static bool ValidRefs(ImmutableArray<string> refs) => !refs.IsDefaultOrEmpty &&
        refs.All(r => r is not null && r.StartsWith("SCOM:", StringComparison.Ordinal) && r.Length <= 300);
    private static string Canonical(string value) => Guid.Parse(value).ToString("D");
}
