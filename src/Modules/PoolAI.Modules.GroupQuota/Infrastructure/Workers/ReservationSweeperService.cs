using System.Data.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoolAI.Modules.GroupQuota.Worker;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Workers;

internal sealed partial class ReservationSweeperService : BackgroundService
{
    internal const int PageSize = 100;
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RoundBudget = TimeSpan.FromSeconds(25);

    private readonly IWorkerSessionLockProvider _lockProvider;
    private readonly ReservationSweepRound _processRound;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReservationSweeperService> _logger;

    public ReservationSweeperService(
        IWorkerSessionLockProvider lockProvider,
        ReservationSweeperProcessor processor,
        TimeProvider timeProvider,
        ILogger<ReservationSweeperService> logger)
        : this(
            lockProvider,
            async (jobLock, pageSize, cancellationToken) =>
            {
                _ = await processor.ProcessAsync(
                    jobLock,
                    pageSize,
                    cancellationToken).ConfigureAwait(false);
            },
            timeProvider,
            logger)
    {
        ArgumentNullException.ThrowIfNull(processor);
    }

    internal ReservationSweeperService(
        IWorkerSessionLockProvider lockProvider,
        ReservationSweepRound processRound,
        TimeProvider timeProvider,
        ILogger<ReservationSweeperService> logger)
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
        using PeriodicTimer cadence = new(SweepInterval, _timeProvider);
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

    private async ValueTask RunBudgetedRoundAsync(
        CancellationToken stoppingToken)
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
        catch (OperationCanceledException) when (
            stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            budgetCancellation.IsCancellationRequested)
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
            WorkerJobs.ReservationSweeper,
            cancellationToken).ConfigureAwait(false);
        if (jobLock is null)
        {
            LogAlreadyOwned(_logger);
            return;
        }

        await using (jobLock.ConfigureAwait(false))
        {
            await _processRound(
                jobLock,
                PageSize,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string MapFailure(Exception exception) => exception switch
    {
        ReservationSweepFailureException
            { Failure: QuotaLedgerFailure.DependencyUnavailable } =>
                "dependency_unavailable",
        ReservationSweepFailureException
            { Failure: QuotaLedgerFailure.TokenNumericOverflow } =>
                "quota_invariant_failure",
        ReservationSweepFailureException => "round_failure",
        DbException => "dependency_unavailable",
        IOException or TimeoutException => "transient_failure",
        InvalidOperationException => "round_failure",
        _ => "unexpected_failure",
    };

    [LoggerMessage(
        EventId = 2301,
        Level = LogLevel.Warning,
        Message = "Reservation sweep round will retry after {FailureCode}.")]
    private static partial void LogRoundFailure(
        ILogger logger,
        string failureCode);

    [LoggerMessage(
        EventId = 2302,
        Level = LogLevel.Debug,
        Message = "Reservation sweep round is already owned by another Worker.")]
    private static partial void LogAlreadyOwned(ILogger logger);
}
