using System.Collections.Immutable;
using ScomMcp;

static class ReconciledSourceChecks
{
    public static async Task Run(string root)
    {
        var count = 0;
        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        const string objectId = "00000000-0000-0000-0000-000000000001";
        const string publisher = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        const string mg = "22222222-2222-4222-8222-222222222222";
        var entity = new ObservedObject(objectId, "Synthetic.Entity", [], "synthetic", "unknown", now,
            ImmutableDictionary<string, string?>.Empty);
        var alert = new ObservedAlert("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", objectId, "synthetic alert", null, "warning", 0, now);
        var point = new ObservedMetric("cccccccc-cccc-4ccc-8ccc-cccccccccccc", objectId, "count", "count", now, 0, "rule", false);
        var series = new MetricSeriesScan(objectId, "count", "count", "rule", now.AddDays(-14), now, true, null, [point]);
        var scan = new NormalizedSourceScan(mg, "managementGroup", null, true, [objectId], [entity], [],
            new(now, true, null), [alert], new(now, true, null), [series], now);
        var profile = new Profile(scan with { MetricAssessment = new(now, false, "METRIC_PROFILE_NOT_ASSESSED") });
        var clock = new Clock { Now = now };
        var source = new ReconciledCollectionSource(profile, new() { BatchRecords = 1 }, clock);
        var reserved = await source.ReserveSessionAsync(CancellationToken.None);
        var first = await source.PrepareAsync(CancellationToken.None);
        var replay = await source.PrepareAsync(CancellationToken.None);
        Check(ReferenceEquals(first, replay) && profile.Calls == 1 && reserved == first.SessionId,
            "reserved pending dataset never recollected before acknowledgement", ref count);
        Check(profile.Request is { } bootstrap && bootstrap.PriorUnresolvedAlerts.IsEmpty &&
            bootstrap.RequestedAtUtc == now && bootstrap.MetricHistoryStartUtc == now.AddDays(-14),
            "bootstrap profile receives explicit raw-history window without invented alert history", ref count);
        Check(!first.Manifest.Metrics.Complete && first.Manifest.Metrics.Reason == "METRIC_PROFILE_NOT_ASSESSED",
            "explicit unassessed metric domain cannot become complete through empty reconciliation", ref count);
        var batches = await Read(first);
        Check(batches.Count == 3 && batches.Sum(b => b.Objects.Length + b.Alerts.Length + b.Metrics.Length) == 3,
            "normalized source assembles bounded inventory/alert/metric batches", ref count);
        var replayBytes = await Read(replay);
        Check(batches.Select(Wire.Encode).SequenceEqual(replayBytes.Select(Wire.Encode)), "replay serializes identical prepared batches", ref count);
        var store = new ReadModelStore(new()
        {
            Enabled = true, DatabasePath = Path.Combine(root, "reconciled-source.db"), ManagementGroupId = mg,
            ScopeKind = "managementGroup", PublisherObjectIds = [publisher],
            AllowedMetricsByClass = new() { ["Synthetic.Entity"] = new() { ["count"] = "count" } }
        }, new() { SnapshotEnabled = false }, clock);
        Commit(first, batches);
        await source.AcknowledgeAsync(first.SessionId, CancellationToken.None);
        await source.AcknowledgeAsync(first.SessionId, CancellationToken.None);
        var later = now.AddMinutes(1);
        clock.Now = later;
        profile.Scan = scan with
        {
            Objects = [],
            Inventory = new(later, false, "DISCOVERY_INCOMPLETE"),
            AlertObservations = [], Alerts = new(later, true, null),
            MetricSeries = [series with { StartUtc = now, EndUtc = later, Observations = [point] }],
            MetricsAsOfUtc = later
        };
        var partial = await source.PrepareAsync(CancellationToken.None);
        Check(profile.Request is { } subsequent && subsequent.PriorUnresolvedAlerts.Single() == new AlertReassessment(alert.Id, objectId),
            "subsequent profile receives prior unresolved alert identity and parent for scoped reassessment", ref count);
        var partialBatches = await Read(partial);
        Check(partial.Manifest.ExpectedObjectIds.Length == 1 && !partial.Manifest.Inventory.Complete &&
            partial.Manifest.Alerts.Reason == "ALERT_PARENT_DISCOVERY_INCOMPLETE" &&
            partial.Manifest.Metrics.Reason == "METRIC_PARENT_DISCOVERY_INCOMPLETE",
            "verified scope distinct from missing discovery/alert/metric parents", ref count);
        Check(partialBatches.Sum(b => b.Alerts.Length + b.Metrics.Length) == 0,
            "undiscovered object cannot produce orphan alert or sample rows", ref count);
        Commit(partial, partialBatches);
        await source.AcknowledgeAsync(partial.SessionId, CancellationToken.None);
        profile.Scan = scan with { ExpectedObjectIds = [], Objects = [], AlertObservations = [], MetricSeries = [],
            Inventory = new(later, true, null), Alerts = new(later, true, null), MetricsAsOfUtc = later };
        var removed = await source.PrepareAsync(CancellationToken.None);
        Commit(removed, await Read(removed));
        Check(store.Page(new("synthetic", ImmutableHashSet.Create(Wire.ScopeId)), "objects", null, 20, null).TotalCount == 0,
            "next committed verified removal no longer serves prior objects", ref count);
        await source.AcknowledgeAsync(removed.SessionId, CancellationToken.None);
        profile.Scan = scan with { MembershipVerified = false };
        var invalidAttempt = await source.ReserveSessionAsync(CancellationToken.None);
        store.Begin(publisher, invalidAttempt);
        await Reject(() => source.PrepareAsync(CancellationToken.None), "unverified source scope rejected before preparation", ref count);
        try
        {
            store.Page(new("synthetic", ImmutableHashSet.Create(Wire.ScopeId)), "objects", null, 20, null);
            throw new InvalidOperationException("Unverified latest attempt served prior success.");
        }
        catch (QueryFault error) when (error.Code == "SOURCE_UNAVAILABLE") { count++; }
        profile.Scan = scan with { ManagementGroupId = "33333333-3333-4333-8333-333333333333" };
        await Reject(() => source.PrepareAsync(CancellationToken.None), "scope identity change refuses old reconciliation state", ref count);
        profile.Scan = scan with { MetricsAsOfUtc = clock.Now.AddMinutes(1) };
        await Reject(() => source.PrepareAsync(CancellationToken.None), "future metric coverage rejected", ref count);
        Console.WriteLine($"PASS: {count} reconciled source assertions; stable preparation, bounded batches, store activation and scope changes.");

        void Commit(PreparedCollection prepared, List<IngestionBatch> values)
        {
            store.Begin(publisher, prepared.SessionId);
            for (var i = 0; i < values.Count; i++) store.Append(publisher, prepared.SessionId, i,
                System.Text.Encoding.UTF8.GetBytes(Wire.Encode(values[i])));
            store.Commit(publisher, prepared.SessionId, System.Text.Encoding.UTF8.GetBytes(Wire.Encode(prepared.Manifest)));
        }
    }
    private static async Task<List<IngestionBatch>> Read(PreparedCollection prepared)
    {
        var batches = new List<IngestionBatch>();
        await foreach (var batch in prepared.Batches) batches.Add(batch);
        return batches;
    }
    private static void Check(bool condition, string message, ref int count)
    {
        if (!condition) throw new InvalidOperationException(message);
        count++;
    }
    private static Task Reject(Func<Task> action, string message, ref int count)
    {
        count++;
        return Verify();
        async Task Verify()
        {
            try { await action(); }
            catch (QueryFault) { return; }
            throw new InvalidOperationException(message);
        }
    }
    private sealed class Profile(NormalizedSourceScan scan) : IScomNormalizationProfile
    {
        public NormalizedSourceScan Scan = scan;
        public int Calls;
        public SourceCollectionRequest? Request;
        public Task<NormalizedSourceScan> CollectAsync(SourceCollectionRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            Calls++;
            return Task.FromResult(Scan);
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
