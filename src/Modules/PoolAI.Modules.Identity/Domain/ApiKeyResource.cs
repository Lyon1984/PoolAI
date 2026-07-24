#pragma warning disable MA0048 // API Key persistence records form one cohesive internal model.
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Modules.Identity.Domain;

internal sealed record ApiKeyResource(
    EntityId Id,
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
    DateTimeOffset ObservedAt)
{
    internal ApiKeyControlPlaneSnapshot ToSnapshot() => new(
        Id,
        UserId,
        GroupId,
        Name,
        Prefix,
        Status,
        EffectiveStatus,
        ExpiresAt,
        AllowedCidrs.ToArray(),
        LastUsedAt,
        Version,
        CreatedAt,
        UpdatedAt,
        ObservedAt);
}

internal sealed record ApiKeyCursor(
    DateTimeOffset CreatedAt,
    EntityId Id);

internal sealed record ApiKeySlice(
    IReadOnlyList<ApiKeyResource> ApiKeys,
    bool HasMore);

internal sealed record ApiKeyCreateWrite(
    EntityId ApiKeyId,
    EntityId UserId,
    EntityId GroupId,
    string Name,
    string Prefix,
    byte[] SecretHash,
    short PepperVersion,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> AllowedCidrs);

internal enum ApiKeyCreateDisposition
{
    Created,
    Conflict,
    ValidationFailed,
}

internal sealed record ApiKeyCreateResult(
    ApiKeyCreateDisposition Disposition,
    ApiKeyResource? ApiKey);

internal sealed record ApiKeyUpdateWrite(
    EntityId ApiKeyId,
    EntityId UserId,
    EntityId ExpectedGroupId,
    long ExpectedVersion,
    ApiKeyEffectiveStatus ExpectedEffectiveStatus,
    bool SetName,
    string? Name,
    bool SetStatus,
    ApiKeyPersistentStatus? Status,
    bool SetExpiresAt,
    DateTimeOffset? ExpiresAt,
    bool SetAllowedCidrs,
    IReadOnlyList<string>? AllowedCidrs);

internal enum ApiKeyUpdateDisposition
{
    Updated,
    NotFound,
    Revoked,
    VersionConflict,
    ResourceConflict,
    ValidationFailed,
}

internal sealed record ApiKeyUpdateResult(
    ApiKeyUpdateDisposition Disposition,
    bool Changed,
    long? CurrentVersion,
    ApiKeyResource? ApiKey);

internal sealed record ApiKeyRevokeWrite(
    EntityId ApiKeyId,
    EntityId UserId,
    long ExpectedVersion,
    string Reason);

internal enum ApiKeyRevokeDisposition
{
    Revoked,
    NotFound,
    AlreadyRevoked,
    VersionConflict,
    ValidationFailed,
}

internal sealed record ApiKeyRevokeResult(
    ApiKeyRevokeDisposition Disposition,
    long? CurrentVersion,
    ApiKeyResource? ApiKey);

internal sealed record ApiKeyRotateWrite(
    EntityId ApiKeyId,
    EntityId UserId,
    EntityId ExpectedGroupId,
    long ExpectedVersion,
    EntityId NewApiKeyId,
    string NewPrefix,
    byte[] NewSecretHash,
    short NewPepperVersion,
    string Reason);

internal enum ApiKeyRotateDisposition
{
    Rotated,
    NotFound,
    Revoked,
    VersionConflict,
    ResourceConflict,
    Conflict,
    ValidationFailed,
}

internal sealed record ApiKeyRotateResult(
    ApiKeyRotateDisposition Disposition,
    long? OldCurrentVersion,
    ApiKeyResource? OldApiKey,
    ApiKeyResource? NewApiKey);
#pragma warning restore MA0048
