#pragma warning disable MA0048 // The limiter decision and port are one cohesive contract.
namespace PoolAI.Modules.Identity.Application;

public enum PasswordResetRateLimitDisposition
{
    Allowed,
    Rejected,
    Unavailable,
}

public sealed record PasswordResetRateLimitDecision(
    PasswordResetRateLimitDisposition Disposition,
    long? RetryAfterSeconds);

public interface IPasswordResetRateLimiter
{
    ValueTask<PasswordResetRateLimitDecision> CheckForgotAsync(
        string ipAddress,
        string normalizedAccount,
        CancellationToken cancellationToken);

    ValueTask<PasswordResetRateLimitDecision> CheckAdminAsync(
        string normalizedAccount,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
