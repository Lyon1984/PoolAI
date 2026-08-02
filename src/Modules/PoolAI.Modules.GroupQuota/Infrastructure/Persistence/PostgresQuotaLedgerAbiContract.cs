#pragma warning disable MA0051 // Each signed PostgreSQL result shape is validated in one visible method.
using System.Numerics;
using Npgsql;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.GroupQuota.Application.Ports;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

internal static class PostgresQuotaLedgerAbiContract
{
    internal static async ValueTask<QuotaReservationRow> ReadReservationAsync(
        NpgsqlCommand command,
        ReserveQuotaWrite write,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        RequireRow(await reader.ReadAsync(cancellationToken).ConfigureAwait(false), "reserve");
        QuotaReservationRow row = new(
            new EntityId(reader.GetGuid(0)),
            new EntityId(reader.GetGuid(1)),
            ParseReservationStatus(reader.GetString(2)),
            reader.GetFieldValue<BigInteger>(3),
            reader.GetFieldValue<BigInteger>(4),
            reader.GetFieldValue<BigInteger>(5),
            reader.GetFieldValue<BigInteger>(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8));
        RequireSingleRow(
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "reserve");
        ValidatePosition(
            row.PeriodId,
            row.TotalTokens,
            row.ConsumedTokens,
            row.ReservedTokens,
            row.RemainingTokens);
        if (row.ReservationId != write.ReservationId
            || row.MaxExpiresAt < row.LeaseExpiresAt)
        {
            throw Invalid("reserve");
        }

        return row;
    }

    internal static async ValueTask<QuotaDispatchRow> ReadDispatchAsync(
        NpgsqlCommand command,
        MarkReservationDispatchedWrite write,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        RequireRow(await reader.ReadAsync(cancellationToken).ConfigureAwait(false), "dispatch");
        QuotaDispatchRow row = new(
            new EntityId(reader.GetGuid(0)),
            new EntityId(reader.GetGuid(1)),
            ParseReservationStatus(reader.GetString(2)),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5));
        RequireSingleRow(
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "dispatch");
        if (row.ReservationId != write.Command.Reservation.ReservationId
            || row.PeriodId != write.Command.Reservation.PeriodId
            || row.DispatchStartedAt < DateTimeOffset.UnixEpoch
            || row.MaxExpiresAt < row.LeaseExpiresAt)
        {
            throw Invalid("dispatch");
        }

