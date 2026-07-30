using PoolAI.Modules.Operations.Abstractions;
using StackExchange.Redis;

namespace PoolAI.Modules.Operations.Infrastructure.Redis;

internal sealed class RedisCoordinationValueStore(
    RedisConnectionProvider connections,
    RuntimeDependencyOptions options) : ICoordinationValueStore
{
    private static readonly TimeSpan StickyTimeToLive = TimeSpan.FromMinutes(60);
    private readonly RedisConnectionProvider _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly RuntimeDependencyOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<CoordinationValueReadResult> GetAndRefreshAsync(
        string keyBase,
        TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        Validate(keyBase, timeToLive);
        try
        {
            ConnectionMultiplexer connection = await _connections
                .GetAsync(cancellationToken)
                .ConfigureAwait(false);
            IDatabase database = connection.GetDatabase();
            RedisKey key = FullKey(keyBase);
            RedisValue value = await database
                .StringGetAsync(key)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!value.HasValue)
            {
                return CoordinationValueReadResult.Missing;
            }

            bool refreshed = await database
                .KeyExpireAsync(key, timeToLive)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return refreshed
                ? CoordinationValueReadResult.Found(value.ToString())
                : CoordinationValueReadResult.Missing;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return CoordinationValueReadResult.Unavailable;
        }
    }

    public async ValueTask<CoordinationValueWriteResult> SetAsync(
        string keyBase,
        string value,
        TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        Validate(keyBase, timeToLive);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 1_024)
        {
            throw new ArgumentException("The coordination value is too large.", nameof(value));
        }

        try
        {
            ConnectionMultiplexer connection = await _connections
                .GetAsync(cancellationToken)
                .ConfigureAwait(false);
            bool stored = await connection
                .GetDatabase()
                .StringSetAsync(FullKey(keyBase), value, timeToLive)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return stored
                ? CoordinationValueWriteResult.Stored
                : CoordinationValueWriteResult.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return CoordinationValueWriteResult.Unavailable;
        }
    }

    private RedisKey FullKey(string keyBase) => $"{_options.RedisKeyPrefix}{keyBase}";

    private static void Validate(string keyBase, TimeSpan timeToLive)
    {
        RedisCoordinationKeyGuard.ValidateSticky(keyBase);
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            timeToLive,
            StickyTimeToLive);
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is RedisException
            or TimeoutException
            or InvalidOperationException;
}
