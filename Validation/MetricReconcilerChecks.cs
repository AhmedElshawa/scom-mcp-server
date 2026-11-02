using System.Collections.Immutable;
using ScomMcp;

static class MetricReconcilerChecks
{
    public static void Run()
    {
        var count = 0;
        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        const string objectId = "00000000-0000-0000-0000-000000000001";
        var scope = ImmutableHashSet.Create(objectId);
        ObservedMetric Point(DateTimeOffset time, double value) =>
            new(Guid.NewGuid().ToString("D"), objectId, "cpuPercent", "percent", time, value, "syntheticRule", true);
        var oldest = Point(now.AddDays(-14), 0);
        var earlier = Point(now.AddMinutes(-3), 10);
        var overlap = Point(now.AddMinutes(-2), 20);
        var removed = Point(now.AddMinutes(-1), 30);
        var latest = Point(now, 40);
        var scan = new MetricSeriesScan(objectId, "cpuPercent", "percent", "syntheticRule",
            overlap.ObservedAtUtc, now, true, null, [overlap with { Value = 25 }, latest, latest]);
        var reconciled = MetricReconciler.Reconcile([oldest, earlier, overlap, removed], [scan], scope, now, 10, 10);
        Check(reconciled.Coverage.Complete && reconciled.Observations.Length == 4 &&
            reconciled.Observations.Sum(m => m.Value) == 75, "inclusive overlap replaces corrections and removes absent values", ref count);
        Check(reconciled.Observations[0].Value == 0 && reconciled.Observations[0].ObservedAtUtc == now.AddDays(-14),
            "14-day inclusive zero retained", ref count);
        var repeated = MetricReconciler.Reconcile(reconciled.Observations, [scan], scope, now, 10, 10);
        Check(repeated.Observations.SequenceEqual(reconciled.Observations), "sample IDs stable across equivalent retries", ref count);
        var incomplete = MetricReconciler.Reconcile([oldest, earlier], [scan with { Complete = false, Reason = "PARTIAL_SOURCE" }],
            scope, now, 10, 10);
        Check(incomplete.Observations.IsEmpty && !incomplete.Coverage.Complete, "partial series excludes all observations not biased subset", ref count);
        var overflow = MetricReconciler.Reconcile([oldest, earlier], [scan], scope, now, 3, 3);
        Check(overflow.Observations.IsEmpty && overflow.ExcludedSeries.Length == 1, "whole-series count overflow excluded", ref count);
        var noScan = MetricReconciler.Reconcile([earlier], [], scope, now, 10, 10);
        Check(noScan.Observations.IsEmpty && !noScan.Coverage.Complete, "unassessed retained series not silently reused", ref count);
        var removedScope = MetricReconciler.Reconcile([earlier], [], ImmutableHashSet<string>.Empty, now, 10, 10);
        Check(removedScope.Observations.IsEmpty, "scope removal prunes prior metrics", ref count);
        Reject(() => MetricReconciler.Reconcile([], [scan with { Observations = [latest, latest with { Value = 99 }] }],
            scope, now, 10, 10), "conflicting observations in a scan reject", ref count);
        Reject(() => MetricReconciler.Reconcile([], [scan, scan], scope, now, 10, 10), "duplicate series rejects", ref count);
        Reject(() => MetricReconciler.Reconcile([], [scan], ImmutableHashSet<string>.Empty, now, 10, 10),
            "out-of-scope scan rejects", ref count);
        Reject(() => MetricReconciler.Reconcile([], [scan with { Observations = [latest with { Value = 101 }] }],
            scope, now, 10, 10), "invalid percentage rejects", ref count);
        Console.WriteLine($"PASS: {count} metric reconciliation assertions; inclusive overlap, raw corrections, whole-series limits and scope.");
    }
    private static void Check(bool result, string message, ref int count)
    {
        if (!result) throw new InvalidOperationException(message);
        count++;
    }
    private static void Reject(Action action, string message, ref int count)
    {
        try { action(); }
        catch (QueryFault) { count++; return; }
        throw new InvalidOperationException(message);
    }
}
