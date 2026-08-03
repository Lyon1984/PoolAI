namespace PoolAI.Modules.Usage.Application.Ports;

internal interface IBoundedUsageProjectionWriter
{
    ValueTask ReplaceOrDeleteAsync(
        EntityId groupId,
        EntityId periodId,
        DateTimeOffset bucketStart,
        UsageHourProjection? projection,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
