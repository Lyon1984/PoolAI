using Npgsql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private const string SeedIdentityM1E2Sql = """
        INSERT INTO public.users (
            id, email, normalized_email, display_name, password_hash, security_stamp
        ) VALUES
            (
                '01900000-0000-7000-8000-000000000500'::uuid,
                'm1e2-schema@example.test',
                'm1e2-schema@example.test',
                'M1-E2 Schema',
                'poolai-password-v1:test',
                '01900000-0000-7000-8000-000000000510'::uuid
            ),
            (
                '01900000-0000-7000-8000-000000000501'::uuid,
                'm1e2-invalid@example.test',
                'm1e2-invalid@example.test',
                'M1-E2 Invalid',
                'poolai-password-v1:test',
                '01900000-0000-7000-8000-000000000511'::uuid
            );

        INSERT INTO public.one_time_tokens (
            id, user_id, purpose, token_hash, pepper_version, expires_at,
            challenge_kind, secret_envelope, security_stamp, token_version
        ) VALUES
            (
                '01900000-0000-7000-8000-000000000502'::uuid,
                '01900000-0000-7000-8000-000000000500'::uuid,
                'totp_challenge', decode(repeat('01', 32), 'hex'), 1,
                clock_timestamp() + interval '10 minutes', 'setup', '{}'::jsonb,
                '01900000-0000-7000-8000-000000000510'::uuid, 1
            ),
            (
                '01900000-0000-7000-8000-000000000503'::uuid,
                '01900000-0000-7000-8000-000000000500'::uuid,
                'totp_challenge', decode(repeat('02', 32), 'hex'), 1,
                clock_timestamp() + interval '5 minutes', 'login', NULL,
                '01900000-0000-7000-8000-000000000510'::uuid, 1
            );

        UPDATE public.one_time_tokens
        SET response_body_envelope = '{}'::jsonb
        WHERE id = '01900000-0000-7000-8000-000000000502'::uuid;

        INSERT INTO public.totp_recovery_codes (
            id, user_id, code_hash, pepper_version
        ) VALUES (
            '01900000-0000-7000-8000-000000000504'::uuid,
            '01900000-0000-7000-8000-000000000500'::uuid,
            decode(repeat('03', 32), 'hex'), 1
        );

        INSERT INTO public.refresh_sessions (
            id, family_id, user_id, token_hash, pepper_version, expires_at
        ) VALUES (
            '01900000-0000-7000-8000-000000000530'::uuid,
            '01900000-0000-7000-8000-000000000531'::uuid,
            '01900000-0000-7000-8000-000000000500'::uuid,
            decode(repeat('0b', 32), 'hex'), 1,
            clock_timestamp() + interval '30 days'
        );
        """;

    private const string InvalidChallengeKindSql = """
        INSERT INTO public.one_time_tokens (
            id, user_id, purpose, token_hash, pepper_version, expires_at,
            challenge_kind
        ) VALUES (
            '01900000-0000-7000-8000-000000000505'::uuid,
            '01900000-0000-7000-8000-000000000501'::uuid,
            'activation', decode(repeat('04', 32), 'hex'), 1,
            clock_timestamp() + interval '5 minutes', 'login'
        );
        """;

    private const string MissingSetupSecretSql = """
        INSERT INTO public.one_time_tokens (
            id, user_id, purpose, token_hash, pepper_version, expires_at,
            challenge_kind, security_stamp, token_version
        ) VALUES (
            '01900000-0000-7000-8000-000000000506'::uuid,
            '01900000-0000-7000-8000-000000000501'::uuid,
            'totp_challenge', decode(repeat('05', 32), 'hex'), 1,
            clock_timestamp() + interval '10 minutes', 'setup',
            '01900000-0000-7000-8000-000000000511'::uuid, 1
        );
        """;

    private const string LoginResponseEnvelopeSql = """
        INSERT INTO public.one_time_tokens (
            id, user_id, purpose, token_hash, pepper_version, expires_at,
            challenge_kind, security_stamp, token_version, response_body_envelope
        ) VALUES (
            '01900000-0000-7000-8000-000000000507'::uuid,
            '01900000-0000-7000-8000-000000000501'::uuid,
            'totp_challenge', decode(repeat('06', 32), 'hex'), 1,
            clock_timestamp() + interval '5 minutes', 'login',
            '01900000-0000-7000-8000-000000000511'::uuid, 1, '{}'::jsonb
        );
        """;

    private const string DuplicateOpenSetupChallengeSql = """
        INSERT INTO public.one_time_tokens (
            id, user_id, purpose, token_hash, pepper_version, expires_at,
            challenge_kind, secret_envelope, security_stamp, token_version
        ) VALUES (
            '01900000-0000-7000-8000-000000000508'::uuid,
            '01900000-0000-7000-8000-000000000500'::uuid,
            'totp_challenge', decode(repeat('07', 32), 'hex'), 1,
            clock_timestamp() + interval '10 minutes', 'setup', '{}'::jsonb,
            '01900000-0000-7000-8000-000000000510'::uuid, 1
        );
        """;

    private const string InvalidRecoveryTerminalSql = """
        INSERT INTO public.totp_recovery_codes (
            id, user_id, code_hash, pepper_version, used_at, revoked_at, revoke_reason
        ) VALUES (
            '01900000-0000-7000-8000-000000000509'::uuid,
            '01900000-0000-7000-8000-000000000501'::uuid,
            decode(repeat('09', 32), 'hex'), 1,
            clock_timestamp(), clock_timestamp(), 'superseded'
        );
        """;

    private const string DuplicateActiveRefreshGenerationSql = """
        INSERT INTO public.refresh_sessions (
            id, family_id, user_id, token_hash, pepper_version, expires_at
        ) VALUES (
            '01900000-0000-7000-8000-000000000532'::uuid,
            '01900000-0000-7000-8000-000000000531'::uuid,
            '01900000-0000-7000-8000-000000000500'::uuid,
            decode(repeat('0c', 32), 'hex'), 1,
            clock_timestamp() + interval '30 days'
        );
        """;

    private const string RotateRefreshGenerationSql = """
        UPDATE public.refresh_sessions
        SET status = 'rotated', rotated_at = clock_timestamp()
        WHERE id = '01900000-0000-7000-8000-000000000530'::uuid;

        INSERT INTO public.refresh_sessions (
            id, family_id, user_id, parent_session_id,
            token_hash, pepper_version, expires_at
        ) VALUES (
            '01900000-0000-7000-8000-000000000533'::uuid,
            '01900000-0000-7000-8000-000000000531'::uuid,
            '01900000-0000-7000-8000-000000000500'::uuid,
            '01900000-0000-7000-8000-000000000530'::uuid,
            decode(repeat('0d', 32), 'hex'), 1,
            clock_timestamp() + interval '30 days'
        );

        UPDATE public.refresh_sessions
        SET replaced_by_session_id = '01900000-0000-7000-8000-000000000533'::uuid
        WHERE id = '01900000-0000-7000-8000-000000000530'::uuid;
        """;

    private const string IdentityM1E2IndexesSql = """
        SELECT count(*)
        FROM pg_catalog.pg_indexes
        WHERE schemaname = 'public'
          AND indexname = ANY (ARRAY[
              'uq_one_time_tokens_user_totp_challenge_open',
              'ix_totp_recovery_codes_user_active',
              'uq_refresh_sessions_family_active'
          ]);
        """;

    private const string ActiveRefreshGenerationCountSql = """
        SELECT count(*)
        FROM public.refresh_sessions
        WHERE family_id = '01900000-0000-7000-8000-000000000531'::uuid
          AND status = 'active';
        """;

    private const string ReadApiChallengeColumnsSql = """
        SET ROLE poolai_api;
        SELECT count(secret_envelope) + count(response_body_envelope)
        FROM public.one_time_tokens;
        """;

    private const string InsertApiChallengeSql = """
        SET ROLE poolai_api;
        WITH inserted AS (
            INSERT INTO public.one_time_tokens (
                id, user_id, purpose, token_hash, pepper_version, expires_at,
                challenge_kind, secret_envelope, security_stamp, token_version
            ) VALUES (
                '01900000-0000-7000-8000-000000000521'::uuid,
                '01900000-0000-7000-8000-000000000501'::uuid,
                'totp_challenge', decode(repeat('0a', 32), 'hex'), 1,
                clock_timestamp() + interval '10 minutes', 'setup', '{}'::jsonb,
                '01900000-0000-7000-8000-000000000511'::uuid, 1
            )
            RETURNING id
        )
        SELECT count(*) FROM inserted;
        """;

    private const string CompleteApiChallengeSql = """
        SET ROLE poolai_api;
        WITH completed AS (
            UPDATE public.one_time_tokens
            SET response_body_envelope = '{}'::jsonb
            WHERE id = '01900000-0000-7000-8000-000000000521'::uuid
            RETURNING id
        )
        SELECT count(*) FROM completed;
        """;

    private const string RewriteChallengeSnapshotSql = """
        SET ROLE poolai_api;
        UPDATE public.one_time_tokens
        SET token_version = token_version
        WHERE false;
        """;

    private const string InsertApiRecoveryCodeSql = """
        SET ROLE poolai_api;
        WITH inserted AS (
            INSERT INTO public.totp_recovery_codes (
                id, user_id, code_hash, pepper_version
            ) VALUES (
                '01900000-0000-7000-8000-000000000520'::uuid,
                '01900000-0000-7000-8000-000000000500'::uuid,
                decode(repeat('08', 32), 'hex'), 1
            )
            RETURNING id
        )
        SELECT count(*) FROM inserted;
        """;

    private const string ConsumeApiRecoveryCodeSql = """
        SET ROLE poolai_api;
        WITH consumed AS (
            UPDATE public.totp_recovery_codes
            SET used_at = clock_timestamp()
            WHERE id = '01900000-0000-7000-8000-000000000520'::uuid
              AND used_at IS NULL
              AND revoked_at IS NULL
            RETURNING id
        )
        SELECT count(*) FROM consumed;
        """;

    private const string DeleteApiRecoveryCodeSql =
        "SET ROLE poolai_api; DELETE FROM public.totp_recovery_codes WHERE false;";

    private const string DeleteApiChallengeSql =
        "SET ROLE poolai_api; DELETE FROM public.one_time_tokens WHERE false;";

    private const string DeleteApiRefreshSessionSql =
        "SET ROLE poolai_api; DELETE FROM public.refresh_sessions WHERE false;";

    private const string WorkerChallengeSecretReadSql = """
        SET ROLE poolai_worker;
        SELECT count(secret_envelope) + count(response_body_envelope)
        FROM public.one_time_tokens;
        """;

    private const string WorkerRecoveryHashReadSql =
        "SET ROLE poolai_worker; SELECT count(code_hash) FROM public.totp_recovery_codes;";

    private const string OwnerChallengeSecretReadSql = """
        SET ROLE poolai_runtime_owner;
        SELECT count(secret_envelope) + count(response_body_envelope)
        FROM public.one_time_tokens;
        """;

    private const string OwnerRecoveryHashReadSql =
        "SET ROLE poolai_runtime_owner; SELECT count(code_hash) FROM public.totp_recovery_codes;";

    private static async ValueTask AssertIdentityM1E2SchemaAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        // Governing contract: docs/database/README.md sections 1, 3, and 11.
        using (NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString))
        using (NpgsqlCommand seed = dataSource.CreateCommand(SeedIdentityM1E2Sql))
        {
            await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AssertTotpChallengeConstraintsAsync(connectionString, cancellationToken)
            .ConfigureAwait(false);
        await AssertTotpRecoveryConstraintAsync(connectionString, cancellationToken)
            .ConfigureAwait(false);
        await AssertRefreshFamilyInvariantAsync(connectionString, cancellationToken)
            .ConfigureAwait(false);
        await AssertIdentityM1E2IndexesAsync(connectionString, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask AssertTotpChallengeConstraintsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertConstraintRejectedAsync(
            connectionString, InvalidChallengeKindSql, PostgresErrorCodes.CheckViolation,
            "ck_one_time_tokens_challenge_kind", cancellationToken).ConfigureAwait(false);
        await AssertConstraintRejectedAsync(
            connectionString, MissingSetupSecretSql, PostgresErrorCodes.CheckViolation,
            "ck_one_time_tokens_secret_envelope", cancellationToken).ConfigureAwait(false);
        await AssertConstraintRejectedAsync(
            connectionString, LoginResponseEnvelopeSql, PostgresErrorCodes.CheckViolation,
            "ck_one_time_tokens_response_envelope", cancellationToken).ConfigureAwait(false);
        await AssertConstraintRejectedAsync(
            connectionString, DuplicateOpenSetupChallengeSql, PostgresErrorCodes.UniqueViolation,
            "uq_one_time_tokens_user_totp_challenge_open", cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertTotpRecoveryConstraintAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertConstraintRejectedAsync(
            connectionString, InvalidRecoveryTerminalSql, PostgresErrorCodes.CheckViolation,
            "ck_totp_recovery_codes_terminal", cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertRefreshFamilyInvariantAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertConstraintRejectedAsync(
            connectionString, DuplicateActiveRefreshGenerationSql,
            PostgresErrorCodes.UniqueViolation, "uq_refresh_sessions_family_active",
            cancellationToken).ConfigureAwait(false);

        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using (NpgsqlCommand rotate = dataSource.CreateCommand(RotateRefreshGenerationSql))
        {
            int affected = await rotate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            Assert.Equal(3, affected);
        }

        using NpgsqlCommand activeGeneration = dataSource.CreateCommand(
            ActiveRefreshGenerationCountSql);
        object? activeCount = await activeGeneration
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(1L, Assert.IsType<long>(activeCount));
    }

    private static async ValueTask AssertIdentityM1E2IndexesAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand indexes = dataSource.CreateCommand(IdentityM1E2IndexesSql);
        object? indexCount = await indexes
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(3L, Assert.IsType<long>(indexCount));
    }

    private static async ValueTask AssertIdentityM1E2RuntimePermissionsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertIdentityM1E2ApiPermissionsAsync(connectionString, cancellationToken)
            .ConfigureAwait(false);
        await AssertIdentityM1E2SensitiveReadsDeniedAsync(connectionString, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask AssertIdentityM1E2ApiPermissionsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertScalarSucceedsAsync(
            connectionString, ReadApiChallengeColumnsSql, cancellationToken).ConfigureAwait(false);
        await AssertScalarSucceedsAsync(
            connectionString, InsertApiChallengeSql, cancellationToken).ConfigureAwait(false);
        await AssertScalarSucceedsAsync(
            connectionString, CompleteApiChallengeSql, cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString, RewriteChallengeSnapshotSql, cancellationToken).ConfigureAwait(false);
        await AssertScalarSucceedsAsync(
            connectionString, InsertApiRecoveryCodeSql, cancellationToken).ConfigureAwait(false);
        await AssertScalarSucceedsAsync(
            connectionString, ConsumeApiRecoveryCodeSql, cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString, DeleteApiRecoveryCodeSql, cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString, DeleteApiChallengeSql, cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString, DeleteApiRefreshSessionSql, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertIdentityM1E2SensitiveReadsDeniedAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertPermissionDeniedAsync(
            connectionString, WorkerChallengeSecretReadSql, cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString, WorkerRecoveryHashReadSql, cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString, OwnerChallengeSecretReadSql, cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString, OwnerRecoveryHashReadSql, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertConstraintRejectedAsync(
        string connectionString,
        string sql,
        string expectedSqlState,
        string expectedConstraint,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(sql);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(cancellationToken)).ConfigureAwait(false);
        Assert.Equal(expectedSqlState, exception.SqlState);
        Assert.Equal(expectedConstraint, exception.ConstraintName);
    }
}
