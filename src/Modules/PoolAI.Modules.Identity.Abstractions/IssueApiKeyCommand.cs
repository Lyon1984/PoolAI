namespace PoolAI.Modules.Identity.Abstractions;

public sealed record IssueApiKeyCommand(
    EntityId UserId,
    EntityId GroupId,
    string Name,
    DateTimeOffset? ExpiresAt);
