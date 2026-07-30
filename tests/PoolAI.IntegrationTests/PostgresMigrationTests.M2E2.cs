#pragma warning disable MA0051 // The named database evidence scenarios intentionally remain contiguous.
using System.Runtime.CompilerServices;
using Npgsql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private const string M2E2AccountId = "01920000-0000-7000-8000-000000000101";
    private const string M2E2ChannelId = "01920000-0000-7000-8000-000000000102";
    private const string M2E2GroupId = "01920000-0000-7000-8000-000000000103";

    // plannedTest: EtagsWritesAndActivationEvidenceRemainIsolated
    private static async ValueTask EtagsWritesAndActivationEvidenceRemainIsolated(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; UPDATE public.channels SET name = name WHERE false;",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_api; INSERT INTO public.group_supply_configurations "
                + $"(group_id) VALUES ('{M2E2GroupId}');",
            cancellationToken).ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connectionString,
            "SET ROLE poolai_worker; SELECT * FROM "
                + $"public.poolai_supply_observe_group_readiness('{M2E2GroupId}');",
            cancellationToken).ConfigureAwait(false);

        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using (NpgsqlCommand seedGroup = dataSource.CreateCommand($"""
            INSERT INTO public.groups (id, name)
            VALUES ('{M2E2GroupId}', 'M2-E2 database evidence');
            """))
        {
            _ = await seedGroup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        string envelope = M2E1Envelope("m2-e2-k1", "AQ", "AQ");
        NpgsqlConnection createConnection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable createConnectionLease =
            createConnection.ConfigureAwait(false);
        using (NpgsqlCommand role = createConnection.CreateCommand())
        {
            role.CommandText = "SET ROLE poolai_api;";
            _ = await role.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using (NpgsqlCommand create = createConnection.CreateCommand())
        {
            create.CommandText = $"""
                SELECT (
                    SELECT disposition
                    FROM public.poolai_supply_create_account(
                        '{M2E2AccountId}', 'openai', 'M2-E2 Account',
                        'https://example.test/v1', $1::jsonb, 'sk-test', NULL,
                        8, 0, 100
                    )
                ) || ':' || (
                    SELECT disposition
                    FROM public.poolai_supply_create_channel(
                        '{M2E2ChannelId}', 'openai', 'M2-E2 Channel',
                        $2::jsonb,
                        $3::jsonb
                    )
                );
                """;
            create.Parameters.AddWithValue(envelope);
            create.Parameters.AddWithValue("""{"gpt-test":"gpt-upstream"}""");
            create.Parameters.AddWithValue(
                """
                {"responses":true,"chat_completions":true,"function_tools":true,"streaming":true}
                """);
            Assert.Equal(
                "created:created",
                Assert.IsType<string>(await create
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand activateAccount = createConnection.CreateCommand())
        {
            activateAccount.CommandText = $"""
                SELECT disposition
                FROM public.poolai_supply_update_account(
                    '{M2E2AccountId}', 1,
                    false, NULL::text, false, NULL::text,
                    false, NULL::jsonb, NULL::text, NULL::text,
                    true, 'active',
                    false, NULL::integer, false, NULL::integer,
                    false, NULL::integer, 'activate'
                );
                """;
            Assert.Equal(
                "updated",
                Assert.IsType<string>(await activateAccount
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand activateChannel = createConnection.CreateCommand())
        {
            activateChannel.CommandText = $"""
                SELECT disposition
                FROM public.poolai_supply_update_channel(
                    '{M2E2ChannelId}', 1,
                    false, NULL::text, true, 'active',
                    false, NULL::jsonb, false, NULL::jsonb, 'activate'
                );
                """;
            Assert.Equal(
                "updated",
                Assert.IsType<string>(await activateChannel
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand createConfiguration = createConnection.CreateCommand())
        {
            createConfiguration.CommandText = $"""
                SELECT disposition
                FROM public.poolai_supply_create_group_configuration(
                    '{M2E2GroupId}', '{M2E2ChannelId}',
                    ARRAY['{M2E2AccountId}'::uuid],
                    ARRAY[NULL::integer], ARRAY[NULL::integer],
                    ARRAY[true]
                );
                """;
            Assert.Equal(
                "created",
                Assert.IsType<string>(await createConfiguration
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand health = dataSource.CreateCommand($"""
            UPDATE public.accounts
            SET last_health_status = 'healthy',
                last_health_at = clock_timestamp()
            WHERE id = '{M2E2AccountId}';
            """))
        {
            _ = await health.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using NpgsqlCommand evidence = dataSource.CreateCommand($"""
            SET ROLE poolai_api;
            SELECT
                observation.disposition = 'ready'
                AND observation.configuration_version > 1
                AND observation.observed_at IS NOT NULL
                AND observation.canonical_snapshot ->> 'ready' = 'true'
                AND observation.canonical_snapshot::text
                    NOT LIKE '%credential%'
                AND observation.canonical_snapshot::text
                    NOT LIKE '%base_url%'
                AND (
                    SELECT version
                    FROM public.groups
                    WHERE id = '{M2E2GroupId}'
                ) = 1
            FROM public.poolai_supply_observe_group_readiness(
                '{M2E2GroupId}') AS observation;
            """);
        Assert.True(Assert.IsType<bool>(
            await evidence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }

    // plannedTest: LifecycleHealthAndRetirementReferencesAreIndependent
    private static async ValueTask LifecycleHealthAndRetirementReferencesAreIndependent(
        string connectionString,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using (NpgsqlCommand blocked = dataSource.CreateCommand($"""
            SET ROLE poolai_api;
            SELECT (
                SELECT disposition
                FROM public.poolai_supply_retire_account(
                    '{M2E2AccountId}', 2, 'retire')
            ) || ':' || (
                SELECT disposition
                FROM public.poolai_supply_retire_channel(
                    '{M2E2ChannelId}', 2, 'retire')
            );
            """))
        {
            Assert.Equal(
                "account_in_use:channel_in_use",
                Assert.IsType<string>(await blocked
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand clearBindings = dataSource.CreateCommand($"""
            SET ROLE poolai_api;
            SELECT disposition || ':' || was_changed::text
            FROM public.poolai_supply_patch_group_configuration(
                '{M2E2GroupId}', 2, false, NULL::uuid, true,
                ARRAY[]::uuid[], ARRAY[]::integer[],
                ARRAY[]::integer[], ARRAY[]::boolean[], 'clear bindings'
            );
            """))
        {
            Assert.Equal(
                "updated:true",
                Assert.IsType<string>(await clearBindings
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand retireAccount = dataSource.CreateCommand($"""
            SET ROLE poolai_api;
            SELECT disposition
            FROM public.poolai_supply_retire_account(
                '{M2E2AccountId}', 2, 'retire after unbind');
            """))
        {
            Assert.Equal(
                "retired",
                Assert.IsType<string>(await retireAccount
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand clearChannel = dataSource.CreateCommand($"""
            SET ROLE poolai_api;
            SELECT disposition || ':' || was_changed::text
            FROM public.poolai_supply_patch_group_configuration(
                '{M2E2GroupId}', 3, true, NULL::uuid, false,
                NULL::uuid[], NULL::integer[], NULL::integer[],
                NULL::boolean[], 'clear channel'
            );
            """))
        {
            Assert.Equal(
                "updated:true",
                Assert.IsType<string>(await clearChannel
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using NpgsqlCommand retireChannel = dataSource.CreateCommand($"""
            SET ROLE poolai_api;
            SELECT
                retirement.disposition = 'retired'
                AND retirement.was_changed
                AND retirement.current_version = 3
                AND observation.disposition = 'not_ready'
                AND observation.canonical_snapshot::text
                    NOT LIKE '%credential%'
            FROM public.poolai_supply_retire_channel(
                '{M2E2ChannelId}', 2, 'retire after detach') AS retirement
            CROSS JOIN public.poolai_supply_observe_group_readiness(
                '{M2E2GroupId}') AS observation;
            """);
        Assert.True(Assert.IsType<bool>(
            await retireChannel.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }
}
#pragma warning restore MA0051
