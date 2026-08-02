#pragma warning disable MA0051 // The replay ABI and evidence remain explicit in one flow.
using Npgsql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private static readonly Guid M3E4DeadSourceId =
        new("01900000-0000-7000-8000-00000000e401");
    private static readonly Guid M3E4PendingSourceId =
        new("01900000-0000-7000-8000-00000000e402");
    private static readonly Guid M3E4ReplayMessageId =
        new("01900000-0000-7000-8000-00000000e403");

    private const string M3E4ReplayDeduplicationKey =
        "m3-e4:outbox-replay:01900000-0000-7000-8000-00000000e403";

    private static async ValueTask AssertM3E4OutboxReplayPermissionsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using (NpgsqlCommand boundary = dataSource.CreateCommand("""
            SELECT (
                (
                    SELECT count(*)
                    FROM pg_catalog.pg_proc AS function
                    JOIN pg_catalog.pg_namespace AS schema
                      ON schema.oid = function.pronamespace
                    WHERE schema.nspname = 'public'
                      AND function.proname =
                          'poolai_operations_replay_dead_outbox'
                ) = 1
                AND EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_proc AS function
                    JOIN pg_catalog.pg_roles AS owner
                      ON owner.oid = function.proowner
                    WHERE function.oid =
                        'public.poolai_operations_replay_dead_outbox(uuid,uuid,text)'
                            ::regprocedure
                      AND function.prosecdef
                      AND function.proretset
                      AND function.provolatile = 'v'
                      AND owner.rolname = 'poolai_runtime_owner'
                      AND NOT owner.rolcanlogin
                      AND function.proconfig @> ARRAY[
                          'search_path=pg_catalog, public, pg_temp'
                      ]::text[]
                      AND function.proargnames = ARRAY[
                          'p_source_message_id',
                          'p_new_message_id',
                          'p_new_deduplication_key',
                          'disposition',
                          'new_message_id',
                          'event_sequence'
                      ]::text[]
                      AND function.proargmodes::text = '{i,i,i,t,t,t}'
                      AND function.proallargtypes = ARRAY[
                          'pg_catalog.uuid'::regtype::oid,
                          'pg_catalog.uuid'::regtype::oid,
                          'pg_catalog.text'::regtype::oid,
                          'pg_catalog.text'::regtype::oid,
                          'pg_catalog.uuid'::regtype::oid,
                          'pg_catalog.int8'::regtype::oid
                      ]::oid[]
                      AND pg_catalog.has_function_privilege(
                          'poolai_api', function.oid, 'EXECUTE')
                      AND NOT pg_catalog.has_function_privilege(
                          'poolai_worker', function.oid, 'EXECUTE')
                      AND NOT EXISTS (
                          SELECT 1
                          FROM pg_catalog.aclexplode(COALESCE(
                              function.proacl,
                              pg_catalog.acldefault(
                                  'f', function.proowner))) AS privilege
                          WHERE privilege.privilege_type = 'EXECUTE'
                            AND (
                                privilege.grantor <> function.proowner
                                OR privilege.is_grantable
                                OR privilege.grantee NOT IN (
                                    function.proowner,
                                    (
                                        SELECT role.oid
                                        FROM pg_catalog.pg_roles AS role
                                        WHERE role.rolname = 'poolai_api'
                                    )
                                )
                            )
                      )
                )
            );
            """))
        {
            Assert.True(Assert.IsType<bool>(await boundary
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
        }

        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_worker;
            SELECT *
            FROM public.poolai_operations_replay_dead_outbox(
                '01900000-0000-7000-8000-00000000e401',
                '01900000-0000-7000-8000-00000000e490',
                'm3-e4-worker-forbidden');
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            SELECT payload FROM public.outbox_messages WHERE false;
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            UPDATE public.outbox_messages SET id = id WHERE false;
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            DELETE FROM public.outbox_messages WHERE false;
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            INSERT INTO public.outbox_messages (
                id, deduplication_key, topic, schema_version,
                aggregate_type, aggregate_id, event_type,
                correlation_id, payload, replay_of
            )
            SELECT
                '01900000-0000-7000-8000-00000000e491',
                'm3-e4-api-replay-bypass', 'poolai.group-quota.v1', 1,
                'group_quota',
                '01900000-0000-7000-8000-00000000e410',
                'poolai.group-quota.settled',
                '01900000-0000-7000-8000-00000000e411',
                '{}'::jsonb,
                '01900000-0000-7000-8000-00000000e401'
            WHERE false;
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertM3E4OutboxReplayAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        await SeedM3E4OutboxSourcesAsync(dataSource, cancellationToken)
            .ConfigureAwait(false);
        string sourceBefore = await ReadM3E4SourceSnapshotAsync(
            dataSource,
            M3E4DeadSourceId,
            cancellationToken).ConfigureAwait(false);

        M3E4ReplayReceipt created = await ExecuteLockedM3E4ReplayAsync(
            dataSource,
            M3E4DeadSourceId,
            M3E4ReplayMessageId,
            M3E4ReplayDeduplicationKey,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("created", created.Disposition);
        Assert.Equal(M3E4ReplayMessageId, created.NewMessageId);
        Assert.NotNull(created.EventSequence);
        Assert.True(created.EventSequence > 0);

        await AssertM3E4ReplayRowAsync(
            dataSource,
            M3E4DeadSourceId,
            M3E4ReplayMessageId,
            M3E4ReplayDeduplicationKey,
            created.EventSequence!.Value,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(
            sourceBefore,
            await ReadM3E4SourceSnapshotAsync(
                dataSource,
                M3E4DeadSourceId,
                cancellationToken).ConfigureAwait(false));

        using NpgsqlConnection apiConnection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await SetM3E4ApiRoleAsync(apiConnection, cancellationToken).ConfigureAwait(false);
        try
        {
            M3E4ReplayReceipt replayed = await ExecuteM3E4ReplayAsync(
                apiConnection,
                M3E4DeadSourceId,
                M3E4ReplayMessageId,
                M3E4ReplayDeduplicationKey,
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(
                new M3E4ReplayReceipt(
                    "replayed",
                    M3E4ReplayMessageId,
                    created.EventSequence),
                replayed);

            M3E4ReplayReceipt missing = await ExecuteM3E4ReplayAsync(
                apiConnection,
                new Guid("01900000-0000-7000-8000-00000000e499"),
                new Guid("01900000-0000-7000-8000-00000000e404"),
                "m3-e4:missing-source",
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(
                new M3E4ReplayReceipt("source_not_found", null, null),
                missing);

            M3E4ReplayReceipt pending = await ExecuteM3E4ReplayAsync(
                apiConnection,
                M3E4PendingSourceId,
                new Guid("01900000-0000-7000-8000-00000000e405"),
                "m3-e4:pending-source",
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(
                new M3E4ReplayReceipt("source_not_dead", null, null),
                pending);

            M3E4ReplayReceipt idConflict = await ExecuteM3E4ReplayAsync(
                apiConnection,
                M3E4DeadSourceId,
                M3E4ReplayMessageId,
                "m3-e4:different-deduplication-key",
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(
                new M3E4ReplayReceipt("replay_conflict", null, null),
                idConflict);

            M3E4ReplayReceipt deduplicationConflict = await ExecuteM3E4ReplayAsync(
                apiConnection,
                M3E4DeadSourceId,
                new Guid("01900000-0000-7000-8000-00000000e406"),
                M3E4ReplayDeduplicationKey,
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(
                new M3E4ReplayReceipt("replay_conflict", null, null),
                deduplicationConflict);

            M3E4ReplayReceipt validation = await ExecuteM3E4ReplayAsync(
                apiConnection,
                M3E4DeadSourceId,
                M3E4DeadSourceId,
                "m3-e4:invalid-same-message-id",
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(
                new M3E4ReplayReceipt("validation_failed", null, null),
                validation);
        }
        finally
        {
            await ResetM3E4RoleAsync(apiConnection, cancellationToken)
                .ConfigureAwait(false);
        }

        using (NpgsqlCommand replayCount = dataSource.CreateCommand("""
            SELECT count(*)
            FROM public.outbox_messages
            WHERE replay_of = $1;
            """))
        {
            replayCount.Parameters.AddWithValue(M3E4DeadSourceId);
            Assert.Equal(
                1L,
                Assert.IsType<long>(await replayCount
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        Assert.Equal(
            sourceBefore,
            await ReadM3E4SourceSnapshotAsync(
                dataSource,
                M3E4DeadSourceId,
                cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask SeedM3E4OutboxSourcesAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            INSERT INTO public.outbox_messages (
                id, deduplication_key, topic, schema_version,
                aggregate_type, aggregate_id, aggregate_version,
                event_type, source_event_sequence,
                correlation_id, causation_id, payload, occurred_at,
                status, next_attempt_at, publish_attempts,
                locked_by, lock_generation, locked_until,
                published_at, dead_at, replay_of, last_error
            ) VALUES (
                '01900000-0000-7000-8000-00000000e401',
                'm3-e4:dead-source',
                'poolai.group-quota.v1', 1, 'group_quota',
                '01900000-0000-7000-8000-00000000e410', NULL,
                'poolai.group-quota.settled', 401,
                '01900000-0000-7000-8000-00000000e411',
                '01900000-0000-7000-8000-00000000e412',
                '{"event":"settled","tokens":"42","opaque_detail":"retained"}'::jsonb,
                '2026-08-02 01:02:03.456+00'::timestamptz,
                'dead', NULL, 3, NULL, 2, NULL, NULL,
                '2026-08-02 01:03:04.567+00'::timestamptz,
                NULL, 'm3-e4 poison probe'
            ), (
                '01900000-0000-7000-8000-00000000e402',
                'm3-e4:pending-source',
                'poolai.group-quota.v1', 1, 'group_quota',
                '01900000-0000-7000-8000-00000000e410', 7,
                'poolai.group-quota.released', 402,
                '01900000-0000-7000-8000-00000000e411', NULL,
                '{"event":"released","tokens":"0"}'::jsonb,
                '2026-08-02 01:04:05.678+00'::timestamptz,
                'pending',
                '2026-08-02 01:04:05.678+00'::timestamptz,
                0, NULL, 0, NULL, NULL, NULL, NULL, NULL
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<M3E4ReplayReceipt> ExecuteLockedM3E4ReplayAsync(
        NpgsqlDataSource dataSource,
        Guid sourceMessageId,
        Guid newMessageId,
        string newDeduplicationKey,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection blocker = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlTransaction blockerTransaction = await blocker
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        using (NpgsqlCommand lockSource = blocker.CreateCommand())
        {
            lockSource.Transaction = blockerTransaction;
            lockSource.CommandText = """
                SELECT id
                FROM public.outbox_messages
                WHERE id = $1
                FOR UPDATE;
                """;
            lockSource.Parameters.AddWithValue(sourceMessageId);
            Assert.Equal(
                sourceMessageId,
                Assert.IsType<Guid>(await lockSource
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using NpgsqlConnection apiConnection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await SetM3E4ApiRoleAsync(apiConnection, cancellationToken).ConfigureAwait(false);
        try
        {
            int processId = await ReadM1E4BackendPidAsync(
                apiConnection,
                cancellationToken).ConfigureAwait(false);
            Task<M3E4ReplayReceipt> replayTask = ExecuteM3E4ReplayAsync(
                apiConnection,
                sourceMessageId,
                newMessageId,
                newDeduplicationKey,
                cancellationToken).AsTask();
            bool observedLockWait = await WaitForM1E4LockWaitAsync(
                dataSource,
                processId,
                cancellationToken).ConfigureAwait(false);
            await blockerTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            M3E4ReplayReceipt receipt = await replayTask.ConfigureAwait(false);
            Assert.True(
                observedLockWait,
                "The replay entry point did not wait for the dead source row lock.");
            return receipt;
        }
        finally
        {
            await ResetM3E4RoleAsync(apiConnection, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<M3E4ReplayReceipt> ExecuteM3E4ReplayAsync(
        NpgsqlConnection connection,
        Guid sourceMessageId,
        Guid newMessageId,
        string newDeduplicationKey,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM public.poolai_operations_replay_dead_outbox($1, $2, $3);
            """;
        command.Parameters.AddWithValue(sourceMessageId);
        command.Parameters.AddWithValue(newMessageId);
        command.Parameters.AddWithValue(newDeduplicationKey);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(3, reader.FieldCount);
        Assert.Equal("disposition", reader.GetName(0));
        Assert.Equal("new_message_id", reader.GetName(1));
        Assert.Equal("event_sequence", reader.GetName(2));
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M3E4ReplayReceipt receipt = new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return receipt;
    }

    private static async ValueTask AssertM3E4ReplayRowAsync(
        NpgsqlDataSource dataSource,
        Guid sourceMessageId,
        Guid replayMessageId,
        string replayDeduplicationKey,
        long eventSequence,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT (
                replay.id = $2
                AND replay.deduplication_key = $3
                AND replay.event_sequence = $4
                AND replay.event_sequence <> source.event_sequence
                AND replay.status = 'pending'
                AND replay.next_attempt_at IS NOT NULL
                AND replay.publish_attempts = 0
                AND replay.locked_by IS NULL
                AND replay.lock_generation = 0
                AND replay.locked_until IS NULL
                AND replay.published_at IS NULL
                AND replay.dead_at IS NULL
                AND replay.last_error IS NULL
                AND replay.replay_of = source.id
                AND ROW(
                    replay.topic,
                    replay.schema_version,
                    replay.aggregate_type,
                    replay.aggregate_id,
                    replay.aggregate_version,
                    replay.event_type,
                    replay.source_event_sequence,
                    replay.correlation_id,
                    replay.causation_id,
                    replay.payload,
                    replay.occurred_at
                ) IS NOT DISTINCT FROM ROW(
                    source.topic,
                    source.schema_version,
                    source.aggregate_type,
                    source.aggregate_id,
                    source.aggregate_version,
                    source.event_type,
                    source.source_event_sequence,
                    source.correlation_id,
                    source.causation_id,
                    source.payload,
                    source.occurred_at
                )
            )
            FROM public.outbox_messages AS source
            JOIN public.outbox_messages AS replay ON replay.id = $2
            WHERE source.id = $1;
            """);
        command.Parameters.AddWithValue(sourceMessageId);
        command.Parameters.AddWithValue(replayMessageId);
        command.Parameters.AddWithValue(replayDeduplicationKey);
        command.Parameters.AddWithValue(eventSequence);
        Assert.True(Assert.IsType<bool>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false)));
    }

    private static async ValueTask<string> ReadM3E4SourceSnapshotAsync(
        NpgsqlDataSource dataSource,
        Guid sourceMessageId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = dataSource.CreateCommand("""
            SELECT pg_catalog.to_jsonb(source)::text
            FROM public.outbox_messages AS source
            WHERE source.id = $1;
            """);
        command.Parameters.AddWithValue(sourceMessageId);
        return Assert.IsType<string>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask SetM3E4ApiRoleAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SET ROLE poolai_api;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ResetM3E4RoleAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "RESET ROLE;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record M3E4ReplayReceipt(
        string Disposition,
        Guid? NewMessageId,
        long? EventSequence);
}
#pragma warning restore MA0051
