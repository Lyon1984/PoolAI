namespace PoolAI.Modules.Usage.Abstractions;

public sealed record UsageAggregationClaimResult(
    UsageAggregationClaimDisposition Disposition,
    UsageAggregationLease? Lease)
{
    public static UsageAggregationClaimResult Acquired(UsageAggregationLease lease) =>
        new(UsageAggregationClaimDisposition.Acquired, lease);

    public static UsageAggregationClaimResult Busy { get; } =
        new(UsageAggregationClaimDisposition.Busy, null);
}
