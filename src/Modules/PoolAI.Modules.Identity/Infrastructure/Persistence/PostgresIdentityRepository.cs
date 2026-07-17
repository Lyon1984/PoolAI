#pragma warning disable MA0051 // SQL mutation methods keep lock order and CAS predicates together.
using Npgsql;
using NpgsqlTypes;
using System.Runtime.CompilerServices;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;

namespace PoolAI.Modules.Identity.Infrastructure.Persistence;

internal sealed class PostgresIdentityRepository : IIdentityRepository
{
    private static readonly Guid AdminRoleId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid OperatorRoleId = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid AuditorRoleId = Guid.Parse("01900000-0000-7000-8000-000000000003");
    private static readonly Guid UserRoleId = Guid.Parse("01900000-0000-7000-8000-000000000004");

    private const string SelectColumns = """
        u.id,
        u.email,
        u.normalized_email,
        u.display_name,
        u.password_hash,
        r.code,
        u.status,
        u.token_version,
        u.version,
        u.created_at,
        u.updated_at
        """;

    private static readonly string GetSql = $"""
        SELECT {SelectColumns}
        FROM public.users AS u
        JOIN public.user_roles AS ur ON ur.user_id = u.id
        JOIN public.roles AS r ON r.id = ur.role_id
        WHERE u.id = $1;
        """;

    private static readonly string GetForUpdateSql = $"""
        SELECT {SelectColumns}
        FROM public.users AS u
        JOIN public.user_roles AS ur ON ur.user_id = u.id
        JOIN public.roles AS r ON r.id = ur.role_id
        WHERE u.id = $1
        FOR UPDATE OF u;
        """;

    private static readonly string FindByEmailSql = $"""
        SELECT {SelectColumns}
        FROM public.users AS u
        JOIN public.user_roles AS ur ON ur.user_id = u.id
        JOIN public.roles AS r ON r.id = ur.role_id
        WHERE u.normalized_email = $1
        FOR UPDATE OF u;
        """;

    private static readonly string ListFirstSql = $"""
        SELECT {SelectColumns}
        FROM public.users AS u
        JOIN public.user_roles AS ur ON ur.user_id = u.id
        JOIN public.roles AS r ON r.id = ur.role_id
        ORDER BY u.created_at DESC, u.id DESC
        LIMIT $1;
        """;

    private static readonly string ListAfterSql = $"""
        SELECT {SelectColumns}
        FROM public.users AS u
        JOIN public.user_roles AS ur ON ur.user_id = u.id
        JOIN public.roles AS r ON r.id = ur.role_id
        WHERE u.created_at < $1
           OR (u.created_at = $1 AND u.id < $2)
        ORDER BY u.created_at DESC, u.id DESC
        LIMIT $3;
        """;

    private const string InsertUserSql = """
        INSERT INTO public.users (
            id, email, normalized_email, display_name, password_hash,
            status, security_stamp, token_version, version
        ) VALUES (
            $1, $2, $3, $4, $5, 'active', $6, 1, 1
        )
        ON CONFLICT (normalized_email) DO NOTHING
        RETURNING id;
        """;

    private const string InsertRoleSql = """
        INSERT INTO public.user_roles (user_id, role_id, assigned_by)
        VALUES ($1, $2, $3);
        """;

    private const string UpdateUserSql = """
        SELECT disposition, was_changed
        FROM public.poolai_identity_update_user($1, $2, $3, $4, $5, $6);
        """;

    private const string RevokeOpenPasswordResetTokensSql = """
        UPDATE public.one_time_tokens
        SET revoked_at = clock_timestamp(),
            revoke_reason = 'superseded'
        WHERE user_id = $1
          AND purpose = 'password_reset'
          AND used_at IS NULL
          AND revoked_at IS NULL;
        """;

    private const string RevokePasswordResetTokensForDisabledUserSql = """
        UPDATE public.one_time_tokens
        SET revoked_at = clock_timestamp(),
            revoke_reason = 'user_disabled'
        WHERE user_id = $1
          AND purpose = 'password_reset'
          AND used_at IS NULL
          AND revoked_at IS NULL;
        """;

    private const string RevokeTotpChallengesForDisabledUserSql = """
        UPDATE public.one_time_tokens
        SET revoked_at = clock_timestamp(),
            revoke_reason = 'user_disabled'
        WHERE user_id = $1
          AND purpose = 'totp_challenge'
          AND used_at IS NULL
          AND revoked_at IS NULL;
        """;

