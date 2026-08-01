namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record TokenEstimateSplit(
    long InputTokens,
    long OutputTokens);
