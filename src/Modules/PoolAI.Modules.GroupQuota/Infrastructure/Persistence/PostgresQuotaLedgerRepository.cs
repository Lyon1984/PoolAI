#pragma warning disable MA0051 // Signed function parameter order stays visible at the adapter boundary.
using System.Numerics;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

internal sealed class PostgresQuotaLedgerRepository : IQuotaLedgerRepository
{
    private const string InsertUsageRequestSql = """
        INSERT INTO public.usage_requests (
            request_id,
            user_id,
            api_key_id,
            subscription_id,
            quota_group_id,
            routing_group_id,
            endpoint,
            client_request_id,
            requested_model,
            is_streaming,
            metadata
        ) VALUES (
            $1, $2, $3, $4, $5, $5, $6, $7, $8, $9, '{}'::jsonb
        )
        ON CONFLICT (request_id) DO NOTHING;
        """;

    private const string ReadExistingUsageRequestSql = """
        SELECT
            user_id,
            api_key_id,
            subscription_id,
            quota_group_id,
            routing_group_id,
            endpoint,
            client_request_id,
            requested_model,
            is_streaming
        FROM public.usage_requests
        WHERE request_id = $1;
        """;

    private const string ReadReservationAttemptIndexShapeSql = """
        SELECT
            count(*),
            min(attempt_index),
            max(attempt_index)
        FROM public.group_token_reservations
        WHERE request_id = $1;
        """;

    private const string ReserveSql = """
        SELECT
            result_reservation_id,
            result_period_id,
            result_status,
            result_total_tokens,
            result_consumed_tokens,
            result_reserved_tokens,
            result_remaining_tokens,
            result_lease_expires_at,
            result_max_expires_at
        FROM public.poolai_quota_reserve(
            $1, $2, $3, $4, $5, $6, $7, $8,
            $9, $10, $11, $12, $13, $14, $15, $16
        );
        """;

    private const string MarkDispatchedSql = """
        SELECT
            result_reservation_id,
            result_period_id,
            result_status,
            result_dispatch_started_at,
            result_lease_expires_at,
            result_max_expires_at
        FROM public.poolai_quota_mark_dispatched(
            $1, $2, $3, $4, $5, $6, $7, $8, $9, $10
        );
        """;

    private const string SettleSql = """
        SELECT
            result_reservation_id,
            result_period_id,
            result_status,
            result_total_tokens,
            result_consumed_tokens,
            result_reserved_tokens,
            result_remaining_tokens
        FROM public.poolai_quota_settle(
            $1, $2, $3, $4, $5, $6, $7, $8,
            $9, $10, $11, $12, $13, $14, $15, $16,
            $17, $18, $19, $20, $21, $22, $23, $24
        );
        """;

    private const string ReleaseSql = """
        SELECT
            result_reservation_id,
            result_period_id,
            result_status,
            result_total_tokens,
            result_consumed_tokens,
            result_reserved_tokens,
            result_remaining_tokens
        FROM public.poolai_quota_release($1, $2, $3, $4, $5, $6);
        """;

    private const string AdjustUsageSql = """
        SELECT
            result_reservation_id,
            result_period_id,
            result_reservation_status,
            result_previous_tokens,
            result_corrected_tokens,
            result_delta_tokens,
            result_consumed_tokens,
            result_reserved_tokens
        FROM public.poolai_quota_adjust_usage(
            $1, $2, $3, $4, $5, $6, $7, $8,
            $9, $10, $11, $12, $13, $14, $15, $16,
            $17, $18, $19, $20, $21, $22, $23, $24, $25
        );
        """;

