using StackExchange.Redis;

namespace PoolAI.Modules.Operations.Infrastructure.Redis;

internal sealed class RedisScriptEvaluator(
    RedisConnectionProvider connections,
    RedisScriptRegistry registry,
    ReleaseManifestV1 releaseManifest)
{
    private readonly RedisConnectionProvider _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly RedisScriptRegistry _registry =
        registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly ReleaseManifestV1 _releaseManifest =
        releaseManifest ?? throw new ArgumentNullException(nameof(releaseManifest));

    public async ValueTask<RedisResult> EvaluateAsync(
        RedisScriptAsset script,
        RedisKey[] keys,
        RedisValue[] values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(values);

        ConnectionMultiplexer connection = await _connections
            .GetAsync(cancellationToken)
            .ConfigureAwait(false);
        IDatabase database = connection.GetDatabase();
        try
        {
            return await database
                .ScriptEvaluateAsync(script.RedisSha1, keys, values)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RedisServerException exception) when (
            exception.Message.StartsWith("NOSCRIPT", StringComparison.Ordinal))
        {
            await _registry.EnsureLoadedAsync(
                connection,
                _releaseManifest.Redis.RequiredServerMajor,
                cancellationToken).ConfigureAwait(false);
            return await database
                .ScriptEvaluateAsync(script.RedisSha1, keys, values)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
