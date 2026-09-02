using System.Numerics;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Routing.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// Adds the Redis Account-lease lifetime to one already prepared upstream
/// operation. The Group reservation coordinator remains the owner of its own
/// PostgreSQL lease and final settlement.
/// </summary>
public sealed class AccountLeaseLifetimeOperation : IReservationLifetimeOperation
{
    public static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MaximumDrainDuration =
        TimeSpan.FromSeconds(15);

    private readonly IAccountLease _accountLease;
    private readonly AccountRoute _initialRoute;
    private readonly IReservationLifetimeOperation _operation;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _drainDuration;
    private int _executed;
    private int _stopReason = (int)AccountLeaseLifetimeStopReason.Completed;
    private long _successfulRenewals;

    public AccountLeaseLifetimeOperation(
        IAccountLease accountLease,
        IReservationLifetimeOperation operation,
        TimeProvider timeProvider,
        TimeSpan drainDuration)
    {
        _accountLease = accountLease
            ?? throw new ArgumentNullException(nameof(accountLease));
        _initialRoute = accountLease.Route
            ?? throw new ArgumentException(
                "The Account lease must expose a route.",
                nameof(accountLease));
        _operation = operation
            ?? throw new ArgumentNullException(nameof(operation));
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        if (drainDuration <= TimeSpan.Zero
            || drainDuration > MaximumDrainDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(drainDuration));
        }

