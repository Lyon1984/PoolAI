namespace PoolAI.Modules.Operations.Abstractions;

public enum CoordinationBreakerOutcome
{
    Success,
    TransientFailure,
    RateLimited,
    AuthFailure,
    Ignored,
}
