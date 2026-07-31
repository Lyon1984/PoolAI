namespace PoolAI.Modules.Supply.Abstractions;

public enum AccountHealthProbeOutcome
{
    Success,
    TransientFailure,
    RateLimited,
    AuthenticationFailure,
    Ignored,
}
