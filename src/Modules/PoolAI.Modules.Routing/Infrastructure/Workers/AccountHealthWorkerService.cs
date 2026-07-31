using System.Data.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Worker;

namespace PoolAI.Modules.Routing.Infrastructure.Workers;

internal sealed partial class AccountHealthWorkerService(
    IWorkerSessionLockProvider lockProvider,
    AccountHealthProbeProcessor processor,
    AccountHealthWorkerOptions options,
    ISupplyHealthReadinessSummaryStore readiness,
    TimeProvider timeProvider,
    ILogger<AccountHealthWorkerService> logger) : BackgroundService
{
    private readonly IWorkerSessionLockProvider _lockProvider =
        lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
    private readonly AccountHealthProbeProcessor _processor =
        processor ?? throw new ArgumentNullException(nameof(processor));
    private readonly AccountHealthWorkerOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly ISupplyHealthReadinessSummaryStore _readiness =
        readiness ?? throw new ArgumentNullException(nameof(readiness));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<AccountHealthWorkerService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    internal ValueTask RunSingleRoundAsync(CancellationToken cancellationToken) =>
        RunRoundAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunRoundAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                SupplyHealthFailureCode failureCode = MapFailure(exception);
                _readiness.Update(
                    SupplyHealthReadinessSummaryStore.Empty(
                        _timeProvider.GetUtcNow(),
                        SupplyHealthCycleStatus.Failed,
                        failureCode));
                LogRoundFailure(
                    _logger,
                    FailureCode(failureCode));
            }

            try
            {
                await Task.Delay(_options.ProbeInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async ValueTask RunRoundAsync(CancellationToken cancellationToken)
    {
        IWorkerSessionLock? jobLock = await _lockProvider.TryAcquireAsync(
            WorkerJobs.SupplyHealth,
            cancellationToken).ConfigureAwait(false);
        if (jobLock is null)
        {
            _readiness.Update(
                SupplyHealthReadinessSummaryStore.Empty(
                    _timeProvider.GetUtcNow(),
                    SupplyHealthCycleStatus.Standby,
                    SupplyHealthFailureCode.NotOwner));
            LogAlreadyOwned(_logger);
            return;
        }

        await using (jobLock.ConfigureAwait(false))
        {
            _ = await _processor.ProcessAsync(
                jobLock,
                _options.MaximumConcurrency,
                _options.ProbeInterval,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static SupplyHealthFailureCode MapFailure(Exception exception) =>
        exception switch
        {
            DbException => SupplyHealthFailureCode.DependencyUnavailable,
            HttpRequestException or IOException or TimeoutException =>
                SupplyHealthFailureCode.UpstreamProbeFailed,
            InvalidOperationException =>
                SupplyHealthFailureCode.ContractFailure,
            _ => SupplyHealthFailureCode.UnexpectedFailure,
        };

    private static string FailureCode(SupplyHealthFailureCode failureCode) =>
        failureCode switch
        {
            SupplyHealthFailureCode.DependencyUnavailable =>
                "dependency_unavailable",
            SupplyHealthFailureCode.UpstreamProbeFailed =>
                "upstream_probe_failed",
            SupplyHealthFailureCode.ContractFailure => "contract_failure",
            SupplyHealthFailureCode.UnexpectedFailure =>
                "unexpected_failure",
            _ => "unexpected_failure",
        };

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Warning,
        Message = "Supply health probe round will retry after {FailureCode}.")]
    private static partial void LogRoundFailure(
        ILogger logger,
        string failureCode);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Debug,
        Message = "Supply health probe round is already owned by another Worker.")]
    private static partial void LogAlreadyOwned(ILogger logger);
}
