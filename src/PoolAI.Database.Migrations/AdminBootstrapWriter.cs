using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;

namespace PoolAI.Database.Migrations;

public sealed class AdminBootstrapWriter
{
    public const string PasswordHashPrefix = "poolai-password-v1:";
    public const int PasswordHashIterations = 100_000;

    private const long AdvisoryLockId = 0x504F4F4C4941444D;

    private readonly PasswordHasher<object> _passwordHasher = new(Options.Create(
        new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = PasswordHashIterations,
        }));

    private const string AcquireLockSql = "SELECT pg_catalog.pg_advisory_xact_lock($1);";

    private const string LockIdentityTablesSql = """
        LOCK TABLE public.users IN SHARE ROW EXCLUSIVE MODE;
        LOCK TABLE public.user_roles IN SHARE ROW EXCLUSIVE MODE;
        """;

    private const string TokenConsumedSql = """
        SELECT EXISTS (
            SELECT 1
            FROM public.audit_logs
            WHERE action = 'identity.bootstrap_admin.created'
              AND metadata ->> 'bootstrap_token_sha256' = $1
        );
        """;

    private const string EligibilitySql = """
        SELECT EXISTS (SELECT 1 FROM public.users),
               EXISTS (
                   SELECT 1
                   FROM public.user_roles AS user_role
                   JOIN public.roles AS role ON role.id = user_role.role_id
                   WHERE role.code = 'admin'
               );
        """;

    private const string InsertUserSql = """
        INSERT INTO public.users (
            id,
            email,
            normalized_email,
            display_name,
            password_hash,
            status,
            security_stamp
        ) VALUES ($1, $2, $3, $4, $5, 'active', $6);
        """;

    private const string InsertAdminRoleSql = """
        INSERT INTO public.user_roles (user_id, role_id)
        SELECT $1, role.id
        FROM public.roles AS role
        WHERE role.code = 'admin';
        """;

    private const string InsertAuditSql = """
        INSERT INTO public.audit_logs (
            id,
            actor_type,
            action,
            target_type,
            target_id,
            reason,
            after_state,
            metadata
        )
        SELECT $1,
               'system',
               'identity.bootstrap_admin.created',
               'user',
               user_record.id,
               'initial_admin_bootstrap',
               pg_catalog.jsonb_build_object(
                   'status', user_record.status,
                   'role', 'admin',
                   'version', user_record.version,
                   'token_version', user_record.token_version
               ),
               pg_catalog.jsonb_build_object(
                   'bootstrap_token_sha256', $2,
                   'password_hash_format', 'poolai-password-v1'
               )
        FROM public.users AS user_record
        WHERE user_record.id = $3;
        """;

    private const string InsertOutboxSql = """
        INSERT INTO public.outbox_messages (
            id,
            deduplication_key,
            topic,
            schema_version,
            aggregate_type,
            aggregate_id,
            aggregate_version,
            event_type,
            source_event_sequence,
            correlation_id,
            causation_id,
            payload
        )
        SELECT $1,
               pg_catalog.format(
                   'identity:user_created:%s:v%s',
                   user_record.id,
                   user_record.version
               ),
               'poolai.identity.v1',
               1,
               'user',
               user_record.id,
               user_record.version,
               'user_created',
               NULL,
               $2,
               NULL,
               pg_catalog.jsonb_build_object(
                   'schema_version', 1,
                   'event_id', $1,
                   'event_type', 'user_created',
                   'user_id', user_record.id,
                   'role', 'admin',
                   'status', user_record.status,
                   'version', user_record.version,
                   'origin', 'bootstrap_cli'
               )
        FROM public.users AS user_record
        WHERE user_record.id = $3;
        """;

    public async ValueTask<AdminBootstrapResult> CreateAsync(
        string connectionString,
        AdminBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);

        Guid userId = Guid.CreateVersion7();
        Guid securityStamp = Guid.CreateVersion7();
        Guid auditId = Guid.CreateVersion7();
        Guid outboxMessageId = Guid.CreateVersion7();
        Guid correlationId = Guid.CreateVersion7();
        string passwordHash = string.Concat(
            PasswordHashPrefix,
            _passwordHasher.HashPassword(new object(), request.Secrets.Password));
        string bootstrapTokenHash = HashBootstrapToken(request.Secrets.BootstrapToken);

        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        await AcquireLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockIdentityTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureTokenNotConsumedAsync(
            connection,
            transaction,
            bootstrapTokenHash,
            cancellationToken).ConfigureAwait(false);
        await EnsureEligibleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await InsertUserAsync(
            connection,
            transaction,
            userId,
            securityStamp,
            request,
            passwordHash,
            cancellationToken).ConfigureAwait(false);
        await InsertAdminRoleAsync(
            connection,
            transaction,
            userId,
            cancellationToken).ConfigureAwait(false);
        await InsertAuditAsync(
            connection,
            transaction,
            auditId,
            userId,
            bootstrapTokenHash,
            cancellationToken).ConfigureAwait(false);
        await InsertOutboxAsync(
            connection,
            transaction,
            outboxMessageId,
            correlationId,
            userId,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AdminBootstrapResult(userId);
    }

    private static async ValueTask AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(AcquireLockSql, connection, transaction);
        command.Parameters.AddWithValue(AdvisoryLockId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask LockIdentityTablesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(LockIdentityTablesSql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureTokenNotConsumedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string bootstrapTokenHash,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(TokenConsumedSql, connection, transaction);
        command.Parameters.AddWithValue(bootstrapTokenHash);
        object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (scalar is true)
        {
            throw new AdminBootstrapException(AdminBootstrapFailure.TokenAlreadyConsumed);
        }
    }

    private static async ValueTask EnsureEligibleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(EligibilitySql, connection, transaction);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Admin bootstrap eligibility could not be read.");
        }

        bool hasUsers = reader.GetBoolean(0);
        bool hasAdmin = reader.GetBoolean(1);
        if (hasUsers || hasAdmin)
        {
            throw new AdminBootstrapException(AdminBootstrapFailure.DatabaseNotEmpty);
        }
    }

    private static async ValueTask InsertUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        Guid securityStamp,
        AdminBootstrapRequest request,
        string passwordHash,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(InsertUserSql, connection, transaction);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(request.Email);
        command.Parameters.AddWithValue(request.NormalizedEmail);
        command.Parameters.AddWithValue(request.DisplayName);
        command.Parameters.AddWithValue(passwordHash);
        command.Parameters.AddWithValue(securityStamp);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException("The bootstrap administrator could not be created.");
        }
    }

    private static async ValueTask InsertAdminRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(InsertAdminRoleSql, connection, transaction);
        command.Parameters.AddWithValue(userId);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new AdminBootstrapException(AdminBootstrapFailure.AdminRoleMissing);
        }
    }

    private static async ValueTask InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid auditId,
        Guid userId,
        string bootstrapTokenHash,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(InsertAuditSql, connection, transaction);
        command.Parameters.AddWithValue(auditId);
        command.Parameters.AddWithValue(bootstrapTokenHash);
        command.Parameters.AddWithValue(userId);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException("The bootstrap audit record could not be created.");
        }
    }

    private static async ValueTask InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid messageId,
        Guid correlationId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(InsertOutboxSql, connection, transaction);
        command.Parameters.AddWithValue(messageId);
        command.Parameters.AddWithValue(correlationId);
        command.Parameters.AddWithValue(userId);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                "The bootstrap integration event could not be created.");
        }
    }

    private static string HashBootstrapToken(string bootstrapToken)
    {
        byte[] tokenBytes = Encoding.UTF8.GetBytes(bootstrapToken);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(tokenBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }
}
