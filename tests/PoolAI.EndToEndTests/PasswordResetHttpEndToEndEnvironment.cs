extern alias PoolAiApi;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Database.Migrations;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Infrastructure.Security;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace PoolAI.EndToEndTests;

internal sealed class PasswordResetHttpEndToEndEnvironment : IAsyncDisposable
{
    internal const string OriginalPassword = "Original-Password-123!";
    private static readonly Guid UserRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000004");
    private PostgreSqlContainer? postgres;
    private RedisContainer? redis;
    private RealPasswordResetApiFactory? apiFactory;

    private PasswordResetHttpEndToEndEnvironment()
    {
    }

    internal NpgsqlDataSource AdministratorDataSource { get; private set; } = null!;

    internal HttpClient Client { get; private set; } = null!;

    internal Guid ActiveUserId { get; } = Guid.CreateVersion7();

    internal Guid DisabledUserId { get; } = Guid.CreateVersion7();

    internal string ActiveEmail { get; private set; } = string.Empty;

    internal string DisabledEmail { get; private set; } = string.Empty;

    internal string MissingEmail { get; private set; } = string.Empty;

    internal string MissingNormalizedEmail => MissingEmail.ToLowerInvariant();

    internal static async ValueTask<PasswordResetHttpEndToEndEnvironment> CreateAsync(
        CancellationToken cancellationToken)
    {
        PasswordResetHttpEndToEndEnvironment environment = new();
        try
        {
            await environment.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return environment;
        }
        catch
        {
            await environment.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<FactCounts> ReadFactCountsAsync(
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*) FROM public.one_time_tokens),
                (SELECT count(*) FROM public.email_outbox),
                (SELECT count(*) FROM public.audit_logs
                    WHERE action = 'identity.password_reset.requested'),
                (SELECT count(*) FROM public.outbox_messages
                    WHERE event_type = 'password_reset_requested'),
                (SELECT count(*) FROM public.idempotency_records);
            """);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new FactCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    internal async ValueTask<long> CountUsersByNormalizedEmailAsync(
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            SELECT count(*)
            FROM public.users
            WHERE normalized_email = $1;
            """);
        command.Parameters.AddWithValue(normalizedEmail);
        return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    internal async ValueTask<long> CountPasswordResetFactsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*) FROM public.one_time_tokens
                    WHERE user_id = $1 AND purpose = 'password_reset')
              + (SELECT count(*) FROM public.email_outbox WHERE user_id = $1);
            """);
        command.Parameters.AddWithValue(userId);
        return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    internal async ValueTask EnableTotpAsync(
        Guid userId,
        string base32Secret,
        CancellationToken cancellationToken)
    {
        ITotpSecretEnvelope envelope = apiFactory!.Services
            .GetRequiredService<ITotpSecretEnvelope>();
        JsonElement encrypted = envelope.Encrypt(
            base32Secret,
            TotpSecretEnvelopeTarget.User,
            new EntityId(userId));
        using NpgsqlCommand command = AdministratorDataSource.CreateCommand("""
            UPDATE public.users
            SET totp_secret_envelope = $2::jsonb,
                totp_last_accepted_step = NULL
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(encrypted.GetRawText());
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (apiFactory is not null)
        {
            await apiFactory.DisposeAsync().ConfigureAwait(false);
        }

        if (AdministratorDataSource is not null)
        {
            await AdministratorDataSource.DisposeAsync().ConfigureAwait(false);
        }

        if (postgres is not null)
        {
            await postgres.DisposeAsync().ConfigureAwait(false);
        }

        if (redis is not null)
        {
            await redis.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        ActiveEmail = $"active-{suffix}@poolai.test";
        DisabledEmail = $"disabled-{suffix}@poolai.test";
        MissingEmail = $"MISSING-{suffix}@POOLAI.TEST";

        string administratorPassword = SecretHex();
        postgres = new PostgreSqlBuilder(ReadContainerImage("postgresql"))
            .WithDatabase("poolai")
            .WithUsername("postgres")
            .WithPassword(administratorPassword)
            .Build();
        redis = new RedisBuilder(ReadContainerImage("redis")).Build();
        await Task.WhenAll(
            postgres.StartAsync(cancellationToken),
            redis.StartAsync(cancellationToken)).ConfigureAwait(false);

        RuntimeConnections connections = await ProvisionRuntimeRolesAsync(
            postgres.GetConnectionString(),
            cancellationToken).ConfigureAwait(false);
        MigrationCatalog catalog = await MigrationCatalog.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        await new PostgresMigrator(catalog).ApplyAsync(
            connections.Migrator,
            "PoolAI.EndToEndTests.password-reset-http",
            cancellationToken).ConfigureAwait(false);

        AdministratorDataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await InsertUsersAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string?> configuration = BuildApiConfiguration(
            connections.Api,
            redis.GetConnectionString(),
            suffix);
        apiFactory = new RealPasswordResetApiFactory(configuration);
        Client = apiFactory.CreateClient();
    }

    private async ValueTask InsertUsersAsync(CancellationToken cancellationToken)
    {
        VersionedPasswordHasher passwordHasher = new();
        await InsertUserAsync(
            ActiveUserId,
            ActiveEmail,
            "active",
            passwordHasher.Hash(OriginalPassword),
            cancellationToken).ConfigureAwait(false);
        await InsertUserAsync(
            DisabledUserId,
            DisabledEmail,
            "disabled",
            passwordHasher.Hash(OriginalPassword),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask InsertUserAsync(
        Guid userId,
        string email,
        string status,
        string passwordHash,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand user = AdministratorDataSource.CreateCommand("""
            INSERT INTO public.users (
                id, email, normalized_email, display_name, password_hash,
                security_stamp, status
            ) VALUES ($1, $2, lower($2), $3, $4, $5, $6);
            """);
        user.Parameters.AddWithValue(userId);
        user.Parameters.AddWithValue(email);
        user.Parameters.AddWithValue($"Password reset {status}");
        user.Parameters.AddWithValue(passwordHash);
        user.Parameters.AddWithValue(Guid.CreateVersion7());
        user.Parameters.AddWithValue(status);
        Assert.Equal(1, await user.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));

        using NpgsqlCommand role = AdministratorDataSource.CreateCommand("""
            INSERT INTO public.user_roles (user_id, role_id, assigned_by)
            VALUES ($1, $2, $1);
            """);
        role.Parameters.AddWithValue(userId);
        role.Parameters.AddWithValue(UserRoleId);
        Assert.Equal(1, await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static Dictionary<string, string?> BuildApiConfiguration(
        string postgresConnectionString,
        string redisConnectionString,
        string suffix)
    {
        string envelopeKey = SecretBase64();
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["App:PublicBaseUrl"] = "https://app.poolai.test",
            ["App:TimeZone"] = "Asia/Shanghai",
            ["App:AllowedHosts:0"] = "localhost",
            ["Cors:AllowedOrigins:0"] = "http://localhost",
            ["Auth:Jwt:SigningKey"] = SecretBase64(),
            ["Auth:RefreshToken:CurrentPepperVersion"] = "7",
            ["Auth:RefreshToken:CurrentPepper"] = SecretBase64(),
            ["Auth:PasswordReset:RateLimitScopePepper"] = SecretBase64(),
            ["Auth:TokenHash:CurrentPepperVersion"] = "7",
            ["Auth:TokenHash:CurrentPepper"] = SecretBase64(),
            ["Auth:TOTP:RecoveryCodePepperVersion"] = "7",
            ["Auth:TOTP:RecoveryCodePepper"] = SecretBase64(),
            ["Auth:Login:IpFailuresPerMinute"] = "20",
            ["Auth:Login:RateLimitScopePepper"] = SecretBase64(),
            ["ApiKeys:CurrentPepper"] = SecretBase64(),
            ["Idempotency:RequestHashPepper"] = SecretBase64(),
            ["Data:Postgres:ConnectionString"] = postgresConnectionString,
            ["Data:Redis:ConnectionString"] = redisConnectionString,
            ["Data:Redis:KeyPrefix"] = $"poolai:r1:e2e-{suffix}:",
            ["Email:Smtp:Host"] = "localhost",
            ["Email:Smtp:Security"] = "starttls",
            ["Email:FromAddress"] = "no-reply@poolai.test",
            ["Secrets:Envelope:CurrentKeyId"] = "email-e2e-k1",
            ["Secrets:Envelope:CurrentKey"] = envelopeKey,
            ["Secrets:Envelope:DecryptKeyRing:email-e2e-k1"] = envelopeKey,
            ["Health:Ntp:Server"] = "127.0.0.1",
        };
    }

    private static async ValueTask<RuntimeConnections> ProvisionRuntimeRolesAsync(
        string administratorConnectionString,
        CancellationToken cancellationToken)
    {
        RuntimePasswords passwords = new(SecretHex(), SecretHex(), SecretHex());
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(administratorConnectionString);
        using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await SetPasswordSettingsAsync(connection, passwords, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await CreateRuntimeRolesAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await SetPasswordSettingsAsync(
                connection,
                new RuntimePasswords(string.Empty, string.Empty, string.Empty),
                CancellationToken.None).ConfigureAwait(false);
        }

        return new RuntimeConnections(
            WithRole(administratorConnectionString, "poolai_migrator", passwords.Migrator),
            WithRole(administratorConnectionString, "poolai_api", passwords.Api),
            WithRole(administratorConnectionString, "poolai_worker", passwords.Worker));
    }

    private static async ValueTask SetPasswordSettingsAsync(
        NpgsqlConnection connection,
        RuntimePasswords passwords,
        CancellationToken cancellationToken)
    {
        foreach ((string setting, string value) in new[]
                 {
                     ("poolai.test_migrator_password", passwords.Migrator),
                     ("poolai.test_api_password", passwords.Api),
                     ("poolai.test_worker_password", passwords.Worker),
                 })
        {
            using NpgsqlCommand command = new(
                "SELECT pg_catalog.set_config($1, $2, false);",
                connection);
            command.Parameters.AddWithValue(setting);
            command.Parameters.AddWithValue(value);
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
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
            GRANT poolai_runtime_owner TO poolai_migrator WITH INHERIT FALSE, SET TRUE;
            ALTER DATABASE poolai OWNER TO poolai_migrator;
            REVOKE CREATE, TEMPORARY ON DATABASE poolai FROM PUBLIC;
            GRANT CONNECT ON DATABASE poolai TO poolai_migrator, poolai_api, poolai_worker;
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string WithRole(
        string administratorConnectionString,
        string role,
        string password) => new NpgsqlConnectionStringBuilder(administratorConnectionString)
        {
            Username = role,
            Password = password,
            ApplicationName = $"PoolAI.EndToEndTests.{role}",
        }.ConnectionString;

    private static string ReadContainerImage(string name)
    {
        using JsonDocument versions = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "eng", "versions.json")));
        JsonElement root = versions.RootElement;
        string image = root.GetProperty("containers").GetProperty(name).GetString()
            ?? throw new InvalidOperationException("The container image lock is missing.");
        string digest = root.GetProperty("containerDigests").GetProperty(name).GetString()
            ?? throw new InvalidOperationException("The container digest lock is missing.");
        return $"{image}@{digest}";
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "eng", "versions.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The PoolAI repository root was not found.");
    }

    private static string SecretHex() => Convert.ToHexString(
        RandomNumberGenerator.GetBytes(24));

    private static string SecretBase64() => Convert.ToBase64String(
        RandomNumberGenerator.GetBytes(32));

    internal sealed record FactCounts(
        long Tokens,
        long Emails,
        long Audits,
        long Events,
        long IdempotencyRecords);

    private sealed class RealPasswordResetApiFactory(
        IReadOnlyDictionary<string, string?> configurationValues)
        : WebApplicationFactory<PoolAiApi::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            foreach ((string key, string? value) in configurationValues)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(configurationValues));
        }
    }

    private sealed record RuntimePasswords(string Migrator, string Api, string Worker);

    private sealed record RuntimeConnections(string Migrator, string Api, string Worker);
}
