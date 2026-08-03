using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Usage.Application;
using PoolAI.Modules.Usage.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresUsageReconciliationProjectionReaderTests(
    PostgresRuntimeFixture fixture)
{
    private static readonly BigInteger MaximumNumeric78 = BigInteger.Parse(
        new string('9', 78),
        System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReaderAtomicallyScopesCurrentAndClosedPeriodsAndGroupWatermarks()
    {
        // Governing contract: ADR 0013 Layer 2 and database README sections 8-9.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProjectionScenario scenario = ProjectionScenario.Create();
        await SeedScopedScenarioAsync(
            scenario,
            includeSelectedWatermark: true,
            cancellationToken).ConfigureAwait(true);

        DatabaseBoundedSnapshot current = await ReadWithDatabaseClockBoundsAsync(
            scenario.GroupId,
            scenario.CurrentPeriodId,
            cancellationToken).ConfigureAwait(true);
        DatabaseBoundedSnapshot closed = await ReadWithDatabaseClockBoundsAsync(
            scenario.GroupId,
            scenario.ClosedPeriodId,
            cancellationToken).ConfigureAwait(true);
        DatabaseBoundedSnapshot otherGroup = await ReadWithDatabaseClockBoundsAsync(
            scenario.OtherGroupId,
            scenario.OtherPeriodId,
            cancellationToken).ConfigureAwait(true);

        AssertSnapshot(
            current,
            scenario.GroupId,
            scenario.CurrentPeriodId,
            projectedTokens: new BigInteger(40),
            checkpoint: scenario.Checkpoint,
            dataThrough: scenario.DataThrough);
        AssertSnapshot(
            closed,
            scenario.GroupId,
            scenario.ClosedPeriodId,
            projectedTokens: new BigInteger(71),
            checkpoint: scenario.Checkpoint,
            dataThrough: scenario.DataThrough);
        AssertSnapshot(
            otherGroup,
            scenario.OtherGroupId,
            scenario.OtherPeriodId,
            projectedTokens: new BigInteger(997),
            checkpoint: scenario.OtherCheckpoint,
            dataThrough: scenario.OtherDataThrough);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReaderUsesInitialCheckpointWhenGroupWatermarkDoesNotExist()
    {
        // Governing contract: ADR 0013 Layer 2 defines no watermark as checkpoint zero.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProjectionScenario scenario = ProjectionScenario.Create();
        await SeedScopedScenarioAsync(
            scenario,
            includeSelectedWatermark: false,
            cancellationToken).ConfigureAwait(true);

        DatabaseBoundedSnapshot read = await ReadWithDatabaseClockBoundsAsync(
            scenario.GroupId,
            scenario.CurrentPeriodId,
            cancellationToken).ConfigureAwait(true);

        AssertSnapshot(
            read,
            scenario.GroupId,
            scenario.CurrentPeriodId,
            projectedTokens: new BigInteger(40),
            checkpoint: 0,
            dataThrough: null);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReaderPreservesNumeric78AndRejectsAnAggregateBeyondTheAbi()
    {
        // Governing contract: D-002 and ADR 0013 require exact numeric(78,0)/BigInteger values.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProjectionScenario scenario = ProjectionScenario.Create();
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        await InsertGroupAndPeriodsAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(true);
        await InsertUsageHourAsync(
            connection,
            transaction,
            scenario.GroupId,
            scenario.CurrentPeriodId,
            scenario.BucketStart,
            MaximumNumeric78,
            cancellationToken).ConfigureAwait(true);
        await InsertWatermarkAsync(
            connection,
            transaction,
            "usage-hourly-v1",
            Partition(scenario.GroupId),
            long.MaxValue,
            scenario.DataThrough,
            cancellationToken).ConfigureAwait(true);
        PostgresTransactionSession session = new(connection, transaction);
        PostgresUsageReconciliationProjectionReader reader = new();

        UsageReconciliationProjectionSnapshot maximum = await reader.ReadAsync(
            scenario.GroupId,
            scenario.CurrentPeriodId,
            session,
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(MaximumNumeric78, maximum.ProjectedConsumedTokens);
        Assert.Equal(long.MaxValue, maximum.CheckpointSourceEventSequence);

        await InsertUsageHourAsync(
            connection,
            transaction,
            scenario.GroupId,
            scenario.CurrentPeriodId,
            scenario.BucketStart.AddHours(1),
            BigInteger.One,
            cancellationToken).ConfigureAwait(true);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => reader.ReadAsync(
                scenario.GroupId,
                scenario.CurrentPeriodId,
                session,
                cancellationToken).AsTask()).ConfigureAwait(true);
        Assert.Equal(
            "The PostgreSQL Usage reconciliation projection violated its ABI.",
            exception.Message);
    }

    [Theory]
    [InlineData("negative_tokens")]
    [InlineData("negative_checkpoint")]
    [InlineData("future_completed_through")]
    [Trait("Category", "PostgreSQL")]
    public async Task ReaderRejectsPersistedProjectionAbiCorruption(string corruption)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProjectionScenario scenario = ProjectionScenario.Create();
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        await InsertGroupAndPeriodsAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(true);
        await InsertUsageHourAsync(
            connection,
            transaction,
            scenario.GroupId,
            scenario.CurrentPeriodId,
            scenario.BucketStart,
            new BigInteger(1),
            cancellationToken).ConfigureAwait(true);
        await InsertWatermarkAsync(
            connection,
            transaction,
            "usage-hourly-v1",
            Partition(scenario.GroupId),
            sequence: 1,
            scenario.DataThrough,
            cancellationToken).ConfigureAwait(true);
        await CorruptAsync(connection, transaction, scenario, corruption, cancellationToken)
            .ConfigureAwait(true);
        PostgresTransactionSession session = new(connection, transaction);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => new PostgresUsageReconciliationProjectionReader()
                .ReadAsync(
                    scenario.GroupId,
                    scenario.CurrentPeriodId,
                    session,
                    cancellationToken).AsTask()).ConfigureAwait(true);

        Assert.Equal(
            "The PostgreSQL Usage reconciliation projection violated its ABI.",
            exception.Message);
    }

    private async ValueTask SeedScopedScenarioAsync(
        ProjectionScenario scenario,
        bool includeSelectedWatermark,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await InsertGroupAndPeriodsAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(false);
        await InsertScopedProjectionsAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(false);
        await InsertScopedWatermarksAsync(
            connection,
            transaction,
            scenario,
            includeSelectedWatermark,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertScopedProjectionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProjectionScenario scenario,
        CancellationToken cancellationToken)
    {
        await InsertUsageHourAsync(
            connection,
            transaction,
            scenario.GroupId,
            scenario.CurrentPeriodId,
            scenario.BucketStart,
            new BigInteger(17),
            cancellationToken).ConfigureAwait(false);
        await InsertUsageHourAsync(
            connection,
            transaction,
            scenario.GroupId,
            scenario.CurrentPeriodId,
            scenario.BucketStart.AddHours(1),
            new BigInteger(23),
            cancellationToken).ConfigureAwait(false);
        await InsertUsageHourAsync(
            connection,
            transaction,
            scenario.GroupId,
            scenario.ClosedPeriodId,
            scenario.BucketStart.AddHours(-1),
            new BigInteger(71),
            cancellationToken).ConfigureAwait(false);
        await InsertUsageHourAsync(
            connection,
            transaction,
            scenario.OtherGroupId,
            scenario.OtherPeriodId,
            scenario.BucketStart,
            new BigInteger(997),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertScopedWatermarksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProjectionScenario scenario,
        bool includeSelectedWatermark,
        CancellationToken cancellationToken)
    {
        if (includeSelectedWatermark)
        {
            await InsertWatermarkAsync(
                connection,
                transaction,
                "usage-hourly-v1",
                Partition(scenario.GroupId),
                scenario.Checkpoint,
                scenario.DataThrough,
                cancellationToken).ConfigureAwait(false);
        }

        await InsertWatermarkAsync(
            connection,
            transaction,
            "usage-hourly-v1",
            Partition(scenario.OtherGroupId),
            scenario.OtherCheckpoint,
            scenario.OtherDataThrough,
            cancellationToken).ConfigureAwait(false);
        await InsertWatermarkAsync(
            connection,
            transaction,
            "other-projector-v1",
            Partition(scenario.GroupId),
            sequence: 8_888,
            scenario.OtherDataThrough,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DatabaseBoundedSnapshot> ReadWithDatabaseClockBoundsAsync(
        EntityId groupId,
        EntityId periodId,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
                unitOfWork.Context);
            DateTimeOffset before = await ReadDatabaseClockAsync(session, cancellationToken)
                .ConfigureAwait(false);
            UsageReconciliationProjectionSnapshot snapshot = await new
                PostgresUsageReconciliationProjectionReader().ReadAsync(
                    groupId,
                    periodId,
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
            DateTimeOffset after = await ReadDatabaseClockAsync(session, cancellationToken)
                .ConfigureAwait(false);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DatabaseBoundedSnapshot(snapshot, before, after);
        }
    }

    private static async ValueTask<DateTimeOffset> ReadDatabaseClockAsync(
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("SELECT clock_timestamp();");
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        DateTimeOffset value = reader.GetFieldValue<DateTimeOffset>(0);
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return value;
    }

    private static void AssertSnapshot(
        DatabaseBoundedSnapshot read,
        EntityId groupId,
        EntityId periodId,
        BigInteger projectedTokens,
        long checkpoint,
        DateTimeOffset? dataThrough)
    {
        Assert.Equal(groupId, read.Snapshot.GroupId);
        Assert.Equal(periodId, read.Snapshot.PeriodId);
        Assert.Equal(projectedTokens, read.Snapshot.ProjectedConsumedTokens);
        Assert.Equal(checkpoint, read.Snapshot.CheckpointSourceEventSequence);
        Assert.Equal(dataThrough, read.Snapshot.DataThrough);
        Assert.InRange(read.Snapshot.CheckedAt, read.Before, read.After);
        Assert.Equal(TimeSpan.Zero, read.Snapshot.CheckedAt.Offset);
    }

    private static async ValueTask InsertGroupAndPeriodsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProjectionScenario scenario,
        CancellationToken cancellationToken)
    {
        await InsertGroupsAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(false);
        await InsertQuotasAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(false);
        await InsertPeriodsAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask InsertGroupsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProjectionScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(SeedGroupsSql, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.GroupId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.OtherGroupId.Value);
        Assert.Equal(
            2,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertQuotasAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProjectionScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(SeedQuotasSql, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.GroupId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.CurrentPeriodId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.OtherGroupId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.OtherPeriodId.Value);
        Assert.Equal(
            2,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertPeriodsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProjectionScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(SeedPeriodsSql, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.ClosedPeriodId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.GroupId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.CurrentPeriodId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.OtherPeriodId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.OtherGroupId.Value);
        command.Parameters.AddWithValue(
            NpgsqlDbType.TimestampTz,
            scenario.BucketStart.AddDays(-1));
        Assert.Equal(
            3,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertUsageHourAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntityId groupId,
        EntityId periodId,
        DateTimeOffset bucketStart,
        BigInteger totalTokens,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new("""
            INSERT INTO public.group_usage_hourly (
                group_id, period_id, bucket_start,
                input_tokens, output_tokens, total_tokens
            ) VALUES ($1, $2, $3, $4, 0, $4);
            """, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, groupId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, periodId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, bucketStart);
        command.Parameters.AddWithValue(NpgsqlDbType.Numeric, totalTokens);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertWatermarkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectorName,
        string partitionKey,
        long sequence,
        DateTimeOffset completedThrough,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new("""
            INSERT INTO public.aggregation_watermarks (
                projector_name, partition_key,
                last_event_sequence, completed_through
            ) VALUES ($1, $2, $3, $4);
            """, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, projectorName);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, partitionKey);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, sequence);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, completedThrough);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask CorruptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProjectionScenario scenario,
        string corruption,
        CancellationToken cancellationToken)
    {
        string[] statements = corruption switch
        {
            "negative_tokens" =>
            [
                "ALTER TABLE public.group_usage_hourly "
                    + "DROP CONSTRAINT ck_group_usage_hourly_tokens",
                "UPDATE public.group_usage_hourly "
                    + "SET input_tokens = -1, total_tokens = -1 "
                    + "WHERE group_id = $1 AND period_id = $2",
            ],
            "negative_checkpoint" =>
            [
                "ALTER TABLE public.aggregation_watermarks "
                    + "DROP CONSTRAINT ck_aggregation_watermarks_sequence",
                "UPDATE public.aggregation_watermarks "
                    + "SET last_event_sequence = -1 "
                    + "WHERE projector_name = 'usage-hourly-v1' "
                    + "AND partition_key = $1",
            ],
            "future_completed_through" =>
            [
                "UPDATE public.aggregation_watermarks "
                    + "SET completed_through = clock_timestamp() + interval '1 day' "
                    + "WHERE projector_name = 'usage-hourly-v1' "
                    + "AND partition_key = $1",
            ],
            _ => throw new InvalidOperationException("Unknown projection corruption."),
        };

        foreach (string statement in statements)
        {
            using NpgsqlCommand command = new(statement, connection, transaction);
            if (statement.Contains("WHERE group_id", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue(NpgsqlDbType.Uuid, scenario.GroupId.Value);
                command.Parameters.AddWithValue(
                    NpgsqlDbType.Uuid,
                    scenario.CurrentPeriodId.Value);
            }
            else if (statement.Contains("$1", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue(
                    NpgsqlDbType.Text,
                    Partition(scenario.GroupId));
            }

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Partition(EntityId groupId) =>
        PostgresUsageReconciliationProjectionReader.Partition(groupId);

    private sealed record DatabaseBoundedSnapshot(
        UsageReconciliationProjectionSnapshot Snapshot,
        DateTimeOffset Before,
        DateTimeOffset After);

    private sealed record ProjectionScenario(
        EntityId GroupId,
        EntityId CurrentPeriodId,
        EntityId ClosedPeriodId,
        EntityId OtherGroupId,
        EntityId OtherPeriodId,
        DateTimeOffset BucketStart,
        long Checkpoint,
        long OtherCheckpoint,
        DateTimeOffset DataThrough,
        DateTimeOffset OtherDataThrough)
    {
        internal static ProjectionScenario Create()
        {
            DateTimeOffset bucketStart = new(2025, 8, 3, 6, 0, 0, TimeSpan.Zero);
            return new ProjectionScenario(
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                bucketStart,
                Checkpoint: 4_294_967_301,
                OtherCheckpoint: 4_294_967_399,
                DataThrough: bucketStart.AddHours(2),
                OtherDataThrough: bucketStart.AddHours(3));
        }
    }

    private const string SeedGroupsSql = """
        INSERT INTO public.groups (id, name, status)
        VALUES
            ($1, 'usage-reconciliation-' || $1::text, 'disabled'),
            ($2, 'usage-reconciliation-' || $2::text, 'disabled');
        """;

    private const string SeedQuotasSql = """
        INSERT INTO public.group_token_quotas (group_id, current_period_id)
        VALUES ($1, $2), ($3, $4);
        """;

    private const string SeedPeriodsSql = """
        INSERT INTO public.group_quota_periods (
            id, group_id, period_number, total_tokens,
            consumed_tokens, reserved_tokens, status, opened_at, closed_at
        ) VALUES
            ($1, $2, 1, 1000, 71, 0, 'closed', $6, $6 + interval '12 hours'),
            ($3, $2, 2, 1000, 40, 0, 'current', $6 + interval '12 hours', NULL),
            ($4, $5, 1, 1000, 997, 0, 'current', $6, NULL);
        """;
}
