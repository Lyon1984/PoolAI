namespace PoolAI.Modules.Operations.Abstractions;

public sealed record InboxReceipt(
    string ConsumerName,
    EntityId MessageId,
    string Topic,
    long EventSequence,
    int SchemaVersion,
    ReadOnlyMemory<byte> PayloadHash);
