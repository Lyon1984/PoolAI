using System.Runtime.CompilerServices;
using System.Text.Json;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.GroupQuota.Application;

internal sealed class GroupQuotaLedgerService(
    IQuotaLedgerRepository repository,
    IUnitOfWorkFactory unitOfWorkFactory,
    IOperationalEventWriter? operationalEventWriter,
    IIdempotentAuditAppender idempotentAuditAppender) :
    IGroupQuotaLedger,
    IAttemptSettlementFactReader,
    IUsageAdjustmentWriter
{
    internal GroupQuotaLedgerService(
        IQuotaLedgerRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        IIdempotentAuditAppender idempotentAuditAppender)
        : this(
            repository,
            unitOfWorkFactory,
            operationalEventWriter: null,
            idempotentAuditAppender)
    {
    }

    private readonly IQuotaLedgerRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly IUnitOfWorkFactory _unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
    private readonly IOperationalEventWriter _operationalEventWriter =
        operationalEventWriter ?? MissingOperationalEventWriter.Instance;
    private readonly IIdempotentAuditAppender _idempotentAuditAppender =
        idempotentAuditAppender
        ?? throw new ArgumentNullException(nameof(idempotentAuditAppender));

    public async ValueTask<Result<ReserveQuotaResult>> ReserveAsync(
        ReserveQuotaCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!QuotaLedgerValidation.IsValid(command))
        {
            return ValidationFailure<ReserveQuotaResult>();
        }

        EntityId reservationId = QuotaMutationIdentityFactory.ReservationId(command.AttemptId);
        ReserveQuotaWrite write = new(
            command,
            reservationId,
            QuotaMutationIdentityFactory.For(command.AttemptId, "reserve"));
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        QuotaRepositoryResult<QuotaReservationRow> result = await _repository
            .ReserveAsync(write, unitOfWork.Context, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Failure<ReserveQuotaResult>(result.Failure);
        }

        QuotaReservationRow row = result.Value!;
        ReservationHandle reservation = new(
            row.ReservationId,
            command.RequestId,
            command.AttemptId,
            command.AttemptIndex,
            command.GroupId,
            row.PeriodId,
            command.AccountId,
            command.ChannelId,
            command.EstimatedTokens,
            command.IsStreaming,
            command.LeaseOwner,
            row.LeaseExpiresAt,
            row.MaxExpiresAt);
        ReserveQuotaResult response = new(
            row.Status,
            reservation,
            Position(command.GroupId, row));
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(response);
    }

    public async ValueTask<Result<DispatchedReservationHandle>> MarkDispatchedAsync(
        MarkReservationDispatchedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!QuotaLedgerValidation.IsValid(command))
        {
            return InternalFailure<DispatchedReservationHandle>();
        }

        MarkReservationDispatchedWrite write = new(
            command,
            QuotaMutationIdentityFactory.For(
                command.Reservation.AttemptId,
                "dispatch"));
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        QuotaRepositoryResult<QuotaDispatchRow> result = await _repository
            .MarkDispatchedAsync(write, unitOfWork.Context, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Failure<DispatchedReservationHandle>(result.Failure);
        }

        QuotaDispatchRow row = result.Value!;
        ReservationHandle refreshed = command.Reservation with
        {
            LeaseExpiresAt = row.LeaseExpiresAt,
            MaxExpiresAt = row.MaxExpiresAt,
        };
        DispatchedReservationHandle response = new(
            row.Status,
            refreshed,
            command.Provider,
            command.Model,
            command.Estimate,
            row.DispatchStartedAt);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(response);
    }

    public async ValueTask<Result<ReservationHandle>> RenewAsync(
        RenewReservationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!QuotaLedgerValidation.IsValid(command))
        {
            return InternalFailure<ReservationHandle>();
        }

        RenewReservationWrite write = new(
            command,
            QuotaMutationIdentityFactory.ForRenewal(
                command.Reservation.AttemptId,
                command.RenewalSequence));
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        QuotaRepositoryResult<QuotaRenewalRow> result = await _repository
            .RenewAsync(write, unitOfWork.Context, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Failure<ReservationHandle>(result.Failure);
        }

        QuotaRenewalRow row = result.Value!;
        ReservationHandle refreshed = command.Reservation with
        {
            LeaseExpiresAt = row.LeaseExpiresAt,
            MaxExpiresAt = row.MaxExpiresAt,
        };
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(refreshed);
    }

    public async ValueTask<Result<QuotaTransitionResult>> SettleAsync(
        SettleReservationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!QuotaLedgerValidation.HasValidStructure(command))
        {
            return InternalFailure<QuotaTransitionResult>();
        }

        if (QuotaLedgerValidation.ExceedsNumeric78(command.Usage))
        {
            await ReportTokenNumericOverflowAsync(
                "settle",
                command.Reservation.Reservation.AttemptId).ConfigureAwait(false);
            return Failure<QuotaTransitionResult>(QuotaLedgerFailure.TokenNumericOverflow);
        }

        EntityId attemptId = command.Reservation.Reservation.AttemptId;
        SettleReservationWrite write = new(
            command,
            QuotaMutationIdentityFactory.For(attemptId, "settle"));
        Result<QuotaTransitionResult> result = await ExecuteTransitionAsync(
            command.Reservation.Reservation.GroupId,
            attemptId,
            (context, token) => _repository.SettleAsync(write, context, token),
            cancellationToken,
            row => AttemptFactAuditFactory.Settled(write, row)).ConfigureAwait(false);
        if (result.IsFailure
            && string.Equals(
                result.Error.Code,
                "token_numeric_overflow",
                StringComparison.Ordinal))
        {
            await ReportTokenNumericOverflowAsync("settle", attemptId).ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask<Result<QuotaTransitionResult>> ReleaseAsync(
        ReleaseReservationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!QuotaLedgerValidation.IsValid(command))
        {
            return InternalFailure<QuotaTransitionResult>();
        }

        EntityId attemptId = command.Reservation.AttemptId;
        ReleaseReservationWrite write = new(
            command,
            QuotaMutationIdentityFactory.For(attemptId, "release"));
        return await ExecuteTransitionAsync(
            command.Reservation.GroupId,
            attemptId,
            (context, token) => _repository.ReleaseAsync(write, context, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<UsageAdjustmentResult>> AdjustAsync(
        AdjustAttemptUsageCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!QuotaLedgerValidation.HasValidStructure(command))
        {
            return InternalFailure<UsageAdjustmentResult>();
        }

        if (QuotaLedgerValidation.ExceedsNumeric78(command.CorrectedUsage))
        {
            await ReportTokenNumericOverflowAsync(
                "adjust_usage",
                command.AttemptId).ConfigureAwait(false);
            return Failure<UsageAdjustmentResult>(QuotaLedgerFailure.TokenNumericOverflow);
        }

        AdjustmentExecution execution = await ExecuteAdjustmentAsync(
            command,
            cancellationToken).ConfigureAwait(false);
        if (execution.Failure == QuotaLedgerFailure.TokenNumericOverflow)
        {
            await ReportTokenNumericOverflowAsync(
                "adjust_usage",
                command.AttemptId).ConfigureAwait(false);
        }

        if (execution.Failure is QuotaLedgerFailure.UsageWithoutDispatch
            or QuotaLedgerFailure.TerminalFactInvariantBroken)
        {
            await ReportLateUsageInvariantAsync(
                execution.Failure,
                command.AttemptId).ConfigureAwait(false);
        }

        return execution.Result;
    }

    private async ValueTask<AdjustmentExecution> ExecuteAdjustmentAsync(
        AdjustAttemptUsageCommand command,
        CancellationToken cancellationToken)
    {
        AdjustAttemptUsageWrite write = new(
            command,
            QuotaMutationIdentityFactory.For(command.AttemptId, "adjust"));
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        QuotaRepositoryResult<UsageAdjustmentRow> result = await _repository
            .AdjustUsageAsync(write, unitOfWork.Context, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return new AdjustmentExecution(
                Failure<UsageAdjustmentResult>(result.Failure),
                result.Failure);
        }

        UsageAdjustmentRow row = result.Value!;
        UsageAdjustmentResult response = new(
            row.ReservationId,
            command.AttemptId,
            command.GroupId,
            row.PeriodId,
            row.ReservationStatus,
            row.PreviousTokens,
            row.CorrectedTokens,
            row.DeltaTokens,
            row.ConsumedTokens,
            row.ReservedTokens);
        await AppendAuditAsync(
            AttemptFactAuditFactory.UsageAdjusted(write, row),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AdjustmentExecution(
            Result.Success(response),
            QuotaLedgerFailure.None);
    }

    public async ValueTask<Result<AttemptSettlementFact>> GetByAttemptIdAsync(
        EntityId attemptId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWorkContext);
        if (attemptId.Value.Version != 7)
        {
            return ValidationFailure<AttemptSettlementFact>();
        }

        AttemptSettlementFact? fact = await _repository
            .GetAttemptSettlementFactAsync(
                attemptId,
                unitOfWorkContext,
                cancellationToken)
            .ConfigureAwait(false);
        return fact is null
            ? Result.Failure<AttemptSettlementFact>(
                "resource_not_found",
                "The settlement fact was not found.")
            : Result.Success(fact);
    }

    private async ValueTask<Result<QuotaTransitionResult>> ExecuteTransitionAsync(
        EntityId groupId,
        EntityId attemptId,
        Func<IUnitOfWorkContext, CancellationToken,
            ValueTask<QuotaRepositoryResult<QuotaTransitionRow>>> operation,
        CancellationToken cancellationToken,
        Func<QuotaTransitionRow, AuditEntry>? auditFactory = null)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        QuotaRepositoryResult<QuotaTransitionRow> result = await operation(
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Failure<QuotaTransitionResult>(result.Failure);
        }

        QuotaTransitionRow row = result.Value!;
        QuotaTransitionResult response = new(
            row.ReservationId,
            attemptId,
            row.PeriodId,
            row.Status,
            Position(groupId, row));
        if (auditFactory is not null)
        {
            await AppendAuditAsync(
                auditFactory(row),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(response);
    }

    private ValueTask AppendAuditAsync(
        AuditEntry entry,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) =>
        _idempotentAuditAppender.AppendOnceAsync(
            entry,
            unitOfWorkContext,
            cancellationToken);

    private static QuotaLedgerPosition Position(
        EntityId groupId,
        QuotaReservationRow row) => new(
            groupId,
            row.PeriodId,
            row.TotalTokens,
            row.ConsumedTokens,
            row.ReservedTokens,
            row.RemainingTokens);

    private static QuotaLedgerPosition Position(
        EntityId groupId,
        QuotaTransitionRow row) => new(
            groupId,
            row.PeriodId,
            row.TotalTokens,
            row.ConsumedTokens,
            row.ReservedTokens,
            row.RemainingTokens);

    private static Result<T> Failure<T>(QuotaLedgerFailure failure) => failure switch
    {
        QuotaLedgerFailure.ValidationFailed => ValidationFailure<T>(),
        QuotaLedgerFailure.GroupDisabled => Result.Failure<T>(
            "group_disabled",
            "The Group is disabled."),
        QuotaLedgerFailure.QuotaExhausted => Result.Failure<T>(
            "group_quota_exhausted",
            "The Group quota is exhausted."),
        QuotaLedgerFailure.QuotaInsufficient => Result.Failure<T>(
            "group_quota_insufficient",
            "The request estimate exceeds the unconsumed Group quota."),
        QuotaLedgerFailure.QuotaReserved => Result.Failure<T>(
            "group_quota_reserved",
            "The available Group quota is held by in-flight reservations.",
            retryAfterSeconds: 1),
        QuotaLedgerFailure.InvalidApiKey => Result.Failure<T>(
            "invalid_api_key",
            "The API key is invalid."),
        QuotaLedgerFailure.SubscriptionInactive => Result.Failure<T>(
            "subscription_inactive",
            "The Subscription is not effective."),
        QuotaLedgerFailure.NoAvailableAccount => Result.Failure<T>(
            "no_available_account",
            "No eligible Account is available in this Group.",
            retryAfterSeconds: 1),
        QuotaLedgerFailure.ResourceNotFound => Result.Failure<T>(
            "resource_not_found",
            "The quota resource was not found."),
        QuotaLedgerFailure.ResourceConflict => Result.Failure<T>(
            "resource_conflict",
            "The quota resource is not in the required state."),
        QuotaLedgerFailure.IdempotencyConflict => Result.Failure<T>(
            "idempotency_conflict",
            "The internal mutation identity conflicts with another quota fact."),
        QuotaLedgerFailure.ReservationLeaseLost => Result.Failure<T>(
            "reservation_lease_lost",
            "The reservation lease was lost before dispatch.",
            retryAfterSeconds: 1),
        QuotaLedgerFailure.TokenNumericOverflow => Result.Failure<T>(
            "token_numeric_overflow",
            "The Token fact exceeds the supported numeric(78,0) range."),
        QuotaLedgerFailure.DependencyUnavailable => Result.Failure<T>(
            "dependency_unavailable",
            "PostgreSQL is temporarily unavailable.",
            retryAfterSeconds: 1),
        _ => InternalFailure<T>(),
    };

    private static Result<T> ValidationFailure<T>() => Result.Failure<T>(
        "validation_failed",
        "The quota command is invalid.");

    private static Result<T> InternalFailure<T>() => Result.Failure<T>(
        "internal_error",
        "The quota transaction could not be completed safely.");

    private ValueTask ReportTokenNumericOverflowAsync(
        string operation,
        EntityId attemptId) => _operationalEventWriter.WriteAsync(
            "group_quota.token_numeric_overflow",
            JsonSerializer.SerializeToElement(new
            {
                severity = "P0",
                operation,
                attempt_id = attemptId.Value,
            }),
            CancellationToken.None);

    private ValueTask ReportLateUsageInvariantAsync(
        QuotaLedgerFailure failure,
        EntityId attemptId) => _operationalEventWriter.WriteAsync(
            "group_quota.late_usage_invariant_violation",
            JsonSerializer.SerializeToElement(new
            {
                severity = "P0",
                classification = failure == QuotaLedgerFailure.UsageWithoutDispatch
                    ? "pre_dispatch_usage"
                    : "terminal_fact_invariant",
                attempt_id = attemptId.Value,
            }),
            CancellationToken.None);

    private sealed record AdjustmentExecution(
        Result<UsageAdjustmentResult> Result,
        QuotaLedgerFailure Failure);

    private sealed class MissingOperationalEventWriter : IOperationalEventWriter
    {
        internal static MissingOperationalEventWriter Instance { get; } = new();

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                "The Group quota ledger requires an operational event writer for P0 alerts.");
    }
}
