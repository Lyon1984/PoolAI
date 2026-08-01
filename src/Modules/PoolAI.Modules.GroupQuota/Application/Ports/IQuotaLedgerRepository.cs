#pragma warning disable MA0048 // The repository protocol is one cohesive internal persistence boundary.
using System.Numerics;
using System.Text.Json;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.GroupQuota.Application.Ports;

internal enum QuotaLedgerFailure
{
    None,
    ValidationFailed,
    GroupDisabled,
    QuotaExhausted,
    QuotaInsufficient,
    QuotaReserved,
    InvalidApiKey,
    SubscriptionInactive,
    NoAvailableAccount,
    ResourceNotFound,
    ResourceConflict,
    IdempotencyConflict,
    ReservationLeaseLost,
    ReservationExpiryRaceLost,
    UsageWithoutDispatch,
    TerminalFactInvariantBroken,
    TokenNumericOverflow,
    DependencyUnavailable,
    Internal,
}

internal sealed record QuotaMutationIdentity(
    EntityId EventId,
    EntityId OutboxId,
    string IdempotencyKey);

internal sealed record ReserveQuotaWrite(
    ReserveQuotaCommand Command,
    EntityId ReservationId,
    QuotaMutationIdentity Mutation);

internal sealed record MarkReservationDispatchedWrite(
    MarkReservationDispatchedCommand Command,
    QuotaMutationIdentity Mutation);

internal sealed record RenewReservationWrite(
    RenewReservationCommand Command,
    QuotaMutationIdentity Mutation);

internal sealed record SettleReservationWrite(
    SettleReservationCommand Command,
    QuotaMutationIdentity Mutation);

internal sealed record ReleaseReservationWrite(
    ReleaseReservationCommand Command,
    QuotaMutationIdentity Mutation);

internal sealed record QuotaExpiryCandidateKey(
    DateTimeOffset LeaseExpiresAt,
    EntityId ReservationId);

internal sealed record QuotaExpiryCandidate(
    EntityId ReservationId,
    EntityId AttemptId,
    EntityId GroupId,
    EntityId PeriodId,
    DateTimeOffset LeaseExpiresAt)
{
    internal QuotaExpiryCandidateKey Key => new(LeaseExpiresAt, ReservationId);
}

internal sealed record ExpireReservationWrite(
    QuotaExpiryCandidate Candidate,
    QuotaMutationIdentity Mutation,
    string Reason);

internal sealed record AdjustAttemptUsageCommand(
    EntityId GroupId,
    EntityId AttemptId,
    EntityId AccountId,
    EntityId ChannelId,
    SettlementProvider Provider,
    string Model,
    UsageAttemptOutcome AttemptOutcome,
    int? UpstreamHttpStatus,
    string? ErrorCode,
    string? UpstreamRequestId,
    DateTimeOffset DispatchStartedAt,
    DateTimeOffset? FirstTokenAt,
    DateTimeOffset CompletedAt,
    UsageRequestOutcome? RequestOutcome,
    TokenUsage CorrectedUsage,
    SettlementUsageSource UsageSource,
    JsonElement? RawUpstreamUsage,
    string Reason);

internal sealed record AdjustAttemptUsageWrite(
    AdjustAttemptUsageCommand Command,
    QuotaMutationIdentity Mutation);

internal sealed record QuotaReservationRow(
    EntityId ReservationId,
    EntityId PeriodId,
    ReservationStatus Status,
    BigInteger TotalTokens,
    BigInteger ConsumedTokens,
    BigInteger ReservedTokens,
    BigInteger RemainingTokens,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset MaxExpiresAt);

internal sealed record QuotaDispatchRow(
    EntityId ReservationId,
    EntityId PeriodId,
    ReservationStatus Status,
    DateTimeOffset DispatchStartedAt,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset MaxExpiresAt);

internal sealed record QuotaRenewalRow(
    EntityId ReservationId,
    EntityId PeriodId,
    ReservationStatus Status,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset MaxExpiresAt);

internal sealed record QuotaTransitionRow(
    EntityId ReservationId,
    EntityId PeriodId,
    ReservationStatus Status,
    BigInteger TotalTokens,
    BigInteger ConsumedTokens,
    BigInteger ReservedTokens,
    BigInteger RemainingTokens);

internal sealed record UsageAdjustmentRow(
    EntityId ReservationId,
    EntityId PeriodId,
    ReservationStatus ReservationStatus,
    BigInteger PreviousTokens,
    BigInteger CorrectedTokens,
    BigInteger DeltaTokens,
    BigInteger ConsumedTokens,
    BigInteger ReservedTokens);

internal sealed record UsageAdjustmentResult(
    EntityId ReservationId,
    EntityId AttemptId,
    EntityId GroupId,
    EntityId PeriodId,
    ReservationStatus ReservationStatus,
    BigInteger PreviousTokens,
    BigInteger CorrectedTokens,
    BigInteger DeltaTokens,
    BigInteger ConsumedTokens,
    BigInteger ReservedTokens);

internal sealed record QuotaRepositoryResult<T>(
    T? Value,
    QuotaLedgerFailure Failure)
    where T : class
{
    internal bool IsSuccess => Failure == QuotaLedgerFailure.None;

    internal static QuotaRepositoryResult<T> Success(T value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), QuotaLedgerFailure.None);

    internal static QuotaRepositoryResult<T> Failed(QuotaLedgerFailure failure) =>
        failure == QuotaLedgerFailure.None
            ? throw new ArgumentOutOfRangeException(nameof(failure))
            : new(null, failure);
}

internal interface IQuotaLedgerRepository
{
    ValueTask<QuotaRepositoryResult<QuotaReservationRow>> ReserveAsync(
        ReserveQuotaWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<QuotaRepositoryResult<QuotaDispatchRow>> MarkDispatchedAsync(
        MarkReservationDispatchedWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<QuotaRepositoryResult<QuotaRenewalRow>> RenewAsync(
        RenewReservationWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> SettleAsync(
        SettleReservationWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> ReleaseAsync(
        ReleaseReservationWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<QuotaExpiryCandidate>> ListDueExpiryCandidatesAsync(
        QuotaExpiryCandidateKey? after,
        int pageSize,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> ExpireAsync(
        ExpireReservationWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<QuotaRepositoryResult<UsageAdjustmentRow>> AdjustUsageAsync(
        AdjustAttemptUsageWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<AttemptSettlementFact?> GetAttemptSettlementFactAsync(
        EntityId attemptId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}

internal interface IUsageAdjustmentWriter
{
    ValueTask<Result<UsageAdjustmentResult>> AdjustAsync(
        AdjustAttemptUsageCommand command,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
