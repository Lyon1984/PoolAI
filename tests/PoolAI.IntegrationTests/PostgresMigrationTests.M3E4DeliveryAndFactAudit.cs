using Npgsql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private static readonly string[] M3E4WorkerOutboxInsertColumns =
    [
        "aggregate_id",
        "aggregate_type",
        "aggregate_version",
        "causation_id",
        "correlation_id",
        "deduplication_key",
        "event_type",
        "id",
        "occurred_at",
        "payload",
        "schema_version",
        "source_event_sequence",
        "topic",
    ];

    private static readonly string[] M3E4WorkerAuditInsertColumns =
    [
        "action",
        "actor_type",
        "actor_user_id",
        "after_state",
        "before_state",
        "id",
        "ip_address",
        "metadata",
        "reason",
        "request_id",
        "target_id",
        "target_type",
        "user_agent",
    ];

    private const string M3E4AttemptFactAuditBoundarySql = """
        WITH expected(column_name, privilege_type) AS (
            VALUES
                ('id', 'SELECT'),
                ('actor_type', 'SELECT'),
                ('actor_user_id', 'SELECT'),
                ('action', 'SELECT'),
                ('target_type', 'SELECT'),
                ('target_id', 'SELECT'),
                ('request_id', 'SELECT'),
                ('reason', 'SELECT'),
                ('ip_address', 'SELECT'),
                ('user_agent', 'SELECT'),
                ('before_state', 'SELECT'),
                ('after_state', 'SELECT'),
                ('metadata', 'SELECT'),
                ('id', 'INSERT'),
                ('actor_type', 'INSERT'),
                ('actor_user_id', 'INSERT'),
                ('action', 'INSERT'),
                ('target_type', 'INSERT'),
                ('target_id', 'INSERT'),
                ('request_id', 'INSERT'),
                ('reason', 'INSERT'),
                ('ip_address', 'INSERT'),
                ('user_agent', 'INSERT'),
                ('before_state', 'INSERT'),
                ('after_state', 'INSERT'),
                ('metadata', 'INSERT')
        ), actual AS (
            SELECT privilege.column_name, privilege.privilege_type
            FROM information_schema.column_privileges AS privilege
            WHERE privilege.table_schema = 'public'
              AND privilege.table_name = 'audit_logs'
              AND privilege.grantee = 'poolai_runtime_owner'
              AND privilege.privilege_type IN (
                  'SELECT', 'INSERT', 'UPDATE', 'REFERENCES')
        ), function_boundary AS (
            SELECT function.oid,
                   function.prosecdef,
                   function.provolatile,
                   function.proretset,
                   function.prorettype,
                   function.proconfig,
                   owner.rolname,
                   owner.rolcanlogin
            FROM pg_catalog.pg_proc AS function
            JOIN pg_catalog.pg_roles AS owner
              ON owner.oid = function.proowner
            WHERE function.oid = pg_catalog.to_regprocedure(
                'public.poolai_operations_append_attempt_fact_audit_once(uuid,text,uuid,text,text,uuid,uuid,text,inet,text,jsonb,jsonb,jsonb)')
        )
        SELECT
            NOT pg_catalog.has_table_privilege(
                'poolai_worker', 'public.audit_logs', 'INSERT')
            AND pg_catalog.has_any_column_privilege(
                'poolai_worker', 'public.audit_logs', 'INSERT')
            AND NOT pg_catalog.has_table_privilege(
                'poolai_worker', 'public.audit_logs',
                'SELECT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
            AND NOT pg_catalog.has_any_column_privilege(
                'poolai_worker', 'public.audit_logs',
                'SELECT, UPDATE, REFERENCES')
            AND NOT pg_catalog.has_table_privilege(
                'poolai_runtime_owner', 'public.audit_logs',
                'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
            AND NOT EXISTS (
                (SELECT * FROM expected EXCEPT SELECT * FROM actual)
                UNION ALL
                (SELECT * FROM actual EXCEPT SELECT * FROM expected)
            )
            AND EXISTS (
                SELECT 1
                FROM function_boundary
                WHERE prosecdef
                  AND provolatile = 'v'
                  AND NOT proretset
                  AND prorettype = 'pg_catalog.text'::regtype::oid
                  AND proconfig @> ARRAY[
                      'search_path=pg_catalog, public, pg_temp'
                  ]::text[]
                  AND rolname = 'poolai_runtime_owner'
                  AND NOT rolcanlogin
                  AND pg_catalog.has_function_privilege(
                      'poolai_api', oid, 'EXECUTE')
                  AND pg_catalog.has_function_privilege(
                      'poolai_worker', oid, 'EXECUTE')
            );
        """;

    private static async ValueTask AssertM3E4DeliveryAndFactAuditClosureAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        await AssertM3E4WorkerInsertAclAsync(dataSource, cancellationToken)
            .ConfigureAwait(false);
        await AssertM3E4WorkerNormalOutboxInsertAsync(dataSource, cancellationToken)
            .ConfigureAwait(false);
        await AssertM3E4WorkerReplayInsertDeniedAsync(
            connectionString,
            cancellationToken).ConfigureAwait(false);
        await AssertM3E4AttemptFactAuditBoundaryAsync(
            dataSource,
            connectionString,
            cancellationToken).ConfigureAwait(false);
        await AssertM3E4WorkerQuotaEntryPointCallableAsync(dataSource, cancellationToken)
            .ConfigureAwait(false);
        await AssertM3E4ApiReplayEntryPointCallableAsync(dataSource, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask AssertM3E4AttemptFactAuditBoundaryAsync(
        NpgsqlDataSource dataSource,
        string connectionString,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand boundary = dataSource.CreateCommand(
            M3E4AttemptFactAuditBoundarySql))
        {
            Assert.True(Assert.IsType<bool>(await boundary
                .ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
        }

        await AssertM3E4WorkerAuditInsertColumnsAsync(dataSource, cancellationToken)
            .ConfigureAwait(false);
        await AssertM3E4WorkerNormalAuditInsertAsync(dataSource, cancellationToken)
            .ConfigureAwait(false);
        await AssertM3E4WorkerAuditTableAccessDeniedAsync(
            connectionString,
            cancellationToken).ConfigureAwait(false);
        await AssertM3E4AttemptFactAuditScopeAsync(dataSource, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask AssertM3E4WorkerAuditTableAccessDeniedAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_worker; SELECT id FROM public.audit_logs LIMIT 1;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_worker; INSERT INTO public.audit_logs (id, actor_type, action, target_type, metadata, occurred_at) VALUES (gen_random_uuid(), 'service', 'supply.account.health_transition', 'account', '{}'::jsonb, clock_timestamp());",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_worker; UPDATE public.audit_logs SET action = action WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_worker; DELETE FROM public.audit_logs WHERE false;",
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM3E4WorkerAuditInsertColumnsAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand columns = dataSource.CreateCommand("""
            SELECT pg_catalog.array_agg(
                privilege.column_name ORDER BY privilege.column_name)
            FROM information_schema.column_privileges AS privilege
            WHERE privilege.table_schema = 'public'
              AND privilege.table_name = 'audit_logs'
              AND privilege.grantee = 'poolai_worker'
              AND privilege.privilege_type = 'INSERT';
            """);
        Assert.Equal(
            M3E4WorkerAuditInsertColumns,
            Assert.IsType<string[]>(await columns
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask AssertM3E4WorkerNormalAuditInsertAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        Guid auditId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        using (NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await SetM3E4RoleAsync(connection, "poolai_worker", cancellationToken)
                .ConfigureAwait(false);
            using NpgsqlCommand insert = new("""
                INSERT INTO public.audit_logs (
                    id, actor_type, action, target_type, target_id, metadata
                ) VALUES (
                    $1, 'service', 'supply.account.health_transition',
                    'account', $2, '{}'::jsonb
                );
                """, connection);
            insert.Parameters.AddWithValue(auditId);
            insert.Parameters.AddWithValue(accountId);
            Assert.Equal(
                1,
                await insert.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        using NpgsqlCommand verify = dataSource.CreateCommand("""
            SELECT pg_catalog.count(*)
            FROM public.audit_logs
            WHERE id = $1
              AND action = 'supply.account.health_transition'
              AND target_id = $2
              AND occurred_at IS NOT NULL;
            """);
        verify.Parameters.AddWithValue(auditId);
        verify.Parameters.AddWithValue(accountId);
        Assert.Equal(
            1L,
            Assert.IsType<long>(await verify
                .ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }

    private static async ValueTask AssertM3E4AttemptFactAuditScopeAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await SetM3E4RoleAsync(connection, "poolai_worker", cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlCommand invalid = new("""
            SELECT public.poolai_operations_append_attempt_fact_audit_once(
                $1, 'service', NULL, 'group_quota.forbidden',
                'usage_attempt', $2, NULL, NULL, NULL, NULL, NULL,
                '{}'::jsonb, '{}'::jsonb
            );
            """, connection);
        invalid.Parameters.AddWithValue(Guid.NewGuid());
        invalid.Parameters.AddWithValue(Guid.NewGuid());
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => invalid.ExecuteScalarAsync(cancellationToken)).ConfigureAwait(false);
        Assert.Equal("22023", exception.SqlState);
        Assert.Equal("poolai_attempt_fact_audit_invalid", exception.MessageText);
        await AssertM3E4AttemptFactAuditPayloadRejectedAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM3E4AttemptFactAuditPayloadRejectedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand invalid = new("""
            SELECT public.poolai_operations_append_attempt_fact_audit_once(
                $1, 'service', NULL,
                'group_quota.attempt_fact_settled',
                'usage_attempt', $2, $3, NULL, NULL, NULL, NULL,
                jsonb_build_object(
                    'input_tokens', '1',
                    'output_tokens', '1',
                    'cache_read_tokens', '0',
                    'cache_creation_tokens', '0',
                    'thinking_tokens', '0',
                    'total_tokens', '2'),
                jsonb_build_object(
                    'quota_event_id', $4::text,
                    'group_id', $5::text,
                    'period_id', $6::text,
                    'reservation_id', $7::text,
                    'attempt_id', $8::text,
                    'outcome', 'succeeded',
                    'usage_source', 'upstream',
                    'raw_payload', 'forbidden')
            );
            """, connection);
        for (int index = 0; index < 8; index++)
        {
            invalid.Parameters.AddWithValue(Guid.NewGuid());
        }

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => invalid.ExecuteScalarAsync(cancellationToken)).ConfigureAwait(false);
        Assert.Equal("22023", exception.SqlState);
        Assert.Equal("poolai_attempt_fact_audit_invalid", exception.MessageText);
    }

    private static async ValueTask AssertM3E4WorkerInsertAclAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand columns = dataSource.CreateCommand("""
            SELECT pg_catalog.array_agg(
                privilege.column_name ORDER BY privilege.column_name)
            FROM information_schema.column_privileges AS privilege
            WHERE privilege.table_schema = 'public'
              AND privilege.table_name = 'outbox_messages'
              AND privilege.grantee = 'poolai_worker'
              AND privilege.privilege_type = 'INSERT';
            """))
        {
            Assert.Equal(
                M3E4WorkerOutboxInsertColumns,
                Assert.IsType<string[]>(await columns
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using NpgsqlCommand boundary = dataSource.CreateCommand("""
            SELECT
                NOT pg_catalog.has_table_privilege(
                    'poolai_worker', 'public.outbox_messages', 'INSERT')
                AND pg_catalog.has_any_column_privilege(
                    'poolai_worker', 'public.outbox_messages', 'INSERT')
                AND NOT pg_catalog.has_column_privilege(
                    'poolai_worker',
                    'public.outbox_messages',
                    'replay_of',
                    'INSERT')
                AND NOT EXISTS (
                    SELECT 1
                    FROM information_schema.column_privileges AS privilege
                    WHERE privilege.table_schema = 'public'
                      AND privilege.table_name = 'outbox_messages'
                      AND privilege.grantee = 'poolai_worker'
                      AND privilege.privilege_type = 'INSERT'
                      AND privilege.is_grantable <> 'NO'
                );
            """);
        Assert.True(Assert.IsType<bool>(await boundary
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false)));
    }

    private static async ValueTask AssertM3E4WorkerNormalOutboxInsertAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        Guid messageId = Guid.NewGuid();
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await SetM3E4RoleAsync(connection, "poolai_worker", cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlCommand command = new("""
            INSERT INTO public.outbox_messages (
                id, deduplication_key, topic, schema_version,
                aggregate_type, aggregate_id, aggregate_version, event_type,
                source_event_sequence, correlation_id, causation_id, payload,
                occurred_at
            ) VALUES (
                $1, $2, 'poolai.security.v1', 1,
                'security', $3, NULL, 'security.probe',
                NULL, $4, NULL, '{}'::jsonb, clock_timestamp()
            )
            RETURNING replay_of IS NULL;
            """, connection);
        command.Parameters.AddWithValue(messageId);
        command.Parameters.AddWithValue($"m3-e4-worker-normal:{messageId:N}");
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(Guid.NewGuid());
        Assert.True(Assert.IsType<bool>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false)));
    }

    private static async ValueTask AssertM3E4WorkerReplayInsertDeniedAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        Guid messageId = Guid.NewGuid();
        await AssertPermissionDeniedAsync(
            connectionString,
            $$"""
            SET ROLE poolai_worker;
            INSERT INTO public.outbox_messages (
                id, deduplication_key, topic, schema_version,
                aggregate_type, aggregate_id, event_type,
                correlation_id, payload, replay_of
            ) VALUES (
                '{{messageId:D}}', 'm3-e4-worker-replay:{{messageId:N}}',
                'poolai.security.v1', 1, 'security',
                '{{Guid.NewGuid():D}}', 'security.probe',
                '{{Guid.NewGuid():D}}', '{}'::jsonb, '{{Guid.NewGuid():D}}'
            );
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM3E4WorkerQuotaEntryPointCallableAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await SetM3E4RoleAsync(connection, "poolai_worker", cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlCommand command = new("""
            SELECT *
            FROM public.poolai_quota_expire($1, $2, $3, $4, $5, $6);
            """, connection);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue($"m3-e4-expire-probe:{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("permission probe");
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(cancellationToken)).ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.Equal("group_quota_not_found", exception.MessageText);
    }

    private static async ValueTask AssertM3E4ApiReplayEntryPointCallableAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await SetM3E4RoleAsync(connection, "poolai_api", cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlCommand command = new("""
            SELECT disposition
            FROM public.poolai_operations_replay_dead_outbox($1, $2, $3);
            """, connection);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue($"m3-e4-api-replay-probe:{Guid.NewGuid():N}");
        Assert.Equal(
            "source_not_found",
            Assert.IsType<string>(await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask SetM3E4RoleAsync(
        NpgsqlConnection connection,
        string role,
        CancellationToken cancellationToken)
    {
        string sql = role switch
        {
            "poolai_api" => "SET ROLE poolai_api;",
            "poolai_worker" => "SET ROLE poolai_worker;",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
