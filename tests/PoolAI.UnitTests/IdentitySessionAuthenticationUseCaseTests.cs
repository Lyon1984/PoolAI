using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class IdentitySessionAuthenticationUseCaseTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 17, 12, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("not-an-email", "Valid-Password-123!")]
    [InlineData("user@example.test", "")]
    [InlineData("user@example.test", null)]
    public async Task LoginRejectsMalformedCredentialsBeforeReadingDependencies(
        string email,
        string? password)
    {
        Fixture fixture = new();

        Result<LoginResultView> result = await fixture.Service.ExecuteAsync(
            Login(email: email, password: password!),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, IdentityErrorCodes.ValidationFailed);
        Assert.Equal(0, fixture.Repository.FindAuthenticationUserCalls);
        Assert.Equal(0, fixture.RateLimiter.Calls);
        Assert.Equal(0, fixture.UnitOfWorkFactory.BeginCalls);
    }

    [Fact]
    public async Task LoginRejectsOversizedPasswordBeforeReadingDependencies()
    {
        Fixture fixture = new();

        Result<LoginResultView> result = await fixture.Service.ExecuteAsync(
            Login(password: new string('x', 1_025)),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, IdentityErrorCodes.ValidationFailed);
        Assert.Equal(0, fixture.Repository.FindAuthenticationUserCalls);
        Assert.Equal(0, fixture.UnitOfWorkFactory.BeginCalls);
    }

    [Fact]
    public async Task UnknownUserFailureUsesTheSamePasswordStateStatementAndIsAudited()
    {
        Fixture fixture = new();
        fixture.Repository.AuthenticationUser = null;
        fixture.PasswordHasher.VerifyResult = false;

        Result<LoginResultView> result = await fixture.Service.ExecuteAsync(
            Login(ipAddress: null),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, IdentityErrorCodes.InvalidCredentials);
        Assert.Equal("user@example.test", fixture.Repository.NormalizedEmail);
        Assert.Equal(string.Empty, Assert.Single(fixture.RateLimiter.IpAddresses));
        Assert.Equal(1, fixture.Repository.RecordPasswordFailureCalls);
        Assert.NotNull(fixture.Repository.PasswordFailureUserId);
        Assert.NotNull(fixture.Repository.PasswordFailureSecurityStamp);
        Assert.Equal(1, fixture.UnitOfWorkFactory.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        AuditEntry audit = Assert.Single(fixture.AuditAppender.Entries);
        Assert.Equal(AuditActorType.System, audit.ActorType);
        Assert.Null(audit.TargetId);
        AssertAudit(audit, "identity.login.rejected", "invalid_credentials");
    }

    [Fact]
    public async Task KnownUserWrongPasswordRecordsLockoutFactAndAuditInOneUnitOfWork()
    {
        AuthenticationUserSnapshot user = User();
        Fixture fixture = new(user);
        fixture.PasswordHasher.VerifyResult = false;

        Result<LoginResultView> result = await fixture.Service.ExecuteAsync(
            Login(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, IdentityErrorCodes.InvalidCredentials);
        Assert.Equal(1, fixture.Repository.RecordPasswordFailureCalls);
        Assert.Equal(user.Id, fixture.Repository.PasswordFailureUserId);
        Assert.Equal(user.SecurityStamp, fixture.Repository.PasswordFailureSecurityStamp);
        Assert.Equal(5, fixture.Repository.MaximumPasswordFailures);
        Assert.Equal(TimeSpan.FromMinutes(15), fixture.Repository.LockoutDuration);
        Assert.Same(
            fixture.Repository.PasswordFailureContext,
            fixture.AuditAppender.Contexts.Single());
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        AssertAudit(
            Assert.Single(fixture.AuditAppender.Entries),
            "identity.login.rejected",
            "invalid_credentials");
    }

    [Fact]
    public async Task PasswordOnlyLoginCreatesSessionAndReturnsContractTokenLifetimes()
    {
        AuthenticationUserSnapshot user = User(role: SystemRole.Operator);
        EntityId familyId = EntityId.New();
        Fixture fixture = new(user);
        fixture.Repository.PasswordLoginResult = new(
            PasswordLoginDisposition.SessionCreated,
            user,
            familyId,
            RetryAfterSeconds: null);

        Result<LoginResultView> result = await fixture.Service.ExecuteAsync(
            Login(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        LoginTokenResultView login = Assert.IsType<LoginTokenResultView>(result.Value);
        Assert.Equal("access-token", login.Tokens.AccessToken);
        Assert.Equal("refresh-token-next", login.Tokens.RefreshToken);
        Assert.Equal(SessionPolicy.AccessTokenSeconds, login.Tokens.ExpiresIn);
        Assert.Equal(SessionPolicy.RefreshTokenSeconds, login.Tokens.RefreshExpiresIn);
        PasswordLoginWrite write = Assert.IsType<PasswordLoginWrite>(
            fixture.Repository.PasswordLoginWrite);
        Assert.Equal(user.Id, write.UserId);
        Assert.Equal(user.SecurityStamp, write.ExpectedSecurityStamp);
        Assert.Null(write.Challenge);
        Assert.NotNull(write.Session);
        Assert.Equal(TimeSpan.FromDays(30), write.Session.Lifetime);
        Assert.Equal("192.0.2.10", write.Session.IpAddress);
        Assert.Equal("session-unit-test", write.Session.UserAgent);
        AccessTokenSubject subject = Assert.IsType<AccessTokenSubject>(
            fixture.AccessTokenIssuer.Subject);
        Assert.Equal(user.Id, subject.UserId);
        Assert.Equal(SystemRole.Operator, subject.Role);
        Assert.Equal(user.TokenVersion, subject.TokenVersion);
        Assert.Equal(familyId, subject.SessionFamilyId);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        AssertAudit(
            Assert.Single(fixture.AuditAppender.Entries),
            "identity.login.succeeded",
            "session_created");
    }

    [Fact]
    public async Task TotpEnabledPasswordLoginCreatesSnapshotChallengeWithoutTokens()
    {
        AuthenticationUserSnapshot user = User(totpEnabled: true);
        Fixture fixture = new(user);
        fixture.Repository.PasswordLoginResult = new(
            PasswordLoginDisposition.MfaRequired,
            user,
            SessionFamilyId: null,
            RetryAfterSeconds: null);

        Result<LoginResultView> result = await fixture.Service.ExecuteAsync(
            Login(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        LoginMfaResultView mfa = Assert.IsType<LoginMfaResultView>(result.Value);
        Assert.Equal(fixture.ChallengeHasher.Secret.Challenge, mfa.ChallengeId);
        Assert.Equal(SessionPolicy.LoginMfaChallengeSeconds, mfa.ExpiresIn);
        PasswordLoginWrite write = Assert.IsType<PasswordLoginWrite>(
            fixture.Repository.PasswordLoginWrite);
        Assert.Null(write.Session);
        Assert.NotNull(write.Challenge);
        Assert.Equal("login", write.Challenge.Kind);
        Assert.Equal(TimeSpan.FromMinutes(5), write.Challenge.Lifetime);
        Assert.Equal(user.SecurityStamp, write.Challenge.SecurityStamp);
        Assert.Equal(user.TokenVersion, write.Challenge.TokenVersion);
        Assert.Null(write.Challenge.SecretEnvelope);
        Assert.Null(write.Challenge.ResponseBodyEnvelope);
        Assert.Equal(0, fixture.RefreshHasher.CreateCalls);
        Assert.Equal(0, fixture.AccessTokenIssuer.Calls);
        AssertAudit(
            Assert.Single(fixture.AuditAppender.Entries),
            "identity.login.mfa_required",
            "mfa_required");
    }

    [Theory]
    [InlineData(
        PasswordLoginDisposition.AccountLocked,
        IdentityErrorCodes.AccountLocked,
        "identity.login.locked",
        "account_locked",
        37L,
        1)]
    [InlineData(
        PasswordLoginDisposition.UserDisabled,
        IdentityErrorCodes.UserDisabled,
        "identity.login.disabled",
        "user_disabled",
        null,
        1)]
    [InlineData(
        PasswordLoginDisposition.StaleCredential,
        IdentityErrorCodes.InvalidCredentials,
        null,
        null,
        null,
        0)]
    internal async Task PasswordLoginPersistenceRaceMapsStableOutcome(
        PasswordLoginDisposition disposition,
        string expectedCode,
        string? expectedAction,
        string? expectedOutcome,
        long? retryAfterSeconds,
        int expectedCommitCalls)
    {
        AuthenticationUserSnapshot user = User();
        Fixture fixture = new(user);
        fixture.Repository.PasswordLoginResult = new(
            disposition,
            user,
            SessionFamilyId: null,
            retryAfterSeconds);

        Result<LoginResultView> result = await fixture.Service.ExecuteAsync(
            Login(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, expectedCode);
        Assert.Equal(retryAfterSeconds, result.Error.RetryAfterSeconds);
        Assert.Equal(expectedCommitCalls, fixture.UnitOfWorkFactory.CommitCalls);
        if (expectedAction is null)
        {
            Assert.Empty(fixture.AuditAppender.Entries);
        }
        else
        {
            AssertAudit(
                Assert.Single(fixture.AuditAppender.Entries),
                expectedAction,
                expectedOutcome!);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345x")]
    public async Task LoginTotpRejectsMalformedCodeBeforeChallengeLookup(string? code)
    {
        Fixture fixture = new();

        Result<TokenPairView> result = await fixture.Service.ExecuteAsync(
            VerifyTotp(code: code!),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, IdentityErrorCodes.ValidationFailed);
        Assert.Equal(0, fixture.Repository.FindTotpChallengeCalls);
        Assert.Equal(0, fixture.RateLimiter.Calls);
        Assert.Equal(0, fixture.UnitOfWorkFactory.BeginCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LoginTotpRejectsMissingDisabledOrStaleChallengeUniformly(int scenario)
    {
        AuthenticationUserSnapshot valid = User(totpEnabled: true);
        TotpChallengeSnapshot? challenge = scenario switch
        {
            0 => null,
            1 => Challenge(User(totpEnabled: true, status: UserLifecycle.Disabled)),
            2 => Challenge(User(totpEnabled: false)),
            3 => Challenge(
                valid,
                securityStamp: EntityId.New()),
            4 => Challenge(
                valid,
                tokenVersion: valid.TokenVersion + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Fixture fixture = new(valid);
        fixture.Repository.TotpChallenge = challenge;

        Result<TokenPairView> result = await fixture.Service.ExecuteAsync(
            VerifyTotp(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, IdentityErrorCodes.MfaChallengeInvalid);
        Assert.Equal(1, fixture.RateLimiter.Calls);
        Assert.Equal(0, fixture.Repository.CompleteMfaLoginCalls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        AssertAudit(
            Assert.Single(fixture.AuditAppender.Entries),
            "identity.login.mfa_rejected",
            "challenge_invalid");
    }

    [Fact]
    public async Task LoginTotpRejectsWrongCodeWithoutPasswordLockoutMutation()
    {
        AuthenticationUserSnapshot user = User(totpEnabled: true);
        Fixture fixture = new(user);
        fixture.Repository.TotpChallenge = Challenge(user);
        fixture.TotpAuthenticator.MatchResult = false;

        Result<TokenPairView> result = await fixture.Service.ExecuteAsync(
            VerifyTotp(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, IdentityErrorCodes.TotpCodeInvalid);
        Assert.Equal(0, fixture.Repository.RecordPasswordFailureCalls);
        Assert.Equal(0, fixture.Repository.CompleteMfaLoginCalls);
        Assert.Equal(1, fixture.RateLimiter.Calls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        AssertAudit(
            Assert.Single(fixture.AuditAppender.Entries),
            "identity.login.mfa_rejected",
            "totp_code_invalid");
    }

    [Fact]
    public async Task LoginTotpSuccessConsumesChallengeAndIssuesSession()
    {
        AuthenticationUserSnapshot user = User(totpEnabled: true);
        EntityId familyId = EntityId.New();
        Fixture fixture = new(user);
        fixture.Repository.TotpChallenge = Challenge(user);
        fixture.Repository.MfaLoginResult = new(
            MfaLoginDisposition.SessionCreated,
            user,
            familyId);

        Result<TokenPairView> result = await fixture.Service.ExecuteAsync(
            VerifyTotp(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Equal("access-token", result.Value.AccessToken);
        Assert.Equal("refresh-token-next", result.Value.RefreshToken);
        Assert.Equal(1, fixture.Repository.CompleteMfaLoginCalls);
        Assert.Equal(fixture.TotpAuthenticator.MatchedStep, fixture.Repository.AcceptedStep);
        Assert.Equal(TimeSpan.FromDays(30), fixture.Repository.MfaSessionWrite?.Lifetime);
        Assert.Equal(familyId, fixture.AccessTokenIssuer.Subject?.SessionFamilyId);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        AssertAudit(
            Assert.Single(fixture.AuditAppender.Entries),
            "identity.login.mfa_succeeded",
            "session_created");
    }

    [Theory]
    [InlineData(
        MfaLoginDisposition.ChallengeInvalid,
        IdentityErrorCodes.MfaChallengeInvalid,
        "identity.login.mfa_rejected",
        "challenge_invalid")]
    [InlineData(
        MfaLoginDisposition.TotpReplay,
        IdentityErrorCodes.TotpCodeInvalid,
        "identity.login.totp_replayed",
        "totp_replay")]
    internal async Task LoginTotpConcurrentPersistenceRaceIsAuditedAndRateCounted(
        MfaLoginDisposition disposition,
        string expectedCode,
        string expectedAction,
        string expectedOutcome)
    {
        AuthenticationUserSnapshot user = User(totpEnabled: true);
        Fixture fixture = new(user);
        fixture.Repository.TotpChallenge = Challenge(user);
        fixture.Repository.MfaLoginResult = new(disposition, user, SessionFamilyId: null);

        Result<TokenPairView> result = await fixture.Service.ExecuteAsync(
            VerifyTotp(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, expectedCode);
        Assert.Equal(1, fixture.Repository.CompleteMfaLoginCalls);
        Assert.Equal(1, fixture.RateLimiter.Calls);
        Assert.Equal(2, fixture.UnitOfWorkFactory.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        AssertAudit(
            Assert.Single(fixture.AuditAppender.Entries),
            expectedAction,
            expectedOutcome);
    }

    [Theory]
    [InlineData(
        LoginFailureRateLimitDisposition.Rejected,
        IdentityErrorCodes.RateLimitExceeded,
        19L)]
    [InlineData(
        LoginFailureRateLimitDisposition.Unavailable,
        IdentityErrorCodes.CoordinationUnavailable,
        1L)]
    internal async Task LoginTotpConcurrentPersistenceRaceChecksRedisBeforeAudit(
        LoginFailureRateLimitDisposition disposition,
        string expectedCode,
        long expectedRetryAfter)
    {
        AuthenticationUserSnapshot user = User(totpEnabled: true);
        Fixture fixture = new(user);
        fixture.Repository.TotpChallenge = Challenge(user);
        fixture.Repository.MfaLoginResult = new(
            MfaLoginDisposition.TotpReplay,
            user,
            SessionFamilyId: null);
        fixture.RateLimiter.Decision = new(disposition, expectedRetryAfter);

        Result<TokenPairView> result = await fixture.Service.ExecuteAsync(
            VerifyTotp(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, expectedCode);
        Assert.Equal(expectedRetryAfter, result.Error.RetryAfterSeconds);
        Assert.Equal(1, fixture.Repository.CompleteMfaLoginCalls);
        Assert.Equal(1, fixture.RateLimiter.Calls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.BeginCalls);
        Assert.Equal(0, fixture.UnitOfWorkFactory.CommitCalls);
        Assert.Empty(fixture.AuditAppender.Entries);
    }

    [Fact]
    public async Task RefreshRejectsMalformedCredentialWithoutDatabasePreflight()
    {
        Fixture fixture = new();
        fixture.RefreshHasher.Candidates = [];

        Result<TokenPairView> result = await fixture.Service.ExecuteAsync(
            Refresh(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, IdentityErrorCodes.RefreshTokenInvalid);
        Assert.Equal(0, fixture.Repository.HasRefreshCredentialCalls);
        Assert.Equal(0, fixture.RefreshHasher.CreateCalls);
        Assert.Equal(0, fixture.UnitOfWorkFactory.BeginCalls);
    }

    [Fact]
    public async Task RefreshRotationReturnsSlidingSessionAndAuditsSuccess()
    {
        AuthenticationUserSnapshot user = User(role: SystemRole.Admin);
        EntityId familyId = EntityId.New();
        Fixture fixture = new(user);
        fixture.Repository.RefreshRotationResult = new(
            RefreshRotationDisposition.Rotated,
            user,
            familyId);

        Result<TokenPairView> result = await fixture.Service.ExecuteAsync(
            Refresh(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Equal("refresh-token-next", result.Value.RefreshToken);
        Assert.Equal(SessionPolicy.RefreshTokenSeconds, result.Value.RefreshExpiresIn);
        Assert.Equal(1, fixture.Repository.HasRefreshCredentialCalls);
        Assert.Equal(1, fixture.Repository.RotateRefreshSessionCalls);
        Assert.Equal(TimeSpan.FromDays(30), fixture.Repository.RefreshReplacement?.Lifetime);
        Assert.Equal("192.0.2.10", fixture.Repository.RefreshReplacement?.IpAddress);
        Assert.Equal(familyId, fixture.AccessTokenIssuer.Subject?.SessionFamilyId);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        AssertAudit(
            Assert.Single(fixture.AuditAppender.Entries),
            "identity.refresh.rotated",
            "rotated");
    }

    [Theory]
    [InlineData(
        RefreshRotationDisposition.Reused,
        IdentityErrorCodes.RefreshTokenReused,
        "identity.refresh.reused")]
    [InlineData(
        RefreshRotationDisposition.Invalid,
        IdentityErrorCodes.RefreshTokenInvalid,
        null)]
    internal async Task RefreshPersistenceRejectionCommitsRevocationButAuditsOnlyReuse(
        RefreshRotationDisposition disposition,
        string expectedCode,
        string? expectedAuditAction)
    {
        AuthenticationUserSnapshot user = User();
        Fixture fixture = new(user);
        fixture.Repository.RefreshRotationResult = new(disposition, user, EntityId.New());

        Result<TokenPairView> result = await fixture.Service.ExecuteAsync(
            Refresh(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, expectedCode);
        Assert.Equal(1, fixture.UnitOfWorkFactory.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        Assert.Equal(0, fixture.AccessTokenIssuer.Calls);
        if (expectedAuditAction is null)
        {
            Assert.Empty(fixture.AuditAppender.Entries);
        }
        else
        {
            AssertAudit(
                Assert.Single(fixture.AuditAppender.Entries),
                expectedAuditAction,
                "reused");
        }
    }

    [Fact]
    public async Task LogoutRejectsRefreshTokenForAllSessionsBeforePersistence()
    {
        Fixture fixture = new();

        Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
            Logout(allSessions: true, refreshToken: "refresh-token-current"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, IdentityErrorCodes.ValidationFailed);
        Assert.Equal(0, fixture.RefreshHasher.HashCandidateCalls);
        Assert.Equal(0, fixture.Repository.LogoutCalls);
        Assert.Equal(0, fixture.UnitOfWorkFactory.BeginCalls);
    }

    [Fact]
    public async Task LogoutMalformedOptionalCredentialIsIdempotentNoOpWithoutTransaction()
    {
        Fixture fixture = new();
        fixture.RefreshHasher.Candidates = [];

        Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
            Logout(refreshToken: "malformed"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Equal(204, result.Value.StatusCode);
        Assert.False(result.Value.IsReplay);
        Assert.Equal(0, fixture.Repository.LogoutCalls);
        Assert.Equal(0, fixture.UnitOfWorkFactory.BeginCalls);
    }

    [Theory]
    [InlineData(SystemRole.Admin, false, false, AuditActorType.Admin, "identity.logout.current")]
    [InlineData(SystemRole.Operator, true, false, AuditActorType.Operator, "identity.logout.all")]
    [InlineData(SystemRole.User, false, true, AuditActorType.User, "identity.logout.current")]
    public async Task ChangedLogoutRevokesRequestedScopeAndAuditsActor(
        SystemRole role,
        bool allSessions,
        bool includeRefreshToken,
        AuditActorType expectedActorType,
        string expectedAction)
    {
        AuthenticationUserSnapshot user = User(role: role);
        Fixture fixture = new(user);
        fixture.Repository.LogoutResult = new(user, Changed: true);

        Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
            Logout(
                Actor(user),
                allSessions,
                includeRefreshToken ? "refresh-token-current" : null),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Equal(204, result.Value.StatusCode);
        Assert.Equal(allSessions, fixture.Repository.LogoutAllSessions);
        Assert.Equal(includeRefreshToken ? 1 : 0, fixture.Repository.LogoutCandidates?.Count);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        AuditEntry audit = Assert.Single(fixture.AuditAppender.Entries);
        Assert.Equal(expectedActorType, audit.ActorType);
        Assert.Equal(user.Id, audit.ActorUserId);
        AssertAudit(
            audit,
            expectedAction,
            allSessions ? "all_sessions_revoked" : "family_revoked");
    }

    [Fact]
    public async Task UnchangedLogoutCommitsIdempotentNoOpWithoutAudit()
    {
        AuthenticationUserSnapshot user = User();
        Fixture fixture = new(user);
        fixture.Repository.LogoutResult = new(user, Changed: false);

        Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
            Logout(Actor(user)),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Equal(204, result.Value.StatusCode);
        Assert.Equal(1, fixture.Repository.LogoutCalls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        Assert.Empty(fixture.AuditAppender.Entries);
    }

    [Fact]
    public async Task CurrentUserReturnsCanonicalRepositoryProjection()
    {
        AuthenticationUserSnapshot user = User(totpEnabled: true, role: SystemRole.Auditor);
        Fixture fixture = new(user);

        Result<CurrentUserView> result = await fixture.Service.ExecuteAsync(
            new GetCurrentUserQuery(Actor(user)),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Equal(user.Id, result.Value.Id);
        Assert.Equal(user.Email, result.Value.Email);
        Assert.Equal(user.DisplayName, result.Value.DisplayName);
        Assert.Equal(SystemRole.Auditor, result.Value.Role);
        Assert.Equal(user.Status, result.Value.Status);
        Assert.True(result.Value.TotpEnabled);
        Assert.Null(result.Value.PasswordChangedAt);
        Assert.Equal(user.Version, result.Value.Version);
        Assert.Equal(user.CreatedAt, result.Value.CreatedAt);
        Assert.Equal(user.UpdatedAt, result.Value.UpdatedAt);
        Assert.Equal(user.Id, fixture.Repository.GetAuthenticationUserId);
    }

    [Fact]
    public async Task CurrentUserRejectsDeletedIdentity()
    {
        AuthenticationUserSnapshot user = User();
        Fixture fixture = new(user);
        fixture.Repository.CurrentUser = null;

        Result<CurrentUserView> result = await fixture.Service.ExecuteAsync(
            new GetCurrentUserQuery(Actor(user)),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertFailure(result, IdentityErrorCodes.InvalidUserToken);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, -1)]
    public async Task AccessValidatorRejectsMalformedIdentityWithoutDatabaseRead(
        int userMarker,
        int familyMarker,
        long tokenVersion)
    {
        Fixture fixture = new();
        Guid userId = userMarker == 0 ? Guid.Empty : Guid.NewGuid();
        Guid familyId = familyMarker == 0 ? Guid.Empty : Guid.NewGuid();

        bool active = await fixture.Service.IsActiveAsync(
            userId,
            familyId,
            tokenVersion,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(active);
        Assert.Equal(0, fixture.Repository.IsSessionFamilyActiveCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AccessValidatorReturnsCanonicalFamilyState(bool repositoryResult)
    {
        AuthenticationUserSnapshot user = User();
        EntityId familyId = EntityId.New();
        Fixture fixture = new(user);
        fixture.Repository.SessionFamilyActive = repositoryResult;

        bool active = await fixture.Service.IsActiveAsync(
            user.Id.Value,
            familyId.Value,
            user.TokenVersion,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(repositoryResult, active);
        Assert.Equal(user.Id, fixture.Repository.ActiveSessionUserId);
        Assert.Equal(familyId, fixture.Repository.ActiveSessionFamilyId);
        Assert.Equal(user.TokenVersion, fixture.Repository.ActiveSessionTokenVersion);
        Assert.Equal(1, fixture.Repository.IsSessionFamilyActiveCalls);
    }

    private static LoginCommand Login(
        string email = "User@Example.Test",
        string password = "Valid-Password-123!",
        string? ipAddress = "192.0.2.10") => new(
        EntityId.New(),
        email,
        password,
        ipAddress,
        "session-unit-test");

    private static VerifyLoginTotpCommand VerifyTotp(string code = "123456") => new(
        EntityId.New(),
        EntityId.New(),
        code,
        "192.0.2.10",
        "session-unit-test");

    private static RefreshSessionCommand Refresh() => new(
        EntityId.New(),
        "refresh-token-current",
        "192.0.2.10",
        "session-unit-test");

    private static LogoutCommand Logout(
        bool allSessions = false,
        string? refreshToken = null) => Logout(
        Actor(User()),
        allSessions,
        refreshToken);

    private static LogoutCommand Logout(
        SessionActor actor,
        bool allSessions = false,
        string? refreshToken = null) => new(
        EntityId.New(),
        actor,
        refreshToken,
        allSessions,
        "192.0.2.10",
        "session-unit-test");

    private static SessionActor Actor(AuthenticationUserSnapshot user) => new(
        user.Id,
        user.Role,
        user.TokenVersion,
        EntityId.New());

    private static AuthenticationUserSnapshot User(
        bool totpEnabled = false,
        UserLifecycle status = UserLifecycle.Active,
        SystemRole role = SystemRole.User,
        long? lastAcceptedStep = null) => new(
        EntityId.New(),
        "user@example.test",
        "user@example.test",
        "Session User",
        "password-hash",
        role,
        status,
        totpEnabled ? JsonSerializer.SerializeToElement(new { v = 1 }) : null,
        lastAcceptedStep,
        EntityId.New(),
        TokenVersion: 7,
        FailedLoginCount: 0,
        LockedUntil: null,
        LastLoginAt: null,
        Version: 11,
        Now.AddDays(-30),
        Now);

    private static TotpChallengeSnapshot Challenge(
        AuthenticationUserSnapshot user,
        EntityId? securityStamp = null,
        long? tokenVersion = null) => new(
        EntityId.New(),
        user.Id,
        "login",
        SecretEnvelope: null,
        ResponseBodyEnvelope: null,
        securityStamp ?? user.SecurityStamp,
        tokenVersion ?? user.TokenVersion,
        Now.AddMinutes(5),
        user);

    private static void AssertFailure<T>(Result<T> result, string expectedCode)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
    }

    private static void AssertAudit(AuditEntry entry, string action, string outcome)
    {
        Assert.Equal(action, entry.Action);
        Assert.True(entry.AfterState.HasValue);
        Assert.Equal(
            outcome,
            entry.AfterState.Value.GetProperty("outcome").GetString());
    }

    private sealed class Fixture
    {
        internal Fixture(AuthenticationUserSnapshot? user = null)
        {
            AuthenticationUserSnapshot fallback = user ?? User();
            Repository = new RecordingRepository
            {
                AuthenticationUser = fallback,
                CurrentUser = fallback,
                PasswordLoginResult = new PasswordLoginPersistenceResult(
                    PasswordLoginDisposition.SessionCreated,
                    fallback,
                    EntityId.New(),
                    RetryAfterSeconds: null),
                MfaLoginResult = new MfaLoginPersistenceResult(
                    MfaLoginDisposition.SessionCreated,
                    fallback,
                    EntityId.New()),
                RefreshRotationResult = new RefreshRotationPersistenceResult(
                    RefreshRotationDisposition.Rotated,
                    fallback,
                    EntityId.New()),
                LogoutResult = new LogoutPersistenceResult(fallback, Changed: true),
            };
            UnitOfWorkFactory = new RecordingUnitOfWorkFactory();
            AuditAppender = new RecordingAuditAppender();
            PasswordHasher = new RecordingPasswordHasher();
            RefreshHasher = new RecordingRefreshHasher();
            ChallengeHasher = new RecordingChallengeHasher();
            TotpAuthenticator = new RecordingTotpAuthenticator();
            TotpSecretEnvelope = new RecordingTotpSecretEnvelope();
            AccessTokenIssuer = new RecordingAccessTokenIssuer();
            RateLimiter = new RecordingRateLimiter();
            Service = new SessionAuthenticationUseCaseService(
                Repository,
                UnitOfWorkFactory,
                AuditAppender,
                PasswordHasher,
                RefreshHasher,
                ChallengeHasher,
                TotpAuthenticator,
                TotpSecretEnvelope,
                AccessTokenIssuer,
                RateLimiter,
                new SessionPolicy(
                    MaximumPasswordFailures: 5,
                    TimeSpan.FromMinutes(15),
                    TimeSpan.FromMinutes(5),
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromDays(30)),
                new FixedTimeProvider(Now));
        }

        internal SessionAuthenticationUseCaseService Service { get; }

        internal RecordingRepository Repository { get; }

        internal RecordingUnitOfWorkFactory UnitOfWorkFactory { get; }

        internal RecordingAuditAppender AuditAppender { get; }

        internal RecordingPasswordHasher PasswordHasher { get; }

        internal RecordingRefreshHasher RefreshHasher { get; }

        internal RecordingChallengeHasher ChallengeHasher { get; }

        internal RecordingTotpAuthenticator TotpAuthenticator { get; }

        internal RecordingTotpSecretEnvelope TotpSecretEnvelope { get; }

        internal RecordingAccessTokenIssuer AccessTokenIssuer { get; }

        internal RecordingRateLimiter RateLimiter { get; }
    }

    private sealed class RecordingRepository : IIdentitySessionRepository
    {
        internal AuthenticationUserSnapshot? AuthenticationUser { get; set; }

        internal AuthenticationUserSnapshot? CurrentUser { get; set; }

        internal TotpChallengeSnapshot? TotpChallenge { get; set; }

        internal PasswordLoginPersistenceResult PasswordLoginResult { get; set; } = null!;

        internal MfaLoginPersistenceResult MfaLoginResult { get; set; } = null!;

        internal RefreshRotationPersistenceResult RefreshRotationResult { get; set; } = null!;

        internal LogoutPersistenceResult LogoutResult { get; set; } = null!;

        internal bool HasRefreshCredential { get; set; } = true;

        internal bool SessionFamilyActive { get; set; } = true;

        internal int FindAuthenticationUserCalls { get; private set; }

        internal int GetAuthenticationUserCalls { get; private set; }

        internal int IsSessionFamilyActiveCalls { get; private set; }

        internal int HasRefreshCredentialCalls { get; private set; }

        internal int RecordPasswordFailureCalls { get; private set; }

        internal int CompletePasswordLoginCalls { get; private set; }

        internal int FindTotpChallengeCalls { get; private set; }

        internal int CompleteMfaLoginCalls { get; private set; }

        internal int RotateRefreshSessionCalls { get; private set; }

        internal int LogoutCalls { get; private set; }

        internal string? NormalizedEmail { get; private set; }

        internal EntityId? GetAuthenticationUserId { get; private set; }

        internal EntityId? ActiveSessionUserId { get; private set; }

        internal EntityId? ActiveSessionFamilyId { get; private set; }

        internal long? ActiveSessionTokenVersion { get; private set; }

        internal EntityId? PasswordFailureUserId { get; private set; }

        internal EntityId? PasswordFailureSecurityStamp { get; private set; }

        internal int? MaximumPasswordFailures { get; private set; }

        internal TimeSpan? LockoutDuration { get; private set; }

        internal IUnitOfWorkContext? PasswordFailureContext { get; private set; }

        internal PasswordLoginWrite? PasswordLoginWrite { get; private set; }

        internal long? AcceptedStep { get; private set; }

        internal RefreshSessionWrite? MfaSessionWrite { get; private set; }

        internal RefreshSessionWrite? RefreshReplacement { get; private set; }

        internal IReadOnlyList<CredentialHashCandidate>? LogoutCandidates { get; private set; }

        internal bool? LogoutAllSessions { get; private set; }

        public ValueTask<AuthenticationUserSnapshot?> FindAuthenticationUserAsync(
            string normalizedEmail,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindAuthenticationUserCalls++;
            NormalizedEmail = normalizedEmail;
            return ValueTask.FromResult(AuthenticationUser);
        }

        public ValueTask<AuthenticationUserSnapshot?> GetAuthenticationUserAsync(
            EntityId userId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetAuthenticationUserCalls++;
            GetAuthenticationUserId = userId;
            return ValueTask.FromResult(CurrentUser);
        }

        public ValueTask<bool> IsSessionFamilyActiveAsync(
            EntityId userId,
            EntityId familyId,
            long tokenVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsSessionFamilyActiveCalls++;
            ActiveSessionUserId = userId;
            ActiveSessionFamilyId = familyId;
            ActiveSessionTokenVersion = tokenVersion;
            return ValueTask.FromResult(SessionFamilyActive);
        }

        public ValueTask<bool> HasRefreshCredentialAsync(
            IReadOnlyList<CredentialHashCandidate> candidates,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HasRefreshCredentialCalls++;
            Assert.NotEmpty(candidates);
            return ValueTask.FromResult(HasRefreshCredential);
        }

        public ValueTask<PasswordFailureDisposition> RecordPasswordFailureAsync(
            EntityId userId,
            EntityId expectedSecurityStamp,
            int maximumFailures,
            TimeSpan lockoutDuration,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordPasswordFailureCalls++;
            PasswordFailureUserId = userId;
            PasswordFailureSecurityStamp = expectedSecurityStamp;
            MaximumPasswordFailures = maximumFailures;
            LockoutDuration = lockoutDuration;
            PasswordFailureContext = unitOfWorkContext;
            return ValueTask.FromResult(PasswordFailureDisposition.Recorded);
        }

        public ValueTask<PasswordLoginPersistenceResult> CompletePasswordLoginAsync(
            PasswordLoginWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompletePasswordLoginCalls++;
            PasswordLoginWrite = write;
            return ValueTask.FromResult(PasswordLoginResult);
        }

        public ValueTask<TotpChallengeSnapshot?> FindTotpChallengeAsync(
            IReadOnlyList<CredentialHashCandidate> candidates,
            string kind,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindTotpChallengeCalls++;
            Assert.NotEmpty(candidates);
            Assert.Equal("login", kind);
            return ValueTask.FromResult(TotpChallenge);
        }

        public ValueTask<MfaLoginPersistenceResult> CompleteMfaLoginAsync(
            IReadOnlyList<CredentialHashCandidate> challengeCandidates,
            long acceptedStep,
            RefreshSessionWrite session,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompleteMfaLoginCalls++;
            Assert.NotEmpty(challengeCandidates);
            AcceptedStep = acceptedStep;
            MfaSessionWrite = session;
            return ValueTask.FromResult(MfaLoginResult);
        }

        public ValueTask<RefreshRotationPersistenceResult> RotateRefreshSessionAsync(
            IReadOnlyList<CredentialHashCandidate> candidates,
            RefreshSessionWrite replacement,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RotateRefreshSessionCalls++;
            Assert.NotEmpty(candidates);
            RefreshReplacement = replacement;
            return ValueTask.FromResult(RefreshRotationResult);
        }

        public ValueTask<LogoutPersistenceResult> LogoutAsync(
            SessionActor actor,
            IReadOnlyList<CredentialHashCandidate> refreshCandidates,
            bool allSessions,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogoutCalls++;
            LogoutCandidates = refreshCandidates;
            LogoutAllSessions = allSessions;
            return ValueTask.FromResult(LogoutResult);
        }

        public ValueTask<SecurityMutationPersistenceResult> ChangePasswordAsync(
            EntityId userId,
            long expectedVersion,
            EntityId expectedSecurityStamp,
            string passwordHash,
            EntityId newSecurityStamp,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<SecurityMutationPersistenceResult> CreateTotpSetupAsync(
            EntityId userId,
            EntityId expectedSecurityStamp,
            TotpChallengeWrite challenge,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<SecurityMutationPersistenceResult> ConfirmTotpAsync(
            TotpConfirmWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<SecurityMutationPersistenceResult> DisableTotpAsync(
            EntityId userId,
            long expectedVersion,
            EntityId expectedSecurityStamp,
            long acceptedStep,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();
    }

    private sealed class RecordingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCalls++;
            return ValueTask.FromResult<IUnitOfWork>(new RecordingUnitOfWork(this));
        }

        private sealed class RecordingUnitOfWork(RecordingUnitOfWorkFactory owner) : IUnitOfWork
        {
            public IUnitOfWorkContext Context { get; } = new UnitOfWorkContext();

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.CommitCalls++;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                owner.DisposeCalls++;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class UnitOfWorkContext : IUnitOfWorkContext;
    }

    private sealed class RecordingAuditAppender : IAuditAppender
    {
        internal List<AuditEntry> Entries { get; } = [];

        internal List<IUnitOfWorkContext> Contexts { get; } = [];

        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            Contexts.Add(unitOfWorkContext);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPasswordHasher : IVersionedPasswordHasher
    {
        internal bool VerifyResult { get; set; } = true;

        internal int HashCalls { get; private set; }

        internal int VerifyCalls { get; private set; }

        public string Hash(string password)
        {
            HashCalls++;
            return "dummy-password-hash";
        }

        public bool Verify(string encodedHash, string password)
        {
            VerifyCalls++;
            return VerifyResult;
        }
    }

    private sealed class RecordingRefreshHasher : IRefreshCredentialHasher
    {
        internal IReadOnlyList<RefreshCredentialCandidate> Candidates { get; set; } =
            [new RefreshCredentialCandidate(Enumerable.Repeat((byte)0x11, 32).ToArray(), 1)];

        internal int CreateCalls { get; private set; }

        internal int HashCandidateCalls { get; private set; }

        public RefreshCredentialSecret Create()
        {
            CreateCalls++;
            return new RefreshCredentialSecret(
                "refresh-token-next",
                Enumerable.Repeat((byte)0x22, 32).ToArray(),
                PepperVersion: 2);
        }

        public IReadOnlyList<RefreshCredentialCandidate> HashCandidates(string token)
        {
            HashCandidateCalls++;
            return Candidates;
        }

        public bool Verify(string token, byte[] expectedHash, short pepperVersion) =>
            throw Unexpected();
    }

    private sealed class RecordingChallengeHasher : IOneTimeChallengeHasher
    {
        internal OneTimeChallengeSecret Secret { get; } = new(
            EntityId.New(),
            Enumerable.Repeat((byte)0x33, 32).ToArray(),
            PepperVersion: 3);

        internal int CreateCalls { get; private set; }

        internal int HashCandidateCalls { get; private set; }

        public OneTimeChallengeSecret Create()
        {
            CreateCalls++;
            return Secret;
        }

        public IReadOnlyList<OneTimeChallengeCandidate> HashCandidates(EntityId challenge)
        {
            HashCandidateCalls++;
            return [new OneTimeChallengeCandidate(Secret.Hash, Secret.PepperVersion)];
        }

        public bool Verify(EntityId challenge, byte[] expectedHash, short pepperVersion) =>
            throw Unexpected();
    }

    private sealed class RecordingTotpAuthenticator : ITotpAuthenticator
    {
        internal bool MatchResult { get; set; } = true;

        internal long MatchedStep { get; set; } = 2_000_001;

        internal int MatchCalls { get; private set; }

        public TotpProvisioningSecret CreateProvisioningSecret(string accountName) =>
            throw Unexpected();

        public string BuildProvisioningUri(string base32Secret, string accountName) =>
            throw Unexpected();

        public bool TryMatchStep(
            string base32Secret,
            string code,
            DateTimeOffset timestamp,
            out long matchedStep)
        {
            MatchCalls++;
            Assert.Equal("totp-secret", base32Secret);
            Assert.Equal("123456", code);
            Assert.Equal(Now, timestamp);
            matchedStep = MatchedStep;
            return MatchResult;
        }
    }

    private sealed class RecordingTotpSecretEnvelope : ITotpSecretEnvelope
    {
        internal int DecryptCalls { get; private set; }

        public JsonElement Encrypt(
            string base32Secret,
            TotpSecretEnvelopeTarget target,
            EntityId targetId) => throw Unexpected();

        public string Decrypt(
            JsonElement envelope,
            TotpSecretEnvelopeTarget target,
            EntityId targetId)
        {
            DecryptCalls++;
            Assert.Equal(TotpSecretEnvelopeTarget.User, target);
            return "totp-secret";
        }
    }

    private sealed class RecordingAccessTokenIssuer : IAccessTokenIssuer
    {
        internal int Calls { get; private set; }

        internal AccessTokenSubject? Subject { get; private set; }

        public AccessTokenSecret Issue(AccessTokenSubject subject)
        {
            Calls++;
            Subject = subject;
            return new AccessTokenSecret("access-token", Now.AddMinutes(15));
        }
    }

    private sealed class RecordingRateLimiter : ILoginFailureRateLimiter
    {
        internal LoginFailureRateLimitDecision Decision { get; set; } = new(
            LoginFailureRateLimitDisposition.Allowed,
            RetryAfterSeconds: null);

        internal int Calls { get; private set; }

        internal List<string> IpAddresses { get; } = [];

        public ValueTask<LoginFailureRateLimitDecision> RecordFailureAsync(
            string ipAddress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            IpAddresses.Add(ipAddress);
            return ValueTask.FromResult(Decision);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static InvalidOperationException Unexpected([System.Runtime.CompilerServices.CallerMemberName]
        string? member = null) => new($"Unexpected call to {member}.");
}
