namespace PoolAI.Modules.Supply.Abstractions;

public sealed record AccountHealthState(
    AccountHealth Health,
    DateTimeOffset? RetryAt,
    DateTimeOffset? ObservedAt,
    long Version);
