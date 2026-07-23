#pragma warning disable MA0048 // API Key mutation contracts form one cohesive public port surface.

namespace PoolAI.Modules.Identity.Abstractions;

public sealed record UpdateApiKeyCommand(
    EntityId RequestId,
    ApiKeyActor Actor,
    ApiKeyAccessMode AccessMode,
    EntityId UserId,
    EntityId ApiKeyId,
    string IdempotencyKey,
    long ExpectedVersion,
    bool SetName,
    string? Name,
    bool SetStatus,
    ApiKeyPersistentStatus? Status,
    bool SetExpiresAt,
    DateTimeOffset? ExpiresAt,
    bool SetAllowedCidrs,
    IReadOnlyList<string>? AllowedCidrs,
    string? Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record RevokeApiKeyCommand(
    EntityId RequestId,
    ApiKeyActor Actor,
    ApiKeyAccessMode AccessMode,
    EntityId UserId,
    EntityId ApiKeyId,
    string IdempotencyKey,
    long ExpectedVersion,
    string Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record RotateApiKeyCommand(
    EntityId RequestId,
    ApiKeyActor Actor,
    ApiKeyAccessMode AccessMode,
    EntityId UserId,
    EntityId ApiKeyId,
    string IdempotencyKey,
    long ExpectedVersion,
    string Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record ApiKeyUpdatedOutcome(
    int StatusCode,
    bool IsReplay,
    ApiKeyControlPlaneSnapshot ApiKey,
    string ETag);

public sealed record ApiKeyRevokedOutcome(
    int StatusCode,
    bool IsReplay,
    string ETag);

#pragma warning restore MA0048
