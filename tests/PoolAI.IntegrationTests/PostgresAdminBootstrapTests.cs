using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using PoolAI.Database.Migrations;
using Testcontainers.PostgreSql;

namespace PoolAI.IntegrationTests;

public sealed class PostgresAdminBootstrapTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ConcurrentBootstrapCreatesOneAdministratorAndConsumesTheWinningToken()
    {
        string postgresPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        string migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        PostgreSqlContainer container = new PostgreSqlBuilder(
            PostgresMigrationTests.ReadPostgresImage())
            .WithDatabase("poolai")
            .WithUsername("postgres")
            .WithPassword(postgresPassword)
            .Build();
        await using ConfiguredAsyncDisposable containerLease = container.ConfigureAwait(true);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await container.StartAsync(cancellationToken).ConfigureAwait(true);
        BootstrapDatabaseConnections connections = await ProvisionDatabaseAsync(
            container,
            migratorPassword,
            cancellationToken).ConfigureAwait(true);

        AdminBootstrapRequest[] requests =
        [
            CreateRequest(
                "first-admin@example.test",
                "First Administrator",
                "bootstrap-password-first-42",
                "bootstrap-token-first-0123456789abcdef-42"),
            CreateRequest(
                "second-admin@example.test",
                "Second Administrator",
                "bootstrap-password-second-42",
                "bootstrap-token-second-0123456789abcdef-42"),
        ];
        AdminBootstrapWriter writer = new();
        BootstrapAttempt success = await RunConcurrentBootstrapAsync(
            writer,
            connections.Migrator,
            requests,
            cancellationToken).ConfigureAwait(true);
        BootstrapDatabaseState state = await ReadStateAsync(
            connections.Administrator,
            cancellationToken).ConfigureAwait(true);
        string tokenHash = AssertIdentityAndAudit(success, state);
        BootstrapOutboxEvent outbox = await ReadOutboxAsync(
            connections.Administrator,
            cancellationToken).ConfigureAwait(true);
        AssertOutbox(success, state, outbox, tokenHash);
        await AssertReplayRejectedAsync(
            writer,
            connections,
            success,
            state,
            cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask<BootstrapDatabaseConnections> ProvisionDatabaseAsync(
        PostgreSqlContainer container,
        string migratorPassword,
        CancellationToken cancellationToken)
    {
        string administratorConnectionString = container.GetConnectionString();
        await PostgresMigrationTests.ProvisionRuntimeRolesAsync(
            administratorConnectionString,
            cancellationToken).ConfigureAwait(false);
        string migratorConnectionString = await PostgresMigrationTests
            .ProvisionComposeMigratorAsync(
                administratorConnectionString,
                migratorPassword,
                cancellationToken)
            .ConfigureAwait(false);
        MigrationCatalog catalog = await MigrationCatalog
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        await new PostgresMigrator(catalog).ApplyAsync(
            migratorConnectionString,
            "PoolAI.IntegrationTests.admin-bootstrap",
            cancellationToken).ConfigureAwait(false);
        return new BootstrapDatabaseConnections(
            administratorConnectionString,
            migratorConnectionString);
    }

    private static async ValueTask<BootstrapAttempt> RunConcurrentBootstrapAsync(
        AdminBootstrapWriter writer,
        string connectionString,
        AdminBootstrapRequest[] requests,
        CancellationToken cancellationToken)
    {
        BootstrapAttempt[] attempts = await Task.WhenAll(
            RunBootstrapAsync(writer, connectionString, requests[0], cancellationToken),
            RunBootstrapAsync(writer, connectionString, requests[1], cancellationToken))
            .ConfigureAwait(false);
        BootstrapAttempt failure = Assert.Single(
            attempts,
            attempt => attempt.Failure is not null);
        Assert.Equal(AdminBootstrapFailure.DatabaseNotEmpty, failure.Failure!.Failure);
        return Assert.Single(attempts, attempt => attempt.Result is not null);
    }

    private static string AssertIdentityAndAudit(
        BootstrapAttempt success,
        BootstrapDatabaseState state)
    {
        AdminBootstrapResult result = success.Result!;
        Assert.Equal('7', result.UserId.ToString()[14]);
        Assert.Equal(1L, state.UserCount);
        Assert.Equal(1L, state.AdminCount);
        Assert.Equal(1L, state.BootstrapAuditCount);
        Assert.Equal(1L, state.BootstrapOutboxCount);
        Assert.Equal(result.UserId, state.UserId);
        Assert.Equal(success.Request.Email, state.Email);
        Assert.Equal(success.Request.NormalizedEmail, state.NormalizedEmail);
        AssertPasswordHash(state.PasswordHash, success.Request.Secrets.Password);

        string tokenHash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(success.Request.Secrets.BootstrapToken)));
        Assert.Contains(tokenHash, state.AuditJson, StringComparison.Ordinal);
        Assert.Contains("poolai-password-v1", state.AuditJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            success.Request.Secrets.Password,
            state.AuditJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            success.Request.Secrets.BootstrapToken,
            state.AuditJson,
            StringComparison.Ordinal);
        return tokenHash;
    }

    private static void AssertOutbox(
        BootstrapAttempt success,
        BootstrapDatabaseState state,
        BootstrapOutboxEvent outbox,
        string tokenHash)
    {
        Guid userId = success.Result!.UserId;
        Assert.Equal("poolai.identity.v1", outbox.Topic);
        Assert.Equal(1, outbox.SchemaVersion);
        Assert.Equal("user", outbox.AggregateType);
        Assert.Equal(userId, outbox.AggregateId);
        Assert.Equal(state.UserVersion, outbox.AggregateVersion);
        Assert.Equal("user_created", outbox.EventType);
        Assert.True(outbox.SourceEventSequenceIsNull);
        Assert.Equal(outbox.MessageId, outbox.PayloadEventId);
        Assert.Equal(userId, outbox.PayloadUserId);
        Assert.Equal(state.UserVersion, outbox.PayloadVersion);
        Assert.Equal("bootstrap_cli", outbox.Origin);
        Assert.Equal(
            $"identity:user_created:{userId}:v{state.UserVersion}",
            outbox.DeduplicationKey);
        Assert.DoesNotContain(success.Request.Email, outbox.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(success.Request.DisplayName, outbox.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(success.Request.Secrets.Password, outbox.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            success.Request.Secrets.BootstrapToken,
            outbox.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(tokenHash, outbox.Json, StringComparison.Ordinal);
    }

    private static async ValueTask AssertReplayRejectedAsync(
        AdminBootstrapWriter writer,
        BootstrapDatabaseConnections connections,
        BootstrapAttempt success,
        BootstrapDatabaseState state,
        CancellationToken cancellationToken)
    {
        AdminBootstrapException replay = await Assert.ThrowsAsync<AdminBootstrapException>(() =>
            writer.CreateAsync(
                connections.Migrator,
                success.Request,
                cancellationToken).AsTask()).ConfigureAwait(false);
        Assert.Equal(AdminBootstrapFailure.TokenAlreadyConsumed, replay.Failure);
        BootstrapDatabaseState afterReplay = await ReadStateAsync(
            connections.Administrator,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(state, afterReplay);
    }

    private static AdminBootstrapRequest CreateRequest(
        string email,
        string displayName,
        string password,
        string bootstrapToken) =>
        new(email, displayName, new AdminBootstrapSecrets(password, bootstrapToken));

    private static async Task<BootstrapAttempt> RunBootstrapAsync(
        AdminBootstrapWriter writer,
        string connectionString,
        AdminBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            AdminBootstrapResult result = await writer
                .CreateAsync(connectionString, request, cancellationToken)
                .ConfigureAwait(false);
            return new BootstrapAttempt(request, result, null);
        }
        catch (AdminBootstrapException exception)
        {
            return new BootstrapAttempt(request, null, exception);
        }
    }

    private static async ValueTask<BootstrapDatabaseState> ReadStateAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT (SELECT count(*) FROM public.users),
                   (
                       SELECT count(*)
                       FROM public.user_roles AS user_role
                       JOIN public.roles AS role ON role.id = user_role.role_id
                       WHERE role.code = 'admin'
                   ),
                   (
                       SELECT count(*)
                       FROM public.audit_logs
                       WHERE action = 'identity.bootstrap_admin.created'
                   ),
                   (
                       SELECT count(*)
                       FROM public.outbox_messages
                       WHERE topic = 'poolai.identity.v1'
                         AND event_type = 'user_created'
                   ),
                   user_record.id,
                   user_record.email,
                   user_record.normalized_email,
                   user_record.password_hash,
                   user_record.version,
                   (
                       SELECT pg_catalog.row_to_json(audit_record)::text
                       FROM public.audit_logs AS audit_record
                       WHERE audit_record.action = 'identity.bootstrap_admin.created'
                   )
            FROM public.users AS user_record;
            """;

        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        BootstrapDatabaseState state = new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetGuid(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8),
            reader.GetString(9));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return state;
    }

    private static async ValueTask<BootstrapOutboxEvent> ReadOutboxAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT id,
                   deduplication_key,
                   topic,
                   schema_version,
                   aggregate_type,
                   aggregate_id,
                   aggregate_version,
                   event_type,
                   source_event_sequence IS NULL,
                   (payload ->> 'event_id')::uuid,
                   (payload ->> 'user_id')::uuid,
                   (payload ->> 'version')::bigint,
                   payload ->> 'origin',
                   pg_catalog.row_to_json(outbox_record)::text
            FROM public.outbox_messages AS outbox_record
            WHERE topic = 'poolai.identity.v1'
              AND event_type = 'user_created';
            """;

        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlCommand command = dataSource.CreateCommand(Sql);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        BootstrapOutboxEvent integrationEvent = new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetGuid(5),
            reader.GetInt64(6),
            reader.GetString(7),
            reader.GetBoolean(8),
            reader.GetGuid(9),
            reader.GetGuid(10),
            reader.GetInt64(11),
            reader.GetString(12),
            reader.GetString(13));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return integrationEvent;
    }

    private static void AssertPasswordHash(string passwordHash, string password)
    {
        Assert.StartsWith(
            AdminBootstrapWriter.PasswordHashPrefix,
            passwordHash,
            StringComparison.Ordinal);
        string identityHash = passwordHash[AdminBootstrapWriter.PasswordHashPrefix.Length..];
        byte[] payload = Convert.FromBase64String(identityHash);
        Assert.Equal(61, payload.Length);
        Assert.Equal(0x01, payload[0]);
        Assert.Equal(2, checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(1, 4))));
        Assert.Equal(
            AdminBootstrapWriter.PasswordHashIterations,
            checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(5, 4))));
        Assert.Equal(16, checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(9, 4))));
        Assert.DoesNotContain(password, passwordHash, StringComparison.Ordinal);
    }

    private sealed record BootstrapAttempt(
        AdminBootstrapRequest Request,
        AdminBootstrapResult? Result,
        AdminBootstrapException? Failure);

    private sealed record BootstrapDatabaseConnections(
        string Administrator,
        string Migrator);

    private sealed record BootstrapDatabaseState(
        long UserCount,
        long AdminCount,
        long BootstrapAuditCount,
        long BootstrapOutboxCount,
        Guid UserId,
        string Email,
        string NormalizedEmail,
        string PasswordHash,
        long UserVersion,
        string AuditJson);

    private sealed record BootstrapOutboxEvent(
        Guid MessageId,
        string DeduplicationKey,
        string Topic,
        int SchemaVersion,
        string AggregateType,
        Guid AggregateId,
        long AggregateVersion,
        string EventType,
        bool SourceEventSequenceIsNull,
        Guid PayloadEventId,
        Guid PayloadUserId,
        long PayloadVersion,
        string Origin,
        string Json);
}
