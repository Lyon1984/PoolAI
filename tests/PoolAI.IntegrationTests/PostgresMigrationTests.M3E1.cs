#pragma warning disable MA0051 // M3-E1 keeps its linearization evidence explicit.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private static readonly Guid M3E1ActorId =
        new("01900000-0000-7000-8000-00000000d000");

    private static async ValueTask AssertM3E1RuntimePermissionsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        // Governing contract: docs/database/README.md sections 10-11 require
        // API-only wrappers and keep the 0002 raw mutations non-callable.
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            SELECT *
            FROM public.poolai_quota_adjust_total(
                '01900000-0000-7000-8000-00000000df01'::uuid,
                100::numeric, 1,
                '01900000-0000-7000-8000-00000000df02'::uuid,
                '01900000-0000-7000-8000-00000000df03'::uuid,
                '01900000-0000-7000-8000-00000000df04'::uuid,
                'm3-e1-raw-adjust-forbidden', 'forbidden');
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_api;
            SELECT *
            FROM public.poolai_quota_reset(
                '01900000-0000-7000-8000-00000000df01'::uuid,
                '01900000-0000-7000-8000-00000000df05'::uuid,
                100::numeric, 1,
                '01900000-0000-7000-8000-00000000df02'::uuid,
                '01900000-0000-7000-8000-00000000df03'::uuid,
                '01900000-0000-7000-8000-00000000df04'::uuid,
                'm3-e1-raw-reset-forbidden', 'forbidden');
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_worker;
            SELECT *
            FROM public.poolai_group_quota_adjust_total(
                '01900000-0000-7000-8000-00000000df01'::uuid,
                100::numeric, 1,
                '01900000-0000-7000-8000-00000000df02'::uuid,
                '01900000-0000-7000-8000-00000000df03'::uuid,
                '01900000-0000-7000-8000-00000000df04'::uuid,
                'm3-e1-worker-adjust-forbidden', 'forbidden');
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            """
            SET ROLE poolai_worker;
            SELECT *
            FROM public.poolai_group_quota_reset(
                '01900000-0000-7000-8000-00000000df01'::uuid,
                '01900000-0000-7000-8000-00000000df05'::uuid,
                100::numeric, 1,
                '01900000-0000-7000-8000-00000000df02'::uuid,
                '01900000-0000-7000-8000-00000000df03'::uuid,
                '01900000-0000-7000-8000-00000000df04'::uuid,
                'm3-e1-worker-reset-forbidden', 'forbidden');
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; "
                + "UPDATE public.group_quota_periods SET total_tokens = total_tokens WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; "
                + "SELECT group_id FROM public.group_token_quotas WHERE false FOR UPDATE;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; "
                + "INSERT INTO public.group_quota_events "
                + "(id, group_id, period_id, event_type, total_tokens_after, "
                + "consumed_tokens_after, reserved_tokens_after, actor_type, idempotency_key) "
                + "SELECT gen_random_uuid(), NULL, NULL, 'total_adjusted', 1, 0, 0, "
                + "'admin', 'forbidden' WHERE false;",
            cancellationToken).ConfigureAwait(false);

        const string SecuritySql = """
            WITH expected(signature) AS (
                VALUES
                    ('public.poolai_group_quota_adjust_total(uuid,numeric,bigint,uuid,uuid,uuid,text,text)'),
                    ('public.poolai_group_quota_reset(uuid,uuid,numeric,bigint,uuid,uuid,uuid,text,text)')
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
              AND function.proretset
              AND pg_catalog.cardinality(function.proargmodes) = CASE
                  WHEN function.proname = 'poolai_group_quota_adjust_total'
                      THEN 15
                  ELSE 17
              END
              AND function.proargnames[
                  pg_catalog.array_upper(function.proargnames, 1)
              ] = 'result_before_state'
              AND function.proallargtypes[
                  pg_catalog.array_upper(function.proallargtypes, 1)
              ] = pg_catalog.to_regtype('pg_catalog.jsonb')
              AND function.proargmodes[
                  pg_catalog.array_upper(function.proargmodes, 1)
              ] = 't'
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
              );
            """;
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using (NpgsqlCommand security = dataSource.CreateCommand(SecuritySql))
        {
            Assert.Equal(
                2L,
                Assert.IsType<long>(await security
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand overloads = dataSource.CreateCommand("""
            SELECT count(*)
            FROM pg_catalog.pg_proc AS function
            JOIN pg_catalog.pg_namespace AS schema
              ON schema.oid = function.pronamespace
            WHERE schema.nspname = 'public'
              AND function.proname = ANY (ARRAY[
                  'poolai_group_quota_adjust_total',
                  'poolai_group_quota_reset'
              ]);
            """))
        {
            Assert.Equal(
                2L,
                Assert.IsType<long>(await overloads
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using NpgsqlCommand auditorConstraint = dataSource.CreateCommand("""
            SELECT pg_catalog.pg_get_constraintdef(catalog_constraint.oid)
            FROM pg_catalog.pg_constraint AS catalog_constraint
            WHERE catalog_constraint.conrelid = 'public.audit_logs'::regclass
              AND catalog_constraint.conname = 'ck_audit_logs_actor_type';
            """);
        Assert.Contains(
            "'auditor'::text",
            Assert.IsType<string>(await auditorConstraint
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)),
            StringComparison.Ordinal);
    }

    private static async ValueTask AssertM3E1PeriodManagementAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        // Governing contracts: DEC-009/010/012/018 and AC-017/018 in
        // docs/开发执行规格-v1.0.md, plus docs/database/README.md sections 4-6.
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);

        await SeedM3E1ActorAsync(connection, cancellationToken).ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(connection, cancellationToken).ConfigureAwait(false);
        await CreateM3E1GroupAsync(
            connection,
            new Guid("01900000-0000-7000-8000-00000000d010"),
            new Guid("01900000-0000-7000-8000-00000000d011"),
            new Guid("01900000-0000-7000-8000-00000000d012"),
            new Guid("01900000-0000-7000-8000-00000000d013"),
            "M3-E1 Period Group",
            "m3-e1-period-initialize",
            cancellationToken).ConfigureAwait(false);

        using (NpgsqlCommand seedFacts = connection.CreateCommand())
        {
            seedFacts.CommandText = """
                RESET ROLE;
                INSERT INTO public.channels (
                    id, provider, name, model_rules, capabilities, status
                ) VALUES (
                    '01900000-0000-7000-8000-00000000d014',
                    'openai', 'M3-E1 Channel',
                    '{"gpt-m3e1":"gpt-m3e1"}'::jsonb,
                    '{"responses":true,"chat_completions":true,"function_tools":true,"streaming":true}'::jsonb,
                    'active'
                );
                INSERT INTO public.accounts (
                    id, provider, name, auth_type, upstream_base_url,
                    credential_envelope, credential_prefix,
                    status, last_health_at, last_health_status
                ) VALUES (
                    '01900000-0000-7000-8000-00000000d015',
                    'openai', 'M3-E1 Account', 'api_key',
                    'https://example.test/v1', '{}'::jsonb, 'sk-m3e1',
                    'active', clock_timestamp(), 'healthy'
                );
                INSERT INTO public.group_supply_configurations (group_id, channel_id)
                VALUES (
                    '01900000-0000-7000-8000-00000000d010',
                    '01900000-0000-7000-8000-00000000d014'
                );
                INSERT INTO public.group_accounts (
                    group_id, account_id, is_enabled
                ) VALUES (
                    '01900000-0000-7000-8000-00000000d010',
                    '01900000-0000-7000-8000-00000000d015', true
                );
                INSERT INTO public.subscription_templates (
                    id, group_id, name, default_duration_days
                ) VALUES (
                    '01900000-0000-7000-8000-00000000d016',
                    '01900000-0000-7000-8000-00000000d010',
                    'M3-E1 Template', 30
                );
                INSERT INTO public.subscriptions (
                    id, user_id, group_id, template_id, template_name_snapshot,
                    status, starts_at, expires_at, assigned_by, change_reason
                ) VALUES (
                    '01900000-0000-7000-8000-00000000d017',
                    '01900000-0000-7000-8000-00000000d000',
                    '01900000-0000-7000-8000-00000000d010',
                    '01900000-0000-7000-8000-00000000d016',
                    'M3-E1 Template', 'active',
                    clock_timestamp() - interval '1 day',
                    clock_timestamp() + interval '30 days',
                    '01900000-0000-7000-8000-00000000d000',
                    'M3-E1 database fixture'
                );
                INSERT INTO public.api_keys (
                    id, user_id, group_id, name, key_prefix,
                    secret_hash, pepper_version
                ) VALUES (
                    '01900000-0000-7000-8000-00000000d018',
                    '01900000-0000-7000-8000-00000000d000',
                    '01900000-0000-7000-8000-00000000d010',
                    'M3-E1 Key', 'sk-m3e1key001',
                    pg_catalog.decode(repeat('ab', 32), 'hex'), 1
                );
                INSERT INTO public.usage_requests (
                    request_id, user_id, api_key_id, subscription_id,
                    quota_group_id, routing_group_id, endpoint,
                    requested_model, is_streaming
                ) VALUES (
                    '01900000-0000-7000-8000-00000000d019',
                    '01900000-0000-7000-8000-00000000d000',
                    '01900000-0000-7000-8000-00000000d018',
                    '01900000-0000-7000-8000-00000000d017',
                    '01900000-0000-7000-8000-00000000d010',
                    '01900000-0000-7000-8000-00000000d010',
                    '/v1/chat/completions', 'gpt-m3e1', false
                );
                WITH database_time AS MATERIALIZED (
                    SELECT clock_timestamp() AS value
                )
                INSERT INTO public.group_token_reservations (
                    id, period_id, group_id, request_id, attempt_id,
                    attempt_index, account_id, channel_id, estimated_tokens,
                    status, is_streaming, lease_owner,
                    lease_expires_at, max_expires_at, created_at, updated_at
                )
                SELECT
                    '01900000-0000-7000-8000-00000000d01a',
                    '01900000-0000-7000-8000-00000000d011',
                    '01900000-0000-7000-8000-00000000d010',
                    '01900000-0000-7000-8000-00000000d019',
                    '01900000-0000-7000-8000-00000000d01b',
                    0,
                    '01900000-0000-7000-8000-00000000d015',
                    '01900000-0000-7000-8000-00000000d014',
                    20, 'pending', false, 'm3-e1-owner',
                    database_time.value + interval '5 minutes',
                    database_time.value + interval '10 minutes',
                    database_time.value, database_time.value
                FROM database_time;
                UPDATE public.group_quota_periods
                SET consumed_tokens = 100,
                    reserved_tokens = 20,
                    version = version + 1,
                    updated_at = clock_timestamp()
                WHERE id = '01900000-0000-7000-8000-00000000d011';
                SET ROLE poolai_api;
                """;
            await seedFacts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        M1E4Mutation activatedGroup = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-00000000d010', 1,
                false, NULL, false, NULL, 'active', 'M3-E1 activate',
                'v1.m3e1ready', clock_timestamp());
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(activatedGroup, "updated", true, 2);

        // DEC-012: a Subscription renewal must not behave like a Group period
        // reset. Exercise the production Subscription entry point, then compare
        // every identity/version field that defines the canonical current period.
        Guid periodGroupId = new("01900000-0000-7000-8000-00000000d010");
        M3E1QuotaPeriodIdentity beforeSubscriptionRenewal =
            await ReadM3E1QuotaPeriodIdentityAsync(
                connection,
                periodGroupId,
                cancellationToken).ConfigureAwait(false);
        M1E4Mutation renewedSubscription = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-00000000d017', 1,
                false, NULL, true, clock_timestamp() + interval '60 days',
                NULL, false,
                '01900000-0000-7000-8000-00000000d000',
                'DEC-012 subscription renewal');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(renewedSubscription, "updated", true, 2);
        Assert.Equal(
            beforeSubscriptionRenewal,
            await ReadM3E1QuotaPeriodIdentityAsync(
                connection,
                periodGroupId,
                cancellationToken).ConfigureAwait(false));

        // Restore the fixture's revoked Subscription through the same production
        // entry point so the later Group archive assertion keeps its original scope.
        M1E4Mutation revokedSubscription = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_subscription_update(
                '01900000-0000-7000-8000-00000000d017', 2,
                false, NULL, false, NULL, 'revoked', false,
                '01900000-0000-7000-8000-00000000d000',
                'restore M3-E1 archive fixture');
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(revokedSubscription, "updated", true, 3);

        M3E1QuotaMutation adjusted = await ExecuteM3E1AdjustmentAsync(
            connection,
            null,
            periodGroupId,
            90,
            1,
            new Guid("01900000-0000-7000-8000-00000000d020"),
            new Guid("01900000-0000-7000-8000-00000000d021"),
            "m3-e1-total-adjust",
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("90:100:20:0:2", adjusted.Result);
        AssertM3E1BeforeState(
            adjusted.BeforeState,
            new Guid("01900000-0000-7000-8000-00000000d010"),
            new Guid("01900000-0000-7000-8000-00000000d011"),
            "active",
            "200",
            "100",
            "20",
            "80",
            "0",
            1);

        M3E1QuotaMutation replayedAdjustment = await ExecuteM3E1AdjustmentAsync(
            connection,
            null,
            new Guid("01900000-0000-7000-8000-00000000d010"),
            90,
            1,
            new Guid("01900000-0000-7000-8000-00000000d020"),
            new Guid("01900000-0000-7000-8000-00000000d021"),
            "m3-e1-total-adjust",
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(adjusted.Result, replayedAdjustment.Result);
        AssertM3E1BeforeState(
            replayedAdjustment.BeforeState,
            new Guid("01900000-0000-7000-8000-00000000d010"),
            new Guid("01900000-0000-7000-8000-00000000d011"),
            "exhausted",
            "90",
            "100",
            "20",
            "0",
            "10",
            2);

        await AssertM3E1BusinessErrorAsync(
            connection,
            """
            SELECT *
            FROM public.poolai_group_quota_adjust_total(
                '01900000-0000-7000-8000-00000000d010',
                91, 1,
                '01900000-0000-7000-8000-00000000d000',
                '01900000-0000-7000-8000-00000000d020',
                '01900000-0000-7000-8000-00000000d021',
                'm3-e1-total-adjust', 'M3-E1 adjust total');
            """,
            "idempotency_key_reused",
            cancellationToken).ConfigureAwait(false);
        await AssertM3E1BusinessErrorAsync(
            connection,
            """
            SELECT *
            FROM public.poolai_group_quota_adjust_total(
                '01900000-0000-7000-8000-00000000d010',
                95, 1,
                '01900000-0000-7000-8000-00000000d000',
                '01900000-0000-7000-8000-00000000d022',
                '01900000-0000-7000-8000-00000000d023',
                'm3-e1-stale-version', 'M3-E1 stale version');
            """,
            "quota_version_conflict",
            cancellationToken).ConfigureAwait(false);

        decimal[] invalidTotals =
            [0m, -1m, 1.5m, 9_007_199_254_740_992m];
        for (int index = 0; index < invalidTotals.Length; index++)
        {
            using NpgsqlCommand invalid = connection.CreateCommand();
            invalid.CommandText = """
                SELECT *
                FROM public.poolai_group_quota_adjust_total(
                    '01900000-0000-7000-8000-00000000d010',
                    $1, 2,
                    '01900000-0000-7000-8000-00000000d000',
                    $2, $3, $4, 'M3-E1 invalid total');
                """;
            invalid.Parameters.AddWithValue(invalidTotals[index]);
            invalid.Parameters.AddWithValue(Guid.CreateVersion7());
            invalid.Parameters.AddWithValue(Guid.CreateVersion7());
            invalid.Parameters.AddWithValue($"m3-e1-invalid-total-{index}");
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => invalid.ExecuteScalarAsync(cancellationToken)).ConfigureAwait(false);
            Assert.Equal("P0001", exception.SqlState);
            Assert.Equal("invalid_quota_total_adjustment", exception.MessageText);
        }

        for (int index = 0; index < invalidTotals.Length; index++)
        {
            using NpgsqlCommand invalid = connection.CreateCommand();
            invalid.CommandText = """
                SELECT *
                FROM public.poolai_group_quota_reset(
                    '01900000-0000-7000-8000-00000000d010',
                    $1, $2, 2,
                    '01900000-0000-7000-8000-00000000d000',
                    $3, $4, $5, 'M3-E1 invalid reset');
                """;
            invalid.Parameters.AddWithValue(Guid.CreateVersion7());
            invalid.Parameters.AddWithValue(invalidTotals[index]);
            invalid.Parameters.AddWithValue(Guid.CreateVersion7());
            invalid.Parameters.AddWithValue(Guid.CreateVersion7());
            invalid.Parameters.AddWithValue($"m3-e1-invalid-reset-{index}");
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
                () => invalid.ExecuteScalarAsync(cancellationToken)).ConfigureAwait(false);
            Assert.Equal("P0001", exception.SqlState);
            Assert.Equal("invalid_quota_reset", exception.MessageText);
        }

        M1E4Mutation renamedGroup = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-00000000d010', 2,
                true, 'M3-E1 Renamed Group',
                false, NULL, NULL, NULL, NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(renamedGroup, "updated", true, 3);

        M3E1QuotaMutation reset = await ExecuteM3E1ResetAsync(
            connection,
            null,
            new Guid("01900000-0000-7000-8000-00000000d010"),
            new Guid("01900000-0000-7000-8000-00000000d030"),
            300,
            2,
            new Guid("01900000-0000-7000-8000-00000000d031"),
            new Guid("01900000-0000-7000-8000-00000000d032"),
            "m3-e1-period-reset",
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("2:300:0:0:300:3", reset.Result);
        AssertM3E1BeforeState(
            reset.BeforeState,
            new Guid("01900000-0000-7000-8000-00000000d010"),
            new Guid("01900000-0000-7000-8000-00000000d011"),
            "exhausted",
            "90",
            "100",
            "20",
            "0",
            "10",
            2);

        DateTime dispatchStartedAt;
        using (NpgsqlCommand dispatch = connection.CreateCommand())
        {
            dispatch.CommandText = """
                SELECT result_dispatch_started_at
                FROM public.poolai_quota_mark_dispatched(
                    '01900000-0000-7000-8000-00000000d010',
                    '01900000-0000-7000-8000-00000000d01b',
                    'm3-e1-owner', 'openai', 'gpt-m3e1', 12, 8,
                    '01900000-0000-7000-8000-00000000d040',
                    '01900000-0000-7000-8000-00000000d041',
                    'm3-e1-late-dispatch');
                """;
            dispatchStartedAt = Assert.IsType<DateTime>(await dispatch
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        using (NpgsqlCommand settle = connection.CreateCommand())
        {
            settle.CommandText = """
                SELECT result_status
                FROM public.poolai_quota_settle(
                    '01900000-0000-7000-8000-00000000d010',
                    '01900000-0000-7000-8000-00000000d01b',
                    '01900000-0000-7000-8000-00000000d015',
                    '01900000-0000-7000-8000-00000000d014',
                    'openai', 'gpt-m3e1', 'succeeded', 200, NULL,
                    12, 8, 0, 0, 0, 'upstream', 'm3-e1-upstream',
                    '{"source":"m3-e1"}'::jsonb,
                    $1, NULL, clock_timestamp(), 'succeeded',
                    '01900000-0000-7000-8000-00000000d042',
                    '01900000-0000-7000-8000-00000000d043',
                    'm3-e1-late-settle');
                """;
            settle.Parameters.AddWithValue(dispatchStartedAt);
            Assert.Equal(
                "settled",
                Assert.IsType<string>(await settle
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        M1E4Mutation disabledGroup = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-00000000d010', 3,
                false, NULL, false, NULL, 'disabled',
                'M3-E1 disable after settlement', NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(disabledGroup, "updated", true, 4);

        M1E4Mutation archivedGroup = await ExecuteM1E4MutationAsync(
            connection,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-00000000d010', 4,
                false, NULL, false, NULL, 'archived',
                'M3-E1 archive after settlement', NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(archivedGroup, "updated", true, 5);

        M3E1QuotaMutation replayAfterArchive = await ExecuteM3E1ResetAsync(
            connection,
            null,
            new Guid("01900000-0000-7000-8000-00000000d010"),
            new Guid("01900000-0000-7000-8000-00000000d030"),
            300,
            2,
            new Guid("01900000-0000-7000-8000-00000000d031"),
            new Guid("01900000-0000-7000-8000-00000000d032"),
            "m3-e1-period-reset",
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(reset.Result, replayAfterArchive.Result);
        AssertM3E1BeforeState(
            replayAfterArchive.BeforeState,
            new Guid("01900000-0000-7000-8000-00000000d010"),
            new Guid("01900000-0000-7000-8000-00000000d030"),
            "disabled",
            "300",
            "0",
            "0",
            "300",
            "0",
            3);

        await AssertM3E1BusinessErrorAsync(
            connection,
            """
            SELECT *
            FROM public.poolai_group_quota_reset(
                '01900000-0000-7000-8000-00000000d010',
                '01900000-0000-7000-8000-00000000d030',
                301, 2,
                '01900000-0000-7000-8000-00000000d000',
                '01900000-0000-7000-8000-00000000d031',
                '01900000-0000-7000-8000-00000000d032',
                'm3-e1-period-reset', 'M3-E1 reset period');
            """,
            "idempotency_key_reused",
            cancellationToken).ConfigureAwait(false);
        await AssertM3E1BusinessErrorAsync(
            connection,
            """
            SELECT *
            FROM public.poolai_group_quota_adjust_total(
                '01900000-0000-7000-8000-00000000d010',
                400, 3,
                '01900000-0000-7000-8000-00000000d000',
                '01900000-0000-7000-8000-00000000d050',
                '01900000-0000-7000-8000-00000000d051',
                'm3-e1-new-after-archive', 'M3-E1 forbidden archive write');
            """,
            "group_not_found_or_archived",
            cancellationToken).ConfigureAwait(false);

        using NpgsqlCommand state = connection.CreateCommand();
        state.CommandText = """
            RESET ROLE;
            SELECT jsonb_build_object(
                'group_version', current_group.version,
                'group_status', current_group.status,
                'quota_version', quota.version,
                'current_period_id', quota.current_period_id,
                'old_period', (
                    SELECT jsonb_build_array(
                        period.period_number,
                        period.status,
                        period.total_tokens::text,
                        period.consumed_tokens::text,
                        period.reserved_tokens::text)
                    FROM public.group_quota_periods AS period
                    WHERE period.id = '01900000-0000-7000-8000-00000000d011'
                ),
                'new_period', (
                    SELECT jsonb_build_array(
                        period.period_number,
                        period.status,
                        period.total_tokens::text,
                        period.consumed_tokens::text,
                        period.reserved_tokens::text)
                    FROM public.group_quota_periods AS period
                    WHERE period.id = '01900000-0000-7000-8000-00000000d030'
                ),
                'reservation', (
                    SELECT jsonb_build_array(
                        reservation.period_id,
                        reservation.status,
                        reservation.actual_tokens::text)
                    FROM public.group_token_reservations AS reservation
                    WHERE reservation.id = '01900000-0000-7000-8000-00000000d01a'
                ),
                'event_types', (
                    SELECT jsonb_object_agg(event_type, event_count)
                    FROM (
                        SELECT quota_event.event_type,
                               count(*) AS event_count
                        FROM public.group_quota_events AS quota_event
                        WHERE quota_event.group_id =
                            '01900000-0000-7000-8000-00000000d010'
                        GROUP BY quota_event.event_type
                    ) AS counts
                ),
                'outbox_count', (
                    SELECT count(*)
                    FROM public.outbox_messages AS message
                    WHERE message.aggregate_id =
                        '01900000-0000-7000-8000-00000000d010'
                      AND message.topic = 'poolai.quota.v1'
                )
            )::text
            FROM public.groups AS current_group
            JOIN public.group_token_quotas AS quota
              ON quota.group_id = current_group.id
            WHERE current_group.id =
                '01900000-0000-7000-8000-00000000d010';
            """;
        string persisted = Assert.IsType<string>(await state
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
        Assert.Contains("\"group_version\": 5", persisted, StringComparison.Ordinal);
        Assert.Contains("\"group_status\": \"archived\"", persisted, StringComparison.Ordinal);
        Assert.Contains("\"quota_version\": 3", persisted, StringComparison.Ordinal);
        Assert.Contains(
            "\"old_period\": [1, \"closed\", \"90\", \"120\", \"0\"]",
            persisted,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"new_period\": [2, \"current\", \"300\", \"0\", \"0\"]",
            persisted,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"reservation\": [\"01900000-0000-7000-8000-00000000d011\", \"settled\", \"20\"]",
            persisted,
            StringComparison.Ordinal);
        Assert.Contains("\"initialized\": 1", persisted, StringComparison.Ordinal);
        Assert.Contains("\"total_adjusted\": 1", persisted, StringComparison.Ordinal);
        Assert.Contains("\"period_reset\": 1", persisted, StringComparison.Ordinal);
        Assert.Contains("\"dispatch_started\": 1", persisted, StringComparison.Ordinal);
        Assert.Contains("\"settled\": 1", persisted, StringComparison.Ordinal);
        Assert.Contains("\"outbox_count\": 5", persisted, StringComparison.Ordinal);
    }

    private static async ValueTask AssertM3E1ArchiveConcurrencyAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        // Governing contract: 0013 and docs/database/README.md require both
        // archive commit orders for adjust and reset to linearize at
        // Quota -> Group -> Period without deadlock.
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        NpgsqlConnection setup = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable setupLease = setup.ConfigureAwait(false);
        await SeedM3E1ActorAsync(setup, cancellationToken).ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(setup, cancellationToken).ConfigureAwait(false);

        Guid archiveFirstGroup = new("01900000-0000-7000-8000-00000000d100");
        await CreateM3E1GroupAsync(
            setup,
            archiveFirstGroup,
            new Guid("01900000-0000-7000-8000-00000000d101"),
            new Guid("01900000-0000-7000-8000-00000000d102"),
            new Guid("01900000-0000-7000-8000-00000000d103"),
            "M3-E1 Archive First",
            "m3-e1-archive-first-initialize",
            cancellationToken).ConfigureAwait(false);

        NpgsqlConnection archiveWinner = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable archiveWinnerLease =
            archiveWinner.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(archiveWinner, cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction archiveTransaction = await archiveWinner
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable archiveTransactionLease =
            archiveTransaction.ConfigureAwait(false);
        M1E4Mutation stagedArchive = await ExecuteM1E4MutationAsync(
            archiveWinner,
            archiveTransaction,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-00000000d100', 1,
                false, NULL, false, NULL, 'archived',
                'M3-E1 archive wins', NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(stagedArchive, "updated", true, 2);

        NpgsqlConnection lateAdjustment = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lateAdjustmentLease =
            lateAdjustment.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(lateAdjustment, cancellationToken).ConfigureAwait(false);
        int lateAdjustmentPid = await ReadM1E4BackendPidAsync(
            lateAdjustment,
            cancellationToken).ConfigureAwait(false);
        Task<M3E1QuotaMutation> lateAdjustmentTask = ExecuteM3E1AdjustmentAsync(
            lateAdjustment,
            null,
            archiveFirstGroup,
            150,
            1,
            new Guid("01900000-0000-7000-8000-00000000d104"),
            new Guid("01900000-0000-7000-8000-00000000d105"),
            "m3-e1-archive-first-adjust",
            cancellationToken).AsTask();
        Assert.True(
            await WaitForM1E4LockWaitAsync(
                dataSource,
                lateAdjustmentPid,
                cancellationToken).ConfigureAwait(false),
            "Quota adjustment did not wait behind the archive quota fence.");
        await archiveTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        PostgresException archived = await Assert.ThrowsAsync<PostgresException>(
            () => lateAdjustmentTask).ConfigureAwait(false);
        Assert.Equal("P0001", archived.SqlState);
        Assert.Equal("group_not_found_or_archived", archived.MessageText);

        Guid adjustmentFirstGroup = new("01900000-0000-7000-8000-00000000d110");
        await CreateM3E1GroupAsync(
            setup,
            adjustmentFirstGroup,
            new Guid("01900000-0000-7000-8000-00000000d111"),
            new Guid("01900000-0000-7000-8000-00000000d112"),
            new Guid("01900000-0000-7000-8000-00000000d113"),
            "M3-E1 Adjustment First",
            "m3-e1-adjust-first-initialize",
            cancellationToken).ConfigureAwait(false);

        NpgsqlConnection adjustmentWinner = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable adjustmentWinnerLease =
            adjustmentWinner.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(adjustmentWinner, cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction adjustmentTransaction = await adjustmentWinner
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable adjustmentTransactionLease =
            adjustmentTransaction.ConfigureAwait(false);
        M3E1QuotaMutation adjustmentFirst = await ExecuteM3E1AdjustmentAsync(
            adjustmentWinner,
            adjustmentTransaction,
            adjustmentFirstGroup,
            150,
            1,
            new Guid("01900000-0000-7000-8000-00000000d114"),
            new Guid("01900000-0000-7000-8000-00000000d115"),
            "m3-e1-adjust-first",
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("150:0:0:150:2", adjustmentFirst.Result);
        AssertM3E1BeforeState(
            adjustmentFirst.BeforeState,
            adjustmentFirstGroup,
            new Guid("01900000-0000-7000-8000-00000000d111"),
            "disabled",
            "200",
            "0",
            "0",
            "200",
            "0",
            1);

        NpgsqlConnection lateArchive = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lateArchiveLease =
            lateArchive.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(lateArchive, cancellationToken).ConfigureAwait(false);
        int lateArchivePid = await ReadM1E4BackendPidAsync(
            lateArchive,
            cancellationToken).ConfigureAwait(false);
        Task<M1E4Mutation> lateArchiveTask = ExecuteM1E4MutationAsync(
            lateArchive,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-00000000d110', 1,
                false, NULL, false, NULL, 'archived',
                'M3-E1 adjustment wins', NULL, NULL);
            """,
            cancellationToken).AsTask();
        Assert.True(
            await WaitForM1E4LockWaitAsync(
                dataSource,
                lateArchivePid,
                cancellationToken).ConfigureAwait(false),
            "Group archive did not wait behind the quota adjustment fence.");
        await adjustmentTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(await lateArchiveTask.ConfigureAwait(false), "updated", true, 2);

        Guid archiveFirstResetGroup = new("01900000-0000-7000-8000-00000000d120");
        await CreateM3E1GroupAsync(
            setup,
            archiveFirstResetGroup,
            new Guid("01900000-0000-7000-8000-00000000d121"),
            new Guid("01900000-0000-7000-8000-00000000d122"),
            new Guid("01900000-0000-7000-8000-00000000d123"),
            "M3-E1 Archive First Reset",
            "m3-e1-archive-first-reset-initialize",
            cancellationToken).ConfigureAwait(false);

        NpgsqlConnection resetArchiveWinner = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable resetArchiveWinnerLease =
            resetArchiveWinner.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(
            resetArchiveWinner,
            cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction resetArchiveTransaction = await resetArchiveWinner
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable resetArchiveTransactionLease =
            resetArchiveTransaction.ConfigureAwait(false);
        M1E4Mutation stagedResetArchive = await ExecuteM1E4MutationAsync(
            resetArchiveWinner,
            resetArchiveTransaction,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-00000000d120', 1,
                false, NULL, false, NULL, 'archived',
                'M3-E1 archive wins reset', NULL, NULL);
            """,
            cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(stagedResetArchive, "updated", true, 2);

        NpgsqlConnection lateReset = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lateResetLease =
            lateReset.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(lateReset, cancellationToken).ConfigureAwait(false);
        int lateResetPid = await ReadM1E4BackendPidAsync(
            lateReset,
            cancellationToken).ConfigureAwait(false);
        Task<M3E1QuotaMutation> lateResetTask = ExecuteM3E1ResetAsync(
            lateReset,
            null,
            archiveFirstResetGroup,
            new Guid("01900000-0000-7000-8000-00000000d124"),
            250,
            1,
            new Guid("01900000-0000-7000-8000-00000000d125"),
            new Guid("01900000-0000-7000-8000-00000000d126"),
            "m3-e1-archive-first-reset",
            cancellationToken).AsTask();
        Assert.True(
            await WaitForM1E4LockWaitAsync(
                dataSource,
                lateResetPid,
                cancellationToken).ConfigureAwait(false),
            "Quota reset did not wait behind the archive quota fence.");
        await resetArchiveTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        PostgresException resetArchived = await Assert.ThrowsAsync<PostgresException>(
            () => lateResetTask).ConfigureAwait(false);
        Assert.Equal("P0001", resetArchived.SqlState);
        Assert.Equal("group_not_found_or_archived", resetArchived.MessageText);

        Guid resetFirstGroup = new("01900000-0000-7000-8000-00000000d130");
        await CreateM3E1GroupAsync(
            setup,
            resetFirstGroup,
            new Guid("01900000-0000-7000-8000-00000000d131"),
            new Guid("01900000-0000-7000-8000-00000000d132"),
            new Guid("01900000-0000-7000-8000-00000000d133"),
            "M3-E1 Reset First",
            "m3-e1-reset-first-initialize",
            cancellationToken).ConfigureAwait(false);

        NpgsqlConnection resetWinner = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable resetWinnerLease =
            resetWinner.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(resetWinner, cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction resetTransaction = await resetWinner
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable resetTransactionLease =
            resetTransaction.ConfigureAwait(false);
        M3E1QuotaMutation resetFirst = await ExecuteM3E1ResetAsync(
            resetWinner,
            resetTransaction,
            resetFirstGroup,
            new Guid("01900000-0000-7000-8000-00000000d134"),
            250,
            1,
            new Guid("01900000-0000-7000-8000-00000000d135"),
            new Guid("01900000-0000-7000-8000-00000000d136"),
            "m3-e1-reset-first",
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("2:250:0:0:250:2", resetFirst.Result);
        AssertM3E1BeforeState(
            resetFirst.BeforeState,
            resetFirstGroup,
            new Guid("01900000-0000-7000-8000-00000000d131"),
            "disabled",
            "200",
            "0",
            "0",
            "200",
            "0",
            1);

        NpgsqlConnection lateResetArchive = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lateResetArchiveLease =
            lateResetArchive.ConfigureAwait(false);
        await SetM1E4ApiRoleAsync(
            lateResetArchive,
            cancellationToken).ConfigureAwait(false);
        int lateResetArchivePid = await ReadM1E4BackendPidAsync(
            lateResetArchive,
            cancellationToken).ConfigureAwait(false);
        Task<M1E4Mutation> lateResetArchiveTask = ExecuteM1E4MutationAsync(
            lateResetArchive,
            null,
            """
            SELECT disposition, was_changed, before_state::text, current_version
            FROM public.poolai_group_update(
                '01900000-0000-7000-8000-00000000d130', 1,
                false, NULL, false, NULL, 'archived',
                'M3-E1 reset wins', NULL, NULL);
            """,
            cancellationToken).AsTask();
        Assert.True(
            await WaitForM1E4LockWaitAsync(
                dataSource,
                lateResetArchivePid,
                cancellationToken).ConfigureAwait(false),
            "Group archive did not wait behind the quota reset fence.");
        await resetTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        AssertM1E4Mutation(
            await lateResetArchiveTask.ConfigureAwait(false),
            "updated",
            true,
            2);

        using NpgsqlCommand persisted = setup.CreateCommand();
        persisted.CommandText = """
            SELECT string_agg(
                current_group.status || ':' || current_group.version::text
                    || ':' || quota.version::text
                    || ':' || period.total_tokens::text,
                ',' ORDER BY current_group.id)
            FROM public.groups AS current_group
            JOIN public.group_token_quotas AS quota
              ON quota.group_id = current_group.id
            JOIN public.group_quota_periods AS period
              ON period.id = quota.current_period_id
             AND period.group_id = quota.group_id
            WHERE current_group.id IN (
                '01900000-0000-7000-8000-00000000d100',
                '01900000-0000-7000-8000-00000000d110',
                '01900000-0000-7000-8000-00000000d120',
                '01900000-0000-7000-8000-00000000d130'
            );
            """;
        Assert.Equal(
            "archived:2:1:200,archived:2:2:150,"
                + "archived:2:1:200,archived:2:2:250",
            Assert.IsType<string>(await persisted
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask SeedM3E1ActorAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            RESET ROLE;
            INSERT INTO public.users (
                id, email, normalized_email, display_name,
                password_hash, security_stamp
            ) VALUES (
                '01900000-0000-7000-8000-00000000d000',
                'm3e1-actor@example.test', 'm3e1-actor@example.test',
                'M3-E1 Actor', 'poolai-password-v1:test',
                '01900000-0000-7000-8000-00000000d001'
            )
            ON CONFLICT (id) DO NOTHING;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CreateM3E1GroupAsync(
        NpgsqlConnection connection,
        Guid groupId,
        Guid periodId,
        Guid eventId,
        Guid outboxId,
        string name,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT disposition
            FROM public.poolai_group_create(
                $1, $2, NULL, $3, 200, $4, $5, $6, $7,
                'M3-E1 database fixture');
            """;
        command.Parameters.AddWithValue(groupId);
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(periodId);
        command.Parameters.AddWithValue(M3E1ActorId);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(outboxId);
        command.Parameters.AddWithValue(idempotencyKey);
        Assert.Equal(
            "created",
            Assert.IsType<string>(await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask<M3E1QuotaMutation> ExecuteM3E1AdjustmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid groupId,
        decimal totalTokens,
        long expectedVersion,
        Guid eventId,
        Guid outboxId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                result_total_tokens::text || ':'
                    || result_consumed_tokens::text || ':'
                    || result_reserved_tokens::text || ':'
                    || result_remaining_tokens::text || ':'
                    || result_quota_version::text,
                result_before_state::text
            FROM public.poolai_group_quota_adjust_total(
                $1, $2, $3, $4, $5, $6, $7, 'M3-E1 adjust total');
            """;
        command.Parameters.AddWithValue(groupId);
        command.Parameters.AddWithValue(totalTokens);
        command.Parameters.AddWithValue(expectedVersion);
        command.Parameters.AddWithValue(M3E1ActorId);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(outboxId);
        command.Parameters.AddWithValue(idempotencyKey);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M3E1QuotaMutation result = new(reader.GetString(0), reader.GetString(1));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return result;
    }

    private static async ValueTask<M3E1QuotaMutation> ExecuteM3E1ResetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid groupId,
        Guid periodId,
        decimal totalTokens,
        long expectedVersion,
        Guid eventId,
        Guid outboxId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                result_period_number::text || ':'
                    || result_total_tokens::text || ':'
                    || result_consumed_tokens::text || ':'
                    || result_reserved_tokens::text || ':'
                    || result_remaining_tokens::text || ':'
                    || result_quota_version::text,
                result_before_state::text
            FROM public.poolai_group_quota_reset(
                $1, $2, $3, $4, $5, $6, $7, $8, 'M3-E1 reset period');
            """;
        command.Parameters.AddWithValue(groupId);
        command.Parameters.AddWithValue(periodId);
        command.Parameters.AddWithValue(totalTokens);
        command.Parameters.AddWithValue(expectedVersion);
        command.Parameters.AddWithValue(M3E1ActorId);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(outboxId);
        command.Parameters.AddWithValue(idempotencyKey);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M3E1QuotaMutation result = new(reader.GetString(0), reader.GetString(1));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return result;
    }

    private static async ValueTask<M3E1QuotaPeriodIdentity>
        ReadM3E1QuotaPeriodIdentityAsync(
            NpgsqlConnection connection,
            Guid groupId,
            CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                quota.current_period_id,
                quota.version,
                current_period.id,
                current_period.version,
                (
                    SELECT count(*)
                    FROM public.group_quota_periods AS period
                    WHERE period.group_id = quota.group_id
                )
            FROM public.group_token_quotas AS quota
            JOIN public.group_quota_periods AS current_period
              ON current_period.id = quota.current_period_id
             AND current_period.group_id = quota.group_id
            WHERE quota.group_id = $1;
            """;
        command.Parameters.AddWithValue(groupId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M3E1QuotaPeriodIdentity result = new(
            reader.GetGuid(0),
            reader.GetInt64(1),
            reader.GetGuid(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return result;
    }

    private static void AssertM3E1BeforeState(
        string json,
        Guid expectedGroupId,
        Guid expectedPeriodId,
        string expectedStatus,
        string expectedTotalTokens,
        string expectedConsumedTokens,
        string expectedReservedTokens,
        string expectedRemainingTokens,
        string expectedOverageTokens,
        long expectedVersion)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(12, root.EnumerateObject().Count());
        Assert.Equal(expectedGroupId, root.GetProperty("group_id").GetGuid());
        Assert.Equal(expectedPeriodId, root.GetProperty("period_id").GetGuid());
        Assert.Equal(expectedStatus, root.GetProperty("status").GetString());
        Assert.Equal(expectedTotalTokens, root.GetProperty("total_tokens").GetString());
        Assert.Equal(expectedConsumedTokens, root.GetProperty("consumed_tokens").GetString());
        Assert.Equal(expectedReservedTokens, root.GetProperty("reserved_tokens").GetString());
        Assert.Equal(expectedRemainingTokens, root.GetProperty("remaining_tokens").GetString());
        Assert.Equal(expectedOverageTokens, root.GetProperty("overage_tokens").GetString());
        DateTimeOffset periodStartedAt =
            root.GetProperty("period_started_at").GetDateTimeOffset();
        Assert.Equal(
            JsonValueKind.Null,
            root.GetProperty("period_ended_at").ValueKind);
        Assert.Equal(expectedVersion, root.GetProperty("version").GetInt64());
        Assert.True(
            root.GetProperty("updated_at").GetDateTimeOffset() >= periodStartedAt);
    }

    private static async ValueTask AssertM3E1BusinessErrorAsync(
        NpgsqlConnection connection,
        string sql,
        string expectedMessage,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteScalarAsync(cancellationToken)).ConfigureAwait(false);
        Assert.Equal("P0001", exception.SqlState);
        Assert.Equal(expectedMessage, exception.MessageText);
    }

    private readonly record struct M3E1QuotaMutation(
        string Result,
        string BeforeState);

    private sealed record M3E1QuotaPeriodIdentity(
        Guid QuotaCurrentPeriodId,
        long QuotaVersion,
        Guid CurrentPeriodId,
        long CurrentPeriodVersion,
        long PeriodCount);
}
