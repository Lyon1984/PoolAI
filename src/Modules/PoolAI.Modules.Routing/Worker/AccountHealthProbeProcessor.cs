using System.Runtime.CompilerServices;
using System.Text.Json;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Routing.Worker;

internal sealed class AccountHealthProbeProcessor(
    IAccountHealthProbeCatalog catalog,
    IAccountHealthProbeExecutor executor,
    IAccountCircuitBreaker breakers,
    IAccountProbeLeaseCoordinator leases,
    IAccountHealthWriter healthWriter,
    IOperationalEventWriter operationalEvents,
    ISupplyHealthReadinessSummaryStore readiness,
    TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim _jobLockVerificationGate = new(1, 1);
    private readonly IAccountHealthProbeCatalog _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IAccountHealthProbeExecutor _executor =
        executor ?? throw new ArgumentNullException(nameof(executor));
    private readonly IAccountCircuitBreaker _breakers =
        breakers ?? throw new ArgumentNullException(nameof(breakers));
    private readonly IAccountProbeLeaseCoordinator _leases =
        leases ?? throw new ArgumentNullException(nameof(leases));
    private readonly IAccountHealthWriter _healthWriter =
        healthWriter ?? throw new ArgumentNullException(nameof(healthWriter));
    private readonly IOperationalEventWriter _operationalEvents =
        operationalEvents ?? throw new ArgumentNullException(nameof(operationalEvents));
    private readonly ISupplyHealthReadinessSummaryStore _readiness =
        readiness ?? throw new ArgumentNullException(nameof(readiness));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    internal async ValueTask<AccountHealthProbeProcessResult> ProcessAsync(
        IWorkerSessionLock jobLock,
        int batchSize,
        TimeSpan healthyProbeInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobLock);
        ArgumentOutOfRangeException.ThrowIfNotEqual(batchSize, 8);

        MutableCounters counters = new();
        EntityId? cursor = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await ConfirmOwnershipAsync(
                    jobLock,
                    counters,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                break;
            }

            Result<IReadOnlyList<AccountHealthProbeCandidate>> batch =
                await _catalog.GetDueBatchAsync(
                    cursor,
                    batchSize,
                    healthyProbeInterval,
                    cancellationToken).ConfigureAwait(false);
            IReadOnlyList<AccountHealthProbeCandidate> candidates =
                RequireSuccess(batch, "catalog");
            if (candidates.Count == 0)
            {
                break;
            }

            await ProcessBatchAsync(
                candidates,
                batchSize,
                counters,
                jobLock,
                cancellationToken).ConfigureAwait(false);

            cursor = candidates[^1].AccountId;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await CompleteAsync(counters, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose() => _jobLockVerificationGate.Dispose();

    private async ValueTask ProcessBatchAsync(
        IReadOnlyList<AccountHealthProbeCandidate> candidates,
        int maximumConcurrency,
        MutableCounters counters,
        IWorkerSessionLock jobLock,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maximumConcurrency,
            },
            async (candidate, itemCancellationToken) =>
            {
                if (!await ConfirmOwnershipAsync(
                        jobLock,
                        counters,
                        itemCancellationToken)
                    .ConfigureAwait(false))
                {
                    return;
                }

                Interlocked.Increment(ref counters.ScannedCount);
                counters.Observe(candidate.Health);
                await ProcessCandidateAsync(
                    candidate,
                    counters,
                    jobLock,
                    itemCancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async ValueTask ProcessCandidateAsync(
        AccountHealthProbeCandidate candidate,
        MutableCounters counters,
        IWorkerSessionLock jobLock,
        CancellationToken cancellationToken)
    {
        Result<AccountBreakerSnapshot> breakerResult = await _breakers
            .ReadAsync(candidate.AccountId, cancellationToken)
            .ConfigureAwait(false);
        AccountBreakerSnapshot breaker = RequireSuccess(
            breakerResult,
            "breaker read");
        CandidateBreakerDisposition disposition = Classify(
            candidate,
            breaker,
            counters);
        if (disposition.ShouldSkip)
        {
            Interlocked.Increment(ref counters.SkippedCount);
            return;
        }

        if (disposition.IsHalfOpen)
        {
            await ProcessHalfOpenAsync(
                candidate,
                counters,
                jobLock,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        IAccountProbeLease? lease = await AcquireAccountLeaseAsync(
            candidate,
            counters,
            cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return;
        }

        await using (lease.ConfigureAwait(false))
        {
            if (!await ConfirmOwnershipAsync(
                    jobLock,
                    counters,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            await ProcessControlledAsync(
                candidate,
                counters,
                jobLock,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static CandidateBreakerDisposition Classify(
        AccountHealthProbeCandidate candidate,
        AccountBreakerSnapshot breaker,
        MutableCounters counters)
    {
        bool controlledUnknown = candidate.Health == AccountHealth.Unknown
            && candidate.LastCheckedAt is null;
        bool halfOpen = candidate.IsActive
            && breaker.State == AccountBreakerState.HalfOpen
            && !controlledUnknown;
        bool routineActiveProbe = candidate.IsActive
            && breaker.State == AccountBreakerState.Closed
            && candidate.Health is
                AccountHealth.Healthy or AccountHealth.Degraded;
        bool authBlocked = breaker.State == AccountBreakerState.Open
            && breaker.OpenUntil is null;
        if (authBlocked)
        {
            Interlocked.Increment(ref counters.AuthBlockedCount);
        }

        if (halfOpen || controlledUnknown)
        {
            Interlocked.Increment(ref counters.ProbeEligibleCount);
        }

        return new(
            halfOpen,
            ShouldSkip: !controlledUnknown
                && !halfOpen
                && !routineActiveProbe);
    }

    private static bool IsInitialControlledValidation(
        AccountHealthProbeCandidate candidate) =>
        candidate.Health == AccountHealth.Unknown
            && candidate.LastCheckedAt is null;

    private async ValueTask<IAccountProbeLease?> AcquireAccountLeaseAsync(
        AccountHealthProbeCandidate candidate,
        MutableCounters counters,
        CancellationToken cancellationToken)
    {
        Result<IAccountProbeLease> leaseResult = await _leases
            .AcquireAsync(
                new AccountProbeLeaseAcquireCommand(
                    candidate.AccountId,
                    candidate.ConcurrencyLimit),
                cancellationToken)
            .ConfigureAwait(false);
        if (leaseResult.IsFailure
            && string.Equals(
                leaseResult.Error.Code,
                "account_capacity_unavailable",
                StringComparison.Ordinal))
        {
            Interlocked.Increment(ref counters.SkippedCount);
            return null;
        }

        return RequireSuccess(leaseResult, "Account lease");
    }

    private async ValueTask ProcessHalfOpenAsync(
        AccountHealthProbeCandidate candidate,
        MutableCounters counters,
        IWorkerSessionLock jobLock,
        CancellationToken cancellationToken)
    {
        Result<AccountBreakerProbeAcquireResult> acquiredResult =
            await _breakers.TryAcquireProbeAsync(
                candidate.AccountId,
                cancellationToken).ConfigureAwait(false);
        AccountBreakerProbeAcquireResult acquired = RequireSuccess(
            acquiredResult,
            "half-open probe");
        if (acquired.Disposition
            == AccountBreakerProbeAcquireDisposition.NotEligible)
        {
            Interlocked.Increment(ref counters.SkippedCount);
            return;
        }

        IAccountBreakerProbe probe = acquired.Probe
            ?? throw new InvalidOperationException(
                "The acquired Account breaker probe is missing.");
        await using (probe.ConfigureAwait(false))
        {
            if (!await ConfirmOwnershipAsync(
                    jobLock,
                    counters,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            if (!await PersistHalfOpenUnknownAsync(
                    candidate,
                    counters,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            IAccountProbeLease? lease = await AcquireAccountLeaseAsync(
                candidate,
                counters,
                cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return;
            }

            await using ConfiguredAsyncDisposable leaseScope =
                lease.ConfigureAwait(false);
            await CompleteHalfOpenProbeAsync(
                probe,
                candidate.AccountId,
                counters,
                jobLock,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> PersistHalfOpenUnknownAsync(
        AccountHealthProbeCandidate candidate,
        MutableCounters counters,
        CancellationToken cancellationToken)
    {
        Result<AccountHealthTransitionResult> result =
            await _healthWriter.RecordAsync(
                new AccountHealthTransition(
                    candidate.AccountId,
                    AccountHealth.Unknown,
                    _timeProvider.GetUtcNow(),
                    RetryAt: null,
                    candidate.AccountVersion,
                    candidate.CredentialRevision),
                cancellationToken).ConfigureAwait(false);
        AccountHealthTransitionResult transition = RequireSuccess(
            result,
            "half-open health transition");
        bool stale = transition.Disposition is
            AccountHealthTransitionDisposition.StaleObservation
            or AccountHealthTransitionDisposition.AccountRetired;
        if (stale)
        {
            Interlocked.Increment(ref counters.SkippedCount);
        }

        return !stale;
    }

    private async ValueTask CompleteHalfOpenProbeAsync(
        IAccountBreakerProbe probe,
        EntityId accountId,
        MutableCounters counters,
        IWorkerSessionLock jobLock,
        CancellationToken cancellationToken)
    {
        if (!await ConfirmOwnershipAsync(
                jobLock,
                counters,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        AccountHealthProbeResult? observation = await ProbeAsync(
            accountId,
            counters,
            cancellationToken).ConfigureAwait(false);
        if (observation is null
            || !await ConfirmOwnershipAsync(
                jobLock,
                counters,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (observation.Outcome == AccountHealthProbeOutcome.Ignored)
        {
            Interlocked.Increment(ref counters.SkippedCount);
            return;
        }

        Result<AccountBreakerSnapshot> completed = await probe
            .CompleteAsync(
                Completion(observation),
                cancellationToken).ConfigureAwait(false);
        if (IsStaleOrRetired(completed))
        {
            Interlocked.Increment(ref counters.SkippedCount);
            return;
        }
        if (completed.IsFailure
            && string.Equals(
                completed.Error.Code,
                "account_probe_not_owned",
                StringComparison.Ordinal))
        {
            Interlocked.Increment(ref counters.SkippedCount);
            return;
        }

        _ = RequireSuccess(completed, "half-open completion");
        Interlocked.Increment(ref counters.HalfOpenProbeCount);
    }

    private async ValueTask ProcessControlledAsync(
        AccountHealthProbeCandidate candidate,
        MutableCounters counters,
        IWorkerSessionLock jobLock,
        CancellationToken cancellationToken)
    {
        AccountHealthProbeResult? observation = await ProbeAsync(
            candidate.AccountId,
            counters,
            cancellationToken).ConfigureAwait(false);
        if (observation is null)
        {
            return;
        }

        if (!await ConfirmOwnershipAsync(
                jobLock,
                counters,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }

        Result<AccountBreakerSnapshot> recorded = await _breakers
            .RecordAsync(
                new AccountBreakerRecordCommand(
                    candidate.AccountId,
                    Map(observation.Outcome),
                    observation.RetryAfter,
                    observation.UpstreamStatusCode,
                    IsInitialControlledValidation(candidate)
                        ? AccountBreakerObservationMode.ControlledActive
                        : AccountBreakerObservationMode.Passive,
                    observation.ObservedAt,
                    observation.ExpectedAccountVersion,
                    observation.ExpectedCredentialRevision,
                    observation.RetryAfterAt),
                cancellationToken)
            .ConfigureAwait(false);
        if (IsStaleOrRetired(recorded))
        {
            Interlocked.Increment(ref counters.SkippedCount);
            return;
        }

        _ = RequireSuccess(recorded, "controlled Account probe");
    }

    private static bool IsStaleOrRetired<T>(Result<T> result) =>
        result.IsFailure
        && result.Error.Code is "resource_conflict" or "not_found";

    private async ValueTask<AccountHealthProbeResult?> ProbeAsync(
        EntityId accountId,
        MutableCounters counters,
        CancellationToken cancellationToken)
    {
        Result<AccountHealthProbeResult> result = await _executor
            .ProbeAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure
            && result.Error.Code is "resource_conflict" or "not_found")
        {
            Interlocked.Increment(ref counters.SkippedCount);
            return null;
        }

        AccountHealthProbeResult observation = RequireSuccess(
            result,
            "upstream Account health");
        Interlocked.Increment(ref counters.ProbedCount);
        if (observation.Outcome == AccountHealthProbeOutcome.Success)
        {
            Interlocked.Increment(ref counters.SuccessCount);
        }
        else
        {
            Interlocked.Increment(ref counters.FailureCount);
        }

        return observation;
    }

    private async ValueTask<AccountHealthProbeProcessResult> CompleteAsync(
        MutableCounters counters,
        CancellationToken cancellationToken)
    {
        AccountHealthProbeProcessResult result = counters.Freeze(
            _timeProvider.GetUtcNow());
        _readiness.Update(new SupplyHealthReadinessSummary(
            result.ObservedAt,
            result.CycleStatus,
            result.FailureCode,
            result.ScannedCount,
            result.UnknownCount,
            result.HealthyCount,
            result.DegradedCount,
            result.CoolingCount,
            result.UnhealthyCount,
            result.AuthBlockedCount,
            result.ProbeEligibleCount,
            result.ProbedCount,
            result.SuccessCount,
            result.FailureCount));
        await _operationalEvents.WriteAsync(
            "routing.account_health_probe_round_completed",
            JsonSerializer.SerializeToElement(new
            {
                observed_at = result.ObservedAt,
                cycle_status = CycleStatus(result.CycleStatus),
                failure_code = FailureCode(result.FailureCode),
                scanned_count = result.ScannedCount,
                unknown_count = result.UnknownCount,
                healthy_count = result.HealthyCount,
                degraded_count = result.DegradedCount,
                cooling_count = result.CoolingCount,
                unhealthy_count = result.UnhealthyCount,
                auth_blocked_count = result.AuthBlockedCount,
                probe_eligible_count = result.ProbeEligibleCount,
                probed_count = result.ProbedCount,
                half_open_probe_count = result.HalfOpenProbeCount,
                skipped_count = result.SkippedCount,
                success_count = result.SuccessCount,
                failure_count = result.FailureCount,
            }),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static string CycleStatus(SupplyHealthCycleStatus status) =>
        status switch
        {
            SupplyHealthCycleStatus.Succeeded => "succeeded",
            SupplyHealthCycleStatus.Partial => "partial",
            SupplyHealthCycleStatus.Failed => "failed",
            SupplyHealthCycleStatus.Standby => "standby",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static string FailureCode(SupplyHealthFailureCode code) =>
        code switch
        {
            SupplyHealthFailureCode.None => "none",
            SupplyHealthFailureCode.NotOwner => "not_owner",
            SupplyHealthFailureCode.LockLost => "lock_lost",
            SupplyHealthFailureCode.UpstreamProbeFailed =>
                "upstream_probe_failed",
            SupplyHealthFailureCode.DependencyUnavailable =>
                "dependency_unavailable",
            SupplyHealthFailureCode.CoordinationUnavailable =>
                "coordination_unavailable",
            SupplyHealthFailureCode.ContractFailure => "contract_failure",
            SupplyHealthFailureCode.UnexpectedFailure => "unexpected_failure",
            _ => throw new ArgumentOutOfRangeException(nameof(code)),
        };

    private async ValueTask<bool> ConfirmOwnershipAsync(
        IWorkerSessionLock jobLock,
        MutableCounters counters,
        CancellationToken cancellationToken)
    {
        bool owned = await VerifyJobOwnershipAsync(
            jobLock,
            cancellationToken).ConfigureAwait(false);
        if (!owned)
        {
            Interlocked.Exchange(ref counters.LockLost, 1);
            Interlocked.Increment(ref counters.SkippedCount);
        }

        return owned;
    }

    private async ValueTask<bool> VerifyJobOwnershipAsync(
        IWorkerSessionLock jobLock,
        CancellationToken cancellationToken)
    {
        await _jobLockVerificationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await jobLock.VerifyOwnershipAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _jobLockVerificationGate.Release();
        }
    }

    private static AccountBreakerProbeCompletion Completion(
        AccountHealthProbeResult result) =>
        new(
            Map(result.Outcome),
            result.RetryAfter,
            result.UpstreamStatusCode,
            result.ObservedAt,
            result.ExpectedAccountVersion,
            result.ExpectedCredentialRevision,
            result.RetryAfterAt);

    private static AccountBreakerOutcome Map(AccountHealthProbeOutcome outcome) =>
        outcome switch
        {
            AccountHealthProbeOutcome.Success => AccountBreakerOutcome.Success,
            AccountHealthProbeOutcome.TransientFailure =>
                AccountBreakerOutcome.TransientFailure,
            AccountHealthProbeOutcome.RateLimited =>
                AccountBreakerOutcome.RateLimited,
            AccountHealthProbeOutcome.AuthenticationFailure =>
                AccountBreakerOutcome.AuthenticationFailure,
            AccountHealthProbeOutcome.Ignored =>
                AccountBreakerOutcome.Ignored,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

    private static T RequireSuccess<T>(Result<T> result, string operation)
    {
        if (result.IsSuccess)
        {
            return result.Value;
        }

        throw new InvalidOperationException(
            $"The {operation} operation failed closed with {result.Error.Code}.");
    }

    private sealed class MutableCounters
    {
        public int LockLost;

        public int ScannedCount;

        public int UnknownCount;

        public int HealthyCount;

        public int DegradedCount;

        public int CoolingCount;

        public int UnhealthyCount;

        public int AuthBlockedCount;

        public int ProbeEligibleCount;

        public int ProbedCount;

        public int HalfOpenProbeCount;

        public int SkippedCount;

        public int SuccessCount;

        public int FailureCount;

        public void Observe(AccountHealth health)
        {
            switch (health)
            {
                case AccountHealth.Unknown:
                    Interlocked.Increment(ref UnknownCount);
                    break;
                case AccountHealth.Healthy:
                    Interlocked.Increment(ref HealthyCount);
                    break;
                case AccountHealth.Degraded:
                    Interlocked.Increment(ref DegradedCount);
                    break;
                case AccountHealth.Cooling:
                    Interlocked.Increment(ref CoolingCount);
                    break;
                case AccountHealth.Unhealthy:
                    Interlocked.Increment(ref UnhealthyCount);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(health));
            }
        }

        public AccountHealthProbeProcessResult Freeze(DateTimeOffset observedAt)
        {
            int attempted = Volatile.Read(ref ProbedCount);
            int succeeded = Volatile.Read(ref SuccessCount);
            int failed = Volatile.Read(ref FailureCount);
            bool lockLost = Volatile.Read(ref LockLost) != 0;
            SupplyHealthCycleStatus status = lockLost
                ? attempted == 0
                    ? SupplyHealthCycleStatus.Failed
                    : SupplyHealthCycleStatus.Partial
                : failed == 0
                    ? SupplyHealthCycleStatus.Succeeded
                    : succeeded == 0
                        ? SupplyHealthCycleStatus.Failed
                        : SupplyHealthCycleStatus.Partial;
            SupplyHealthFailureCode failureCode = lockLost
                ? SupplyHealthFailureCode.LockLost
                : failed == 0
                    ? SupplyHealthFailureCode.None
                    : SupplyHealthFailureCode.UpstreamProbeFailed;
            return new(
                observedAt,
                status,
                failureCode,
                Volatile.Read(ref ScannedCount),
                Volatile.Read(ref UnknownCount),
                Volatile.Read(ref HealthyCount),
                Volatile.Read(ref DegradedCount),
                Volatile.Read(ref CoolingCount),
                Volatile.Read(ref UnhealthyCount),
                Volatile.Read(ref AuthBlockedCount),
                Volatile.Read(ref ProbeEligibleCount),
                attempted,
                Volatile.Read(ref HalfOpenProbeCount),
                Volatile.Read(ref SkippedCount),
                succeeded,
                failed);
        }
    }

    private sealed record CandidateBreakerDisposition(
        bool IsHalfOpen,
        bool ShouldSkip);
}
