using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ScomMcp;

public sealed class TargetedLiveSettings
{
    public bool Enabled { get; set; }
    public string PublisherObjectId { get; set; } = "";
    public string ProviderPolicySha256 { get; set; } = "";
    public int RequestSeconds { get; set; } = 15;
    public int ExecutionSeconds { get; set; } = 5;
    public int MaxAlerts { get; set; } = 20;
    public int QueueCapacity { get; set; } = 8;
    public int RequestsPerCallerMinute { get; set; } = 3;
    public int RequestsPerMinute { get; set; } = 10;

    public void Validate(ReadModelSettings model)
    {
        if (RequestSeconds is < 5 or > 30 || ExecutionSeconds is < 1 or > 10 ||
            ExecutionSeconds > RequestSeconds - 2 || MaxAlerts is < 1 or > 100 ||
            QueueCapacity is < 1 or > 16 || RequestsPerCallerMinute is < 1 or > 10 ||
            RequestsPerMinute is < 1 or > 30 ||
            Enabled && (!model.Enabled || !ReadModelSettings.ValidId(PublisherObjectId) ||
                !model.PublisherObjectIds.Contains(PublisherObjectId) || !Hash(ProviderPolicySha256)))
            throw new InvalidOperationException("TargetedLive settings require a pinned indexed source/publisher/provider and bounded budgets.");
    }

    internal static bool Hash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

public sealed record LiveSourceBinding(string ManagementGroupId, string ScopeKind, string? GroupId);
public sealed record LiveCheckRequest(string RequestId, string ObjectId, LiveSourceBinding Source,
    DateTimeOffset RequestedAtUtc, DateTimeOffset ExpiresAtUtc, int ExecutionSeconds, int MaxAlerts, int MaxSourceRequests);
public sealed record LiveAlert(string Id, string ObjectId, string Name, string Severity, int ResolutionState,
    DateTimeOffset ObservedAtUtc);
public sealed record LiveCheckObservation(
    [property: JsonRequired] string RequestId, [property: JsonRequired] string ObjectId,
    [property: JsonRequired] LiveSourceBinding Source, [property: JsonRequired] string Outcome,
    [property: JsonRequired] bool ScopeVerified, [property: JsonRequired] DateTimeOffset ScopeCheckedAtUtc,
    [property: JsonRequired] DateTimeOffset CheckedAtUtc, string? Health, DateTimeOffset? HealthObservedAtUtc,
    [property: JsonRequired] bool AlertsComplete, [property: JsonRequired] ImmutableArray<LiveAlert> Alerts,
    [property: JsonRequired] int SourceRequests);
public sealed record LiveClaim(string ProviderPolicySha256, LiveSourceBinding Source, bool ContractVerified);
public sealed record LiveCompletion(LiveCheckObservation Observation);
public sealed record LiveCheckAnswer(string Mode, string RequestId, string RetainedDatasetId,
    DateTimeOffset ReceivedAtUtc, LiveCheckObservation Observation, ImmutableArray<string> Limitations);

// Volatile rendezvous, not retained monitoring data. Losing the host loses requests, never creates a live-success fallback.
public sealed class TargetedLiveChecks(TargetedLiveSettings settings, ReadModelSettings model, TimeProvider clock,
    ILogger<TargetedLiveChecks> logger)
{
    private sealed class Entry(LiveCheckRequest request, string owner)
    {
        public LiveCheckRequest Request { get; } = request;
        public string Owner { get; } = owner;
        public TaskCompletionSource<LiveCheckObservation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Claimed;
        public int Waiters;
        public string? ResultDigest;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly List<(DateTimeOffset At, string Owner)> admissions = [];
    private DateTimeOffset readyUntil;
    private DateTimeOffset nextStart;
    private Entry? active;
    public LiveSourceBinding Source { get; } = new(model.ManagementGroupId, model.ScopeKind, model.GroupId);

    public async Task<LiveCheckObservation> RequestAsync(Caller caller, string objectId, CancellationToken ct,
        string? correlationId = null)
    {
        Guard.Argument(ReadModelSettings.ValidId(objectId) && Guid.Parse(objectId).ToString("D") == objectId,
            "objectId must be a canonical GUID.");
        CallerAuthorization.AuthorizeScope(caller, Wire.ScopeId);
        ct.ThrowIfCancellationRequested();
        Entry entry;
        lock (gate)
        {
            Cleanup();
            if (!settings.Enabled) throw Fault("LIVE_NOT_ENABLED");
            if (clock.GetUtcNow() > readyUntil || active?.Request.ExpiresAtUtc <= clock.GetUtcNow())
                throw Fault("LIVE_PROVIDER_UNAVAILABLE");
            var owner = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(caller.Binding)));
            entry = entries.Values.FirstOrDefault(e => e.Owner == owner && e.Request.ObjectId == objectId &&
                !e.Completion.Task.IsCompleted)!;
            if (entry is null)
            {
                var now = clock.GetUtcNow();
                if (admissions.Count >= settings.RequestsPerMinute ||
                    admissions.Count(a => a.Owner == owner) >= settings.RequestsPerCallerMinute)
                    throw Fault("LIVE_RATE_LIMITED");
                if (entries.Count >= settings.QueueCapacity) throw Fault("LIVE_QUEUE_FULL");
                var request = new LiveCheckRequest(Guid.NewGuid().ToString("D"), objectId, Source, now,
                    now.AddSeconds(settings.RequestSeconds), settings.ExecutionSeconds, settings.MaxAlerts, 3);
                entry = new(request, owner);
                entries.Add(request.RequestId, entry);
                admissions.Add((now, owner));
                logger.LogInformation("Targeted check admitted. Event={Event} Utc={Utc} RequestId={RequestId} TraceId={TraceId}",
                    "LIVE_ADMITTED", now, request.RequestId, correlationId);
            }
            if (entry.Waiters >= 2) throw Fault("LIVE_RATE_LIMITED");
            entry.Waiters++;
        }
        try
        {
            var remaining = entry.Request.ExpiresAtUtc - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw Fault("LIVE_TIMEOUT");
            return await entry.Completion.Task.WaitAsync(remaining, ct);
        }
        catch (TimeoutException) { throw Fault("LIVE_TIMEOUT"); }
        finally
        {
            lock (gate)
            {
                entry.Waiters--;
                if (entry.Waiters == 0 && !entry.Claimed) entries.Remove(entry.Request.RequestId);
            }
        }
    }

