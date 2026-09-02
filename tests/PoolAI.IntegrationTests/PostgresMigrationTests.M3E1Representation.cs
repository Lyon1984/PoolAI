#pragma warning disable MA0051 // The upgrade-path evidence is intentionally linear.
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Npgsql;
using PoolAI.Database.Migrations;
using Testcontainers.PostgreSql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task M3E1RepresentationUpgradeDoesNotDeadlockWithInFlight0013Writer()
    {
        // Governing contract: docs/database/README.md requires forward migrations
        // and quota writers to preserve the shared Quota -> Group -> Period order.
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        PostgreSqlContainer container = new PostgreSqlBuilder(ReadPostgresImage())
            .WithDatabase("poolai")
            .WithUsername("postgres")
            .WithPassword(password)
            .Build();
        await using ConfiguredAsyncDisposable containerLease = container.ConfigureAwait(true);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await container.StartAsync(cancellationToken).ConfigureAwait(true);
        string connectionString = container.GetConnectionString();
        await ProvisionRuntimeRolesAsync(connectionString, cancellationToken).ConfigureAwait(true);

        MigrationCatalog catalog = await MigrationCatalog
            .LoadAsync(cancellationToken)
            .ConfigureAwait(true);
        await ApplyM3E1MigrationPrefixAsync(
            catalog,
            connectionString,
            cancellationToken).ConfigureAwait(true);
        await SeedPre0014QuotaRepresentationAsync(
            connectionString,
            cancellationToken).ConfigureAwait(true);

        const string MigratorApplicationName =
            "PoolAI.IntegrationTests.m3-e1-0014-lock-order";
        NpgsqlConnectionStringBuilder migratorConnectionString = new(connectionString)
        {
            ApplicationName = MigratorApplicationName,
        };
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlConnection writer = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(true);
        using NpgsqlTransaction writerTransaction = await writer
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(true);

        using (NpgsqlCommand acquireQuota = writer.CreateCommand())
        {
            acquireQuota.Transaction = writerTransaction;
            acquireQuota.CommandText = """
                SET LOCAL deadlock_timeout = '100ms';
                SELECT group_id
                FROM public.group_token_quotas
                WHERE group_id = '01900000-0000-7000-8000-00000000e100'
                FOR UPDATE;
                """;
            Assert.Equal(
                new Guid("01900000-0000-7000-8000-00000000e100"),
                Assert.IsType<Guid>(await acquireQuota
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(true)));
        }

        int writerProcessId = writer.ProcessID;
        PostgresMigrator migrator = new(catalog);
        Task migrationTask = migrator.ApplyAsync(
            migratorConnectionString.ConnectionString,
            "PoolAI.IntegrationTests.m3-e1-0014-lock-order",
            cancellationToken).AsTask();

        Assert.True(
            await WaitForM3E10014WriterDrainAsync(
                dataSource,
                MigratorApplicationName,
                writerProcessId,
                cancellationToken).ConfigureAwait(true),
            "Migration 0014 did not reach its writer-drain wait behind the 0013 writer.");

        Exception? writerFailure = await Record.ExceptionAsync(async () =>
        {
            using NpgsqlCommand acquireDownstreamRows = writer.CreateCommand();
            acquireDownstreamRows.Transaction = writerTransaction;
            acquireDownstreamRows.CommandText = """
                SELECT id
                FROM public.groups
                WHERE id = '01900000-0000-7000-8000-00000000e100'
                FOR SHARE;

                SELECT id
                FROM public.group_quota_periods
                WHERE id = '01900000-0000-7000-8000-00000000e101'
                  AND group_id = '01900000-0000-7000-8000-00000000e100'
                FOR UPDATE;
                """;
            await acquireDownstreamRows
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

        if (writerFailure is null)
        {
            await writerTransaction.CommitAsync(cancellationToken).ConfigureAwait(true);
        }
        else
        {
            await writerTransaction.RollbackAsync(cancellationToken).ConfigureAwait(true);
        }

        Exception? migrationFailure = await Record
            .ExceptionAsync(() => migrationTask)
            .ConfigureAwait(true);
        bool deadlockDetected = writerFailure is PostgresException
        {
            SqlState: PostgresErrorCodes.DeadlockDetected,
        }
            || migrationFailure is PostgresException
            {
                SqlState: PostgresErrorCodes.DeadlockDetected,
            };
        Assert.False(
            deadlockDetected,
            $"Migration 0014 inverted the Quota -> Group -> Period lock order. "
            + $"writer={writerFailure?.GetType().Name}:{writerFailure?.Message}; "
            + $"migration={migrationFailure?.GetType().Name}:{migrationFailure?.Message}");
        Assert.Null(writerFailure);
        Assert.Null(migrationFailure);
        Assert.Equal(
            "19:42:2026-07-01 03:00:00+00",
            await ReadM3E1EpochStateAsync(
                connectionString,
                cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task M3E1RepresentationEpochAndCounterTriggerAreForwardOnly()
    {
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        PostgreSqlContainer container = new PostgreSqlBuilder(ReadPostgresImage())
            .WithDatabase("poolai")
            .WithUsername("postgres")
            .WithPassword(password)
            .Build();
        await using ConfiguredAsyncDisposable containerLease = container.ConfigureAwait(true);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await container.StartAsync(cancellationToken).ConfigureAwait(true);
        string connectionString = container.GetConnectionString();
        await ProvisionRuntimeRolesAsync(connectionString, cancellationToken).ConfigureAwait(true);

        MigrationCatalog catalog = await MigrationCatalog
            .LoadAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(19, catalog.Assets.Count);
        await ApplyM3E1MigrationPrefixAsync(
            catalog,
            connectionString,
            cancellationToken).ConfigureAwait(true);
        await SeedPre0014QuotaRepresentationAsync(
            connectionString,
            cancellationToken).ConfigureAwait(true);

        PostgresMigrator migrator = new(catalog);
        await SetM3E1EpochQuotaVersionAsync(
            connectionString,
            long.MaxValue,
            cancellationToken).ConfigureAwait(true);
        PostgresException overflow = await Assert.ThrowsAsync<PostgresException>(
            () => migrator.ApplyAsync(
                connectionString,
                "PoolAI.IntegrationTests.m3-e1-overflow",
                cancellationToken).AsTask()).ConfigureAwait(true);
        Assert.Equal(PostgresErrorCodes.NumericValueOutOfRange, overflow.SqlState);
        Assert.Equal(
            "group_quota_representation_version_epoch_overflow",
            overflow.MessageText);
        Assert.Equal(
            "13:9223372036854775807",
            await ReadM3E1EpochHistoryAndVersionAsync(
                connectionString,
                cancellationToken).ConfigureAwait(true));

        await SetM3E1EpochQuotaVersionAsync(
            connectionString,
            41,
            cancellationToken).ConfigureAwait(true);
        await migrator.ApplyAsync(
            connectionString,
            "PoolAI.IntegrationTests.m3-e1-representation",
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            "19:42:2026-07-01 03:00:00+00",
            await ReadM3E1EpochStateAsync(
                connectionString,
                cancellationToken).ConfigureAwait(true));

        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(true);
        await SetM1E4ApiRoleAsync(connection, cancellationToken).ConfigureAwait(true);

        Guid groupId = new("01900000-0000-7000-8000-00000000e100");
        Guid oldPeriodId = new("01900000-0000-7000-8000-00000000e101");
        M3E1QuotaMutation adjustment = await ExecuteM3E1AdjustmentAsync(
            connection,
            null,
            groupId,
            150,
            42,
            new Guid("01900000-0000-7000-8000-00000000e110"),
            new Guid("01900000-0000-7000-8000-00000000e111"),
            "m3-e1-0014-adjust",
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("150:10:5:135:43", adjustment.Result);
        AssertM3E1BeforeState(
            adjustment.BeforeState,
            groupId,
            oldPeriodId,
            "active",
            "200",
            "10",
            "5",
            "185",
            "0",
            42);

        using (NpgsqlCommand counters = connection.CreateCommand())
        {
            counters.CommandText = """
                RESET ROLE;
                UPDATE public.group_quota_periods
                SET consumed_tokens = consumed_tokens,
                    reserved_tokens = reserved_tokens
                WHERE id = '01900000-0000-7000-8000-00000000e101';

                UPDATE public.group_quota_periods
                SET consumed_tokens = consumed_tokens + 1,
                    updated_at = '2026-07-01 04:00:00+00'::timestamptz
                WHERE id = '01900000-0000-7000-8000-00000000e101';
                SET ROLE poolai_api;
                """;
            await counters.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
        }
        Assert.Equal(
            44L,
            await ReadM3E1QuotaVersionAsync(
                connection,
                groupId,
                cancellationToken).ConfigureAwait(true));

        Guid newPeriodId = new("01900000-0000-7000-8000-00000000e120");
        Guid resetEventId = new("01900000-0000-7000-8000-00000000e121");
        Guid resetOutboxId = new("01900000-0000-7000-8000-00000000e122");
        M3E1QuotaMutation reset = await ExecuteM3E1ResetAsync(
            connection,
            null,
            groupId,
            newPeriodId,
            300,
            44,
            resetEventId,
            resetOutboxId,
            "m3-e1-0014-reset",
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("2:300:0:0:300:45", reset.Result);

        using (NpgsqlCommand lateOldPeriodWrite = connection.CreateCommand())
        {
            lateOldPeriodWrite.CommandText = """
                RESET ROLE;
                UPDATE public.group_quota_periods
                SET consumed_tokens = consumed_tokens + 1,
                    updated_at = '2026-07-01 05:00:00+00'::timestamptz
                WHERE id = '01900000-0000-7000-8000-00000000e101';
                SET ROLE poolai_api;
                """;
            await lateOldPeriodWrite
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(true);
        }
        Assert.Equal(
            45L,
            await ReadM3E1QuotaVersionAsync(
                connection,
                groupId,
                cancellationToken).ConfigureAwait(true));

        M3E1QuotaMutation replayBeforeGroupDrift = await ExecuteM3E1ResetAsync(
            connection,
            null,
            groupId,
            newPeriodId,
            300,
            44,
            resetEventId,
            resetOutboxId,
            "m3-e1-0014-reset",
            cancellationToken).ConfigureAwait(true);
        AssertM3E1BeforeState(
            replayBeforeGroupDrift.BeforeState,
            groupId,
            newPeriodId,
            "active",
            "300",
            "0",
            "0",
            "300",
            "0",
            45);

        using (NpgsqlCommand groupDrift = connection.CreateCommand())
        {
            groupDrift.CommandText = """
                RESET ROLE;
                UPDATE public.groups
                SET name = 'M3-E1 0014 Group Drift',
                    status = 'archived',
                    version = version + 1,
                    updated_at = '2026-07-01 06:00:00+00'::timestamptz,
                    deleted_at = '2026-07-01 06:00:00+00'::timestamptz
                WHERE id = '01900000-0000-7000-8000-00000000e100';
                SET ROLE poolai_api;
                """;
            await groupDrift.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
        }

        M3E1QuotaMutation replayAfterGroupDrift = await ExecuteM3E1ResetAsync(
            connection,
            null,
            groupId,
            newPeriodId,
            300,
            44,
            resetEventId,
            resetOutboxId,
            "m3-e1-0014-reset",
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(replayBeforeGroupDrift.Result, replayAfterGroupDrift.Result);
        Assert.Equal(
            replayBeforeGroupDrift.BeforeState,
            replayAfterGroupDrift.BeforeState);
        Assert.Equal(
            45L,
            await ReadM3E1QuotaVersionAsync(
                connection,
                groupId,
                cancellationToken).ConfigureAwait(true));
    }

    private static async ValueTask ApplyM3E1MigrationPrefixAsync(
        MigrationCatalog catalog,
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (MigrationAsset asset in catalog.Assets.Take(13))
        {
            using (NpgsqlCommand migration = new(asset.Sql, connection, transaction)
            {
                CommandTimeout = 0,
            })
            {
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using NpgsqlCommand history = new(
                """
                INSERT INTO public.poolai_schema_migrations (
                    version, name, checksum_sha256, applied_by
                ) VALUES ($1, $2, $3, 'PoolAI.IntegrationTests.m3-e1-prefix');
                """,
                connection,
                transaction);
            history.Parameters.AddWithValue(asset.Version);
            history.Parameters.AddWithValue(asset.Name);
            history.Parameters.AddWithValue(asset.ChecksumSha256);
            await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SeedPre0014QuotaRepresentationAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            BEGIN;
            INSERT INTO public.users (
                id, email, normalized_email, display_name,
                password_hash, security_stamp
            ) VALUES (
                '01900000-0000-7000-8000-00000000d000',
                'm3e1-0014@example.test', 'm3e1-0014@example.test',
                'M3-E1 0014 Actor', 'poolai-password-v1:test',
                '01900000-0000-7000-8000-00000000e001'
            );
            INSERT INTO public.groups (
                id, name, status, version, created_at, updated_at
            ) VALUES (
                '01900000-0000-7000-8000-00000000e100',
                'M3-E1 0014 Group', 'disabled', 7,
                '2026-07-01 00:00:00+00'::timestamptz,
                '2026-07-01 03:00:00+00'::timestamptz
            );
            INSERT INTO public.group_token_quotas (
                group_id, current_period_id, enabled, version,
                created_at, updated_at
            ) VALUES (
                '01900000-0000-7000-8000-00000000e100',
                '01900000-0000-7000-8000-00000000e101', true, 41,
                '2026-07-01 00:00:00+00'::timestamptz,
                '2026-07-01 01:00:00+00'::timestamptz
            );
            INSERT INTO public.group_quota_periods (
                id, group_id, period_number, total_tokens,
                consumed_tokens, reserved_tokens, status,
                opened_at, version, created_at, updated_at
            ) VALUES (
                '01900000-0000-7000-8000-00000000e101',
                '01900000-0000-7000-8000-00000000e100', 1, 200,
                10, 5, 'current',
                '2026-07-01 00:00:00+00'::timestamptz, 3,
                '2026-07-01 00:00:00+00'::timestamptz,
                '2026-07-01 02:00:00+00'::timestamptz
            );
            COMMIT;
            """;
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SetM3E1EpochQuotaVersionAsync(
        string connectionString,
        long version,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand("""
            UPDATE public.group_token_quotas SET version = $1
            WHERE group_id = '01900000-0000-7000-8000-00000000e100';
            """);
        command.Parameters.AddWithValue(version);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<string> ReadM3E1EpochHistoryAndVersionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT count(*)::text || ':' || quota.version::text
            FROM public.poolai_schema_migrations
            CROSS JOIN public.group_token_quotas AS quota
            WHERE quota.group_id = '01900000-0000-7000-8000-00000000e100'
            GROUP BY quota.version;
            """);
        return Assert.IsType<string>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<string> ReadM3E1EpochStateAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT count(*)::text || ':' || quota.version::text || ':'
                || to_char(quota.updated_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS')
                || '+00'
            FROM public.poolai_schema_migrations
            CROSS JOIN public.group_token_quotas AS quota
            WHERE quota.group_id = '01900000-0000-7000-8000-00000000e100'
            GROUP BY quota.version, quota.updated_at;
            """);
        return Assert.IsType<string>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<long> ReadM3E1QuotaVersionAsync(
        NpgsqlConnection connection,
        Guid groupId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT version
            FROM public.group_token_quotas
            WHERE group_id = $1;
            """;
        command.Parameters.AddWithValue(groupId);
        return Assert.IsType<long>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<bool> WaitForM3E10014WriterDrainAsync(
        NpgsqlDataSource dataSource,
        string applicationName,
        int writerProcessId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            using NpgsqlCommand command = dataSource.CreateCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_stat_activity AS activity
                    WHERE activity.application_name = $1
                      AND activity.wait_event_type = 'Lock'
                      AND $2 = ANY (pg_catalog.pg_blocking_pids(activity.pid))
                      AND EXISTS (
                          SELECT 1
                          FROM pg_catalog.pg_locks AS table_lock
                          WHERE table_lock.pid = activity.pid
                            AND table_lock.locktype = 'relation'
                            AND table_lock.relation =
                                'public.group_token_quotas'::regclass
                            AND (
                                (
                                    table_lock.mode = 'ShareRowExclusiveLock'
                                    AND table_lock.granted
                                )
                                OR (
                                    table_lock.mode = 'ExclusiveLock'
                                    AND NOT table_lock.granted
                                )
                            )
                      )
                );
                """);
            command.Parameters.AddWithValue(applicationName);
            command.Parameters.AddWithValue(writerProcessId);
            if (await command
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false) is true)
            {
                return true;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(25),
                cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
#pragma warning restore MA0051
