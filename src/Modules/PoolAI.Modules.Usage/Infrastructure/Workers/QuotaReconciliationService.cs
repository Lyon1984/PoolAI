using System.Data.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage.Worker;

namespace PoolAI.Modules.Usage.Infrastructure.Workers;

internal sealed partial class QuotaReconciliationService : BackgroundService
{
    internal const int PageSize = 100;
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RoundBudget = TimeSpan.FromSeconds(25);

    private readonly IWorkerSessionLockProvider _lockProvider;
    private readonly QuotaReconciliationScanRound _processRound;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QuotaReconciliationService> _logger;

    public QuotaReconciliationService(
        IWorkerSessionLockProvider lockProvider,
        QuotaReconciliationProcessor processor,
        TimeProvider timeProvider,
        ILogger<QuotaReconciliationService> logger)
        : this(
            lockProvider,
            processor.ProcessAsync,
            timeProvider,
            logger)
    {
        ArgumentNullException.ThrowIfNull(processor);
    }

    internal QuotaReconciliationService(
        IWorkerSessionLockProvider lockProvider,
        QuotaReconciliationScanRound processRound,
        TimeProvider timeProvider,
        ILogger<QuotaReconciliationService> logger)
    {
        _lockProvider = lockProvider
            ?? throw new ArgumentNullException(nameof(lockProvider));
        _processRound = processRound
            ?? throw new ArgumentNullException(nameof(processRound));
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal ValueTask RunSingleRoundAsync(CancellationToken cancellationToken) =>
        RunRoundAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer cadence = new(ScanInterval, _timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunBudgetedRoundAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                _ = await cadence.WaitForNextTickAsync(stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async ValueTask RunBudgetedRoundAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource budgetCancellation = new(
            RoundBudget,
            _timeProvider);
        using CancellationTokenSource roundCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                budgetCancellation.Token);
        try
        {
            await RunRoundAsync(roundCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (budgetCancellation.IsCancellationRequested)
        {
            LogRoundFailure(_logger, "round_budget_exhausted");
        }
        catch (Exception exception)
        {
            LogRoundFailure(_logger, MapFailure(exception));
        }
    }

    private async ValueTask RunRoundAsync(CancellationToken cancellationToken)
    {
        IWorkerSessionLock? jobLock = await _lockProvider.TryAcquireAsync(
            WorkerJobs.QuotaReconciliation,
            cancellationToken).ConfigureAwait(false);
        if (jobLock is null)
        {
            LogAlreadyOwned(_logger);
            return;
        }

        await using (jobLock.ConfigureAwait(false))
        {
            _ = await _processRound(
                jobLock,
                PageSize,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string MapFailure(Exception exception) => exception switch
    {
        DbException => "dependency_unavailable",
        IOException or TimeoutException => "transient_failure",
        InvalidOperationException => "reconciliation_invariant_failure",
        _ => "unexpected_failure",
    };

    [LoggerMessage(
        EventId = 2502,
        Level = LogLevel.Warning,
        Message = "Quota reconciliation round will retry after {FailureCode}.")]
    private static partial void LogRoundFailure(ILogger logger, string failureCode);

    [LoggerMessage(
        EventId = 2503,
        Level = LogLevel.Debug,
        Message = "Quota reconciliation round is owned by another Worker.")]
    private static partial void LogAlreadyOwned(ILogger logger);
}
