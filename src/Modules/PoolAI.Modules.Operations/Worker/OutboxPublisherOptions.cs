using Microsoft.Extensions.Configuration;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Worker;

internal sealed class OutboxPublisherOptions
{
    internal OutboxPublisherOptions(
        int maximumAttempts,
        TimeSpan pollInterval,
        TimeSpan claimDuration,
        TimeSpan retryBaseDelay,
        TimeSpan retryMaximumDelay)
    {
        if (maximumAttempts is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        Positive(pollInterval, nameof(pollInterval));
        Positive(claimDuration, nameof(claimDuration));
        MaximumAttempts = maximumAttempts;
        PollInterval = pollInterval;
        ClaimDuration = claimDuration;
        HeartbeatInterval = TimeSpan.FromTicks(claimDuration.Ticks / 3);
        RetryPolicy = new DeliveryRetryPolicy(
            maximumAttempts,
            retryBaseDelay,
            retryMaximumDelay);
    }

    internal int MaximumAttempts { get; }

    internal TimeSpan PollInterval { get; }

    internal TimeSpan ClaimDuration { get; }

    internal TimeSpan HeartbeatInterval { get; }

    internal DeliveryRetryPolicy RetryPolicy { get; }

    internal static OutboxPublisherOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        int pollSeconds = configuration.GetValue("Outbox:PollSeconds", 1);
        int claimSeconds = configuration.GetValue("Outbox:ClaimSeconds", 30);
        int retryBaseSeconds = configuration.GetValue("Outbox:RetryBaseSeconds", 1);
        int retryMaximumSeconds = configuration.GetValue("Outbox:RetryMaxSeconds", 300);
        if (pollSeconds is < 1 or > 30
            || claimSeconds is < 10 or > 300
            || retryBaseSeconds is < 1 or > 86_400
            || retryMaximumSeconds is < 1 or > 86_400)
        {
            throw new InvalidOperationException("Outbox Worker timing is invalid.");
        }

        try
        {
            return new OutboxPublisherOptions(
                configuration.GetValue("Outbox:MaxAttempts", 12),
                TimeSpan.FromSeconds(pollSeconds),
                TimeSpan.FromSeconds(claimSeconds),
                TimeSpan.FromSeconds(retryBaseSeconds),
                TimeSpan.FromSeconds(retryMaximumSeconds));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "Outbox Worker configuration is invalid.",
                exception);
        }
    }

    private static void Positive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
