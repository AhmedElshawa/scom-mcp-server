using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScomMcp;

public static class Wire
{
    public const string ScopeId = "scom-lab";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
}

public sealed record ScopeInfo(string Id, string Label);
public sealed record ManagementGroup(string Id, string Name);
public sealed record Capabilities(ImmutableArray<string> Classes, ImmutableArray<string> Metrics,
    bool NetworkDevices, bool LicenseEntitlements);
public sealed record Device(
    string EntityId, string SourceObjectId, string Hostname, string EntityKind, string OsFamily,
    string? OsVersion, string Health, string RawHealth, string Virtualization, string MonitoringMode,
    double? MemoryTotalMiB, int? ProcessorCount, DateTimeOffset? DiscoveredAtUtc,
    ImmutableArray<string> GroupIds, ImmutableArray<string> SourceRefs);
public sealed record Volume(string EntityId, string ParentEntityId, string DeviceId, string? FileSystem,
    double? SizeMiB, ImmutableArray<string> SourceRefs);
public sealed record Sample(string EntityId, string Metric, DateTimeOffset ObservedAtUtc, [property: JsonRequired] double Value,
    string CollectionRuleId, [property: JsonRequired] bool Optimized);
public sealed record Snapshot(
    string SchemaVersion, string DatasetId, string SourceMode, string Status,
    ManagementGroup ManagementGroup, ScopeInfo Scope, DateTimeOffset CollectedAtUtc,
    DateTimeOffset CollectionStartedAtUtc, int ExpectedDeviceCount,
    ImmutableArray<Device> Devices, ImmutableArray<Volume> Volumes, ImmutableArray<Sample> Samples,
    Capabilities Capabilities, ImmutableArray<string> Limitations, ImmutableArray<string> SourceRefs);
public sealed record CollectorStatus(string Status, DateTimeOffset AttemptedAtUtc, string? ErrorCode, string? DatasetId);
public sealed record Dataset(Snapshot Snapshot, string Fingerprint, int DuplicateDevices, int DuplicateVolumes,
    int DuplicateSamples, ImmutableArray<string> ValidationLimitations, string StorageMode = "snapshot");
public sealed record Freshness(double AgeSeconds, string State);
public sealed record Coverage(int ExpectedDeviceCount, int UniqueDeviceCount, bool Complete,
    DateTimeOffset CollectionStartedAtUtc);
public sealed record Envelope(string SchemaVersion, string Status, string SourceMode, string? DatasetId,
    ScopeInfo Scope, object Data, DateTimeOffset? AsOfUtc, Freshness? Freshness, Coverage? Coverage,
    ImmutableArray<string> Limitations, ImmutableArray<string> SourceRefs, string CorrelationId);
public sealed record ToolResponse(Envelope Envelope, bool IsError = false);

public sealed class QueryFault(string code, string safeMessage) : Exception(safeMessage)
{
    public string Code { get; } = code;
}

public static class Guard
{
    public static void Argument([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new QueryFault("INVALID_ARGUMENT", message);
    }
    public static void Source([DoesNotReturnIf(false)] bool condition, string code)
    {
        if (!condition) throw new QueryFault("SOURCE_UNAVAILABLE", code);
    }
    public static string Entity(string entityId)
    {
        Argument(Guid.TryParse(entityId, out _), "entityId must be a GUID.");
        return Guid.Parse(entityId).ToString("D");
    }
}