    private const string AttemptFactSql = """
        SELECT
            attempt.attempt_id,
            attempt.request_id,
            attempt.attempt_index,
            attempt.reservation_id,
            attempt.quota_group_id,
            reservation.period_id,
            attempt.account_id,
            attempt.channel_id,
            attempt.provider,
            attempt.model,
            attempt.status,
            attempt.routing_group_id,
            attempt.input_tokens,
            attempt.output_tokens,
            attempt.cache_read_tokens,
            attempt.cache_creation_tokens,
            attempt.thinking_tokens,
            attempt.usage_source,
            attempt.is_estimated,
            attempt.dispatch_started_at,
            attempt.first_token_at,
            attempt.completed_at,
            adjustment.quota_event_id,
            adjustment.previous_total_tokens,
            adjustment.corrected_input_tokens,
            adjustment.corrected_output_tokens,
            adjustment.corrected_cache_read_tokens,
            adjustment.corrected_cache_creation_tokens,
            adjustment.corrected_thinking_tokens,
            adjustment.usage_source,
            adjustment.delta_tokens,
            adjustment.adjusted_at,
            usage_request.requested_model,
            usage_request.is_streaming,
            attempt.upstream_http_status,
            attempt.error_code,
            reservation.is_streaming
        FROM public.usage_attempts AS attempt
        JOIN public.group_token_reservations AS reservation
          ON reservation.id = attempt.reservation_id
         AND reservation.attempt_id = attempt.attempt_id
         AND reservation.request_id = attempt.request_id
         AND reservation.attempt_index = attempt.attempt_index
         AND reservation.group_id = attempt.quota_group_id
        JOIN public.usage_requests AS usage_request
          ON usage_request.request_id = attempt.request_id
         AND usage_request.quota_group_id = attempt.quota_group_id
         AND usage_request.routing_group_id = attempt.routing_group_id
        LEFT JOIN public.usage_attempt_adjustments AS adjustment
          ON adjustment.attempt_id = attempt.attempt_id
        WHERE attempt.attempt_id = $1;
        """;

    public async ValueTask<QuotaRepositoryResult<QuotaReservationRow>> ReserveAsync(
        ReserveQuotaWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        try
        {
            if (write.Command.AttemptIndex == 0)
            {
                await InsertUsageRequestAsync(session, write.Command, cancellationToken)
                    .ConfigureAwait(false);
            }

            QuotaLedgerFailure requestFailure = await ValidateUsageRequestAsync(
                session,
                write.Command,
                cancellationToken).ConfigureAwait(false);
            if (requestFailure != QuotaLedgerFailure.None)
            {
                return QuotaRepositoryResult<QuotaReservationRow>.Failed(requestFailure);
            }

            using NpgsqlCommand command = session.CreateCommand(ReserveSql);
            command.Parameters.AddWithValue(write.ReservationId.Value);
            command.Parameters.AddWithValue(write.Command.AttemptId.Value);
            command.Parameters.AddWithValue(write.Command.RequestId.Value);
            command.Parameters.AddWithValue(write.Command.AttemptIndex);
            command.Parameters.AddWithValue(write.Command.UserId.Value);
            command.Parameters.AddWithValue(write.Command.ApiKeyId.Value);
            command.Parameters.AddWithValue(write.Command.SubscriptionId.Value);
            command.Parameters.AddWithValue(write.Command.GroupId.Value);
            command.Parameters.AddWithValue(write.Command.AccountId.Value);
            command.Parameters.AddWithValue(write.Command.ChannelId.Value);
            AddNumeric(command.Parameters, new BigInteger(write.Command.EstimatedTokens));
            command.Parameters.AddWithValue(write.Command.IsStreaming);
            command.Parameters.AddWithValue(write.Command.LeaseOwner);
            AddMutation(command.Parameters, write.Mutation);
            QuotaReservationRow row = await PostgresQuotaLedgerAbiContract
                .ReadReservationAsync(command, write, cancellationToken)
                .ConfigureAwait(false);
            bool hasContiguousAttemptIndices = await ValidateReservationAttemptIndicesAsync(
                session,
                write.Command.RequestId,
                cancellationToken).ConfigureAwait(false);
            if (!hasContiguousAttemptIndices)
            {
                return QuotaRepositoryResult<QuotaReservationRow>.Failed(
                    QuotaLedgerFailure.Internal);
            }

            return QuotaRepositoryResult<QuotaReservationRow>.Success(row);
        }
        catch (PostgresException exception) when (IsBusinessError(exception))
        {
            return QuotaRepositoryResult<QuotaReservationRow>.Failed(
                MapBusinessError(QuotaSqlOperation.Reserve, exception.MessageText));
        }
        catch (NpgsqlException exception) when (exception.IsTransient)
        {
            return QuotaRepositoryResult<QuotaReservationRow>.Failed(
                QuotaLedgerFailure.DependencyUnavailable);
        }
    }