    public LiveCheckRequest? Claim(string publisher, LiveClaim claim)
    {
        lock (gate)
        {
            Cleanup();
            AuthorizeProvider(publisher);
            if (claim.Source != Source || !claim.ContractVerified ||
                !string.Equals(claim.ProviderPolicySha256, settings.ProviderPolicySha256, StringComparison.OrdinalIgnoreCase))
                throw Fault("LIVE_PROVIDER_UNVERIFIED");
            var now = clock.GetUtcNow();
            readyUntil = now.AddSeconds(6);
            // One lease, including cancelled/ambiguous work, until it completes or expires.
            if (active is not null || now < nextStart) return null;
            var entry = entries.Values.FirstOrDefault(e => !e.Claimed && e.Waiters > 0);
            if (entry is null) return null;
            entry.Claimed = true;
            active = entry;
            nextStart = now.AddSeconds(2);
            return entry.Request;
        }
    }

    public void Complete(string publisher, LiveCheckObservation observation)
    {
        lock (gate)
        {
            Cleanup();
            AuthorizeProvider(publisher);
            if (observation is null || !ReadModelSettings.ValidId(observation.RequestId))
                throw Fault("LIVE_RESULT_INVALID");
            if (!entries.TryGetValue(observation.RequestId, out var entry) || !entry.Claimed)
                throw Fault("LIVE_REQUEST_EXPIRED_OR_UNKNOWN");
            var request = entry.Request;
            if (observation.ObjectId != request.ObjectId || observation.Source != Source ||
                observation.Outcome is not ("ok" or "outOfScope" or "unavailable") ||
                observation.CheckedAtUtc < request.RequestedAtUtc || observation.CheckedAtUtc > clock.GetUtcNow() ||
                observation.Alerts.IsDefault || observation.Alerts.Length > settings.MaxAlerts ||
                observation.SourceRequests is < 0 or > 3 ||
                observation.Health is not (null or "healthy" or "warning" or "critical" or "unknown") ||
                observation.HealthObservedAtUtc > observation.CheckedAtUtc ||
                observation.HealthObservedAtUtc < request.RequestedAtUtc ||
                observation.Alerts.Any(a => a is null || !ReadModelSettings.ValidId(a.Id) ||
                    Guid.Parse(a.Id).ToString("D") != a.Id || a.ObjectId != request.ObjectId ||
                    a.Name is null || a.Name.Length > 256 || a.Severity is not ("information" or "warning" or "critical") ||
                    a.ResolutionState is < 0 or > 255 || a.ObservedAtUtc < request.RequestedAtUtc ||
                    a.ObservedAtUtc > observation.CheckedAtUtc) ||
                observation.Alerts.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != observation.Alerts.Length ||
                observation.Outcome == "ok" && (!observation.ScopeVerified || observation.SourceRequests < 1 ||
                    observation.ScopeCheckedAtUtc < request.RequestedAtUtc || observation.ScopeCheckedAtUtc > observation.CheckedAtUtc ||
                    observation.Health is null || observation.HealthObservedAtUtc is null ||
                    observation.HealthObservedAtUtc < observation.ScopeCheckedAtUtc ||
                    observation.Alerts.Any(a => a.ObservedAtUtc < observation.ScopeCheckedAtUtc)) ||
                observation.Outcome != "ok" && (observation.Health is not null || observation.HealthObservedAtUtc is not null ||
                    observation.Alerts.Length != 0 || observation.AlertsComplete))
                throw Fault("LIVE_RESULT_INVALID");
            if (request.ExpiresAtUtc <= clock.GetUtcNow())
            {
                // The worker posts only after joining its child. A late acknowledgement releases the lease but never delivers data.
                if (active == entry) active = null;
                entries.Remove(request.RequestId);
                return;
            }
            var encoded = Wire.Encode(observation);
            if (Encoding.UTF8.GetByteCount(encoded) > 32768) throw Fault("LIVE_RESULT_BUDGET");
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encoded)));
            if (entry.ResultDigest is not null && entry.ResultDigest != digest) throw Fault("LIVE_REPLAY_CONFLICT");
            entry.ResultDigest = digest;
            entry.Completion.TrySetResult(observation);
            if (active == entry) active = null;
            logger.LogInformation("Targeted check received. Event={Event} Utc={Utc} RequestId={RequestId} Outcome={Outcome}",
                "LIVE_RECEIVED", clock.GetUtcNow(), request.RequestId, observation.Outcome);
        }
    }

    private void AuthorizeProvider(string publisher)
    {
        if (!settings.Enabled) throw Fault("LIVE_NOT_ENABLED");
        if (publisher != settings.PublisherObjectId) throw Fault("LIVE_PUBLISHER_FORBIDDEN");
    }

    private void Cleanup()
    {
        var now = clock.GetUtcNow();
        admissions.RemoveAll(a => now - a.At >= TimeSpan.FromMinutes(1));
        foreach (var entry in entries.Values.Where(e => e.Request.ExpiresAtUtc <= now).ToArray())
        {
            entry.Completion.TrySetException(Fault("LIVE_TIMEOUT"));
            // Observe an abandoned task's exception while preserving it for remaining waiters.
            _ = entry.Completion.Task.Exception;
            if (active != entry) entries.Remove(entry.Request.RequestId);
        }
    }

    internal static QueryFault Fault(string code) => new(code, code);
}
