#pragma warning disable MA0051 // Session transaction scripts intentionally keep their linearization points visible.
using System.Globalization;
using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Persistence;

internal sealed class PostgresIdentitySessionRepository : IIdentitySessionRepository
{
    private const string AuthenticationUserColumns = """
        u.id, u.email, u.normalized_email, u.display_name, u.password_hash,
        r.code, u.status, u.totp_secret_envelope::text,
        u.totp_last_accepted_step, u.security_stamp, u.token_version,
        u.failed_login_count, u.locked_until, u.last_login_at, u.version,
        u.created_at, u.updated_at
        """;

    private readonly PostgresIdentitySessionReader _reader;

    internal PostgresIdentitySessionRepository(NpgsqlDataSource dataSource)
    {
        _reader = new PostgresIdentitySessionReader(
            dataSource ?? throw new ArgumentNullException(nameof(dataSource)));
    }

    public ValueTask<AuthenticationUserSnapshot?> FindAuthenticationUserAsync(
        string normalizedEmail,
        CancellationToken cancellationToken) => _reader.FindAuthenticationUserAsync(
            normalizedEmail,
            cancellationToken);

    public ValueTask<AuthenticationUserSnapshot?> GetAuthenticationUserAsync(
        EntityId userId,
        CancellationToken cancellationToken) => _reader.GetAuthenticationUserAsync(
            userId,
            cancellationToken);

