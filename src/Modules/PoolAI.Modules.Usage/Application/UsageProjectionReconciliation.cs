using System.Numerics;

namespace PoolAI.Modules.Usage.Application;

internal sealed record UsageProjectionReconciliation(
    UsageProjectionReconciliationStatus Status,
    BigInteger ExpectedConsumedTokens,
    BigInteger ProjectedConsumedTokens,
    BigInteger ConsumedVariance,
    long CheckpointSourceEventSequence,
    long LatestSourceEventSequence,
    DateTimeOffset? DataThrough);
