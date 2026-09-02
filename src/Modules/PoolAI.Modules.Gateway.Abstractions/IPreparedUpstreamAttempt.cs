namespace PoolAI.Modules.Gateway.Abstractions;

public interface IPreparedUpstreamAttempt : IAsyncDisposable
{
    ValueTask<Result<PreparedUpstreamRequest>> CreateRequestAsync(
        CancellationToken cancellationToken);

    ValueTask<Result<NormalizedUpstreamResult>> ParseResponseAsync(
        AdapterUpstreamResponse response,
        CancellationToken cancellationToken);
}
