namespace PoolAI.Modules.Usage.Abstractions;

public sealed record PoolUsageSnapshot(
    EntityId GroupId,
    BigInteger InputTokens,
    BigInteger OutputTokens,
    BigInteger RequestCount,
    DateTimeOffset DataThrough);