    public async ValueTask<QuotaRepositoryResult<QuotaDispatchRow>> MarkDispatchedAsync(
        MarkReservationDispatchedWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        try
        {
            using NpgsqlCommand command = session.CreateCommand(MarkDispatchedSql);
            ReservationHandle reservation = write.Command.Reservation;
            command.Parameters.AddWithValue(reservation.GroupId.Value);
            command.Parameters.AddWithValue(reservation.AttemptId.Value);
            command.Parameters.AddWithValue(reservation.LeaseOwner);
            command.Parameters.AddWithValue(Provider(write.Command.Provider));
            command.Parameters.AddWithValue(write.Command.Model);
            AddNumeric(command.Parameters, new BigInteger(write.Command.Estimate.InputTokens));
            AddNumeric(command.Parameters, new BigInteger(write.Command.Estimate.OutputTokens));
            AddMutation(command.Parameters, write.Mutation);
            QuotaDispatchRow row = await PostgresQuotaLedgerAbiContract
                .ReadDispatchAsync(command, write, cancellationToken)
                .ConfigureAwait(false);
            return QuotaRepositoryResult<QuotaDispatchRow>.Success(row);
        }
        catch (PostgresException exception) when (IsBusinessError(exception))
        {
            return QuotaRepositoryResult<QuotaDispatchRow>.Failed(
                MapBusinessError(QuotaSqlOperation.MarkDispatched, exception.MessageText));
        }
        catch (NpgsqlException exception) when (exception.IsTransient)
        {
            return QuotaRepositoryResult<QuotaDispatchRow>.Failed(
                QuotaLedgerFailure.DependencyUnavailable);
        }
    }

    public async ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> SettleAsync(
        SettleReservationWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        try
        {
            using NpgsqlCommand command = session.CreateCommand(SettleSql);
            SettleReservationCommand settle = write.Command;
            DispatchedReservationHandle dispatched = settle.Reservation;
            ReservationHandle reservation = dispatched.Reservation;
            command.Parameters.AddWithValue(reservation.GroupId.Value);
            command.Parameters.AddWithValue(reservation.AttemptId.Value);
            command.Parameters.AddWithValue(reservation.AccountId.Value);
            command.Parameters.AddWithValue(reservation.ChannelId.Value);
            command.Parameters.AddWithValue(Provider(dispatched.Provider));
            command.Parameters.AddWithValue(dispatched.Model);
            command.Parameters.AddWithValue(AttemptOutcome(settle.AttemptOutcome));
            AddNullableInteger(command.Parameters, settle.UpstreamHttpStatus);
            AddNullableText(command.Parameters, settle.ErrorCode);
            AddUsage(command.Parameters, settle.Usage);
            command.Parameters.AddWithValue(UsageSource(settle.UsageSource));
            AddNullableText(command.Parameters, settle.UpstreamRequestId);
            AddNullableJson(command.Parameters, settle.RawUpstreamUsage);
            command.Parameters.AddWithValue(dispatched.DispatchStartedAt.ToUniversalTime());
            AddNullableTimestamp(command.Parameters, settle.FirstTokenAt);
            command.Parameters.AddWithValue(settle.CompletedAt.ToUniversalTime());
            AddNullableText(
                command.Parameters,
                settle.RequestOutcome is null
                    ? null
                    : RequestOutcome(settle.RequestOutcome.Value));
            AddMutation(command.Parameters, write.Mutation);
            QuotaTransitionRow row = await PostgresQuotaLedgerAbiContract
                .ReadTransitionAsync(
                    command,
                    reservation.ReservationId,
                    reservation.PeriodId,
                    ReservationStatus.Settled,
                    "settlement",
                    cancellationToken)
                .ConfigureAwait(false);
            return QuotaRepositoryResult<QuotaTransitionRow>.Success(row);
        }
        catch (PostgresException exception) when (IsBusinessError(exception))
        {
            return QuotaRepositoryResult<QuotaTransitionRow>.Failed(
                MapBusinessError(QuotaSqlOperation.Settle, exception.MessageText));
        }
        catch (NpgsqlException exception) when (exception.IsTransient)
        {
            return QuotaRepositoryResult<QuotaTransitionRow>.Failed(
                QuotaLedgerFailure.DependencyUnavailable);
        }
    }

