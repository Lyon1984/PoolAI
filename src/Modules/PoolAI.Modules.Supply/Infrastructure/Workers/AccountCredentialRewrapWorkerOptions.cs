using Microsoft.Extensions.Configuration;

namespace PoolAI.Modules.Supply.Infrastructure.Workers;

internal sealed record AccountCredentialRewrapWorkerOptions(
    bool Enabled,
    int BatchSize,
    int MaxAttempts,
    TimeSpan RetryDelay)
{
    internal static AccountCredentialRewrapWorkerOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        int batchSize = configuration.GetValue(
            "Secrets:Envelope:Rewrap:BatchSize",
            100);
        int maxAttempts = configuration.GetValue(
            "Secrets:Envelope:Rewrap:MaxAttempts",
            3);
        int retryDelaySeconds = configuration.GetValue(
            "Secrets:Envelope:Rewrap:RetryDelaySeconds",
            5);
        if (batchSize is <= 0 or > 1000)
        {
            throw new InvalidOperationException(
                "Account credential rewrap batch size is invalid.");
        }

        if (maxAttempts is <= 0 or > 10)
        {
            throw new InvalidOperationException(
                "Account credential rewrap attempt count is invalid.");
        }

        if (retryDelaySeconds is <= 0 or > 60)
        {
            throw new InvalidOperationException(
                "Account credential rewrap retry delay is invalid.");
        }

        return new AccountCredentialRewrapWorkerOptions(
            configuration.GetValue(
                "Secrets:Envelope:Rewrap:Enabled",
                false),
            batchSize,
            maxAttempts,
            TimeSpan.FromSeconds(retryDelaySeconds));
    }

    public override string ToString() => nameof(AccountCredentialRewrapWorkerOptions);
}
