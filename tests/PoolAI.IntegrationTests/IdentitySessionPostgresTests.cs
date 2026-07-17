#pragma warning disable MA0051 // These tests keep the session linearization points explicit.
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
public sealed class IdentitySessionPostgresTests(PostgresRuntimeFixture fixture)
{
    private static readonly Guid UserRoleId = Guid.Parse(
        "01900000-0000-7000-8000-000000000004");
    private readonly PostgresRuntimeFixture _fixture =
        fixture ?? throw new ArgumentNullException(nameof(fixture));

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PasswordAndTotpAreBothRequiredAndAcceptedStepsCannotReplay()
    {
        // Governing contracts: AC-002/034 and section 7.4.1. A password-complete
        // login for a TOTP user creates only a one-use challenge; the first TOTP
        // step creates a session and that step cannot authorize a later challenge.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        AuthenticationUserSnapshot user = await InsertUserAsync(
            runtime.Repository,
            totpEnabled: true,
            cancellationToken).ConfigureAwait(true);

        ChallengeFixture firstChallenge = Challenge(user);
        PasswordLoginPersistenceResult password = await InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.CompletePasswordLoginAsync(
                new PasswordLoginWrite(
                    user.Id,
                    user.SecurityStamp,
                    Session: null,
                    firstChallenge.Write),
                context,
                token),
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(PasswordLoginDisposition.MfaRequired, password.Disposition);
        Assert.Equal(
            0L,
            await CountRefreshSessionsAsync(user.Id, cancellationToken).ConfigureAwait(true));

        const long AcceptedStep = 1_900_000_000 / 30;
        SessionFixture firstSession = Session();
        MfaLoginPersistenceResult verified = await InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.CompleteMfaLoginAsync(
                firstChallenge.Candidates,
                AcceptedStep,
                firstSession.Write,
                context,
                token),
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(MfaLoginDisposition.SessionCreated, verified.Disposition);
        Assert.Equal(firstSession.Write.Id, verified.SessionFamilyId);
        Assert.True(await runtime.Repository.IsSessionFamilyActiveAsync(
            user.Id,
            firstSession.Write.Id,
            verified.User!.TokenVersion,
            cancellationToken).ConfigureAwait(true));

