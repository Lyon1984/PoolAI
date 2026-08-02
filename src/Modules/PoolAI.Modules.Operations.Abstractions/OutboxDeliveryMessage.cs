namespace PoolAI.Modules.Operations.Abstractions;

public sealed record OutboxDeliveryMessage
{
    public OutboxDeliveryMessage(
        OutboxMessageEnvelope envelope,
        string partitionKey,
        long? partitionSequence,
        bool lineageAlreadyPublished = false)
    {
        Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        if (!string.Equals(partitionKey, partitionKey.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The partition key must be canonical.",
                nameof(partitionKey));
        }

        if (partitionSequence is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionSequence));
        }

        PartitionKey = partitionKey;
        PartitionSequence = partitionSequence;
        LineageAlreadyPublished = lineageAlreadyPublished;
    }

    public OutboxMessageEnvelope Envelope { get; }

    public string PartitionKey { get; }

    public long? PartitionSequence { get; }

    public bool LineageAlreadyPublished { get; }
}