    public async ValueTask<UserStatusSnapshot?> ReadCanonicalAuthorizationAsync(
        EntityId userId,
        EntityId familyId,
        long tokenVersion,
        CancellationToken cancellationToken) => await _reader.ReadCanonicalAuthorizationAsync(
            userId,
            familyId,
            tokenVersion,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> HasRefreshCredentialAsync(
        IReadOnlyList<CredentialHashCandidate> candidates,
        CancellationToken cancellationToken) => await _reader.HasRefreshCredentialAsync(
            candidates,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<PasswordFailureDisposition> RecordPasswordFailureAsync(
        EntityId userId,
        EntityId expectedSecurityStamp,
        int maximumFailures,
        TimeSpan lockoutDuration,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand("""
            WITH authority AS MATERIALIZED (
                SELECT clock_timestamp() AS now
            ), next_failure AS MATERIALIZED (
                SELECT CASE
                    WHEN u.locked_until IS NOT NULL AND u.locked_until <= a.now THEN 1
                    ELSE u.failed_login_count + 1
                END AS count,
                a.now
                FROM public.users AS u
                CROSS JOIN authority AS a
                WHERE u.id = $1
                  AND u.security_stamp = $2
                  AND u.status = 'active'
                  AND u.deleted_at IS NULL
                  AND (u.locked_until IS NULL OR u.locked_until <= a.now)
                FOR UPDATE OF u
            )
            UPDATE public.users AS u
            SET failed_login_count = n.count,
                locked_until = CASE
                    WHEN n.count >= $3 THEN n.now + $4::interval
                    ELSE NULL
                END
            FROM next_failure AS n
            WHERE u.id = $1
            RETURNING u.id;
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(expectedSecurityStamp.Value);
        command.Parameters.AddWithValue(maximumFailures);
        command.Parameters.AddWithValue(lockoutDuration);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is Guid
            ? PasswordFailureDisposition.Recorded
            : PasswordFailureDisposition.Ignored;
    }

    public async ValueTask<PasswordLoginPersistenceResult> CompletePasswordLoginAsync(
        PasswordLoginWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        AuthenticationUserSnapshot? user = await ReadUserInTransactionAsync(
            session,
            write.UserId,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (user is null || user.SecurityStamp != write.ExpectedSecurityStamp)
        {
            return new(PasswordLoginDisposition.StaleCredential, null, null, null);
        }

        if (user.Status != UserLifecycle.Active)
        {
            return new(PasswordLoginDisposition.UserDisabled, null, null, null);
        }

        DateTimeOffset now = await ReadDatabaseNowAsync(session, cancellationToken).ConfigureAwait(false);
        if (user.LockedUntil is not null && user.LockedUntil > now)
        {
            long retryAfter = Math.Max(
                1,
                checked((long)Math.Ceiling((user.LockedUntil.Value - now).TotalSeconds)));
            return new(PasswordLoginDisposition.AccountLocked, user, null, retryAfter);
        }

        await ResetSuccessfulLoginAsync(session, user.Id, cancellationToken).ConfigureAwait(false);
        if (user.TotpEnabled)
        {
            if (write.Challenge is null)
            {
                return new(PasswordLoginDisposition.StaleCredential, null, null, null);
            }

            await RevokeOpenTotpChallengesAsync(
                session,
                user.Id,
                "login",
                "superseded",
                cancellationToken).ConfigureAwait(false);
            await InsertTotpChallengeAsync(
                session,
                write.Challenge,
                user.Id,
                cancellationToken).ConfigureAwait(false);
            AuthenticationUserSnapshot current = await RequireUserAsync(
                session,
                user.Id,
                cancellationToken).ConfigureAwait(false);
            return new(PasswordLoginDisposition.MfaRequired, current, null, null);
        }

        if (write.Session is null)
        {
            return new(PasswordLoginDisposition.StaleCredential, null, null, null);
        }

        await InsertInitialRefreshSessionAsync(
            session,
            write.Session,
            user.Id,
            cancellationToken).ConfigureAwait(false);
        AuthenticationUserSnapshot completed = await RequireUserAsync(
            session,
            user.Id,
            cancellationToken).ConfigureAwait(false);
        return new(
            PasswordLoginDisposition.SessionCreated,
            completed,
            write.Session.Id,
            null);
    }

    public async ValueTask<TotpChallengeSnapshot?> FindTotpChallengeAsync(
        IReadOnlyList<CredentialHashCandidate> candidates,
        string kind,
        CancellationToken cancellationToken) => await _reader.FindTotpChallengeAsync(
            candidates,
            kind,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<MfaLoginPersistenceResult> CompleteMfaLoginAsync(
        IReadOnlyList<CredentialHashCandidate> challengeCandidates,
        long acceptedStep,
        RefreshSessionWrite sessionWrite,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ValidateCandidates(challengeCandidates);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        EntityId? candidateUserId = await FindChallengeUserIdAsync(
            session,
            challengeCandidates,
            "login",
            cancellationToken).ConfigureAwait(false);
        if (candidateUserId is null)
        {
            return new(MfaLoginDisposition.ChallengeInvalid, null, null);
        }

        AuthenticationUserSnapshot? user = await ReadUserInTransactionAsync(
            session,
            candidateUserId.Value,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        TotpChallengeSnapshot? challenge = await ReadChallengeInTransactionAsync(
            session,
            challengeCandidates,
            "login",
            cancellationToken).ConfigureAwait(false);
        if (!IsUsableChallenge(challenge, user, "login"))
        {
            return new(MfaLoginDisposition.ChallengeInvalid, null, null);
        }

        if (user!.TotpLastAcceptedStep is long lastAcceptedStep
            && acceptedStep <= lastAcceptedStep)
        {
            return new(MfaLoginDisposition.TotpReplay, null, null);
        }

        bool consumed = await TryConsumeChallengeAsync(
            session,
            challenge!.Id,
            responseBodyEnvelope: null,
            cancellationToken).ConfigureAwait(false);
        if (!consumed)
        {
            return new(MfaLoginDisposition.ChallengeInvalid, null, null);
        }

        EnsureExactlyOne(await AcceptTotpStepAsync(
            session,
            user.Id,
            acceptedStep,
            cancellationToken).ConfigureAwait(false));
        await ResetSuccessfulLoginAsync(session, user.Id, cancellationToken).ConfigureAwait(false);
        await InsertInitialRefreshSessionAsync(
            session,
            sessionWrite,
            user.Id,
            cancellationToken).ConfigureAwait(false);
        AuthenticationUserSnapshot completed = await RequireUserAsync(
            session,
            user.Id,
            cancellationToken).ConfigureAwait(false);
        return new(MfaLoginDisposition.SessionCreated, completed, sessionWrite.Id);
    }

    public async ValueTask<RefreshRotationPersistenceResult> RotateRefreshSessionAsync(
        IReadOnlyList<CredentialHashCandidate> candidates,
        RefreshSessionWrite replacement,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ValidateCandidates(candidates);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        RefreshLocator? locator = await FindRefreshLocatorAsync(
            session,
            candidates,
            cancellationToken).ConfigureAwait(false);
        if (locator is null)
        {
            return new(RefreshRotationDisposition.Invalid, null, null);
        }

        AuthenticationUserSnapshot? user = await ReadUserInTransactionAsync(
            session,
            locator.UserId,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        RefreshLocator? locked = await LockRefreshAsync(
            session,
            candidates,
            cancellationToken).ConfigureAwait(false);
        if (locked is null || user is null || user.Status != UserLifecycle.Active)
        {
            return new(RefreshRotationDisposition.Invalid, null, null);
        }

        if (string.Equals(locked.Status, "rotated", StringComparison.Ordinal))
        {
            await RevokeRefreshFamilyAsync(
                session,
                locked.UserId,
                locked.FamilyId,
                "refresh_reuse",
                cancellationToken).ConfigureAwait(false);
            return new(RefreshRotationDisposition.Reused, user, locked.FamilyId);
        }

        DateTimeOffset now = await ReadDatabaseNowAsync(session, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(locked.Status, "active", StringComparison.Ordinal)
            || locked.ExpiresAt <= now)
        {
            if (string.Equals(locked.Status, "active", StringComparison.Ordinal))
            {
                await MarkRefreshExpiredAsync(
                    session,
                    locked.Id,
                    cancellationToken).ConfigureAwait(false);
            }

            return new(RefreshRotationDisposition.Invalid, null, null);
        }

        await RotateRefreshAsync(
            session,
            locked,
            replacement,
            cancellationToken).ConfigureAwait(false);
        AuthenticationUserSnapshot current = await RequireUserAsync(
            session,
            user.Id,
            cancellationToken).ConfigureAwait(false);
        return new(RefreshRotationDisposition.Rotated, current, locked.FamilyId);
    }

    public async ValueTask<LogoutPersistenceResult> LogoutAsync(
        SessionActor actor,
        IReadOnlyList<CredentialHashCandidate> refreshCandidates,
        bool allSessions,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        AuthenticationUserSnapshot? user = await ReadUserInTransactionAsync(
            session,
            actor.UserId,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new(null, false);
        }

        int changed;
        if (allSessions)
        {
            changed = await RevokeAllRefreshSessionsAsync(
                session,
                actor.UserId,
                "logout_all",
                cancellationToken).ConfigureAwait(false);
            if (changed > 0)
            {
                await IncrementTokenVersionAsync(
                    session,
                    actor.UserId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            EntityId? familyId = refreshCandidates.Count == 0
                ? actor.SessionFamilyId
                : await FindActorRefreshFamilyAsync(
                    session,
                    actor.UserId,
                    refreshCandidates,
                    cancellationToken).ConfigureAwait(false);
            if (refreshCandidates.Count > 0 && familyId != actor.SessionFamilyId)
            {
                familyId = null;
            }
            changed = familyId is null
                ? 0
                : await RevokeRefreshFamilyAsync(
                    session,
                    actor.UserId,
                    familyId.Value,
                    "logout",
                    cancellationToken).ConfigureAwait(false);
        }

        AuthenticationUserSnapshot current = await RequireUserAsync(
            session,
            actor.UserId,
            cancellationToken).ConfigureAwait(false);
        return new(current, changed > 0);
    }

    public async ValueTask<SecurityMutationPersistenceResult> ChangePasswordAsync(
        EntityId userId,
        long expectedVersion,
        EntityId expectedSecurityStamp,
        string passwordHash,
        EntityId newSecurityStamp,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        AuthenticationUserSnapshot? user = await ReadUserInTransactionAsync(
            session,
            userId,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        SecurityMutationPersistenceResult? failure = ValidateSecurityMutation(
            user,
            expectedVersion,
            expectedSecurityStamp);
        if (failure is not null)
        {
            return failure;
        }

        using (NpgsqlCommand update = session.CreateCommand("""
            UPDATE public.users
            SET password_hash = $2,
                security_stamp = $3
            WHERE id = $1;
            """))
        {
            update.Parameters.AddWithValue(userId.Value);
            update.Parameters.AddWithValue(passwordHash);
            update.Parameters.AddWithValue(newSecurityStamp.Value);
            EnsureExactlyOne(await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        await RevokeAllRefreshSessionsAsync(
            session,
            userId,
            "password_changed",
            cancellationToken).ConfigureAwait(false);
        await RevokeAllTotpChallengesAsync(
            session,
            userId,
            "security_changed",
            cancellationToken).ConfigureAwait(false);
        return new(
            SecurityMutationDisposition.Updated,
            await RequireUserAsync(session, userId, cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<SecurityMutationPersistenceResult> CreateTotpSetupAsync(
        EntityId userId,
        EntityId expectedSecurityStamp,
        TotpChallengeWrite challenge,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        AuthenticationUserSnapshot? user = await ReadUserInTransactionAsync(
            session,
            userId,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (user is null || user.Status != UserLifecycle.Active
            || user.SecurityStamp != expectedSecurityStamp)
        {
            return new(SecurityMutationDisposition.InvalidCredential, user);
        }

        if (user.TotpEnabled)
        {
            return new(SecurityMutationDisposition.TotpAlreadyEnabled, user);
        }

        await RevokeOpenTotpChallengesAsync(
            session,
            userId,
            "setup",
            "superseded",
            cancellationToken).ConfigureAwait(false);
        await InsertTotpChallengeAsync(session, challenge, userId, cancellationToken)
            .ConfigureAwait(false);
        if (challenge.ResponseBodyEnvelope is not null)
        {
            await SetChallengeResponseEnvelopeAsync(
                session,
                challenge.Id,
                challenge.ResponseBodyEnvelope.Value,
                cancellationToken).ConfigureAwait(false);
        }
        return new(SecurityMutationDisposition.Updated, user);
    }

    public async ValueTask<SecurityMutationPersistenceResult> ConfirmTotpAsync(
        TotpConfirmWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        AuthenticationUserSnapshot? user = await ReadUserInTransactionAsync(
            session,
            write.UserId,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new(SecurityMutationDisposition.NotFound, null);
        }

        if (user.Version != write.ExpectedVersion)
        {
            return new(SecurityMutationDisposition.VersionConflict, user, user.Version);
        }

        if (user.TotpEnabled)
        {
            return new(SecurityMutationDisposition.TotpAlreadyEnabled, user);
        }

        TotpChallengeSnapshot? challenge = await ReadChallengeInTransactionAsync(
            session,
            write.ChallengeCandidates,
            "setup",
            cancellationToken).ConfigureAwait(false);
        if (challenge is null
            || challenge.UserId != user.Id
            || challenge.SecurityStamp != user.SecurityStamp
            || challenge.TokenVersion != user.TokenVersion)
        {
            return new(SecurityMutationDisposition.ChallengeInvalid, user);
        }

        if (user.TotpLastAcceptedStep is long lastAcceptedStep
            && write.AcceptedStep <= lastAcceptedStep)
        {
            return new(SecurityMutationDisposition.TotpReplay, user);
        }

        bool consumed = await TryConsumeChallengeAsync(
            session,
            challenge.Id,
            write.RecoveryCodesEnvelope,
            cancellationToken).ConfigureAwait(false);
        if (!consumed)
        {
            return new(SecurityMutationDisposition.ChallengeExpired, user);
        }

        using (NpgsqlCommand update = session.CreateCommand("""
            UPDATE public.users
            SET totp_secret_envelope = $2::jsonb,
                totp_last_accepted_step = $3
            WHERE id = $1
              AND $3 > COALESCE(totp_last_accepted_step, -1);
            """))
        {
            update.Parameters.AddWithValue(user.Id.Value);
            AddJson(update.Parameters, write.UserSecretEnvelope);
            update.Parameters.AddWithValue(write.AcceptedStep);
            EnsureExactlyOne(await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        await RevokeRecoveryCodesAsync(session, user.Id, "totp_reenabled", cancellationToken)
            .ConfigureAwait(false);
        foreach (TotpRecoveryCodeWrite recoveryCode in write.RecoveryCodes)
        {
            await InsertRecoveryCodeAsync(
                session,
                user.Id,
                recoveryCode,
                cancellationToken).ConfigureAwait(false);
        }

        await RevokeAllRefreshSessionsAsync(
            session,
            user.Id,
            "totp_enabled",
            cancellationToken).ConfigureAwait(false);
        AuthenticationUserSnapshot current = await RequireUserAsync(
            session,
            user.Id,
            cancellationToken).ConfigureAwait(false);
        return new(SecurityMutationDisposition.Updated, current);
    }

    public async ValueTask<SecurityMutationPersistenceResult> DisableTotpAsync(
        EntityId userId,
        long expectedVersion,
        EntityId expectedSecurityStamp,
        long acceptedStep,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        AuthenticationUserSnapshot? user = await ReadUserInTransactionAsync(
            session,
            userId,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        SecurityMutationPersistenceResult? failure = ValidateSecurityMutation(
            user,
            expectedVersion,
            expectedSecurityStamp);
        if (failure is not null)
        {
            return failure;
        }

        if (!user!.TotpEnabled)
        {
            return new(SecurityMutationDisposition.TotpNotEnabled, user);
        }

        using (NpgsqlCommand update = session.CreateCommand("""
            UPDATE public.users
            SET totp_secret_envelope = NULL,
                totp_last_accepted_step = NULL,
                security_stamp = $3
            WHERE id = $1
              AND $2 > COALESCE(totp_last_accepted_step, -1);
            """))
        {
            update.Parameters.AddWithValue(userId.Value);
            update.Parameters.AddWithValue(acceptedStep);
            update.Parameters.AddWithValue(EntityId.New().Value);
            int affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                return new(SecurityMutationDisposition.TotpReplay, user);
            }
        }

        await RevokeRecoveryCodesAsync(session, userId, "totp_disabled", cancellationToken)
            .ConfigureAwait(false);
        await RevokeAllRefreshSessionsAsync(session, userId, "totp_disabled", cancellationToken)
            .ConfigureAwait(false);
        await RevokeAllTotpChallengesAsync(session, userId, "totp_disabled", cancellationToken)
            .ConfigureAwait(false);
        return new(
            SecurityMutationDisposition.Updated,
            await RequireUserAsync(session, userId, cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<AuthenticationUserSnapshot?> ReadUserInTransactionAsync(
        PostgresTransactionSession session,
        EntityId userId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(
            BuildUserSql("u.id = $1", forUpdate));
        command.Parameters.AddWithValue(userId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadUser(reader, 0)
            : null;
    }

    internal static string BuildUserSql(string predicate, bool forUpdate) => string.Concat(
        "SELECT ",
        AuthenticationUserColumns,
        " FROM public.users AS u JOIN public.user_roles AS ur ON ur.user_id = u.id ",
        "JOIN public.roles AS r ON r.id = ur.role_id WHERE ",
        predicate,
        " AND u.deleted_at IS NULL",
        forUpdate ? " FOR UPDATE OF u" : string.Empty,
        ";");

    internal static AuthenticationUserSnapshot ReadUser(NpgsqlDataReader reader, int offset) => new(
        new EntityId(reader.GetGuid(offset)),
        reader.GetString(offset + 1),
        reader.GetString(offset + 2),
        reader.GetString(offset + 3),
        reader.GetString(offset + 4),
        ParseRole(reader.GetString(offset + 5)),
        ParseStatus(reader.GetString(offset + 6)),
        reader.IsDBNull(offset + 7)
            ? null
            : JsonDocument.Parse(reader.GetString(offset + 7)).RootElement.Clone(),
        reader.IsDBNull(offset + 8) ? null : reader.GetInt64(offset + 8),
        new EntityId(reader.GetGuid(offset + 9)),
        reader.GetInt64(offset + 10),
        reader.GetInt32(offset + 11),
        reader.IsDBNull(offset + 12) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 12),
        reader.IsDBNull(offset + 13) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 13),
        reader.GetInt64(offset + 14),
        reader.GetFieldValue<DateTimeOffset>(offset + 15),
        reader.GetFieldValue<DateTimeOffset>(offset + 16));

    private static async ValueTask<AuthenticationUserSnapshot> RequireUserAsync(
        PostgresTransactionSession session,
        EntityId userId,
        CancellationToken cancellationToken) => await ReadUserInTransactionAsync(
            session,
            userId,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The Identity user disappeared inside its transaction.");

    private static async ValueTask<DateTimeOffset> ReadDatabaseNowAsync(
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("SELECT clock_timestamp();");
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PostgreSQL did not return its authoritative clock.");
        }

        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static async ValueTask ResetSuccessfulLoginAsync(
        PostgresTransactionSession session,
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            UPDATE public.users
            SET failed_login_count = 0,
                locked_until = NULL,
                last_login_at = clock_timestamp()
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        EnsureExactlyOne(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertInitialRefreshSessionAsync(
        PostgresTransactionSession session,
        RefreshSessionWrite write,
        EntityId userId,
        CancellationToken cancellationToken) => await InsertRefreshSessionAsync(
            session,
            write,
            userId,
            write.Id,
            parentId: null,
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask InsertRefreshSessionAsync(
        PostgresTransactionSession session,
        RefreshSessionWrite write,
        EntityId userId,
        EntityId familyId,
        EntityId? parentId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            INSERT INTO public.refresh_sessions (
                id, family_id, user_id, parent_session_id,
                token_hash, pepper_version, expires_at, ip_address, user_agent
            ) VALUES (
                $1, $2, $3, $4, $5, $6,
                clock_timestamp() + $7::interval, $8::inet, $9
            );
            """);
        command.Parameters.AddWithValue(write.Id.Value);
        command.Parameters.AddWithValue(familyId.Value);
        command.Parameters.AddWithValue(userId.Value);
        AddNullable(command.Parameters, NpgsqlDbType.Uuid, parentId?.Value);
        command.Parameters.AddWithValue(write.TokenHash);
        command.Parameters.AddWithValue(write.PepperVersion);
        command.Parameters.AddWithValue(write.Lifetime);
        AddNullable(command.Parameters, NpgsqlDbType.Inet, NormalizeIp(write.IpAddress));
        AddNullable(command.Parameters, NpgsqlDbType.Text, Truncate(write.UserAgent, 512));
        EnsureExactlyOne(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertTotpChallengeAsync(
        PostgresTransactionSession session,
        TotpChallengeWrite challenge,
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            INSERT INTO public.one_time_tokens (
                id, user_id, purpose, token_hash, pepper_version, expires_at,
                challenge_kind, secret_envelope, security_stamp, token_version
            ) VALUES (
                $1, $2, 'totp_challenge', $3, $4,
                clock_timestamp() + $5::interval, $6, $7::jsonb, $8, $9
            );
            """);
        command.Parameters.AddWithValue(challenge.Id.Value);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(challenge.TokenHash);
        command.Parameters.AddWithValue(challenge.PepperVersion);
        command.Parameters.AddWithValue(challenge.Lifetime);
        command.Parameters.AddWithValue(challenge.Kind);
        AddNullableJson(command.Parameters, challenge.SecretEnvelope);
        command.Parameters.AddWithValue(challenge.SecurityStamp.Value);
        command.Parameters.AddWithValue(challenge.TokenVersion);
        EnsureExactlyOne(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask RevokeOpenTotpChallengesAsync(
        PostgresTransactionSession session,
        EntityId userId,
        string kind,
        string reason,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            UPDATE public.one_time_tokens
            SET revoked_at = clock_timestamp(),
                revoke_reason = $3
            WHERE user_id = $1
              AND purpose = 'totp_challenge'
              AND challenge_kind = $2
              AND used_at IS NULL
              AND revoked_at IS NULL;
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(kind);
        command.Parameters.AddWithValue(reason);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SetChallengeResponseEnvelopeAsync(
        PostgresTransactionSession session,
        EntityId challengeId,
        JsonElement responseEnvelope,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            UPDATE public.one_time_tokens
            SET response_body_envelope = $2::jsonb
            WHERE id = $1
              AND purpose = 'totp_challenge'
              AND challenge_kind = 'setup'
              AND used_at IS NULL
              AND revoked_at IS NULL;
            """);
        command.Parameters.AddWithValue(challengeId.Value);
        AddJson(command.Parameters, responseEnvelope);
        EnsureExactlyOne(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask RevokeAllTotpChallengesAsync(
        PostgresTransactionSession session,
        EntityId userId,
        string reason,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            UPDATE public.one_time_tokens
            SET revoked_at = clock_timestamp(),
                revoke_reason = $2
            WHERE user_id = $1
              AND purpose = 'totp_challenge'
              AND used_at IS NULL
              AND revoked_at IS NULL;
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(reason);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string BuildFindChallengeSql(bool forUpdate) => string.Concat(
        "SELECT t.id, t.user_id, t.challenge_kind, t.secret_envelope::text, ",
        "t.response_body_envelope::text, t.security_stamp, t.token_version, t.expires_at, ",
        AuthenticationUserColumns,
        " FROM public.one_time_tokens AS t ",
        "JOIN public.users AS u ON u.id = t.user_id ",
        "JOIN public.user_roles AS ur ON ur.user_id = u.id ",
        "JOIN public.roles AS r ON r.id = ur.role_id ",
        "WHERE t.purpose = 'totp_challenge' AND t.challenge_kind = $5 ",
        "AND t.used_at IS NULL AND t.revoked_at IS NULL ",
        "AND t.expires_at > clock_timestamp() AND ",
        "((t.pepper_version = $1 AND t.token_hash = $2) ",
        "OR ($3::smallint IS NOT NULL AND t.pepper_version = $3 AND t.token_hash = $4))",
        forUpdate ? " FOR UPDATE OF t" : string.Empty,
        ";");

    internal static TotpChallengeSnapshot ReadChallenge(NpgsqlDataReader reader) => new(
        new EntityId(reader.GetGuid(0)),
        new EntityId(reader.GetGuid(1)),
        reader.GetString(2),
        ReadNullableJson(reader, 3),
        ReadNullableJson(reader, 4),
        new EntityId(reader.GetGuid(5)),
        reader.GetInt64(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        ReadUser(reader, 8));

    private static async ValueTask<TotpChallengeSnapshot?> ReadChallengeInTransactionAsync(
        PostgresTransactionSession session,
        IReadOnlyList<CredentialHashCandidate> candidates,
        string kind,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(BuildFindChallengeSql(forUpdate: true));
        AddCandidates(command.Parameters, candidates);
        command.Parameters.AddWithValue(kind);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadChallenge(reader)
            : null;
    }

    private static async ValueTask<EntityId?> FindChallengeUserIdAsync(
        PostgresTransactionSession session,
        IReadOnlyList<CredentialHashCandidate> candidates,
        string kind,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT user_id
            FROM public.one_time_tokens
            WHERE purpose = 'totp_challenge'
              AND challenge_kind = $5
              AND ((pepper_version = $1 AND token_hash = $2)
                OR ($3::smallint IS NOT NULL AND pepper_version = $3 AND token_hash = $4));
            """);
        AddCandidates(command.Parameters, candidates);
        command.Parameters.AddWithValue(kind);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is Guid id ? new EntityId(id) : null;
    }

    private static bool IsUsableChallenge(
        TotpChallengeSnapshot? challenge,
        AuthenticationUserSnapshot? user,
        string kind) => challenge is not null
        && user is not null
        && user.Status == UserLifecycle.Active
        && user.TotpEnabled
        && string.Equals(challenge.Kind, kind, StringComparison.Ordinal)
        && challenge.UserId == user.Id
        && challenge.SecurityStamp == user.SecurityStamp
        && challenge.TokenVersion == user.TokenVersion;

    private static async ValueTask<int> AcceptTotpStepAsync(
        PostgresTransactionSession session,
        EntityId userId,
        long acceptedStep,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            UPDATE public.users
            SET totp_last_accepted_step = $2
            WHERE id = $1
              AND $2 > COALESCE(totp_last_accepted_step, -1);
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(acceptedStep);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> TryConsumeChallengeAsync(
        PostgresTransactionSession session,
        EntityId challengeId,
        JsonElement? responseBodyEnvelope,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            WITH authority AS MATERIALIZED (
                SELECT clock_timestamp() AS now
            )
            UPDATE public.one_time_tokens AS token
            SET used_at = authority.now,
                response_body_envelope = COALESCE(
                    $2::jsonb,
                    token.response_body_envelope)
            FROM authority
            WHERE id = $1
              AND used_at IS NULL
              AND revoked_at IS NULL
              AND expires_at > authority.now;
            """);
        command.Parameters.AddWithValue(challengeId.Value);
        AddNullableJson(command.Parameters, responseBodyEnvelope);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async ValueTask<RefreshLocator?> FindRefreshLocatorAsync(
        PostgresTransactionSession session,
        IReadOnlyList<CredentialHashCandidate> candidates,
        CancellationToken cancellationToken) => await ReadRefreshLocatorAsync(
            session,
            candidates,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<RefreshLocator?> LockRefreshAsync(
        PostgresTransactionSession session,
        IReadOnlyList<CredentialHashCandidate> candidates,
        CancellationToken cancellationToken) => await ReadRefreshLocatorAsync(
            session,
            candidates,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<RefreshLocator?> ReadRefreshLocatorAsync(
        PostgresTransactionSession session,
        IReadOnlyList<CredentialHashCandidate> candidates,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        string sql = """
            SELECT id, family_id, user_id, status, expires_at
            FROM public.refresh_sessions
            WHERE (pepper_version = $1 AND token_hash = $2)
               OR ($3::smallint IS NOT NULL AND pepper_version = $3 AND token_hash = $4)
            """ + (forUpdate ? " FOR UPDATE;" : ";");
        using NpgsqlCommand command = session.CreateCommand(sql);
        AddCandidates(command.Parameters, candidates);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new RefreshLocator(
                new EntityId(reader.GetGuid(0)),
                new EntityId(reader.GetGuid(1)),
                new EntityId(reader.GetGuid(2)),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4))
            : null;
    }

    private static async ValueTask RotateRefreshAsync(
        PostgresTransactionSession session,
        RefreshLocator current,
        RefreshSessionWrite replacement,
        CancellationToken cancellationToken)
    {
        using (NpgsqlCommand rotate = session.CreateCommand("""
            UPDATE public.refresh_sessions
            SET status = 'rotated',
                rotated_at = clock_timestamp()
            WHERE id = $1
              AND status = 'active';
            """))
        {
            rotate.Parameters.AddWithValue(current.Id.Value);
            EnsureExactlyOne(await rotate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        await InsertRefreshSessionAsync(
            session,
            replacement,
            current.UserId,
            current.FamilyId,
            current.Id,
            cancellationToken).ConfigureAwait(false);

        using NpgsqlCommand link = session.CreateCommand("""
            UPDATE public.refresh_sessions
            SET replaced_by_session_id = $2
            WHERE id = $1
              AND status = 'rotated';
            """);
        link.Parameters.AddWithValue(current.Id.Value);
        link.Parameters.AddWithValue(replacement.Id.Value);
        EnsureExactlyOne(await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask MarkRefreshExpiredAsync(
        PostgresTransactionSession session,
        EntityId sessionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            UPDATE public.refresh_sessions
            SET status = 'expired'
            WHERE id = $1 AND status = 'active';
            """);
        command.Parameters.AddWithValue(sessionId.Value);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> RevokeRefreshFamilyAsync(
        PostgresTransactionSession session,
        EntityId userId,
        EntityId familyId,
        string reason,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            UPDATE public.refresh_sessions
            SET status = 'revoked',
                revoked_at = clock_timestamp(),
                revoke_reason = $3
            WHERE user_id = $1
              AND family_id = $2
              AND status = 'active';
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(familyId.Value);
        command.Parameters.AddWithValue(reason);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> RevokeAllRefreshSessionsAsync(
        PostgresTransactionSession session,
        EntityId userId,
        string reason,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            UPDATE public.refresh_sessions
            SET status = 'revoked',
                revoked_at = clock_timestamp(),
                revoke_reason = $2
            WHERE user_id = $1
              AND status = 'active';
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(reason);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<EntityId?> FindActorRefreshFamilyAsync(
        PostgresTransactionSession session,
        EntityId userId,
        IReadOnlyList<CredentialHashCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ValidateCandidates(candidates);
        using NpgsqlCommand command = session.CreateCommand("""
            SELECT family_id
            FROM public.refresh_sessions
            WHERE user_id = $5
              AND status = 'active'
              AND expires_at > clock_timestamp()
              AND ((pepper_version = $1 AND token_hash = $2)
                OR ($3::smallint IS NOT NULL AND pepper_version = $3 AND token_hash = $4))
            LIMIT 1;
            """);
        AddCandidates(command.Parameters, candidates);
        command.Parameters.AddWithValue(userId.Value);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is Guid familyId ? new EntityId(familyId) : null;
    }

    private static async ValueTask IncrementTokenVersionAsync(
        PostgresTransactionSession session,
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            UPDATE public.users
            SET token_version = token_version + 1
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        EnsureExactlyOne(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static SecurityMutationPersistenceResult? ValidateSecurityMutation(
        AuthenticationUserSnapshot? user,
        long expectedVersion,
        EntityId expectedSecurityStamp)
    {
        if (user is null)
        {
            return new(SecurityMutationDisposition.NotFound, null);
        }

        if (user.Status != UserLifecycle.Active || user.SecurityStamp != expectedSecurityStamp)
        {
            return new(SecurityMutationDisposition.InvalidCredential, user);
        }

        return user.Version != expectedVersion
            ? new(SecurityMutationDisposition.VersionConflict, user, user.Version)
            : null;
    }

    private static async ValueTask InsertRecoveryCodeAsync(
        PostgresTransactionSession session,
        EntityId userId,
        TotpRecoveryCodeWrite write,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            INSERT INTO public.totp_recovery_codes (
                id, user_id, code_hash, pepper_version
            ) VALUES ($1, $2, $3, $4);
            """);
        command.Parameters.AddWithValue(write.Id.Value);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(write.CodeHash);
        command.Parameters.AddWithValue(write.PepperVersion);
        EnsureExactlyOne(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask RevokeRecoveryCodesAsync(
        PostgresTransactionSession session,
        EntityId userId,
        string reason,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand("""
            UPDATE public.totp_recovery_codes
            SET revoked_at = clock_timestamp(),
                revoke_reason = $2
            WHERE user_id = $1
              AND used_at IS NULL
              AND revoked_at IS NULL;
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(reason);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateCandidates(IReadOnlyList<CredentialHashCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count is < 1 or > 2)
        {
            throw new ArgumentException("One or two credential candidates are required.", nameof(candidates));
        }
    }

    internal static void AddCandidates(
        NpgsqlParameterCollection parameters,
        IReadOnlyList<CredentialHashCandidate> candidates)
    {
        CredentialHashCandidate current = candidates[0];
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

    private static JsonElement? ReadNullableJson(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : JsonDocument.Parse(reader.GetString(ordinal)).RootElement.Clone();

    private static void AddJson(NpgsqlParameterCollection parameters, JsonElement value) =>
        parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = value.GetRawText(),
        });

    private static void AddNullableJson(
        NpgsqlParameterCollection parameters,
        JsonElement? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = value is null ? DBNull.Value : value.Value.GetRawText(),
        });

    private static void AddNullable(
        NpgsqlParameterCollection parameters,
        NpgsqlDbType type,
        object? value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = type,
            Value = value ?? DBNull.Value,
        });

    private static System.Net.IPAddress? NormalizeIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return System.Net.IPAddress.TryParse(value, out System.Net.IPAddress? address)
            ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            : null;
    }

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value)
            ? null
            : value.Length <= maximumLength ? value : value[..maximumLength];

    private static SystemRole ParseRole(string value) => value switch
    {
        "admin" => SystemRole.Admin,
        "operator" => SystemRole.Operator,
        "auditor" => SystemRole.Auditor,
        "user" => SystemRole.User,
        _ => throw new InvalidOperationException("Identity user has an unknown role."),
    };

    private static UserLifecycle ParseStatus(string value) => value switch
    {
        "active" => UserLifecycle.Active,
        "disabled" => UserLifecycle.Disabled,
        _ => throw new InvalidOperationException("Identity user has an unknown status."),
    };

    private static void EnsureExactlyOne(int affectedRows)
    {
        if (affectedRows != 1)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Identity session mutation affected {affectedRows} rows instead of one."));
        }
    }

    private sealed record RefreshLocator(
        EntityId Id,
        EntityId FamilyId,
        EntityId UserId,
        string Status,
        DateTimeOffset ExpiresAt);
}
#pragma warning restore MA0051
