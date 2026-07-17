#pragma warning disable MA0051 // Coverage scenarios keep each PostgreSQL state transition explicit.
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class IdentitySessionPostgresCoverageTests(PostgresRuntimeFixture fixture)
{
    private static readonly Guid AdminRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000001");
    private static readonly Guid OperatorRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000002");
    private static readonly Guid AuditorRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000003");
    private static readonly Guid UserRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000004");
    private readonly PostgresRuntimeFixture _fixture =
        fixture ?? throw new ArgumentNullException(nameof(fixture));

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReadersReturnEveryRoleAndFalseForUnknownCredentials()
    {
        // Governing contracts: DEC-004/005 and section 7.4.1. Authentication
        // reads run as poolai_api, return the canonical role, and do not turn
        // unknown users, challenges, or refresh digests into positive matches.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        Assert.Throws<ArgumentNullException>(
            static () => new PostgresIdentitySessionRepository(null!));

        Assert.Null(await runtime.Repository.FindAuthenticationUserAsync(
            $"missing-{EntityId.New()}@poolai.test",
            cancellationToken).ConfigureAwait(true));
        Assert.Null(await runtime.Repository.GetAuthenticationUserAsync(
            EntityId.New(),
            cancellationToken).ConfigureAwait(true));

        (Guid RoleId, SystemRole Role)[] roles =
        [
            (AdminRoleId, SystemRole.Admin),
            (OperatorRoleId, SystemRole.Operator),
            (AuditorRoleId, SystemRole.Auditor),
            (UserRoleId, SystemRole.User),
        ];
        foreach ((Guid roleId, SystemRole expectedRole) in roles)
        {
            AuthenticationUserSnapshot user = await InsertUserAsync(
                runtime.Repository,
                roleId,
                cancellationToken: cancellationToken).ConfigureAwait(true);
            AuthenticationUserSnapshot found = Assert.IsType<AuthenticationUserSnapshot>(
                await runtime.Repository.FindAuthenticationUserAsync(
                    user.NormalizedEmail,
                    cancellationToken).ConfigureAwait(true));
            Assert.Equal(expectedRole, found.Role);
            Assert.Null(found.TotpSecretEnvelope);
            Assert.Null(found.TotpLastAcceptedStep);
            Assert.Null(found.LockedUntil);
            Assert.Null(found.LastLoginAt);
        }

        CredentialHashCandidate[] unknown = Candidates();
        CredentialHashCandidate[] twoUnknown =
        [
            new(RandomNumberGenerator.GetBytes(32), 8),
            new(RandomNumberGenerator.GetBytes(32), 7),
        ];
        Assert.False(await runtime.Repository.HasRefreshCredentialAsync(
            unknown,
            cancellationToken).ConfigureAwait(true));
        Assert.False(await runtime.Repository.HasRefreshCredentialAsync(
            twoUnknown,
            cancellationToken).ConfigureAwait(true));
        Assert.Null(await runtime.Repository.FindTotpChallengeAsync(
            twoUnknown,
            "login",
            cancellationToken).ConfigureAwait(true));
        Assert.Null(await runtime.Repository.ReadCanonicalAuthorizationAsync(
            EntityId.New(),
            EntityId.New(),
            tokenVersion: 1,
            cancellationToken).ConfigureAwait(true));

        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.Repository
            .HasRefreshCredentialAsync(null!, cancellationToken)
            .AsTask()).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Repository
            .HasRefreshCredentialAsync([], cancellationToken)
            .AsTask()).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Repository
            .FindTotpChallengeAsync(
                [
                    .. unknown,
                    new CredentialHashCandidate(RandomNumberGenerator.GetBytes(32), 8),
                    new CredentialHashCandidate(RandomNumberGenerator.GetBytes(32), 9),
                ],
                "login",
                cancellationToken)
                .AsTask()).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CurrentUserStatusReaderReturnsEveryRoleLifecycleAndMissingUser()
    {
        // Governing contracts: DEC-031 and M1-E4 Group/Subscription authorization.
        // Cross-context callers receive one fresh canonical Identity projection;
        // missing users are explicit failures and disabled users remain observable.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NpgsqlDataSource dataSource = _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        Assert.Throws<ArgumentNullException>(
            static () => new PostgresIdentitySessionReader(null!));
        PostgresIdentitySessionReader reader = new(dataSource);

        Result<UserStatusSnapshot> missing = await reader.GetCurrentAsync(
            EntityId.New(),
            cancellationToken).ConfigureAwait(true);
        Assert.True(missing.IsFailure);
        Assert.Equal("resource_not_found", missing.Error.Code);

        SessionRuntime runtime = Runtime();
        (Guid RoleId, SystemRole Role)[] roles =
        [
            (AdminRoleId, SystemRole.Admin),
            (OperatorRoleId, SystemRole.Operator),
            (AuditorRoleId, SystemRole.Auditor),
            (UserRoleId, SystemRole.User),
        ];
        foreach ((Guid roleId, SystemRole expectedRole) in roles)
        {
            AuthenticationUserSnapshot user = await InsertUserAsync(
                runtime.Repository,
                roleId,
                cancellationToken: cancellationToken).ConfigureAwait(true);
            Result<UserStatusSnapshot> current = await reader.GetCurrentAsync(
                user.Id,
                cancellationToken).ConfigureAwait(true);

            Assert.True(current.IsSuccess);
            Assert.Equal(user.Id, current.Value.UserId);
            Assert.Equal(UserLifecycle.Active, current.Value.Lifecycle);
            Assert.Equal(expectedRole, current.Value.Role);
            Assert.Equal(user.TokenVersion, current.Value.TokenVersion);
            Assert.Equal(user.Version, current.Value.Version);
            Assert.True(current.Value.ObservedAt > DateTimeOffset.MinValue);
        }

        AuthenticationUserSnapshot disabled = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            status: "disabled",
            cancellationToken: cancellationToken).ConfigureAwait(true);
        Result<UserStatusSnapshot> disabledStatus = await reader.GetCurrentAsync(
            disabled.Id,
            cancellationToken).ConfigureAwait(true);
        Assert.True(disabledStatus.IsSuccess);
        Assert.Equal(UserLifecycle.Disabled, disabledStatus.Value.Lifecycle);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task EveryRequestStrongReadsUserRoleStatusAndTokenVersion()
    {
        // Governing contract: DEC-031. Each authorization call executes the
        // canonical PostgreSQL read again; an old positive result cannot survive
        // a committed role or lifecycle mutation.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        AuthenticationUserSnapshot user = await InsertUserAsync(
            runtime.Repository,
            OperatorRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        SessionFixture session = await CreateSessionAsync(
            runtime,
            user,
            Session(),
            cancellationToken).ConfigureAwait(true);

        UserStatusSnapshot initial = Assert.IsType<UserStatusSnapshot>(
            await runtime.Repository.ReadCanonicalAuthorizationAsync(
                user.Id,
                session.Write.Id,
                user.TokenVersion,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(SystemRole.Operator, initial.Role);
        Assert.Equal(UserLifecycle.Active, initial.Lifecycle);
        Assert.True(initial.ObservedAt > DateTimeOffset.MinValue);

        using (NpgsqlCommand changeRole = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.user_roles
            SET role_id = $2,
                assigned_at = clock_timestamp()
            WHERE user_id = $1;
            """))
        {
            changeRole.Parameters.AddWithValue(user.Id.Value);
            changeRole.Parameters.AddWithValue(AuditorRoleId);
            Assert.Equal(
                1,
                await changeRole.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        }

        Assert.Null(await runtime.Repository.ReadCanonicalAuthorizationAsync(
            user.Id,
            session.Write.Id,
            user.TokenVersion,
            cancellationToken).ConfigureAwait(true));
        AuthenticationUserSnapshot roleChanged = await RequireUserAsync(
            runtime.Repository,
            user.Id,
            cancellationToken).ConfigureAwait(true);
        UserStatusSnapshot canonicalRole = Assert.IsType<UserStatusSnapshot>(
            await runtime.Repository.ReadCanonicalAuthorizationAsync(
                user.Id,
                session.Write.Id,
                roleChanged.TokenVersion,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(SystemRole.Auditor, canonicalRole.Role);
        Assert.Equal(user.TokenVersion + 1, roleChanged.TokenVersion);

        using (NpgsqlCommand disable = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.users SET status = 'disabled' WHERE id = $1;
            """))
        {
            disable.Parameters.AddWithValue(user.Id.Value);
            Assert.Equal(
                1,
                await disable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        }

        Assert.Null(await runtime.Repository.ReadCanonicalAuthorizationAsync(
            user.Id,
            session.Write.Id,
            roleChanged.TokenVersion + 1,
            cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task OnlyFourFrozenRolesExistAndDatabasePreventsMultipleRolesPerUser()
    {
        // Governing contract: DEC-006. The migration-owned role catalog and the
        // one-role-per-user constraint are the persistence boundary for Policy.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using (NpgsqlCommand roles = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT code FROM public.roles ORDER BY code;
            """))
        using (NpgsqlDataReader reader = await roles.ExecuteReaderAsync(cancellationToken)
                   .ConfigureAwait(true))
        {
            List<string> actual = [];
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(true))
            {
                actual.Add(reader.GetString(0));
            }

            Assert.Equal(["admin", "auditor", "operator", "user"], actual);
        }

        SessionRuntime runtime = Runtime();
        AuthenticationUserSnapshot user = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        using NpgsqlCommand duplicate = _fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.user_roles (user_id, role_id) VALUES ($1, $2);
            """);
        duplicate.Parameters.AddWithValue(user.Id.Value);
        duplicate.Parameters.AddWithValue(OperatorRoleId);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => duplicate.ExecuteNonQueryAsync(cancellationToken)).ConfigureAwait(true);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("uq_user_roles_one_role_per_user", exception.ConstraintName);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PasswordLoginRejectsStaleDisabledLockedAndIncompleteWrites()
    {
        // Governing contract: section 7.4.1. Stale credentials, disabled users,
        // active locks, and missing MFA/session writes cannot create a session;
        // only an accepted password clears failure state and persists one family.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        AuthenticationUserSnapshot active = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        AuthenticationUserSnapshot disabled = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            status: "disabled",
            cancellationToken: cancellationToken).ConfigureAwait(true);
        AuthenticationUserSnapshot locked = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        await SetLockAsync(locked.Id, cancellationToken).ConfigureAwait(true);
        locked = await RequireUserAsync(runtime.Repository, locked.Id, cancellationToken)
            .ConfigureAwait(true);
        AuthenticationUserSnapshot totp = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            totpEnabled: true,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.Equal(
            PasswordFailureDisposition.Ignored,
            await RecordFailureAsync(
                runtime,
                EntityId.New(),
                EntityId.New(),
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            PasswordFailureDisposition.Ignored,
            await RecordFailureAsync(
                runtime,
                active.Id,
                EntityId.New(),
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            PasswordFailureDisposition.Ignored,
            await RecordFailureAsync(
                runtime,
                disabled.Id,
                disabled.SecurityStamp,
                cancellationToken).ConfigureAwait(true));

        PasswordLoginPersistenceResult missing = await CompletePasswordAsync(
            runtime,
            new PasswordLoginWrite(
                EntityId.New(),
                EntityId.New(),
                Session().Write,
                Challenge: null),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(PasswordLoginDisposition.StaleCredential, missing.Disposition);
        Assert.Null(missing.User);

        PasswordLoginPersistenceResult stale = await CompletePasswordAsync(
            runtime,
            new PasswordLoginWrite(
                active.Id,
                EntityId.New(),
                Session().Write,
                Challenge: null),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(PasswordLoginDisposition.StaleCredential, stale.Disposition);

        PasswordLoginPersistenceResult inactive = await CompletePasswordAsync(
            runtime,
            new PasswordLoginWrite(
                disabled.Id,
                disabled.SecurityStamp,
                Session().Write,
                Challenge: null),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(PasswordLoginDisposition.UserDisabled, inactive.Disposition);

        PasswordLoginPersistenceResult lockedResult = await CompletePasswordAsync(
            runtime,
            new PasswordLoginWrite(
                locked.Id,
                locked.SecurityStamp,
                Session().Write,
                Challenge: null),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(PasswordLoginDisposition.AccountLocked, lockedResult.Disposition);
        Assert.InRange(Assert.IsType<long>(lockedResult.RetryAfterSeconds), 1, 301);

        PasswordLoginPersistenceResult missingChallenge = await CompletePasswordAsync(
            runtime,
            new PasswordLoginWrite(
                totp.Id,
                totp.SecurityStamp,
                Session: null,
                Challenge: null),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(PasswordLoginDisposition.StaleCredential, missingChallenge.Disposition);

        PasswordLoginPersistenceResult missingSession = await CompletePasswordAsync(
            runtime,
            new PasswordLoginWrite(
                active.Id,
                active.SecurityStamp,
                Session: null,
                Challenge: null),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(PasswordLoginDisposition.StaleCredential, missingSession.Disposition);

        SessionFixture unusualSession = Session(
            ipAddress: "not-an-address",
            userAgent: new string('u', 600));
        PasswordLoginPersistenceResult successful = await CompletePasswordAsync(
            runtime,
            new PasswordLoginWrite(
                active.Id,
                active.SecurityStamp,
                unusualSession.Write,
                Challenge: null),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(PasswordLoginDisposition.SessionCreated, successful.Disposition);
        Assert.Equal(unusualSession.Write.Id, successful.SessionFamilyId);
        Assert.Equal(0, successful.User!.FailedLoginCount);
        Assert.Null(successful.User.LockedUntil);
        Assert.NotNull(successful.User.LastLoginAt);
        Assert.Equal(
            (null, 512),
            await ReadSessionClientAsync(
                unusualSession.Write.Id,
                cancellationToken).ConfigureAwait(true));

        AuthenticationUserSnapshot mappedUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        SessionFixture mapped = Session("::ffff:192.0.2.44", string.Empty);
        Assert.Equal(
            PasswordLoginDisposition.SessionCreated,
            (await CompletePasswordAsync(
                runtime,
                new PasswordLoginWrite(
                    mappedUser.Id,
                    mappedUser.SecurityStamp,
                    mapped.Write,
                    Challenge: null),
                cancellationToken).ConfigureAwait(true)).Disposition);
        Assert.Equal(
            ("192.0.2.44", null),
            await ReadSessionClientAsync(mapped.Write.Id, cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LoginChallengesRejectUnknownExpiredRevokedAndChangedUsers()
    {
        // Governing contracts: AC-002/034 and section 7.4.1. PostgreSQL expiry,
        // terminal state, lifecycle, TOTP state, and frozen security snapshots
        // all collapse to the same invalid challenge result.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        CredentialHashCandidate[] unknown = Candidates();
        Assert.Equal(
            MfaLoginDisposition.ChallengeInvalid,
            (await CompleteMfaAsync(
                runtime,
                unknown,
                acceptedStep: 101,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot expiredUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            totpEnabled: true,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        ChallengeFixture expired = await CreateLoginChallengeAsync(
            runtime,
            expiredUser,
            cancellationToken).ConfigureAwait(true);
        TotpChallengeSnapshot visible = Assert.IsType<TotpChallengeSnapshot>(
            await runtime.Repository.FindTotpChallengeAsync(
                [
                    new CredentialHashCandidate(RandomNumberGenerator.GetBytes(32), 8),
                    expired.Candidates[0],
                ],
                "login",
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(expired.Write.Id, visible.Id);
        Assert.Null(visible.SecretEnvelope);
        Assert.Null(visible.ResponseBodyEnvelope);
        Assert.Null(await runtime.Repository.FindTotpChallengeAsync(
            expired.Candidates,
            "setup",
            cancellationToken).ConfigureAwait(true));
        await ExpireChallengeAsync(expired.Write.Id, cancellationToken).ConfigureAwait(true);
        Assert.Null(await runtime.Repository.FindTotpChallengeAsync(
            expired.Candidates,
            "login",
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            MfaLoginDisposition.ChallengeInvalid,
            (await CompleteMfaAsync(
                runtime,
                expired.Candidates,
                acceptedStep: 102,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot revokedUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            totpEnabled: true,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        ChallengeFixture revoked = await CreateLoginChallengeAsync(
            runtime,
            revokedUser,
            cancellationToken).ConfigureAwait(true);
        await RevokeChallengeAsync(revoked.Write.Id, cancellationToken).ConfigureAwait(true);
        Assert.Null(await runtime.Repository.FindTotpChallengeAsync(
            revoked.Candidates,
            "login",
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            MfaLoginDisposition.ChallengeInvalid,
            (await CompleteMfaAsync(
                runtime,
                revoked.Candidates,
                acceptedStep: 103,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot disabledUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            totpEnabled: true,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        ChallengeFixture disabledChallenge = await CreateLoginChallengeAsync(
            runtime,
            disabledUser,
            cancellationToken).ConfigureAwait(true);
        await DisableUserAsync(disabledUser.Id, softDelete: false, cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(
            MfaLoginDisposition.ChallengeInvalid,
            (await CompleteMfaAsync(
                runtime,
                disabledChallenge.Candidates,
                acceptedStep: 104,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot noTotpUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        ChallengeFixture noTotpChallenge = await InsertChallengeAsync(
            noTotpUser,
            "login",
            cancellationToken: cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            MfaLoginDisposition.ChallengeInvalid,
            (await CompleteMfaAsync(
                runtime,
                noTotpChallenge.Candidates,
                acceptedStep: 105,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot stampUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            totpEnabled: true,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        ChallengeFixture stampChallenge = await CreateLoginChallengeAsync(
            runtime,
            stampUser,
            cancellationToken).ConfigureAwait(true);
        await ChangeSecurityStampAsync(stampUser.Id, cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            MfaLoginDisposition.ChallengeInvalid,
            (await CompleteMfaAsync(
                runtime,
                stampChallenge.Candidates,
                acceptedStep: 106,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot deletedUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            totpEnabled: true,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        ChallengeFixture deletedChallenge = await CreateLoginChallengeAsync(
            runtime,
            deletedUser,
            cancellationToken).ConfigureAwait(true);
        await DisableUserAsync(deletedUser.Id, softDelete: true, cancellationToken)
            .ConfigureAwait(true);
        Assert.Null(await runtime.Repository.GetAuthenticationUserAsync(
            deletedUser.Id,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            MfaLoginDisposition.ChallengeInvalid,
            (await CompleteMfaAsync(
                runtime,
                deletedChallenge.Candidates,
                acceptedStep: 107,
                cancellationToken).ConfigureAwait(true)).Disposition);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RefreshRotationRejectsEveryTerminalStateAndReusedGenerationRevokesFamily()
    {
        // Governing contract: AC-003 and section 7.4.1. The digest preflight
        // includes historical states, while the transaction rejects unknown,
        // expired, revoked, and disabled-user credentials and revokes a family
        // when a rotated generation is reused.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        CredentialHashCandidate[] unknown = Candidates();
        Assert.Equal(
            RefreshRotationDisposition.Invalid,
            (await RotateAsync(runtime, unknown, cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot expiredUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        SessionFixture expired = await CreateSessionAsync(
            runtime,
            expiredUser,
            Session(ipAddress: " ", userAgent: null),
            cancellationToken).ConfigureAwait(true);
        await ExpireRefreshAsync(expired.Write.Id, cancellationToken).ConfigureAwait(true);
        Assert.Null(await runtime.Repository.ReadCanonicalAuthorizationAsync(
            expiredUser.Id,
            expired.Write.Id,
            expiredUser.TokenVersion,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            RefreshRotationDisposition.Invalid,
            (await RotateAsync(runtime, expired.Candidates, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        Assert.Equal(
            "expired",
            await ReadRefreshStatusAsync(expired.Write.Id, cancellationToken).ConfigureAwait(true));

        AuthenticationUserSnapshot revokedUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        SessionFixture revoked = await CreateSessionAsync(
            runtime,
            revokedUser,
            Session(ipAddress: null, userAgent: null),
            cancellationToken).ConfigureAwait(true);
        await SetRefreshRevokedAsync(revoked.Write.Id, cancellationToken).ConfigureAwait(true);
        Assert.True(await runtime.Repository.HasRefreshCredentialAsync(
            revoked.Candidates,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            RefreshRotationDisposition.Invalid,
            (await RotateAsync(runtime, revoked.Candidates, cancellationToken)
                .ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot disabledUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        SessionFixture disabled = await CreateSessionAsync(
            runtime,
            disabledUser,
            Session(),
            cancellationToken).ConfigureAwait(true);
        await DisableUserAsync(disabledUser.Id, softDelete: false, cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(
            RefreshRotationDisposition.Invalid,
            (await RotateAsync(runtime, disabled.Candidates, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        Assert.Null(await runtime.Repository.ReadCanonicalAuthorizationAsync(
            disabledUser.Id,
            disabled.Write.Id,
            disabledUser.TokenVersion + 1,
            cancellationToken).ConfigureAwait(true));

        AuthenticationUserSnapshot reusedUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        SessionFixture original = await CreateSessionAsync(
            runtime,
            reusedUser,
            Session(),
            cancellationToken).ConfigureAwait(true);
        Assert.True(await runtime.Repository.HasRefreshCredentialAsync(
            [
                new CredentialHashCandidate(RandomNumberGenerator.GetBytes(32), 8),
                original.Candidates[0],
            ],
            cancellationToken).ConfigureAwait(true));
        SessionFixture replacement = Session("2001:db8::7", "short-agent");
        RefreshRotationPersistenceResult rotated = await RotateAsync(
            runtime,
            original.Candidates,
            cancellationToken,
            replacement).ConfigureAwait(true);
        Assert.Equal(RefreshRotationDisposition.Rotated, rotated.Disposition);
        Assert.Equal(original.Write.Id, rotated.SessionFamilyId);
        Assert.NotNull(await runtime.Repository.ReadCanonicalAuthorizationAsync(
            reusedUser.Id,
            original.Write.Id,
            reusedUser.TokenVersion,
            cancellationToken).ConfigureAwait(true));

        RefreshRotationPersistenceResult reused = await RotateAsync(
            runtime,
            original.Candidates,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(RefreshRotationDisposition.Reused, reused.Disposition);
        Assert.Equal(original.Write.Id, reused.SessionFamilyId);
        Assert.Null(await runtime.Repository.ReadCanonicalAuthorizationAsync(
            reusedUser.Id,
            original.Write.Id,
            reusedUser.TokenVersion,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            (1, "refresh_reuse"),
            await ReadFamilyRevocationAsync(
                original.Write.Id,
                cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LogoutTreatsUnknownOwnershipAndEmptyScopesAsNoOps()
    {
        // Governing contract: AC-003 and section 7.4.1. Logout never reveals
        // refresh ownership: missing actors, unknown or foreign credentials,
        // mismatched sid values, and already-empty scopes all return a no-op.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        SessionActor missingActor = new(
            EntityId.New(),
            SystemRole.User,
            TokenVersion: 1,
            EntityId.New());
        LogoutPersistenceResult missing = await LogoutAsync(
            runtime,
            missingActor,
            [],
            allSessions: false,
            cancellationToken).ConfigureAwait(true);
        Assert.Null(missing.User);
        Assert.False(missing.Changed);

        AuthenticationUserSnapshot owner = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        SessionFixture owned = await CreateSessionAsync(
            runtime,
            owner,
            Session(),
            cancellationToken).ConfigureAwait(true);
        SessionActor actor = new(owner.Id, owner.Role, owner.TokenVersion, owned.Write.Id);

        LogoutPersistenceResult unknown = await LogoutAsync(
            runtime,
            actor,
            Candidates(),
            allSessions: false,
            cancellationToken).ConfigureAwait(true);
        Assert.False(unknown.Changed);

        LogoutPersistenceResult wrongSid = await LogoutAsync(
            runtime,
            actor with { SessionFamilyId = EntityId.New() },
            owned.Candidates,
            allSessions: false,
            cancellationToken).ConfigureAwait(true);
        Assert.False(wrongSid.Changed);

        AuthenticationUserSnapshot foreignUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        SessionFixture foreign = await CreateSessionAsync(
            runtime,
            foreignUser,
            Session(),
            cancellationToken).ConfigureAwait(true);
        LogoutPersistenceResult foreignToken = await LogoutAsync(
            runtime,
            actor,
            foreign.Candidates,
            allSessions: false,
            cancellationToken).ConfigureAwait(true);
        Assert.False(foreignToken.Changed);

        LogoutPersistenceResult current = await LogoutAsync(
            runtime,
            actor,
            owned.Candidates,
            allSessions: false,
            cancellationToken).ConfigureAwait(true);
        Assert.True(current.Changed);
        LogoutPersistenceResult currentReplay = await LogoutAsync(
            runtime,
            actor,
            [],
            allSessions: false,
            cancellationToken).ConfigureAwait(true);
        Assert.False(currentReplay.Changed);

        AuthenticationUserSnapshot emptyUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        SessionActor emptyActor = new(
            emptyUser.Id,
            emptyUser.Role,
            emptyUser.TokenVersion,
            EntityId.New());
        LogoutPersistenceResult allEmpty = await LogoutAsync(
            runtime,
            emptyActor,
            [],
            allSessions: true,
            cancellationToken).ConfigureAwait(true);
        Assert.False(allEmpty.Changed);
        Assert.Equal(emptyUser.TokenVersion, allEmpty.User!.TokenVersion);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PasswordMutationReturnsEveryConflictAndRevokesSecurityArtifacts()
    {
        // Governing contracts: DEC-005/026 and section 7.4.1. Password changes
        // require the current version/security stamp and atomically revoke the
        // user's refresh families and open TOTP challenges.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        AuthenticationUserSnapshot user = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        SessionFixture session = await CreateSessionAsync(
            runtime,
            user,
            Session(),
            cancellationToken).ConfigureAwait(true);
        ChallengeFixture setup = SetupChallenge(user, includeResponseEnvelope: false);
        Assert.Equal(
            SecurityMutationDisposition.Updated,
            (await CreateSetupAsync(runtime, user, setup, cancellationToken)
                .ConfigureAwait(true)).Disposition);

        Assert.Equal(
            SecurityMutationDisposition.NotFound,
            (await ChangePasswordAsync(
                runtime,
                EntityId.New(),
                expectedVersion: 1,
                EntityId.New(),
                cancellationToken).ConfigureAwait(true)).Disposition);
        Assert.Equal(
            SecurityMutationDisposition.InvalidCredential,
            (await ChangePasswordAsync(
                runtime,
                user.Id,
                user.Version,
                EntityId.New(),
                cancellationToken).ConfigureAwait(true)).Disposition);
        SecurityMutationPersistenceResult conflict = await ChangePasswordAsync(
            runtime,
            user.Id,
            user.Version + 1,
            user.SecurityStamp,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SecurityMutationDisposition.VersionConflict, conflict.Disposition);
        Assert.Equal(user.Version, conflict.CurrentVersion);

        AuthenticationUserSnapshot disabled = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            status: "disabled",
            cancellationToken: cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SecurityMutationDisposition.InvalidCredential,
            (await ChangePasswordAsync(
                runtime,
                disabled.Id,
                disabled.Version,
                disabled.SecurityStamp,
                cancellationToken).ConfigureAwait(true)).Disposition);

        EntityId newStamp = EntityId.New();
        SecurityMutationPersistenceResult updated = await InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.ChangePasswordAsync(
                user.Id,
                user.Version,
                user.SecurityStamp,
                "poolai-password-v1:coverage-updated",
                newStamp,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SecurityMutationDisposition.Updated, updated.Disposition);
        Assert.Equal(newStamp, updated.User!.SecurityStamp);
        Assert.Equal(user.TokenVersion + 1, updated.User.TokenVersion);
        Assert.Equal(user.Version + 1, updated.User.Version);
        Assert.Equal(
            "revoked:password_changed",
            await ReadRefreshTerminalAsync(session.Write.Id, cancellationToken)
                .ConfigureAwait(true));
        Assert.Equal(
            "revoked:security_changed",
            await ReadChallengeTerminalAsync(setup.Write.Id, cancellationToken)
                .ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task TotpSetupRejectsInvalidActorsAndSupersedesSecretResponses()
    {
        // Governing contract: section 7.4.1. Setup is limited to an active user
        // with the matching stamp, rejects an existing TOTP enrollment, and
        // atomically supersedes the prior open setup while preserving the
        // optional encrypted idempotent response.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        ChallengeFixture missingChallenge = SetupChallenge(
            EntityId.New(),
            EntityId.New(),
            tokenVersion: 1,
            includeResponseEnvelope: false);
        Assert.Equal(
            SecurityMutationDisposition.InvalidCredential,
            (await CreateSetupAsync(
                runtime,
                EntityId.New(),
                EntityId.New(),
                missingChallenge,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot disabled = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            status: "disabled",
            cancellationToken: cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SecurityMutationDisposition.InvalidCredential,
            (await CreateSetupAsync(
                runtime,
                disabled,
                SetupChallenge(disabled, includeResponseEnvelope: false),
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot active = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SecurityMutationDisposition.InvalidCredential,
            (await CreateSetupAsync(
                runtime,
                active.Id,
                EntityId.New(),
                SetupChallenge(active, includeResponseEnvelope: false),
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot enabled = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            totpEnabled: true,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SecurityMutationDisposition.TotpAlreadyEnabled,
            (await CreateSetupAsync(
                runtime,
                enabled,
                SetupChallenge(enabled, includeResponseEnvelope: false),
                cancellationToken).ConfigureAwait(true)).Disposition);

        ChallengeFixture first = SetupChallenge(active, includeResponseEnvelope: false);
        SecurityMutationPersistenceResult created = await CreateSetupAsync(
            runtime,
            active,
            first,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SecurityMutationDisposition.Updated, created.Disposition);
        TotpChallengeSnapshot firstVisible = Assert.IsType<TotpChallengeSnapshot>(
            await runtime.Repository.FindTotpChallengeAsync(
                first.Candidates,
                "setup",
                cancellationToken).ConfigureAwait(true));
        Assert.NotNull(firstVisible.SecretEnvelope);
        Assert.Null(firstVisible.ResponseBodyEnvelope);

        ChallengeFixture second = SetupChallenge(active, includeResponseEnvelope: true);
        Assert.Equal(
            SecurityMutationDisposition.Updated,
            (await CreateSetupAsync(runtime, active, second, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        Assert.Null(await runtime.Repository.FindTotpChallengeAsync(
            first.Candidates,
            "setup",
            cancellationToken).ConfigureAwait(true));
        TotpChallengeSnapshot secondVisible = Assert.IsType<TotpChallengeSnapshot>(
            await runtime.Repository.FindTotpChallengeAsync(
                second.Candidates,
                "setup",
                cancellationToken).ConfigureAwait(true));
        Assert.NotNull(secondVisible.SecretEnvelope);
        Assert.NotNull(secondVisible.ResponseBodyEnvelope);
        Assert.Equal(
            "revoked:superseded",
            await ReadChallengeTerminalAsync(first.Write.Id, cancellationToken)
                .ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task TotpConfirmRejectsMissingConflictingInvalidAndReplayStates()
    {
        // Governing contracts: AC-002/034 and section 7.4.1. Confirmation
        // rejects unknown users, stale versions, existing enrollment, expired
        // or revoked challenges, cross-user candidates, snapshot drift, and an
        // already accepted TOTP step without mutating enrollment state.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        CredentialHashCandidate[] unknown = Candidates();
        Assert.Equal(
            SecurityMutationDisposition.NotFound,
            (await ConfirmAsync(
                runtime,
                EntityId.New(),
                expectedVersion: 1,
                unknown,
                acceptedStep: 200,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot conflictUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SecurityMutationDisposition.VersionConflict,
            (await ConfirmAsync(
                runtime,
                conflictUser.Id,
                conflictUser.Version + 1,
                unknown,
                acceptedStep: 201,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot enabledUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            totpEnabled: true,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SecurityMutationDisposition.TotpAlreadyEnabled,
            (await ConfirmAsync(
                runtime,
                enabledUser.Id,
                enabledUser.Version,
                unknown,
                acceptedStep: 202,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot noChallengeUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SecurityMutationDisposition.ChallengeInvalid,
            (await ConfirmAsync(
                runtime,
                noChallengeUser.Id,
                noChallengeUser.Version,
                unknown,
                acceptedStep: 203,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot challengeOwner = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        ChallengeFixture foreignChallenge = SetupChallenge(
            challengeOwner,
            includeResponseEnvelope: false);
        Assert.Equal(
            SecurityMutationDisposition.Updated,
            (await CreateSetupAsync(
                runtime,
                challengeOwner,
                foreignChallenge,
                cancellationToken).ConfigureAwait(true)).Disposition);
        Assert.Equal(
            SecurityMutationDisposition.ChallengeInvalid,
            (await ConfirmAsync(
                runtime,
                noChallengeUser.Id,
                noChallengeUser.Version,
                foreignChallenge.Candidates,
                acceptedStep: 204,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot stampUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        ChallengeFixture wrongStamp = SetupChallenge(
            stampUser.Id,
            EntityId.New(),
            stampUser.TokenVersion,
            includeResponseEnvelope: false);
        Assert.Equal(
            SecurityMutationDisposition.Updated,
            (await CreateSetupAsync(runtime, stampUser, wrongStamp, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        Assert.Equal(
            SecurityMutationDisposition.ChallengeInvalid,
            (await ConfirmAsync(
                runtime,
                stampUser.Id,
                stampUser.Version,
                wrongStamp.Candidates,
                acceptedStep: 205,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot tokenUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        ChallengeFixture wrongToken = SetupChallenge(
            tokenUser.Id,
            tokenUser.SecurityStamp,
            tokenUser.TokenVersion + 1,
            includeResponseEnvelope: false);
        Assert.Equal(
            SecurityMutationDisposition.Updated,
            (await CreateSetupAsync(runtime, tokenUser, wrongToken, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        Assert.Equal(
            SecurityMutationDisposition.ChallengeInvalid,
            (await ConfirmAsync(
                runtime,
                tokenUser.Id,
                tokenUser.Version,
                wrongToken.Candidates,
                acceptedStep: 206,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot expiredUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        ChallengeFixture expired = SetupChallenge(expiredUser, includeResponseEnvelope: false);
        Assert.Equal(
            SecurityMutationDisposition.Updated,
            (await CreateSetupAsync(runtime, expiredUser, expired, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        await ExpireChallengeAsync(expired.Write.Id, cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SecurityMutationDisposition.ChallengeInvalid,
            (await ConfirmAsync(
                runtime,
                expiredUser.Id,
                expiredUser.Version,
                expired.Candidates,
                acceptedStep: 207,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot revokedUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        ChallengeFixture revoked = SetupChallenge(revokedUser, includeResponseEnvelope: false);
        Assert.Equal(
            SecurityMutationDisposition.Updated,
            (await CreateSetupAsync(runtime, revokedUser, revoked, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        await RevokeChallengeAsync(revoked.Write.Id, cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SecurityMutationDisposition.ChallengeInvalid,
            (await ConfirmAsync(
                runtime,
                revokedUser.Id,
                revokedUser.Version,
                revoked.Candidates,
                acceptedStep: 208,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot replayUser = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        ChallengeFixture replay = SetupChallenge(replayUser, includeResponseEnvelope: false);
        Assert.Equal(
            SecurityMutationDisposition.Updated,
            (await CreateSetupAsync(runtime, replayUser, replay, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        await SetAcceptedStepAsync(replayUser.Id, 300, cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SecurityMutationDisposition.TotpReplay,
            (await ConfirmAsync(
                runtime,
                replayUser.Id,
                replayUser.Version,
                replay.Candidates,
                acceptedStep: 300,
                cancellationToken).ConfigureAwait(true)).Disposition);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task TotpLifecyclePersistsRecoveryCodesAndDisableRevokesEveryCredential()
    {
        // Governing contracts: AC-002/003/034 and section 7.4.1. A successful
        // setup/confirm stores eight digests and revokes sessions; later MFA
        // creates a fresh family, while disable rejects replay and atomically
        // revokes that family, every recovery digest, and every open challenge.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        AuthenticationUserSnapshot user = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        SessionFixture preEnrollment = await CreateSessionAsync(
            runtime,
            user,
            Session(ipAddress: null, userAgent: null),
            cancellationToken).ConfigureAwait(true);
        ChallengeFixture setup = SetupChallenge(user, includeResponseEnvelope: true);
        Assert.Equal(
            SecurityMutationDisposition.Updated,
            (await CreateSetupAsync(runtime, user, setup, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        TotpRecoveryCodeWrite[] recoveryCodes = RecoveryCodes();
        SecurityMutationPersistenceResult confirmed = await ConfirmAsync(
            runtime,
            user.Id,
            user.Version,
            setup.Candidates,
            acceptedStep: 400,
            cancellationToken,
            recoveryCodes).ConfigureAwait(true);
        Assert.Equal(SecurityMutationDisposition.Updated, confirmed.Disposition);
        AuthenticationUserSnapshot enabled = Assert.IsType<AuthenticationUserSnapshot>(
            confirmed.User);
        Assert.True(enabled.TotpEnabled);
        Assert.Equal(400, enabled.TotpLastAcceptedStep);
        Assert.Equal(user.TokenVersion + 1, enabled.TokenVersion);
        Assert.Equal(user.Version + 1, enabled.Version);
        Assert.Equal(
            "revoked:totp_enabled",
            await ReadRefreshTerminalAsync(preEnrollment.Write.Id, cancellationToken)
                .ConfigureAwait(true));
        Assert.Equal(8, await CountRecoveryCodesAsync(user.Id, cancellationToken)
            .ConfigureAwait(true));
        Assert.Equal(
            "used",
            await ReadChallengeTerminalAsync(setup.Write.Id, cancellationToken)
                .ConfigureAwait(true));

        ChallengeFixture login = await CreateLoginChallengeAsync(
            runtime,
            enabled,
            cancellationToken).ConfigureAwait(true);
        SessionFixture activeSession = Session(ipAddress: "192.0.2.88", userAgent: "mfa-agent");
        MfaLoginPersistenceResult mfa = await InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.CompleteMfaLoginAsync(
                login.Candidates,
                acceptedStep: 401,
                activeSession.Write,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(MfaLoginDisposition.SessionCreated, mfa.Disposition);

        AuthenticationUserSnapshot current = await RequireUserAsync(
            runtime.Repository,
            user.Id,
            cancellationToken).ConfigureAwait(true);
        ChallengeFixture openLogin = await CreateLoginChallengeAsync(
            runtime,
            current,
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(
            SecurityMutationDisposition.NotFound,
            (await DisableTotpAsync(
                runtime,
                EntityId.New(),
                expectedVersion: 1,
                EntityId.New(),
                acceptedStep: 1,
                cancellationToken).ConfigureAwait(true)).Disposition);
        Assert.Equal(
            SecurityMutationDisposition.InvalidCredential,
            (await DisableTotpAsync(
                runtime,
                current.Id,
                current.Version,
                EntityId.New(),
                acceptedStep: 402,
                cancellationToken).ConfigureAwait(true)).Disposition);
        Assert.Equal(
            SecurityMutationDisposition.VersionConflict,
            (await DisableTotpAsync(
                runtime,
                current.Id,
                current.Version + 1,
                current.SecurityStamp,
                acceptedStep: 402,
                cancellationToken).ConfigureAwait(true)).Disposition);
        Assert.Equal(
            SecurityMutationDisposition.TotpReplay,
            (await DisableTotpAsync(
                runtime,
                current.Id,
                current.Version,
                current.SecurityStamp,
                acceptedStep: 401,
                cancellationToken).ConfigureAwait(true)).Disposition);

        AuthenticationUserSnapshot notEnabled = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            SecurityMutationDisposition.TotpNotEnabled,
            (await DisableTotpAsync(
                runtime,
                notEnabled.Id,
                notEnabled.Version,
                notEnabled.SecurityStamp,
                acceptedStep: 1,
                cancellationToken).ConfigureAwait(true)).Disposition);

        SecurityMutationPersistenceResult disabled = await DisableTotpAsync(
            runtime,
            current.Id,
            current.Version,
            current.SecurityStamp,
            acceptedStep: 402,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(SecurityMutationDisposition.Updated, disabled.Disposition);
        Assert.False(disabled.User!.TotpEnabled);
        Assert.Null(disabled.User.TotpLastAcceptedStep);
        Assert.Equal(current.TokenVersion + 1, disabled.User.TokenVersion);
        Assert.Equal(current.Version + 1, disabled.User.Version);
        Assert.Equal(
            "revoked:totp_disabled",
            await ReadRefreshTerminalAsync(activeSession.Write.Id, cancellationToken)
                .ConfigureAwait(true));
        Assert.Equal(
            "revoked:totp_disabled",
            await ReadChallengeTerminalAsync(openLogin.Write.Id, cancellationToken)
                .ConfigureAwait(true));
        Assert.Equal(
            (8, "totp_disabled"),
            await ReadRecoveryRevocationsAsync(user.Id, cancellationToken)
                .ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task TotpReenableRevokesLegacyCodesAndRecoveryConsumptionCasWinsOnce()
    {
        // Governing contract: database sections 3 and 11. Confirm defensively
        // revokes any active legacy batch before inserting eight replacements;
        // the reserved recovery-code CAS has exactly one concurrent winner even
        // though R1 exposes no recovery-code authentication endpoint.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        AuthenticationUserSnapshot user = await InsertUserAsync(
            runtime.Repository,
            UserRoleId,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        TotpRecoveryCodeWrite legacy = new(
            EntityId.New(),
            RandomNumberGenerator.GetBytes(32),
            PepperVersion: 7);
        await InsertRecoveryCodeAsync(user.Id, legacy, cancellationToken).ConfigureAwait(true);

        ChallengeFixture setup = SetupChallenge(user, includeResponseEnvelope: true);
        Assert.Equal(
            SecurityMutationDisposition.Updated,
            (await CreateSetupAsync(runtime, user, setup, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        TotpRecoveryCodeWrite[] replacements = RecoveryCodes();
        Assert.Equal(
            SecurityMutationDisposition.Updated,
            (await ConfirmAsync(
                runtime,
                user.Id,
                user.Version,
                setup.Candidates,
                acceptedStep: 500,
                cancellationToken,
                replacements).ConfigureAwait(true)).Disposition);

        Assert.Equal(
            "revoked:totp_reenabled",
            await ReadRecoveryTerminalAsync(legacy.Id, cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            8,
            await CountActiveRecoveryCodesAsync(user.Id, cancellationToken).ConfigureAwait(true));

        Task<bool>[] attempts =
        [
            TryConsumeRecoveryCodeAsync(user.Id, replacements[0], cancellationToken).AsTask(),
            TryConsumeRecoveryCodeAsync(user.Id, replacements[0], cancellationToken).AsTask(),
        ];
        bool[] results = await Task.WhenAll(attempts).ConfigureAwait(true);
        Assert.Equal(1, results.Count(static consumed => consumed));
        Assert.False(await TryConsumeRecoveryCodeAsync(
            user.Id,
            replacements[0],
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            "used",
            await ReadRecoveryTerminalAsync(replacements[0].Id, cancellationToken)
                .ConfigureAwait(true));
    }

    private SessionRuntime Runtime()
    {
        NpgsqlDataSource dataSource = _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        return new SessionRuntime(
            new PostgresIdentitySessionRepository(dataSource),
            _fixture.ApiServices.GetRequiredService<IUnitOfWorkFactory>());
    }

    private async ValueTask<AuthenticationUserSnapshot> InsertUserAsync(
        PostgresIdentitySessionRepository repository,
        Guid roleId,
        string status = "active",
        bool totpEnabled = false,
        long? acceptedStep = null,
        CancellationToken cancellationToken = default)
    {
        EntityId id = EntityId.New();
        string email = $"coverage-{id.Value:N}@poolai.test";
        using (NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.users (
                id, email, normalized_email, display_name, password_hash,
                status, totp_secret_envelope, totp_last_accepted_step, security_stamp
            ) VALUES (
                $1, $2, $2, 'Session coverage', 'poolai-password-v1:coverage',
                $3, $4::jsonb, $5, $6
            );
            """))
        {
            command.Parameters.AddWithValue(id.Value);
            command.Parameters.AddWithValue(email);
            command.Parameters.AddWithValue(status);
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
                Value = totpEnabled ? Envelope("totp-user").GetRawText() : DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint,
                Value = acceptedStep is null ? DBNull.Value : acceptedStep.Value,
            });
            command.Parameters.AddWithValue(EntityId.New().Value);
            Assert.Equal(
                1,
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        using (NpgsqlCommand role = _fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.user_roles (user_id, role_id) VALUES ($1, $2);
            """))
        {
            role.Parameters.AddWithValue(id.Value);
            role.Parameters.AddWithValue(roleId);
            Assert.Equal(
                1,
                await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        return await RequireUserAsync(repository, id, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<AuthenticationUserSnapshot> RequireUserAsync(
        PostgresIdentitySessionRepository repository,
        EntityId userId,
        CancellationToken cancellationToken) => Assert.IsType<AuthenticationUserSnapshot>(
            await repository.GetAuthenticationUserAsync(userId, cancellationToken)
                .ConfigureAwait(false));

    private static ValueTask<PasswordFailureDisposition> RecordFailureAsync(
        SessionRuntime runtime,
        EntityId userId,
        EntityId securityStamp,
        CancellationToken cancellationToken) => InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.RecordPasswordFailureAsync(
                userId,
                securityStamp,
                maximumFailures: 3,
                TimeSpan.FromMinutes(5),
                context,
                token),
            cancellationToken);

    private static ValueTask<PasswordLoginPersistenceResult> CompletePasswordAsync(
        SessionRuntime runtime,
        PasswordLoginWrite write,
        CancellationToken cancellationToken) => InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.CompletePasswordLoginAsync(
                write,
                context,
                token),
            cancellationToken);

    private static async ValueTask<ChallengeFixture> CreateLoginChallengeAsync(
        SessionRuntime runtime,
        AuthenticationUserSnapshot user,
        CancellationToken cancellationToken)
    {
        ChallengeFixture challenge = LoginChallenge(user);
        PasswordLoginPersistenceResult result = await CompletePasswordAsync(
            runtime,
            new PasswordLoginWrite(user.Id, user.SecurityStamp, Session: null, challenge.Write),
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(PasswordLoginDisposition.MfaRequired, result.Disposition);
        return challenge;
    }

    private static ValueTask<MfaLoginPersistenceResult> CompleteMfaAsync(
        SessionRuntime runtime,
        IReadOnlyList<CredentialHashCandidate> candidates,
        long acceptedStep,
        CancellationToken cancellationToken) => InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.CompleteMfaLoginAsync(
                candidates,
                acceptedStep,
                Session().Write,
                context,
                token),
            cancellationToken);

    private static async ValueTask<SessionFixture> CreateSessionAsync(
        SessionRuntime runtime,
        AuthenticationUserSnapshot user,
        SessionFixture session,
        CancellationToken cancellationToken)
    {
        PasswordLoginPersistenceResult result = await CompletePasswordAsync(
            runtime,
            new PasswordLoginWrite(user.Id, user.SecurityStamp, session.Write, Challenge: null),
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(PasswordLoginDisposition.SessionCreated, result.Disposition);
        return session;
    }

    private static ValueTask<RefreshRotationPersistenceResult> RotateAsync(
        SessionRuntime runtime,
        IReadOnlyList<CredentialHashCandidate> candidates,
        CancellationToken cancellationToken,
        SessionFixture? replacement = null) => InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.RotateRefreshSessionAsync(
                candidates,
                (replacement ?? Session()).Write,
                context,
                token),
            cancellationToken);

    private static ValueTask<LogoutPersistenceResult> LogoutAsync(
        SessionRuntime runtime,
        SessionActor actor,
        IReadOnlyList<CredentialHashCandidate> candidates,
        bool allSessions,
        CancellationToken cancellationToken) => InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.LogoutAsync(
                actor,
                candidates,
                allSessions,
                context,
                token),
            cancellationToken);

    private static ValueTask<SecurityMutationPersistenceResult> ChangePasswordAsync(
        SessionRuntime runtime,
        EntityId userId,
        long expectedVersion,
        EntityId expectedSecurityStamp,
        CancellationToken cancellationToken) => InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.ChangePasswordAsync(
                userId,
                expectedVersion,
                expectedSecurityStamp,
                "poolai-password-v1:coverage-rejected",
                EntityId.New(),
                context,
                token),
            cancellationToken);

    private static ValueTask<SecurityMutationPersistenceResult> CreateSetupAsync(
        SessionRuntime runtime,
        AuthenticationUserSnapshot user,
        ChallengeFixture challenge,
        CancellationToken cancellationToken) => CreateSetupAsync(
            runtime,
            user.Id,
            user.SecurityStamp,
            challenge,
            cancellationToken);

    private static ValueTask<SecurityMutationPersistenceResult> CreateSetupAsync(
        SessionRuntime runtime,
        EntityId userId,
        EntityId expectedSecurityStamp,
        ChallengeFixture challenge,
        CancellationToken cancellationToken) => InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.CreateTotpSetupAsync(
                userId,
                expectedSecurityStamp,
                challenge.Write,
                context,
                token),
            cancellationToken);

    private static ValueTask<SecurityMutationPersistenceResult> ConfirmAsync(
        SessionRuntime runtime,
        EntityId userId,
        long expectedVersion,
        IReadOnlyList<CredentialHashCandidate> candidates,
        long acceptedStep,
        CancellationToken cancellationToken,
        IReadOnlyList<TotpRecoveryCodeWrite>? recoveryCodes = null) => InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.ConfirmTotpAsync(
                new TotpConfirmWrite(
                    userId,
                    expectedVersion,
                    candidates,
                    acceptedStep,
                    Envelope("enabled-secret"),
                    Envelope("recovery-response"),
                    recoveryCodes ?? []),
                context,
                token),
            cancellationToken);

    private static ValueTask<SecurityMutationPersistenceResult> DisableTotpAsync(
        SessionRuntime runtime,
        EntityId userId,
        long expectedVersion,
        EntityId expectedSecurityStamp,
        long acceptedStep,
        CancellationToken cancellationToken) => InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.DisableTotpAsync(
                userId,
                expectedVersion,
                expectedSecurityStamp,
                acceptedStep,
                context,
                token),
            cancellationToken);

    private async ValueTask<ChallengeFixture> InsertChallengeAsync(
        AuthenticationUserSnapshot user,
        string kind,
        EntityId? snapshotStamp = null,
        long? snapshotTokenVersion = null,
        CancellationToken cancellationToken = default)
    {
        ChallengeFixture challenge = string.Equals(kind, "setup", StringComparison.Ordinal)
            ? SetupChallenge(
                user.Id,
                snapshotStamp ?? user.SecurityStamp,
                snapshotTokenVersion ?? user.TokenVersion,
                includeResponseEnvelope: false)
            : LoginChallenge(
                user.Id,
                snapshotStamp ?? user.SecurityStamp,
                snapshotTokenVersion ?? user.TokenVersion);
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.one_time_tokens (
                id, user_id, purpose, token_hash, pepper_version, expires_at,
                challenge_kind, secret_envelope, security_stamp, token_version
            ) VALUES (
                $1, $2, 'totp_challenge', $3, $4,
                clock_timestamp() + interval '5 minutes', $5, $6::jsonb, $7, $8
            );
            """);
        command.Parameters.AddWithValue(challenge.Write.Id.Value);
        command.Parameters.AddWithValue(user.Id.Value);
        command.Parameters.AddWithValue(challenge.Write.TokenHash);
        command.Parameters.AddWithValue(challenge.Write.PepperVersion);
        command.Parameters.AddWithValue(challenge.Write.Kind);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
            Value = challenge.Write.SecretEnvelope is null
                ? DBNull.Value
                : challenge.Write.SecretEnvelope.Value.GetRawText(),
        });
        command.Parameters.AddWithValue(challenge.Write.SecurityStamp.Value);
        command.Parameters.AddWithValue(challenge.Write.TokenVersion);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        return challenge;
    }

    private async ValueTask SetLockAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.users
            SET failed_login_count = 3,
                locked_until = clock_timestamp() + interval '5 minutes'
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask DisableUserAsync(
        EntityId userId,
        bool softDelete,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand(
            softDelete
                ? """
                  UPDATE public.users
                  SET status = 'disabled', deleted_at = clock_timestamp()
                  WHERE id = $1;
                  """
                : """
                  UPDATE public.users SET status = 'disabled' WHERE id = $1;
                  """);
        command.Parameters.AddWithValue(userId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask ChangeSecurityStampAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.users SET security_stamp = $2 WHERE id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(EntityId.New().Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask SetAcceptedStepAsync(
        EntityId userId,
        long step,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.users SET totp_last_accepted_step = $2 WHERE id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(step);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask ExpireChallengeAsync(
        EntityId challengeId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.one_time_tokens
            SET expires_at = created_at + interval '1 microsecond'
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(challengeId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask RevokeChallengeAsync(
        EntityId challengeId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.one_time_tokens
            SET revoked_at = clock_timestamp(), revoke_reason = 'coverage_revoked'
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(challengeId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask ExpireRefreshAsync(
        EntityId sessionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.refresh_sessions
            SET issued_at = clock_timestamp() - interval '2 hours',
                expires_at = clock_timestamp() - interval '1 hour'
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(sessionId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask SetRefreshRevokedAsync(
        EntityId sessionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.refresh_sessions
            SET status = 'revoked',
                revoked_at = clock_timestamp(),
                revoke_reason = 'coverage_revoked'
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(sessionId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<(string? IpAddress, int? UserAgentLength)> ReadSessionClientAsync(
        EntityId sessionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT host(ip_address), length(user_agent)
            FROM public.refresh_sessions
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(sessionId.Value);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1));
    }

    private async ValueTask<string> ReadRefreshStatusAsync(
        EntityId sessionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT status FROM public.refresh_sessions WHERE id = $1;
            """);
        command.Parameters.AddWithValue(sessionId.Value);
        return Assert.IsType<string>(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<(int Revoked, string? Reason)> ReadFamilyRevocationAsync(
        EntityId familyId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT count(*) FILTER (WHERE status = 'revoked'),
                   max(revoke_reason) FILTER (WHERE status = 'revoked')
            FROM public.refresh_sessions
            WHERE family_id = $1;
            """);
        command.Parameters.AddWithValue(familyId.Value);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async ValueTask<string> ReadRefreshTerminalAsync(
        EntityId sessionId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT status || ':' || COALESCE(revoke_reason, '')
            FROM public.refresh_sessions
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(sessionId.Value);
        return Assert.IsType<string>(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<string> ReadChallengeTerminalAsync(
        EntityId challengeId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT CASE
                WHEN used_at IS NOT NULL THEN 'used'
                WHEN revoked_at IS NOT NULL THEN 'revoked:' || revoke_reason
                ELSE 'open'
            END
            FROM public.one_time_tokens
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(challengeId.Value);
        return Assert.IsType<string>(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<int> CountRecoveryCodesAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT count(*) FROM public.totp_recovery_codes WHERE user_id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        return checked((int)Assert.IsType<long>(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }

    private async ValueTask InsertRecoveryCodeAsync(
        EntityId userId,
        TotpRecoveryCodeWrite recoveryCode,
        CancellationToken cancellationToken)
    {
        NpgsqlDataSource dataSource = _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        using NpgsqlCommand command = dataSource.CreateCommand("""
            INSERT INTO public.totp_recovery_codes (
                id, user_id, code_hash, pepper_version
            ) VALUES ($1, $2, $3, $4);
            """);
        command.Parameters.AddWithValue(recoveryCode.Id.Value);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(recoveryCode.CodeHash);
        command.Parameters.AddWithValue(recoveryCode.PepperVersion);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<int> CountActiveRecoveryCodesAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT count(*)
            FROM public.totp_recovery_codes
            WHERE user_id = $1
              AND used_at IS NULL
              AND revoked_at IS NULL;
            """);
        command.Parameters.AddWithValue(userId.Value);
        return checked((int)Assert.IsType<long>(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }

    private async ValueTask<bool> TryConsumeRecoveryCodeAsync(
        EntityId userId,
        TotpRecoveryCodeWrite recoveryCode,
        CancellationToken cancellationToken)
    {
        NpgsqlDataSource dataSource = _fixture.ApiServices.GetRequiredService<NpgsqlDataSource>();
        using NpgsqlCommand command = dataSource.CreateCommand("""
            UPDATE public.totp_recovery_codes
            SET used_at = clock_timestamp()
            WHERE user_id = $1
              AND code_hash = $2
              AND pepper_version = $3
              AND used_at IS NULL
              AND revoked_at IS NULL
            RETURNING id;
            """);
        command.Parameters.AddWithValue(userId.Value);
        command.Parameters.AddWithValue(recoveryCode.CodeHash);
        command.Parameters.AddWithValue(recoveryCode.PepperVersion);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid;
    }

    private async ValueTask<string> ReadRecoveryTerminalAsync(
        EntityId recoveryCodeId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT CASE
                WHEN used_at IS NOT NULL THEN 'used'
                WHEN revoked_at IS NOT NULL THEN 'revoked:' || revoke_reason
                ELSE 'active'
            END
            FROM public.totp_recovery_codes
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(recoveryCodeId.Value);
        return Assert.IsType<string>(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<(int Revoked, string? Reason)> ReadRecoveryRevocationsAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT count(*) FILTER (WHERE revoked_at IS NOT NULL),
                   max(revoke_reason) FILTER (WHERE revoked_at IS NOT NULL)
            FROM public.totp_recovery_codes
            WHERE user_id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static SessionFixture Session(
        string? ipAddress = "192.0.2.10",
        string? userAgent = "identity-session-coverage")
    {
        byte[] hash = RandomNumberGenerator.GetBytes(32);
        return new SessionFixture(
            new RefreshSessionWrite(
                EntityId.New(),
                hash,
                PepperVersion: 7,
                TimeSpan.FromDays(30),
                ipAddress,
                userAgent),
            [new CredentialHashCandidate(hash, 7)]);
    }

    private static CredentialHashCandidate[] Candidates() =>
        [new(RandomNumberGenerator.GetBytes(32), 7)];

    private static ChallengeFixture LoginChallenge(AuthenticationUserSnapshot user) =>
        LoginChallenge(user.Id, user.SecurityStamp, user.TokenVersion);

    private static ChallengeFixture LoginChallenge(
        EntityId userId,
        EntityId securityStamp,
        long tokenVersion) => Challenge(
            userId,
            securityStamp,
            tokenVersion,
            "login",
            secretEnvelope: null,
            responseEnvelope: null);

    private static ChallengeFixture SetupChallenge(
        AuthenticationUserSnapshot user,
        bool includeResponseEnvelope) => SetupChallenge(
            user.Id,
            user.SecurityStamp,
            user.TokenVersion,
            includeResponseEnvelope);

    private static ChallengeFixture SetupChallenge(
        EntityId userId,
        EntityId securityStamp,
        long tokenVersion,
        bool includeResponseEnvelope) => Challenge(
            userId,
            securityStamp,
            tokenVersion,
            "setup",
            Envelope("pending-seed"),
            includeResponseEnvelope ? Envelope("setup-response") : null);

    private static ChallengeFixture Challenge(
        EntityId userId,
        EntityId securityStamp,
        long tokenVersion,
        string kind,
        JsonElement? secretEnvelope,
        JsonElement? responseEnvelope)
    {
        _ = userId;
        byte[] hash = RandomNumberGenerator.GetBytes(32);
        return new ChallengeFixture(
            new TotpChallengeWrite(
                EntityId.New(),
                kind,
                hash,
                PepperVersion: 7,
                TimeSpan.FromMinutes(5),
                secretEnvelope,
                responseEnvelope,
                securityStamp,
                tokenVersion),
            [new CredentialHashCandidate(hash, 7)]);
    }

    private static TotpRecoveryCodeWrite[] RecoveryCodes() => Enumerable.Range(0, 8)
        .Select(static _ => new TotpRecoveryCodeWrite(
            EntityId.New(),
            RandomNumberGenerator.GetBytes(32),
            PepperVersion: 7))
        .ToArray();

    private static JsonElement Envelope(string marker) =>
        JsonSerializer.SerializeToElement(new { v = 1, marker });

    private static async ValueTask<T> InTransactionAsync<T>(
        IUnitOfWorkFactory factory,
        Func<IUnitOfWorkContext, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        T result = await action(unitOfWork.Context, cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private sealed record SessionRuntime(
        PostgresIdentitySessionRepository Repository,
        IUnitOfWorkFactory UnitOfWorkFactory);

    private sealed record SessionFixture(
        RefreshSessionWrite Write,
        IReadOnlyList<CredentialHashCandidate> Candidates);

    private sealed record ChallengeFixture(
        TotpChallengeWrite Write,
        IReadOnlyList<CredentialHashCandidate> Candidates);
}
#pragma warning restore MA0051
