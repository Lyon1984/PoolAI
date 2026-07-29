using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Application.Ports;

internal interface IAccountCredentialStore
{
    ValueTask<AccountCredentialCreateResult> CreateAsync(
        AccountCredentialCreate account,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<AccountCredentialReplacementResult> ReplaceAsync(
        AccountCredentialReplacement replacement,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<AccountCredentialSnapshot>> SelectBatchAsync(
        EntityId? afterExclusive,
        int maximumCount,
        CancellationToken cancellationToken);

    ValueTask<AccountCredentialSnapshot?> FindAsync(
        EntityId accountId,
        CancellationToken cancellationToken);

    ValueTask<AccountCredentialRewrapWriteResult> TryRewrapAsync(
        AccountCredentialRewrapWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
