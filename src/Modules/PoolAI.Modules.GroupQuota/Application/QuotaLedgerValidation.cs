using System.Numerics;
using System.Text.Json;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;

namespace PoolAI.Modules.GroupQuota.Application;

internal static class QuotaLedgerValidation
{
    internal const long MaximumSafeTokenCount = 9_007_199_254_740_991L;
    internal static readonly BigInteger MaximumNumeric78 =
        BigInteger.Pow(10, 78) - BigInteger.One;

    internal static bool IsValid(ReserveQuotaCommand command) =>
        command.RequestId.Value.Version == 7
        && command.AttemptId.Value.Version == 7
        && command.AttemptIndex >= 0
        && Enum.IsDefined(command.Endpoint)
        && !string.IsNullOrWhiteSpace(command.RequestedModel)
        && (command.ClientRequestId is null
            || !string.IsNullOrWhiteSpace(command.ClientRequestId))
        && command.EstimatedTokens is >= 1 and <= MaximumSafeTokenCount
        && !string.IsNullOrWhiteSpace(command.LeaseOwner);

    internal static bool IsValid(MarkReservationDispatchedCommand command)
    {
        if (command.Reservation is null || command.Estimate is null)
        {
            return false;
        }

        return IsValid(command.Reservation)
            && Enum.IsDefined(command.Provider)
            && !string.IsNullOrWhiteSpace(command.Model)
            && command.Estimate.InputTokens >= 0
            && command.Estimate.OutputTokens >= 0
            && command.Estimate.InputTokens <= MaximumSafeTokenCount
            && command.Estimate.OutputTokens <= MaximumSafeTokenCount
            && command.Estimate.InputTokens + command.Estimate.OutputTokens
                == command.Reservation.EstimatedTokens;
    }

    internal static bool IsValid(RenewReservationCommand command) =>
        command.Reservation is not null
        && IsValid(command.Reservation)
        && command.RenewalSequence > 0;

    internal static bool IsValid(SettleReservationCommand command) =>
        HasValidStructure(command) && !ExceedsNumeric78(command.Usage);

    internal static bool HasValidStructure(SettleReservationCommand command)
    {
        if (command.Reservation is null || command.Usage is null)
        {
            return false;
        }

        bool confirmedNoExecution =
            command.UsageSource == SettlementUsageSource.ConfirmedNoExecution;
        return IsValid(command.Reservation.Reservation)
            && Enum.IsDefined(command.Reservation.Provider)
            && Enum.IsDefined(command.AttemptOutcome)
            && Enum.IsDefined(command.UsageSource)
            && (command.RequestOutcome is null
                || Enum.IsDefined(command.RequestOutcome.Value))
            && (command.UpstreamHttpStatus is null
                || command.UpstreamHttpStatus is >= 100 and <= 599)
            && (command.ErrorCode is null || !string.IsNullOrWhiteSpace(command.ErrorCode))
            && (command.UpstreamRequestId is null
                || !string.IsNullOrWhiteSpace(command.UpstreamRequestId))
            && HasValidTokenShape(command.Usage)
            && IsObjectOrNull(command.RawUpstreamUsage)
            && command.CompletedAt >= command.Reservation.DispatchStartedAt
            && (command.FirstTokenAt is null
                || command.FirstTokenAt >= command.Reservation.DispatchStartedAt
                    && command.FirstTokenAt <= command.CompletedAt)
            && (!confirmedNoExecution
                || command.Usage.TotalTokens == BigInteger.Zero
                    && command.Usage.CacheReadTokens == BigInteger.Zero
                    && command.Usage.CacheCreationTokens == BigInteger.Zero
                    && command.Usage.ThinkingTokens == BigInteger.Zero
                    && command.AttemptOutcome is UsageAttemptOutcome.Failed
                        or UsageAttemptOutcome.Cancelled
                    && !string.IsNullOrWhiteSpace(command.ErrorCode)
                    && command.FirstTokenAt is null
                    && (command.UpstreamHttpStatus is null
                        || command.UpstreamHttpStatus is 401 or 403 or 429));
    }

