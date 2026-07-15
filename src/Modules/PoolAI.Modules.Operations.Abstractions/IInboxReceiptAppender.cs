namespace PoolAI.Modules.Operations.Abstractions;

public interface IInboxReceiptAppender
{
    ValueTask<InboxReceiptAppendResult> AppendAsync(
        InboxReceipt receipt,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
