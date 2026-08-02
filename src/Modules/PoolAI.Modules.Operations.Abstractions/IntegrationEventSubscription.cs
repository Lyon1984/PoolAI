namespace PoolAI.Modules.Operations.Abstractions;

public sealed record IntegrationEventSubscription
{
    public IntegrationEventSubscription(
        string consumerName,
        string topic,
        int schemaVersion)
    {
        ConsumerName = ValidateName(consumerName, nameof(consumerName));
        Topic = ValidateName(topic, nameof(topic));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        SchemaVersion = schemaVersion;
    }

    public string ConsumerName { get; }

    public string Topic { get; }

    public int SchemaVersion { get; }

    private static string ValidateName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > 128)
        {
            throw new ArgumentException(
                "Integration event subscription names must be canonical and bounded.",
                parameterName);
        }

        return value;
    }
}
