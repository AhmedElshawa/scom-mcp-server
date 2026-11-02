using System.Collections.Immutable;

namespace ScomMcp;

public sealed record AlertReconciliation(ImmutableArray<ObservedAlert> Alerts, DomainObservation Coverage,
    DateTimeOffset HistoryStartUtc);

public static class AlertReconciler
{
    public static AlertReconciliation Reconcile(AlertReconciliation? previous, ImmutableArray<ObservedAlert> observations,
        IReadOnlySet<string> expectedObjectIds, DomainObservation scan, DateTimeOffset now,
        TimeSpan resolvedRetention, int maxAlerts)
    {
        ArgumentNullException.ThrowIfNull(expectedObjectIds);
        ArgumentNullException.ThrowIfNull(scan);
        if (now.Offset != TimeSpan.Zero || now == default || scan.AsOfUtc.Offset != TimeSpan.Zero ||
            scan.AsOfUtc == default || scan.AsOfUtc > now || resolvedRetention < TimeSpan.Zero ||
            resolvedRetention > TimeSpan.FromDays(365) || maxAlerts is < 1 or > 100000 ||
            observations.IsDefault || (scan.Complete ? scan.Reason is not null : string.IsNullOrWhiteSpace(scan.Reason)) ||
            expectedObjectIds.Any(id => !CanonicalId(id)))
            throw new QueryFault("SOURCE_UNAVAILABLE", "ALERT_RECONCILIATION_INPUT_INVALID");
        if (previous is not null && (scan.AsOfUtc < previous.Coverage.AsOfUtc ||
            previous.Alerts.IsDefault || previous.HistoryStartUtc == default ||
            previous.HistoryStartUtc.Offset != TimeSpan.Zero || previous.HistoryStartUtc > scan.AsOfUtc))
            throw new QueryFault("SOURCE_UNAVAILABLE", "ALERT_RECONCILIATION_TIME_REGRESSION");

        var rows = new Dictionary<string, ObservedAlert>(StringComparer.Ordinal);
        if (previous is not null)
            foreach (var alert in previous.Alerts)
            {
                Validate(alert);
                if (expectedObjectIds.Contains(alert.ObjectId) && !rows.TryAdd(alert.Id, alert))
                    throw new QueryFault("SOURCE_UNAVAILABLE", "ALERT_RECONCILIATION_DUPLICATE");
            }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alert in observations)
        {
            Validate(alert);
            if (!expectedObjectIds.Contains(alert.ObjectId) || !seen.Add(alert.Id))
                throw new QueryFault("SOURCE_UNAVAILABLE", "ALERT_RECONCILIATION_OUTSIDE_SCOPE_OR_DUPLICATE");
            if (rows.TryGetValue(alert.Id, out var existing))
            {
                if (existing.ObjectId != alert.ObjectId || alert.ObservedAtUtc < existing.ObservedAtUtc ||
                    alert.ObservedAtUtc == existing.ObservedAtUtc && existing != alert)
                    throw new QueryFault("SOURCE_UNAVAILABLE", "ALERT_RECONCILIATION_CONFLICT");
            }
            rows[alert.Id] = alert;
        }
        // SCOM resolution state 255 is resolved. Absence, even from an
        // active-only complete scan, is not an explicit resolution observation.
        var unresolvedAbsent = rows.Values.Any(a => a.ResolutionState != 255 && !seen.Contains(a.Id));
        var retained = rows.Values.Where(a => a.ResolutionState != 255 ||
            scan.AsOfUtc - a.ObservedAtUtc <= resolvedRetention).OrderBy(a => a.Id, StringComparer.Ordinal).ToImmutableArray();
        if (retained.Length > maxAlerts)
            throw new QueryFault("SOURCE_UNAVAILABLE", "ALERT_RECONCILIATION_BUDGET");
        var coverage = !scan.Complete ? scan :
            unresolvedAbsent ? new(scan.AsOfUtc, false, "PRIOR_ALERTS_NOT_REASSESSED") : scan;
        var historyStart = previous?.HistoryStartUtc ?? scan.AsOfUtc;
        var cutoff = scan.AsOfUtc - resolvedRetention;
        if (cutoff > historyStart) historyStart = cutoff;
        return new(retained, coverage, historyStart);

        void Validate(ObservedAlert? alert)
        {
            if (alert is null || !CanonicalId(alert.Id) || !CanonicalId(alert.ObjectId) ||
                alert.ObservedAtUtc == default || alert.ObservedAtUtc.Offset != TimeSpan.Zero ||
                alert.ObservedAtUtc > scan.AsOfUtc || alert.ResolutionState is < 0 or > 255 ||
                alert.Severity is not ("information" or "warning" or "critical") ||
                string.IsNullOrWhiteSpace(alert.Name) || alert.Name.Length > 512 || alert.Description?.Length > 2000)
                throw new QueryFault("SOURCE_UNAVAILABLE", "ALERT_RECONCILIATION_RECORD_INVALID");
        }
    }

    private static bool CanonicalId(string? id) =>
        ReadModelSettings.ValidId(id) && Guid.Parse(id!).ToString("D") == id;
}