        _drainDuration = drainDuration;
    }

    public AccountLeaseLifetimeStopReason StopReason =>
        (AccountLeaseLifetimeStopReason)Volatile.Read(ref _stopReason);

    public long SuccessfulRenewals =>
        Interlocked.Read(ref _successfulRenewals);

    public async ValueTask<ReservationSettlementEvidence> ExecuteAsync(
        ReservationLifetimeCancellation cancellation)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                "An Account lease lifetime operation is single-use.");
        }

        if (LeaseIsExpired())
        {
            return StopBeforeUpstreamStarts();
        }

        return await ExecuteWithMonitorAsync(cancellation)
            .ConfigureAwait(false);
    }

    private async ValueTask<ReservationSettlementEvidence>
        ExecuteWithMonitorAsync(ReservationLifetimeCancellation cancellation)
    {
        using CancellationTokenSource monitorCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellation.AbortUpstream);
        using CancellationTokenSource accountAbort =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellation.AbortUpstream);
        using CancellationTokenSource accountDrain =
            CancellationTokenSource.CreateLinkedTokenSource(cancellation.Drain);

        Task<AccountLeaseLifetimeStopReason?> monitorTask = MonitorAsync(
            monitorCancellation.Token);
        if (LeaseIsExpired())
        {
            monitorCancellation.Cancel();
            _ = await ObserveMonitorAsync(monitorTask).ConfigureAwait(false);
            return StopBeforeUpstreamStarts();
        }

        Task<ReservationSettlementEvidence> operationTask = StartOperation(
            new ReservationLifetimeCancellation(
                accountAbort.Token,
                accountDrain.Token));

        try
        {
            Task first = await Task.WhenAny(operationTask, monitorTask)
                .ConfigureAwait(false);
            if (first == operationTask)
            {
                return await CompleteOperationAsync(
                        monitorCancellation,
                        monitorTask,
                        operationTask)
                    .ConfigureAwait(false);
            }

            AccountLeaseLifetimeStopReason? reason =
                await ObserveMonitorAsync(monitorTask).ConfigureAwait(false);
            if (reason is null)
            {
                return await ReadEvidenceAsync(operationTask)
                    .ConfigureAwait(false);
            }

            return await CompleteAfterLeaseFailureAsync(
                    reason.Value,
                    accountAbort,
                    accountDrain,
                    operationTask)
                .ConfigureAwait(false);
        }
        finally
        {
            monitorCancellation.Cancel();
        }
    }

    private bool LeaseIsExpired() =>
        _initialRoute.LeaseExpiresAt <= _timeProvider.GetUtcNow();

    private ReservationSettlementEvidence.KnownUsage StopBeforeUpstreamStarts()
    {
        Volatile.Write(
            ref _stopReason,
            (int)AccountLeaseLifetimeStopReason.LeaseLost);
        return new ReservationSettlementEvidence.KnownUsage(
            new TokenUsage(
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero),
            SettlementUsageSource.ConfirmedNoExecution);
    }

    private static async ValueTask<ReservationSettlementEvidence>
        CompleteOperationAsync(
            CancellationTokenSource monitorCancellation,
            Task<AccountLeaseLifetimeStopReason?> monitorTask,
            Task<ReservationSettlementEvidence> operationTask)
    {
        monitorCancellation.Cancel();
        await ObserveMonitorAsync(monitorTask).ConfigureAwait(false);
        return await ReadEvidenceAsync(operationTask).ConfigureAwait(false);
    }

    private async ValueTask<ReservationSettlementEvidence>
        CompleteAfterLeaseFailureAsync(
            AccountLeaseLifetimeStopReason reason,
            CancellationTokenSource accountAbort,
            CancellationTokenSource accountDrain,
            Task<ReservationSettlementEvidence> operationTask)
    {
        Volatile.Write(ref _stopReason, (int)reason);
        accountAbort.Cancel();
        Task deadline = Task.Delay(
            _drainDuration,
            _timeProvider,
            CancellationToken.None);
        Task drained = await Task.WhenAny(operationTask, deadline)
            .ConfigureAwait(false);
        if (drained == operationTask)
        {
            return await ReadEvidenceAsync(operationTask)
                .ConfigureAwait(false);
        }

        accountDrain.Cancel();
        ObserveFault(operationTask);
        return ReservationSettlementEvidence.NoKnownUsage.Instance;
    }

    private async Task<AccountLeaseLifetimeStopReason?> MonitorAsync(
        CancellationToken cancellationToken)
    {
        int consecutiveCoordinationFailures = 0;
        DateTimeOffset leaseExpiresAt = _initialRoute.LeaseExpiresAt;
        while (!cancellationToken.IsCancellationRequested)
        {
            bool? renewalDue = await WaitUntilRenewalAsync(
                    leaseExpiresAt,
                    cancellationToken)
                .ConfigureAwait(false);
            if (renewalDue is null)
            {
                return null;
            }

            if (!renewalDue.Value)
            {
                return AccountLeaseLifetimeStopReason.LeaseLost;
            }

            AccountLeaseRenewResult? renewed = await TryRenewAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (renewed is null && cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (renewed?.Disposition == AccountLeaseRenewDisposition.Renewed
                && renewed.Route is not null
                && IsCompatibleRoute(renewed.Route))
            {
                consecutiveCoordinationFailures = 0;
                Interlocked.Increment(ref _successfulRenewals);
                leaseExpiresAt = renewed.Route.LeaseExpiresAt;
                continue;
            }

            if (renewed?.Disposition
                == AccountLeaseRenewDisposition.CoordinationUnavailable)
            {
                consecutiveCoordinationFailures++;
                if (consecutiveCoordinationFailures < 2)
                {
                    continue;
                }

                return AccountLeaseLifetimeStopReason
                    .CoordinationUnavailable;
            }

            return AccountLeaseLifetimeStopReason.LeaseLost;
        }

        return null;
    }

    private async ValueTask<bool?> WaitUntilRenewalAsync(
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan remaining = leaseExpiresAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        TimeSpan delay = RenewInterval;
        if (remaining < RenewInterval + RenewInterval)
        {
            delay = TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / 2));
        }

        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            return _timeProvider.GetUtcNow() < leaseExpiresAt;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async ValueTask<AccountLeaseRenewResult?> TryRenewAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _accountLease.RenewAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            return AccountLeaseRenewResult.Unavailable;
        }
    }

    private bool IsCompatibleRoute(AccountRoute renewed)
    {
        return renewed.GroupId == _initialRoute.GroupId
            && renewed.ChannelId == _initialRoute.ChannelId
            && renewed.AccountId == _initialRoute.AccountId
            && renewed.Provider == _initialRoute.Provider
            && string.Equals(
                renewed.ClientModel,
                _initialRoute.ClientModel,
                StringComparison.Ordinal)
            && string.Equals(
                renewed.UpstreamModel,
                _initialRoute.UpstreamModel,
                StringComparison.Ordinal)
            && renewed.UpstreamBaseUri == _initialRoute.UpstreamBaseUri
            && renewed.Capabilities == _initialRoute.Capabilities
            && renewed.SupplyConfigurationVersion
                == _initialRoute.SupplyConfigurationVersion
            && renewed.ChannelVersion == _initialRoute.ChannelVersion
            && renewed.AccountVersion == _initialRoute.AccountVersion
            && renewed.CredentialRevision == _initialRoute.CredentialRevision
            && renewed.LeaseExpiresAt > _timeProvider.GetUtcNow();
    }

    private Task<ReservationSettlementEvidence> StartOperation(
        ReservationLifetimeCancellation cancellation)
    {
        try
        {
            return _operation.ExecuteAsync(cancellation).AsTask();
        }
        catch (Exception exception)
        {
            return Task.FromException<ReservationSettlementEvidence>(exception);
        }
    }

    private static async Task<AccountLeaseLifetimeStopReason?>
        ObserveMonitorAsync(Task<AccountLeaseLifetimeStopReason?> monitor)
    {
        try
        {
            return await monitor.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return AccountLeaseLifetimeStopReason.CoordinationUnavailable;
        }
    }

    private static async Task<ReservationSettlementEvidence>
        ReadEvidenceAsync(Task<ReservationSettlementEvidence> operation)
    {
        try
        {
            return await operation.ConfigureAwait(false)
                ?? ReservationSettlementEvidence.NoKnownUsage.Instance;
        }
        catch (Exception)
        {
            return ReservationSettlementEvidence.NoKnownUsage.Instance;
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