    private const string RevokeRefreshSessionsForDisabledUserSql = """
        UPDATE public.refresh_sessions
        SET status = 'revoked',
            revoked_at = clock_timestamp(),
            revoke_reason = 'user_disabled'
        WHERE user_id = $1
          AND status = 'active';
        """;

    private const string InsertPasswordResetTokenSql = """
        INSERT INTO public.one_time_tokens (
            id, user_id, purpose, token_hash, pepper_version, expires_at
        ) VALUES (
            $1, $2, 'password_reset', $3, $4, clock_timestamp() + $5
        );
        """;

    private const string InsertEmailOutboxSql = """
        INSERT INTO public.email_outbox (
            id, idempotency_key, message_id, user_id, one_time_token_id,
            recipient_envelope, template_code, template_payload,
            delivery_secret_envelope
        ) VALUES (
            $1, $2, $3, $4, $5, $6::jsonb,
            'password-reset-v1', $7::jsonb, $8::jsonb
        );
        """;

    private const string FindPasswordResetCandidateSql = """
        SELECT token.id, token.user_id
        FROM public.one_time_tokens AS token
        WHERE token.purpose = 'password_reset'
          AND ((token.pepper_version = $1 AND token.token_hash = $2)
            OR ($3::smallint IS NOT NULL
                AND token.pepper_version = $3
                AND token.token_hash = $4))
        ORDER BY token.id
        LIMIT 1;
        """;

    private const string HasConsumablePasswordResetSql = """
        SELECT EXISTS (
            SELECT 1
            FROM public.one_time_tokens AS token
            JOIN public.users AS user_account ON user_account.id = token.user_id
            WHERE token.purpose = 'password_reset'
              AND ((token.pepper_version = $1 AND token.token_hash = $2)
                OR ($3::smallint IS NOT NULL
                    AND token.pepper_version = $3
                    AND token.token_hash = $4))
              AND token.used_at IS NULL
              AND token.revoked_at IS NULL
              AND token.expires_at > clock_timestamp()
              AND user_account.status = 'active'
              AND user_account.deleted_at IS NULL
        );
        """;

    private const string LockPasswordResetUserSql = """
        SELECT status
        FROM public.users
        WHERE id = $1
        FOR UPDATE;
        """;

    private const string ConsumePasswordResetSql = """
        WITH consumed AS (
            UPDATE public.one_time_tokens AS token
            SET used_at = clock_timestamp()
            WHERE token.id = $1
              AND token.user_id = $2
              AND token.purpose = 'password_reset'
              AND token.used_at IS NULL
              AND token.revoked_at IS NULL
              AND token.expires_at > clock_timestamp()
            RETURNING token.id AS password_reset_id, token.user_id
        )
        UPDATE public.users AS user_account
        SET password_hash = $3,
            security_stamp = $4
        FROM consumed
        WHERE user_account.id = consumed.user_id
          AND user_account.status = 'active'
        RETURNING user_account.id, consumed.password_reset_id;
        """;

    private const string RevokeActiveRefreshSessionsSql = """
        UPDATE public.refresh_sessions
        SET status = 'revoked',
            revoked_at = clock_timestamp(),
            revoke_reason = 'password_reset'
        WHERE user_id = $1
          AND status = 'active';
        """;

    private const string RevokeOpenTotpChallengesForPasswordResetSql = """
        UPDATE public.one_time_tokens
        SET revoked_at = clock_timestamp(),
            revoke_reason = 'password_reset'
        WHERE user_id = $1
          AND purpose = 'totp_challenge'
          AND used_at IS NULL
          AND revoked_at IS NULL;
        """;

    private readonly NpgsqlDataSource _dataSource;

    internal PostgresIdentityRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<UserSlice> ListAsync(
        UserCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = cursor is null ? ListFirstSql : ListAfterSql;
        if (cursor is null)
        {
            command.Parameters.AddWithValue(limit + 1);
        }
        else
        {
            command.Parameters.AddWithValue(cursor.CreatedAt.ToUniversalTime());
            command.Parameters.AddWithValue(cursor.Id.Value);
            command.Parameters.AddWithValue(limit + 1);
        }

        List<IdentityUser> users = new(limit + 1);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            users.Add(ReadUser(reader));
        }

