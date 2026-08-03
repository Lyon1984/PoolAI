using System.Numerics;

namespace PoolAI.Modules.Usage.Worker;

internal sealed record QuotaReconciliationMetricSnapshot(
    BigInteger ReconciliationDeltaTokens,
    long AuthoritativeMismatchedGroups,
    long ProjectionMismatchedGroups,
    long DeliveryMismatchedGroups,
    long CounterVarianceLeakCandidates,
    long OverdueLeakCandidates,
    double OldestOverdueSeconds,
    BigInteger OverageTokens,
    BigInteger ReservedTokens,
    double UsageAggregationLagSeconds)
{
    internal static QuotaReconciliationMetricSnapshot Empty { get; } = new(
        BigInteger.Zero,
        0,
        0,
        0,
        0,
        0,
        0,
        BigInteger.Zero,
        BigInteger.Zero,
        0);
}
