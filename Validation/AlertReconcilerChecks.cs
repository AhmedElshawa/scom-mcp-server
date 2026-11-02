using System.Collections.Immutable;
using ScomMcp;

static class AlertReconcilerChecks
{
    public static void Run()
    {
        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        const string objectId = "00000000-0000-0000-0000-000000000001";
        var ids = ImmutableHashSet.Create(objectId);
        var active = new ObservedAlert("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", objectId,
            "Synthetic alert", null, "warning", 0, now);
        var start = AlertReconciler.Reconcile(null, [active], ids, new(now, true, null), now, TimeSpan.FromDays(7), 10);
        Check(start.Alerts.Length == 1 && start.HistoryStartUtc == now && start.Coverage.Complete, "bootstrap starts observed history");
        var later = now.AddMinutes(5);
        var absent = AlertReconciler.Reconcile(start, [], ids, new(later, true, null), later, TimeSpan.FromDays(7), 10);
        Check(absent.Alerts[0].ResolutionState == 0 && !absent.Coverage.Complete &&
            absent.Coverage.Reason == "PRIOR_ALERTS_NOT_REASSESSED", "complete active-only absence never resolves prior alert");
        var partial = AlertReconciler.Reconcile(start, [], ids, new(later, false, "QUERY_INCOMPLETE"), later, TimeSpan.FromDays(7), 10);
        Check(partial.Alerts.Length == 1 && partial.Coverage.Reason == "QUERY_INCOMPLETE", "partial scan retains prior evidence");
        var resolved = active with { ResolutionState = 255, ObservedAtUtc = later };
        var updated = AlertReconciler.Reconcile(start, [resolved], ids, new(later, true, null), later, TimeSpan.FromDays(7), 10);
        Check(updated.Alerts[0].ResolutionState == 255 && updated.Coverage.Complete, "explicit observed resolution retained");
        var cutoff = later.AddDays(8);
        var expired = AlertReconciler.Reconcile(updated, [], ids, new(cutoff, true, null), cutoff, TimeSpan.FromDays(7), 10);
        Check(expired.Alerts.IsEmpty && expired.HistoryStartUtc == cutoff.AddDays(-7), "resolved retention bounded with honest history start");
        var removed = AlertReconciler.Reconcile(start, [], ImmutableHashSet<string>.Empty,
            new(later, true, null), later, TimeSpan.FromDays(7), 10);
        Check(removed.Alerts.IsEmpty, "authoritative scope removal stops serving outside alerts");
        Reject(() => AlertReconciler.Reconcile(start, [active with { ObservedAtUtc = now.AddSeconds(-1) }],
            ids, new(later, true, null), later, TimeSpan.FromDays(7), 10), "older observation refused");
        Reject(() => AlertReconciler.Reconcile(start, [active with { Severity = "critical" }],
            ids, new(later, true, null), later, TimeSpan.FromDays(7), 10), "same timestamp conflict refused");
        Reject(() => AlertReconciler.Reconcile(start, [active, active],
            ids, new(later, true, null), later, TimeSpan.FromDays(7), 10), "duplicate observations refused");
        Reject(() => AlertReconciler.Reconcile(null, [active], ImmutableHashSet<string>.Empty,
            new(now, true, null), now, TimeSpan.FromDays(7), 10), "outside scope observation refused");
        Reject(() => AlertReconciler.Reconcile(null, [active, active with { Id = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb" }],
            ids, new(now, true, null), now, TimeSpan.FromDays(7), 1), "budget overflow unavailable not truncated");
        Console.WriteLine("PASS: 11 alert reconciliation assertions; explicit resolution, partial absence, scope and bounded history.");
    }
    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (QueryFault) { return; }
        throw new InvalidOperationException(message);
    }
}
