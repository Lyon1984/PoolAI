using System.Numerics;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Usage.Application;
using PoolAI.Modules.Usage.Application.Ports;

namespace PoolAI.Modules.Usage.Infrastructure.Persistence;

internal sealed class PostgresUsageReconciliationProjectionReader :
    IUsageReconciliationProjectionReader
{
    private static readonly BigInteger MaximumAggregateTokenCount =
        BigInteger.Pow(10, 78) - BigInteger.One;

    private const string ReadSql = """
        WITH reconciliation_clock AS MATERIALIZED (
            SELECT clock_timestamp() AS checked_at
        )
        SELECT
            coalesce((
                SELECT sum(hourly.total_tokens)
                FROM public.group_usage_hourly AS hourly
                WHERE hourly.group_id = $1
                  AND hourly.period_id = $2
            ), 0::numeric) AS projected_consumed_tokens,
            coalesce((
                SELECT watermark.last_event_sequence
                FROM public.aggregation_watermarks AS watermark
                WHERE watermark.projector_name = 'usage-hourly-v1'
                  AND watermark.partition_key = $3
            ), 0::bigint) AS checkpoint_source_event_sequence,
            (
                SELECT watermark.completed_through
                FROM public.aggregation_watermarks AS watermark
                WHERE watermark.projector_name = 'usage-hourly-v1'
                  AND watermark.partition_key = $3
            ) AS data_through,
            clock.checked_at
        FROM reconciliation_clock AS clock;
        """;

    public async ValueTask<UsageReconciliationProjectionSnapshot> ReadAsync(
        EntityId groupId,
        EntityId periodId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ValidateId(groupId, nameof(groupId));
        ValidateId(periodId, nameof(periodId));
        ArgumentNullException.ThrowIfNull(unitOfWorkContext);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ReadSql);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue(periodId.Value);
        command.Parameters.AddWithValue(Partition(groupId));
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The PostgreSQL Usage reconciliation projection query returned no snapshot.");
        }

        UsageReconciliationProjectionSnapshot snapshot;
        try
        {
            snapshot = new UsageReconciliationProjectionSnapshot(
                groupId,
                periodId,
                reader.GetFieldValue<BigInteger>(0),
                reader.GetInt64(1),
                ReadTimestamp(reader, 2),
                RequireTimestamp(reader, 3));
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(
                "The PostgreSQL Usage reconciliation projection violated its ABI.",
                exception);
        }

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The PostgreSQL Usage reconciliation projection returned duplicate snapshots.");
        }

        Validate(snapshot);
        return snapshot;
    }

    internal static string Partition(EntityId groupId) =>
        $"poolai.quota.v1:group:{groupId.Value:D}";

    private static void Validate(UsageReconciliationProjectionSnapshot snapshot)
    {
        if (snapshot.ProjectedConsumedTokens < BigInteger.Zero
            || snapshot.ProjectedConsumedTokens > MaximumAggregateTokenCount
            || snapshot.CheckpointSourceEventSequence < 0
            || snapshot.CheckedAt < DateTimeOffset.UnixEpoch
            || snapshot.CheckedAt.Offset != TimeSpan.Zero
            || snapshot.DataThrough is { } dataThrough
                && (dataThrough < DateTimeOffset.UnixEpoch
                    || dataThrough.Offset != TimeSpan.Zero
                    || dataThrough > snapshot.CheckedAt))
        {
            throw new InvalidOperationException(
                "The PostgreSQL Usage reconciliation projection violated its ABI.");
        }
    }

    private static DateTimeOffset? ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : RequireTimestamp(reader, ordinal);

    private static DateTimeOffset RequireTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void ValidateId(EntityId id, string parameterName)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }
    }
}
