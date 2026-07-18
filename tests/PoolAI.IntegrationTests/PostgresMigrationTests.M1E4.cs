#pragma warning disable MA0051 // M1-E4 database linearization scenarios stay explicit.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private static async ValueTask AssertM1E4RuntimePermissionsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; UPDATE public.groups SET name = name WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; INSERT INTO public.groups (id, name) "
                + "VALUES ('01900000-0000-7000-8000-000000000790', 'M1-E4 bypass');",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; UPDATE public.subscription_templates "
                + "SET name = name WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; INSERT INTO public.subscription_templates "
                + "(id, group_id, name, default_duration_days) VALUES "
                + "('01900000-0000-7000-8000-000000000791', "
                + "'01900000-0000-7000-8000-000000000792', 'M1-E4 bypass', 30);",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; UPDATE public.subscriptions SET status = status WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; INSERT INTO public.subscriptions "
                + "(id, user_id, group_id, template_id, template_name_snapshot, "
                + "starts_at, expires_at, assigned_by, change_reason) VALUES "
                + "('01900000-0000-7000-8000-000000000793', "
                + "'01900000-0000-7000-8000-000000000794', "
                + "'01900000-0000-7000-8000-000000000795', "
                + "'01900000-0000-7000-8000-000000000796', 'M1-E4 bypass', "
                + "clock_timestamp(), clock_timestamp() + interval '1 day', "
                + "'01900000-0000-7000-8000-000000000794', 'bypass');",
            cancellationToken).ConfigureAwait(false);
        foreach (string table in new[]
                 {
                     "groups",
                     "subscription_templates",
                     "subscriptions",
                 })
        {
            await AssertPermissionDeniedAsync(
                connectionString,
                $"SET ROLE poolai_api; DELETE FROM public.{table} WHERE false;",
                cancellationToken).ConfigureAwait(false);
        }

        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            MERGE INTO public.groups AS target
            USING (SELECT NULL::uuid AS id WHERE false) AS source
              ON target.id = source.id
            WHEN MATCHED THEN UPDATE SET name = target.name;
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            MERGE INTO public.subscription_templates AS target
            USING (SELECT NULL::uuid AS id WHERE false) AS source
              ON target.id = source.id
            WHEN MATCHED THEN UPDATE SET name = target.name;
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            MERGE INTO public.subscriptions AS target
            USING (SELECT NULL::uuid AS id WHERE false) AS source
              ON target.id = source.id
            WHEN MATCHED THEN UPDATE SET status = target.status;
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; SELECT * FROM public.poolai_quota_initialize("
                + "'01900000-0000-7000-8000-000000000795'::uuid, "
                + "'01900000-0000-7000-8000-000000000796'::uuid, 1::numeric, "
                + "'01900000-0000-7000-8000-000000000797'::uuid, "
                + "'01900000-0000-7000-8000-000000000798'::uuid, "
                + "'01900000-0000-7000-8000-000000000799'::uuid, "
                + "'m1-e4-bypass', 'bypass');",
            cancellationToken).ConfigureAwait(false);

        const string SecuritySql = """
            WITH expected(signature) AS (
                VALUES
                    ('public.poolai_group_create(uuid,text,text,uuid,numeric,uuid,uuid,uuid,text,text)'),
                    ('public.poolai_group_update(uuid,bigint,boolean,text,boolean,text,text,text,text,timestamp with time zone)'),
                    ('public.poolai_subscription_template_create(uuid,uuid,text,text,integer)'),
                    ('public.poolai_subscription_template_update(uuid,bigint,boolean,text,boolean,text,boolean,integer,text,text)'),
                    ('public.poolai_subscription_template_retire(uuid,bigint,text)'),
                    ('public.poolai_subscription_assign(uuid,uuid,uuid,timestamp with time zone,timestamp with time zone,uuid,text)'),
                    ('public.poolai_subscription_update(uuid,bigint,boolean,timestamp with time zone,boolean,timestamp with time zone,text,boolean,uuid,text)')
            ), resolved AS (
                SELECT pg_catalog.to_regprocedure(expected.signature) AS function_oid
                FROM expected
            )
            SELECT count(*)
            FROM resolved
            JOIN pg_catalog.pg_proc AS function
              ON function.oid = resolved.function_oid
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = function.proowner
            WHERE resolved.function_oid IS NOT NULL
              AND function.prosecdef
              AND owner.rolname = 'poolai_runtime_owner'
              AND owner.rolcanlogin = false
              AND function.proconfig @> ARRAY[
                  'search_path=pg_catalog, public, pg_temp'
              ]::text[]
              AND pg_catalog.has_function_privilege(
                  'poolai_api', function.oid, 'EXECUTE')
              AND NOT pg_catalog.has_function_privilege(
                  'poolai_worker', function.oid, 'EXECUTE')
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.aclexplode(COALESCE(
                      function.proacl,
                      pg_catalog.acldefault('f', function.proowner))) AS privilege
                  WHERE privilege.privilege_type = 'EXECUTE'
                    AND privilege.grantee = 0
              );
            """;
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        await AssertM1E4TablePrivilegeCatalogAsync(dataSource, cancellationToken)
            .ConfigureAwait(false);
        using (NpgsqlCommand security = dataSource.CreateCommand(SecuritySql))
        {
            Assert.Equal(
                7L,
                Assert.IsType<long>(await security
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using NpgsqlCommand overloads = dataSource.CreateCommand("""
            SELECT count(*)
            FROM pg_catalog.pg_proc AS function
            JOIN pg_catalog.pg_namespace AS schema
              ON schema.oid = function.pronamespace
            WHERE schema.nspname = 'public'
              AND function.proname = ANY (ARRAY[
                  'poolai_group_create',
                  'poolai_group_update',
                  'poolai_subscription_template_create',
                  'poolai_subscription_template_update',
                  'poolai_subscription_template_retire',
                  'poolai_subscription_assign',
                  'poolai_subscription_update'
              ]);
            """);
        Assert.Equal(
            7L,
            Assert.IsType<long>(await overloads
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));

        using NpgsqlCommand retiredQuotaWrites = dataSource.CreateCommand("""
            SELECT count(*)
            FROM pg_catalog.unnest(ARRAY[
                'public.poolai_quota_initialize(uuid,uuid,numeric,uuid,uuid,uuid,text,text)'::regprocedure,
                'public.poolai_quota_reset(uuid,uuid,numeric,bigint,uuid,uuid,uuid,text,text)'::regprocedure,
                'public.poolai_quota_adjust_total(uuid,numeric,bigint,uuid,uuid,uuid,text,text)'::regprocedure
            ]) AS entry_point(function_oid)
            WHERE NOT pg_catalog.has_function_privilege(
                'poolai_api', entry_point.function_oid, 'EXECUTE');
            """);
        Assert.Equal(
            3L,
            Assert.IsType<long>(await retiredQuotaWrites
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask AssertM1E4TablePrivilegeCatalogAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        const string PrivilegeSql = """
            WITH scope(grantee, table_name) AS (
                VALUES
                    ('poolai_api', 'groups'),
                    ('poolai_api', 'subscription_templates'),
                    ('poolai_api', 'subscriptions'),
                    ('poolai_worker', 'groups'),
                    ('poolai_worker', 'subscription_templates'),
                    ('poolai_worker', 'subscriptions'),
                    ('PUBLIC', 'groups'),
                    ('PUBLIC', 'subscription_templates'),
                    ('PUBLIC', 'subscriptions')
            ), table_grants AS (
                SELECT privilege.grantee,
                       privilege.table_name,
                       string_agg(
                           DISTINCT privilege.privilege_type || ':'
                               || privilege.is_grantable,
                           ',' ORDER BY privilege.privilege_type || ':'
                               || privilege.is_grantable
                       ) AS privilege_types
                FROM information_schema.table_privileges AS privilege
                WHERE privilege.table_schema = 'public'
                  AND privilege.grantee IN ('poolai_api', 'poolai_worker', 'PUBLIC')
                  AND privilege.table_name IN (
                      'groups', 'subscription_templates', 'subscriptions'
                  )
                GROUP BY privilege.grantee, privilege.table_name
            ), column_grants AS (
                SELECT privilege.grantee,
                       privilege.table_name,
                       string_agg(
                           DISTINCT privilege.privilege_type || ':'
                               || privilege.is_grantable,
                           ',' ORDER BY privilege.privilege_type || ':'
                               || privilege.is_grantable
                       ) AS privilege_types
                FROM information_schema.column_privileges AS privilege
                WHERE privilege.table_schema = 'public'
                  AND privilege.grantee IN ('poolai_api', 'poolai_worker', 'PUBLIC')
                  AND privilege.table_name IN (
                      'groups', 'subscription_templates', 'subscriptions'
                  )
                GROUP BY privilege.grantee, privilege.table_name
            )
            SELECT scope.grantee,
                   scope.table_name,
                   coalesce(table_grants.privilege_types, ''),
                   coalesce(column_grants.privilege_types, '')
            FROM scope
            LEFT JOIN table_grants USING (grantee, table_name)
            LEFT JOIN column_grants USING (grantee, table_name);
            """;
        Dictionary<string, (string TablePrivileges, string ColumnPrivileges)> actual =
            new(StringComparer.Ordinal);
        using NpgsqlCommand command = dataSource.CreateCommand(PrivilegeSql);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actual.Add(
                $"{reader.GetString(0)}:{reader.GetString(1)}",
                (reader.GetString(2), reader.GetString(3)));
        }

        Dictionary<string, (string TablePrivileges, string ColumnPrivileges)> expected =
            new(StringComparer.Ordinal);
        foreach (string table in new[]
                 {
                     "groups",
                     "subscription_templates",
                     "subscriptions",
                 })
        {
            expected.Add($"poolai_api:{table}", ("SELECT:NO", "SELECT:NO"));
            expected.Add($"poolai_worker:{table}", (string.Empty, string.Empty));
            expected.Add($"PUBLIC:{table}", (string.Empty, string.Empty));
        }

        Assert.Equal(expected.Count, actual.Count);
        foreach ((string key, (string TablePrivileges, string ColumnPrivileges) privileges) in
                 expected)
        {
            Assert.Equal(privileges, actual[key]);
        }

        using NpgsqlCommand effective = dataSource.CreateCommand("""
            WITH scope(role_name, table_name, expected_select) AS (
                VALUES
                    ('poolai_api', 'groups', true),
                    ('poolai_api', 'subscription_templates', true),
                    ('poolai_api', 'subscriptions', true),
                    ('poolai_worker', 'groups', false),
                    ('poolai_worker', 'subscription_templates', false),
                    ('poolai_worker', 'subscriptions', false)
            ), invalid_privilege AS (
                SELECT 1
                FROM scope
                WHERE pg_catalog.has_table_privilege(
                          role_name, pg_catalog.format('public.%I', table_name), 'SELECT')
                      IS DISTINCT FROM expected_select
                   OR pg_catalog.has_any_column_privilege(
                          role_name, pg_catalog.format('public.%I', table_name), 'SELECT')
                      IS DISTINCT FROM expected_select
                   OR pg_catalog.has_table_privilege(
                          role_name,
                          pg_catalog.format('public.%I', table_name),
                          'SELECT WITH GRANT OPTION')
                   OR pg_catalog.has_any_column_privilege(
                          role_name,
                          pg_catalog.format('public.%I', table_name),
                          'SELECT WITH GRANT OPTION')
                   OR pg_catalog.has_table_privilege(
                          role_name, pg_catalog.format('public.%I', table_name), 'INSERT')
                   OR pg_catalog.has_table_privilege(
                          role_name, pg_catalog.format('public.%I', table_name), 'UPDATE')
                   OR pg_catalog.has_table_privilege(
                          role_name, pg_catalog.format('public.%I', table_name), 'DELETE')
                   OR pg_catalog.has_table_privilege(
                          role_name, pg_catalog.format('public.%I', table_name), 'TRUNCATE')
                   OR pg_catalog.has_table_privilege(
                          role_name, pg_catalog.format('public.%I', table_name), 'REFERENCES')
                   OR pg_catalog.has_table_privilege(
                          role_name, pg_catalog.format('public.%I', table_name), 'TRIGGER')
                   OR pg_catalog.has_table_privilege(
                          role_name, pg_catalog.format('public.%I', table_name), 'MAINTAIN')
            ), unexpected_membership AS (
                SELECT 1
                FROM pg_catalog.pg_auth_members AS membership
                JOIN pg_catalog.pg_roles AS member ON member.oid = membership.member
                WHERE member.rolname IN ('poolai_api', 'poolai_worker')
            )
            SELECT (SELECT count(*) FROM invalid_privilege)
                 + (SELECT count(*) FROM unexpected_membership);
            """);
        Assert.Equal(
            0L,
            Assert.IsType<long>(await effective
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask AssertM1E4LifecycleAndClockAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);

        using (NpgsqlCommand seedUsers = connection.CreateCommand())
        {
            seedUsers.CommandText = """
                INSERT INTO public.users (
                    id, email, normalized_email, display_name,
                    password_hash, security_stamp
                ) VALUES
                    (
                        '01900000-0000-7000-8000-000000000700',
                        'm1e4-actor@example.test', 'm1e4-actor@example.test',
                        'M1-E4 Actor', 'poolai-password-v1:test',
                        '01900000-0000-7000-8000-000000000780'
                    ),
                    (
                        '01900000-0000-7000-8000-000000000701',
                        'm1e4-user@example.test', 'm1e4-user@example.test',
                        'M1-E4 User', 'poolai-password-v1:test',
                        '01900000-0000-7000-8000-000000000781'
                    ),
                    (
                        '01900000-0000-7000-8000-000000000702',
                        'm1e4-boundary@example.test', 'm1e4-boundary@example.test',
                        'M1-E4 Boundary', 'poolai-password-v1:test',
                        '01900000-0000-7000-8000-000000000782'
                    ),
                    (
                        '01900000-0000-7000-8000-000000000703',
                        'm1e4-retired@example.test', 'm1e4-retired@example.test',
                        'M1-E4 Retired', 'poolai-password-v1:test',
                        '01900000-0000-7000-8000-000000000783'
                    );
                SET ROLE poolai_api;
                """;
            await seedUsers.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        M1E4Mutation createdGroup = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_create(
                '01900000-0000-7000-8000-000000000710',
                'M1-E4 Lifecycle Group', 'database control-plane coverage',
                '01900000-0000-7000-8000-000000000711', 100000,
                '01900000-0000-7000-8000-000000000700',
                '01900000-0000-7000-8000-000000000712',
                '01900000-0000-7000-8000-000000000713',
                'm1-e4-group-lifecycle-initialize', 'M1-E4 group create');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(createdGroup, "created", true, 1);

        M1E4Mutation duplicateGroup = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_create(
                '01900000-0000-7000-8000-000000000710',
                'M1-E4 Lifecycle Group', NULL,
                '01900000-0000-7000-8000-000000000716', 100000,
                '01900000-0000-7000-8000-000000000700',
                '01900000-0000-7000-8000-000000000717',
                '01900000-0000-7000-8000-000000000718',
                'm1-e4-group-lifecycle-duplicate', 'duplicate');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(duplicateGroup, "conflict", false, null);

        using (NpgsqlCommand quota = connection.CreateCommand())
        {
            quota.CommandText = """
                SELECT quota.version::text || ':' || period.total_tokens::text
                FROM public.group_token_quotas AS quota
                JOIN public.group_quota_periods AS period
                  ON period.id = quota.current_period_id
                 AND period.group_id = quota.group_id
                WHERE quota.group_id = '01900000-0000-7000-8000-000000000710';
                """;
            Assert.Equal(
                "1:100000",
                Assert.IsType<string>(await quota
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand seedSupply = connection.CreateCommand())
        {
            seedSupply.CommandText = """
                RESET ROLE;
                INSERT INTO public.channels (
                    id, provider, name, model_rules, status
                ) VALUES (
                    '01900000-0000-7000-8000-000000000714',
                    'openai', 'M1-E4 Channel', '{"gpt-test":"gpt-test"}'::jsonb,
                    'active'
                );
                INSERT INTO public.accounts (
                    id, provider, name, auth_type, upstream_base_url,
                    credential_envelope, credential_prefix,
                    status, last_health_at, last_health_status
                ) VALUES (
                    '01900000-0000-7000-8000-000000000715',
                    'openai', 'M1-E4 Account', 'api_key', 'https://example.test/v1',
                    '{}'::jsonb, 'sk-m1e4', 'active', clock_timestamp(), 'healthy'
                );
                INSERT INTO public.group_supply_configurations (group_id, channel_id)
                VALUES (
                    '01900000-0000-7000-8000-000000000710',
                    '01900000-0000-7000-8000-000000000714'
                );
                INSERT INTO public.group_accounts (group_id, account_id, is_enabled)
                VALUES (
                    '01900000-0000-7000-8000-000000000710',
                    '01900000-0000-7000-8000-000000000715', true
                );
                SET ROLE poolai_api;
                """;
            await seedSupply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        M1E4Mutation activatedGroup = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-000000000710', 1,
                false, NULL, false, NULL, 'active', 'activate',
                'v1.m1e4ready', clock_timestamp());
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(activatedGroup, "updated", true, 2);

        M1E4Mutation staleGroup = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-000000000710', 1,
                true, 'stale write', false, NULL, NULL, NULL, NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(staleGroup, "version_conflict", false, 2);
        AssertM1E4BeforeState(staleGroup, "name", "M1-E4 Lifecycle Group");

        M1E4Mutation nullGroupVersion = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-000000000710', NULL,
                false, NULL, false, NULL, NULL, NULL, NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(nullGroupVersion, "validation_failed", false, null);

        M1E4Mutation createdTemplate = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_template_create(
                '01900000-0000-7000-8000-000000000720',
                '01900000-0000-7000-8000-000000000710',
                'M1-E4 Template', 'access only', 30);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(createdTemplate, "created", true, 1);

        NpgsqlTransaction missingUserTransaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (missingUserTransaction.ConfigureAwait(false))
        {
            M1E4Mutation missingUser = await ExecuteM1E4MutationAsync(
                connection,
                missingUserTransaction,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_assign(
                    '01900000-0000-7000-8000-00000000073f',
                    '01900000-0000-7000-8000-0000000007ff',
                    '01900000-0000-7000-8000-000000000720',
                    NULL, NULL,
                    '01900000-0000-7000-8000-000000000700', 'missing target user');
                """,
                cancellationToken).ConfigureAwait(false);
            AssertM1E4Mutation(missingUser, "conflict", false, null);
            Assert.Null(missingUser.BeforeState);

            using NpgsqlCommand transactionRemainsUsable = connection.CreateCommand();
            transactionRemainsUsable.Transaction = missingUserTransaction;
            transactionRemainsUsable.CommandText = "SELECT 1;";
            Assert.Equal(
                1,
                Assert.IsType<int>(await transactionRemainsUsable
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
            await missingUserTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        using (NpgsqlCommand missingUserWasNotInserted = connection.CreateCommand())
        {
            missingUserWasNotInserted.CommandText = """
                SELECT count(*)
                FROM public.subscriptions
                WHERE id = '01900000-0000-7000-8000-00000000073f';
                """;
            Assert.Equal(
                0L,
                Assert.IsType<long>(await missingUserWasNotInserted
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        M1E4Mutation assigned = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_assign(
                '01900000-0000-7000-8000-000000000730',
                '01900000-0000-7000-8000-000000000701',
                '01900000-0000-7000-8000-000000000720',
                NULL, NULL,
                '01900000-0000-7000-8000-000000000700', 'assign');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(assigned, "created", true, 1);

        using (NpgsqlCommand snapshot = connection.CreateCommand())
        {
            snapshot.CommandText = """
                SELECT template_name_snapshot,
                       extract(epoch FROM (expires_at - starts_at))::bigint
                FROM public.subscriptions
                WHERE id = '01900000-0000-7000-8000-000000000730';
                """;
            using NpgsqlDataReader reader = await snapshot
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            Assert.Equal("M1-E4 Template", reader.GetString(0));
            Assert.Equal(2_592_000L, reader.GetInt64(1));
            Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        }

        M1E4Mutation canonicalConflict = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_assign(
                '01900000-0000-7000-8000-000000000732',
                '01900000-0000-7000-8000-000000000701',
                '01900000-0000-7000-8000-000000000720',
                NULL, NULL,
                '01900000-0000-7000-8000-000000000700', 'duplicate canonical');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(canonicalConflict, "subscription_conflict", false, null);

        M1E4Mutation renamedTemplate = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_template_update(
                '01900000-0000-7000-8000-000000000720', 1,
                true, 'M1-E4 Template Renamed', false, NULL,
                false, NULL, NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(renamedTemplate, "updated", true, 2);
        AssertM1E4BeforeState(renamedTemplate, "name", "M1-E4 Template");

        M1E4Mutation staleTemplate = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_template_update(
                '01900000-0000-7000-8000-000000000720', 1,
                false, NULL, true, 'stale', false, NULL, NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(staleTemplate, "version_conflict", false, 2);

        M1E4Mutation templateNoOp = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_template_update(
                '01900000-0000-7000-8000-000000000720', 2,
                false, NULL, false, NULL, false, NULL, NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(templateNoOp, "updated", false, 2);
        Assert.Null(templateNoOp.BeforeState);

        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                connection,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_template_create(
                    '01900000-0000-7000-8000-000000000721',
                    '01900000-0000-7000-8000-000000000710',
                    'M1-E4 Other Template', NULL, 30);
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        M1E4Mutation duplicateTemplateName = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_template_update(
                '01900000-0000-7000-8000-000000000721', 1,
                true, 'M1-E4 Template Renamed', false, NULL,
                false, NULL, NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(duplicateTemplateName, "conflict", false, 1);
        AssertM1E4BeforeState(
            duplicateTemplateName,
            "name",
            "M1-E4 Other Template");

        using (NpgsqlCommand immutableSnapshot = connection.CreateCommand())
        {
            immutableSnapshot.CommandText = """
                SELECT template_name_snapshot
                FROM public.subscriptions
                WHERE id = '01900000-0000-7000-8000-000000000730';
                """;
            Assert.Equal(
                "M1-E4 Template",
                Assert.IsType<string>(await immutableSnapshot
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        M1E4Mutation boundaryAssigned = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_assign(
                '01900000-0000-7000-8000-000000000731',
                '01900000-0000-7000-8000-000000000702',
                '01900000-0000-7000-8000-000000000720',
                clock_timestamp() - interval '1 second',
                clock_timestamp() + interval '2 seconds',
                '01900000-0000-7000-8000-000000000700', 'boundary');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(boundaryAssigned, "created", true, 1);
        Assert.Equal(
            "active:active:1",
            await ReadM1E4EffectiveStateAsync(
                connection,
                new Guid("01900000-0000-7000-8000-000000000731"),
                cancellationToken).ConfigureAwait(false));
        await WaitForM1E4SubscriptionExpiryAsync(
            connection,
            new Guid("01900000-0000-7000-8000-000000000731"),
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(
            "active:expired:1",
            await ReadM1E4EffectiveStateAsync(
                connection,
                new Guid("01900000-0000-7000-8000-000000000731"),
                cancellationToken).ConfigureAwait(false));

        M1E4Mutation suspended = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-000000000730', 1,
                false, NULL, false, NULL, 'suspended', false,
                '01900000-0000-7000-8000-000000000700', 'pause');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(suspended, "updated", true, 2);

        M1E4Mutation staleSubscription = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-000000000730', 1,
                false, NULL, false, NULL, 'active', false,
                '01900000-0000-7000-8000-000000000700', 'stale resume');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(staleSubscription, "version_conflict", false, 2);
        AssertM1E4BeforeState(staleSubscription, "status", "suspended");
        AssertM1E4BeforeState(staleSubscription, "effective_status", "suspended");

        M1E4Mutation resumed = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-000000000730', 2,
                false, NULL, false, NULL, 'active', false,
                '01900000-0000-7000-8000-000000000700', 'resume');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(resumed, "updated", true, 3);

        M1E4Mutation subscriptionNoOp = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-000000000730', 3,
                false, NULL, false, NULL, NULL, false,
                '01900000-0000-7000-8000-000000000700', 'no-op');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(subscriptionNoOp, "updated", false, 3);
        Assert.Null(subscriptionNoOp.BeforeState);

        M1E4Mutation revoked = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-000000000730', 3,
                false, NULL, false, NULL, 'revoked', false,
                '01900000-0000-7000-8000-000000000700', 'revoke');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(revoked, "updated", true, 4);

        M1E4Mutation operatorRegrant = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-000000000730', 4,
                true, clock_timestamp() + interval '1 day',
                true, clock_timestamp() + interval '2 days',
                'active', false,
                '01900000-0000-7000-8000-000000000700', 'operator regrant');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(operatorRegrant, "invalid_transition", false, 4);

        M1E4Mutation retiredTemplate = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_template_retire(
                '01900000-0000-7000-8000-000000000720', 2, 'retire');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(retiredTemplate, "retired", true, 3);

        M1E4Mutation terminalTemplate = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_template_retire(
                '01900000-0000-7000-8000-000000000720', 3, 'retire again');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(terminalTemplate, "invalid_transition", false, 3);

        M1E4Mutation adminRegrant = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-000000000730', 4,
                true, clock_timestamp() + interval '1 day',
                true, clock_timestamp() + interval '2 days',
                'active', true,
                '01900000-0000-7000-8000-000000000700', 'admin regrant');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(adminRegrant, "updated", true, 5);

        M1E4Mutation retiredAssign = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_assign(
                '01900000-0000-7000-8000-000000000733',
                '01900000-0000-7000-8000-000000000703',
                '01900000-0000-7000-8000-000000000720',
                NULL, NULL,
                '01900000-0000-7000-8000-000000000700', 'retired template');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(retiredAssign, "template_disabled", false, null);

        M1E4Mutation activeArchive = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-000000000710', 2,
                false, NULL, false, NULL, 'archived', 'archive active', NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(activeArchive, "invalid_transition", false, 2);

        M1E4Mutation disabledGroup = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-000000000710', 2,
                false, NULL, false, NULL, 'disabled', 'disable', NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(disabledGroup, "updated", true, 3);

        M1E4Mutation disabledAssign = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_assign(
                '01900000-0000-7000-8000-000000000734',
                '01900000-0000-7000-8000-000000000703',
                '01900000-0000-7000-8000-000000000720',
                NULL, NULL,
                '01900000-0000-7000-8000-000000000700', 'disabled Group');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(disabledAssign, "group_disabled", false, null);

        M1E4Mutation blockedArchive = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-000000000710', 3,
                false, NULL, false, NULL, 'archived', 'archive scheduled', NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(blockedArchive, "archive_blocked", false, 3);

        M1E4Mutation revokeScheduled = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-000000000730', 5,
                false, NULL, false, NULL, 'revoked', false,
                '01900000-0000-7000-8000-000000000700', 'revoke scheduled');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(revokeScheduled, "updated", true, 6);

        M1E4Mutation archivedGroup = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-000000000710', 3,
                false, NULL, false, NULL, 'archived', 'archive', NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(archivedGroup, "updated", true, 4);

        M1E4Mutation terminalGroup = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-000000000710', 4,
                true, 'terminal rewrite', false, NULL, 'disabled',
                'reactivate', NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(terminalGroup, "invalid_transition", false, 4);

        M1E4Mutation archivedSubscription = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-000000000730', 6,
                false, NULL, false, NULL, 'revoked', false,
                '01900000-0000-7000-8000-000000000700', 'archived Group');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(archivedSubscription, "group_archived", false, null);
    }

    private static async ValueTask AssertM1E4ArchiveConcurrencyAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        NpgsqlConnection setup = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable setupLease = setup.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(setup, cancellationToken).ConfigureAwait(false);

        M1E4Mutation raceGroup = await ExecuteM1E4MutationAsync(
            setup,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_create(
                '01900000-0000-7000-8000-000000000750', 'M1-E4 Race Group', NULL,
                '01900000-0000-7000-8000-000000000751', 100000,
                '01900000-0000-7000-8000-000000000700',
                '01900000-0000-7000-8000-000000000752',
                '01900000-0000-7000-8000-000000000753',
                'm1-e4-race-initialize', 'race create');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(raceGroup, "created", true, 1);

        using (NpgsqlCommand raceSupply = setup.CreateCommand())
        {
            raceSupply.CommandText = """
                RESET ROLE;
                INSERT INTO public.group_supply_configurations (group_id, channel_id)
                VALUES (
                    '01900000-0000-7000-8000-000000000750',
                    '01900000-0000-7000-8000-000000000714'
                );
                INSERT INTO public.group_accounts (group_id, account_id, is_enabled)
                VALUES (
                    '01900000-0000-7000-8000-000000000750',
                    '01900000-0000-7000-8000-000000000715', true
                );
                SET ROLE poolai_api;
                """;
            await raceSupply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_group_update(
                    '01900000-0000-7000-8000-000000000750', 1,
                    false, NULL, false, NULL, 'active', 'activate race',
                    'v1.m1e4race', clock_timestamp());
                """,
                cancellationToken).ConfigureAwait(false),
            "updated",
            true,
            2);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_template_create(
                    '01900000-0000-7000-8000-000000000754',
                    '01900000-0000-7000-8000-000000000750',
                    'M1-E4 Race Template', NULL, 30);
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_assign(
                    '01900000-0000-7000-8000-000000000755',
                    '01900000-0000-7000-8000-000000000701',
                    '01900000-0000-7000-8000-000000000754',
                    clock_timestamp() - interval '2 days',
                    clock_timestamp() - interval '1 day',
                    '01900000-0000-7000-8000-000000000700', 'expired race grant');
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_group_update(
                    '01900000-0000-7000-8000-000000000750', 2,
                    false, NULL, false, NULL, 'disabled', 'disable race', NULL, NULL);
                """,
                cancellationToken).ConfigureAwait(false),
            "updated",
            true,
            3);

        NpgsqlConnection extensionConnection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable extensionLease =
            extensionConnection.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(extensionConnection, cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction extensionTransaction = await extensionConnection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable extensionTransactionLease =
            extensionTransaction.ConfigureAwait(false);
        M1E4Mutation extension = await ExecuteM1E4MutationAsync(
            extensionConnection,
            extensionTransaction,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-000000000755', 1,
                false, NULL, true, clock_timestamp() + interval '1 day',
                NULL, false,
                '01900000-0000-7000-8000-000000000700', 'extend during archive');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(extension, "updated", true, 2);

        NpgsqlConnection archiveConnection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable archiveLease = archiveConnection.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(archiveConnection, cancellationToken).ConfigureAwait(false);
        int archivePid = await ReadM1E4BackendPidAsync(
            archiveConnection,
            cancellationToken).ConfigureAwait(false);
        Task<M1E4Mutation> archiveTask = ExecuteM1E4MutationAsync(
            archiveConnection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-000000000750', 3,
                false, NULL, false, NULL, 'archived', 'archive race', NULL, NULL);
            """,
            cancellationToken).AsTask();
        Assert.True(
            await WaitForM1E4LockWaitAsync(
                dataSource,
                archivePid,
                cancellationToken).ConfigureAwait(false),
            "Archive did not wait behind the Group share lock held by Subscription update.");
        await extensionTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        M1E4Mutation blockedArchive = await archiveTask.ConfigureAwait(false);
        AssertM1E4Mutation(blockedArchive, "archive_blocked", false, 3);

        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_update(
                    '01900000-0000-7000-8000-000000000755', 2,
                    false, NULL, false, NULL, 'revoked', false,
                    '01900000-0000-7000-8000-000000000700', 'revoke race');
                """,
                cancellationToken).ConfigureAwait(false),
            "updated",
            true,
            3);

        NpgsqlConnection archiveWinner = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable archiveWinnerLease =
            archiveWinner.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(archiveWinner, cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction archiveWinnerTransaction = await archiveWinner
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable archiveWinnerTransactionLease =
            archiveWinnerTransaction.ConfigureAwait(false);
        M1E4Mutation archiveWon = await ExecuteM1E4MutationAsync(
            archiveWinner,
            archiveWinnerTransaction,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-000000000750', 3,
                false, NULL, false, NULL, 'archived', 'archive wins', NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(archiveWon, "updated", true, 4);

        NpgsqlConnection lateSubscription = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lateSubscriptionLease =
            lateSubscription.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(lateSubscription, cancellationToken).ConfigureAwait(false);
        int lateSubscriptionPid = await ReadM1E4BackendPidAsync(
            lateSubscription,
            cancellationToken).ConfigureAwait(false);
        Task<M1E4Mutation> lateSubscriptionTask = ExecuteM1E4MutationAsync(
            lateSubscription,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-000000000755', 3,
                false, NULL, false, NULL, 'revoked', false,
                '01900000-0000-7000-8000-000000000700', 'late mutation');
            """,
            cancellationToken).AsTask();
        Assert.True(
            await WaitForM1E4LockWaitAsync(
                dataSource,
                lateSubscriptionPid,
                cancellationToken).ConfigureAwait(false),
            "Subscription mutation did not wait behind the Group archive lock.");
        await archiveWinnerTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        M1E4Mutation fencedSubscription = await lateSubscriptionTask.ConfigureAwait(false);
        AssertM1E4Mutation(fencedSubscription, "group_archived", false, null);

        await AssertM1E4LockWaitClockAsync(
            dataSource,
            setup,
            cancellationToken).ConfigureAwait(false);
        await AssertM1E4TemplateRenameConcurrencyAsync(
            dataSource,
            setup,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM1E4TemplateRenameConcurrencyAsync(
        NpgsqlDataSource dataSource,
        NpgsqlConnection setup,
        CancellationToken cancellationToken)
    {
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_group_create(
                    '01900000-0000-7000-8000-000000000766',
                    'M1-E4 Template Rename Group', NULL,
                    '01900000-0000-7000-8000-000000000767', 100000,
                    '01900000-0000-7000-8000-000000000700',
                    '01900000-0000-7000-8000-000000000768',
                    '01900000-0000-7000-8000-000000000769',
                    'm1-e4-template-rename-initialize', 'rename concurrency');
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_template_create(
                    '01900000-0000-7000-8000-00000000076a',
                    '01900000-0000-7000-8000-000000000766',
                    'M1-E4 Cross Rename A', NULL, 30);
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_template_create(
                    '01900000-0000-7000-8000-00000000076b',
                    '01900000-0000-7000-8000-000000000766',
                    'M1-E4 Cross Rename B', NULL, 30);
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);

        NpgsqlConnection first = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable firstLease = first.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(first, cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction firstTransaction = await first
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable firstTransactionLease =
            firstTransaction.ConfigureAwait(false);
        M1E4Mutation stagedFirst = await ExecuteM1E4MutationAsync(
            first,
            firstTransaction,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_template_update(
                '01900000-0000-7000-8000-00000000076a', 1,
                true, 'M1-E4 Cross Rename Temporary',
                false, NULL, false, NULL, NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(stagedFirst, "updated", true, 2);

        NpgsqlConnection second = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable secondLease = second.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(second, cancellationToken).ConfigureAwait(false);
        int secondPid = await ReadM1E4BackendPidAsync(second, cancellationToken)
            .ConfigureAwait(false);
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Task<M1E4Mutation> secondRename = ExecuteM1E4MutationAsync(
            second,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_template_update(
                '01900000-0000-7000-8000-00000000076b', 1,
                true, 'M1-E4 Cross Rename A',
                false, NULL, false, NULL, NULL, NULL);
            """,
            timeout.Token).AsTask();
        Assert.True(
            await WaitForM1E4LockWaitAsync(dataSource, secondPid, timeout.Token)
                .ConfigureAwait(false),
            "The competing Template rename did not wait behind the Group mutation fence.");

        bool firstCommitted = false;
        try
        {
            M1E4Mutation firstCrossRename = await ExecuteM1E4MutationAsync(
                first,
                firstTransaction,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_template_update(
                    '01900000-0000-7000-8000-00000000076a', 2,
                    true, 'M1-E4 Cross Rename B',
                    false, NULL, false, NULL, NULL, NULL);
                """,
                timeout.Token).ConfigureAwait(false);
            AssertM1E4Mutation(firstCrossRename, "conflict", false, 2);
            AssertM1E4BeforeState(
                firstCrossRename,
                "name",
                "M1-E4 Cross Rename Temporary");
            await firstTransaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            firstCommitted = true;

            M1E4Mutation secondWon = await secondRename.ConfigureAwait(false);
            AssertM1E4Mutation(secondWon, "updated", true, 2);
            AssertM1E4BeforeState(secondWon, "name", "M1-E4 Cross Rename B");
        }
        finally
        {
            if (!firstCommitted)
            {
                await firstTransaction.RollbackAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (!secondRename.IsCompleted)
            {
                timeout.Cancel();
            }

            try
            {
                _ = await secondRename.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // The primary assertion/exception remains the test failure.
            }
        }

        using NpgsqlCommand finalState = setup.CreateCommand();
        finalState.CommandText = """
            SELECT string_agg(template.name || ':' || template.version::text, ','
                              ORDER BY template.id)
            FROM public.subscription_templates AS template
            WHERE template.id IN (
                '01900000-0000-7000-8000-00000000076a',
                '01900000-0000-7000-8000-00000000076b'
            );
            """;
        Assert.Equal(
            "M1-E4 Cross Rename Temporary:2,M1-E4 Cross Rename A:2",
            Assert.IsType<string>(await finalState
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask AssertM1E4LockWaitClockAsync(
        NpgsqlDataSource dataSource,
        NpgsqlConnection setup,
        CancellationToken cancellationToken)
    {
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_group_create(
                    '01900000-0000-7000-8000-000000000760', 'M1-E4 Clock Group', NULL,
                    '01900000-0000-7000-8000-000000000761', 100000,
                    '01900000-0000-7000-8000-000000000700',
                    '01900000-0000-7000-8000-000000000762',
                    '01900000-0000-7000-8000-000000000763',
                    'm1-e4-clock-initialize', 'clock create');
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        using (NpgsqlCommand clockSupply = setup.CreateCommand())
        {
            clockSupply.CommandText = """
                RESET ROLE;
                INSERT INTO public.group_supply_configurations (group_id, channel_id)
                VALUES (
                    '01900000-0000-7000-8000-000000000760',
                    '01900000-0000-7000-8000-000000000714'
                );
                INSERT INTO public.group_accounts (group_id, account_id, is_enabled)
                VALUES (
                    '01900000-0000-7000-8000-000000000760',
                    '01900000-0000-7000-8000-000000000715', true
                );
                SET ROLE poolai_api;
                """;
            await clockSupply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_group_update(
                    '01900000-0000-7000-8000-000000000760', 1,
                    false, NULL, false, NULL, 'active', 'activate clock',
                    'v1.m1e4clock', clock_timestamp());
                """,
                cancellationToken).ConfigureAwait(false),
            "updated",
            true,
            2);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_template_create(
                    '01900000-0000-7000-8000-000000000764',
                    '01900000-0000-7000-8000-000000000760',
                    'M1-E4 Clock Template', NULL, 30);
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_assign(
                    '01900000-0000-7000-8000-000000000765',
                    '01900000-0000-7000-8000-000000000702',
                    '01900000-0000-7000-8000-000000000764',
                    clock_timestamp() - interval '1 second',
                    clock_timestamp() + interval '5 seconds',
                    '01900000-0000-7000-8000-000000000700', 'clock grant');
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        AssertM1E4Mutation(
            await ExecuteM1E4MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_subscription_update(
                    '01900000-0000-7000-8000-000000000765', 1,
                    false, NULL, false, NULL, 'suspended', false,
                    '01900000-0000-7000-8000-000000000700', 'clock pause');
                """,
                cancellationToken).ConfigureAwait(false),
            "updated",
            true,
            2);

        NpgsqlConnection blocker = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable blockerLease = blocker.ConfigureAwait(false);
        NpgsqlTransaction blockerTransaction = await blocker
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable blockerTransactionLease =
            blockerTransaction.ConfigureAwait(false);
        using (NpgsqlCommand lockSubscription = blocker.CreateCommand())
        {
            lockSubscription.Transaction = blockerTransaction;
            lockSubscription.CommandText = """
                SELECT id
                FROM public.subscriptions
                WHERE id = '01900000-0000-7000-8000-000000000765'
                FOR UPDATE;
                """;
            _ = await lockSubscription
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using (NpgsqlCommand beforeBoundary = blocker.CreateCommand())
        {
            beforeBoundary.Transaction = blockerTransaction;
            beforeBoundary.CommandText = """
                SELECT clock_timestamp() < expires_at
                FROM public.subscriptions
                WHERE id = '01900000-0000-7000-8000-000000000765';
                """;
            Assert.True(Assert.IsType<bool>(await beforeBoundary
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
        }

        NpgsqlConnection waiter = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable waiterLease = waiter.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(waiter, cancellationToken).ConfigureAwait(false);
        int waiterPid = await ReadM1E4BackendPidAsync(waiter, cancellationToken)
            .ConfigureAwait(false);
        Task<M1E4Mutation> resumeTask = ExecuteM1E4MutationAsync(
            waiter,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-000000000765', 2,
                false, NULL, false, NULL, 'active', false,
                '01900000-0000-7000-8000-000000000700', 'resume after wait');
            """,
            cancellationToken).AsTask();
        Assert.True(
            await WaitForM1E4LockWaitAsync(dataSource, waiterPid, cancellationToken)
                .ConfigureAwait(false),
            "Subscription resume did not reach the row-lock wait boundary.");
        await WaitForM1E4SubscriptionExpiryAsync(
            setup,
            new Guid("01900000-0000-7000-8000-000000000765"),
            cancellationToken).ConfigureAwait(false);
        await blockerTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        M1E4Mutation resumeAfterExpiry = await resumeTask.ConfigureAwait(false);
        AssertM1E4Mutation(resumeAfterExpiry, "invalid_transition", false, 2);

        using NpgsqlCommand persisted = setup.CreateCommand();
        persisted.CommandText = """
            SELECT status || ':' || version::text
            FROM public.subscriptions
            WHERE id = '01900000-0000-7000-8000-000000000765';
            """;
        Assert.Equal(
            "suspended:2",
            Assert.IsType<string>(await persisted
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask<M1E4Mutation> ExecuteM1E4MutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M1E4Mutation mutation = new(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return mutation;
    }

    private static void AssertM1E4Mutation(
        M1E4Mutation mutation,
        string disposition,
        bool wasChanged,
        long? currentVersion)
    {
        Assert.Equal(disposition, mutation.Disposition);
        Assert.Equal(wasChanged, mutation.WasChanged);
        Assert.Equal(currentVersion, mutation.CurrentVersion);
    }

    private static void AssertM1E4BeforeState(
        M1E4Mutation mutation,
        string propertyName,
        string expectedValue)
    {
        Assert.NotNull(mutation.BeforeState);
        using JsonDocument before = JsonDocument.Parse(mutation.BeforeState!);
        Assert.Equal(expectedValue, before.RootElement.GetProperty(propertyName).GetString());
    }

    private static async ValueTask SetM1E4ApiRoleAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SET ROLE poolai_api;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadM1E4BackendPidAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.pg_backend_pid();";
        return Assert.IsType<int>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<bool> WaitForM1E4LockWaitAsync(
        NpgsqlDataSource dataSource,
        int processId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            using NpgsqlCommand command = dataSource.CreateCommand("""
                SELECT wait_event_type = 'Lock'
                FROM pg_catalog.pg_stat_activity
                WHERE pid = $1;
                """);
            command.Parameters.AddWithValue(processId);
            object? scalar = await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (scalar is true)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private static async ValueTask WaitForM1E4SubscriptionExpiryAsync(
        NpgsqlConnection connection,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 480; attempt++)
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT clock_timestamp() >= expires_at
                FROM public.subscriptions
                WHERE id = $1;
                """;
            command.Parameters.AddWithValue(subscriptionId);
            object? scalar = await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (scalar is true)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                .ConfigureAwait(false);
        }

        Assert.Fail("The Subscription did not reach its PostgreSQL expiry boundary.");
    }

    private static async ValueTask<string> ReadM1E4EffectiveStateAsync(
        NpgsqlConnection connection,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH observed AS MATERIALIZED (
                SELECT clock_timestamp() AS at
            )
            SELECT subscription.status || ':' ||
                   CASE
                       WHEN subscription.status = 'revoked' THEN 'revoked'
                       WHEN subscription.status = 'suspended' THEN 'suspended'
                       WHEN observed.at < subscription.starts_at THEN 'scheduled'
                       WHEN observed.at >= subscription.expires_at THEN 'expired'
                       ELSE 'active'
                   END || ':' || subscription.version::text
            FROM public.subscriptions AS subscription
            CROSS JOIN observed
            WHERE subscription.id = $1;
            """;
        command.Parameters.AddWithValue(subscriptionId);
        return Assert.IsType<string>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private sealed record M1E4Mutation(
        string Disposition,
        bool WasChanged,
        string? BeforeState,
        long? CurrentVersion);
}
#pragma warning restore MA0051
