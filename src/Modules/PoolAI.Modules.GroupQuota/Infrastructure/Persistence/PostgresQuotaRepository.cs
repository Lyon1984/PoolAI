#pragma warning disable MA0051 // The adapter keeps the complete function-call and savepoint protocol visible.
using System.Numerics;
using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Domain;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

internal sealed partial class PostgresQuotaRepository(NpgsqlDataSource dataSource) :
    IQuotaRepository
{
    private const string AdjustSavepoint = "group_quota_adjust_call";
    private const string ResetSavepoint = "group_quota_reset_call";

    private const string SelectCurrentSql = """
        SELECT
            quota.group_id,
            period.id,
            CASE
                WHEN quota.enabled = false THEN 'disabled'
                WHEN period.consumed_tokens >= period.total_tokens THEN 'exhausted'
                ELSE 'active'
            END AS quota_status,
            period.total_tokens,
            period.consumed_tokens,
            period.reserved_tokens,
            GREATEST(
                period.total_tokens - period.consumed_tokens - period.reserved_tokens,
                0::numeric
            ) AS remaining_tokens,
            GREATEST(period.consumed_tokens - period.total_tokens, 0::numeric) AS overage_tokens,
            period.opened_at,
            period.closed_at,
            quota.version,
            quota.updated_at
        FROM public.group_token_quotas AS quota
        JOIN public.group_quota_periods AS period
          ON period.id = quota.current_period_id
         AND period.group_id = quota.group_id
         AND period.status = 'current'
        WHERE quota.group_id = $1;
        """;

    private const string AdjustSql = """
        SELECT
            result_period_id,
            result_total_tokens,
            result_consumed_tokens,
            result_reserved_tokens,
            result_remaining_tokens,
            result_quota_version,
            result_before_state::text
        FROM public.poolai_group_quota_adjust_total(
            $1, $2, $3, $4, $5, $6, $7, $8
        );
        """;

    private const string ResetSql = """
        SELECT
            result_period_id,
            result_period_number,
            result_total_tokens,
            result_consumed_tokens,
            result_reserved_tokens,
            result_remaining_tokens,
            result_quota_version,
            result_before_state::text
        FROM public.poolai_group_quota_reset(
            $1, $2, $3, $4, $5, $6, $7, $8, $9
        );
        """;

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<QuotaWriteResult> AdjustTotalAsync(
        AdjustQuotaWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        GroupQuotaResource? before = null;
        await BeginSavepointAsync(session, AdjustSavepoint, cancellationToken).ConfigureAwait(false);
        try
        {
            PostgresQuotaAdjustFunctionRow functionResult;
            using (NpgsqlCommand command = session.CreateCommand(AdjustSql))
            {
                command.Parameters.AddWithValue(write.GroupId.Value);
                AddNumeric(command.Parameters, write.NewTotalTokens);
                command.Parameters.AddWithValue(write.ExpectedVersion);
                command.Parameters.AddWithValue(write.ActorUserId.Value);
                command.Parameters.AddWithValue(write.EventId.Value);
                command.Parameters.AddWithValue(write.OutboxId.Value);
                command.Parameters.AddWithValue(write.EventIdempotencyKey);
                command.Parameters.AddWithValue(write.Reason);
                functionResult = await PostgresQuotaAbiContract
                    .ReadAdjustResultAsync(command, cancellationToken)
                    .ConfigureAwait(false);
            }

            before = PostgresQuotaAbiContract.ParseBeforeState(functionResult.BeforeState);
            GroupQuotaResource after = await ReadRequiredCurrentAsync(
                write.GroupId,
                session,
                cancellationToken).ConfigureAwait(false);
            PostgresQuotaAbiContract.ValidateAdjustResult(
                write,
                before,
                after,
                functionResult);
            await ReleaseSavepointAsync(session, AdjustSavepoint, cancellationToken)
                .ConfigureAwait(false);
            return new QuotaWriteResult(
                QuotaWriteDisposition.Written,
                before,
                after,
                after.Version);
        }
        catch (PostgresException exception) when (
            string.Equals(exception.SqlState, "P0001", StringComparison.Ordinal))
        {
            await RollbackAndReleaseSavepointAsync(
                session,
                AdjustSavepoint,
                cancellationToken).ConfigureAwait(false);
            QuotaWriteDisposition? disposition = MapBusinessError(exception.MessageText);
            if (disposition is null)
            {
                throw;
            }

            long? currentVersion = null;
            if (disposition == QuotaWriteDisposition.VersionConflict)
            {
                currentVersion = (await ReadCurrentAsync(
                    write.GroupId,
                    session,
                    cancellationToken).ConfigureAwait(false))?.Version;
            }

            return new QuotaWriteResult(
                disposition.Value,
                CurrentVersion: currentVersion);
        }
    }

    public async ValueTask<QuotaWriteResult> ResetAsync(
        ResetQuotaWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        GroupQuotaResource? before = null;
        await BeginSavepointAsync(session, ResetSavepoint, cancellationToken).ConfigureAwait(false);
        try
        {
            PostgresQuotaResetFunctionRow functionResult;
            using (NpgsqlCommand command = session.CreateCommand(ResetSql))
            {
                command.Parameters.AddWithValue(write.GroupId.Value);
                command.Parameters.AddWithValue(write.NewPeriodId.Value);
                AddNumeric(command.Parameters, write.TotalTokens);
                command.Parameters.AddWithValue(write.ExpectedVersion);
                command.Parameters.AddWithValue(write.ActorUserId.Value);
                command.Parameters.AddWithValue(write.EventId.Value);
                command.Parameters.AddWithValue(write.OutboxId.Value);
                command.Parameters.AddWithValue(write.EventIdempotencyKey);
                command.Parameters.AddWithValue(write.Reason);
                functionResult = await PostgresQuotaAbiContract
                    .ReadResetResultAsync(command, cancellationToken)
                    .ConfigureAwait(false);
            }

            before = PostgresQuotaAbiContract.ParseBeforeState(functionResult.BeforeState);
            GroupQuotaResource after = await ReadRequiredCurrentAsync(
                write.GroupId,
                session,
                cancellationToken).ConfigureAwait(false);
            PostgresQuotaAbiContract.ValidateResetResult(
                write,
                before,
                after,
                functionResult);
            await ReleaseSavepointAsync(session, ResetSavepoint, cancellationToken)
                .ConfigureAwait(false);
            return new QuotaWriteResult(
                QuotaWriteDisposition.Written,
                before,
                after,
                after.Version);
        }
        catch (PostgresException exception) when (
            string.Equals(exception.SqlState, "P0001", StringComparison.Ordinal))
        {
            await RollbackAndReleaseSavepointAsync(
                session,
                ResetSavepoint,
                cancellationToken).ConfigureAwait(false);
            QuotaWriteDisposition? disposition = MapBusinessError(exception.MessageText);
            if (disposition is null)
            {
                throw;
            }

            long? currentVersion = null;
            if (disposition == QuotaWriteDisposition.VersionConflict)
            {
                currentVersion = (await ReadCurrentAsync(
                    write.GroupId,
                    session,
                    cancellationToken).ConfigureAwait(false))?.Version;
            }

            return new QuotaWriteResult(
                disposition.Value,
                CurrentVersion: currentVersion);
        }
    }

    private static async ValueTask<GroupQuotaResource?> ReadCurrentAsync(
        EntityId groupId,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand snapshot = session.CreateCommand(SelectCurrentSql);
        snapshot.Parameters.AddWithValue(groupId.Value);
        return await PostgresQuotaAbiContract
            .ReadSnapshotAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<GroupQuotaResource> ReadRequiredCurrentAsync(
        EntityId groupId,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(SelectCurrentSql);
        command.Parameters.AddWithValue(groupId.Value);
        return await PostgresQuotaAbiContract
            .ReadSnapshotAsync(command, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The written Group quota snapshot could not be reloaded.");
    }

    private static QuotaWriteDisposition? MapBusinessError(string code) => code switch
    {
        "group_quota_not_found" => QuotaWriteDisposition.NotFound,
        "group_not_found_or_archived" => QuotaWriteDisposition.Archived,
        "quota_version_conflict" => QuotaWriteDisposition.VersionConflict,
        "idempotency_key_reused" => QuotaWriteDisposition.IdempotencyConflict,
        "group_quota_period_not_current" => QuotaWriteDisposition.Conflict,
        _ => null,
    };

    private static void AddNumeric(
        NpgsqlParameterCollection parameters,
        BigInteger value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Numeric,
            Value = value,
        });

    private static async ValueTask BeginSavepointAsync(
        PostgresTransactionSession session,
        string name,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand($"SAVEPOINT {name};");
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReleaseSavepointAsync(
        PostgresTransactionSession session,
        string name,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand($"RELEASE SAVEPOINT {name};");
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RollbackAndReleaseSavepointAsync(
        PostgresTransactionSession session,
        string name,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand rollback = session.CreateCommand($"ROLLBACK TO SAVEPOINT {name};"))
        {
            _ = await rollback.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReleaseSavepointAsync(session, name, cancellationToken).ConfigureAwait(false);
    }
}
#pragma warning restore MA0051
