using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PoolAI.Modules.Identity.Worker;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Identity.Infrastructure.Email;

internal sealed partial class EmailOutboxSenderService : BackgroundService
{
    private readonly IWorkerSessionLockProvider _lockProvider;
    private readonly EmailOutboxProcessor _processor;
    private readonly EmailOutboxMetrics _metrics;
    private readonly EmailOutboxWorkerOptions _options;
    private readonly ILogger<EmailOutboxSenderService> _logger;

    public EmailOutboxSenderService(
        IWorkerSessionLockProvider lockProvider,
        EmailOutboxProcessor processor,
        EmailOutboxMetrics metrics,
        EmailOutboxWorkerOptions options,
        ILogger<EmailOutboxSenderService> logger)
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
            WorkerJobs.EmailOutboxSender,
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
                EmailOutboxProcessResult result = await _processor.ProcessNextAsync(
                    jobLock,
                    cancellationToken).ConfigureAwait(false);
                if (result is not EmailOutboxProcessResult.Processed)
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
        EventId = 1101,
        Level = LogLevel.Warning,
        Message = "Email outbox sender cycle failed with {FailureType}.")]
    private static partial void LogCycleFailure(ILogger logger, string failureType);
}
