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
#pragma warning restore MA0048