        MfaLoginPersistenceResult consumedChallenge = await InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.CompleteMfaLoginAsync(
                firstChallenge.Candidates,
                AcceptedStep + 1,
                Session().Write,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(MfaLoginDisposition.ChallengeInvalid, consumedChallenge.Disposition);

        AuthenticationUserSnapshot current = Assert.IsType<AuthenticationUserSnapshot>(
            await runtime.Repository.GetAuthenticationUserAsync(
                user.Id,
                cancellationToken).ConfigureAwait(true));
        ChallengeFixture secondChallenge = Challenge(current);
        PasswordLoginPersistenceResult secondPassword = await InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.CompletePasswordLoginAsync(
                new PasswordLoginWrite(
                    current.Id,
                    current.SecurityStamp,
                    Session: null,
                    secondChallenge.Write),
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(PasswordLoginDisposition.MfaRequired, secondPassword.Disposition);

        MfaLoginPersistenceResult replay = await InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.CompleteMfaLoginAsync(
                secondChallenge.Candidates,
                AcceptedStep,
                Session().Write,
                context,
                token),
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(MfaLoginDisposition.TotpReplay, replay.Disposition);
        Assert.Equal(
            1L,
            await CountRefreshSessionsAsync(user.Id, cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ConcurrentRefreshRotatesOnceAndRevokesReplayFamily()
    {
        // Governing contract: AC-003 and section 7.4.1. Both transactions race
        // the same generation; one rotates it and the other observes the durable
        // rotated state, classifies reuse, and revokes the active child.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        AuthenticationUserSnapshot user = await InsertUserAsync(
            runtime.Repository,
            totpEnabled: false,
            cancellationToken).ConfigureAwait(true);
        SessionFixture initial = await CreateSessionAsync(
            runtime,
            user,
            cancellationToken).ConfigureAwait(true);

        Assert.True(await runtime.Repository.HasRefreshCredentialAsync(
            initial.Candidates,
            cancellationToken).ConfigureAwait(true));
        Assert.False(await runtime.Repository.HasRefreshCredentialAsync(
            [new CredentialHashCandidate(RandomNumberGenerator.GetBytes(32), 1)],
            cancellationToken).ConfigureAwait(true));
        Assert.True(await runtime.Repository.IsSessionFamilyActiveAsync(
            user.Id,
            initial.Write.Id,
            user.TokenVersion,
            cancellationToken).ConfigureAwait(true));

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RefreshRotationPersistenceResult> first = RotateAfterStartAsync(
            runtime,
            initial.Candidates,
            Session().Write,
            start.Task,
            cancellationToken);
        Task<RefreshRotationPersistenceResult> second = RotateAfterStartAsync(
            runtime,
            initial.Candidates,
            Session().Write,
            start.Task,
            cancellationToken);
        start.SetResult();
        RefreshRotationPersistenceResult[] results = await Task.WhenAll(first, second)
            .ConfigureAwait(true);

        Assert.Single(
            results,
            static value => value.Disposition == RefreshRotationDisposition.Rotated);
        Assert.Single(
            results,
            static value => value.Disposition == RefreshRotationDisposition.Reused);
        Assert.All(results, result => Assert.Equal(initial.Write.Id, result.SessionFamilyId));
        Assert.True(await runtime.Repository.HasRefreshCredentialAsync(
            initial.Candidates,
            cancellationToken).ConfigureAwait(true));
        Assert.False(await runtime.Repository.IsSessionFamilyActiveAsync(
            user.Id,
            initial.Write.Id,
            user.TokenVersion,
            cancellationToken).ConfigureAwait(true));

        RefreshFamilyFacts facts = await ReadFamilyFactsAsync(
            initial.Write.Id,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(2, facts.Total);
        Assert.Equal(1, facts.Rotated);
        Assert.Equal(1, facts.Revoked);
        Assert.Equal(0, facts.Active);
        Assert.Equal("refresh_reuse", facts.RevokedReason);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LogoutScopesRevocationAndAccessValidationUsesSidAndTokenVersion()
    {
        // Governing contract: AC-003 and section 7.4.1. Current logout is scoped
        // by JWT sid, a mismatched refresh credential is a no-op, and all-session
        // logout revokes every family while incrementing token_version once.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        AuthenticationUserSnapshot user = await InsertUserAsync(
            runtime.Repository,
            totpEnabled: false,
            cancellationToken).ConfigureAwait(true);
        SessionFixture current = await CreateSessionAsync(
            runtime,
            user,
            cancellationToken).ConfigureAwait(true);
        AuthenticationUserSnapshot afterFirst = Assert.IsType<AuthenticationUserSnapshot>(
            await runtime.Repository.GetAuthenticationUserAsync(
                user.Id,
                cancellationToken).ConfigureAwait(true));
        SessionFixture other = await CreateSessionAsync(
            runtime,
            afterFirst,
            cancellationToken).ConfigureAwait(true);
        SessionActor actor = new(
            user.Id,
            user.Role,
            user.TokenVersion,
            current.Write.Id);

        Assert.True(await runtime.Repository.IsSessionFamilyActiveAsync(
            user.Id,
            current.Write.Id,
            user.TokenVersion,
            cancellationToken).ConfigureAwait(true));
        Assert.False(await runtime.Repository.IsSessionFamilyActiveAsync(
            user.Id,
            current.Write.Id,
            user.TokenVersion + 1,
            cancellationToken).ConfigureAwait(true));

        LogoutPersistenceResult mismatched = await LogoutAsync(
            runtime,
            actor,
            other.Candidates,
            allSessions: false,
            cancellationToken).ConfigureAwait(true);
        Assert.False(mismatched.Changed);
        Assert.True(await runtime.Repository.IsSessionFamilyActiveAsync(
            user.Id,
            current.Write.Id,
            user.TokenVersion,
            cancellationToken).ConfigureAwait(true));
        Assert.True(await runtime.Repository.IsSessionFamilyActiveAsync(
            user.Id,
            other.Write.Id,
            user.TokenVersion,
            cancellationToken).ConfigureAwait(true));

        LogoutPersistenceResult currentLogout = await LogoutAsync(
            runtime,
            actor,
            Array.Empty<CredentialHashCandidate>(),
            allSessions: false,
            cancellationToken).ConfigureAwait(true);
        Assert.True(currentLogout.Changed);
        Assert.False(await runtime.Repository.IsSessionFamilyActiveAsync(
            user.Id,
            current.Write.Id,
            user.TokenVersion,
            cancellationToken).ConfigureAwait(true));
        Assert.True(await runtime.Repository.IsSessionFamilyActiveAsync(
            user.Id,
            other.Write.Id,
            user.TokenVersion,
            cancellationToken).ConfigureAwait(true));

        LogoutPersistenceResult currentReplay = await LogoutAsync(
            runtime,
            actor,
            Array.Empty<CredentialHashCandidate>(),
            allSessions: false,
            cancellationToken).ConfigureAwait(true);
        Assert.False(currentReplay.Changed);

        LogoutPersistenceResult allLogout = await LogoutAsync(
            runtime,
            actor,
            Array.Empty<CredentialHashCandidate>(),
            allSessions: true,
            cancellationToken).ConfigureAwait(true);
        Assert.True(allLogout.Changed);
        Assert.Equal(user.TokenVersion + 1, allLogout.User!.TokenVersion);
        Assert.False(await runtime.Repository.IsSessionFamilyActiveAsync(
            user.Id,
            other.Write.Id,
            user.TokenVersion + 1,
            cancellationToken).ConfigureAwait(true));

        LogoutPersistenceResult allReplay = await LogoutAsync(
            runtime,
            actor with { TokenVersion = user.TokenVersion + 1 },
            Array.Empty<CredentialHashCandidate>(),
            allSessions: true,
            cancellationToken).ConfigureAwait(true);
        Assert.False(allReplay.Changed);
        Assert.Equal(user.TokenVersion + 1, allReplay.User!.TokenVersion);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LogoutWithRotatedGenerationIsNoOpButActiveGenerationRevokesFamily()
    {
        // Governing contract: OpenAPI logout and error-catalog section 3.2. A
        // rotated/expired/revoked optional refresh credential is invalid ownership
        // evidence and must remain a 204 no-op even when its family matches JWT sid.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        AuthenticationUserSnapshot user = await InsertUserAsync(
            runtime.Repository,
            totpEnabled: false,
            cancellationToken).ConfigureAwait(true);
        SessionFixture initial = await CreateSessionAsync(
            runtime,
            user,
            cancellationToken).ConfigureAwait(true);
        SessionFixture replacement = Session();
        RefreshRotationPersistenceResult rotation = await InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.RotateRefreshSessionAsync(
                initial.Candidates,
                replacement.Write,
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(RefreshRotationDisposition.Rotated, rotation.Disposition);

        SessionActor actor = new(
            user.Id,
            user.Role,
            user.TokenVersion,
            initial.Write.Id);
        LogoutPersistenceResult oldGeneration = await LogoutAsync(
            runtime,
            actor,
            initial.Candidates,
            allSessions: false,
            cancellationToken).ConfigureAwait(true);

        Assert.False(oldGeneration.Changed);
        Assert.True(await runtime.Repository.IsSessionFamilyActiveAsync(
            user.Id,
            initial.Write.Id,
            user.TokenVersion,
            cancellationToken).ConfigureAwait(true));

        LogoutPersistenceResult activeGeneration = await LogoutAsync(
            runtime,
            actor,
            replacement.Candidates,
            allSessions: false,
            cancellationToken).ConfigureAwait(true);

        Assert.True(activeGeneration.Changed);
        Assert.False(await runtime.Repository.IsSessionFamilyActiveAsync(
            user.Id,
            initial.Write.Id,
            user.TokenVersion,
            cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AccountLockUsesDatabaseClockAndDoesNotExtendWhileLocked()
    {
        // Governing contract: section 7.4.1. The threshold locks by PostgreSQL
        // clock, locked-period failures do not mutate the row, and the first
        // failure after expiry restarts the count from one.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SessionRuntime runtime = Runtime();
        AuthenticationUserSnapshot user = await InsertUserAsync(
            runtime.Repository,
            totpEnabled: false,
            cancellationToken).ConfigureAwait(true);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            PasswordFailureDisposition disposition = await RecordFailureAsync(
                runtime,
                user,
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(PasswordFailureDisposition.Recorded, disposition);
        }

        AuthenticationUserSnapshot locked = Assert.IsType<AuthenticationUserSnapshot>(
            await runtime.Repository.GetAuthenticationUserAsync(
                user.Id,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(3, locked.FailedLoginCount);
        Assert.NotNull(locked.LockedUntil);

        PasswordFailureDisposition ignored = await RecordFailureAsync(
            runtime,
            user,
            cancellationToken).ConfigureAwait(true);
        AuthenticationUserSnapshot stillLocked = Assert.IsType<AuthenticationUserSnapshot>(
            await runtime.Repository.GetAuthenticationUserAsync(
                user.Id,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(PasswordFailureDisposition.Ignored, ignored);
        Assert.Equal(locked.FailedLoginCount, stillLocked.FailedLoginCount);
        Assert.Equal(locked.LockedUntil, stillLocked.LockedUntil);

        PasswordLoginPersistenceResult correctPasswordWhileLocked = await InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.CompletePasswordLoginAsync(
                new PasswordLoginWrite(
                    user.Id,
                    user.SecurityStamp,
                    Session().Write,
                    Challenge: null),
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            PasswordLoginDisposition.AccountLocked,
            correctPasswordWhileLocked.Disposition);
        Assert.NotNull(correctPasswordWhileLocked.RetryAfterSeconds);
        Assert.InRange(correctPasswordWhileLocked.RetryAfterSeconds.Value, 1, 301);
        Assert.Equal(
            0L,
            await CountRefreshSessionsAsync(user.Id, cancellationToken).ConfigureAwait(true));

        await ExpireLockAsync(user.Id, cancellationToken).ConfigureAwait(true);
        PasswordFailureDisposition afterExpiry = await RecordFailureAsync(
            runtime,
            user,
            cancellationToken).ConfigureAwait(true);
        AuthenticationUserSnapshot restarted = Assert.IsType<AuthenticationUserSnapshot>(
            await runtime.Repository.GetAuthenticationUserAsync(
                user.Id,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(PasswordFailureDisposition.Recorded, afterExpiry);
        Assert.Equal(1, restarted.FailedLoginCount);
        Assert.Null(restarted.LockedUntil);

        PasswordLoginPersistenceResult successful = await InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.CompletePasswordLoginAsync(
                new PasswordLoginWrite(
                    user.Id,
                    user.SecurityStamp,
                    Session().Write,
                    Challenge: null),
                context,
                token),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(PasswordLoginDisposition.SessionCreated, successful.Disposition);
        Assert.Equal(0, successful.User!.FailedLoginCount);
        Assert.Null(successful.User.LockedUntil);
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
        bool totpEnabled,
        CancellationToken cancellationToken)
    {
        EntityId userId = EntityId.New();
        string email = $"session-{userId.Value:N}@poolai.test";
        using (NpgsqlCommand user = _fixture.AdministratorDataSource.CreateCommand(
            totpEnabled
                ? """
                  INSERT INTO public.users (
                      id, email, normalized_email, display_name, password_hash,
                      totp_secret_envelope, security_stamp
                  ) VALUES (
                      $1, $2, $2, 'Session test', 'poolai-password-v1:test',
                      '{"v":1}'::jsonb, $3
                  );
                  """
                : """
                  INSERT INTO public.users (
                      id, email, normalized_email, display_name, password_hash,
                      security_stamp
                  ) VALUES (
                      $1, $2, $2, 'Session test', 'poolai-password-v1:test', $3
                  );
                  """))
        {
            user.Parameters.AddWithValue(userId.Value);
            user.Parameters.AddWithValue(email);
            user.Parameters.AddWithValue(EntityId.New().Value);
            Assert.Equal(
                1,
                await user.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        using (NpgsqlCommand role = _fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.user_roles (user_id, role_id)
            VALUES ($1, $2);
            """))
        {
            role.Parameters.AddWithValue(userId.Value);
            role.Parameters.AddWithValue(UserRoleId);
            Assert.Equal(
                1,
                await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        return Assert.IsType<AuthenticationUserSnapshot>(
            await repository.GetAuthenticationUserAsync(
                userId,
                cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<SessionFixture> CreateSessionAsync(
        SessionRuntime runtime,
        AuthenticationUserSnapshot user,
        CancellationToken cancellationToken)
    {
        SessionFixture fixture = Session();
        PasswordLoginPersistenceResult result = await InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.CompletePasswordLoginAsync(
                new PasswordLoginWrite(
                    user.Id,
                    user.SecurityStamp,
                    fixture.Write,
                    Challenge: null),
                context,
                token),
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(PasswordLoginDisposition.SessionCreated, result.Disposition);
        Assert.Equal(fixture.Write.Id, result.SessionFamilyId);
        return fixture;
    }

    private static async Task<RefreshRotationPersistenceResult> RotateAfterStartAsync(
        SessionRuntime runtime,
        IReadOnlyList<CredentialHashCandidate> candidates,
        RefreshSessionWrite replacement,
        Task start,
        CancellationToken cancellationToken)
    {
        await start.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.RotateRefreshSessionAsync(
                candidates,
                replacement,
                context,
                token),
            cancellationToken).ConfigureAwait(false);
    }

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

    private static ValueTask<PasswordFailureDisposition> RecordFailureAsync(
        SessionRuntime runtime,
        AuthenticationUserSnapshot user,
        CancellationToken cancellationToken) => InTransactionAsync(
            runtime.UnitOfWorkFactory,
            (context, token) => runtime.Repository.RecordPasswordFailureAsync(
                user.Id,
                user.SecurityStamp,
                maximumFailures: 3,
                TimeSpan.FromMinutes(5),
                context,
                token),
            cancellationToken);

    private async ValueTask<long> CountRefreshSessionsAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT count(*) FROM public.refresh_sessions WHERE user_id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        return Assert.IsType<long>(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<RefreshFamilyFacts> ReadFamilyFactsAsync(
        EntityId familyId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT count(*),
                   count(*) FILTER (WHERE status = 'active'),
                   count(*) FILTER (WHERE status = 'rotated'),
                   count(*) FILTER (WHERE status = 'revoked'),
                   max(revoke_reason) FILTER (WHERE status = 'revoked')
            FROM public.refresh_sessions
            WHERE family_id = $1;
            """);
        command.Parameters.AddWithValue(familyId.Value);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new RefreshFamilyFacts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private async ValueTask ExpireLockAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.users
            SET locked_until = clock_timestamp() - interval '1 second'
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(userId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

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

    private static SessionFixture Session()
    {
        byte[] hash = RandomNumberGenerator.GetBytes(32);
        return new SessionFixture(
            new RefreshSessionWrite(
                EntityId.New(),
                hash,
                PepperVersion: 7,
                TimeSpan.FromDays(30),
                "192.0.2.10",
                "identity-session-integration-test"),
            [new CredentialHashCandidate(hash, 7)]);
    }

    private static ChallengeFixture Challenge(AuthenticationUserSnapshot user)
    {
        byte[] hash = RandomNumberGenerator.GetBytes(32);
        EntityId id = EntityId.New();
        return new ChallengeFixture(
            new TotpChallengeWrite(
                id,
                "login",
                hash,
                PepperVersion: 7,
                TimeSpan.FromMinutes(5),
                SecretEnvelope: null,
                ResponseBodyEnvelope: null,
                user.SecurityStamp,
                user.TokenVersion),
            [new CredentialHashCandidate(hash, 7)]);
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

    private sealed record RefreshFamilyFacts(
        int Total,
        int Active,
        int Rotated,
        int Revoked,
        string? RevokedReason);
}
#pragma warning restore MA0051
