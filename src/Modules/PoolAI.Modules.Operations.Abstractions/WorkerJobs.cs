namespace PoolAI.Modules.Operations.Abstractions;

public static class WorkerJobs
{
    public static WorkerJobIdentity ReservationSweeper { get; } =
        new("poolai:r1:worker:reservation-sweeper:v1");

    public static WorkerJobIdentity OutboxPublisher { get; } =
        new("poolai:r1:worker:outbox-publisher:v1");

    public static WorkerJobIdentity UsageAggregator { get; } =
        new("poolai:r1:worker:usage-aggregator:v1");

    public static WorkerJobIdentity UsageRebuild { get; } =
        new("poolai:r1:worker:usage-rebuild:v1");

    public static WorkerJobIdentity EmailOutboxSender { get; } =
        new("poolai:r1:worker:email-outbox-sender:v1");

    public static WorkerJobIdentity SupplyHealth { get; } =
        new("poolai:r1:worker:supply-health:v1");

    public static WorkerJobIdentity OperationsAlerts { get; } =
        new("poolai:r1:worker:operations-alerts:v1");
}
