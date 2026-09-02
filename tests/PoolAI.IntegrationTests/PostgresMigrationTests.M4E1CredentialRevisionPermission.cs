#pragma warning disable MA0051 // The exact schema-19 failure and schema-20 catalog delta stay reviewable together.
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Npgsql;
using PoolAI.Database.Migrations;
using Testcontainers.PostgreSql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private const int M4E1CredentialPermissionHardTimeoutMilliseconds = 5 * 60 * 1000;

    private static readonly Guid M4E1CredentialPermissionAccountId = Guid.Parse(
        "01960000-0000-7000-8000-000000002001");

    [Fact(Timeout = M4E1CredentialPermissionHardTimeoutMilliseconds)]
    [Trait("Category", "PostgreSQL")]
    public async Task M4E1CredentialRevisionPermissionIsForwardOnlyAndExact()
    {
        // Governing contracts: ADR 0016 and docs/database/README.md section 11.
        // Schema 19 must reproduce both real API-role 42501 failures. Migration
        // 0020 then adds one column SELECT and no other catalog capability.
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
        Assert.Equal(20, catalog.Assets.Count);
        await ApplyM4E1CredentialPermissionPrefixAsync(
            catalog,
            connectionString,
            cancellationToken).ConfigureAwait(true);
        await SeedM4E1CredentialPermissionAccountAsync(
            connectionString,
            cancellationToken).ConfigureAwait(true);

        await AssertM4E1CredentialPermissionQueriesDeniedAsync(
            connectionString,
            cancellationToken).ConfigureAwait(true);
        await AssertM4E1CredentialPermissionAuditRejectsBroadOrPublicReadAsync(
            catalog,
            connectionString,
            cancellationToken).ConfigureAwait(true);

        M4E1PermissionCatalog before = await ReadM4E1PermissionCatalogAsync(
            connectionString,
            cancellationToken).ConfigureAwait(true);
        await new PostgresMigrator(catalog).ApplyAsync(
            connectionString,
            "PoolAI.IntegrationTests.m4-e1-credential-permission",
            cancellationToken).ConfigureAwait(true);
        M4E1PermissionCatalog after = await ReadM4E1PermissionCatalogAsync(
            connectionString,
            cancellationToken).ConfigureAwait(true);

        using (NpgsqlConnection evidenceConnection = new(connectionString))
        {
            await evidenceConnection.OpenAsync(cancellationToken).ConfigureAwait(true);
            Assert.Equal(
                20,
                await ReadM4E1TopMigrationAsync(
                    evidenceConnection,
                    cancellationToken).ConfigureAwait(true));
        }
        AssertM4E1CredentialPermissionCatalogDelta(before, after);
        await AssertM4E1CredentialPermissionQueriesSucceedAsync(
            connectionString,
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask ApplyM4E1CredentialPermissionPrefixAsync(
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
        foreach (MigrationAsset asset in catalog.Assets.Take(19))
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
                ) VALUES ($1, $2, $3, 'PoolAI.IntegrationTests.m4-e1-credential-prefix');
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

    private static async ValueTask SeedM4E1CredentialPermissionAccountAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await SetM2E1RoleAsync(connection, "poolai_api", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT disposition
                FROM public.poolai_supply_create_account(
                    $1, 'openai', 'M4-E1 permission account',
                    'https://example.test/v1', $2::jsonb,
                    'sk-test', NULL, 2, 0, 100
                );
                """;
            command.Parameters.AddWithValue(M4E1CredentialPermissionAccountId);
            command.Parameters.AddWithValue(M2E1Envelope("m4-e1-permission-k1", "AQ", "AQ"));
            Assert.Equal(
                "created",
                Assert.IsType<string>(await command
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }
        finally
        {
            await ResetM2E1RoleAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask AssertM4E1CredentialPermissionQueriesDeniedAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        foreach (string query in M4E1CredentialPermissionQueries())
        {
            using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
            using NpgsqlConnection connection = await dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await SetM2E1RoleAsync(connection, "poolai_api", cancellationToken)
                .ConfigureAwait(false);
            try
            {
                using NpgsqlCommand command = connection.CreateCommand();
                command.CommandText = query;
                command.Parameters.AddWithValue(M4E1CredentialPermissionAccountId);
                PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                    () => command.ExecuteScalarAsync(cancellationToken)).ConfigureAwait(false);
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
            }
            finally
            {
                await ResetM2E1RoleAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask AssertM4E1CredentialPermissionAuditRejectsBroadOrPublicReadAsync(
        MigrationCatalog catalog,
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        (string GrantSql, string RevokeSql)[] forbiddenDrifts =
        [
            (
                "GRANT SELECT ON public.accounts TO poolai_api;",
                """
                REVOKE SELECT ON public.accounts FROM poolai_api;
                GRANT SELECT (
                    id, provider, name, auth_type, upstream_base_url,
                    credential_envelope, credential_prefix, credential_hint, settings,
                    status, priority, weight, max_concurrency,
                    upstream_rate_limited_until, last_health_at, last_health_status,
                    version, created_at, updated_at, deleted_at
                ) ON public.accounts TO poolai_api;
                """),
            (
                "GRANT SELECT ON public.accounts TO PUBLIC;",
                "REVOKE SELECT ON public.accounts FROM PUBLIC;"),
            (
                "GRANT SELECT (credential_revision) ON public.accounts TO PUBLIC;",
                "REVOKE SELECT (credential_revision) ON public.accounts FROM PUBLIC;"),
        ];

        foreach ((string grantSql, string revokeSql) in forbiddenDrifts)
        {
            using NpgsqlCommand grant = dataSource.CreateCommand(grantSql);
            _ = await grant.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
                new PostgresMigrator(catalog).ApplyAsync(
                    connectionString,
                    "PoolAI.IntegrationTests.m4-e1-credential-acl-drift",
                    cancellationToken).AsTask()).ConfigureAwait(false);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
            Assert.Equal(
                "poolai_m4_e1_credential_revision_acl_shape_forbidden",
                exception.MessageText);

            using NpgsqlCommand revoke = dataSource.CreateCommand(revokeSql);
            _ = await revoke.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            using NpgsqlCommand history = dataSource.CreateCommand(
                "SELECT max(version) FROM public.poolai_schema_migrations;");
            Assert.Equal(
                19,
                Assert.IsType<long>(await history
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }
    }

    private static async ValueTask AssertM4E1CredentialPermissionQueriesSucceedAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        foreach (string query in M4E1CredentialPermissionQueries())
        {
            using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
            using NpgsqlConnection connection = await dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await SetM2E1RoleAsync(connection, "poolai_api", cancellationToken)
                .ConfigureAwait(false);
            try
            {
                using NpgsqlCommand command = connection.CreateCommand();
                command.CommandText = query;
                command.Parameters.AddWithValue(M4E1CredentialPermissionAccountId);
                Assert.Equal(
                    1,
                    Assert.IsType<long>(await command
                        .ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false)));
            }
            finally
            {
                await ResetM2E1RoleAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string[] M4E1CredentialPermissionQueries() =>
    [
        """
        SELECT account.credential_revision
        FROM public.accounts AS account
        WHERE account.id = $1
          AND account.provider = 'openai'
          AND account.version = 1;
        """,
        """
        SELECT account.credential_revision
        FROM public.accounts AS account
        WHERE account.id = $1
          AND account.provider = 'openai'
          AND account.upstream_base_url = 'https://example.test/v1'
          AND account.credential_envelope IS NOT NULL
          AND account.status = 'disabled'
          AND account.version = 1;
        """,
    ];

    private static async ValueTask<M4E1PermissionCatalog> ReadM4E1PermissionCatalogAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        return new M4E1PermissionCatalog(
            await ReadM4E1PermissionRowsAsync(
                dataSource,
                """
                SELECT role.rolname || ':' || role.rolsuper::text || ':'
                    || role.rolinherit::text || ':' || role.rolcreaterole::text || ':'
                    || role.rolcreatedb::text || ':' || role.rolcanlogin::text || ':'
                    || role.rolreplication::text || ':' || role.rolbypassrls::text
                FROM pg_catalog.pg_roles AS role
                ORDER BY role.rolname;
                """,
                cancellationToken).ConfigureAwait(false),
            await ReadM4E1PermissionRowsAsync(
                dataSource,
                """
                SELECT granted.rolname || '->' || member.rolname || ':'
                    || membership.admin_option::text || ':'
                    || membership.inherit_option::text || ':'
                    || membership.set_option::text
                FROM pg_catalog.pg_auth_members AS membership
                JOIN pg_catalog.pg_roles AS granted
                  ON granted.oid = membership.roleid
                JOIN pg_catalog.pg_roles AS member
                  ON member.oid = membership.member
                ORDER BY granted.rolname, member.rolname;
                """,
                cancellationToken).ConfigureAwait(false),
            await ReadM4E1PermissionRowsAsync(
                dataSource,
                """
                SELECT namespace.nspname || '.' || relation.relname || ':'
                    || COALESCE(grantee.rolname, 'PUBLIC') || ':'
                    || privilege.privilege_type || ':' || privilege.is_grantable::text
                FROM pg_catalog.pg_class AS relation
                JOIN pg_catalog.pg_namespace AS namespace
                  ON namespace.oid = relation.relnamespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
                    relation.relacl,
                    pg_catalog.acldefault('r', relation.relowner))) AS privilege
                LEFT JOIN pg_catalog.pg_roles AS grantee
                  ON grantee.oid = privilege.grantee
                WHERE namespace.nspname = 'public'
                  AND relation.relkind IN ('r', 'p', 'v', 'm', 'S')
                ORDER BY relation.relname, grantee.rolname,
                    privilege.privilege_type, privilege.is_grantable;
                """,
                cancellationToken).ConfigureAwait(false),
            await ReadM4E1PermissionRowsAsync(
                dataSource,
                """
                SELECT relation.relname || '.' || attribute.attname || ':'
                    || COALESCE(grantee.rolname, 'PUBLIC') || ':'
                    || privilege.privilege_type || ':' || privilege.is_grantable::text
                FROM pg_catalog.pg_attribute AS attribute
                JOIN pg_catalog.pg_class AS relation
                  ON relation.oid = attribute.attrelid
                JOIN pg_catalog.pg_namespace AS namespace
                  ON namespace.oid = relation.relnamespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) AS privilege
                LEFT JOIN pg_catalog.pg_roles AS grantee
                  ON grantee.oid = privilege.grantee
                WHERE namespace.nspname = 'public'
                  AND attribute.attnum > 0
                  AND NOT attribute.attisdropped
                ORDER BY relation.relname, attribute.attnum, grantee.rolname,
                    privilege.privilege_type, privilege.is_grantable;
                """,
                cancellationToken).ConfigureAwait(false),
            await ReadM4E1PermissionRowsAsync(
                dataSource,
                """
                SELECT procedure.oid::regprocedure::text || ':'
                    || COALESCE(grantee.rolname, 'PUBLIC') || ':'
                    || privilege.privilege_type || ':' || privilege.is_grantable::text
                FROM pg_catalog.pg_proc AS procedure
                JOIN pg_catalog.pg_namespace AS namespace
                  ON namespace.oid = procedure.pronamespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
                    procedure.proacl,
                    pg_catalog.acldefault('f', procedure.proowner))) AS privilege
                LEFT JOIN pg_catalog.pg_roles AS grantee
                  ON grantee.oid = privilege.grantee
                WHERE namespace.nspname = 'public'
                ORDER BY procedure.oid::regprocedure::text, grantee.rolname,
                    privilege.privilege_type, privilege.is_grantable;
                """,
                cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<string[]> ReadM4E1PermissionRowsAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        List<string> rows = [];
        using NpgsqlCommand command = dataSource.CreateCommand(sql);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(reader.GetString(0));
        }

        return rows.ToArray();
    }

    private static void AssertM4E1CredentialPermissionCatalogDelta(
        M4E1PermissionCatalog before,
        M4E1PermissionCatalog after)
    {
        Assert.Equal(before.Roles, after.Roles);
        Assert.Equal(before.Memberships, after.Memberships);
        Assert.Equal(before.TablePrivileges, after.TablePrivileges);
        Assert.Equal(before.FunctionPrivileges, after.FunctionPrivileges);
        Assert.Empty(before.ColumnPrivileges.Except(after.ColumnPrivileges, StringComparer.Ordinal));
        Assert.Equal(
            ["accounts.credential_revision:poolai_api:SELECT:false"],
            after.ColumnPrivileges.Except(before.ColumnPrivileges, StringComparer.Ordinal));
        Assert.DoesNotContain(
            after.TablePrivileges,
            privilege => privilege.StartsWith(
                "public.accounts:poolai_api:",
                StringComparison.Ordinal));
    }

    private sealed record M4E1PermissionCatalog(
        string[] Roles,
        string[] Memberships,
        string[] TablePrivileges,
        string[] ColumnPrivileges,
        string[] FunctionPrivileges);
}
#pragma warning restore MA0051
