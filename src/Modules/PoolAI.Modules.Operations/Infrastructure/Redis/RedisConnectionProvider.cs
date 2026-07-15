using StackExchange.Redis;

namespace PoolAI.Modules.Operations.Infrastructure.Redis;

internal sealed class RedisConnectionProvider : IAsyncDisposable
{
    private readonly Lock _sync = new();
    private readonly ConfigurationOptions _configuration;
    private Task<ConnectionMultiplexer>? _connectionTask;
    private bool _disposed;

    public RedisConnectionProvider(RuntimeDependencyOptions options)
    {
        _configuration = ConfigurationOptions.Parse(options.RedisConnectionString);
        _configuration.AbortOnConnectFail = false;
        int timeoutMilliseconds = checked((int)options.Timeout.TotalMilliseconds);
        _configuration.ConnectTimeout = timeoutMilliseconds;
        _configuration.AsyncTimeout = timeoutMilliseconds;
        _configuration.SyncTimeout = timeoutMilliseconds;
    }

    public async ValueTask<ConnectionMultiplexer> GetAsync(
        CancellationToken cancellationToken)
    {
        Task<ConnectionMultiplexer> connectionTask;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            connectionTask = _connectionTask ??= ConnectionMultiplexer.ConnectAsync(_configuration);
        }

        try
        {
            return await connectionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!connectionTask.IsCompleted || connectionTask.IsCompletedSuccessfully)
            {
                throw;
            }

            lock (_sync)
            {
                if (ReferenceEquals(_connectionTask, connectionTask))
                {
                    _connectionTask = null;
                }
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<ConnectionMultiplexer>? connectionTask;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connectionTask = _connectionTask;
            _connectionTask = null;
        }

        if (connectionTask is null)
        {
            return;
        }

        try
        {
            ConnectionMultiplexer connection = await connectionTask.ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (RedisException)
        {
            // A failed connection task owns no usable multiplexer to dispose.
        }
    }
}
