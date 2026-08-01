namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record TokenUsage(
    BigInteger InputTokens,
    BigInteger OutputTokens,
    BigInteger CacheReadTokens,
    BigInteger CacheCreationTokens,
    BigInteger ThinkingTokens)
{
    public BigInteger TotalTokens => InputTokens + OutputTokens;
}