        bool hasMore = users.Count > limit;
        if (hasMore)
        {
            users.RemoveAt(users.Count - 1);
        }

        return new UserSlice(users, hasMore);
    }

    public async ValueTask<IdentityUser?> GetAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = GetSql;
        command.Parameters.AddWithValue(userId.Value);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IdentityUser?> GetAsync(
        EntityId userId,
        IUnitOfWorkContext unitOfWorkContext,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        NpgsqlCommand command = session.CreateCommand(forUpdate ? GetForUpdateSql : GetSql);
        command.Parameters.AddWithValue(userId.Value);
        return ReadSingleAndDisposeAsync(command, cancellationToken);
    }

    public ValueTask<IdentityUser?> FindByNormalizedEmailAsync(
        string normalizedEmail,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        NpgsqlCommand command = session.CreateCommand(FindByEmailSql);
        command.Parameters.AddWithValue(normalizedEmail);
        return ReadSingleAndDisposeAsync(command, cancellationToken);
    }

    public async ValueTask<IdentityUser?> CreateAsync(
        EntityId userId,
        string email,
        string normalizedEmail,
        string displayName,
        string passwordHash,
        SystemRole role,
        EntityId assignedBy,
        EntityId securityStamp,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using (NpgsqlCommand insertUser = session.CreateCommand(InsertUserSql))
        {
            insertUser.Parameters.AddWithValue(userId.Value);
            insertUser.Parameters.AddWithValue(email);
            insertUser.Parameters.AddWithValue(normalizedEmail);
            insertUser.Parameters.AddWithValue(displayName);
            insertUser.Parameters.AddWithValue(passwordHash);
            insertUser.Parameters.AddWithValue(securityStamp.Value);
            object? inserted = await insertUser
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (inserted is not Guid)
            {
                return null;
            }
        }

        using (NpgsqlCommand insertRole = session.CreateCommand(InsertRoleSql))
        {
            insertRole.Parameters.AddWithValue(userId.Value);
            insertRole.Parameters.AddWithValue(RoleId(role));
            insertRole.Parameters.AddWithValue(assignedBy.Value);
            int affectedRows = await insertRole
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            EnsureExactlyOne(affectedRows, "Identity user role insert");
        }

        return await GetAsync(
            userId,
            unitOfWorkContext,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<UpdateUserPersistenceResult> UpdateAsync(
        EntityId userId,
        long expectedVersion,
        string? displayName,
        SystemRole? role,
        UserLifecycle? status,
        EntityId assignedBy,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        IdentityUser? before = await GetAsync(
            userId,
            unitOfWorkContext,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
        UpdateUserDisposition disposition;
        bool changed;
        using (NpgsqlCommand updateUser = session.CreateCommand(UpdateUserSql))
        {
            updateUser.Parameters.AddWithValue(userId.Value);
            updateUser.Parameters.AddWithValue(expectedVersion);
            AddNullable(updateUser.Parameters, NpgsqlDbType.Text, displayName);
            AddNullable(
                updateUser.Parameters,
                NpgsqlDbType.Text,
                status is null ? null : StatusCode(status.Value));
            AddNullable(
                updateUser.Parameters,
                NpgsqlDbType.Uuid,
                role is null ? null : RoleId(role.Value));
            updateUser.Parameters.AddWithValue(assignedBy.Value);
            using NpgsqlDataReader reader = await updateUser
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Identity user update function returned no disposition.");
            }

            disposition = ParseUpdateDisposition(reader.GetString(0));
            changed = reader.GetBoolean(1);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Identity user update function returned multiple dispositions.");
            }
        }

        IdentityUser? current = disposition == UpdateUserDisposition.NotFound
            ? null
            : await GetAsync(
                userId,
                unitOfWorkContext,
                forUpdate: false,
                cancellationToken).ConfigureAwait(false);
        if (disposition != UpdateUserDisposition.Updated)
        {
            return new UpdateUserPersistenceResult(
                disposition,
                current,
                current,
                false);
        }

        IdentityUser after = current
            ?? throw new InvalidOperationException("Updated Identity user could not be reloaded.");
        IdentityUser original = before
            ?? throw new InvalidOperationException("Updated Identity user had no pre-update state.");
        if (changed
            && after.Status == UserLifecycle.Disabled
            && original.Status != UserLifecycle.Disabled)
        {
            using NpgsqlCommand revokeResetTokens = session.CreateCommand(
                RevokePasswordResetTokensForDisabledUserSql);
            revokeResetTokens.Parameters.AddWithValue(userId.Value);
            _ = await revokeResetTokens
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);

            using NpgsqlCommand revokeTotpChallenges = session.CreateCommand(
                RevokeTotpChallengesForDisabledUserSql);
            revokeTotpChallenges.Parameters.AddWithValue(userId.Value);
            _ = await revokeTotpChallenges
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);

            using NpgsqlCommand revokeRefreshSessions = session.CreateCommand(
                RevokeRefreshSessionsForDisabledUserSql);
            revokeRefreshSessions.Parameters.AddWithValue(userId.Value);
            _ = await revokeRefreshSessions
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return new UpdateUserPersistenceResult(
            UpdateUserDisposition.Updated,
            after,
            original,
            changed);
    }

    public async ValueTask InsertPasswordResetAsync(
        PasswordResetOutboxWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using (NpgsqlCommand revoke = session.CreateCommand(RevokeOpenPasswordResetTokensSql))
        {
            revoke.Parameters.AddWithValue(write.UserId.Value);
            _ = await revoke.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (NpgsqlCommand insertToken = session.CreateCommand(InsertPasswordResetTokenSql))
        {
            insertToken.Parameters.AddWithValue(write.TokenId.Value);
            insertToken.Parameters.AddWithValue(write.UserId.Value);
            insertToken.Parameters.AddWithValue(write.TokenHash);
            insertToken.Parameters.AddWithValue(write.PepperVersion);
            insertToken.Parameters.AddWithValue(write.Lifetime);
            int affectedRows = await insertToken
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            EnsureExactlyOne(affectedRows, "Password-reset token insert");
        }

        using NpgsqlCommand insertEmail = session.CreateCommand(InsertEmailOutboxSql);
        insertEmail.Parameters.AddWithValue(write.EmailOutboxId.Value);
        insertEmail.Parameters.AddWithValue(write.IdempotencyKey);
        insertEmail.Parameters.AddWithValue(write.MessageId);
        insertEmail.Parameters.AddWithValue(write.UserId.Value);
        insertEmail.Parameters.AddWithValue(write.TokenId.Value);
        AddJson(insertEmail.Parameters, write.RecipientEnvelope);
        AddJson(insertEmail.Parameters, write.TemplatePayload);
        AddJson(insertEmail.Parameters, write.DeliverySecretEnvelope);
        int emailRows = await insertEmail
            .ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureExactlyOne(emailRows, "Password-reset email outbox insert");
    }

    public async ValueTask<PasswordResetConsumeResult?> ConsumePasswordResetAsync(
        IReadOnlyList<PasswordResetTokenCandidate> candidates,
        string passwordHash,
        EntityId securityStamp,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count is < 1 or > 2)
        {
            return null;
        }

        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        EntityId tokenId;
        EntityId candidateUserId;
        using (NpgsqlCommand find = session.CreateCommand(FindPasswordResetCandidateSql))
        {
            AddTokenCandidateParameters(find.Parameters, candidates);
            using NpgsqlDataReader reader = await find
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            tokenId = new EntityId(reader.GetGuid(0));
            candidateUserId = new EntityId(reader.GetGuid(1));
        }

        using (NpgsqlCommand lockUser = session.CreateCommand(LockPasswordResetUserSql))
        {
            lockUser.Parameters.AddWithValue(candidateUserId.Value);
            object? status = await lockUser
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(status as string, "active", StringComparison.Ordinal))
            {
                return null;
            }
        }

        EntityId? userId;
        EntityId? passwordResetId;
        using (NpgsqlCommand consume = session.CreateCommand(ConsumePasswordResetSql))
        {
            consume.Parameters.AddWithValue(tokenId.Value);
            consume.Parameters.AddWithValue(candidateUserId.Value);
            consume.Parameters.AddWithValue(passwordHash);
            consume.Parameters.AddWithValue(securityStamp.Value);
            using NpgsqlDataReader reader = await consume
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                userId = new EntityId(reader.GetGuid(0));
                passwordResetId = new EntityId(reader.GetGuid(1));
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "Password-reset consume affected more than one user.");
                }
            }
            else
            {
                userId = null;
                passwordResetId = null;
            }
        }

        if (userId is null)
        {
            return null;
        }

        using (NpgsqlCommand revokeSessions = session.CreateCommand(RevokeActiveRefreshSessionsSql))
        {
            revokeSessions.Parameters.AddWithValue(userId.Value.Value);
            _ = await revokeSessions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (NpgsqlCommand revokeTotpChallenges = session.CreateCommand(
            RevokeOpenTotpChallengesForPasswordResetSql))
        {
            revokeTotpChallenges.Parameters.AddWithValue(userId.Value.Value);
            _ = await revokeTotpChallenges
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        IdentityUser user = await GetAsync(
            userId.Value,
            unitOfWorkContext,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Password-reset user could not be reloaded.");
        return new PasswordResetConsumeResult(user, passwordResetId!.Value);
    }

    public async ValueTask<bool> HasConsumablePasswordResetAsync(
        IReadOnlyList<PasswordResetTokenCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count is < 1 or > 2)
        {
            return false;
        }

        var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = HasConsumablePasswordResetSql;
        AddTokenCandidateParameters(command.Parameters, candidates);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool exists
            ? exists
            : throw new InvalidOperationException(
                "Password-reset consumability check did not return a Boolean value.");
    }

    private static async ValueTask<IdentityUser?> ReadSingleAndDisposeAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using (command)
        {
            return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<IdentityUser?> ReadSingleAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadUser(reader)
            : null;
    }

    private static IdentityUser ReadUser(NpgsqlDataReader reader) => new(
        new EntityId(reader.GetGuid(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        ParseRole(reader.GetString(5)),
        ParseStatus(reader.GetString(6)),
        reader.GetInt64(7),
        reader.GetInt64(8),
        reader.GetFieldValue<DateTimeOffset>(9),
        reader.GetFieldValue<DateTimeOffset>(10));

    private static Guid RoleId(SystemRole role) => role switch
    {
        SystemRole.Admin => AdminRoleId,
        SystemRole.Operator => OperatorRoleId,
        SystemRole.Auditor => AuditorRoleId,
        SystemRole.User => UserRoleId,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static SystemRole ParseRole(string code) => code switch
    {
        "admin" => SystemRole.Admin,
        "operator" => SystemRole.Operator,
        "auditor" => SystemRole.Auditor,
        "user" => SystemRole.User,
        _ => throw new InvalidOperationException("Identity user has an unknown role."),
    };

    private static UpdateUserDisposition ParseUpdateDisposition(string disposition) =>
        disposition switch
        {
            "updated" => UpdateUserDisposition.Updated,
            "not_found" => UpdateUserDisposition.NotFound,
            "version_conflict" => UpdateUserDisposition.VersionConflict,
            "last_active_admin" => UpdateUserDisposition.LastActiveAdminConflict,
            _ => throw new InvalidOperationException(
                "Identity user update function returned an unknown disposition."),
        };

    private static string StatusCode(UserLifecycle status) => status switch
    {
        UserLifecycle.Active => "active",
        UserLifecycle.Disabled => "disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static UserLifecycle ParseStatus(string status) => status switch
    {
        "active" => UserLifecycle.Active,
        "disabled" => UserLifecycle.Disabled,
        _ => throw new InvalidOperationException("Identity user has an unknown status."),
    };

    private static void AddJson(NpgsqlParameterCollection parameters, JsonElement value) =>
        parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = value.GetRawText(),
        });

    private static void AddTokenCandidateParameters(
        NpgsqlParameterCollection parameters,
        IReadOnlyList<PasswordResetTokenCandidate> candidates)
    {
        PasswordResetTokenCandidate current = candidates[0];
        parameters.AddWithValue(current.PepperVersion);
        parameters.AddWithValue(current.Hash);
        AddNullable(
            parameters,
            NpgsqlDbType.Smallint,
            candidates.Count == 2 ? candidates[1].PepperVersion : null);
        AddNullable(
            parameters,
            NpgsqlDbType.Bytea,
            candidates.Count == 2 ? candidates[1].Hash : null);
    }

    private static void AddNullable(
        NpgsqlParameterCollection parameters,
        NpgsqlDbType type,
        object? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = type,
            Value = value ?? DBNull.Value,
        });

    private static void EnsureExactlyOne(int affectedRows, string operation)
    {
        if (affectedRows != 1)
        {
            throw new InvalidOperationException($"{operation} must affect exactly one row.");
        }
    }
}
#pragma warning restore MA0051
