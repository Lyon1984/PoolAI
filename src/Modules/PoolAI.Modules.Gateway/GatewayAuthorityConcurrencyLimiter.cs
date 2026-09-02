using System.Collections.Concurrent;

namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// Enforces the configured connection budget across all attempts targeting the
/// same canonical authority. Authorities originate from canonical Supply
/// configuration rather than request input, so retaining one bounded entry per
/// configured authority avoids removal races without creating a request-keyed
/// cache.
/// </summary>
internal sealed class GatewayAuthorityConcurrencyLimiter
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _limits =
        new(StringComparer.Ordinal);
    private readonly int _maximumConcurrency;

    internal GatewayAuthorityConcurrencyLimiter(int maximumConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrency, 1);
        _maximumConcurrency = maximumConcurrency;
    }

    internal async ValueTask<IDisposable> AcquireAsync(
        Uri destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        string authority = GatewayOutboundTransport.CanonicalAuthority(
            destination);
        SemaphoreSlim limit = _limits.GetOrAdd(
            authority,
            _ => new SemaphoreSlim(
                _maximumConcurrency,
                _maximumConcurrency));
        await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(limit);
    }

    private sealed class Lease(SemaphoreSlim limit) : IDisposable
    {
        private SemaphoreSlim? _limit = limit;

        public void Dispose() => Interlocked.Exchange(ref _limit, null)?.Release();
    }
}
