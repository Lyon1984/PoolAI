using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage.Worker;

namespace PoolAI.Modules.Usage.Infrastructure.Workers;

internal sealed partial class UsageProjectionRebuildOneShotService : BackgroundService
{
    private readonly IWorkerSessionLockProvider _lockProvider;
    private readonly UsageProjectionRebuildRun _rebuild;
    private readonly UsageProjectionRebuildWorkerOptions _options;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly Action<int> _setExitCode;
    private readonly ILogger<UsageProjectionRebuildOneShotService> _logger;

    public UsageProjectionRebuildOneShotService(
        IWorkerSessionLockProvider lockProvider,
        UsagePeriodProjectionRebuilder rebuilder,
        UsageProjectionRebuildWorkerOptions options,
        IHostApplicationLifetime applicationLifetime,
        ILogger<UsageProjectionRebuildOneShotService> logger)
        : this(
            lockProvider,
            rebuilder.RebuildAsync,
            options,
            applicationLifetime,
            static exitCode => Environment.ExitCode = exitCode,
            logger)
    {
        ArgumentNullException.ThrowIfNull(rebuilder);
    }

    internal UsageProjectionRebuildOneShotService(
        IWorkerSessionLockProvider lockProvider,
        UsageProjectionRebuildRun rebuild,
        UsageProjectionRebuildWorkerOptions options,
        IHostApplicationLifetime applicationLifetime,
        Action<int> setExitCode,
        ILogger<UsageProjectionRebuildOneShotService> logger)
    {
        _lockProvider = lockProvider
            ?? throw new ArgumentNullException(nameof(lockProvider));
        _rebuild = rebuild ?? throw new ArgumentNullException(nameof(rebuild));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _applicationLifetime = applicationLifetime
            ?? throw new ArgumentNullException(nameof(applicationLifetime));
        _setExitCode = setExitCode ?? throw new ArgumentNullException(nameof(setExitCode));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            BoundedUsagePeriodRebuildResult result = await RunOnceAsync(stoppingToken)
                .ConfigureAwait(false);
            bool completed = result.Disposition
                is BoundedUsagePeriodRebuildDisposition.Completed;
            _setExitCode(completed ? 0 : 1);
            LogResult(
                _logger,
                result.Disposition,
                result.RebuiltBucketCount,
                result.CheckpointSourceEventSequence);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _setExitCode(1);
            LogFailure(_logger, "cancelled");
        }
        catch (Exception exception)
        {
            _setExitCode(1);
            LogFailure(_logger, exception.GetType().Name);
        }
        finally
        {
            _applicationLifetime.StopApplication();
        }
    }

    internal async ValueTask<BoundedUsagePeriodRebuildResult> RunOnceAsync(
        CancellationToken cancellationToken)
    {
        BoundedUsagePeriodRebuildRequest request = _options.Request
            ?? throw new InvalidOperationException(
                "The enabled one-shot Usage rebuild request is missing.");
        IWorkerSessionLock? jobLock = await _lockProvider.TryAcquireAsync(
            WorkerJobs.UsageRebuild,
            cancellationToken).ConfigureAwait(false);
        if (jobLock is null)
        {
            return new BoundedUsagePeriodRebuildResult(
                BoundedUsagePeriodRebuildDisposition.Busy,
                CheckpointSourceEventSequence: 0,
                RebuiltBucketCount: 0,
                RemainingProjectionVariance: System.Numerics.BigInteger.Zero);
        }

        await using (jobLock.ConfigureAwait(false))
        {
            return await _rebuild(jobLock, request, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        EventId = 2510,
        Level = LogLevel.Information,
        Message = "One-shot Usage projection rebuild exited with {Disposition}; rebuilt {BucketCount} buckets at checkpoint {CheckpointSequence}.")]
    private static partial void LogResult(
        ILogger logger,
        BoundedUsagePeriodRebuildDisposition disposition,
        int bucketCount,
        long checkpointSequence);

    [LoggerMessage(
        EventId = 2511,
        Level = LogLevel.Error,
        Message = "One-shot Usage projection rebuild failed with {FailureType}.")]
    private static partial void LogFailure(ILogger logger, string failureType);
}
