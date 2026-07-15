namespace PoolAI.Modules.Operations.Abstractions;

public enum InboxReceiptDisposition
{
    Inserted,
    Duplicate,
    MessageConflict,
    SequenceConflict,
}
