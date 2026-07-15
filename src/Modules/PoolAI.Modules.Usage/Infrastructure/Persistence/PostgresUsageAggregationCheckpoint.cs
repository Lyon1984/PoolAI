using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Usage.Abstractions;

namespace PoolAI.Modules.Usage.Infrastructure.Persistence;

internal sealed class PostgresUsageAggregationCheckpoint : IUsageAggregationCheckpoint
{
    private const string ClaimSql = """
        INSERT INTO public.aggregation_watermarks (
            projector_name, partition_key, lease_owner, lease_until
        ) VALUES ($1, $2, $3, clock_timestamp() + $4)
        ON CONFLICT (projector_name, partition_key) DO UPDATE
        SET lease_owner = EXCLUDED.lease_owner,
            lease_until = clock_timestamp() + $4,
            version = aggregation_watermarks.version + 1,
            updated_at = clock_timestamp()
        WHERE aggregation_watermarks.lease_owner IS NULL
           OR aggregation_watermarks.lease_until <= clock_timestamp()
        RETURNING last_event_sequence, completed_through, version;
        """;

    private const string HeartbeatSql = """
        UPDATE public.aggregation_watermarks
        SET lease_until = clock_timestamp() + $5,
            updated_at = clock_timestamp()
        WHERE projector_name = $1
          AND partition_key = $2
          AND lease_owner = $3
          AND version = $4
          AND lease_until > clock_timestamp();
        """;

    private const string AdvanceSql = """
        UPDATE public.aggregation_watermarks
        SET last_event_sequence = $6,
            completed_through = COALESCE($7, completed_through),
            version = version + 1,
            updated_at = clock_timestamp()
        WHERE projector_name = $1
          AND partition_key = $2
          AND lease_owner = $3
          AND version = $4
          AND last_event_sequence = $5
          AND lease_until > clock_timestamp()
          AND ($7 IS NULL OR completed_through IS NULL OR $7 >= completed_through)
        RETURNING version;
        """;

    private const string ReleaseSql = """
        UPDATE public.aggregation_watermarks
        SET lease_owner = NULL,
            lease_until = NULL,
            version = version + 1,
            updated_at = clock_timestamp()
        WHERE projector_name = $1
          AND partition_key = $2
          AND lease_owner = $3
          AND version = $4;
        """;

    public async ValueTask<UsageAggregationClaimResult> ClaimAsync(
        UsageAggregationClaimRequest request,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        NotBlank(request.ProjectorName, nameof(request.ProjectorName));
        NotBlank(request.PartitionKey, nameof(request.PartitionKey));
        NotBlank(request.Owner, nameof(request.Owner));
        Positive(request.LeaseDuration, nameof(request.LeaseDuration));

        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ClaimSql);
        command.Parameters.AddWithValue(request.ProjectorName);
        command.Parameters.AddWithValue(request.PartitionKey);
        command.Parameters.AddWithValue(request.Owner);
        command.Parameters.AddWithValue(request.LeaseDuration);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return UsageAggregationClaimResult.Busy;
        }

        return UsageAggregationClaimResult.Acquired(new UsageAggregationLease(
            request.ProjectorName,
            request.PartitionKey,
            request.Owner,
            reader.GetInt64(2),
            reader.GetInt64(0),
            ReadTimestamp(reader, 1)));
    }

    public async ValueTask<bool> HeartbeatAsync(
        UsageAggregationLease lease,
        TimeSpan leaseDuration,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        Validate(lease);
        Positive(leaseDuration, nameof(leaseDuration));
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(HeartbeatSql);
        AddLeaseParameters(command, lease);
        command.Parameters.AddWithValue(leaseDuration);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<UsageAggregationLease?> AdvanceAsync(
        UsageAggregationAdvanceRequest request,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Lease);
        if (request.NextEventSequence <= request.Lease.LastEventSequence)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The next event sequence must be greater than the checkpoint.");
        }

        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(AdvanceSql);
        AddLeaseParameters(command, request.Lease);
        command.Parameters.AddWithValue(request.Lease.LastEventSequence);
        command.Parameters.AddWithValue(request.NextEventSequence);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = (object?)request.CompletedThrough?.ToUniversalTime() ?? DBNull.Value,
        });
        object? version = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return version is long nextVersion
            ? request.Lease with
            {
                Version = nextVersion,
                LastEventSequence = request.NextEventSequence,
                CompletedThrough = request.CompletedThrough?.ToUniversalTime()
                    ?? request.Lease.CompletedThrough,
            }
            : null;
    }

    public async ValueTask<bool> ReleaseAsync(
        UsageAggregationLease lease,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        Validate(lease);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ReleaseSql);
        AddLeaseParameters(command, lease);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void AddLeaseParameters(NpgsqlCommand command, UsageAggregationLease lease)
    {
        command.Parameters.AddWithValue(lease.ProjectorName);
        command.Parameters.AddWithValue(lease.PartitionKey);
        command.Parameters.AddWithValue(lease.Owner);
        command.Parameters.AddWithValue(lease.Version);
    }

    private static DateTimeOffset? ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(reader.GetFieldValue<DateTime>(ordinal).ToUniversalTime());

    private static void Validate(UsageAggregationLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        NotBlank(lease.ProjectorName, nameof(lease.ProjectorName));
        NotBlank(lease.PartitionKey, nameof(lease.PartitionKey));
        NotBlank(lease.Owner, nameof(lease.Owner));
        if (lease.Version <= 0 || lease.LastEventSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lease));
        }
    }

    private static void NotBlank(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The value cannot have surrounding whitespace.", parameterName);
        }
    }

    private static void Positive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
