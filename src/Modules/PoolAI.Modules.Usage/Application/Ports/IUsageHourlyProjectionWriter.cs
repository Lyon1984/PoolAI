namespace PoolAI.Modules.Usage.Application.Ports;

internal interface IUsageHourlyProjectionWriter
{
    ValueTask ReplaceAsync(
        UsageHourProjection projection,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
