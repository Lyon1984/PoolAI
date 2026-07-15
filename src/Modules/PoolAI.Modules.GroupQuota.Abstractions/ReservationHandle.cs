namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record ReservationHandle(
    EntityId ReservationId,
    EntityId PeriodId,
    DateTimeOffset LeaseExpiresAt);
