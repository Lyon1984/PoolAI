#pragma warning disable MA0051 // The v17 failure and v18 upgrade proof stay visible as one contract scenario.
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
    public async Task M3ExitDispatchClockRegressionFailsAt17AndIsCorrectedAt18()
    {
        // Governing contract: DEC-026 assigns reservation time to PostgreSQL,
        // while the frozen dispatch CHECK requires the persisted fence to be no
        // earlier than reservation creation even if that wall clock steps back.
        string postgresPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        string migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        PostgreSqlContainer container = new PostgreSqlBuilder(ReadPostgresImage())
            .WithDatabase("poolai")
            .WithUsername("postgres")
            .WithPassword(postgresPassword)
            .Build();
        await using ConfiguredAsyncDisposable containerLease = container.ConfigureAwait(true);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await container.StartAsync(cancellationToken).ConfigureAwait(true);
        string administratorConnectionString = container.GetConnectionString();
        await ProvisionRuntimeRolesAsync(administratorConnectionString, cancellationToken)
            .ConfigureAwait(true);
        string migratorConnectionString = await ProvisionComposeMigratorAsync(
            administratorConnectionString,
            migratorPassword,
            cancellationToken).ConfigureAwait(true);
        MigrationCatalog catalog = await MigrationCatalog
            .LoadAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(18, catalog.Assets.Count);
        await ApplyM3ExitMigrationPrefixAsync(
            catalog,
            migratorConnectionString,
            cancellationToken).ConfigureAwait(true);

        using NpgsqlDataSource administrator = NpgsqlDataSource.Create(
            administratorConnectionString);
        using NpgsqlConnection connection = await administrator
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(true);
        await SeedM3ExitFutureReservationAsync(connection, cancellationToken)
            .ConfigureAwait(true);
        M3ExitDispatchClockEvidence before = await ReadM3ExitDispatchClockEvidenceAsync(
            connection,
            cancellationToken).ConfigureAwait(true);
        Assert.Null(before.DispatchStartedAt);
        Assert.Null(before.EventDispatchStartedAt);
        Assert.Equal(0, before.DispatchEventCount);
        Assert.Equal(0, before.DispatchOutboxCount);
        M3ExitDispatchClockEvidence expiredBefore =
            await ReadM3ExitExpiredDispatchClockEvidenceAsync(
                connection,
                cancellationToken).ConfigureAwait(true);
        Assert.Null(expiredBefore.DispatchStartedAt);
        Assert.Null(expiredBefore.EventDispatchStartedAt);
        Assert.Equal(0, expiredBefore.DispatchEventCount);
        Assert.Equal(0, expiredBefore.DispatchOutboxCount);
        await AssertM3ExitExpiredTemporalFixtureAsync(connection, cancellationToken)
            .ConfigureAwait(true);
        string securityAt17 = await ReadM3ExitDispatchFunctionSecurityAsync(
            connection,
            cancellationToken).ConfigureAwait(true);
        long functionOidAt17 = await ReadM3ExitDispatchFunctionOidAsync(
            connection,
            cancellationToken).ConfigureAwait(true);

        await SetM1E4ApiRoleAsync(connection, cancellationToken).ConfigureAwait(true);
        PostgresException checkViolation = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteM3ExitDispatchAsync(connection, cancellationToken).AsTask())
            .ConfigureAwait(true);
        Assert.Equal(PostgresErrorCodes.CheckViolation, checkViolation.SqlState);
        Assert.Equal("ck_group_token_reservations_dispatch", checkViolation.ConstraintName);

        await ResetM3ExitRoleAsync(connection, cancellationToken).ConfigureAwait(true);
        M3ExitDispatchClockEvidence afterFailed17 =
            await ReadM3ExitDispatchClockEvidenceAsync(connection, cancellationToken)
                .ConfigureAwait(true);
        Assert.Equal(before, afterFailed17);

        await new PostgresMigrator(catalog).ApplyAsync(
            migratorConnectionString,
            "PoolAI.IntegrationTests.m3-exit-dispatch-clock",
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            securityAt17,
            await ReadM3ExitDispatchFunctionSecurityAsync(connection, cancellationToken)
                .ConfigureAwait(true));
        Assert.Equal(
            functionOidAt17,
            await ReadM3ExitDispatchFunctionOidAsync(connection, cancellationToken)
                .ConfigureAwait(true));
        Assert.Equal(
            18L,
            await ReadM3ExitTopMigrationAsync(connection, cancellationToken)
                .ConfigureAwait(true));

        await SetM1E4ApiRoleAsync(connection, cancellationToken).ConfigureAwait(true);
        DateTime dispatchStartedAt = await ExecuteM3ExitDispatchAsync(
            connection,
            cancellationToken).ConfigureAwait(true);
        DateTime replayedDispatchStartedAt = await ExecuteM3ExitDispatchAsync(
            connection,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(dispatchStartedAt, replayedDispatchStartedAt);
        PostgresException expiredLease = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteM3ExitExpiredDispatchAsync(connection, cancellationToken).AsTask())
            .ConfigureAwait(true);
        Assert.Equal(PostgresErrorCodes.RaiseException, expiredLease.SqlState);
        Assert.Equal("reservation_lease_expired", expiredLease.MessageText);

        await ResetM3ExitRoleAsync(connection, cancellationToken).ConfigureAwait(true);
        M3ExitDispatchClockEvidence corrected = await ReadM3ExitDispatchClockEvidenceAsync(
            connection,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(before.UpdatedAt, dispatchStartedAt);
        Assert.Equal(before.CreatedAt, corrected.CreatedAt);
        Assert.Equal(dispatchStartedAt, corrected.UpdatedAt);
        Assert.Equal(dispatchStartedAt, corrected.DispatchStartedAt);
        Assert.Equal(dispatchStartedAt, corrected.EventDispatchStartedAt);
        Assert.Equal(1, corrected.DispatchEventCount);
        Assert.Equal(1, corrected.DispatchOutboxCount);
        Assert.Equal(
            expiredBefore,
            await ReadM3ExitExpiredDispatchClockEvidenceAsync(
                connection,
                cancellationToken).ConfigureAwait(true));
    }

    private static async ValueTask ApplyM3ExitMigrationPrefixAsync(
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
        foreach (MigrationAsset asset in catalog.Assets.Take(17))
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
                ) VALUES ($1, $2, $3, 'PoolAI.IntegrationTests.m3-exit-prefix');
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

    private static async ValueTask SeedM3ExitFutureReservationAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await SeedM3E1ActorAsync(connection, cancellationToken).ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(connection, cancellationToken).ConfigureAwait(false);
        await CreateM3E1GroupAsync(
            connection,
            new Guid("01900000-0000-7000-8000-00000000f010"),
            new Guid("01900000-0000-7000-8000-00000000f011"),
            new Guid("01900000-0000-7000-8000-00000000f012"),
            new Guid("01900000-0000-7000-8000-00000000f013"),
            "M3 Exit Clock Group",
            "m3-exit-clock-initialize",
            cancellationToken).ConfigureAwait(false);

        using NpgsqlCommand seed = connection.CreateCommand();
        seed.CommandText = """
            RESET ROLE;
            INSERT INTO public.channels (
                id, provider, name, model_rules, capabilities, status
            ) VALUES (
                '01900000-0000-7000-8000-00000000f014',
                'openai', 'M3 Exit Clock Channel',
                '{"gpt-m3-exit":"gpt-m3-exit"}'::jsonb,
                '{"responses":true,"chat_completions":true,
                  "function_tools":true,"streaming":true}'::jsonb,
                'active'
            );
            INSERT INTO public.accounts (
                id, provider, name, auth_type, upstream_base_url,
                credential_envelope, credential_prefix,
                status, last_health_at, last_health_status
            ) VALUES (
                '01900000-0000-7000-8000-00000000f015',
                'openai', 'M3 Exit Clock Account', 'api_key',
                'https://example.test/v1',
                '{"v":1,"alg":"A256GCM+A256GCM-v1","kid":"test-kek-v1",
                  "wrapped_dek":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                  "wrap_nonce":"AAAAAAAAAAAAAAAA","wrap_tag":"AAAAAAAAAAAAAAAAAAAAAA",
                  "ciphertext":"bTMtZXhpdC1jbG9jaw","nonce":"AQEBAQEBAQEBAQEB",
                  "tag":"AgICAgICAgICAgICAgICAg"}'::jsonb,
                'sk-m3-exit-clock', 'active', clock_timestamp(), 'healthy'
            );
            INSERT INTO public.group_supply_configurations (group_id, channel_id)
            VALUES (
                '01900000-0000-7000-8000-00000000f010',
                '01900000-0000-7000-8000-00000000f014'
            );
            INSERT INTO public.group_accounts (group_id, account_id, is_enabled)
            VALUES (
                '01900000-0000-7000-8000-00000000f010',
                '01900000-0000-7000-8000-00000000f015', true
            );
            INSERT INTO public.subscription_templates (
                id, group_id, name, default_duration_days
            ) VALUES (
                '01900000-0000-7000-8000-00000000f016',
                '01900000-0000-7000-8000-00000000f010',
                'M3 Exit Clock Template', 30
            );
            INSERT INTO public.subscriptions (
                id, user_id, group_id, template_id, template_name_snapshot,
                status, starts_at, expires_at, assigned_by, change_reason
            ) VALUES (
                '01900000-0000-7000-8000-00000000f017',
                '01900000-0000-7000-8000-00000000d000',
                '01900000-0000-7000-8000-00000000f010',
                '01900000-0000-7000-8000-00000000f016',
                'M3 Exit Clock Template', 'active',
                clock_timestamp() - interval '1 day',
                clock_timestamp() + interval '30 days',
                '01900000-0000-7000-8000-00000000d000',
                'M3 Exit clock regression fixture'
            );
            INSERT INTO public.api_keys (
                id, user_id, group_id, name, key_prefix,
                secret_hash, pepper_version
            ) VALUES (
                '01900000-0000-7000-8000-00000000f018',
                '01900000-0000-7000-8000-00000000d000',
                '01900000-0000-7000-8000-00000000f010',
                'M3 Exit Clock Key', 'sk-m3exitclock1',
                pg_catalog.decode(repeat('ab', 32), 'hex'), 1
            );
            INSERT INTO public.usage_requests (
                request_id, user_id, api_key_id, subscription_id,
                quota_group_id, routing_group_id, endpoint,
                requested_model, is_streaming
            ) VALUES (
                '01900000-0000-7000-8000-00000000f019',
                '01900000-0000-7000-8000-00000000d000',
                '01900000-0000-7000-8000-00000000f018',
                '01900000-0000-7000-8000-00000000f017',
                '01900000-0000-7000-8000-00000000f010',
                '01900000-0000-7000-8000-00000000f010',
                '/v1/responses', 'gpt-m3-exit', false
            ), (
                '01900000-0000-7000-8000-00000000f022',
                '01900000-0000-7000-8000-00000000d000',
                '01900000-0000-7000-8000-00000000f018',
                '01900000-0000-7000-8000-00000000f017',
                '01900000-0000-7000-8000-00000000f010',
                '01900000-0000-7000-8000-00000000f010',
                '/v1/responses', 'gpt-m3-exit', false
            );
            WITH database_time AS MATERIALIZED (
                SELECT clock_timestamp() AS value
            )
            INSERT INTO public.group_token_reservations (
                id, period_id, group_id, request_id, attempt_id,
                attempt_index, account_id, channel_id, estimated_tokens,
                status, is_streaming, lease_owner,
                lease_expires_at, max_expires_at, created_at, updated_at
            )
            SELECT
                '01900000-0000-7000-8000-00000000f01a'::uuid,
                '01900000-0000-7000-8000-00000000f011'::uuid,
                '01900000-0000-7000-8000-00000000f010'::uuid,
                '01900000-0000-7000-8000-00000000f019'::uuid,
                '01900000-0000-7000-8000-00000000f01b'::uuid, 0,
                '01900000-0000-7000-8000-00000000f015'::uuid,
                '01900000-0000-7000-8000-00000000f014'::uuid,
                5, 'pending', false, 'm3-exit-clock-owner',
                database_time.value + interval '5 minutes',
                database_time.value + interval '10 minutes',
                database_time.value + interval '30 seconds',
                database_time.value + interval '30 seconds'
            FROM database_time
            UNION ALL
            SELECT
                '01900000-0000-7000-8000-00000000f023',
                '01900000-0000-7000-8000-00000000f011',
                '01900000-0000-7000-8000-00000000f010',
                '01900000-0000-7000-8000-00000000f022',
                '01900000-0000-7000-8000-00000000f024', 0,
                '01900000-0000-7000-8000-00000000f015',
                '01900000-0000-7000-8000-00000000f014',
                5, 'pending', false, 'm3-exit-expired-owner',
                database_time.value - interval '30 seconds',
                database_time.value + interval '4 minutes 30 seconds',
                database_time.value - interval '5 minutes 30 seconds',
                database_time.value + interval '30 seconds'
            FROM database_time;
            UPDATE public.group_quota_periods
            SET reserved_tokens = 10,
                version = version + 1,
                updated_at = clock_timestamp()
            WHERE id = '01900000-0000-7000-8000-00000000f011';
            """;
        await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<DateTime> ExecuteM3ExitDispatchAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT result_dispatch_started_at
            FROM public.poolai_quota_mark_dispatched(
                '01900000-0000-7000-8000-00000000f010',
                '01900000-0000-7000-8000-00000000f01b',
                'm3-exit-clock-owner', 'openai', 'gpt-m3-exit', 3, 2,
                '01900000-0000-7000-8000-00000000f020',
                '01900000-0000-7000-8000-00000000f021',
                'm3-exit-clock-dispatch');
            """;
        return Assert.IsType<DateTime>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<DateTime> ExecuteM3ExitExpiredDispatchAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT result_dispatch_started_at
            FROM public.poolai_quota_mark_dispatched(
                '01900000-0000-7000-8000-00000000f010',
                '01900000-0000-7000-8000-00000000f024',
                'm3-exit-expired-owner', 'openai', 'gpt-m3-exit', 3, 2,
                '01900000-0000-7000-8000-00000000f025',
                '01900000-0000-7000-8000-00000000f026',
                'm3-exit-expired-dispatch');
            """;
        return Assert.IsType<DateTime>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<M3ExitDispatchClockEvidence>
        ReadM3ExitDispatchClockEvidenceAsync(
            NpgsqlConnection connection,
            CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                reservation.created_at,
                reservation.updated_at,
                reservation.dispatch_started_at,
                (SELECT (event.metadata ->> 'dispatch_started_at')::timestamptz
                 FROM public.group_quota_events AS event
                 WHERE event.id = '01900000-0000-7000-8000-00000000f020'),
                (SELECT count(*)::integer
                 FROM public.group_quota_events AS event
                 WHERE event.id = '01900000-0000-7000-8000-00000000f020'),
                (SELECT count(*)::integer
                 FROM public.outbox_messages AS message
                 WHERE message.id = '01900000-0000-7000-8000-00000000f021'
                   AND message.payload ->> 'event_id'
                       = '01900000-0000-7000-8000-00000000f020')
            FROM public.group_token_reservations AS reservation
            WHERE reservation.id = '01900000-0000-7000-8000-00000000f01a';
            """;
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M3ExitDispatchClockEvidence evidence = new(
            reader.GetFieldValue<DateTime>(0),
            reader.GetFieldValue<DateTime>(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTime>(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTime>(3),
            reader.GetInt32(4),
            reader.GetInt32(5));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return evidence;
    }

    private static async ValueTask<M3ExitDispatchClockEvidence>
        ReadM3ExitExpiredDispatchClockEvidenceAsync(
            NpgsqlConnection connection,
            CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                reservation.created_at,
                reservation.updated_at,
                reservation.dispatch_started_at,
                (SELECT (event.metadata ->> 'dispatch_started_at')::timestamptz
                 FROM public.group_quota_events AS event
                 WHERE event.id = '01900000-0000-7000-8000-00000000f025'),
                (SELECT count(*)::integer
                 FROM public.group_quota_events AS event
                 WHERE event.id = '01900000-0000-7000-8000-00000000f025'),
                (SELECT count(*)::integer
                 FROM public.outbox_messages AS message
                 WHERE message.id = '01900000-0000-7000-8000-00000000f026'
                   AND message.payload ->> 'event_id'
                       = '01900000-0000-7000-8000-00000000f025')
            FROM public.group_token_reservations AS reservation
            WHERE reservation.id = '01900000-0000-7000-8000-00000000f023';
            """;
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M3ExitDispatchClockEvidence evidence = new(
            reader.GetFieldValue<DateTime>(0),
            reader.GetFieldValue<DateTime>(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTime>(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTime>(3),
            reader.GetInt32(4),
            reader.GetInt32(5));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return evidence;
    }

    private static async ValueTask AssertM3ExitExpiredTemporalFixtureAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                reservation.updated_at > pg_catalog.clock_timestamp()
                AND reservation.lease_expires_at <= pg_catalog.clock_timestamp()
                AND reservation.lease_expires_at - reservation.created_at
                    = interval '5 minutes'
                AND reservation.max_expires_at - reservation.created_at
                    = interval '10 minutes'
            FROM public.group_token_reservations AS reservation
            WHERE reservation.id = '01900000-0000-7000-8000-00000000f023';
            """;
        Assert.True(Assert.IsType<bool>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false)));
    }

    private static async ValueTask<string> ReadM3ExitDispatchFunctionSecurityAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT pg_catalog.concat_ws(
                '|', owner.rolname, procedure.prosecdef::text,
                procedure.provolatile::text, procedure.proretset::text,
                pg_catalog.pg_get_function_identity_arguments(procedure.oid),
                pg_catalog.pg_get_function_result(procedure.oid),
                procedure.proconfig::text, procedure.proacl::text)
            FROM pg_catalog.pg_proc AS procedure
            JOIN pg_catalog.pg_roles AS owner ON owner.oid = procedure.proowner
            WHERE procedure.oid =
                'public.poolai_quota_mark_dispatched(uuid,uuid,text,text,text,numeric,numeric,uuid,uuid,text)'::regprocedure;
            """;
        return Assert.IsType<string>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<long> ReadM3ExitDispatchFunctionOidAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT procedure.oid::bigint
            FROM pg_catalog.pg_proc AS procedure
            WHERE procedure.oid =
                'public.poolai_quota_mark_dispatched(uuid,uuid,text,text,text,numeric,numeric,uuid,uuid,text)'::regprocedure;
            """;
        return Assert.IsType<long>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<long> ReadM3ExitTopMigrationAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT max(version) FROM public.poolai_schema_migrations;";
        return Assert.IsType<long>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask ResetM3ExitRoleAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "RESET ROLE;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record M3ExitDispatchClockEvidence(
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime? DispatchStartedAt,
        DateTime? EventDispatchStartedAt,
        int DispatchEventCount,
        int DispatchOutboxCount);
}
#pragma warning restore MA0051
