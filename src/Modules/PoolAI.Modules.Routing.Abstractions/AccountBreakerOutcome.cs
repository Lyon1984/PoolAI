namespace PoolAI.Modules.Routing.Abstractions;

public enum AccountBreakerOutcome
{
    Success,
    TransientFailure,
    RateLimited,
    AuthenticationFailure,
    Ignored,
}
