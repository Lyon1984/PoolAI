using Microsoft.Extensions.Configuration;

namespace PoolAI.Modules.Identity.Infrastructure;

internal sealed class PasswordResetRateLimitOptions
{
    internal PasswordResetRateLimitOptions(
        byte[] scopePepper,
        int ipRequestsPerMinute,
        int accountRequestsPerMinute)
    {
        ScopePepper = scopePepper;
        IpRequestsPerMinute = ipRequestsPerMinute;
        AccountRequestsPerMinute = accountRequestsPerMinute;
    }

    internal byte[] ScopePepper { get; }

    internal int IpRequestsPerMinute { get; }

    internal int AccountRequestsPerMinute { get; }

    internal static PasswordResetRateLimitOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        byte[] scopePepper;
        try
        {
            scopePepper = Convert.FromBase64String(
                configuration["Auth:PasswordReset:RateLimitScopePepper"]
                    ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Auth:PasswordReset:RateLimitScopePepper is invalid.",
                exception);
        }

        int ipLimit = configuration.GetValue(
            "Auth:PasswordReset:IpRequestsPerMinute",
            5);
        int accountLimit = configuration.GetValue(
            "Auth:PasswordReset:AccountRequestsPerMinute",
            3);
        if (scopePepper.Length < 32 || ipLimit is < 1 or > 60 || accountLimit is < 1 or > 20)
        {
            throw new InvalidOperationException("Password-reset rate-limit configuration is invalid.");
        }

        return new PasswordResetRateLimitOptions(scopePepper, ipLimit, accountLimit);
    }
}
