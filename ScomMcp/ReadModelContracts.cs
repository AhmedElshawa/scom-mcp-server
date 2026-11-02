using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace ScomMcp;

public sealed class ReadModelSettings
{
    public bool Enabled { get; set; }
    public string DatabasePath { get; set; } = "";
    public string ManagementGroupId { get; set; } = "";
    public string ScopeKind { get; set; } = "group";
    public string? GroupId { get; set; }
    public string[] PublisherObjectIds { get; set; } = [];
    public string IngestionRole { get; set; } = "Scom.Ingest";
    public Dictionary<string, string[]> AllowedPropertiesByClass { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, string>> AllowedMetricsByClass { get; set; } = new(StringComparer.Ordinal);
    public bool IncludeAlertDescriptions { get; set; }
    public int MaxBatchBytes { get; set; } = 1024 * 1024;
    public int MaxDatasetBytes { get; set; } = 16 * 1024 * 1024;
    public int MaxRecords { get; set; } = 100000;
    public int MaxDatabaseBytes { get; set; } = 128 * 1024 * 1024;
    public int FreshMinutes { get; set; } = 15;
    public int UnavailableMinutes { get; set; } = 60;
    public int SessionMinutes { get; set; } = 30;
    public int RequestBodyTimeoutSeconds { get; set; } = 30;
    public bool WindowsToolsEnabled { get; set; }
    public WindowsMetricHostingSettings? WindowsMetricHosting { get; set; }
    public void Validate()
    {
        if (WindowsMetricHosting is { } hosting)
        {
            if (!WindowsToolsEnabled || new[] { hosting.ComputerClass, hosting.OperatingSystemClass, hosting.RelationshipClass }
                .Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 256 ||
                    name.Any(char.IsWhiteSpace) || name.IndexOfAny(['*', '?', '[', ']']) >= 0) ||
                hosting.ComputerClass == hosting.OperatingSystemClass)
                throw new InvalidOperationException("Windows metric hosting requires reviewed exact classes and relationship identity.");
        }
        if (WindowsToolsEnabled && !Enabled)
            throw new InvalidOperationException("Indexed Windows tools require the read model to be enabled.");
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(DatabasePath) || !Path.IsPathFullyQualified(DatabasePath) ||
            DatabasePath.StartsWith(@"\\", StringComparison.Ordinal) ||
            !ValidId(ManagementGroupId) || ScopeKind is not ("group" or "managementGroup") ||
            (ScopeKind == "group" ? !ValidId(GroupId) : GroupId is not null) ||
            PublisherObjectIds is null || PublisherObjectIds.Length == 0 || PublisherObjectIds.Any(id => !ValidId(id)) ||
            AllowedPropertiesByClass is null || AllowedPropertiesByClass.Count > 256 ||
            AllowedPropertiesByClass.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 256 ||
                p.Value is null || p.Value.Length > 64 || p.Value.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 128)) ||
            AllowedMetricsByClass is null || AllowedMetricsByClass.Count > 256 ||
            AllowedMetricsByClass.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 256 ||
                p.Value is null || p.Value.Count > 64 || p.Value.Any(m => string.IsNullOrWhiteSpace(m.Key) ||
                    m.Key.Length > 128 || string.IsNullOrWhiteSpace(m.Value) || m.Value.Length > 64)) ||
            string.IsNullOrWhiteSpace(IngestionRole) || IngestionRole.Any(char.IsWhiteSpace) ||
            MaxBatchBytes is < 1024 or > 1024 * 1024 ||
            MaxDatasetBytes < MaxBatchBytes || MaxDatasetBytes > 32 * 1024 * 1024 ||
            MaxRecords is < 1 or > 1000000 ||
            MaxDatabaseBytes < 4L * MaxDatasetBytes || MaxDatabaseBytes > 1024 * 1024 * 1024 ||
            FreshMinutes < 1 || UnavailableMinutes <= FreshMinutes || UnavailableMinutes > 1440 ||
            SessionMinutes is < 1 or > 120 || RequestBodyTimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException("Read model configuration is invalid. Use protected server-local storage.");
        var root = Path.GetPathRoot(DatabasePath);
        if (OperatingSystem.IsWindows() && root is not null && new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidOperationException("Read model storage must not use a mapped network drive.");
        ManagementGroupId = Guid.Parse(ManagementGroupId).ToString("D");
        GroupId = GroupId is null ? null : Guid.Parse(GroupId).ToString("D");
        PublisherObjectIds = PublisherObjectIds.Select(id => Guid.Parse(id).ToString("D")).Distinct().ToArray();
    }

    public sealed class WindowsMetricHostingSettings
    {
        public string ComputerClass { get; set; } = "";
        public string OperatingSystemClass { get; set; } = "";
        public string RelationshipClass { get; set; } = "";
    }
    internal static bool ValidId(string? id) => Guid.TryParseExact(id, "D", out var value) && value != Guid.Empty;
}

public sealed record ObservedObject(string Id, string ClassName, ImmutableArray<string> BaseClasses,
    string DisplayName, string Health, DateTimeOffset ObservedAtUtc,
    ImmutableDictionary<string, string?> Properties);
public sealed record ObservedRelationship(string Id, string ClassName, string SourceId, string TargetId);
public sealed record ObservedAlert(string Id, string ObjectId, string Name, string? Description,
    string Severity, [property: JsonRequired] int ResolutionState, DateTimeOffset ObservedAtUtc);
public sealed record ObservedMetric(string Id, string ObjectId, string Metric, string Unit,
    DateTimeOffset ObservedAtUtc, [property: JsonRequired] double Value,
    string RuleId, [property: JsonRequired] bool Optimized);
public sealed record IngestionBatch(ImmutableArray<ObservedObject> Objects,
    ImmutableArray<ObservedRelationship> Relationships, ImmutableArray<ObservedAlert> Alerts,
    ImmutableArray<ObservedMetric> Metrics, WindowsInventoryBatch? WindowsInventory = null);
public sealed record WindowsInventoryBatch(ImmutableArray<Device> Devices, ImmutableArray<Volume> Volumes);
public sealed record DomainObservation(DateTimeOffset AsOfUtc, [property: JsonRequired] bool Complete, string? Reason);
public sealed record ReadModelManifest(string ManagementGroupId, string ScopeKind, string? GroupId,
    [property: JsonRequired] bool MembershipVerified, ImmutableArray<string> ExpectedObjectIds,
    DomainObservation Inventory, DomainObservation Alerts, DomainObservation Metrics,
    DateTimeOffset AlertHistoryStartUtc, ImmutableArray<string>? ExpectedComputerIds = null);
public sealed record ReadModelProvenance(string ManagementGroupId, string ScopeKind, string? GroupId,
    bool MembershipVerified, int ExpectedObjectCount, DomainObservation Inventory,
    DomainObservation Alerts, DomainObservation Metrics, DateTimeOffset AlertHistoryStartUtc);
public sealed record ReadModelPage(string DatasetId, string Status, ReadModelProvenance Manifest,
    int TotalCount, int PageSize, string? NextCursor, object[] Rows);