        return row;
    }

    internal static async ValueTask<QuotaRenewalRow> ReadRenewalAsync(
        NpgsqlCommand command,
        RenewReservationWrite write,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        RequireRow(await reader.ReadAsync(cancellationToken).ConfigureAwait(false), "renewal");
        QuotaRenewalRow row = new(
            new EntityId(reader.GetGuid(0)),
            new EntityId(reader.GetGuid(1)),
            ParseReservationStatus(reader.GetString(2)),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4));
        RequireSingleRow(
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "renewal");
        ReservationHandle expected = write.Command.Reservation;
        if (row.ReservationId != expected.ReservationId
            || row.PeriodId != expected.PeriodId
            || row.Status != ReservationStatus.Pending
            || row.LeaseExpiresAt < expected.LeaseExpiresAt
            || row.MaxExpiresAt != expected.MaxExpiresAt
            || row.LeaseExpiresAt > row.MaxExpiresAt)
        {
            throw Invalid("renewal");
        }

        return row;
    }

    internal static async ValueTask<IReadOnlyList<QuotaExpiryCandidate>>
        ReadExpiryCandidatesAsync(
            NpgsqlCommand command,
            QuotaExpiryCandidateKey? after,
            int pageSize,
            CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<QuotaExpiryCandidate> candidates = new(capacity: pageSize);
        QuotaExpiryCandidateKey? previous = after;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            QuotaExpiryCandidate candidate = new(
                new EntityId(reader.GetGuid(0)),
                new EntityId(reader.GetGuid(1)),
                new EntityId(reader.GetGuid(2)),
                new EntityId(reader.GetGuid(3)),
                reader.GetFieldValue<DateTimeOffset>(4));
            if (previous is not null && Compare(candidate.Key, previous) <= 0)
            {
                throw Invalid("expiry candidate order");
            }

            candidates.Add(candidate);
            if (candidates.Count > pageSize)
            {
                throw Invalid("expiry candidate page size");
            }

            previous = candidate.Key;
        }

        return candidates;
    }

    internal static async ValueTask<QuotaTransitionRow> ReadTransitionAsync(
        NpgsqlCommand command,
        EntityId expectedReservationId,
        EntityId expectedPeriodId,
        ReservationStatus expectedStatus,
        string operation,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        RequireRow(await reader.ReadAsync(cancellationToken).ConfigureAwait(false), operation);
        QuotaTransitionRow row = new(
            new EntityId(reader.GetGuid(0)),
            new EntityId(reader.GetGuid(1)),
            ParseReservationStatus(reader.GetString(2)),
            reader.GetFieldValue<BigInteger>(3),
            reader.GetFieldValue<BigInteger>(4),
            reader.GetFieldValue<BigInteger>(5),
            reader.GetFieldValue<BigInteger>(6));
        RequireSingleRow(
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            operation);
        ValidatePosition(
            row.PeriodId,
            row.TotalTokens,
            row.ConsumedTokens,
            row.ReservedTokens,
            row.RemainingTokens);
        if (row.ReservationId != expectedReservationId
            || row.PeriodId != expectedPeriodId
            || row.Status != expectedStatus)
        {
            throw Invalid(operation);
        }

        return row;
    }

    internal static async ValueTask<UsageAdjustmentRow> ReadAdjustmentAsync(
        NpgsqlCommand command,
        AdjustAttemptUsageWrite write,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        RequireRow(await reader.ReadAsync(cancellationToken).ConfigureAwait(false), "adjustment");
        UsageAdjustmentRow row = new(
            new EntityId(reader.GetGuid(0)),
            new EntityId(reader.GetGuid(1)),
            ParseReservationStatus(reader.GetString(2)),
            reader.GetFieldValue<BigInteger>(3),
            reader.GetFieldValue<BigInteger>(4),
            reader.GetFieldValue<BigInteger>(5),
            reader.GetFieldValue<BigInteger>(6),
            reader.GetFieldValue<BigInteger>(7));
        RequireSingleRow(
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "adjustment");
        if (row.ReservationId.Value.Version != 7
            || row.PeriodId.Value.Version != 7
            || row.ReservationStatus is not (
                ReservationStatus.Settled or ReservationStatus.Expired)
            || row.PreviousTokens < BigInteger.Zero
            || row.PreviousTokens > QuotaLedgerValidation.MaximumNumeric78
            || row.CorrectedTokens != write.Command.CorrectedUsage.TotalTokens
            || row.CorrectedTokens < BigInteger.Zero
            || row.CorrectedTokens > QuotaLedgerValidation.MaximumNumeric78
            || row.DeltaTokens != row.CorrectedTokens - row.PreviousTokens
            || row.ConsumedTokens < BigInteger.Zero
            || row.ConsumedTokens > QuotaLedgerValidation.MaximumNumeric78
            || row.ReservedTokens < BigInteger.Zero
            || row.ReservedTokens > QuotaLedgerValidation.MaximumNumeric78)
        {
            throw Invalid("adjustment");
        }

        return row;
    }

    internal static async ValueTask<AttemptSettlementFact?> ReadAttemptFactAsync(
        NpgsqlCommand command,
        EntityId expectedAttemptId,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        AttemptSettlementFact fact = ReadAttemptFact(reader);
        RequireSingleRow(
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "attempt fact");
        if (fact.AttemptId != expectedAttemptId)
        {
            throw Invalid("attempt fact identity");
        }

        return fact;
    }

    internal static AttemptSettlementFact ReadAttemptFact(
        NpgsqlDataReader reader,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(reader);
        TokenUsage baseTokens = new(
            reader.GetFieldValue<BigInteger>(offset + 12),
            reader.GetFieldValue<BigInteger>(offset + 13),
            reader.GetFieldValue<BigInteger>(offset + 14),
            reader.GetFieldValue<BigInteger>(offset + 15),
            reader.GetFieldValue<BigInteger>(offset + 16));
        AttemptUsage baseUsage = new(
            baseTokens,
            ParseUsageSource(reader.GetString(offset + 17)),
            reader.GetBoolean(offset + 18));
        if (reader.GetGuid(offset + 11) != reader.GetGuid(offset + 4))
        {
            throw Invalid("attempt Group identity");
        }

        AttemptUsageAdjustment? adjustment = reader.IsDBNull(offset + 22)
            ? null
            : new AttemptUsageAdjustment(
                new EntityId(reader.GetGuid(offset + 22)),
                reader.GetFieldValue<BigInteger>(offset + 23),
                new TokenUsage(
                    reader.GetFieldValue<BigInteger>(offset + 24),
                    reader.GetFieldValue<BigInteger>(offset + 25),
                    reader.GetFieldValue<BigInteger>(offset + 26),
                    reader.GetFieldValue<BigInteger>(offset + 27),
                    reader.GetFieldValue<BigInteger>(offset + 28)),
                ParseUsageSource(reader.GetString(offset + 29)),
                reader.GetFieldValue<BigInteger>(offset + 30),
                reader.GetFieldValue<DateTimeOffset>(offset + 31));
        AttemptSettlementFact fact = new(
            new EntityId(reader.GetGuid(offset)),
            new EntityId(reader.GetGuid(offset + 1)),
            reader.GetInt32(offset + 2),
            new EntityId(reader.GetGuid(offset + 3)),
            new EntityId(reader.GetGuid(offset + 4)),
            new EntityId(reader.GetGuid(offset + 5)),
            new EntityId(reader.GetGuid(offset + 6)),
            new EntityId(reader.GetGuid(offset + 7)),
            ParseProvider(reader.GetString(offset + 8)),
            reader.GetString(offset + 32),
            reader.GetString(offset + 9),
            ParseAttemptOutcome(reader.GetString(offset + 10)),
            reader.IsDBNull(offset + 34) ? null : reader.GetInt32(offset + 34),
            reader.IsDBNull(offset + 35) ? null : reader.GetString(offset + 35),
            reader.GetBoolean(offset + 33),
            baseUsage,
            adjustment,
            reader.GetFieldValue<DateTimeOffset>(offset + 19),
            reader.IsDBNull(offset + 20)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(offset + 20),
            reader.GetFieldValue<DateTimeOffset>(offset + 21));
        if (fact.IsStreaming != reader.GetBoolean(offset + 36))
        {
            throw Invalid("attempt request stream identity");
        }

        ValidateAttemptFact(fact);
        return fact;
    }

    internal static ReservationStatus ParseReservationStatus(string value) => value switch
    {
        "pending" => ReservationStatus.Pending,
        "settled" => ReservationStatus.Settled,
        "released" => ReservationStatus.Released,
        "expired" => ReservationStatus.Expired,
        _ => throw Invalid("reservation status"),
    };

    private static int Compare(QuotaExpiryCandidateKey left, QuotaExpiryCandidateKey right)
    {
        int expiry = left.LeaseExpiresAt.CompareTo(right.LeaseExpiresAt);
        return expiry != 0
            ? expiry
            : StringComparer.Ordinal.Compare(
                left.ReservationId.Value.ToString("N"),
                right.ReservationId.Value.ToString("N"));
    }

    private static SettlementProvider ParseProvider(string value) => value switch
    {
        "openai" => SettlementProvider.OpenAi,
        "openai_compatible" => SettlementProvider.OpenAiCompatible,
        _ => throw Invalid("settlement provider"),
    };

    private static UsageAttemptOutcome ParseAttemptOutcome(string value) => value switch
    {
        "succeeded" => UsageAttemptOutcome.Succeeded,
        "failed" => UsageAttemptOutcome.Failed,
        "cancelled" => UsageAttemptOutcome.Cancelled,
        _ => throw Invalid("attempt outcome"),
    };

    private static SettlementUsageSource ParseUsageSource(string value) => value switch
    {
        "upstream" => SettlementUsageSource.Upstream,
        "local_tokenizer" => SettlementUsageSource.LocalTokenizer,
        "conservative_estimate" => SettlementUsageSource.ConservativeEstimate,
        "confirmed_no_execution" => SettlementUsageSource.ConfirmedNoExecution,
        _ => throw Invalid("usage source"),
    };

    private static void ValidatePosition(
        EntityId periodId,
        BigInteger total,
        BigInteger consumed,
        BigInteger reserved,
        BigInteger remaining)
    {
        BigInteger expectedRemaining = BigInteger.Max(
            total - consumed - reserved,
            BigInteger.Zero);
        if (periodId.Value.Version != 7
            || total < BigInteger.One
            || total > QuotaLedgerValidation.MaximumSafeTokenCount
            || consumed < BigInteger.Zero
            || consumed > QuotaLedgerValidation.MaximumNumeric78
            || reserved < BigInteger.Zero
            || reserved > QuotaLedgerValidation.MaximumNumeric78
            || remaining != expectedRemaining)
        {
            throw Invalid("quota position");
        }
    }

    private static void ValidateAttemptFact(AttemptSettlementFact fact)
    {
        bool sourceEstimateMatches = fact.Usage.Source switch
        {
            SettlementUsageSource.Upstream => !fact.Usage.IsEstimated,
            SettlementUsageSource.ConfirmedNoExecution => !fact.Usage.IsEstimated,
            SettlementUsageSource.LocalTokenizer => fact.Usage.IsEstimated,
            SettlementUsageSource.ConservativeEstimate => fact.Usage.IsEstimated,
            _ => false,
        };
        if (fact.AttemptIndex < 0
            || string.IsNullOrWhiteSpace(fact.RequestedModel)
            || string.IsNullOrWhiteSpace(fact.UpstreamModel)
            || fact.UpstreamHttpStatus is < 100 or > 599
            || (fact.ErrorCode is not null
                && string.IsNullOrWhiteSpace(fact.ErrorCode))
            || !QuotaLedgerValidation.IsValid(fact.Usage.Tokens)
            || !sourceEstimateMatches
            || fact.CompletedAt < fact.DispatchStartedAt
            || (fact.FirstTokenAt is not null
                && (fact.FirstTokenAt < fact.DispatchStartedAt
                    || fact.FirstTokenAt > fact.CompletedAt))
            || (fact.Usage.Source == SettlementUsageSource.ConfirmedNoExecution
                && (string.IsNullOrWhiteSpace(fact.ErrorCode)
                    || fact.UpstreamHttpStatus is not null
                        && fact.UpstreamHttpStatus is not (401 or 403 or 429))))
        {
            throw Invalid("attempt fact");
        }

        AttemptUsageAdjustment? adjustment = fact.Adjustment;
        if (adjustment is not null
            && (adjustment.PreviousTotalTokens != fact.Usage.Tokens.TotalTokens
                || !QuotaLedgerValidation.IsValid(adjustment.CorrectedTokens)
                || adjustment.DeltaTokens
                    != adjustment.CorrectedTokens.TotalTokens
                        - adjustment.PreviousTotalTokens
                || adjustment.AdjustedAt < fact.CompletedAt))
        {
            throw Invalid("attempt adjustment fact");
        }
    }

    private static void RequireRow(bool hasRow, string operation)
    {
        if (!hasRow)
        {
            throw Invalid(operation);
        }
    }

    private static void RequireSingleRow(bool isSingle, string operation)
    {
        if (!isSingle)
        {
            throw Invalid(operation);
        }
    }

    private static InvalidOperationException Invalid(string operation) => new(
        $"The PostgreSQL Group quota {operation} result violated the signed ABI.");
}
#pragma warning restore MA0051
