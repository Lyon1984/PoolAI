#pragma warning disable MA0051 // The M2-E1 database ABI and role matrix stay together.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private static readonly Guid M2E1AccountId = Guid.Parse(
        "01910000-0000-7000-8000-000000001001");

    private static async ValueTask AssertM2E1AccountCredentialPersistenceAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertM2E1DirectWritesDeniedAsync(
            connectionString,
            cancellationToken).ConfigureAwait(false);

        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        await AssertM2E1SecurityBoundaryAsync(
            dataSource,
            cancellationToken).ConfigureAwait(false);
        await AssertM2E1EnvelopeStorageGuardAsync(
            dataSource,
            cancellationToken).ConfigureAwait(false);
        await AssertM2E1MutationAndRewrapSemanticsAsync(
            dataSource,
            cancellationToken).ConfigureAwait(false);
        await AssertM2E1ConcurrentReplacementWinsAsync(
            dataSource,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM2E1DirectWritesDeniedAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            INSERT INTO public.accounts (
                id, provider, name, auth_type, upstream_base_url,
                credential_envelope, credential_prefix
            ) VALUES (
                '01910000-0000-7000-8000-000000001090',
                'openai', 'M2-E1 bypass', 'api_key',
                'https://example.test/v1', '{}'::jsonb, 'bypass'
            );
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            UPDATE public.accounts
            SET credential_envelope = credential_envelope
            WHERE false;
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_worker;
            UPDATE public.accounts
            SET credential_envelope = credential_envelope,
                credential_revision = credential_revision
            WHERE false;
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_runtime_owner;
            SELECT credential_envelope
            FROM public.accounts
            WHERE false;
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            SELECT *
            FROM public.poolai_supply_select_account_credential_rewrap_batch(
                NULL, 1);
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_worker;
            SELECT *
            FROM public.poolai_supply_create_account(
                '01910000-0000-7000-8000-000000001091',
                'openai', 'forbidden', 'https://example.test/v1',
                '{}'::jsonb, 'forbidden', NULL, 1, 0, 100
            );
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            SELECT public.poolai_secret_envelope_v1_is_structurally_valid(
                '{}'::jsonb);
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_worker;
            SELECT public.poolai_secret_envelope_v1_is_structurally_valid(
                '{}'::jsonb);
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM2E1SecurityBoundaryAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand functions = dataSource.CreateCommand("""
            WITH expected(signature, grantee, security_definer) AS (
                VALUES
                    (
                        'public.poolai_supply_create_account(uuid,text,text,text,jsonb,text,text,integer,integer,integer)',
                        'poolai_api',
                        true
                    ),
                    (
                        'public.poolai_supply_replace_account_credential(uuid,bigint,jsonb,text,text)',
                        'poolai_api',
                        true
                    ),
                    (
                        'public.poolai_supply_select_account_credential_rewrap_batch(uuid,integer)',
                        'poolai_worker',
                        false
                    ),
                    (
                        'public.poolai_supply_rewrap_account_credential(uuid,bigint,jsonb)',
                        'poolai_worker',
                        true
                    )
            ), resolved AS (
                SELECT
                    pg_catalog.to_regprocedure(expected.signature) AS oid,
                    expected.grantee,
                    expected.security_definer
                FROM expected
            )
            SELECT count(*)
            FROM resolved
            JOIN pg_catalog.pg_proc AS function ON function.oid = resolved.oid
            JOIN pg_catalog.pg_roles AS owner ON owner.oid = function.proowner
            WHERE resolved.oid IS NOT NULL
              AND function.prosecdef = resolved.security_definer
              AND owner.rolname = 'poolai_runtime_owner'
              AND NOT owner.rolcanlogin
              AND function.proconfig @> ARRAY[
                  'search_path=pg_catalog, public, pg_temp'
              ]::text[]
              AND pg_catalog.has_function_privilege(
                  resolved.grantee, function.oid, 'EXECUTE')
              AND NOT pg_catalog.has_function_privilege(
                  CASE resolved.grantee
                      WHEN 'poolai_api' THEN 'poolai_worker'
                      ELSE 'poolai_api'
                  END,
                  function.oid,
                  'EXECUTE')
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.aclexplode(COALESCE(
                      function.proacl,
                      pg_catalog.acldefault('f', function.proowner))) AS acl
                  WHERE acl.privilege_type = 'EXECUTE'
                    AND (
                        acl.grantor <> function.proowner
                        OR acl.is_grantable
                        OR acl.grantee NOT IN (
                            function.proowner,
                            (
                                SELECT role.oid
                                FROM pg_catalog.pg_roles AS role
                                WHERE role.rolname = resolved.grantee
                            )
                        )
                    )
              );
            """);
        Assert.Equal(
            4L,
            Assert.IsType<long>(await functions
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));

        using NpgsqlCommand boundary = dataSource.CreateCommand("""
            WITH invalid_role AS (
                SELECT 1
                FROM pg_catalog.pg_roles AS role
                WHERE role.rolname IN (
                    'poolai_runtime_owner', 'poolai_api', 'poolai_worker')
                  AND (
                      role.rolsuper
                      OR role.rolcreaterole
                      OR role.rolcreatedb
                      OR role.rolreplication
                      OR role.rolbypassrls
                      OR (
                          role.rolname = 'poolai_runtime_owner'
                          AND role.rolcanlogin
                      )
                  )
            ), invalid_membership AS (
                SELECT 1
                WHERE pg_catalog.pg_has_role(
                          'poolai_api', 'poolai_runtime_owner', 'MEMBER')
                   OR pg_catalog.pg_has_role(
                          'poolai_worker', 'poolai_runtime_owner', 'MEMBER')
            ), invalid_column AS (
                SELECT 1
                WHERE pg_catalog.has_column_privilege(
                          'poolai_runtime_owner',
                          'public.accounts',
                          'credential_envelope',
                          'SELECT')
                   OR NOT pg_catalog.has_column_privilege(
                          'poolai_worker',
                          'public.accounts',
                          'credential_envelope',
                          'SELECT')
                   OR NOT pg_catalog.has_column_privilege(
                          'poolai_worker',
                          'public.accounts',
                          'credential_revision',
                          'SELECT')
                   OR pg_catalog.has_column_privilege(
                          'poolai_worker',
                          'public.accounts',
                          'credential_envelope',
                          'UPDATE')
                   OR pg_catalog.has_column_privilege(
                          'poolai_worker',
                          'public.accounts',
                          'credential_revision',
                          'UPDATE')
                   OR pg_catalog.has_any_column_privilege(
                          'poolai_api', 'public.accounts', 'INSERT')
                   OR pg_catalog.has_any_column_privilege(
                          'poolai_api', 'public.accounts', 'UPDATE')
            )
            SELECT (SELECT count(*) FROM invalid_role)
                 + (SELECT count(*) FROM invalid_membership)
                 + (SELECT count(*) FROM invalid_column);
            """);
        Assert.Equal(
            0L,
            Assert.IsType<long>(await boundary
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));

        using NpgsqlCommand trigger = dataSource.CreateCommand("""
            SELECT count(*)
            FROM pg_catalog.pg_trigger AS trigger
            JOIN pg_catalog.pg_proc AS function
              ON function.oid = trigger.tgfoid
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = function.proowner
            WHERE trigger.tgrelid = 'public.accounts'::regclass
              AND trigger.tgname = 'tr_accounts_credential_revision'
              AND NOT trigger.tgisinternal
              AND function.proname = 'poolai_guard_account_credential_revision'
              AND function.prosecdef
              AND owner.rolname = 'poolai_runtime_owner'
              AND NOT owner.rolcanlogin
              AND function.proconfig @> ARRAY[
                  'search_path=pg_catalog, public, pg_temp'
              ]::text[];
            """);
        Assert.Equal(
            1L,
            Assert.IsType<long>(await trigger
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));

        using NpgsqlCommand supplyGuards = dataSource.CreateCommand("""
            SELECT count(*)
            FROM pg_catalog.pg_proc AS function
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = function.proowner
            WHERE function.oid IN (
                      'public.poolai_guard_group_supply_configuration()'::regprocedure,
                      'public.poolai_validate_group_account_binding()'::regprocedure
                  )
              AND function.prosecdef
              AND owner.rolname = 'poolai_runtime_owner'
              AND NOT owner.rolcanlogin
              AND function.proconfig @> ARRAY[
                  'search_path=pg_catalog, public, pg_temp'
              ]::text[]
              AND NOT pg_catalog.has_function_privilege(
                  'poolai_api', function.oid, 'EXECUTE')
              AND NOT pg_catalog.has_function_privilege(
                  'poolai_worker', function.oid, 'EXECUTE');
            """);
        Assert.Equal(
            2L,
            Assert.IsType<long>(await supplyGuards
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask AssertM2E1EnvelopeStorageGuardAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        string envelope = M2E1Envelope("m2-e1-k1", "AQ", "AQ");
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT
                public.poolai_secret_envelope_v1_is_structurally_valid(
                    $1::jsonb)
                AND NOT public.poolai_secret_envelope_v1_is_structurally_valid(
                    $1::jsonb || '{"extra":true}'::jsonb)
                AND NOT public.poolai_secret_envelope_v1_is_structurally_valid(
                    pg_catalog.jsonb_set(
                        $1::jsonb, '{alg}', '"unknown"'::jsonb))
                AND NOT public.poolai_secret_envelope_v1_is_structurally_valid(
                    pg_catalog.jsonb_set(
                        $1::jsonb, '{kid}', '"   "'::jsonb))
                AND NOT public.poolai_secret_envelope_v1_is_structurally_valid(
                    pg_catalog.jsonb_set(
                        $1::jsonb, '{wrapped_dek}', '"AQ"'::jsonb))
                AND NOT public.poolai_secret_envelope_v1_is_structurally_valid(
                    pg_catalog.jsonb_set(
                        $1::jsonb, '{ciphertext}', '"AQ=="'::jsonb));
            """);
        command.Parameters.AddWithValue(envelope);
        Assert.True(Assert.IsType<bool>(
            await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask AssertM2E1MutationAndRewrapSemanticsAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        string original = M2E1Envelope("m2-e1-k1", "AQ", "AQ");
        string rewrapped = M2E1Envelope("m2-e1-k2", "AQ", "Ag");
        string wrongContent = M2E1Envelope("m2-e1-k2", "Ag", "Aw");
        string replacement = M2E1Envelope("m2-e1-k2", "Aw", "BA");

        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await SetM2E1RoleAsync(
            connection,
            "poolai_api",
            cancellationToken).ConfigureAwait(false);
        try
        {
            using NpgsqlCommand nullProvider = connection.CreateCommand();
            nullProvider.CommandText = """
                SELECT disposition
                FROM public.poolai_supply_create_account(
                    '01910000-0000-7000-8000-000000001099',
                    NULL, 'invalid', 'https://example.test/v1',
                    $1::jsonb, 'prefix', NULL, 1, 0, 100
                );
                """;
            nullProvider.Parameters.AddWithValue(original);
            Assert.Equal(
                "validation_failed",
                Assert.IsType<string>(await nullProvider
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));

            M2E1Mutation created = await ExecuteM2E1MutationAsync(
                connection,
                """
                SELECT disposition, current_version, current_credential_revision
                FROM public.poolai_supply_create_account(
                    $1, 'openai', 'M2-E1 Account',
                    'https://example.test/v1', $2::jsonb,
                    'm2-e1-prefix', 'm2-e1-hint', 7, 5, 100
                );
                """,
                [M2E1AccountId, original],
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(new M2E1Mutation("created", 1, 1), created);
        }
        finally
        {
            await ResetM2E1RoleAsync(connection, cancellationToken)
                .ConfigureAwait(false);
        }

        await SetM2E1RoleAsync(
            connection,
            "poolai_worker",
            cancellationToken).ConfigureAwait(false);
        try
        {
            M2E1Mutation changed = await ExecuteM2E1MutationAsync(
                connection,
                """
                SELECT disposition, NULL::bigint, current_credential_revision
                FROM public.poolai_supply_rewrap_account_credential(
                    $1, 1, $2::jsonb);
                """,
                [M2E1AccountId, rewrapped],
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(new M2E1Mutation("rewrapped", null, 2), changed);

            M2E1Mutation contentRejected = await ExecuteM2E1MutationAsync(
                connection,
                """
                SELECT disposition, NULL::bigint, current_credential_revision
                FROM public.poolai_supply_rewrap_account_credential(
                    $1, 2, $2::jsonb);
                """,
                [M2E1AccountId, wrongContent],
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(
                new M2E1Mutation("content_mismatch", null, 2),
                contentRejected);

            M2E1Mutation stale = await ExecuteM2E1MutationAsync(
                connection,
                """
                SELECT disposition, NULL::bigint, current_credential_revision
                FROM public.poolai_supply_rewrap_account_credential(
                    $1, 1, $2::jsonb);
                """,
                [M2E1AccountId, rewrapped],
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(
                new M2E1Mutation("credential_revision_conflict", null, 2),
                stale);
        }
        finally
        {
            await ResetM2E1RoleAsync(connection, cancellationToken)
                .ConfigureAwait(false);
        }

        using (NpgsqlCommand preserved = dataSource.CreateCommand("""
            SELECT version = 1
               AND credential_revision = 2
               AND updated_at = created_at
               AND credential_prefix = 'm2-e1-prefix'
               AND credential_hint = 'm2-e1-hint'
               AND status = 'disabled'
               AND upstream_rate_limited_until IS NULL
               AND last_health_at IS NULL
               AND last_health_status = 'unknown'
               AND credential_envelope ->> 'kid' = 'm2-e1-k2'
               AND credential_envelope ->> 'ciphertext' = 'AQ'
               AND credential_envelope ->> 'nonce' = 'AQIDBAUGBwgJCgsM'
               AND credential_envelope ->> 'tag' =
                   'AAECAwQFBgcICQoLDA0ODw'
            FROM public.accounts
            WHERE id = $1;
            """))
        {
            preserved.Parameters.AddWithValue(M2E1AccountId);
            Assert.True(Assert.IsType<bool>(
                await preserved.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        await SetM2E1RoleAsync(
            connection,
            "poolai_api",
            cancellationToken).ConfigureAwait(false);
        try
        {
            M2E1Mutation replaced = await ExecuteM2E1MutationAsync(
                connection,
                """
                SELECT disposition, current_version, current_credential_revision
                FROM public.poolai_supply_replace_account_credential(
                    $1, 1, $2::jsonb, 'replacement-prefix',
                    'replacement-hint');
                """,
                [M2E1AccountId, replacement],
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(new M2E1Mutation("replaced", 2, 3), replaced);
        }
        finally
        {
            await ResetM2E1RoleAsync(connection, cancellationToken)
                .ConfigureAwait(false);
        }

        using (NpgsqlCommand retire = dataSource.CreateCommand("""
            UPDATE public.accounts
            SET status = 'retired',
                deleted_at = clock_timestamp(),
                version = version + 1,
                updated_at = clock_timestamp()
            WHERE id = $1;
            """))
        {
            retire.Parameters.AddWithValue(M2E1AccountId);
            Assert.Equal(
                1,
                await retire.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        await SetM2E1RoleAsync(
            connection,
            "poolai_worker",
            cancellationToken).ConfigureAwait(false);
        try
        {
            using NpgsqlCommand selector = connection.CreateCommand();
            selector.CommandText = """
                SELECT count(*)
                FROM public.poolai_supply_select_account_credential_rewrap_batch(
                    NULL, 1000)
                WHERE account_id = $1
                  AND revision = 3
                  AND envelope ->> 'ciphertext' = 'Aw';
                """;
            selector.Parameters.AddWithValue(M2E1AccountId);
            Assert.Equal(
                1L,
                Assert.IsType<long>(await selector
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }
        finally
        {
            await ResetM2E1RoleAsync(connection, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<M2E1Mutation> ExecuteM2E1MutationAsync(
        NpgsqlConnection connection,
        string sql,
        object[] parameters,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (object parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M2E1Mutation mutation = new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return mutation;
    }

    private static async ValueTask AssertM2E1ConcurrentReplacementWinsAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await AssertM2E1ConcurrentOrderAsync(
            dataSource,
            Guid.Parse("01910000-0000-7000-8000-000000001010"),
            replacementFirst: true,
            cancellationToken).ConfigureAwait(false);
        await AssertM2E1ConcurrentOrderAsync(
            dataSource,
            Guid.Parse("01910000-0000-7000-8000-000000001011"),
            replacementFirst: false,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM2E1ConcurrentOrderAsync(
        NpgsqlDataSource dataSource,
        Guid accountId,
        bool replacementFirst,
        CancellationToken cancellationToken)
    {
        string original = M2E1Envelope("m2-e1-k1", "AQ", "AQ");
        string rewrapped = M2E1Envelope("m2-e1-k2", "AQ", "Ag");
        string replacement = M2E1Envelope("m2-e1-k2", "Aw", "BA");
        await CreateM2E1ConcurrencyAccountAsync(
            dataSource,
            accountId,
            original,
            cancellationToken).ConfigureAwait(false);

        using NpgsqlConnection first = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlConnection second = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        NpgsqlTransaction firstTransaction = await first
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable firstLease =
            firstTransaction.ConfigureAwait(false);
        NpgsqlTransaction secondTransaction = await second
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable secondLease =
            secondTransaction.ConfigureAwait(false);

        int secondBackendPid = await ReadBackendPidAsync(
            second,
            secondTransaction,
            cancellationToken).ConfigureAwait(false);
        if (replacementFirst)
        {
            await SetM2E1LocalRoleAsync(
                first,
                firstTransaction,
                "poolai_api",
                cancellationToken).ConfigureAwait(false);
            await SetM2E1LocalRoleAsync(
                second,
                secondTransaction,
                "poolai_worker",
                cancellationToken).ConfigureAwait(false);
            M2E1Mutation replaced = await ExecuteM2E1MutationAsync(
                first,
                """
                SELECT disposition, current_version, current_credential_revision
                FROM public.poolai_supply_replace_account_credential(
                    $1, 1, $2::jsonb, 'concurrent-replacement',
                    'replacement-first');
                """,
                [accountId, replacement],
                cancellationToken,
                firstTransaction).ConfigureAwait(false);
            Assert.Equal(new M2E1Mutation("replaced", 2, 2), replaced);

            Task<M2E1Mutation> blockedRewrap = ExecuteM2E1MutationAsync(
                second,
                """
                SELECT disposition, NULL::bigint, current_credential_revision
                FROM public.poolai_supply_rewrap_account_credential(
                    $1, 1, $2::jsonb);
                """,
                [accountId, rewrapped],
                cancellationToken,
                secondTransaction).AsTask();
            await WaitForM2E1RowLockAsync(
                dataSource,
                secondBackendPid,
                cancellationToken).ConfigureAwait(false);
            await firstTransaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            Assert.Equal(
                new M2E1Mutation("credential_revision_conflict", null, 2),
                await blockedRewrap.ConfigureAwait(false));
            await secondTransaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await SetM2E1LocalRoleAsync(
                first,
                firstTransaction,
                "poolai_worker",
                cancellationToken).ConfigureAwait(false);
            await SetM2E1LocalRoleAsync(
                second,
                secondTransaction,
                "poolai_api",
                cancellationToken).ConfigureAwait(false);
            M2E1Mutation rewrapResult = await ExecuteM2E1MutationAsync(
                first,
                """
                SELECT disposition, NULL::bigint, current_credential_revision
                FROM public.poolai_supply_rewrap_account_credential(
                    $1, 1, $2::jsonb);
                """,
                [accountId, rewrapped],
                cancellationToken,
                firstTransaction).ConfigureAwait(false);
            Assert.Equal(
                new M2E1Mutation("rewrapped", null, 2),
                rewrapResult);

            Task<M2E1Mutation> blockedReplacement = ExecuteM2E1MutationAsync(
                second,
                """
                SELECT disposition, current_version, current_credential_revision
                FROM public.poolai_supply_replace_account_credential(
                    $1, 1, $2::jsonb, 'concurrent-replacement',
                    'rewrap-first');
                """,
                [accountId, replacement],
                cancellationToken,
                secondTransaction).AsTask();
            await WaitForM2E1RowLockAsync(
                dataSource,
                secondBackendPid,
                cancellationToken).ConfigureAwait(false);
            await firstTransaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            Assert.Equal(
                new M2E1Mutation("replaced", 2, 3),
                await blockedReplacement.ConfigureAwait(false));
            await secondTransaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using NpgsqlCommand final = dataSource.CreateCommand("""
            SELECT version = 2
               AND credential_envelope ->> 'ciphertext' = 'Aw'
               AND credential_prefix = 'concurrent-replacement'
            FROM public.accounts
            WHERE id = $1;
            """);
        final.Parameters.AddWithValue(accountId);
        Assert.True(Assert.IsType<bool>(
            await final.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask CreateM2E1ConcurrencyAccountAsync(
        NpgsqlDataSource dataSource,
        Guid accountId,
        string envelope,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await SetM2E1RoleAsync(
            connection,
            "poolai_api",
            cancellationToken).ConfigureAwait(false);
        try
        {
            M2E1Mutation created = await ExecuteM2E1MutationAsync(
                connection,
                """
                SELECT disposition, current_version, current_credential_revision
                FROM public.poolai_supply_create_account(
                    $1, 'openai', 'M2-E1 Concurrent',
                    'https://example.test/v1', $2::jsonb,
                    'concurrent-original', NULL, 1, 0, 100
                );
                """,
                [accountId, envelope],
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(new M2E1Mutation("created", 1, 1), created);
        }
        finally
        {
            await ResetM2E1RoleAsync(connection, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask SetM2E1LocalRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role,
        CancellationToken cancellationToken)
    {
        string commandText = role switch
        {
            "poolai_api" => "SET LOCAL ROLE poolai_api;",
            "poolai_worker" => "SET LOCAL ROLE poolai_worker;",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadBackendPidAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_catalog.pg_backend_pid();";
        return Assert.IsType<int>(
            await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false));
    }

    private static async ValueTask WaitForM2E1RowLockAsync(
        NpgsqlDataSource dataSource,
        int backendPid,
        CancellationToken cancellationToken)
    {
        for (int probe = 0; probe < 100; probe++)
        {
            using NpgsqlCommand command = dataSource.CreateCommand("""
                SELECT wait_event_type = 'Lock'
                FROM pg_catalog.pg_stat_activity
                WHERE pid = $1;
                """);
            command.Parameters.AddWithValue(backendPid);
            object? waiting = await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (waiting is true)
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(20),
                cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The concurrent Account credential mutation did not wait on the row lock.");
    }

    private static async ValueTask SetM2E1RoleAsync(
        NpgsqlConnection connection,
        string role,
        CancellationToken cancellationToken)
    {
        string commandText = role switch
        {
            "poolai_api" => "SET ROLE poolai_api;",
            "poolai_worker" => "SET ROLE poolai_worker;",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask ResetM2E1RoleAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "RESET ROLE;";
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string M2E1Envelope(
        string keyId,
        string ciphertext,
        string wrappedSeed) => JsonSerializer.Serialize(new
        {
            v = 1,
            alg = "A256GCM+A256GCM-v1",
            kid = keyId,
            wrapped_dek =
                string.Equals(wrappedSeed, "AQ", StringComparison.Ordinal)
                    ? "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                    : "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE",
            wrap_nonce = "AAECAwQFBgcICQoL",
            wrap_tag = "AAECAwQFBgcICQoLDA0ODw",
            ciphertext,
            nonce = "AQIDBAUGBwgJCgsM",
            tag = "AAECAwQFBgcICQoLDA0ODw",
        });

    private sealed record M2E1Mutation(
        string Disposition,
        long? Version,
        long? CredentialRevision);
}
#pragma warning restore MA0051
