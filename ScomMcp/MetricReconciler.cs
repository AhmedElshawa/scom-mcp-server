using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ScomMcp;

public sealed record MetricSeriesScan(string ObjectId, string Metric, string Unit, string RuleId,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool Complete, string? Reason,
    ImmutableArray<ObservedMetric> Observations);
public sealed record MetricReconciliation(ImmutableArray<ObservedMetric> Observations,
    DomainObservation Coverage, ImmutableArray<string> ExcludedSeries);

public static class MetricReconciler
{
    public static MetricReconciliation Reconcile(ImmutableArray<ObservedMetric> previous,
        ImmutableArray<MetricSeriesScan> scans, IReadOnlySet<string> expectedObjectIds,
        DateTimeOffset asOfUtc, int maxSeriesSamples, int maxTotalSamples)
    {
        ArgumentNullException.ThrowIfNull(expectedObjectIds);
        if (previous.IsDefault || scans.IsDefault || asOfUtc == default || asOfUtc.Offset != TimeSpan.Zero ||
            maxSeriesSamples is < 1 or > 100000 || maxTotalSamples < maxSeriesSamples || maxTotalSamples > 1000000 ||
            expectedObjectIds.Any(id => !Canonical(id)))
            throw new QueryFault("SOURCE_UNAVAILABLE", "METRIC_RECONCILIATION_INPUT_INVALID");
        var cutoff = asOfUtc.AddDays(-14);
        var prior = new Dictionary<(string Object, string Metric, string Rule), Dictionary<DateTimeOffset, ObservedMetric>>();
        foreach (var observation in previous)
        {
            ValidateObservation(observation);
            if (!expectedObjectIds.Contains(observation.ObjectId) || observation.ObservedAtUtc < cutoff) continue;
            var key = (observation.ObjectId, observation.Metric, observation.RuleId);
            if (!prior.TryGetValue(key, out var values)) prior[key] = values = [];
            if (values.TryGetValue(observation.ObservedAtUtc, out var old) && !Equivalent(old, observation))
                throw new QueryFault("SOURCE_UNAVAILABLE", "METRIC_PRIOR_CONFLICT");
            values[observation.ObservedAtUtc] = observation;
        }
        var assessed = new HashSet<(string Object, string Metric, string Rule)>();
        var output = new List<ObservedMetric>();
        var excluded = new List<string>();
        foreach (var scan in scans.OrderBy(s => s?.ObjectId, StringComparer.Ordinal)
            .ThenBy(s => s?.Metric, StringComparer.Ordinal).ThenBy(s => s?.RuleId, StringComparer.Ordinal))
        {
            if (scan is null || !Canonical(scan.ObjectId) || !expectedObjectIds.Contains(scan.ObjectId) ||
                string.IsNullOrWhiteSpace(scan.Metric) || scan.Metric.Length > 128 ||
                string.IsNullOrWhiteSpace(scan.Unit) || scan.Unit.Length > 64 ||
                string.IsNullOrWhiteSpace(scan.RuleId) || scan.RuleId.Length > 500 ||
                scan.StartUtc.Offset != TimeSpan.Zero || scan.EndUtc.Offset != TimeSpan.Zero ||
                scan.StartUtc < cutoff || scan.StartUtc > scan.EndUtc || scan.EndUtc > asOfUtc ||
                scan.Observations.IsDefault || (scan.Complete ? scan.Reason is not null : string.IsNullOrWhiteSpace(scan.Reason)))
                throw new QueryFault("SOURCE_UNAVAILABLE", "METRIC_SERIES_INVALID");
            var key = (scan.ObjectId, scan.Metric, scan.RuleId);
            if (!assessed.Add(key)) throw new QueryFault("SOURCE_UNAVAILABLE", "METRIC_SERIES_DUPLICATE");
            var fresh = new Dictionary<DateTimeOffset, ObservedMetric>();
            foreach (var observation in scan.Observations)
            {
                ValidateObservation(observation);
                if (observation.ObjectId != scan.ObjectId || observation.Metric != scan.Metric ||
                    observation.Unit != scan.Unit || observation.RuleId != scan.RuleId ||
                    observation.ObservedAtUtc < scan.StartUtc || observation.ObservedAtUtc > scan.EndUtc)
                    throw new QueryFault("SOURCE_UNAVAILABLE", "METRIC_SERIES_OBSERVATION_MISMATCH");
                if (fresh.TryGetValue(observation.ObservedAtUtc, out var duplicate) && !Equivalent(duplicate, observation))
                    throw new QueryFault("SOURCE_UNAVAILABLE", "METRIC_SERIES_CONFLICT");
                fresh[observation.ObservedAtUtc] = observation;
            }
            var label = SeriesLabel(key);
            if (!scan.Complete)
            {
                excluded.Add(label);
                continue;
            }
            var merged = new Dictionary<DateTimeOffset, ObservedMetric>();
            if (prior.TryGetValue(key, out var retained))
                foreach (var entry in retained)
                {
                    if (entry.Value.Unit != scan.Unit)
                        throw new QueryFault("SOURCE_UNAVAILABLE", "METRIC_UNIT_REGRESSION");
                    // A complete inclusive overlap supersedes old observations,
                    // including corrected or removed source values in that range.
                    if (entry.Key < scan.StartUtc || entry.Key > scan.EndUtc) merged.Add(entry.Key, entry.Value);
                }
            foreach (var entry in fresh) merged[entry.Key] = entry.Value;
            if (merged.Count > maxSeriesSamples || output.Count + merged.Count > maxTotalSamples)
            {
                excluded.Add(label);
                continue;
            }
            output.AddRange(merged.Values.OrderBy(m => m.ObservedAtUtc).Select(m => m with { Id = ObservationId(m) }));
        }
        // Unassessed retained series are deliberately not used as current data.
        foreach (var key in prior.Keys.Where(key => !assessed.Contains(key)))
            excluded.Add(SeriesLabel(key));
        var limitations = excluded.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        return new(output.ToImmutableArray(), new(asOfUtc, limitations.IsEmpty,
            limitations.IsEmpty ? null : "METRIC_SERIES_INCOMPLETE_OR_BUDGET_EXCLUDED"), limitations);

        void ValidateObservation(ObservedMetric? value)
        {
            if (value is null || !Canonical(value.ObjectId) || !Canonical(value.Id) ||
                string.IsNullOrWhiteSpace(value.Metric) || value.Metric.Length > 128 ||
                string.IsNullOrWhiteSpace(value.Unit) || value.Unit.Length > 64 ||
                string.IsNullOrWhiteSpace(value.RuleId) || value.RuleId.Length > 500 ||
                !double.IsFinite(value.Value) || value.Unit == "percent" && value.Value is < 0 or > 100 ||
                value.ObservedAtUtc == default || value.ObservedAtUtc.Offset != TimeSpan.Zero ||
                value.ObservedAtUtc > asOfUtc)
                throw new QueryFault("SOURCE_UNAVAILABLE", "METRIC_OBSERVATION_INVALID");
        }
    }
    private static bool Equivalent(ObservedMetric a, ObservedMetric b) =>
        a.Unit == b.Unit && a.Value == b.Value && a.Optimized == b.Optimized;
    private static bool Canonical(string? id) => ReadModelSettings.ValidId(id) && Guid.Parse(id!).ToString("D") == id;
    private static string SeriesLabel((string Object, string Metric, string Rule) key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Wire.Encode(new
        { objectId = key.Object, metric = key.Metric, ruleId = key.Rule }))));
    private static string ObservationId(ObservedMetric sample)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Wire.Encode(new
        { sample.ObjectId, sample.Metric, sample.RuleId, sample.ObservedAtUtc })));
        return new Guid(bytes.AsSpan(0, 16)).ToString("D");
    }
}