    internal static bool IsValid(ReleaseReservationCommand command) =>
        command.Reservation is not null
        && IsValid(command.Reservation)
        && !string.IsNullOrWhiteSpace(command.Reason);

    internal static bool IsValid(AdjustAttemptUsageCommand command) =>
        HasValidStructure(command) && !ExceedsNumeric78(command.CorrectedUsage);

    internal static bool HasValidStructure(AdjustAttemptUsageCommand command)
    {
        if (command.CorrectedUsage is null)
        {
            return false;
        }

        bool confirmedNoExecution =
            command.UsageSource == SettlementUsageSource.ConfirmedNoExecution;
        return command.AttemptId.Value.Version == 7
            && Enum.IsDefined(command.Provider)
            && !string.IsNullOrWhiteSpace(command.Model)
            && Enum.IsDefined(command.AttemptOutcome)
            && Enum.IsDefined(command.UsageSource)
            && (command.RequestOutcome is null
                || Enum.IsDefined(command.RequestOutcome.Value))
            && (command.UpstreamHttpStatus is null
                || command.UpstreamHttpStatus is >= 100 and <= 599)
            && (command.ErrorCode is null || !string.IsNullOrWhiteSpace(command.ErrorCode))
            && (command.UpstreamRequestId is null
                || !string.IsNullOrWhiteSpace(command.UpstreamRequestId))
            && HasValidTokenShape(command.CorrectedUsage)
            && IsObjectOrNull(command.RawUpstreamUsage)
            && command.CompletedAt >= command.DispatchStartedAt
            && (command.FirstTokenAt is null
                || command.FirstTokenAt >= command.DispatchStartedAt
                    && command.FirstTokenAt <= command.CompletedAt)
            && !string.IsNullOrWhiteSpace(command.Reason)
            && (!confirmedNoExecution
                || command.CorrectedUsage.TotalTokens == BigInteger.Zero
                    && command.CorrectedUsage.CacheReadTokens == BigInteger.Zero
                    && command.CorrectedUsage.CacheCreationTokens == BigInteger.Zero
                    && command.CorrectedUsage.ThinkingTokens == BigInteger.Zero);
    }

    internal static bool IsValid(TokenUsage usage) =>
        HasValidTokenShape(usage) && !ExceedsNumeric78(usage);

    private static bool HasValidTokenShape(TokenUsage usage) =>
        usage.InputTokens >= BigInteger.Zero
        && usage.OutputTokens >= BigInteger.Zero
        && usage.CacheReadTokens >= BigInteger.Zero
        && usage.CacheCreationTokens >= BigInteger.Zero
        && usage.ThinkingTokens >= BigInteger.Zero
        && usage.CacheReadTokens <= usage.InputTokens
        && usage.CacheCreationTokens <= usage.InputTokens
        && usage.CacheReadTokens + usage.CacheCreationTokens <= usage.InputTokens
        && usage.ThinkingTokens <= usage.OutputTokens;

    internal static bool ExceedsNumeric78(TokenUsage usage) =>
        usage.InputTokens > MaximumNumeric78
        || usage.OutputTokens > MaximumNumeric78
        || usage.CacheReadTokens > MaximumNumeric78
        || usage.CacheCreationTokens > MaximumNumeric78
        || usage.ThinkingTokens > MaximumNumeric78
        || usage.TotalTokens > MaximumNumeric78;

    private static bool IsValid(ReservationHandle reservation) =>
        reservation.RequestId.Value.Version == 7
        && reservation.AttemptId.Value.Version == 7
        && reservation.ReservationId.Value.Version == 7
        && reservation.AttemptIndex >= 0
        && reservation.EstimatedTokens is >= 1 and <= MaximumSafeTokenCount
        && !string.IsNullOrWhiteSpace(reservation.LeaseOwner)
        && reservation.MaxExpiresAt >= reservation.LeaseExpiresAt;

    private static bool IsObjectOrNull(JsonElement? value) =>
        value is null || value.Value.ValueKind == JsonValueKind.Object;
}
