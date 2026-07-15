namespace PoolAI.Modules.Operations.Infrastructure;

internal sealed record RuntimeDependencyOptions(
    string RedisConnectionString,
    string RedisKeyPrefix,
    TimeSpan Timeout);
