#pragma warning disable MA0048 // API Key control-plane contracts form one cohesive public port surface.

namespace PoolAI.Modules.Identity.Abstractions;

public enum ApiKeyAccessMode
{
    Self,
    AdminProxy,
}

public enum ApiKeyPersistentStatus
{
    Active,
    Disabled,
    Revoked,
}

public enum ApiKeyEffectiveStatus
{
    Active,
    Disabled,
    Expired,
    Revoked,
}

public enum ApiKeyAccessDecisionKind
{
    Authorized,
    SubscriptionRequired,
    SubscriptionInactive,
}

public sealed record ApiKeyActor(
    EntityId UserId,
    SystemRole Role,
    long TokenVersion);

public sealed record ListApiKeysQuery(
    ApiKeyActor Actor,
    ApiKeyAccessMode AccessMode,
    EntityId UserId,
    string? Cursor,
    int Limit = 50);

public sealed record GetApiKeyQuery(
    ApiKeyActor Actor,
    ApiKeyAccessMode AccessMode,
    EntityId UserId,
    EntityId ApiKeyId);

public sealed record CreateApiKeyCommand(
    EntityId RequestId,
    ApiKeyActor Actor,
    ApiKeyAccessMode AccessMode,
    EntityId UserId,
    EntityId GroupId,
    string IdempotencyKey,
    string Name,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> AllowedCidrs,
    string? Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record ApiKeyAccessDecision(
    ApiKeyAccessDecisionKind Kind,
    EntityId UserId,
    EntityId GroupId,
    EntityId? SubscriptionId,
    DateTimeOffset? ObservedAt);

public sealed record ApiKeyControlPlaneSnapshot(
    EntityId ApiKeyId,
    EntityId UserId,
    EntityId GroupId,
    string Name,
    string Prefix,
    ApiKeyPersistentStatus Status,
    ApiKeyEffectiveStatus EffectiveStatus,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> AllowedCidrs,
    DateTimeOffset? LastUsedAt,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ObservedAt);

public sealed record ApiKeyPage(
    IReadOnlyList<ApiKeyControlPlaneSnapshot> Data,
    string? NextCursor,
    bool HasMore);

public sealed record ApiKeyCreatedOutcome(
    int StatusCode,
    bool IsReplay,
    ApiKeyControlPlaneSnapshot ApiKey,
    string Secret,
    string ETag,
    string Location);

#pragma warning restore MA0048
