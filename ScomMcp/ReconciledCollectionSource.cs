using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ScomMcp;

public sealed record NormalizedSourceScan(string ManagementGroupId, string ScopeKind, string? GroupId,
    bool MembershipVerified, ImmutableArray<string> ExpectedObjectIds, ImmutableArray<ObservedObject> Objects,
    ImmutableArray<ObservedRelationship> Relationships, DomainObservation Inventory,
    ImmutableArray<ObservedAlert> AlertObservations, DomainObservation Alerts,
    ImmutableArray<MetricSeriesScan> MetricSeries, DateTimeOffset MetricsAsOfUtc,
    WindowsInventoryBatch? WindowsInventory = null, ImmutableArray<string>? ExpectedComputerIds = null,
    DomainObservation? MetricAssessment = null);

public interface IScomNormalizationProfile
{
    Task<NormalizedSourceScan> CollectAsync(SourceCollectionRequest request, CancellationToken cancellationToken);
}

public sealed record AlertReassessment(string AlertId, string ObjectId);
public sealed record SourceCollectionRequest(DateTimeOffset RequestedAtUtc, DateTimeOffset MetricHistoryStartUtc,
    ImmutableArray<AlertReassessment> PriorUnresolvedAlerts);

public sealed class ReconciledSourceSettings
{
    public int MaxRecords { get; set; } = 100000;
    public int BatchRecords { get; set; } = 100;
    public int MaxAlerts { get; set; } = 10000;
    public int MaxSeriesSamples { get; set; } = 10000;
    public int MaxMetricSamples { get; set; } = 50000;
    public TimeSpan ResolvedAlertRetention { get; set; } = TimeSpan.FromDays(7);
    public void Validate()
    {
        if (MaxRecords is < 1 or > 1000000 || BatchRecords is < 1 or > 1000 || BatchRecords > MaxRecords ||
            MaxAlerts is < 1 or > 100000 || MaxSeriesSamples is < 1 or > 100000 ||
            MaxMetricSamples < MaxSeriesSamples || MaxMetricSamples > 1000000 ||
            ResolvedAlertRetention < TimeSpan.Zero || ResolvedAlertRetention > TimeSpan.FromDays(365))
            throw new InvalidOperationException("Reconciled source limits are invalid.");
    }
}

internal sealed record SourceScope(string ManagementGroup, string Kind, string? Group);
internal sealed record PendingSourceState(string SessionId, ReadModelManifest Manifest, IngestionBatch Inventory,
    AlertReconciliation Alerts, ImmutableArray<ObservedMetric> Metrics,
    ImmutableArray<ObservedAlert> ServedAlerts, ImmutableArray<ObservedMetric> ServedMetrics, SourceScope Scope);
internal sealed record ReconciledSourceState(
    [property: JsonRequired] AlertReconciliation? PriorAlerts,
    [property: JsonRequired] ImmutableArray<ObservedMetric> PriorMetrics,
    [property: JsonRequired] SourceScope? PriorScope,
    [property: JsonRequired] PendingSourceState? Pending,
    [property: JsonRequired] string? Acknowledged,
    [property: JsonRequired] string? Reserved);

// The optional store belongs to the worker, not the MCP query process.
public sealed class ReconciledCollectionSource : IScomCollectionSource
{
    private readonly IScomNormalizationProfile profile;
    private readonly ReconciledSourceSettings settings;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private AlertReconciliation? priorAlerts;
    private ImmutableArray<ObservedMetric> priorMetrics = [];
    private SourceScope? priorScope;
    private PendingSourceState? pending;
    private PreparedCollection? pendingCollection;
    private string? acknowledged;
    private string? reserved;
    private readonly SourceCheckpointStore? checkpoint;
    private readonly string settingsHash;

