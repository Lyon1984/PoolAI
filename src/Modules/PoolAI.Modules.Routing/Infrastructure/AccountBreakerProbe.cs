using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Application;

namespace PoolAI.Modules.Routing.Infrastructure;

internal sealed class AccountBreakerProbe(
    AccountCircuitBreaker owner,
    EntityId accountId,
    string ownerToken,
    DateTimeOffset expiresAt) : IAccountBreakerProbe
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AccountCircuitBreaker _owner =
        owner ?? throw new ArgumentNullException(nameof(owner));
    private readonly string _ownerToken =
        ownerToken ?? throw new ArgumentNullException(nameof(ownerToken));
    private bool _completed;

    public EntityId AccountId { get; } = accountId;

    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    public async ValueTask<Result<AccountBreakerSnapshot>> CompleteAsync(
        AccountBreakerProbeCompletion completion,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_completed)
            {
                return Result.Failure<AccountBreakerSnapshot>(
                    "account_probe_not_owned",
                    "The Account half-open probe has already completed.",
                    retryAfterSeconds: 1);
            }

            Result<AccountBreakerSnapshot> result = await _owner
                .CompleteProbeAsync(
                    AccountId,
                    _ownerToken,
                    completion,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess
                || string.Equals(
                    result.Error.Code,
                    "account_probe_not_owned",
                    StringComparison.Ordinal))
            {
                _completed = true;
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _completed = true;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
