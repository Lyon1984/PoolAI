#pragma warning disable MA0051 // M1-E5 database lifecycle and lock scenarios stay explicit.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private static async ValueTask AssertM1E5RuntimePermissionsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        // Governing contract: ADR 0007 and docs/database/README.md require all
        // API Key writes to pass through the Identity-owned entry points.
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            INSERT INTO public.api_keys (
                id, user_id, group_id, name, key_prefix,
                secret_hash, pepper_version
            ) VALUES (
                '01910000-0000-7000-8000-000000000801',
                '01910000-0000-7000-8000-000000000802',
                '01910000-0000-7000-8000-000000000803',
                'M1-E5 bypass', 'sk-pool-Bypass01',
                decode(repeat('01', 32), 'hex'), 1
            );
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; UPDATE public.api_keys SET name = name WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; DELETE FROM public.api_keys WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            MERGE INTO public.api_keys AS target
            USING (SELECT NULL::uuid AS id WHERE false) AS source
              ON target.id = source.id
            WHEN MATCHED THEN UPDATE SET name = target.name;
            """,
            cancellationToken).ConfigureAwait(false);

        const string SecuritySql = """
            WITH expected(signature) AS (
                VALUES
                    ('public.poolai_api_key_create(uuid,uuid,uuid,text,text,bytea,smallint,timestamp with time zone,jsonb)'),
                    ('public.poolai_api_key_update(uuid,uuid,uuid,bigint,text,boolean,text,boolean,text,boolean,timestamp with time zone,boolean,jsonb)'),
                    ('public.poolai_api_key_revoke(uuid,uuid,bigint,text)'),
                    ('public.poolai_api_key_rotate(uuid,uuid,uuid,bigint,uuid,text,bytea,smallint,text)')
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
              AND EXISTS (
                  SELECT 1
                  FROM pg_catalog.aclexplode(COALESCE(
                      function.proacl,
                      pg_catalog.acldefault('f', function.proowner))) AS privilege
                  WHERE privilege.privilege_type = 'EXECUTE'
                    AND privilege.grantee = (
                        SELECT role.oid
                        FROM pg_catalog.pg_roles AS role
                        WHERE role.rolname = 'poolai_api'
                    )
                    AND privilege.grantor = function.proowner
                    AND NOT privilege.is_grantable
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.aclexplode(COALESCE(
                      function.proacl,
                      pg_catalog.acldefault('f', function.proowner))) AS privilege
                  WHERE privilege.privilege_type = 'EXECUTE'
                    AND (
                        privilege.grantor <> function.proowner
                        OR privilege.grantee NOT IN (
                            function.proowner,
                            (
                                SELECT role.oid
                                FROM pg_catalog.pg_roles AS role
                                WHERE role.rolname = 'poolai_api'
                            )
                        )
                        OR (
                            privilege.grantee = (
                                SELECT role.oid
                                FROM pg_catalog.pg_roles AS role
                                WHERE role.rolname = 'poolai_api'
                            )
                            AND privilege.is_grantable
                        )
                    )
              );
            """;
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using (NpgsqlCommand security = dataSource.CreateCommand(SecuritySql))
        {
            Assert.Equal(
                4L,
                Assert.IsType<long>(await security
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        NpgsqlConnection aclProbe = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (aclProbe.ConfigureAwait(false))
        {
            NpgsqlTransaction aclProbeTransaction = await aclProbe
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (aclProbeTransaction.ConfigureAwait(false))
            {
                using (NpgsqlCommand addUnexpectedGrant = aclProbe.CreateCommand())
                {
                    addUnexpectedGrant.Transaction = aclProbeTransaction;
                    addUnexpectedGrant.CommandText = """
                        GRANT EXECUTE ON FUNCTION public.poolai_api_key_create(
                            uuid, uuid, uuid, text, text, bytea,
                            smallint, timestamptz, jsonb
                        ) TO pg_monitor;
                        """;
                    await addUnexpectedGrant
                        .ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                using (NpgsqlCommand detectUnexpectedGrant = aclProbe.CreateCommand())
                {
                    detectUnexpectedGrant.Transaction = aclProbeTransaction;
                    detectUnexpectedGrant.CommandText = SecuritySql;
                    Assert.Equal(
                        3L,
                        Assert.IsType<long>(await detectUnexpectedGrant
                            .ExecuteScalarAsync(cancellationToken)
                            .ConfigureAwait(false)));
                }

                await aclProbeTransaction
                    .RollbackAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        using (NpgsqlCommand overloads = dataSource.CreateCommand("""
            SELECT count(*)
            FROM pg_catalog.pg_proc AS function
            JOIN pg_catalog.pg_namespace AS schema
              ON schema.oid = function.pronamespace
            WHERE schema.nspname = 'public'
              AND function.proname = ANY (ARRAY[
                  'poolai_api_key_create',
                  'poolai_api_key_update',
                  'poolai_api_key_revoke',
                  'poolai_api_key_rotate'
              ]);
            """))
        {
            Assert.Equal(
                4L,
                Assert.IsType<long>(await overloads
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand validator = dataSource.CreateCommand("""
            SELECT count(*)
            FROM pg_catalog.pg_proc AS function
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = function.proowner
            WHERE function.oid = pg_catalog.to_regprocedure(
                      'public.poolai_api_key_ip_acl_is_canonical(jsonb)')
              AND NOT function.prosecdef
              AND function.provolatile = 'i'
              AND function.proisstrict
              AND owner.rolname = 'poolai_runtime_owner'
              AND owner.rolcanlogin = false
              AND function.proconfig @> ARRAY[
                  'search_path=pg_catalog, public, pg_temp'
              ]::text[]
              AND NOT pg_catalog.has_function_privilege(
                  'poolai_api', function.oid, 'EXECUTE')
              AND NOT pg_catalog.has_function_privilege(
                  'poolai_worker', function.oid, 'EXECUTE')
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.aclexplode(COALESCE(
                      function.proacl,
                      pg_catalog.acldefault('f', function.proowner))) AS privilege
                  WHERE privilege.privilege_type = 'EXECUTE'
                    AND (
                        privilege.grantor <> function.proowner
                        OR privilege.grantee <> function.proowner
                    )
              );
            """))
        {
            Assert.Equal(
                1L,
                Assert.IsType<long>(await validator
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using NpgsqlCommand effective = dataSource.CreateCommand("""
            WITH scope(role_name, expected_select) AS (
                VALUES
                    ('poolai_api', true),
                    ('poolai_worker', false)
            ), invalid_privilege AS (
                SELECT 1
                FROM scope
                WHERE pg_catalog.has_any_column_privilege(
                          role_name, 'public.api_keys', 'SELECT')
                      IS DISTINCT FROM expected_select
                   OR pg_catalog.has_table_privilege(
                          role_name, 'public.api_keys', 'SELECT WITH GRANT OPTION')
                   OR pg_catalog.has_any_column_privilege(
                          role_name, 'public.api_keys', 'SELECT WITH GRANT OPTION')
                   OR pg_catalog.has_table_privilege(
                          role_name, 'public.api_keys', 'INSERT')
                   OR pg_catalog.has_any_column_privilege(
                          role_name, 'public.api_keys', 'INSERT')
                   OR pg_catalog.has_table_privilege(
                          role_name, 'public.api_keys', 'UPDATE')
                   OR pg_catalog.has_any_column_privilege(
                          role_name, 'public.api_keys', 'UPDATE')
                   OR pg_catalog.has_table_privilege(
                          role_name, 'public.api_keys', 'DELETE')
                   OR pg_catalog.has_table_privilege(
                          role_name, 'public.api_keys', 'TRUNCATE')
                   OR pg_catalog.has_table_privilege(
                          role_name, 'public.api_keys', 'REFERENCES')
                   OR pg_catalog.has_table_privilege(
                          role_name, 'public.api_keys', 'TRIGGER')
                   OR pg_catalog.has_table_privilege(
                          role_name, 'public.api_keys', 'MAINTAIN')
            ), owner_columns AS (
                SELECT
                    COALESCE(
                        array_agg(attribute.attname::text ORDER BY attribute.attnum)
                            FILTER (WHERE pg_catalog.has_column_privilege(
                                'poolai_runtime_owner',
                                'public.api_keys',
                                attribute.attname,
                                'SELECT')),
                        ARRAY[]::text[]) AS select_columns,
                    COALESCE(
                        array_agg(attribute.attname::text ORDER BY attribute.attnum)
                            FILTER (WHERE pg_catalog.has_column_privilege(
                                'poolai_runtime_owner',
                                'public.api_keys',
                                attribute.attname,
                                'INSERT')),
                        ARRAY[]::text[]) AS insert_columns,
                    COALESCE(
                        array_agg(attribute.attname::text ORDER BY attribute.attnum)
                            FILTER (WHERE pg_catalog.has_column_privilege(
                                'poolai_runtime_owner',
                                'public.api_keys',
                                attribute.attname,
                                'UPDATE')),
                        ARRAY[]::text[]) AS update_columns
                FROM pg_catalog.pg_attribute AS attribute
                WHERE attribute.attrelid = 'public.api_keys'::regclass
                  AND attribute.attnum > 0
                  AND NOT attribute.attisdropped
            ), invalid_owner_privilege AS (
                SELECT 1
                FROM owner_columns
                WHERE select_columns IS DISTINCT FROM ARRAY[
                          'id', 'user_id', 'group_id', 'name', 'key_prefix',
                          'status', 'expires_at', 'ip_acl', 'last_used_at',
                          'revoked_at', 'revoke_reason', 'version',
                          'created_at', 'updated_at'
                      ]::text[]
                   OR insert_columns IS DISTINCT FROM ARRAY[
                          'id', 'user_id', 'group_id', 'name', 'key_prefix',
                          'secret_hash', 'pepper_version', 'status',
                          'expires_at', 'ip_acl', 'version', 'created_at',
                          'updated_at'
                      ]::text[]
                   OR update_columns IS DISTINCT FROM ARRAY[
                          'id', 'name', 'status', 'expires_at', 'ip_acl',
                          'revoked_at', 'revoke_reason', 'version', 'updated_at'
                      ]::text[]
                   OR pg_catalog.has_table_privilege(
                          'poolai_runtime_owner', 'public.api_keys', 'SELECT')
                   OR pg_catalog.has_table_privilege(
                          'poolai_runtime_owner', 'public.api_keys', 'INSERT')
                   OR pg_catalog.has_table_privilege(
                          'poolai_runtime_owner', 'public.api_keys', 'UPDATE')
                   OR pg_catalog.has_table_privilege(
                          'poolai_runtime_owner', 'public.api_keys', 'DELETE')
                   OR pg_catalog.has_table_privilege(
                          'poolai_runtime_owner', 'public.api_keys', 'TRUNCATE')
                   OR pg_catalog.has_table_privilege(
                          'poolai_runtime_owner', 'public.api_keys', 'REFERENCES')
                   OR pg_catalog.has_table_privilege(
                          'poolai_runtime_owner', 'public.api_keys', 'TRIGGER')
                   OR pg_catalog.has_table_privilege(
                          'poolai_runtime_owner', 'public.api_keys', 'MAINTAIN')
                   OR pg_catalog.has_any_column_privilege(
                          'poolai_runtime_owner', 'public.api_keys',
                          'SELECT WITH GRANT OPTION')
                   OR pg_catalog.has_any_column_privilege(
                          'poolai_runtime_owner', 'public.api_keys',
                          'INSERT WITH GRANT OPTION')
                   OR pg_catalog.has_any_column_privilege(
                          'poolai_runtime_owner', 'public.api_keys',
                          'UPDATE WITH GRANT OPTION')
                   OR pg_catalog.has_schema_privilege(
                          'poolai_runtime_owner', 'public', 'CREATE')
            ), unexpected_membership AS (
                SELECT 1
                FROM pg_catalog.pg_auth_members AS membership
                JOIN pg_catalog.pg_roles AS member ON member.oid = membership.member
                WHERE member.rolname IN ('poolai_api', 'poolai_worker')
            ), exposed_credential AS (
                SELECT 1
                FROM (VALUES
                    ('poolai_runtime_owner', 'secret_hash'),
                    ('poolai_runtime_owner', 'pepper_version'),
                    ('poolai_worker', 'secret_hash'),
                    ('poolai_worker', 'pepper_version')
                ) AS forbidden(role_name, column_name)
                WHERE pg_catalog.has_column_privilege(
                    forbidden.role_name,
                    'public.api_keys',
                    forbidden.column_name,
                    'SELECT')
            )
            SELECT (SELECT count(*) FROM invalid_privilege)
                 + (SELECT count(*) FROM invalid_owner_privilege)
                 + (SELECT count(*) FROM unexpected_membership)
                 + (SELECT count(*) FROM exposed_credential);
            """);
        Assert.Equal(
            0L,
            Assert.IsType<long>(await effective
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask AssertM1E5LifecycleAndClockAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        NpgsqlConnection setup = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable setupLease = setup.ConfigureAwait(false);

        using (NpgsqlCommand seed = setup.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO public.users (
                    id, email, normalized_email, display_name,
                    password_hash, security_stamp
                ) VALUES (
                    '01910000-0000-7000-8000-000000000810',
                    'm1e5-owner@example.test', 'm1e5-owner@example.test',
                    'M1-E5 Owner', 'poolai-password-v1:test',
                    '01910000-0000-7000-8000-000000000811'
                );
                INSERT INTO public.groups (id, name)
                VALUES (
                    '01910000-0000-7000-8000-000000000812',
                    'M1-E5 API Key Group'
                );
                SET ROLE poolai_api;
                """;
            await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        M1E5Mutation created = await ExecuteM1E5MutationAsync(
            setup,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_create(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812',
                'M1-E5 Primary', 'sk-pool-AbCdEf12',
                decode(repeat('11', 32), 'hex'), 1::smallint,
                clock_timestamp() + interval '1 day',
                '["10.0.0.0/24","2001:db8::/32"]'::jsonb
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(created, "created", true, 1);
        Assert.Null(created.BeforeState);

        using (NpgsqlCommand persistedCreate = setup.CreateCommand())
        {
            persistedCreate.CommandText = """
                SELECT name || ':' || key_prefix || ':' || status || ':' || version::text,
                       ip_acl::text,
                       encode(secret_hash, 'hex'),
                       pepper_version
                FROM public.api_keys
                WHERE id = '01910000-0000-7000-8000-000000000820';
                """;
            using NpgsqlDataReader reader = await persistedCreate
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            Assert.Equal("M1-E5 Primary:sk-pool-AbCdEf12:active:1", reader.GetString(0));
            Assert.Equal("[\"10.0.0.0/24\", \"2001:db8::/32\"]", reader.GetString(1));
            Assert.Equal(new string('1', 64), reader.GetString(2));
            Assert.Equal((short)1, reader.GetInt16(3));
            Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        }

        await AssertM1E5CanonicalCidrValidationAsync(setup, cancellationToken)
            .ConfigureAwait(false);
        await AssertM1E5ValidationAndDispositionAsync(setup, cancellationToken)
            .ConfigureAwait(false);

        M1E5Mutation disabled = await ExecuteM1E5MutationAsync(
            setup,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 1, 'active',
                false, NULL, true, 'disabled', false, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(disabled, "updated", true, 2);
        AssertM1E5BeforeState(disabled, "status", "active");

        M1E5Mutation stale = await ExecuteM1E5MutationAsync(
            setup,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 1, 'disabled',
                true, 'stale', false, NULL, false, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(stale, "version_conflict", false, 2);
        AssertM1E5BeforeState(stale, "status", "disabled");

        M1E5Mutation fixedGroup = await ExecuteM1E5MutationAsync(
            setup,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-0000000008ff', 2, 'disabled',
                true, 'must not move Group', false, NULL, false, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(fixedGroup, "resource_conflict", false, 2);
        AssertM1E5BeforeState(
            fixedGroup,
            "group_id",
            "01910000-0000-7000-8000-000000000812");

        M1E5Mutation noOp = await ExecuteM1E5MutationAsync(
            setup,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 2, 'disabled',
                false, NULL, true, 'disabled', false, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(noOp, "updated", false, 2);

        M1E5Mutation revoked = await ExecuteM1E5MutationAsync(
            setup,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_revoke(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810', 2,
                'M1-E5 retired credential'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(revoked, "revoked", true, 3);
        AssertM1E5BeforeState(revoked, "status", "disabled");

        M1E5Mutation repeatedRevoke = await ExecuteM1E5MutationAsync(
            setup,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_revoke(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810', 1,
                'must preserve terminal precedence'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(repeatedRevoke, "api_key_revoked", false, 3);
        AssertM1E5BeforeState(repeatedRevoke, "status", "revoked");

        M1E5Mutation revokedPrecedence = await ExecuteM1E5MutationAsync(
            setup,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 1, 'revoked',
                false, NULL, true, 'active', false, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(revokedPrecedence, "api_key_revoked", false, 3);
        AssertM1E5BeforeState(revokedPrecedence, "status", "revoked");

        await AssertM1E5RotationAndAtomicityAsync(dataSource, setup, cancellationToken)
            .ConfigureAwait(false);
        await AssertM1E5LockWaitClockAsync(dataSource, setup, cancellationToken)
            .ConfigureAwait(false);
        await AssertM1E5ActiveExpiryDriftAsync(dataSource, setup, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask AssertM1E5CanonicalCidrValidationAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH invalid(case_name, key_id, digest, ip_acl) AS (
                VALUES
                    (
                        'host-bits',
                        '01910000-0000-7000-8000-000000000821'::uuid,
                        decode(repeat('21', 32), 'hex'),
                        '["10.0.0.1/24"]'::jsonb
                    ),
                    (
                        'duplicate',
                        '01910000-0000-7000-8000-000000000822'::uuid,
                        decode(repeat('22', 32), 'hex'),
                        '["10.0.0.0/24","10.0.0.0/24"]'::jsonb
                    ),
                    (
                        'unsorted',
                        '01910000-0000-7000-8000-000000000823'::uuid,
                        decode(repeat('23', 32), 'hex'),
                        '["2001:db8::/32","10.0.0.0/24"]'::jsonb
                    ),
                    (
                        'mapped-v4',
                        '01910000-0000-7000-8000-000000000824'::uuid,
                        decode(repeat('24', 32), 'hex'),
                        '["::ffff:192.0.2.0/120"]'::jsonb
                    ),
                    (
                        'zone',
                        '01910000-0000-7000-8000-000000000825'::uuid,
                        decode(repeat('25', 32), 'hex'),
                        '["fe80::%eth0/64"]'::jsonb
                    ),
                    (
                        'leading-zero',
                        '01910000-0000-7000-8000-000000000826'::uuid,
                        decode(repeat('26', 32), 'hex'),
                        '["010.0.0.0/8"]'::jsonb
                    ),
                    (
                        'non-string',
                        '01910000-0000-7000-8000-000000000827'::uuid,
                        decode(repeat('27', 32), 'hex'),
                        '[1]'::jsonb
                    ),
                    (
                        'not-array',
                        '01910000-0000-7000-8000-00000000082c'::uuid,
                        decode(repeat('2c', 32), 'hex'),
                        '{"cidr":"10.0.0.0/24"}'::jsonb
                    ),
                    (
                        'uppercase-ipv6',
                        '01910000-0000-7000-8000-000000000828'::uuid,
                        decode(repeat('28', 32), 'hex'),
                        '["2001:DB8::/32"]'::jsonb
                    ),
                    (
                        'overlong',
                        '01910000-0000-7000-8000-00000000082a'::uuid,
                        decode(repeat('2a', 32), 'hex'),
                        pg_catalog.to_jsonb(ARRAY[repeat('a', 65) || '/1'])
                    )
            )
            SELECT invalid.case_name,
                   mutation.disposition,
                   mutation.was_changed,
                   mutation.before_state::text,
                   mutation.current_version
            FROM invalid
            CROSS JOIN LATERAL public.poolai_api_key_create(
                invalid.key_id,
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812',
                'M1-E5 invalid CIDR', 'sk-pool-Inval1d0',
                invalid.digest, 1::smallint, NULL, invalid.ip_acl
            ) AS mutation
            ORDER BY invalid.case_name;
            """;
        int observed = 0;
        using (NpgsqlDataReader reader = await command
                   .ExecuteReaderAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                observed++;
                Assert.Equal("validation_failed", reader.GetString(1));
                Assert.False(reader.GetBoolean(2));
                Assert.True(reader.IsDBNull(3));
                Assert.True(reader.IsDBNull(4));
            }
        }

        Assert.Equal(10, observed);

        M1E5Mutation tooMany = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_create(
                '01910000-0000-7000-8000-00000000082b',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812',
                'M1-E5 too many CIDRs', 'sk-pool-TooMany1',
                decode(repeat('2b', 32), 'hex'), 1::smallint, NULL,
                (SELECT jsonb_agg('10.0.0.0/8'::text)
                 FROM generate_series(1, 51))
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(tooMany, "validation_failed", false, null);
    }

    private static async ValueTask AssertM1E5ValidationAndDispositionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand invalidCreates = connection.CreateCommand())
        {
            invalidCreates.CommandText = """
                WITH invalid(
                    case_name, key_id, key_prefix, digest,
                    pepper_version, expires_at
                ) AS (
                    VALUES
                        (
                            'prefix',
                            '01910000-0000-7000-8000-000000000860'::uuid,
                            'bad-prefix', decode(repeat('60', 32), 'hex'),
                            1::smallint, NULL::timestamptz
                        ),
                        (
                            'digest-length',
                            '01910000-0000-7000-8000-000000000861'::uuid,
                            'sk-pool-Invalid01', decode(repeat('61', 31), 'hex'),
                            1::smallint, NULL::timestamptz
                        ),
                        (
                            'pepper-version',
                            '01910000-0000-7000-8000-000000000862'::uuid,
                            'sk-pool-Invalid02', decode(repeat('62', 32), 'hex'),
                            0::smallint, NULL::timestamptz
                        ),
                        (
                            'past-expiry',
                            '01910000-0000-7000-8000-000000000863'::uuid,
                            'sk-pool-Invalid03', decode(repeat('63', 32), 'hex'),
                            1::smallint,
                            clock_timestamp() - interval '1 second'
                        )
                )
                SELECT invalid.case_name,
                       mutation.disposition,
                       mutation.was_changed,
                       mutation.before_state::text,
                       mutation.current_version
                FROM invalid
                CROSS JOIN LATERAL public.poolai_api_key_create(
                    invalid.key_id,
                    '01910000-0000-7000-8000-000000000810',
                    '01910000-0000-7000-8000-000000000812',
                    'M1-E5 invalid create', invalid.key_prefix,
                    invalid.digest, invalid.pepper_version,
                    invalid.expires_at, '[]'::jsonb
                ) AS mutation
                ORDER BY invalid.case_name;
                """;
            using NpgsqlDataReader reader = await invalidCreates
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            int observed = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                observed++;
                Assert.Equal("validation_failed", reader.GetString(1));
                Assert.False(reader.GetBoolean(2));
                Assert.True(reader.IsDBNull(3));
                Assert.True(reader.IsDBNull(4));
            }

            Assert.Equal(4, observed);
        }

        M1E5Mutation nullUpdateVersion = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812',
                NULL::bigint, 'active',
                true, 'must not update', false, NULL,
                false, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(nullUpdateVersion, "validation_failed", false, null);
        Assert.Null(nullUpdateVersion.BeforeState);

        M1E5Mutation nullUpdateLifecycle = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812',
                1, NULL::text,
                true, 'must not update', false, NULL,
                false, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(nullUpdateLifecycle, "validation_failed", false, null);

        M1E5Mutation wrongOwner = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-0000000008ff',
                '01910000-0000-7000-8000-000000000812', 1, 'active',
                true, 'must not update', false, NULL,
                false, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(wrongOwner, "not_found", false, null);

        M1E5Mutation missingKey = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-0000000008ff',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 1, 'active',
                true, 'must not update', false, NULL,
                false, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(missingKey, "not_found", false, null);

        M1E5Mutation wrongOwnerRevoke = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_revoke(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-0000000008ff',
                1, 'must not revoke'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(wrongOwnerRevoke, "not_found", false, null);

        M1E5Mutation missingRevoke = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_revoke(
                '01910000-0000-7000-8000-0000000008ff',
                '01910000-0000-7000-8000-000000000810',
                1, 'must not revoke'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(missingRevoke, "not_found", false, null);

        M1E5Mutation nullRevokeVersion = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_revoke(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810',
                NULL::bigint, 'must not revoke'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(nullRevokeVersion, "validation_failed", false, null);

        M1E5Mutation staleRevoke = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_revoke(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810',
                2, 'must not revoke'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(staleRevoke, "version_conflict", false, 1);
        AssertM1E5BeforeState(staleRevoke, "status", "active");

        M1E5Rotation nullRotateVersion = await ExecuteM1E5RotationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text,
                   old_current_version, new_api_key_id, new_current_version
            FROM public.poolai_api_key_rotate(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', NULL::bigint,
                '01910000-0000-7000-8000-000000000864',
                'sk-pool-Invalid04', decode(repeat('64', 32), 'hex'),
                1::smallint, 'must not rotate'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Rotation(
            nullRotateVersion,
            "validation_failed",
            false,
            null,
            null,
            null);

        M1E5Rotation wrongOwnerRotation = await ExecuteM1E5RotationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text,
                   old_current_version, new_api_key_id, new_current_version
            FROM public.poolai_api_key_rotate(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-0000000008ff',
                '01910000-0000-7000-8000-000000000812', 1,
                '01910000-0000-7000-8000-000000000865',
                'sk-pool-Invalid05', decode(repeat('65', 32), 'hex'),
                1::smallint, 'must not rotate'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Rotation(
            wrongOwnerRotation,
            "not_found",
            false,
            null,
            null,
            null);

        M1E5Rotation missingRotation = await ExecuteM1E5RotationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text,
                   old_current_version, new_api_key_id, new_current_version
            FROM public.poolai_api_key_rotate(
                '01910000-0000-7000-8000-0000000008fe',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 1,
                '01910000-0000-7000-8000-000000000866',
                'sk-pool-Invalid06', decode(repeat('66', 32), 'hex'),
                1::smallint, 'must not rotate'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Rotation(
            missingRotation,
            "not_found",
            false,
            null,
            null,
            null);

        M1E5Rotation wrongGroupRotation = await ExecuteM1E5RotationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text,
                   old_current_version, new_api_key_id, new_current_version
            FROM public.poolai_api_key_rotate(
                '01910000-0000-7000-8000-000000000820',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-0000000008ff', 1,
                '01910000-0000-7000-8000-000000000867',
                'sk-pool-Invalid07', decode(repeat('67', 32), 'hex'),
                1::smallint, 'must not rotate'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Rotation(
            wrongGroupRotation,
            "resource_conflict",
            false,
            1,
            null,
            null);
        AssertM1E5BeforeState(
            wrongGroupRotation.BeforeState,
            "group_id",
            "01910000-0000-7000-8000-000000000812");

        using NpgsqlCommand unchanged = connection.CreateCommand();
        unchanged.CommandText = """
            SELECT
                (SELECT status || ':' || version::text || ':' || name
                 FROM public.api_keys
                 WHERE id = '01910000-0000-7000-8000-000000000820'),
                (SELECT count(*)
                 FROM public.api_keys
                 WHERE id BETWEEN
                     '01910000-0000-7000-8000-000000000860'
                     AND '01910000-0000-7000-8000-000000000867');
            """;
        using NpgsqlDataReader unchangedReader = await unchanged
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await unchangedReader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal("active:1:M1-E5 Primary", unchangedReader.GetString(0));
        Assert.Equal(0L, unchangedReader.GetInt64(1));
        Assert.False(await unchangedReader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask AssertM1E5RotationAndAtomicityAsync(
        NpgsqlDataSource dataSource,
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        M1E5Mutation rotationSource = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_create(
                '01910000-0000-7000-8000-000000000830',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812',
                'M1-E5 Rotate Source', 'sk-pool-Rotate01',
                decode(repeat('31', 32), 'hex'), 1::smallint,
                clock_timestamp() + interval '2 days',
                '["10.20.0.0/16","2001:db8:1::/48"]'::jsonb
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(rotationSource, "created", true, 1);

        M1E5Mutation disabledSource = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000830',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 1, 'active',
                false, NULL, true, 'disabled', false, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(disabledSource, "updated", true, 2);

        M1E5Rotation staleRotation = await ExecuteM1E5RotationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text,
                   old_current_version, new_api_key_id, new_current_version
            FROM public.poolai_api_key_rotate(
                '01910000-0000-7000-8000-000000000830',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 1,
                '01910000-0000-7000-8000-000000000829',
                'sk-pool-Stale001', decode(repeat('29', 32), 'hex'), 2::smallint,
                'stale rotation'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Rotation(staleRotation, "version_conflict", false, 2, null, null);
        AssertM1E5BeforeState(staleRotation.BeforeState, "status", "disabled");

        M1E5Rotation rotated = await ExecuteM1E5RotationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text,
                   old_current_version, new_api_key_id, new_current_version
            FROM public.poolai_api_key_rotate(
                '01910000-0000-7000-8000-000000000830',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 2,
                '01910000-0000-7000-8000-000000000831',
                'sk-pool-Rotate02', decode(repeat('32', 32), 'hex'), 2::smallint,
                'M1-E5 routine rotation'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Rotation(
            rotated,
            "rotated",
            true,
            3,
            new Guid("01910000-0000-7000-8000-000000000831"),
            1);
        AssertM1E5BeforeState(rotated.BeforeState, "status", "disabled");

        using (NpgsqlCommand copied = connection.CreateCommand())
        {
            copied.CommandText = """
                SELECT old_key.status || ':' || old_key.version::text || ':'
                           || old_key.revoke_reason,
                       new_key.status || ':' || new_key.version::text,
                       new_key.user_id = old_key.user_id,
                       new_key.group_id = old_key.group_id,
                       new_key.name = old_key.name,
                       new_key.expires_at = old_key.expires_at,
                       new_key.ip_acl = old_key.ip_acl,
                       new_key.key_prefix,
                       encode(new_key.secret_hash, 'hex'),
                       new_key.pepper_version,
                       (SELECT count(*)
                        FROM public.api_keys AS stale_new
                        WHERE stale_new.id =
                            '01910000-0000-7000-8000-000000000829')
                FROM public.api_keys AS old_key
                JOIN public.api_keys AS new_key
                  ON new_key.id = '01910000-0000-7000-8000-000000000831'
                WHERE old_key.id = '01910000-0000-7000-8000-000000000830';
                """;
            using NpgsqlDataReader reader = await copied
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            Assert.Equal("revoked:3:M1-E5 routine rotation", reader.GetString(0));
            Assert.Equal("active:1", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
            Assert.True(reader.GetBoolean(4));
            Assert.True(reader.GetBoolean(5));
            Assert.True(reader.GetBoolean(6));
            Assert.Equal("sk-pool-Rotate02", reader.GetString(7));
            Assert.Equal(string.Concat(Enumerable.Repeat("32", 32)), reader.GetString(8));
            Assert.Equal((short)2, reader.GetInt16(9));
            Assert.Equal(0L, reader.GetInt64(10));
            Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        }

        AssertM1E5Mutation(
            await ExecuteM1E5MutationAsync(
                connection,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_api_key_create(
                    '01910000-0000-7000-8000-000000000832',
                    '01910000-0000-7000-8000-000000000810',
                    '01910000-0000-7000-8000-000000000812',
                    'M1-E5 Atomic Source', 'sk-pool-Atomic01',
                    decode(repeat('33', 32), 'hex'), 3::smallint,
                    NULL, '[]'::jsonb
                );
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);

        M1E5Rotation duplicateDigest = await ExecuteM1E5RotationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text,
                   old_current_version, new_api_key_id, new_current_version
            FROM public.poolai_api_key_rotate(
                '01910000-0000-7000-8000-000000000832',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 1,
                '01910000-0000-7000-8000-000000000833',
                'sk-pool-Atomic02', decode(repeat('32', 32), 'hex'), 3::smallint,
                'must remain atomic'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Rotation(duplicateDigest, "conflict", false, 1, null, null);

        await AssertM1E5ConcurrentRotationAsync(
            dataSource,
            connection,
            cancellationToken).ConfigureAwait(false);

        using NpgsqlCommand atomicState = connection.CreateCommand();
        atomicState.CommandText = """
            SELECT
                (SELECT status || ':' || version::text
                 FROM public.api_keys
                 WHERE id = '01910000-0000-7000-8000-000000000832'),
                (SELECT count(*)
                 FROM public.api_keys
                 WHERE id = '01910000-0000-7000-8000-000000000833');
            """;
        using NpgsqlDataReader atomicReader = await atomicState
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await atomicReader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal("active:1", atomicReader.GetString(0));
        Assert.Equal(0L, atomicReader.GetInt64(1));
        Assert.False(await atomicReader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask AssertM1E5ConcurrentRotationAsync(
        NpgsqlDataSource dataSource,
        NpgsqlConnection setup,
        CancellationToken cancellationToken)
    {
        AssertM1E5Mutation(
            await ExecuteM1E5MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_api_key_create(
                    '01910000-0000-7000-8000-000000000850',
                    '01910000-0000-7000-8000-000000000810',
                    '01910000-0000-7000-8000-000000000812',
                    'M1-E5 Race Key', 'sk-pool-RaceOld1',
                    decode(repeat('51', 32), 'hex'), 1::smallint,
                    clock_timestamp() + interval '2 days',
                    '["192.0.2.0/24"]'::jsonb
                );
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);

        NpgsqlConnection blocker = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable blockerLease = blocker.ConfigureAwait(false);
        NpgsqlTransaction blockerTransaction = await blocker
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable blockerTransactionLease =
            blockerTransaction.ConfigureAwait(false);
        using (NpgsqlCommand lockKey = blocker.CreateCommand())
        {
            lockKey.Transaction = blockerTransaction;
            lockKey.CommandText = """
                SELECT id
                FROM public.api_keys
                WHERE id = '01910000-0000-7000-8000-000000000850'
                FOR UPDATE;
                """;
            _ = await lockKey.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        NpgsqlConnection first = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable firstLease = first.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(first, cancellationToken).ConfigureAwait(false);
        int firstPid = await ReadM1E4BackendPidAsync(first, cancellationToken)
            .ConfigureAwait(false);
        Task<M1E5Rotation> firstTask = ExecuteM1E5RotationAsync(
            first,
            null,
            """
            SELECT disposition, was_changed, before_state::text,
                   old_current_version, new_api_key_id, new_current_version
            FROM public.poolai_api_key_rotate(
                '01910000-0000-7000-8000-000000000850',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 1,
                '01910000-0000-7000-8000-000000000851',
                'sk-pool-RaceNew1', decode(repeat('52', 32), 'hex'), 2::smallint,
                'race winner one'
            );
            """,
            cancellationToken).AsTask();

        NpgsqlConnection second = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable secondLease = second.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(second, cancellationToken).ConfigureAwait(false);
        int secondPid = await ReadM1E4BackendPidAsync(second, cancellationToken)
            .ConfigureAwait(false);
        Task<M1E5Rotation> secondTask = ExecuteM1E5RotationAsync(
            second,
            null,
            """
            SELECT disposition, was_changed, before_state::text,
                   old_current_version, new_api_key_id, new_current_version
            FROM public.poolai_api_key_rotate(
                '01910000-0000-7000-8000-000000000850',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 1,
                '01910000-0000-7000-8000-000000000852',
                'sk-pool-RaceNew2', decode(repeat('53', 32), 'hex'), 2::smallint,
                'race winner two'
            );
            """,
            cancellationToken).AsTask();

        Assert.True(
            await WaitForM1E4LockWaitAsync(dataSource, firstPid, cancellationToken)
                .ConfigureAwait(false),
            "First rotate did not reach the shared old-Key row-lock boundary.");
        Assert.True(
            await WaitForM1E4LockWaitAsync(dataSource, secondPid, cancellationToken)
                .ConfigureAwait(false),
            "Second rotate did not reach the shared old-Key row-lock boundary.");
        await blockerTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        M1E5Rotation[] results = await Task
            .WhenAll(firstTask, secondTask)
            .ConfigureAwait(false);
        Assert.Equal(
            1,
            results.Count(result => string.Equals(
                result.Disposition,
                "rotated",
                StringComparison.Ordinal)));
        Assert.Equal(
            1,
            results.Count(result => string.Equals(
                result.Disposition,
                "api_key_revoked",
                StringComparison.Ordinal)));
        M1E5Rotation winner = results.Single(result => string.Equals(
            result.Disposition,
            "rotated",
            StringComparison.Ordinal));
        M1E5Rotation loser = results.Single(result => string.Equals(
            result.Disposition,
            "api_key_revoked",
            StringComparison.Ordinal));
        AssertM1E5Rotation(winner, "rotated", true, 2, winner.NewApiKeyId, 1);
        Assert.NotNull(winner.NewApiKeyId);
        Assert.True(
            winner.NewApiKeyId == new Guid("01910000-0000-7000-8000-000000000851")
            || winner.NewApiKeyId == new Guid("01910000-0000-7000-8000-000000000852"));
        AssertM1E5BeforeState(winner.BeforeState, "status", "active");
        AssertM1E5Rotation(loser, "api_key_revoked", false, 2, null, null);
        AssertM1E5BeforeState(loser.BeforeState, "status", "revoked");

        string expectedReason = winner.NewApiKeyId ==
            new Guid("01910000-0000-7000-8000-000000000851")
                ? "race winner one"
                : "race winner two";
        using NpgsqlCommand persisted = setup.CreateCommand();
        persisted.CommandText = """
            SELECT old_key.status || ':' || old_key.version::text || ':'
                       || old_key.revoke_reason,
                   count(new_key.id),
                   count(new_key.id) FILTER (WHERE new_key.status = 'active')
            FROM public.api_keys AS old_key
            LEFT JOIN public.api_keys AS new_key
              ON new_key.id IN (
                  '01910000-0000-7000-8000-000000000851',
                  '01910000-0000-7000-8000-000000000852'
              )
            WHERE old_key.id = '01910000-0000-7000-8000-000000000850'
            GROUP BY old_key.status, old_key.version, old_key.revoke_reason;
            """;
        using NpgsqlDataReader reader = await persisted
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal($"revoked:2:{expectedReason}", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask AssertM1E5LockWaitClockAsync(
        NpgsqlDataSource dataSource,
        NpgsqlConnection setup,
        CancellationToken cancellationToken)
    {
        AssertM1E5Mutation(
            await ExecuteM1E5MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_api_key_create(
                    '01910000-0000-7000-8000-000000000840',
                    '01910000-0000-7000-8000-000000000810',
                    '01910000-0000-7000-8000-000000000812',
                    'M1-E5 Clock Key', 'sk-pool-Clock001',
                    decode(repeat('41', 32), 'hex'), 1::smallint,
                    clock_timestamp() + interval '5 seconds', '[]'::jsonb
                );
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);
        AssertM1E5Mutation(
            await ExecuteM1E5MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_api_key_update(
                    '01910000-0000-7000-8000-000000000840',
                    '01910000-0000-7000-8000-000000000810',
                    '01910000-0000-7000-8000-000000000812', 1, 'active',
                    false, NULL, true, 'disabled', false, NULL, false, NULL
                );
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
        using (NpgsqlCommand lockKey = blocker.CreateCommand())
        {
            lockKey.Transaction = blockerTransaction;
            lockKey.CommandText = """
                SELECT id
                FROM public.api_keys
                WHERE id = '01910000-0000-7000-8000-000000000840'
                FOR UPDATE;
                """;
            _ = await lockKey.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        using (NpgsqlCommand beforeExpiry = blocker.CreateCommand())
        {
            beforeExpiry.Transaction = blockerTransaction;
            beforeExpiry.CommandText = """
                SELECT clock_timestamp() < expires_at
                FROM public.api_keys
                WHERE id = '01910000-0000-7000-8000-000000000840';
                """;
            Assert.True(Assert.IsType<bool>(await beforeExpiry
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
        Task<M1E5Mutation> enableTask = ExecuteM1E5MutationAsync(
            waiter,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000840',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 2, 'disabled',
                false, NULL, true, 'active', false, NULL, false, NULL
            );
            """,
            cancellationToken).AsTask();

        NpgsqlConnection rotateWaiter = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable rotateWaiterLease =
            rotateWaiter.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(rotateWaiter, cancellationToken).ConfigureAwait(false);
        int rotateWaiterPid = await ReadM1E4BackendPidAsync(
            rotateWaiter,
            cancellationToken).ConfigureAwait(false);
        Task<M1E5Rotation> rotateTask = ExecuteM1E5RotationAsync(
            rotateWaiter,
            null,
            """
            SELECT disposition, was_changed, before_state::text,
                   old_current_version, new_api_key_id, new_current_version
            FROM public.poolai_api_key_rotate(
                '01910000-0000-7000-8000-000000000840',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 2,
                '01910000-0000-7000-8000-000000000841',
                'sk-pool-Clock002', decode(repeat('42', 32), 'hex'), 2::smallint,
                'rotate after lock wait'
            );
            """,
            cancellationToken).AsTask();
        Assert.True(
            await WaitForM1E4LockWaitAsync(dataSource, waiterPid, cancellationToken)
                .ConfigureAwait(false),
            "API Key enable did not reach the row-lock wait boundary.");
        Assert.True(
            await WaitForM1E4LockWaitAsync(dataSource, rotateWaiterPid, cancellationToken)
                .ConfigureAwait(false),
            "API Key rotate did not reach the row-lock wait boundary.");
        await WaitForM1E5ApiKeyExpiryAsync(
            setup,
            new Guid("01910000-0000-7000-8000-000000000840"),
            cancellationToken).ConfigureAwait(false);
        await blockerTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        M1E5Mutation afterExpiry = await enableTask.ConfigureAwait(false);
        AssertM1E5Mutation(afterExpiry, "resource_conflict", false, 2);
        AssertM1E5BeforeState(afterExpiry, "status", "disabled");
        AssertM1E5BeforeState(afterExpiry, "effective_status", "disabled");
        M1E5Rotation rotateAfterExpiry = await rotateTask.ConfigureAwait(false);
        AssertM1E5Rotation(rotateAfterExpiry, "resource_conflict", false, 2, null, null);
        AssertM1E5BeforeState(rotateAfterExpiry.BeforeState, "status", "disabled");
        AssertM1E5BeforeState(
            rotateAfterExpiry.BeforeState,
            "effective_status",
            "disabled");

        using NpgsqlCommand persisted = setup.CreateCommand();
        persisted.CommandText = """
            SELECT
                (SELECT status || ':' || version::text
                 FROM public.api_keys
                 WHERE id = '01910000-0000-7000-8000-000000000840'),
                (SELECT count(*)
                 FROM public.api_keys
                 WHERE id = '01910000-0000-7000-8000-000000000841');
            """;
        using NpgsqlDataReader persistedReader = await persisted
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await persistedReader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal("disabled:2", persistedReader.GetString(0));
        Assert.Equal(0L, persistedReader.GetInt64(1));
        Assert.False(await persistedReader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask AssertM1E5ActiveExpiryDriftAsync(
        NpgsqlDataSource dataSource,
        NpgsqlConnection setup,
        CancellationToken cancellationToken)
    {
        AssertM1E5Mutation(
            await ExecuteM1E5MutationAsync(
                setup,
                null,
                """
                SELECT disposition, was_changed, before_state::text, current_version
                FROM public.poolai_api_key_create(
                    '01910000-0000-7000-8000-000000000842',
                    '01910000-0000-7000-8000-000000000810',
                    '01910000-0000-7000-8000-000000000812',
                    'M1-E5 Lifecycle Drift', 'sk-pool-Drift001',
                    decode(repeat('43', 32), 'hex'), 1::smallint,
                    clock_timestamp() + interval '5 seconds', '[]'::jsonb
                );
                """,
                cancellationToken).ConfigureAwait(false),
            "created",
            true,
            1);

        NpgsqlConnection blocker = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable blockerLease =
            blocker.ConfigureAwait(false);
        NpgsqlTransaction blockerTransaction = await blocker
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable blockerTransactionLease =
            blockerTransaction.ConfigureAwait(false);
        using (NpgsqlCommand lockKey = blocker.CreateCommand())
        {
            lockKey.Transaction = blockerTransaction;
            lockKey.CommandText = """
                SELECT id
                FROM public.api_keys
                WHERE id = '01910000-0000-7000-8000-000000000842'
                FOR UPDATE;
                """;
            _ = await lockKey.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        NpgsqlConnection waiter = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable waiterLease = waiter.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(waiter, cancellationToken).ConfigureAwait(false);
        int waiterPid = await ReadM1E4BackendPidAsync(waiter, cancellationToken)
            .ConfigureAwait(false);
        Task<M1E5Mutation> restoreTask = ExecuteM1E5MutationAsync(
            waiter,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000842',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 1, 'active',
                false, NULL, false, NULL, true, NULL, false, NULL
            );
            """,
            cancellationToken).AsTask();
        Assert.True(
            await WaitForM1E4LockWaitAsync(dataSource, waiterPid, cancellationToken)
                .ConfigureAwait(false),
            "Active API Key update did not reach the row-lock wait boundary.");
        await WaitForM1E5ApiKeyExpiryAsync(
            setup,
            new Guid("01910000-0000-7000-8000-000000000842"),
            cancellationToken).ConfigureAwait(false);
        await blockerTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        M1E5Mutation drifted = await restoreTask.ConfigureAwait(false);
        AssertM1E5Mutation(drifted, "resource_conflict", false, 1);
        AssertM1E5BeforeState(drifted, "status", "active");
        AssertM1E5BeforeState(drifted, "effective_status", "expired");

        using (NpgsqlCommand unchanged = setup.CreateCommand())
        {
            unchanged.CommandText = """
                SELECT status || ':' || version::text,
                       expires_at IS NOT NULL,
                       clock_timestamp() >= expires_at
                FROM public.api_keys
                WHERE id = '01910000-0000-7000-8000-000000000842';
                """;
            using NpgsqlDataReader reader = await unchanged
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            Assert.Equal("active:1", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        }

        M1E5Mutation afterReloadAndGate = await ExecuteM1E5MutationAsync(
            setup,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000842',
                '01910000-0000-7000-8000-000000000810',
                '01910000-0000-7000-8000-000000000812', 1, 'expired',
                false, NULL, false, NULL, true, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(afterReloadAndGate, "updated", true, 2);
        AssertM1E5BeforeState(afterReloadAndGate, "effective_status", "expired");

        using NpgsqlCommand restored = setup.CreateCommand();
        restored.CommandText = """
            SELECT status || ':' || version::text, expires_at IS NULL
            FROM public.api_keys
            WHERE id = '01910000-0000-7000-8000-000000000842';
            """;
        using NpgsqlDataReader restoredReader = await restored
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await restoredReader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal("active:2", restoredReader.GetString(0));
        Assert.True(restoredReader.GetBoolean(1));
        Assert.False(await restoredReader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<M1E5Mutation> ExecuteM1E5MutationAsync(
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
        M1E5Mutation mutation = new(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return mutation;
    }

    private static async ValueTask<M1E5Rotation> ExecuteM1E5RotationAsync(
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
        M1E5Rotation rotation = new(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return rotation;
    }

    private static void AssertM1E5Mutation(
        M1E5Mutation mutation,
        string disposition,
        bool wasChanged,
        long? currentVersion)
    {
        Assert.Equal(disposition, mutation.Disposition);
        Assert.Equal(wasChanged, mutation.WasChanged);
        Assert.Equal(currentVersion, mutation.CurrentVersion);
    }

    private static void AssertM1E5Rotation(
        M1E5Rotation rotation,
        string disposition,
        bool wasChanged,
        long? oldCurrentVersion,
        Guid? newApiKeyId,
        long? newCurrentVersion)
    {
        Assert.Equal(disposition, rotation.Disposition);
        Assert.Equal(wasChanged, rotation.WasChanged);
        Assert.Equal(oldCurrentVersion, rotation.OldCurrentVersion);
        Assert.Equal(newApiKeyId, rotation.NewApiKeyId);
        Assert.Equal(newCurrentVersion, rotation.NewCurrentVersion);
    }

    private static void AssertM1E5BeforeState(
        M1E5Mutation mutation,
        string propertyName,
        string expectedValue) =>
        AssertM1E5BeforeState(mutation.BeforeState, propertyName, expectedValue);

    private static void AssertM1E5BeforeState(
        string? beforeState,
        string propertyName,
        string expectedValue)
    {
        Assert.NotNull(beforeState);
        using JsonDocument before = JsonDocument.Parse(beforeState!);
        Assert.Equal(expectedValue, before.RootElement.GetProperty(propertyName).GetString());
        Assert.False(before.RootElement.TryGetProperty("secret_hash", out _));
        Assert.False(before.RootElement.TryGetProperty("pepper_version", out _));
    }

    private static async ValueTask WaitForM1E5ApiKeyExpiryAsync(
        NpgsqlConnection connection,
        Guid apiKeyId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 480; attempt++)
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT clock_timestamp() >= expires_at
                FROM public.api_keys
                WHERE id = $1;
                """;
            command.Parameters.AddWithValue(apiKeyId);
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

        Assert.Fail("The API Key did not reach its PostgreSQL expiry boundary.");
    }

    private sealed record M1E5Mutation(
        string Disposition,
        bool WasChanged,
        string? BeforeState,
        long? CurrentVersion);

    private sealed record M1E5Rotation(
        string Disposition,
        bool WasChanged,
        string? BeforeState,
        long? OldCurrentVersion,
        Guid? NewApiKeyId,
        long? NewCurrentVersion);
}
#pragma warning restore MA0051
