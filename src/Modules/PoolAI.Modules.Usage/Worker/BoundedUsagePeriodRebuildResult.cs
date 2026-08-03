using System.Numerics;

namespace PoolAI.Modules.Usage.Worker;

internal sealed record BoundedUsagePeriodRebuildResult(
    BoundedUsagePeriodRebuildDisposition Disposition,
    long CheckpointSourceEventSequence,
    int RebuiltBucketCount,
    BigInteger RemainingProjectionVariance);
