using System.Numerics;

namespace PoolAI.Modules.Usage.Application;

internal sealed record UsageHourlyAggregate(
    long RequestCount,
    long AttemptCount,
    long FailureCount,
    long FailoverCount,
    long EstimatedAttemptCount,
    BigInteger InputTokens,
    BigInteger OutputTokens,
    BigInteger CacheCreationTokens,
    BigInteger CacheReadTokens,
    BigInteger ThinkingTokens)
{
    internal BigInteger TotalTokens => InputTokens + OutputTokens;
}
