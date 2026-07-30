namespace PoolAI.Modules.Supply.Abstractions;

public sealed record AccountHealthProbeCandidate(
    EntityId AccountId,
    AccountHealth Health,
    int ConcurrencyLimit,
    DateTimeOffset? RetryAt,
    DateTimeOffset? LastCheckedAt,
    long AccountVersion,
    long CredentialRevision,
    bool IsActive);
