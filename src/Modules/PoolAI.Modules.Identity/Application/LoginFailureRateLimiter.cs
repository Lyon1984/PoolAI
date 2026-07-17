#pragma warning disable MA0048 // The login failure limiter decision and port are cohesive.
namespace PoolAI.Modules.Identity.Application;

public enum LoginFailureRateLimitDisposition
{
    Allowed,
    Rejected,
    Unavailable,
}

public sealed record LoginFailureRateLimitDecision(
    LoginFailureRateLimitDisposition Disposition,
    long? RetryAfterSeconds);

public interface ILoginFailureRateLimiter
{
    ValueTask<LoginFailureRateLimitDecision> RecordFailureAsync(
        string ipAddress,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
