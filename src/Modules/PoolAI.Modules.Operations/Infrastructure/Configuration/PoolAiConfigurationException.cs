namespace PoolAI.Modules.Operations.Infrastructure.Configuration;

public sealed class PoolAiConfigurationException(IReadOnlyList<string> invalidKeys)
    : InvalidOperationException(
        $"Invalid or missing configuration keys: {string.Join(", ", invalidKeys)}.")
{
    public IReadOnlyList<string> InvalidKeys { get; } = invalidKeys;
}
