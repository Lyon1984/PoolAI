#pragma warning disable MA0048 // Account resource lifecycle is intentionally collocated.
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Supply.Domain;

internal enum AccountResourceStatus
{
    Active,
    Disabled,
    Retired,
}

internal sealed record AccountResource(
    EntityId Id,
    UpstreamProvider Provider,
    string Name,
    string UpstreamBaseUrl,
    string CredentialPrefix,
    AccountResourceStatus Status,
    AccountHealth Health,
    DateTimeOffset? UpstreamRateLimitedUntil,
    DateTimeOffset? LastHealthAt,
    int MaxConcurrency,
    int Priority,
    int Weight,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
#pragma warning restore MA0048