    public async ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> ReleaseAsync(
        ReleaseReservationWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        try
        {
            using NpgsqlCommand command = session.CreateCommand(ReleaseSql);
            ReservationHandle reservation = write.Command.Reservation;
            command.Parameters.AddWithValue(reservation.GroupId.Value);
            command.Parameters.AddWithValue(reservation.AttemptId.Value);
            AddMutation(command.Parameters, write.Mutation);
            command.Parameters.AddWithValue(write.Command.Reason);
            QuotaTransitionRow row = await PostgresQuotaLedgerAbiContract
                .ReadTransitionAsync(
                    command,
                    reservation.ReservationId,
                    reservation.PeriodId,
                    ReservationStatus.Released,
                    "release",
                    cancellationToken)
                .ConfigureAwait(false);
            return QuotaRepositoryResult<QuotaTransitionRow>.Success(row);
        }
        catch (PostgresException exception) when (IsBusinessError(exception))
        {
            return QuotaRepositoryResult<QuotaTransitionRow>.Failed(
                MapBusinessError(QuotaSqlOperation.Release, exception.MessageText));
        }
        catch (NpgsqlException exception) when (exception.IsTransient)
        {
            return QuotaRepositoryResult<QuotaTransitionRow>.Failed(
                QuotaLedgerFailure.DependencyUnavailable);
        }
    }

    public async ValueTask<QuotaRepositoryResult<UsageAdjustmentRow>> AdjustUsageAsync(
        AdjustAttemptUsageWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        try
        {
            using NpgsqlCommand command = session.CreateCommand(AdjustUsageSql);
            AdjustAttemptUsageCommand adjust = write.Command;
            command.Parameters.AddWithValue(adjust.GroupId.Value);
            command.Parameters.AddWithValue(adjust.AttemptId.Value);
            command.Parameters.AddWithValue(adjust.AccountId.Value);
            command.Parameters.AddWithValue(adjust.ChannelId.Value);
            command.Parameters.AddWithValue(Provider(adjust.Provider));
            command.Parameters.AddWithValue(adjust.Model);
            command.Parameters.AddWithValue(AttemptOutcome(adjust.AttemptOutcome));
            AddNullableInteger(command.Parameters, adjust.UpstreamHttpStatus);
            AddNullableText(command.Parameters, adjust.ErrorCode);
            AddUsage(command.Parameters, adjust.CorrectedUsage);
            command.Parameters.AddWithValue(UsageSource(adjust.UsageSource));
            AddNullableText(command.Parameters, adjust.UpstreamRequestId);
            AddNullableJson(command.Parameters, adjust.RawUpstreamUsage);
            command.Parameters.AddWithValue(adjust.DispatchStartedAt.ToUniversalTime());
            AddNullableTimestamp(command.Parameters, adjust.FirstTokenAt);
            command.Parameters.AddWithValue(adjust.CompletedAt.ToUniversalTime());
            AddNullableText(
                command.Parameters,
                adjust.RequestOutcome is null
                    ? null
                    : RequestOutcome(adjust.RequestOutcome.Value));
            AddMutation(command.Parameters, write.Mutation);
            command.Parameters.AddWithValue(adjust.Reason);
            UsageAdjustmentRow row = await PostgresQuotaLedgerAbiContract
                .ReadAdjustmentAsync(command, write, cancellationToken)
                .ConfigureAwait(false);
            return QuotaRepositoryResult<UsageAdjustmentRow>.Success(row);
        }
        catch (PostgresException exception) when (IsBusinessError(exception))
        {
            return QuotaRepositoryResult<UsageAdjustmentRow>.Failed(
                MapBusinessError(QuotaSqlOperation.AdjustUsage, exception.MessageText));
        }
        catch (NpgsqlException exception) when (exception.IsTransient)
        {
            return QuotaRepositoryResult<UsageAdjustmentRow>.Failed(
                QuotaLedgerFailure.DependencyUnavailable);
        }
    }

