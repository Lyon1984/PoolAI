namespace PoolAI.Modules.Supply.Abstractions;

public sealed record AccountHealthTransition(
    EntityId AccountId,
    AccountHealth Health,
    DateTimeOffset ObservedAt,
    DateTimeOffset? RetryAt = null,
    long ExpectedAccountVersion = 0,
    long ExpectedCredentialRevision = 0);
