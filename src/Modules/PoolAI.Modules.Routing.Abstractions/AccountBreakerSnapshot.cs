namespace PoolAI.Modules.Routing.Abstractions;

public sealed record AccountBreakerSnapshot(
    AccountBreakerState State,
    long Samples,
    long Failures,
    long ConsecutiveFailures,
    DateTimeOffset? OpenUntil,
    AccountBreakerAction Action);
