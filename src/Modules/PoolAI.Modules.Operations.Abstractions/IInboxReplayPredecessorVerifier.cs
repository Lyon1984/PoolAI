namespace PoolAI.Modules.Operations.Abstractions;

public interface IInboxReplayPredecessorVerifier
{
    ValueTask<bool> HasExactReceiptAsync(
        InboxReplayPredecessorProof proof,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
