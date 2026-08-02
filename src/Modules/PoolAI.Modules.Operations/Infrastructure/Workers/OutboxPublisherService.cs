using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure.Observability;
using PoolAI.Modules.Operations.Worker;

namespace PoolAI.Modules.Operations.Infrastructure.Workers;

internal sealed partial class OutboxPublisherService : BackgroundService
{
    private readonly IWorkerSessionLockProvider _lockProvider;
    private readonly OutboxPublisherProcessor _processor;
    private readonly OutboxPublisherMetrics _metrics;
    private readonly OutboxPublisherOptions _options;
    private readonly ILogger<OutboxPublisherService> _logger;

    public OutboxPublisherService(
        IWorkerSessionLockProvider lockProvider,
        OutboxPublisherProcessor processor,
        OutboxPublisherMetrics metrics,
        OutboxPublisherOptions options,
        ILogger<OutboxPublisherService> logger)
    {
        _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOwnedCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is
                NpgsqlException or IOException or TimeoutException)
            {
                LogCycleFailure(_logger, exception.GetType().Name);
            }

            await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RunOwnedCycleAsync(CancellationToken cancellationToken)
    {
        IWorkerSessionLock? jobLock = await _lockProvider.TryAcquireAsync(
            WorkerJobs.OutboxPublisher,
            cancellationToken).ConfigureAwait(false);
        if (jobLock is null)
        {
            return;
        }

        await using (jobLock.ConfigureAwait(false))
        {
            bool processedAny = false;
            await _metrics.RefreshIfDueAsync(
                force: false,
                cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                OutboxPublishProcessResult result = await _processor.ProcessNextAsync(
                    jobLock,
                    cancellationToken).ConfigureAwait(false);
                if (result is not OutboxPublishProcessResult.Processed)
                {
                    await _metrics.RefreshIfDueAsync(
                        force: processedAny,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                processedAny = true;
                await _metrics.RefreshIfDueAsync(
                    force: false,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Warning,
        Message = "Integration Event Outbox publisher cycle failed with {FailureType}.")]
    private static partial void LogCycleFailure(ILogger logger, string failureType);
}
