namespace PoolAI.Modules.Gateway.Application;

public sealed class GatewayEstimationOptions
{
    public const int DefaultOutputTokens = 4_096;
    public const long DefaultMaximumEstimatedTokens = 2_000_000;

    public GatewayEstimationOptions(
        int defaultMaxOutputTokens = DefaultOutputTokens,
        long maximumEstimatedTokensPerAttempt = DefaultMaximumEstimatedTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            defaultMaxOutputTokens,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumEstimatedTokensPerAttempt,
            defaultMaxOutputTokens);

        DefaultMaxOutputTokens = defaultMaxOutputTokens;
        MaximumEstimatedTokensPerAttempt = maximumEstimatedTokensPerAttempt;
    }

    public int DefaultMaxOutputTokens { get; }

    public long MaximumEstimatedTokensPerAttempt { get; }
}
