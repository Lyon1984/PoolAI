using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.Database.Migrations;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Identity;
using PoolAI.Modules.Operations;
using PoolAI.Modules.Usage;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace PoolAI.IntegrationTests;

public sealed class PostgresRuntimeFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private RedisContainer? _redisContainer;

    public ServiceProvider ApiServices { get; private set; } = null!;

    public ServiceProvider WorkerServices { get; private set; } = null!;

    public NpgsqlDataSource AdministratorDataSource { get; private set; } = null!;

    public string RedisConnectionString { get; private set; } = null!;

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
        _redisContainer = new RedisBuilder(ReadRedisImage()).Build();

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await Task.WhenAll(
            _container.StartAsync(cancellationToken),
            _redisContainer.StartAsync(cancellationToken)).ConfigureAwait(true);
        string administratorConnectionString = _container.GetConnectionString();
        RedisConnectionString = _redisContainer.GetConnectionString();
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
            "PoolAI.IntegrationTests.runtime-fixture",
            cancellationToken).ConfigureAwait(true);

        AdministratorDataSource = NpgsqlDataSource.Create(administratorConnectionString);
        ApiServices = BuildApiServices(connections.Api, RedisConnectionString);
        WorkerServices = BuildWorkerServices(connections.Worker, RedisConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (ApiServices is not null)
        {
            await ApiServices.DisposeAsync().ConfigureAwait(true);
        }

        if (WorkerServices is not null)
        {
            await WorkerServices.DisposeAsync().ConfigureAwait(true);
        }

        if (AdministratorDataSource is not null)
        {
            await AdministratorDataSource.DisposeAsync().ConfigureAwait(true);
        }

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(true);
        }

        if (_redisContainer is not null)
        {
            await _redisContainer.DisposeAsync().ConfigureAwait(true);
        }
    }

    public async ValueTask ForceOutboxDueAsync(
        Guid messageId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            UPDATE public.outbox_messages
            SET next_attempt_at = clock_timestamp() - interval '1 second'
            WHERE id = $1 AND status = 'pending';
            """);
        command.Parameters.AddWithValue(messageId);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }

    public async ValueTask ForceOutboxLeaseExpiredAsync(
        Guid messageId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            UPDATE public.outbox_messages
            SET locked_until = clock_timestamp() - interval '1 second'
            WHERE id = $1 AND status = 'processing';
            """);
        command.Parameters.AddWithValue(messageId);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }

    public async ValueTask SetOutboxNotDueAsync(
        Guid messageId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            UPDATE public.outbox_messages
            SET next_attempt_at = clock_timestamp() + interval '1 hour'
            WHERE id = $1 AND status = 'pending';
            """);
        command.Parameters.AddWithValue(messageId);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }

    public async ValueTask DeferOtherPendingOutboxAsync(
        Guid[] messageIds,
        CancellationToken cancellationToken)
    {
        if (messageIds.Length == 0)
        {
            throw new ArgumentException("At least one message id is required.", nameof(messageIds));
        }

        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            UPDATE public.outbox_messages
            SET next_attempt_at = clock_timestamp() + interval '1 hour'
            WHERE status = 'pending' AND id <> ALL($1);
            """);
        command.Parameters.AddWithValue(messageIds);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
    }

    public async ValueTask DeferOtherPendingEmailOutboxAsync(
        Guid emailId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            UPDATE public.email_outbox
            SET next_attempt_at = clock_timestamp() + interval '1 hour'
            WHERE status = 'pending' AND id <> $1;
            """);
        command.Parameters.AddWithValue(emailId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true);
    }

    private static ServiceProvider BuildApiServices(
        string connectionString,
        string redisConnectionString)
    {
        ConfigurationManager configuration = Configuration(
            connectionString,
            redisConnectionString);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPoolAiPostgresRuntime(connectionString);
        services.AddOperationsModule(configuration, "Integration");
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static ServiceProvider BuildWorkerServices(
        string connectionString,
        string redisConnectionString)
    {
        ConfigurationManager configuration = Configuration(
            connectionString,
            redisConnectionString);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPoolAiPostgresRuntime(connectionString);
        services.AddOperationsModule(configuration, "Integration");
        services.AddIdentityModule();
        services.AddUsageModule();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static ConfigurationManager Configuration(
        string connectionString,
        string redisConnectionString)
    {
        ConfigurationManager configuration = new();
        configuration["Data:Postgres:ConnectionString"] = connectionString;
        configuration["Data:Redis:ConnectionString"] = redisConnectionString;
        configuration["Data:Redis:KeyPrefix"] = "poolai:r1:integration:";
        configuration["Health:Ntp:Server"] = "127.0.0.1";
        configuration["Health:Ntp:Port"] = "123";
        return configuration;
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
            .ConfigureAwait(true);
        await SetPasswordSettingAsync(
            connection,
            "poolai.test_migrator_password",
            migratorPassword,
            cancellationToken).ConfigureAwait(true);
        await SetPasswordSettingAsync(
            connection,
            "poolai.test_api_password",
            apiPassword,
            cancellationToken).ConfigureAwait(true);
        await SetPasswordSettingAsync(
            connection,
            "poolai.test_worker_password",
            workerPassword,
            cancellationToken).ConfigureAwait(true);

        try
        {
            await CreateRuntimeRolesAsync(connection, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            await SetPasswordSettingAsync(
                connection,
                "poolai.test_migrator_password",
                string.Empty,
                CancellationToken.None).ConfigureAwait(true);
            await SetPasswordSettingAsync(
                connection,
                "poolai.test_api_password",
                string.Empty,
                CancellationToken.None).ConfigureAwait(true);
            await SetPasswordSettingAsync(
                connection,
                "poolai.test_worker_password",
                string.Empty,
                CancellationToken.None).ConfigureAwait(true);
        }

        return new RuntimeConnections(
            WithRole(administratorConnectionString, "poolai_migrator", migratorPassword),
            WithRole(administratorConnectionString, "poolai_api", apiPassword),
            WithRole(administratorConnectionString, "poolai_worker", workerPassword));
    }

    private static async ValueTask CreateRuntimeRolesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new("""
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
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(true);
    }

    private static string WithRole(
        string administratorConnectionString,
        string role,
        string password) => new NpgsqlConnectionStringBuilder(administratorConnectionString)
        {
            Username = role,
            Password = password,
            ApplicationName = $"PoolAI.IntegrationTests.{role}",
        }.ConnectionString;

    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private static string ReadPostgresImage()
    {
        string root = MigrationCatalogTests.FindRepositoryRoot();
        using JsonDocument versions = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eng", "versions.json")));
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

    private static string ReadRedisImage()
    {
        string root = MigrationCatalogTests.FindRepositoryRoot();
        using JsonDocument versions = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eng", "versions.json")));
        string image = versions.RootElement
            .GetProperty("containers")
            .GetProperty("redis")
            .GetString()
            ?? throw new InvalidOperationException("The Redis image lock is missing.");
        string digest = versions.RootElement
            .GetProperty("containerDigests")
            .GetProperty("redis")
            .GetString()
            ?? throw new InvalidOperationException("The Redis digest lock is missing.");
        return $"{image}@{digest}";
    }

    private sealed record RuntimeConnections(string Migrator, string Api, string Worker);
}
