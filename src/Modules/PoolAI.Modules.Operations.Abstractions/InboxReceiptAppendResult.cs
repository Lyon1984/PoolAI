namespace PoolAI.Modules.Operations.Abstractions;

public readonly record struct InboxReceiptAppendResult(InboxReceiptDisposition Disposition)
{
    public bool IsAccepted =>
        Disposition is InboxReceiptDisposition.Inserted or InboxReceiptDisposition.Duplicate;
}
