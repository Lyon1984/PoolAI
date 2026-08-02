namespace PoolAI.Modules.Usage.Application;

internal sealed record AccountUsageHourProjection(
    EntityId AccountId,
    UsageHourlyAggregate Aggregate);
