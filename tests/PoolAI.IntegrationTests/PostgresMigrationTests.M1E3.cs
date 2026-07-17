using Npgsql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private static async ValueTask AssertIdentityM1E3RuntimePermissionsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; UPDATE public.users SET status = status WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; UPDATE public.users SET display_name = display_name WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertScalarSucceedsAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            UPDATE public.users SET token_version = token_version WHERE false;
            SELECT true;
            """,
            cancellationToken).ConfigureAwait(false);

        await AssertIdentityM1E3UpdateWhitelistAsync(
            connectionString,
            cancellationToken).ConfigureAwait(false);
        await AssertIdentityM1E3RoleTriggerSecurityAsync(
            connectionString,
            cancellationToken).ConfigureAwait(false);
        await AssertIdentityM1E3RoleInsertAsync(
            connectionString,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertIdentityM1E3UpdateWhitelistAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand updateColumns = dataSource.CreateCommand("""
            SELECT array_agg(attribute.attname ORDER BY attribute.attname)
            FROM pg_catalog.pg_attribute AS attribute
            WHERE attribute.attrelid = 'public.users'::regclass
              AND attribute.attnum > 0
              AND NOT attribute.attisdropped
              AND pg_catalog.has_column_privilege(
                  'poolai_api',
                  'public.users',
                  attribute.attname,
                  'UPDATE');
            """);
        string[] expected =
        [
            "failed_login_count",
            "last_login_at",
            "locked_until",
            "password_hash",
            "security_stamp",
            "token_version",
            "totp_last_accepted_step",
            "totp_secret_envelope",
        ];
        Assert.Equal(
            expected,
            Assert.IsType<string[]>(await updateColumns
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));

    }

    private static async ValueTask AssertIdentityM1E3RoleTriggerSecurityAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand triggerSecurity = dataSource.CreateCommand("""
            SELECT procedure.prosecdef
               AND owner.rolname = 'poolai_runtime_owner'
               AND procedure.proconfig @> ARRAY[
                   'search_path=pg_catalog, public, pg_temp'
               ]::text[]
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.aclexplode(COALESCE(
                       procedure.proacl,
                       pg_catalog.acldefault('f', procedure.proowner))) AS privilege
                   LEFT JOIN pg_catalog.pg_roles AS grantee
                     ON grantee.oid = privilege.grantee
                   WHERE privilege.privilege_type = 'EXECUTE'
                     AND (privilege.grantee = 0 OR grantee.rolname IN (
                         'poolai_api', 'poolai_worker'
                     ))
               )
            FROM pg_catalog.pg_proc AS procedure
            JOIN pg_catalog.pg_namespace AS namespace
              ON namespace.oid = procedure.pronamespace
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = procedure.proowner
            WHERE namespace.nspname = 'public'
              AND procedure.proname = 'poolai_bump_role_user_version'
              AND procedure.pronargs = 0;
            """);
        Assert.True(Assert.IsType<bool>(await triggerSecurity
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false)));
    }

    private static async ValueTask AssertIdentityM1E3RoleInsertAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand auditor = dataSource.CreateCommand("""
            SET ROLE poolai_api;
            INSERT INTO public.users (
                id, email, normalized_email, display_name, password_hash,
                security_stamp, token_version, version
            ) VALUES (
                '01900000-0000-7000-8000-000000000601'::uuid,
                'm1e3-auditor@example.test',
                'm1e3-auditor@example.test',
                'M1-E3 Auditor',
                'poolai-password-v1:test',
                '01900000-0000-7000-8000-000000000602'::uuid,
                1,
                1
            );
            INSERT INTO public.user_roles (user_id, role_id, assigned_by)
            SELECT
                '01900000-0000-7000-8000-000000000601'::uuid,
                id,
                '01900000-0000-7000-8000-000000000601'::uuid
            FROM public.roles
            WHERE code = 'auditor';
            INSERT INTO public.audit_logs (
                id, actor_type, actor_user_id, action, target_type, target_id,
                request_id, metadata
            ) VALUES (
                '01900000-0000-7000-8000-000000000603'::uuid,
                'auditor',
                '01900000-0000-7000-8000-000000000601'::uuid,
                'identity.authorization.verified',
                'user',
                '01900000-0000-7000-8000-000000000601'::uuid,
                '01900000-0000-7000-8000-000000000604'::uuid,
                '{}'::jsonb
            );
            SELECT audit.actor_type
                   || ':' || user_account.token_version::text
                   || ':' || user_account.version::text
            FROM public.audit_logs AS audit
            JOIN public.users AS user_account
              ON user_account.id = audit.actor_user_id
            WHERE audit.id = '01900000-0000-7000-8000-000000000603'::uuid;
            """);
        Assert.Equal(
            "auditor:2:2",
            Assert.IsType<string>(
                await auditor.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }
}
