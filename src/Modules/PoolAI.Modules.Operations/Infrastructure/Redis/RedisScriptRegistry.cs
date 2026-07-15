using StackExchange.Redis;

namespace PoolAI.Modules.Operations.Infrastructure.Redis;

internal sealed class RedisScriptRegistry(RedisScriptCatalog catalog)
{
    public async ValueTask EnsureLoadedAsync(
        ConnectionMultiplexer connection,
        int requiredServerMajor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        IServer[] primaries = connection
            .GetEndPoints(configuredOnly: false)
            .Distinct()
            .Select(endpoint => connection.GetServer(endpoint))
            .Where(static server => server.IsConnected && !server.IsReplica)
            .ToArray();
        if (primaries.Length == 0)
        {
            throw new RedisConnectionException(
                ConnectionFailureType.UnableToResolvePhysicalConnection,
                "No connected Redis primary is available.");
        }

        foreach (IServer primary in primaries)
        {
            if (primary.Version.Major != requiredServerMajor)
            {
                throw new RedisManifestIncompatibleException();
            }

            foreach (RedisScriptAsset script in catalog.Scripts)
            {
                await EnsureLoadedOnceAsync(primary, script, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask EnsureLoadedOnceAsync(
        IServer server,
        RedisScriptAsset script,
        CancellationToken cancellationToken)
    {
        bool exists = await server
            .ScriptExistsAsync(script.RedisSha1)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        byte[] loadedSha1 = await server
            .ScriptLoadAsync(script.Body)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!loadedSha1.AsSpan().SequenceEqual(script.RedisSha1))
        {
            throw new RedisManifestIncompatibleException();
        }

        bool loaded = await server
            .ScriptExistsAsync(script.RedisSha1)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!loaded)
        {
            throw new RedisManifestIncompatibleException();
        }
    }
}