    public ReconciledCollectionSource(IScomNormalizationProfile profile, ReconciledSourceSettings settings, TimeProvider clock,
        SourceCheckpointStore? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        settings.Validate();
        this.profile = profile;
        this.clock = clock;
        this.settings = new()
        {
            MaxRecords = settings.MaxRecords, BatchRecords = settings.BatchRecords, MaxAlerts = settings.MaxAlerts,
            MaxSeriesSamples = settings.MaxSeriesSamples, MaxMetricSamples = settings.MaxMetricSamples,
            ResolvedAlertRetention = settings.ResolvedAlertRetention
        };
        this.checkpoint = checkpoint;
        settingsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Wire.Encode(this.settings))));
        if (checkpoint?.Load(settingsHash) is { } state)
        {
            ValidateState(state);
            priorAlerts = state.PriorAlerts;
            priorMetrics = state.PriorMetrics;
            priorScope = state.PriorScope;
            pending = state.Pending;
            acknowledged = state.Acknowledged;
            reserved = state.Reserved;
        }
    }

    public async Task<PreparedCollection> PrepareAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (pending is { } existing) return pendingCollection ??= Prepared(existing);
            var now = clock.GetUtcNow();
            var request = new SourceCollectionRequest(now, now.AddDays(-14),
                priorAlerts?.Alerts.Where(a => a.ResolutionState != 255)
                    .Select(a => new AlertReassessment(a.Id, a.ObjectId)).ToImmutableArray() ?? []);
            var scan = await profile.CollectAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateScan(scan);
            var scope = new SourceScope(scan.ManagementGroupId, scan.ScopeKind, scan.GroupId);
            if (priorScope is { } previousScope && previousScope != scope)
                throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_SCOPE_CHANGED_REQUIRES_RESTART");
            var expected = scan.ExpectedObjectIds.ToHashSet(StringComparer.Ordinal);
            var objects = scan.Objects.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            if (objects.Count != scan.Objects.Length || !objects.IsSubsetOf(expected) ||
                scan.Inventory.Complete && !objects.SetEquals(expected))
                throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_DISCOVERY_MEMBERSHIP_INVALID");
            var alerts = AlertReconciler.Reconcile(priorAlerts, scan.AlertObservations, expected, scan.Alerts,
                clock.GetUtcNow(), settings.ResolvedAlertRetention, settings.MaxAlerts);
            var metrics = MetricReconciler.Reconcile(priorMetrics, scan.MetricSeries, expected, scan.MetricsAsOfUtc,
                settings.MaxSeriesSamples, settings.MaxMetricSamples);
            // Retained data for still-authorized but undiscovered objects cannot
            // create orphan rows. Disclose that omission independently of scope.
            var servedAlerts = alerts.Alerts.Where(a => objects.Contains(a.ObjectId)).ToImmutableArray();
            var servedMetrics = metrics.Observations.Where(m => objects.Contains(m.ObjectId)).ToImmutableArray();
            var alertCoverage = servedAlerts.Length == alerts.Alerts.Length ? alerts.Coverage :
                new(scan.Alerts.AsOfUtc, false, "ALERT_PARENT_DISCOVERY_INCOMPLETE");
            var metricCoverage = servedMetrics.Length == metrics.Observations.Length ? metrics.Coverage :
                new(scan.MetricsAsOfUtc, false, "METRIC_PARENT_DISCOVERY_INCOMPLETE");
            if (scan.MetricAssessment is { Complete: false } assessment)
                metricCoverage = assessment;
            foreach (var relationship in scan.Relationships)
                if (relationship is null || !objects.Contains(relationship.SourceId) || !objects.Contains(relationship.TargetId))
                    throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_RELATIONSHIP_PARENT_INVALID");
            var recordCount = (long)scan.Objects.Length + scan.Relationships.Length + servedAlerts.Length + servedMetrics.Length +
                (scan.WindowsInventory?.Devices.Length ?? 0) + (scan.WindowsInventory?.Volumes.Length ?? 0);
            if (recordCount > settings.MaxRecords)
                throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_TOTAL_RECORD_BUDGET");
            var session = reserved ?? Guid.NewGuid().ToString("D");
            var manifest = new ReadModelManifest(scan.ManagementGroupId, scan.ScopeKind, scan.GroupId, true,
                scan.ExpectedObjectIds, scan.Inventory, alertCoverage, metricCoverage, alerts.HistoryStartUtc,
                scan.ExpectedComputerIds);
            var next = new PendingSourceState(session, manifest,
                new(scan.Objects, scan.Relationships, [], [], scan.WindowsInventory),
                alerts, metrics.Observations, servedAlerts, servedMetrics, scope);
            checkpoint?.Save(new(priorAlerts, priorMetrics, priorScope, next, acknowledged, session), settingsHash);
            reserved = session;
            pending = next;
            return pendingCollection = Prepared(next);
        }
        finally { gate.Release(); }
    }

    public async Task AcknowledgeAsync(string sessionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (sessionId == acknowledged) return;
            if (pending is not { } prepared || prepared.SessionId != sessionId)
                throw new InvalidOperationException("Source acknowledgement does not match its prepared dataset.");
            checkpoint?.Save(new(prepared.Alerts, prepared.Metrics, prepared.Scope, null, sessionId, null), settingsHash);
            priorAlerts = prepared.Alerts;
            priorMetrics = prepared.Metrics;
            priorScope = prepared.Scope;
            acknowledged = sessionId;
            pending = null;
            pendingCollection = null;
            reserved = null;
        }

        finally { gate.Release(); }
    }

    public async Task<string> ReserveSessionAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (reserved is not null) return reserved;
            var session = Guid.NewGuid().ToString("D");
            checkpoint?.Save(new(priorAlerts, priorMetrics, priorScope, pending, acknowledged, session), settingsHash);
            return reserved = session;
        }
        finally { gate.Release(); }
    }

    private PreparedCollection Prepared(PendingSourceState state) =>
        new(state.SessionId, state.Manifest, Batches(state.Inventory, state.ServedAlerts, state.ServedMetrics));

    private async IAsyncEnumerable<IngestionBatch> Batches(IngestionBatch scan, ImmutableArray<ObservedAlert> alerts,
        ImmutableArray<ObservedMetric> metrics, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Single-domain chunks preserve stable serialization across enumeration.
        for (var i = 0; i < scan.Objects.Length; i += settings.BatchRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(scan.Objects[i..Math.Min(i + settings.BatchRecords, scan.Objects.Length)], [], [], []);
        }
        for (var i = 0; i < scan.Relationships.Length; i += settings.BatchRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new([], scan.Relationships[i..Math.Min(i + settings.BatchRecords, scan.Relationships.Length)], [], []);
        }
        for (var i = 0; i < alerts.Length; i += settings.BatchRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new([], [], alerts[i..Math.Min(i + settings.BatchRecords, alerts.Length)], []);
        }
        for (var i = 0; i < metrics.Length; i += settings.BatchRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new([], [], [], metrics[i..Math.Min(i + settings.BatchRecords, metrics.Length)]);
        }
        if (scan.WindowsInventory is { } windows)
        {
            for (var i = 0; i < windows.Devices.Length; i += settings.BatchRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new([], [], [], [], new(windows.Devices[i..Math.Min(i + settings.BatchRecords, windows.Devices.Length)], []));
            }
            for (var i = 0; i < windows.Volumes.Length; i += settings.BatchRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new([], [], [], [], new([], windows.Volumes[i..Math.Min(i + settings.BatchRecords, windows.Volumes.Length)]));
            }
        }
        await Task.CompletedTask;
    }

    private void ValidateScan(NormalizedSourceScan? scan)
    {
        if (scan is null || !scan.MembershipVerified || !Canonical(scan.ManagementGroupId) ||
            scan.ScopeKind is not ("group" or "managementGroup") ||
            (scan.ScopeKind == "group" ? !Canonical(scan.GroupId) : scan.GroupId is not null) ||
            scan.ExpectedObjectIds.IsDefault || scan.ExpectedObjectIds.Length > settings.MaxRecords ||
            scan.ExpectedObjectIds.Any(id => !Canonical(id)) ||
            scan.ExpectedObjectIds.Distinct(StringComparer.Ordinal).Count() != scan.ExpectedObjectIds.Length ||
            scan.Objects.IsDefault || scan.Objects.Any(o => o is null || !Canonical(o.Id)) ||
            scan.Relationships.IsDefault || scan.AlertObservations.IsDefault || scan.MetricSeries.IsDefault ||
            (long)scan.Objects.Length + scan.Relationships.Length + scan.AlertObservations.Length > settings.MaxRecords ||
            scan.MetricSeries.Length > settings.MaxRecords ||
            scan.Inventory is null || scan.Alerts is null ||
            scan.Inventory.AsOfUtc == default || scan.Inventory.AsOfUtc.Offset != TimeSpan.Zero ||
            scan.Inventory.AsOfUtc > clock.GetUtcNow() ||
            scan.MetricsAsOfUtc == default || scan.MetricsAsOfUtc.Offset != TimeSpan.Zero ||
            scan.MetricsAsOfUtc > clock.GetUtcNow() ||
            scan.MetricAssessment is { } assessment && (assessment.AsOfUtc != scan.MetricsAsOfUtc ||
                (assessment.Complete ? assessment.Reason is not null : string.IsNullOrWhiteSpace(assessment.Reason))) ||
            (scan.Inventory.Complete ? scan.Inventory.Reason is not null : string.IsNullOrWhiteSpace(scan.Inventory.Reason)) ||
            scan.WindowsInventory is { } windows && (windows.Devices.IsDefault || windows.Volumes.IsDefault ||
                windows.Devices.Any(d => d is null) || windows.Volumes.Any(v => v is null)) ||
            scan.ExpectedComputerIds is { } computers && (computers.IsDefault || computers.Any(id => !Canonical(id)) ||
                computers.Distinct(StringComparer.Ordinal).Count() != computers.Length ||
                !computers.ToHashSet(StringComparer.Ordinal).IsSubsetOf(scan.ExpectedObjectIds)))
            throw new QueryFault("SOURCE_UNAVAILABLE", "SOURCE_NORMALIZED_SCAN_INVALID");
    }
    private static bool Canonical(string? id) => ReadModelSettings.ValidId(id) && Guid.Parse(id!).ToString("D") == id;

    private void ValidateState(ReconciledSourceState state)
    {
        if (state.PriorMetrics.IsDefault || state.PriorMetrics.Length > settings.MaxMetricSamples ||
            state.PriorMetrics.Any(m => m is null) ||
            state.PriorAlerts is { } alerts && (alerts.Alerts.IsDefault || alerts.Alerts.Length > settings.MaxAlerts ||
                alerts.Alerts.Any(a => a is null) || alerts.Coverage is null) ||
            state.Acknowledged is not null && !Canonical(state.Acknowledged) ||
            state.Reserved is not null && !Canonical(state.Reserved) ||
            state.PriorScope is { } scope && !ValidScope(scope))
            throw new InvalidOperationException("Worker checkpoint state is invalid.");
        if (state.Pending is not { } prepared) return;
        if (prepared.SessionId != state.Reserved || !Canonical(prepared.SessionId) || !ValidScope(prepared.Scope) ||
            state.PriorScope is { } previous && previous != prepared.Scope ||
            prepared.Manifest is null || !prepared.Manifest.MembershipVerified ||
            prepared.Manifest.ManagementGroupId != prepared.Scope.ManagementGroup ||
            prepared.Manifest.ScopeKind != prepared.Scope.Kind || prepared.Manifest.GroupId != prepared.Scope.Group ||
            prepared.Inventory is null || prepared.Inventory.Objects.IsDefault || prepared.Inventory.Relationships.IsDefault ||
            !prepared.Inventory.Alerts.IsEmpty || !prepared.Inventory.Metrics.IsEmpty ||
            prepared.Alerts is null || prepared.Alerts.Alerts.IsDefault || prepared.Metrics.IsDefault ||
            prepared.ServedAlerts.IsDefault || prepared.ServedMetrics.IsDefault ||
            prepared.Metrics.Length > settings.MaxMetricSamples || prepared.Alerts.Alerts.Length > settings.MaxAlerts ||
            (long)prepared.Inventory.Objects.Length + prepared.Inventory.Relationships.Length +
                prepared.ServedAlerts.Length + prepared.ServedMetrics.Length +
                (prepared.Inventory.WindowsInventory?.Devices.Length ?? 0) +
                (prepared.Inventory.WindowsInventory?.Volumes.Length ?? 0) > settings.MaxRecords)
            throw new InvalidOperationException("Worker pending checkpoint is invalid.");
    }

    private static bool ValidScope(SourceScope scope) => Canonical(scope.ManagementGroup) &&
        scope.Kind is "group" or "managementGroup" &&
        (scope.Kind == "group" ? Canonical(scope.Group) : scope.Group is null);
}
