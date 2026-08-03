using System.Diagnostics.Metrics;
using System.Numerics;
using PoolAI.Modules.Usage.Worker;

namespace PoolAI.Modules.Usage.Infrastructure.Observability;

internal sealed class QuotaReconciliationMetrics : IDisposable
{
    internal const string MeterName = "PoolAI.Usage.QuotaReconciliation";
    private const string DefaultGroupTier = "default";
    private const string WorkerName = "quota-reconciliation";
    private readonly Meter _meter = new(MeterName);
    private QuotaReconciliationMetricSnapshot _snapshot =
        QuotaReconciliationMetricSnapshot.Empty;

    public QuotaReconciliationMetrics()
    {
        _meter.CreateObservableGauge(
            "poolai_quota_reconciliation_delta_tokens",
            ObserveDeltaTokens,
            unit: "{token}",
            description: "Absolute authoritative and checkpoint-aligned quota deltas.");
        _meter.CreateObservableGauge(
            "poolai_quota_reconciliation_mismatched_groups",
            ObserveMismatchedGroups,
            unit: "{group}",
            description: "Groups with reconciliation discrepancies by fixed layer kind.");
        _meter.CreateObservableGauge(
            "poolai_quota_reservation_leak_candidates",
            ObserveLeakCandidates,
            unit: "{reservation}",
            description: "Reservation leak candidates by fixed classification.");
        _meter.CreateObservableGauge(
            "poolai_quota_reservation_oldest_overdue_seconds",
            ObserveOldestOverdue,
            unit: "s",
            description: "Oldest overdue pending reservation age.");
        _meter.CreateObservableGauge(
            "poolai_quota_overage_tokens",
            ObserveOverageTokens,
            unit: "{token}",
            description: "Non-negative quota overage Tokens.");
        _meter.CreateObservableGauge(
            "poolai_quota_reserved_tokens",
            ObserveReservedTokens,
            unit: "{token}",
            description: "Current reserved quota Tokens.");
        _meter.CreateObservableGauge(
            "poolai_usage_aggregation_lag_seconds",
            ObserveUsageLag,
            unit: "s",
            description: "Oldest observed Usage aggregation lag.");
    }

    internal void Publish(QuotaReconciliationMetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _snapshot, snapshot);
    }

    internal static double ToFiniteDouble(BigInteger value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        double converted = (double)value;
        return double.IsPositiveInfinity(converted) ? double.MaxValue : converted;
    }

    public void Dispose() => _meter.Dispose();

    private Measurement<double> ObserveDeltaTokens() => new(
        ToFiniteDouble(Volatile.Read(ref _snapshot).ReconciliationDeltaTokens),
        new KeyValuePair<string, object?>("group_tier", DefaultGroupTier));

    private IEnumerable<Measurement<long>> ObserveMismatchedGroups()
    {
        QuotaReconciliationMetricSnapshot snapshot = Volatile.Read(ref _snapshot);
        return
        [
            Kind(snapshot.AuthoritativeMismatchedGroups, "authoritative"),
            Kind(snapshot.ProjectionMismatchedGroups, "projection"),
            Kind(snapshot.DeliveryMismatchedGroups, "delivery"),
        ];
    }

    private IEnumerable<Measurement<long>> ObserveLeakCandidates()
    {
        QuotaReconciliationMetricSnapshot snapshot = Volatile.Read(ref _snapshot);
        return
        [
            Kind(snapshot.CounterVarianceLeakCandidates, "counter_variance"),
            Kind(snapshot.OverdueLeakCandidates, "overdue"),
        ];
    }

    private Measurement<double> ObserveOldestOverdue() => new(
        Volatile.Read(ref _snapshot).OldestOverdueSeconds,
        new KeyValuePair<string, object?>("group_tier", DefaultGroupTier));

    private Measurement<double> ObserveOverageTokens() => new(
        ToFiniteDouble(Volatile.Read(ref _snapshot).OverageTokens),
        new KeyValuePair<string, object?>("group_tier", DefaultGroupTier));

    private Measurement<double> ObserveReservedTokens() => new(
        ToFiniteDouble(Volatile.Read(ref _snapshot).ReservedTokens),
        new KeyValuePair<string, object?>("group_tier", DefaultGroupTier));

    private Measurement<double> ObserveUsageLag() => new(
        Volatile.Read(ref _snapshot).UsageAggregationLagSeconds,
        new KeyValuePair<string, object?>("worker", WorkerName));

    private static Measurement<long> Kind(long value, string kind) => new(
        value,
        new KeyValuePair<string, object?>("kind", kind));
}
