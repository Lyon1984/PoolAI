using System.Numerics;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Usage.Application;

internal sealed record UsageReconciliationProjectionSnapshot(
    EntityId GroupId,
    EntityId PeriodId,
    BigInteger ProjectedConsumedTokens,
    long CheckpointSourceEventSequence,
    DateTimeOffset? DataThrough,
    DateTimeOffset CheckedAt);
