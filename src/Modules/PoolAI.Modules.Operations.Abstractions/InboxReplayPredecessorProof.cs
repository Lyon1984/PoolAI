namespace PoolAI.Modules.Operations.Abstractions;

public sealed record InboxReplayPredecessorProof
{
    public InboxReplayPredecessorProof(
        string consumerName,
        EntityId predecessorMessageId,
        string topic,
        int schemaVersion,
        ReadOnlyMemory<byte> payloadHash)
    {
        ValidateName(consumerName, nameof(consumerName));
        ValidateName(topic, nameof(topic));
        if (predecessorMessageId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "The replay predecessor message id cannot be empty.",
                nameof(predecessorMessageId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        if (payloadHash.Length != 32)
        {
            throw new ArgumentException(
                "The replay predecessor payload hash must contain 32 bytes.",
                nameof(payloadHash));
        }

        ConsumerName = consumerName;
        PredecessorMessageId = predecessorMessageId;
        Topic = topic;
        SchemaVersion = schemaVersion;
        PayloadHash = payloadHash.ToArray();
    }

    public string ConsumerName { get; }

    public EntityId PredecessorMessageId { get; }

    public string Topic { get; }

    public int SchemaVersion { get; }

    public ReadOnlyMemory<byte> PayloadHash { get; }

    private static void ValidateName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > 128)
        {
            throw new ArgumentException(
                "Inbox replay proof names must be canonical and bounded.",
                parameterName);
        }
    }
}
