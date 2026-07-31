using Microsoft.Extensions.Configuration;

namespace PoolAI.Modules.GroupQuota.Infrastructure;

internal sealed class QuotaMutationDenialRateLimitOptions
{
    internal const int DefaultAttemptsPerMinute = 5;
    internal const int MinimumAttemptsPerMinute = 1;
    internal const int MaximumAttemptsPerMinute = 20;

    internal QuotaMutationDenialRateLimitOptions(int attemptsPerMinute)
    {
        if (attemptsPerMinute is < MinimumAttemptsPerMinute
            or > MaximumAttemptsPerMinute)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptsPerMinute));
        }

        AttemptsPerMinute = attemptsPerMinute;
    }

    internal int AttemptsPerMinute { get; }

    internal static QuotaMutationDenialRateLimitOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        int attemptsPerMinute = configuration.GetValue(
            "Quota:DeniedMutationAttemptsPerMinute",
            DefaultAttemptsPerMinute);
        try
        {
            return new QuotaMutationDenialRateLimitOptions(attemptsPerMinute);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException(
                "Quota denial-rate-limit configuration is invalid.",
                exception);
        }
    }
}
