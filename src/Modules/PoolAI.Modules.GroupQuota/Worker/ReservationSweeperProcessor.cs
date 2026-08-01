using System.Runtime.CompilerServices;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.GroupQuota.Worker;

internal sealed class ReservationSweeperProcessor(
    IQuotaLedgerRepository repository,
    IUnitOfWorkFactory unitOfWorkFactory,
    IOperationalEventWriter operationalEventWriter)
{
    private const string ExpiryReason = "reservation_lease_expired";

    private readonly IQuotaLedgerRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly IUnitOfWorkFactory _unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
    private readonly IOperationalEventWriter _operationalEventWriter =
        operationalEventWriter
        ?? throw new ArgumentNullException(nameof(operationalEventWriter));

    internal async ValueTask<ReservationSweepProcessResult> ProcessAsync(
        IWorkerSessionLock jobLock,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobLock);
        if (pageSize is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        SweepCounters counters = new();
        QuotaExpiryCandidateKey? cursor = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await jobLock.VerifyOwnershipAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                return counters.OwnershipLost();
            }

            IReadOnlyList<QuotaExpiryCandidate> page = await ReadPageAsync(
                cursor,
                pageSize,
                cancellationToken).ConfigureAwait(false);
            counters.PageCount++;
            ValidatePage(page, cursor, pageSize);
            if (page.Count == 0)
            {
                return counters.Completed();
            }

            if (!await ProcessPageAsync(
                    jobLock,
                    page,
                    counters,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return counters.OwnershipLost();
            }

            if (page.Count < pageSize)
            {
                return counters.Completed();
            }

            cursor = page[^1].Key;
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async ValueTask<bool> ProcessPageAsync(
        IWorkerSessionLock jobLock,
        IReadOnlyList<QuotaExpiryCandidate> page,
        SweepCounters counters,
        CancellationToken cancellationToken)
    {
        foreach (QuotaExpiryCandidate candidate in page)
        {
            if (!await jobLock.VerifyOwnershipAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                return false;
            }

            counters.ScannedCount++;
            QuotaLedgerFailure failure = await ExpireAsync(
                candidate,
                cancellationToken).ConfigureAwait(false);
            if (failure == QuotaLedgerFailure.None)
            {
                counters.ExpiredCount++;
            }
            else if (failure == QuotaLedgerFailure.ReservationExpiryRaceLost)
            {
                counters.RaceLostCount++;
            }
            else
            {
                if (failure != QuotaLedgerFailure.DependencyUnavailable)
                {
                    await ReportInvariantFailureAsync(
                        failure,
                        candidate.AttemptId).ConfigureAwait(false);
                }

                throw new ReservationSweepFailureException(failure);
            }
        }

        return true;
    }

    private ValueTask ReportInvariantFailureAsync(
        QuotaLedgerFailure failure,
        EntityId attemptId) => _operationalEventWriter.WriteAsync(
            failure == QuotaLedgerFailure.TokenNumericOverflow
                ? "group_quota.token_numeric_overflow"
                : "group_quota.reservation_sweeper_invariant_violation",
            JsonSerializer.SerializeToElement(new
            {
                severity = "P0",
                operation = "expire",
                classification = failure.ToString(),
                attempt_id = attemptId.Value,
            }),
            CancellationToken.None);

    private async ValueTask<IReadOnlyList<QuotaExpiryCandidate>> ReadPageAsync(
        QuotaExpiryCandidateKey? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        IReadOnlyList<QuotaExpiryCandidate> page = await _repository
            .ListDueExpiryCandidatesAsync(
                cursor,
                pageSize,
                unitOfWork.Context,
                cancellationToken)
            .ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page;
    }

    private async ValueTask<QuotaLedgerFailure> ExpireAsync(
        QuotaExpiryCandidate candidate,
        CancellationToken cancellationToken)
    {
        ExpireReservationWrite write = new(
            candidate,
            QuotaMutationIdentityFactory.For(candidate.AttemptId, "expire"),
            ExpiryReason);
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        QuotaRepositoryResult<QuotaTransitionRow> result = await _repository
            .ExpireAsync(write, unitOfWork.Context, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Failure;
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return QuotaLedgerFailure.None;
    }

    private static void ValidatePage(
        IReadOnlyList<QuotaExpiryCandidate> page,
        QuotaExpiryCandidateKey? after,
        int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Count > maximumCount)
        {
            throw new InvalidOperationException(
                "The reservation expiry selector exceeded its page bound.");
        }

        QuotaExpiryCandidateKey? previous = after;
        foreach (QuotaExpiryCandidate candidate in page)
        {
            if (candidate.ReservationId.Value.Version != 7
                || candidate.AttemptId.Value.Version != 7
                || candidate.GroupId.Value.Version != 7
                || candidate.PeriodId.Value.Version != 7)
            {
                throw new InvalidOperationException(
                    "The reservation expiry selector returned an invalid identity.");
            }

            if (previous is not null && Compare(candidate.Key, previous) <= 0)
            {
                throw new InvalidOperationException(
                    "The reservation expiry selector did not return a strict keyset page.");
            }

            previous = candidate.Key;
        }
    }

    private static int Compare(
        QuotaExpiryCandidateKey left,
        QuotaExpiryCandidateKey right)
    {
        int leaseComparison = left.LeaseExpiresAt.CompareTo(right.LeaseExpiresAt);
        return leaseComparison != 0
            ? leaseComparison
            : StringComparer.Ordinal.Compare(
                left.ReservationId.Value.ToString("N"),
                right.ReservationId.Value.ToString("N"));
    }

    private sealed class SweepCounters
    {
        internal int PageCount { get; set; }

        internal int ScannedCount { get; set; }

        internal int ExpiredCount { get; set; }

        internal int RaceLostCount { get; set; }

        internal ReservationSweepProcessResult Completed() =>
            Result(ReservationSweepProcessDisposition.Completed);

        internal ReservationSweepProcessResult OwnershipLost() =>
            Result(ReservationSweepProcessDisposition.OwnershipLost);

        private ReservationSweepProcessResult Result(
            ReservationSweepProcessDisposition disposition) => new(
                disposition,
                PageCount,
                ScannedCount,
                ExpiredCount,
                RaceLostCount);
    }
}
