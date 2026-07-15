namespace PoolAI.Modules.Usage.Abstractions;

public sealed record AccountUsageSnapshot(
    EntityId GroupId,
    EntityId AccountId,
    BigInteger InputTokens,
    BigInteger OutputTokens,
    BigInteger AttemptCount,
    DateTimeOffset DataThrough);
