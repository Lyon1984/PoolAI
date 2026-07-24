#pragma warning disable MA0051 // Unicode parity cases stay reviewable beside the DB contract.
using System.Runtime.CompilerServices;
using Npgsql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private static async ValueTask AssertM1E5TextValidationPermissionsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        // Governing contract: proposed ADR 0008 and migration 0009 keep the
        // reusable validator private to the NOLOGIN Identity function owner.
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using (NpgsqlCommand validator = dataSource.CreateCommand("""
            SELECT count(*)
            FROM pg_catalog.pg_proc AS function
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = function.proowner
            WHERE function.oid = pg_catalog.to_regprocedure(
                      'public.poolai_api_key_text_is_valid(text,integer)')
              AND NOT function.prosecdef
              AND function.provolatile = 'i'
              AND function.proisstrict
              AND function.proparallel = 's'
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

        using (NpgsqlCommand constraints = dataSource.CreateCommand("""
            SELECT count(*)
            FROM pg_catalog.pg_constraint AS constraint_definition
            WHERE constraint_definition.conrelid = 'public.api_keys'::regclass
              AND constraint_definition.contype = 'c'
              AND (
                  (
                      constraint_definition.conname =
                          'ck_api_keys_m1_e5_name_text'
                      AND pg_catalog.strpos(
                          pg_catalog.pg_get_expr(
                              constraint_definition.conbin,
                              constraint_definition.conrelid),
                          'poolai_api_key_text_is_valid(name, 100)') > 0
                  )
                  OR (
                      constraint_definition.conname =
                          'ck_api_keys_m1_e5_reason_text'
                      AND pg_catalog.strpos(
                          pg_catalog.pg_get_expr(
                              constraint_definition.conbin,
                              constraint_definition.conrelid),
                          'poolai_api_key_text_is_valid(revoke_reason, 500)') > 0
                  )
              );
            """))
        {
            Assert.Equal(
                2L,
                Assert.IsType<long>(await constraints
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            SELECT public.poolai_api_key_text_is_valid('probe', 100);
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_worker;
            SELECT public.poolai_api_key_text_is_valid('probe', 100);
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM1E5TextValidationAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        await AssertM1E5TextValidatorCasesAsync(dataSource, cancellationToken)
            .ConfigureAwait(false);

        NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using (NpgsqlCommand seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO public.users (
                    id, email, normalized_email, display_name,
                    password_hash, security_stamp
                ) VALUES (
                    '01910000-0000-7000-8000-000000000910',
                    'm1e5-text@example.test', 'm1e5-text@example.test',
                    'M1-E5 Text Owner', 'poolai-password-v1:test',
                    '01910000-0000-7000-8000-000000000911'
                );
                INSERT INTO public.groups (id, name)
                VALUES (
                    '01910000-0000-7000-8000-000000000912',
                    'M1-E5 Text Group'
                );
                SET ROLE poolai_api;
                """;
            await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string PreservedName = "\u00A0 API Key 🔑 \u3000";
        M1E5Mutation created = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_create(
                '01910000-0000-7000-8000-000000000920',
                '01910000-0000-7000-8000-000000000910',
                '01910000-0000-7000-8000-000000000912',
                chr(160) || ' API Key 🔑 ' || chr(12288),
                'sk-pool-TextVal01',
                decode(repeat('91', 32), 'hex'), 1::smallint,
                NULL, '[]'::jsonb
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(created, "created", true, 1);
        await AssertM1E5TextValueAsync(
            connection,
            new Guid("01910000-0000-7000-8000-000000000920"),
            PreservedName,
            null,
            1,
            cancellationToken).ConfigureAwait(false);

        M1E5Mutation supplementaryBoundary = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_create(
                '01910000-0000-7000-8000-000000000921',
                '01910000-0000-7000-8000-000000000910',
                '01910000-0000-7000-8000-000000000912',
                repeat(chr(128273), 100),
                'sk-pool-TextVal02',
                decode(repeat('92', 32), 'hex'), 1::smallint,
                NULL, '[]'::jsonb
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(supplementaryBoundary, "created", true, 1);

        using (NpgsqlCommand invalidCreates = connection.CreateCommand())
        {
            invalidCreates.CommandText = """
                SELECT candidate.case_name, result.disposition
                FROM (
                    VALUES
                        (
                            'all-whitespace',
                            '01910000-0000-7000-8000-000000000922'::uuid,
                            chr(160) || chr(5760) || chr(8199) || chr(12288),
                            decode(repeat('93', 32), 'hex')
                        ),
                        (
                            'control',
                            '01910000-0000-7000-8000-000000000923'::uuid,
                            'bad' || chr(31) || 'name',
                            decode(repeat('94', 32), 'hex')
                        ),
                        (
                            'too-long-supplementary',
                            '01910000-0000-7000-8000-000000000924'::uuid,
                            repeat(chr(128273), 101),
                            decode(repeat('95', 32), 'hex')
                        )
                ) AS candidate(case_name, key_id, key_name, digest)
                CROSS JOIN LATERAL public.poolai_api_key_create(
                    candidate.key_id,
                    '01910000-0000-7000-8000-000000000910',
                    '01910000-0000-7000-8000-000000000912',
                    candidate.key_name,
                    'sk-pool-TextBad01',
                    candidate.digest,
                    1::smallint,
                    NULL,
                    '[]'::jsonb
                ) AS result
                ORDER BY candidate.case_name;
                """;
            using NpgsqlDataReader reader = await invalidCreates
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            int cases = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cases++;
                Assert.Equal("validation_failed", reader.GetString(1));
            }

            Assert.Equal(3, cases);
        }

        const string UpdatedName = "\u3000 Updated 🔑 \u00A0";
        M1E5Mutation updated = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000920',
                '01910000-0000-7000-8000-000000000910',
                '01910000-0000-7000-8000-000000000912',
                1, 'active',
                true, chr(12288) || ' Updated 🔑 ' || chr(160),
                false, NULL, false, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(updated, "updated", true, 2);
        await AssertM1E5TextValueAsync(
            connection,
            new Guid("01910000-0000-7000-8000-000000000920"),
            UpdatedName,
            null,
            2,
            cancellationToken).ConfigureAwait(false);

        M1E5Mutation invalidUpdate = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_update(
                '01910000-0000-7000-8000-000000000920',
                '01910000-0000-7000-8000-000000000910',
                '01910000-0000-7000-8000-000000000912',
                2, 'active',
                true, 'bad' || chr(159) || 'name',
                false, NULL, false, NULL, false, NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(invalidUpdate, "validation_failed", false, null);
        await AssertM1E5TextValueAsync(
            connection,
            new Guid("01910000-0000-7000-8000-000000000920"),
            UpdatedName,
            null,
            2,
            cancellationToken).ConfigureAwait(false);

        M1E5Mutation invalidRevoke = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_revoke(
                '01910000-0000-7000-8000-000000000920',
                '01910000-0000-7000-8000-000000000910',
                2, chr(160) || chr(8199) || chr(12288)
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(invalidRevoke, "validation_failed", false, null);

        const string PreservedRevokeReason = "\u00A0 retire 🔑 \u3000";
        M1E5Mutation revoked = await ExecuteM1E5MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_api_key_revoke(
                '01910000-0000-7000-8000-000000000920',
                '01910000-0000-7000-8000-000000000910',
                2, chr(160) || ' retire 🔑 ' || chr(12288)
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Mutation(revoked, "revoked", true, 3);
        await AssertM1E5TextValueAsync(
            connection,
            new Guid("01910000-0000-7000-8000-000000000920"),
            UpdatedName,
            PreservedRevokeReason,
            3,
            cancellationToken).ConfigureAwait(false);

        M1E5Rotation invalidRotate = await ExecuteM1E5RotationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text,
                   old_current_version, new_api_key_id, new_current_version
            FROM public.poolai_api_key_rotate(
                '01910000-0000-7000-8000-000000000921',
                '01910000-0000-7000-8000-000000000910',
                '01910000-0000-7000-8000-000000000912',
                1,
                '01910000-0000-7000-8000-000000000925',
                'sk-pool-TextVal03',
                decode(repeat('96', 32), 'hex'), 1::smallint,
                repeat(chr(128273), 501)
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Rotation(invalidRotate, "validation_failed", false, null, null, null);

        const string PreservedRotateReason = "\u3000 rotate 🔑 \u00A0";
        M1E5Rotation rotated = await ExecuteM1E5RotationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text,
                   old_current_version, new_api_key_id, new_current_version
            FROM public.poolai_api_key_rotate(
                '01910000-0000-7000-8000-000000000921',
                '01910000-0000-7000-8000-000000000910',
                '01910000-0000-7000-8000-000000000912',
                1,
                '01910000-0000-7000-8000-000000000926',
                'sk-pool-TextVal04',
                decode(repeat('97', 32), 'hex'), 1::smallint,
                chr(12288) || ' rotate 🔑 ' || chr(160)
            );
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E5Rotation(
            rotated,
            "rotated",
            true,
            2,
            new Guid("01910000-0000-7000-8000-000000000926"),
            1);
        await AssertM1E5TextValueAsync(
            connection,
            new Guid("01910000-0000-7000-8000-000000000921"),
            string.Concat(Enumerable.Repeat("🔑", 100)),
            PreservedRotateReason,
            2,
            cancellationToken).ConfigureAwait(false);

        using (NpgsqlCommand rejectedRows = connection.CreateCommand())
        {
            rejectedRows.CommandText = """
                SELECT count(*)
                FROM public.api_keys
                WHERE id IN (
                    '01910000-0000-7000-8000-000000000922',
                    '01910000-0000-7000-8000-000000000923',
                    '01910000-0000-7000-8000-000000000924',
                    '01910000-0000-7000-8000-000000000925'
                );
                """;
            Assert.Equal(
                0L,
                Assert.IsType<long>(await rejectedRows
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        await AssertM1E5TextConstraintsRejectBypassAsync(
            dataSource,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM1E5TextValidatorCasesAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand cases = dataSource.CreateCommand("""
            WITH cases(case_name, value, max_code_points, expected) AS (
                VALUES
                    ('name-basic', 'API Key', 100, true),
                    ('name-preserved-whitespace',
                        chr(160) || ' API Key ' || chr(12288), 100, true),
                    ('name-supplementary-boundary',
                        repeat(chr(128273), 100), 100, true),
                    ('name-supplementary-too-long',
                        repeat(chr(128273), 101), 100, false),
                    ('reason-supplementary-boundary',
                        repeat(chr(128273), 500), 500, true),
                    ('reason-supplementary-too-long',
                        repeat(chr(128273), 501), 500, false),
                    ('empty', '', 100, false),
                    ('frozen-whitespace-only',
                        chr(32) || chr(160) || chr(5760) || chr(8199)
                            || chr(8239) || chr(8287) || chr(12288),
                        100, false),
                    ('c0', 'bad' || chr(1), 100, false),
                    ('delete-control', 'bad' || chr(127), 100, false),
                    ('c1', 'bad' || chr(159), 100, false),
                    ('line-separator', 'bad' || chr(8232), 100, false),
                    ('paragraph-separator', 'bad' || chr(8233), 100, false),
                    ('unsupported-bound', 'API Key', 101, false)
            )
            SELECT pg_catalog.string_agg(case_name, ',' ORDER BY case_name)
            FROM cases
            WHERE public.poolai_api_key_text_is_valid(value, max_code_points)
                  IS DISTINCT FROM expected;
            """))
        {
            Assert.Equal(
                DBNull.Value,
                await cases
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        using NpgsqlCommand surrogate = dataSource.CreateCommand(
            "SELECT pg_catalog.chr(55296);");
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => surrogate.ExecuteScalarAsync(cancellationToken)).ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.ProgramLimitExceeded, exception.SqlState);
    }

    private static async ValueTask AssertM1E5TextValueAsync(
        NpgsqlConnection connection,
        Guid apiKeyId,
        string expectedName,
        string? expectedReason,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, revoke_reason, version
            FROM public.api_keys
            WHERE id = $1;
            """;
        command.Parameters.AddWithValue(apiKeyId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal(expectedName, reader.GetString(0));
        if (expectedReason is null)
        {
            Assert.True(reader.IsDBNull(1));
        }
        else
        {
            Assert.Equal(expectedReason, reader.GetString(1));
        }

        Assert.Equal(expectedVersion, reader.GetInt64(2));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask AssertM1E5TextConstraintsRejectBypassAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand invalidName = dataSource.CreateCommand("""
            INSERT INTO public.api_keys (
                id, user_id, group_id, name, key_prefix,
                secret_hash, pepper_version
            ) VALUES (
                '01910000-0000-7000-8000-000000000927',
                '01910000-0000-7000-8000-000000000910',
                '01910000-0000-7000-8000-000000000912',
                chr(160) || chr(12288),
                'sk-pool-TextBad02',
                decode(repeat('98', 32), 'hex'), 1
            );
            """))
        {
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => invalidName.ExecuteNonQueryAsync(cancellationToken)).ConfigureAwait(false);
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("ck_api_keys_m1_e5_name_text", exception.ConstraintName);
        }

        using (NpgsqlCommand invalidReason = dataSource.CreateCommand("""
            UPDATE public.api_keys
            SET revoke_reason = 'bad' || chr(8232) || 'reason'
            WHERE id = '01910000-0000-7000-8000-000000000926';
            """))
        {
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => invalidReason.ExecuteNonQueryAsync(cancellationToken)).ConfigureAwait(false);
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("ck_api_keys_m1_e5_reason_text", exception.ConstraintName);
        }
    }
}
#pragma warning restore MA0051
