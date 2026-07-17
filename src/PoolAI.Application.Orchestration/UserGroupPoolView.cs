using System.Numerics;
using PoolAI.BuildingBlocks;

namespace PoolAI.Application.Orchestration;

public sealed record UserGroupPoolView(
    EntityId GroupId,
    string GroupName,
    EntityId SubscriptionId,
    string PlanName,
    DateTimeOffset AccessExpiresAt,
    string QuotaStatus,
    BigInteger TotalTokens,
    BigInteger ConsumedTokens,
    BigInteger ReservedTokens,
    BigInteger RemainingTokens,
    DateTimeOffset UpdatedAt);
