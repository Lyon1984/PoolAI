namespace PoolAI.Modules.Operations.Abstractions;

public sealed class OutboxClaimRequest
{
    private readonly IReadOnlyList<string> _topics;

    public OutboxClaimRequest(
        EntityId owner,
        IEnumerable<string> topics,
        int maximumCount,
        TimeSpan leaseDuration)
    {
        if (owner.Value == Guid.Empty)
        {
            throw new ArgumentException("The claim owner cannot be empty.", nameof(owner));
        }

        ArgumentNullException.ThrowIfNull(topics);
        string[] canonicalTopics = topics
            .Select(static topic => ValidateTopic(topic))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        ArgumentOutOfRangeException.ThrowIfZero(canonicalTopics.Length, nameof(topics));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            leaseDuration,
            TimeSpan.Zero);

        Owner = owner;
        _topics = Array.AsReadOnly(canonicalTopics);
        MaximumCount = maximumCount;
        LeaseDuration = leaseDuration;
    }

    public EntityId Owner { get; }

    public IReadOnlyList<string> Topics => _topics;

    public int MaximumCount { get; }

    public TimeSpan LeaseDuration { get; }

    private static string ValidateTopic(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        if (!string.Equals(topic, topic.Trim(), StringComparison.Ordinal)
            || topic.Length > 128)
        {
            throw new ArgumentException(
                "Outbox topics must be canonical and bounded.",
                nameof(topic));
        }

        return topic;
    }
}
