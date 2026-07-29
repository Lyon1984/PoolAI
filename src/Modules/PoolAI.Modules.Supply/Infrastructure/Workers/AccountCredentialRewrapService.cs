using System.Data.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Worker;

namespace PoolAI.Modules.Supply.Infrastructure.Workers;

internal sealed partial class AccountCredentialRewrapService : BackgroundService
{
    private readonly IWorkerSessionLockProvider _lockProvider;
    private readonly AccountCredentialRewrapProcessor _processor;
    private readonly AccountCredentialRewrapWorkerOptions _options;
    private readonly ILogger<AccountCredentialRewrapService> _logger;

    public AccountCredentialRewrapService(
        IWorkerSessionLockProvider lockProvider,
        AccountCredentialRewrapProcessor processor,
        AccountCredentialRewrapWorkerOptions options,
        ILogger<AccountCredentialRewrapService> logger)
    {
        _lockProvider = lockProvider
            ?? throw new ArgumentNullException(nameof(lockProvider));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                AccountCredentialRewrapProcessDisposition? disposition =
                    await RunOwnedAttemptAsync(stoppingToken).ConfigureAwait(false);
                if (disposition is null)
                {
                    attempt--;
                    await Task.Delay(_options.RetryDelay, stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (disposition is
                    AccountCredentialRewrapProcessDisposition.Completed)
                {
                    return;
                }

                if (attempt >= _options.MaxAttempts)
                {
                    throw new InvalidOperationException(
                        "Account credential rewrap lost worker ownership.");
                }

                LogRetry(_logger, attempt, "ownership_lost");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                IsTransient(exception) && attempt < _options.MaxAttempts)
            {
                LogRetry(_logger, attempt, exception.GetType().Name);
            }

            try
            {
                await Task.Delay(_options.RetryDelay, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async ValueTask<AccountCredentialRewrapProcessDisposition?>
        RunOwnedAttemptAsync(CancellationToken cancellationToken)
    {
        IWorkerSessionLock? jobLock = await _lockProvider.TryAcquireAsync(
            WorkerJobs.AccountCredentialRewrap,
            cancellationToken).ConfigureAwait(false);
        if (jobLock is null)
        {
            LogAlreadyOwned(_logger);
            return null;
        }

        await using (jobLock.ConfigureAwait(false))
        {
            AccountCredentialRewrapProcessResult result = await _processor
                .ProcessAsync(jobLock, _options.BatchSize, cancellationToken)
                .ConfigureAwait(false);
            return result.Disposition;
        }
    }

    private static bool IsTransient(Exception exception) =>
        exception is DbException or IOException or TimeoutException;

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "Account credential rewrap attempt {Attempt} will retry after {FailureType}.")]
    private static partial void LogRetry(
        ILogger logger,
        int attempt,
        string failureType);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Information,
        Message = "Account credential rewrap is already owned by another Worker.")]
    private static partial void LogAlreadyOwned(ILogger logger);
}
