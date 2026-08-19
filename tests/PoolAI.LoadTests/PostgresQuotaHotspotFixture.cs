using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.Database.Migrations;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota;
using PoolAI.Modules.Operations;
using Testcontainers.PostgreSql;

namespace PoolAI.LoadTests;

public sealed class PostgresQuotaHotspotFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public NpgsqlDataSource AdministratorDataSource { get; private set; } = null!;

    public ServiceProvider ApiServices { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        string administratorPassword = Secret();
        string migratorPassword = Secret();
        string apiPassword = Secret();
        string workerPassword = Secret();
        _container = new PostgreSqlBuilder(ReadPostgresImage())
            .WithDatabase("poolai")
            .WithUsername("postgres")
            .WithPassword(administratorPassword)
            .Build();

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _container.StartAsync(cancellationToken).ConfigureAwait(true);
        string administratorConnectionString = _container.GetConnectionString();
        RuntimeConnections connections = await ProvisionRuntimeRolesAsync(
            administratorConnectionString,
            migratorPassword,
            apiPassword,
            workerPassword,
            cancellationToken).ConfigureAwait(true);

        MigrationCatalog catalog = await MigrationCatalog
            .LoadAsync(cancellationToken)
            .ConfigureAwait(true);
        await new PostgresMigrator(catalog).ApplyAsync(
            connections.Migrator,
            "PoolAI.LoadTests.m3-exit-hotspot",
            cancellationToken).ConfigureAwait(true);

        AdministratorDataSource = NpgsqlDataSource.Create(administratorConnectionString);
        ApiServices = BuildApiServices(connections.Api);
    }

    public async ValueTask DisposeAsync()
    {
        if (ApiServices is not null)
        {
            await ApiServices.DisposeAsync().ConfigureAwait(true);
        }

        if (AdministratorDataSource is not null)
        {
            await AdministratorDataSource.DisposeAsync().ConfigureAwait(true);
        }

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(true);
        }
    }

    public async ValueTask<QuotaHotspotScenario> CreateScenarioAsync(
        long totalTokens,
        CancellationToken cancellationToken)
    {
        QuotaHotspotScenario scenario = QuotaHotspotScenario.Create(totalTokens);
        using NpgsqlConnection connection = await AdministratorDataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable transactionLease =
            transaction.ConfigureAwait(false);

        await InsertIdentityAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(false);
        await InsertGroupAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(false);
        await InsertAccountAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(false);
        await InsertChannelAndConfigurationAsync(
            connection,
            transaction,
            scenario,
            cancellationToken).ConfigureAwait(false);
        await ActivateGroupAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(false);
        await InsertAccessGrantAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return scenario;
    }

    #pragma warning disable MA0051 // Keep the atomic evidence query readable and auditable in one place.
    public async ValueTask<QuotaHotspotEvidence> ReadEvidenceAsync(
        QuotaHotspotScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            SELECT
                period.total_tokens::text,
                period.consumed_tokens::text,
                period.reserved_tokens::text,
                (period.total_tokens - period.consumed_tokens - period.reserved_tokens)::text,
                (SELECT count(*)::integer
                 FROM public.usage_requests request
                 WHERE request.quota_group_id = quota.group_id),
                (SELECT count(*)::integer
                 FROM public.group_token_reservations reservation
                 WHERE reservation.group_id = quota.group_id),
                (SELECT count(*)::integer
                 FROM public.group_token_reservations reservation
                 WHERE reservation.group_id = quota.group_id
                   AND reservation.status = 'settled'),
                (SELECT count(*)::integer
                 FROM public.group_token_reservations reservation
                 WHERE reservation.group_id = quota.group_id
                   AND reservation.status = 'released'),
                (SELECT count(*)::integer
                 FROM public.usage_attempts attempt
                 WHERE attempt.quota_group_id = quota.group_id),
                (SELECT count(*)::integer
                 FROM public.group_quota_events event
                 WHERE event.group_id = quota.group_id
                   AND event.event_type = 'reserved'),
                (SELECT count(*)::integer
                 FROM public.group_quota_events event
                 WHERE event.group_id = quota.group_id
                   AND event.event_type = 'dispatch_started'),
                (SELECT count(*)::integer
                 FROM public.group_quota_events event
                 WHERE event.group_id = quota.group_id
                   AND event.event_type = 'settled'),
                (SELECT count(*)::integer
                 FROM public.group_quota_events event
                 WHERE event.group_id = quota.group_id
                   AND event.event_type = 'released'),
                (SELECT count(*)::integer
                 FROM public.group_quota_events event
                 WHERE event.group_id = quota.group_id),
                (SELECT count(*)::integer
                 FROM public.outbox_messages message
                 WHERE message.topic = 'poolai.quota.v1'
                   AND message.aggregate_type = 'group'
                   AND message.aggregate_id = quota.group_id),
                (SELECT count(*)::integer
                 FROM public.audit_logs audit
                 WHERE audit.action = 'group_quota.attempt_fact_settled'
                   AND audit.metadata ->> 'group_id' = quota.group_id::text),
                (
                    SELECT count(*)::integer
                    FROM (
                        SELECT event.id::text AS identity
                        FROM public.group_quota_events event
                        WHERE event.group_id = quota.group_id
                        GROUP BY event.id
                        HAVING count(*) > 1
                        UNION ALL
                        SELECT event.idempotency_key
                        FROM public.group_quota_events event
                        WHERE event.group_id = quota.group_id
                        GROUP BY event.idempotency_key
                        HAVING count(*) > 1
                        UNION ALL
                        SELECT message.id::text
                        FROM public.outbox_messages message
                        WHERE message.topic = 'poolai.quota.v1'
                          AND message.aggregate_id = quota.group_id
                        GROUP BY message.id
                        HAVING count(*) > 1
                        UNION ALL
                        SELECT message.deduplication_key
                        FROM public.outbox_messages message
                        WHERE message.topic = 'poolai.quota.v1'
                          AND message.aggregate_id = quota.group_id
                        GROUP BY message.deduplication_key
                        HAVING count(*) > 1
                        UNION ALL
                        SELECT attempt.attempt_id::text
                        FROM public.usage_attempts attempt
                        WHERE attempt.quota_group_id = quota.group_id
                        GROUP BY attempt.attempt_id
                        HAVING count(*) > 1
                        UNION ALL
                        SELECT audit.id::text
                        FROM public.audit_logs audit
                        WHERE audit.action = 'group_quota.attempt_fact_settled'
                          AND audit.metadata ->> 'group_id' = quota.group_id::text
                        GROUP BY audit.id
                        HAVING count(*) > 1
                    ) duplicates
                ),
                (SELECT count(*)::integer
                 FROM public.group_quota_events event
                 WHERE event.group_id = quota.group_id
                   AND (
                       event.total_tokens_after < 1
                       OR event.consumed_tokens_after < 0
                       OR event.reserved_tokens_after < 0
                       OR event.consumed_tokens_after + event.reserved_tokens_after
                           > event.total_tokens_after
                   )),
                (
                    SELECT count(*)::integer
                    FROM information_schema.columns column_shape
                    WHERE column_shape.table_schema = 'public'
                      AND column_shape.table_name IN (
                          'group_quota_periods',
                          'group_token_reservations',
                          'group_quota_events',
                          'usage_attempts'
                      )
                      AND column_shape.data_type = 'numeric'
                      AND column_shape.numeric_precision <> 78
                )
            FROM public.group_token_quotas quota
            JOIN public.group_quota_periods period
              ON period.id = quota.current_period_id
            WHERE quota.group_id = $1;
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        QuotaHotspotEvidence evidence = new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetInt32(15),
            reader.GetInt32(16),
            reader.GetInt32(17),
            reader.GetInt32(18));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return evidence;
    }
    #pragma warning restore MA0051

    public async ValueTask<DateTimeOffset> AdvanceReservationTemporalFrontierAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            WITH temporal_frontier AS MATERIALIZED (
                SELECT pg_catalog.clock_timestamp() + interval '30 seconds' AS value
            )
            UPDATE public.group_token_reservations AS reservation
            SET created_at = temporal_frontier.value,
                updated_at = temporal_frontier.value
            FROM temporal_frontier
            WHERE reservation.attempt_id = $1
              AND reservation.status = 'pending'
              AND reservation.dispatch_started_at IS NULL
              AND reservation.lease_expires_at > temporal_frontier.value
              AND reservation.max_expires_at > temporal_frontier.value
            RETURNING reservation.updated_at;
            """);
        command.Parameters.AddWithValue(attemptId);
        object? scalar = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        DateTime temporalFrontier = Assert.IsType<DateTime>(scalar);
        Assert.Equal(DateTimeKind.Utc, temporalFrontier.Kind);
        return new DateTimeOffset(temporalFrontier);
    }

    public async ValueTask<QuotaHotspotDispatchClockEvidence>
        ReadDispatchClockEvidenceAsync(
            Guid attemptId,
            CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            SELECT
                reservation.created_at,
                reservation.updated_at,
                reservation.dispatch_started_at,
                (SELECT (event.metadata ->> 'dispatch_started_at')::timestamptz
                 FROM public.group_quota_events AS event
                 WHERE event.reservation_id = reservation.id
                   AND event.event_type = 'dispatch_started'),
                (SELECT count(*)::integer
                 FROM public.group_quota_events AS event
                 WHERE event.reservation_id = reservation.id
                   AND event.event_type = 'dispatch_started'),
                (SELECT count(*)::integer
                 FROM public.group_quota_events AS event
                 JOIN public.outbox_messages AS message
                   ON message.payload ->> 'event_id' = event.id::text
                 WHERE event.reservation_id = reservation.id
                   AND event.event_type = 'dispatch_started')
            FROM public.group_token_reservations AS reservation
            WHERE reservation.attempt_id = $1;
            """);
        command.Parameters.AddWithValue(attemptId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        QuotaHotspotDispatchClockEvidence evidence = new(
            reader.GetFieldValue<DateTimeOffset>(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetInt32(4),
            reader.GetInt32(5));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return evidence;
    }

    private static ServiceProvider BuildApiServices(string connectionString)
    {
        ConfigurationManager configuration = new();
        configuration["Data:Postgres:ConnectionString"] = connectionString;
        configuration["Data:Redis:ConnectionString"] = "127.0.0.1:1,abortConnect=true";
        configuration["Data:Redis:KeyPrefix"] = "poolai:r1:load:m3-exit:";
        configuration["Health:Ntp:Server"] = "127.0.0.1";
        configuration["Health:Ntp:Port"] = "123";
        configuration["Idempotency:RequestHashPepper"] = Convert.ToBase64String(
            SHA256.HashData("PoolAI.LoadTests.M3ExitHotspot"u8));

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPoolAiPostgresRuntime(
            connectionString,
            commandTimeoutSeconds: 60,
            maximumPoolSize: 64);
        services.AddOperationsModule(configuration, "LoadTests");
        services.AddGroupQuotaModule();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
    }

    private static async ValueTask InsertIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QuotaHotspotScenario scenario,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand user = new("""
                   INSERT INTO public.users (
                       id, email, normalized_email, display_name,
                       password_hash, security_stamp
                   ) VALUES (
                       $1, $2, $2, 'M3 Exit hotspot User',
                       'test-password-hash', $3
                   );
                   """, connection, transaction))
        {
            user.Parameters.AddWithValue(scenario.UserId);
            user.Parameters.AddWithValue(scenario.Email);
            user.Parameters.AddWithValue(
                Guid.Parse("019b90c0-0000-7000-8000-000000000009"));
            Assert.Equal(
                1,
                await user.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        using NpgsqlCommand role = new("""
            INSERT INTO public.user_roles (user_id, role_id, assigned_by)
            VALUES ($1, '01900000-0000-7000-8000-000000000001'::uuid, $1);
            """, connection, transaction);
        role.Parameters.AddWithValue(scenario.UserId);
        Assert.Equal(
            1,
            await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertGroupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QuotaHotspotScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new("""
            SELECT disposition
            FROM public.poolai_group_create(
                $1, $2, NULL, $3, $4, $5, $6, $7, $8,
                'M3 Exit deterministic PostgreSQL hotspot fixture');
            """, connection, transaction);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.GroupName);
        command.Parameters.AddWithValue(scenario.PeriodId);
        command.Parameters.AddWithValue(scenario.TotalTokens);
        command.Parameters.AddWithValue(scenario.UserId);
        command.Parameters.AddWithValue(
            Guid.Parse("019b90c0-0000-7000-8000-00000000000a"));
        command.Parameters.AddWithValue(
            Guid.Parse("019b90c0-0000-7000-8000-00000000000b"));
        command.Parameters.AddWithValue("m3-exit:init:fixed-seed");
        Assert.Equal(
            "created",
            Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask InsertAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QuotaHotspotScenario scenario,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand create = new("""
                   SELECT disposition
                   FROM public.poolai_supply_create_account(
                       $1, 'openai', $2, 'https://example.test/v1',
                       '{"v":1,"alg":"A256GCM+A256GCM-v1","kid":"test-kek-v1",
                         "wrapped_dek":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                         "wrap_nonce":"AAAAAAAAAAAAAAAA","wrap_tag":"AAAAAAAAAAAAAAAAAAAAAA",
                         "ciphertext":"bTMtaG90c3BvdA","nonce":"AQEBAQEBAQEBAQEB",
                         "tag":"AgICAgICAgICAgICAgICAg"}'::jsonb,
                       'sk-m3-hotspot', NULL, 1, 0, 100
                   );
                   """, connection, transaction))
        {
            create.Parameters.AddWithValue(scenario.AccountId);
            create.Parameters.AddWithValue(scenario.AccountName);
            Assert.Equal(
                "created",
                Assert.IsType<string>(await create.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using NpgsqlCommand activate = new("""
            UPDATE public.accounts
            SET status = 'active',
                last_health_at = clock_timestamp(),
                last_health_status = 'healthy'
            WHERE id = $1;
            """, connection, transaction);
        activate.Parameters.AddWithValue(scenario.AccountId);
        Assert.Equal(1, await activate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertChannelAndConfigurationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QuotaHotspotScenario scenario,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand create = new("""
                   SELECT disposition
                   FROM public.poolai_supply_create_channel(
                       $1,
                       'openai',
                       $2,
                       '{"gpt-m3-exit":"gpt-m3-exit"}'::jsonb,
                       '{"responses":true,"chat_completions":true,
                         "function_tools":true,"streaming":true}'::jsonb
                   );
                   """, connection, transaction))
        {
            create.Parameters.AddWithValue(scenario.ChannelId);
            create.Parameters.AddWithValue(scenario.ChannelName);
            Assert.Equal(
                "created",
                Assert.IsType<string>(await create.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand activate = new("""
                   SELECT disposition
                   FROM public.poolai_supply_update_channel(
                       $1, 1, false, NULL, true, 'active',
                       false, NULL::jsonb, false, NULL::jsonb,
                       'M3 Exit hotspot fixture activation'
                   );
                   """, connection, transaction))
        {
            activate.Parameters.AddWithValue(scenario.ChannelId);
            Assert.Equal(
                "updated",
                Assert.IsType<string>(await activate.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using NpgsqlCommand configuration = new("""
            SELECT disposition
            FROM public.poolai_supply_create_group_configuration(
                $1,
                $2,
                ARRAY[$3]::uuid[],
                ARRAY[NULL::integer],
                ARRAY[NULL::integer],
                ARRAY[true]::boolean[]
            );
            """, connection, transaction);
        configuration.Parameters.AddWithValue(scenario.GroupId);
        configuration.Parameters.AddWithValue(scenario.ChannelId);
        configuration.Parameters.AddWithValue(scenario.AccountId);
        Assert.Equal(
            "created",
            Assert.IsType<string>(await configuration.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask ActivateGroupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QuotaHotspotScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new("""
            SELECT disposition
            FROM public.poolai_group_update(
                $1, 1, false, NULL, false, NULL,
                'active', 'M3 Exit hotspot fixture activation',
                $2, clock_timestamp()
            );
            """, connection, transaction);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.ReadinessToken);
        Assert.Equal(
            "updated",
            Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask InsertAccessGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QuotaHotspotScenario scenario,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand template = new("""
                   SELECT disposition
                   FROM public.poolai_subscription_template_create(
                       $1, $2, $3, NULL, 30
                   );
                   """, connection, transaction))
        {
            template.Parameters.AddWithValue(scenario.TemplateId);
            template.Parameters.AddWithValue(scenario.GroupId);
            template.Parameters.AddWithValue(scenario.TemplateName);
            Assert.Equal(
                "created",
                Assert.IsType<string>(await template.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand subscription = new("""
                   SELECT disposition
                   FROM public.poolai_subscription_assign(
                       $1, $2, $3,
                       clock_timestamp() - interval '1 minute',
                       clock_timestamp() + interval '1 day',
                       $2, 'M3 Exit hotspot fixture'
                   );
                   """, connection, transaction))
        {
            subscription.Parameters.AddWithValue(scenario.SubscriptionId);
            subscription.Parameters.AddWithValue(scenario.UserId);
            subscription.Parameters.AddWithValue(scenario.TemplateId);
            Assert.Equal(
                "created",
                Assert.IsType<string>(await subscription.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using NpgsqlCommand apiKey = new("""
            SELECT disposition
            FROM public.poolai_api_key_create(
                $1, $2, $3, 'M3 Exit hotspot key', $4, $5,
                1::smallint, NULL, '[]'::jsonb
            );
            """, connection, transaction);
        apiKey.Parameters.AddWithValue(scenario.ApiKeyId);
        apiKey.Parameters.AddWithValue(scenario.UserId);
        apiKey.Parameters.AddWithValue(scenario.GroupId);
        apiKey.Parameters.AddWithValue(scenario.KeyPrefix);
        apiKey.Parameters.AddWithValue(SHA256.HashData("m3-exit-fixed-api-key-digest"u8));
        Assert.Equal(
            "created",
            Assert.IsType<string>(await apiKey.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask<RuntimeConnections> ProvisionRuntimeRolesAsync(
        string administratorConnectionString,
        string migratorPassword,
        string apiPassword,
        string workerPassword,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(administratorConnectionString);
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await SetPasswordSettingAsync(
            connection,
            "poolai.test_migrator_password",
            migratorPassword,
            cancellationToken).ConfigureAwait(false);
        await SetPasswordSettingAsync(
            connection,
            "poolai.test_api_password",
            apiPassword,
            cancellationToken).ConfigureAwait(false);
        await SetPasswordSettingAsync(
            connection,
            "poolai.test_worker_password",
            workerPassword,
            cancellationToken).ConfigureAwait(false);

        using (NpgsqlCommand command = new("""
                   CREATE ROLE poolai_runtime_owner NOLOGIN
                       NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                   DO $roles$
                   BEGIN
                       EXECUTE pg_catalog.format(
                           'CREATE ROLE poolai_migrator LOGIN PASSWORD %L '
                           'NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
                           pg_catalog.current_setting('poolai.test_migrator_password'));
                       EXECUTE pg_catalog.format(
                           'CREATE ROLE poolai_api LOGIN PASSWORD %L '
                           'NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS',
                           pg_catalog.current_setting('poolai.test_api_password'));
                       EXECUTE pg_catalog.format(
                           'CREATE ROLE poolai_worker LOGIN PASSWORD %L '
                           'NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS',
                           pg_catalog.current_setting('poolai.test_worker_password'));
                   END;
                   $roles$;
                   GRANT poolai_runtime_owner TO poolai_migrator
                       WITH INHERIT FALSE, SET TRUE;
                   ALTER DATABASE poolai OWNER TO poolai_migrator;
                   REVOKE CREATE, TEMPORARY ON DATABASE poolai FROM PUBLIC;
                   GRANT CONNECT ON DATABASE poolai
                       TO poolai_migrator, poolai_api, poolai_worker;
                   """, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new RuntimeConnections(
            WithRole(administratorConnectionString, "poolai_migrator", migratorPassword),
            WithRole(administratorConnectionString, "poolai_api", apiPassword));
    }

    private static async ValueTask SetPasswordSettingAsync(
        NpgsqlConnection connection,
        string setting,
        string value,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(
            "SELECT pg_catalog.set_config($1, $2, false);",
            connection);
        command.Parameters.AddWithValue(setting);
        command.Parameters.AddWithValue(value);
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string WithRole(
        string administratorConnectionString,
        string role,
        string password) => new NpgsqlConnectionStringBuilder(administratorConnectionString)
        {
            Username = role,
            Password = password,
            ApplicationName = $"PoolAI.LoadTests.{role}",
        }.ConnectionString;

    private static string ReadPostgresImage()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string versionsPath = Path.Combine(directory.FullName, "eng", "versions.json");
            if (File.Exists(versionsPath))
            {
                using JsonDocument versions = JsonDocument.Parse(File.ReadAllText(versionsPath));
                string image = versions.RootElement
                    .GetProperty("containers")
                    .GetProperty("postgresql")
                    .GetString()
                    ?? throw new InvalidOperationException("The PostgreSQL image lock is missing.");
                string digest = versions.RootElement
                    .GetProperty("containerDigests")
                    .GetProperty("postgresql")
                    .GetString()
                    ?? throw new InvalidOperationException("The PostgreSQL digest lock is missing.");
                return $"{image}@{digest}";
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate eng/versions.json.");
    }

    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private sealed record RuntimeConnections(string Migrator, string Api);
}