    public async ValueTask<AttemptSettlementFact?> GetAttemptSettlementFactAsync(
        EntityId attemptId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(AttemptFactSql);
        command.Parameters.AddWithValue(attemptId.Value);
        return await PostgresQuotaLedgerAbiContract
            .ReadAttemptFactAsync(command, attemptId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask InsertUsageRequestAsync(
        PostgresTransactionSession session,
        ReserveQuotaCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand request = session.CreateCommand(InsertUsageRequestSql);
        request.Parameters.AddWithValue(command.RequestId.Value);
        request.Parameters.AddWithValue(command.UserId.Value);
        request.Parameters.AddWithValue(command.ApiKeyId.Value);
        request.Parameters.AddWithValue(command.SubscriptionId.Value);
        request.Parameters.AddWithValue(command.GroupId.Value);
        request.Parameters.AddWithValue(Endpoint(command.Endpoint));
        AddNullableText(request.Parameters, command.ClientRequestId);
        request.Parameters.AddWithValue(command.RequestedModel);
        request.Parameters.AddWithValue(command.IsStreaming);
        // A single VALUES row with ON CONFLICT DO NOTHING has a frozen 0-or-1
        // command tag. The following identity read is the fail-closed check.
        _ = await request.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<QuotaLedgerFailure> ValidateUsageRequestAsync(
        PostgresTransactionSession session,
        ReserveQuotaCommand command,
        CancellationToken cancellationToken)
    {
        // Do not take a row lock before poolai_quota_reserve acquires the quota-root lock.
        // These columns are immutable and a plain read preserves the signed lock order.
        using NpgsqlCommand existing = session.CreateCommand(ReadExistingUsageRequestSql);
        existing.Parameters.AddWithValue(command.RequestId.Value);
        using NpgsqlDataReader reader = await existing
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return QuotaLedgerFailure.Internal;
        }

        string? clientRequestId = reader.IsDBNull(6) ? null : reader.GetString(6);
        bool matches = reader.GetGuid(0) == command.UserId.Value
            && reader.GetGuid(1) == command.ApiKeyId.Value
            && reader.GetGuid(2) == command.SubscriptionId.Value
            && reader.GetGuid(3) == command.GroupId.Value
            && reader.GetGuid(4) == command.GroupId.Value
            && string.Equals(reader.GetString(5), Endpoint(command.Endpoint), StringComparison.Ordinal)
            && string.Equals(clientRequestId, command.ClientRequestId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(7), command.RequestedModel, StringComparison.Ordinal)
            && reader.GetBoolean(8) == command.IsStreaming;
        bool hasUnexpectedRow = await reader
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        return hasUnexpectedRow
            ? QuotaLedgerFailure.Internal
            : matches
                ? QuotaLedgerFailure.None
                : QuotaLedgerFailure.IdempotencyConflict;
    }

    private static async ValueTask<bool> ValidateReservationAttemptIndicesAsync(
        PostgresTransactionSession session,
        EntityId requestId,
        CancellationToken cancellationToken)
    {
        // poolai_quota_reserve retains the quota-row lock until this UoW ends, so
        // no attempt for the same Group can interleave with this shape check.
        using NpgsqlCommand shape = session.CreateCommand(ReadReservationAttemptIndexShapeSql);
        shape.Parameters.AddWithValue(requestId.Value);
        using NpgsqlDataReader reader = await shape
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return hasRow
            && !reader.IsDBNull(1)
            && !reader.IsDBNull(2)
            && reader.GetInt32(1) == 0
            && reader.GetInt64(0) == (long)reader.GetInt32(2) + 1
            && !await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddUsage(
        NpgsqlParameterCollection parameters,
        TokenUsage usage)
    {
        AddNumeric(parameters, usage.InputTokens);
        AddNumeric(parameters, usage.OutputTokens);
        AddNumeric(parameters, usage.CacheReadTokens);
        AddNumeric(parameters, usage.CacheCreationTokens);
        AddNumeric(parameters, usage.ThinkingTokens);
    }

    private static void AddMutation(
        NpgsqlParameterCollection parameters,
        QuotaMutationIdentity mutation)
    {
        parameters.AddWithValue(mutation.EventId.Value);
        parameters.AddWithValue(mutation.OutboxId.Value);
        parameters.AddWithValue(mutation.IdempotencyKey);
    }

    private static void AddNumeric(
        NpgsqlParameterCollection parameters,
        BigInteger value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Numeric,
            Value = value,
        });

    private static void AddNullableText(
        NpgsqlParameterCollection parameters,
        string? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = value is null ? DBNull.Value : value,
        });

    private static void AddNullableInteger(
        NpgsqlParameterCollection parameters,
        int? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Integer,
            Value = value is null ? DBNull.Value : value.Value,
        });

