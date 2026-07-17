using Microsoft.Extensions.Configuration;

namespace PoolAI.Modules.Identity.Infrastructure;

internal sealed class LoginFailureRateLimitOptions
{
    internal LoginFailureRateLimitOptions(byte[] scopePepper, int failuresPerMinute)
    {
        ArgumentNullException.ThrowIfNull(scopePepper);
        if (scopePepper.Length < 32)
        {
            throw new ArgumentException(
                "The login rate-limit pepper must contain at least 256 bits.",
                nameof(scopePepper));
        }

        if (failuresPerMinute is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(failuresPerMinute));
        }

        ScopePepper = scopePepper;
        FailuresPerMinute = failuresPerMinute;
    }

    internal byte[] ScopePepper { get; }

    internal int FailuresPerMinute { get; }

    internal static LoginFailureRateLimitOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        byte[] pepper;
        try
        {
            pepper = Convert.FromBase64String(
                configuration["Auth:Login:RateLimitScopePepper"] ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Auth:Login:RateLimitScopePepper is invalid.",
                exception);
        }

        int limit = configuration.GetValue("Auth:Login:IpFailuresPerMinute", 20);
        try
        {
            return new LoginFailureRateLimitOptions(pepper, limit);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "Login failure rate-limit configuration is invalid.",
                exception);
        }
    }
}
