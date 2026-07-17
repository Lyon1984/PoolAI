using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using PoolAI.Database.Migrations;
using Testcontainers.PostgreSql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PostgreSql18MigrationAndRuntimeInvariantsHold()
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
        PostgresMigrator migrator = new(catalog);
        await ApplyConcurrentlyAndRepeatAsync(migrator, connectionString, cancellationToken)
            .ConfigureAwait(true);
        await AssertMigrationCountAsync(connectionString, cancellationToken).ConfigureAwait(true);
        await AssertNumeric78BoundaryAsync(connectionString, cancellationToken).ConfigureAwait(true);
        await AssertRefreshSessionForeignKeysAreIndexedAsync(connectionString, cancellationToken)
            .ConfigureAwait(true);
        await AssertIdentityM1E2SchemaAsync(connectionString, cancellationToken)
            .ConfigureAwait(true);
        await AssertRuntimeRolePermissionsAsync(connectionString, cancellationToken).ConfigureAwait(true);
        await AssertIdentityM1E2RuntimePermissionsAsync(connectionString, cancellationToken)
            .ConfigureAwait(true);
        await AssertIdentityM1E3RuntimePermissionsAsync(connectionString, cancellationToken)
            .ConfigureAwait(true);
        await AssertIdentityEntryPointSecurityAsync(connectionString, cancellationToken)
            .ConfigureAwait(true);
        await AssertOutboxFencingAsync(connectionString, cancellationToken).ConfigureAwait(true);
        await AssertAppliedMetadataDriftRejectedAsync(
            migrator,
            catalog.Assets[0],
            connectionString,
            cancellationToken).ConfigureAwait(true);
        await AssertFutureHistoryRejectedAsync(
            migrator,
            catalog.Assets[^1].Version + 1,
            connectionString,
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ComposeEquivalentMigratorCanApplyRuntimePermissionMigration()
    {
        // Governing contract: docs/database/README.md requires the migrations to run
        // through a LOGIN migrator with SET-only membership in the NOLOGIN runtime owner.
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
        await AssertComposeMigratorMembershipAsync(
            administratorConnectionString,
            cancellationToken).ConfigureAwait(true);

        MigrationCatalog catalog = await MigrationCatalog
            .LoadAsync(cancellationToken)
            .ConfigureAwait(true);
        PostgresMigrator migrator = new(catalog);
        await migrator.ApplyAsync(
            migratorConnectionString,
            "PoolAI.IntegrationTests.compose-migrator",
            cancellationToken).ConfigureAwait(true);

        await AssertMigrationCountAsync(administratorConnectionString, cancellationToken)
            .ConfigureAwait(true);
        await AssertQuotaEntryPointSecurityAsync(
            administratorConnectionString,
            cancellationToken).ConfigureAwait(true);
        await AssertIdentityEntryPointSecurityAsync(
            administratorConnectionString,
            cancellationToken).ConfigureAwait(true);
        await AssertIdentityM1E3RuntimePermissionsAsync(
            administratorConnectionString,
            cancellationToken).ConfigureAwait(true);
        await AssertRuntimeSchemaCreateRevokedAsync(
            administratorConnectionString,
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask ApplyConcurrentlyAndRepeatAsync(
        PostgresMigrator migrator,
        string connectionString,
        CancellationToken cancellationToken)
    {
        Task first = migrator.ApplyAsync(
            connectionString,
            "PoolAI.IntegrationTests.concurrent-1",
            cancellationToken).AsTask();
        Task second = migrator.ApplyAsync(
            connectionString,
            "PoolAI.IntegrationTests.concurrent-2",
            cancellationToken).AsTask();
        await Task.WhenAll(first, second).ConfigureAwait(false);
        await migrator.ApplyAsync(
            connectionString,
            "PoolAI.IntegrationTests.repeat",
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertMigrationCountAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(
            "SELECT count(*) FROM public.poolai_schema_migrations;");
        object? scalar = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(6L, Assert.IsType<long>(scalar));
    }

    private static async ValueTask AssertNumeric78BoundaryAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand maximum = dataSource.CreateCommand(
            "SELECT repeat('9', 78)::numeric(78, 0)::text;");
        object? scalar = await maximum
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(new string('9', 78), Assert.IsType<string>(scalar));

        using NpgsqlCommand overflow = dataSource.CreateCommand(
            "SELECT repeat('9', 79)::numeric(78, 0);");
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => overflow.ExecuteScalarAsync(cancellationToken)).ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.NumericValueOutOfRange, exception.SqlState);
    }

    private static async ValueTask AssertRuntimeRolePermissionsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertScalarSucceedsAsync(
            connectionString,
            "SET ROLE poolai_api; SELECT count(*) FROM public.poolai_schema_migrations;",
            cancellationToken).ConfigureAwait(false);
        await AssertScalarSucceedsAsync(
            connectionString,
            "SET ROLE poolai_worker; SELECT count(*) FROM public.outbox_messages;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; SELECT count(*) FROM public.outbox_messages;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; CREATE TABLE public.poolai_api_escape(id integer);",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_worker; UPDATE public.groups SET status = status WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            UPDATE public.user_roles
            SET role_id = role_id,
                assigned_by = assigned_by,
                assigned_at = assigned_at
            WHERE false;
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; UPDATE public.user_roles SET user_id = user_id WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; DELETE FROM public.user_roles WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertScalarSucceedsAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            SELECT disposition
            FROM public.poolai_identity_update_user(
                '01900000-0000-7000-8000-000000000099'::uuid,
                1,
                NULL,
                NULL,
                NULL,
                '01900000-0000-7000-8000-000000000099'::uuid
            );
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertRefreshSessionForeignKeysAreIndexedAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        // Governing contract: docs/database/README.md sections 1 and 10 require
        // bounded runtime session cleanup under the poolai_api permission model.
        string[] expectedConstraints =
        [
            "fk_refresh_sessions_parent",
            "fk_refresh_sessions_replacement",
        ];
        const string Sql = """
            SELECT constraint_definition.conname,
                   EXISTS (
                       SELECT 1
                       FROM pg_catalog.pg_index AS index_definition
                       WHERE index_definition.indrelid = constraint_definition.conrelid
                         AND index_definition.indisvalid
                         AND index_definition.indisready
                         AND index_definition.indnkeyatts >= 1
                         AND index_definition.indexprs IS NULL
                         AND (index_definition.indkey::smallint[])[0]
                             = constraint_definition.conkey[1]
                         AND (
                             index_definition.indpred IS NULL
                             OR pg_catalog.pg_get_expr(
                                 index_definition.indpred,
                                 index_definition.indrelid
                             ) = pg_catalog.format(
                                 '(%I IS NOT NULL)',
                                 foreign_key_column.attname
                             )
                         )
                   ) AS has_prefix_index
            FROM pg_catalog.pg_constraint AS constraint_definition
            JOIN pg_catalog.pg_attribute AS foreign_key_column
              ON foreign_key_column.attrelid = constraint_definition.conrelid
             AND foreign_key_column.attnum = constraint_definition.conkey[1]
            WHERE constraint_definition.connamespace = 'public'::regnamespace
              AND constraint_definition.conrelid = 'public.refresh_sessions'::regclass
              AND constraint_definition.contype = 'f'
              AND constraint_definition.conname = ANY ($1::text[])
            ORDER BY constraint_definition.conname;
            """;

        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue(expectedConstraints);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        int observedConstraints = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            observedConstraints++;
            Assert.True(
                reader.GetBoolean(1),
                $"Foreign key {reader.GetString(0)} is missing a usable prefix index.");
        }

        Assert.Equal(expectedConstraints.Length, observedConstraints);
    }

    private static async ValueTask AssertScalarSucceedsAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(sql);
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertPermissionDeniedAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(sql);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(cancellationToken)).ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private static async ValueTask AssertOutboxFencingAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        Guid messageId = Guid.NewGuid();
        Guid ownerOne = Guid.NewGuid();
        Guid ownerTwo = Guid.NewGuid();
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        await InsertOutboxMessageAsync(dataSource, messageId, cancellationToken)
            .ConfigureAwait(false);
        await ClaimOutboxAsync(dataSource, messageId, ownerOne, 1, 1, cancellationToken)
            .ConfigureAwait(false);

        PostgresException staleGeneration = await Assert.ThrowsAsync<PostgresException>(() =>
            ClaimOutboxAsync(dataSource, messageId, ownerTwo, 1, 2, cancellationToken).AsTask())
            .ConfigureAwait(false);
        Assert.Contains(
            "delivery_claim_generation_must_increment",
            staleGeneration.MessageText,
            StringComparison.Ordinal);

        await ClaimOutboxAsync(dataSource, messageId, ownerTwo, 2, 2, cancellationToken)
            .ConfigureAwait(false);
        int staleWriteCount = await PublishOutboxAsync(
            dataSource,
            messageId,
            ownerOne,
            1,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(0, staleWriteCount);
        int currentWriteCount = await PublishOutboxAsync(
            dataSource,
            messageId,
            ownerTwo,
            2,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(1, currentWriteCount);
    }

    private static async ValueTask InsertOutboxMessageAsync(
        NpgsqlDataSource dataSource,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            INSERT INTO public.outbox_messages (
                id, deduplication_key, topic, aggregate_type, aggregate_id,
                event_type, correlation_id, payload
            ) VALUES ($1, $2, 'integration', 'test', $3, 'test.created', $4, '{}'::jsonb);
            """;
        using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue(messageId);
        command.Parameters.AddWithValue($"integration:{messageId:N}");
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(Guid.NewGuid());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ClaimOutboxAsync(
        NpgsqlDataSource dataSource,
        Guid messageId,
        Guid owner,
        long generation,
        int attempts,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            UPDATE public.outbox_messages
            SET status = 'processing',
                locked_by = $1,
                lock_generation = $2,
                publish_attempts = $3,
                locked_until = clock_timestamp() + interval '30 seconds'
            WHERE id = $4;
            """;
        using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue(owner);
        command.Parameters.AddWithValue(generation);
        command.Parameters.AddWithValue(attempts);
        command.Parameters.AddWithValue(messageId);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        Assert.Equal(1, affected);
    }

    private static async ValueTask<int> PublishOutboxAsync(
        NpgsqlDataSource dataSource,
        Guid messageId,
        Guid owner,
        long generation,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            UPDATE public.outbox_messages
            SET status = 'published',
                next_attempt_at = NULL,
                locked_by = NULL,
                locked_until = NULL,
                published_at = clock_timestamp()
            WHERE id = $1 AND locked_by = $2 AND lock_generation = $3;
            """;
        using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue(messageId);
        command.Parameters.AddWithValue(owner);
        command.Parameters.AddWithValue(generation);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertFutureHistoryRejectedAsync(
        PostgresMigrator migrator,
        long futureVersion,
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand future = dataSource.CreateCommand("""
            INSERT INTO public.poolai_schema_migrations (
                version, name, checksum_sha256, applied_by
            ) VALUES ($1, $2, repeat('a', 64), 'future-release');
            """);
        future.Parameters.AddWithValue(futureVersion);
        future.Parameters.AddWithValue($"{futureVersion:D4}_future.sql");
        await future.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => migrator.ApplyAsync(
                connectionString,
                "PoolAI.IntegrationTests.future-check",
                cancellationToken).AsTask()).ConfigureAwait(false);
        Assert.Contains("not a supported prefix", exception.Message, StringComparison.Ordinal);
    }

    private static async ValueTask AssertAppliedMetadataDriftRejectedAsync(
        PostgresMigrator migrator,
        MigrationAsset expected,
        string connectionString,
        CancellationToken cancellationToken)
    {
        await UpdateMigrationMetadataAsync(
            connectionString,
            expected.Version,
            "renamed-migration.sql",
            expected.ChecksumSha256,
            cancellationToken).ConfigureAwait(false);
        await AssertMigrationDriftRejectedAsync(migrator, connectionString, cancellationToken)
            .ConfigureAwait(false);

        await UpdateMigrationMetadataAsync(
            connectionString,
            expected.Version,
            expected.Name,
            new string('0', 64),
            cancellationToken).ConfigureAwait(false);
        await AssertMigrationDriftRejectedAsync(migrator, connectionString, cancellationToken)
            .ConfigureAwait(false);

        await UpdateMigrationMetadataAsync(
            connectionString,
            expected.Version,
            expected.Name,
            expected.ChecksumSha256,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertMigrationDriftRejectedAsync(
        PostgresMigrator migrator,
        string connectionString,
        CancellationToken cancellationToken)
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => migrator.ApplyAsync(
                connectionString,
                "PoolAI.IntegrationTests.drift-check",
                cancellationToken).AsTask()).ConfigureAwait(false);
        Assert.Contains("checksum or name drift", exception.Message, StringComparison.Ordinal);
    }

    private static async ValueTask UpdateMigrationMetadataAsync(
        string connectionString,
        long version,
        string name,
        string checksum,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand("""
            UPDATE public.poolai_schema_migrations
            SET name = $1, checksum_sha256 = $2
            WHERE version = $3;
            """);
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(checksum);
        command.Parameters.AddWithValue(version);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        Assert.Equal(1, affected);
    }

    internal static async ValueTask ProvisionRuntimeRolesAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            CREATE ROLE poolai_runtime_owner NOLOGIN
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
            CREATE ROLE poolai_api LOGIN
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
            CREATE ROLE poolai_worker LOGIN
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
            """;
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<string> ProvisionComposeMigratorAsync(
        string administratorConnectionString,
        string migratorPassword,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(administratorConnectionString);
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        using (NpgsqlCommand setPassword = new(
            "SELECT pg_catalog.set_config('poolai.test_migrator_password', $1, false);",
            connection))
        {
            setPassword.Parameters.AddWithValue(migratorPassword);
            await setPassword.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using NpgsqlCommand createMigrator = new("""
                DO $provision$
                BEGIN
                    EXECUTE pg_catalog.format(
                        'CREATE ROLE poolai_migrator LOGIN PASSWORD %L '
                        'NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
                        pg_catalog.current_setting('poolai.test_migrator_password'));
                END;
                $provision$;
                """, connection);
            await createMigrator.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            using NpgsqlCommand clearPassword = new(
                "SELECT pg_catalog.set_config('poolai.test_migrator_password', '', false);",
                connection);
            await clearPassword.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        using (NpgsqlCommand configureMembership = new("""
            GRANT poolai_runtime_owner TO poolai_migrator
                WITH INHERIT FALSE, SET TRUE;
            ALTER DATABASE poolai OWNER TO poolai_migrator;
            REVOKE CREATE, TEMPORARY ON DATABASE poolai FROM PUBLIC;
            GRANT CONNECT ON DATABASE poolai
                TO poolai_migrator, poolai_api, poolai_worker;
            """, connection))
        {
            await configureMembership.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        NpgsqlConnectionStringBuilder migratorConnection = new(administratorConnectionString)
        {
            Username = "poolai_migrator",
            Password = migratorPassword,
            ApplicationName = "PoolAI.IntegrationTests.compose-migrator",
        };
        return migratorConnection.ConnectionString;
    }

    private static async ValueTask AssertComposeMigratorMembershipAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT member.rolcanlogin,
                   membership.admin_option,
                   membership.inherit_option,
                   membership.set_option
            FROM pg_catalog.pg_auth_members AS membership
            JOIN pg_catalog.pg_roles AS granted_role
              ON granted_role.oid = membership.roleid
            JOIN pg_catalog.pg_roles AS member
              ON member.oid = membership.member
            WHERE granted_role.rolname = 'poolai_runtime_owner'
              AND member.rolname = 'poolai_migrator';
            """;
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask AssertQuotaEntryPointSecurityAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        string[] expectedNames =
        [
            "poolai_quota_adjust_total",
            "poolai_quota_adjust_usage",
            "poolai_quota_expire",
            "poolai_quota_initialize",
            "poolai_quota_mark_dispatched",
            "poolai_quota_release",
            "poolai_quota_renew",
            "poolai_quota_reserve",
            "poolai_quota_reset",
            "poolai_quota_settle",
        ];
        const string Sql = """
            SELECT function.proname, owner.rolname, function.prosecdef
            FROM pg_catalog.pg_proc AS function
            JOIN pg_catalog.pg_namespace AS schema
              ON schema.oid = function.pronamespace
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = function.proowner
            WHERE schema.nspname = 'public'
              AND function.proname = ANY ($1::text[])
            ORDER BY function.proname;
            """;

        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue(expectedNames);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, (string Owner, bool IsSecurityDefiner)> actual =
            new(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actual.Add(reader.GetString(0), (reader.GetString(1), reader.GetBoolean(2)));
        }

        Assert.Equal(expectedNames.Length, actual.Count);
        foreach (string expectedName in expectedNames)
        {
            Assert.True(
                actual.TryGetValue(expectedName, out (string Owner, bool IsSecurityDefiner) metadata),
                $"Quota entry point {expectedName} was not created.");
            Assert.Equal("poolai_runtime_owner", metadata.Owner);
            Assert.True(metadata.IsSecurityDefiner);
        }
    }

    private static async ValueTask AssertIdentityEntryPointSecurityAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT owner.rolname,
                   function.prosecdef,
                   function.proconfig,
                   pg_catalog.has_function_privilege(
                       'poolai_api',
                       function.oid,
                       'EXECUTE')
            FROM pg_catalog.pg_proc AS function
            JOIN pg_catalog.pg_namespace AS schema
              ON schema.oid = function.pronamespace
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = function.proowner
            WHERE schema.nspname = 'public'
              AND function.proname = 'poolai_identity_update_user';
            """;
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal("poolai_runtime_owner", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        string[] settings = reader.GetFieldValue<string[]>(2);
        Assert.Contains(
            "search_path=pg_catalog, public, pg_temp",
            settings,
            StringComparer.Ordinal);
        Assert.True(reader.GetBoolean(3));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask AssertRuntimeSchemaCreateRevokedAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT role_name,
                   pg_catalog.has_schema_privilege(role_name, 'public', 'CREATE')
            FROM pg_catalog.unnest(
                ARRAY['poolai_runtime_owner', 'poolai_api', 'poolai_worker']
            ) AS role_name
            ORDER BY role_name;
            """;
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        int roleCount = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            roleCount++;
            Assert.False(
                reader.GetBoolean(1),
                $"Runtime role {reader.GetString(0)} retained CREATE on public schema.");
        }

        Assert.Equal(3, roleCount);
    }

    internal static string ReadPostgresImage()
    {
        string root = MigrationCatalogTests.FindRepositoryRoot();
        using JsonDocument versions = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eng", "versions.json")));
        string image = versions.RootElement
            .GetProperty("containers")
            .GetProperty("postgresql")
            .GetString()
            ?? throw new InvalidOperationException("The PostgreSQL image lock is missing.");
        string digest = versions.RootElement
            .GetProperty("containerDigests")
            .GetProperty("postgresql")
            .GetString()
            ?? throw new InvalidOperationException("The PostgreSQL digest lock is missing.");
        return $"{image}@{digest}";
    }
}
