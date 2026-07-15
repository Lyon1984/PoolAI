namespace PoolAI.Modules.Operations.Infrastructure.Redis;

internal sealed record RedisScriptAsset(
    string Name,
    int LogicalVersion,
    string BodySha256,
    byte[] RedisSha1,
    string Body);