    private static void AddNullableTimestamp(
        NpgsqlParameterCollection parameters,
        DateTimeOffset? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = value is null ? DBNull.Value : value.Value.ToUniversalTime(),
        });

    private static void AddNullableJson(
        NpgsqlParameterCollection parameters,
        JsonElement? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = value is null ? DBNull.Value : value.Value.GetRawText(),
        });

    private static bool IsBusinessError(PostgresException exception) =>
        string.Equals(
            exception.SqlState,
            PostgresErrorCodes.RaiseException,
            StringComparison.Ordinal);

    private static QuotaLedgerFailure MapBusinessError(
        QuotaSqlOperation operation,
        string code) => code switch
        {
            "invalid_quota_reservation" => QuotaLedgerFailure.ValidationFailed,
            "group_disabled" or "group_quota_disabled" => QuotaLedgerFailure.GroupDisabled,
            "group_quota_exhausted" => QuotaLedgerFailure.QuotaExhausted,
            "group_quota_insufficient" => QuotaLedgerFailure.QuotaInsufficient,
            "group_quota_reserved" => QuotaLedgerFailure.QuotaReserved,
            "token_numeric_overflow" => QuotaLedgerFailure.TokenNumericOverflow,
            "invalid_api_key" => QuotaLedgerFailure.InvalidApiKey,
            "subscription_inactive" => QuotaLedgerFailure.SubscriptionInactive,
            "no_available_account" => QuotaLedgerFailure.NoAvailableAccount,
            "group_quota_not_found" or "group_not_found_or_archived" =>
                QuotaLedgerFailure.ResourceNotFound,
            "group_quota_period_not_current" => QuotaLedgerFailure.ResourceConflict,
            "idempotency_key_reused" => QuotaLedgerFailure.IdempotencyConflict,
            "reservation_lease_expired" or "reservation_max_lifetime_reached"
                when operation == QuotaSqlOperation.MarkDispatched =>
                    QuotaLedgerFailure.ReservationLeaseLost,
            _ => QuotaLedgerFailure.Internal,
        };

    private static string Endpoint(UsageRequestEndpoint endpoint) => endpoint switch
    {
        UsageRequestEndpoint.Responses => "/v1/responses",
        UsageRequestEndpoint.ChatCompletions => "/v1/chat/completions",
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };

    private static string Provider(SettlementProvider provider) => provider switch
    {
        SettlementProvider.OpenAi => "openai",
        SettlementProvider.OpenAiCompatible => "openai_compatible",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static string AttemptOutcome(UsageAttemptOutcome outcome) => outcome switch
    {
        UsageAttemptOutcome.Succeeded => "succeeded",
        UsageAttemptOutcome.Failed => "failed",
        UsageAttemptOutcome.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static string RequestOutcome(UsageRequestOutcome outcome) => outcome switch
    {
        UsageRequestOutcome.Succeeded => "succeeded",
        UsageRequestOutcome.Failed => "failed",
        UsageRequestOutcome.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static string UsageSource(SettlementUsageSource source) => source switch
    {
        SettlementUsageSource.Upstream => "upstream",
        SettlementUsageSource.LocalTokenizer => "local_tokenizer",
        SettlementUsageSource.ConservativeEstimate => "conservative_estimate",
        SettlementUsageSource.ConfirmedNoExecution => "confirmed_no_execution",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private enum QuotaSqlOperation
    {
        Reserve,
        MarkDispatched,
        Settle,
        Release,
        AdjustUsage,
    }
}
#pragma warning restore MA0051
