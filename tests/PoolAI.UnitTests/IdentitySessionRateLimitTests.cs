using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Infrastructure;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class IdentitySessionRateLimitTests
{
    private const string LoginIpKey =
        "rate:login:v1:{41aa02b92bf8238ab208f319f0d98325}";

    [Fact]
    public async Task LoginFailureUsesDedicatedCanonicalIpScopeAndConfiguredLimit()
    {
        QueueCounter counter = new(
            FixedWindowCounterResult.Allowed(1),
            FixedWindowCounterResult.Allowed(2));
        OperationsLoginFailureRateLimiter limiter = CreateLimiter(counter);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        LoginFailureRateLimitDecision ipv4 = await limiter.RecordFailureAsync(
            "192.0.2.10",
            cancellationToken).ConfigureAwait(true);
        LoginFailureRateLimitDecision mapped = await limiter.RecordFailureAsync(
            "::ffff:192.0.2.10",
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(LoginFailureRateLimitDisposition.Allowed, ipv4.Disposition);
        Assert.Equal(LoginFailureRateLimitDisposition.Allowed, mapped.Disposition);
        Assert.Collection(
            counter.Requests,
            request => Assert.Equal(new FixedWindowCounterRequest(LoginIpKey, 20), request),
            request => Assert.Equal(new FixedWindowCounterRequest(LoginIpKey, 20), request));
    }

    [Theory]
    [MemberData(nameof(RejectedAndUnavailable))]
    public async Task LoginFailureMapsRedisRejectAndFailureWithoutFallback(
        FixedWindowCounterResult counterResult,
        LoginFailureRateLimitDisposition expectedDisposition,
        long expectedRetryAfter)
    {
        QueueCounter counter = new(counterResult);
        OperationsLoginFailureRateLimiter limiter = CreateLimiter(counter);

        LoginFailureRateLimitDecision decision = await limiter.RecordFailureAsync(
            "192.0.2.10",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(expectedDisposition, decision.Disposition);
        Assert.Equal(expectedRetryAfter, decision.RetryAfterSeconds);
        Assert.Single(counter.Requests);
    }

    [Fact]
    public async Task MalformedNonemptyLoginIpFailsClosedBeforeRedis()
    {
        QueueCounter counter = new();
        OperationsLoginFailureRateLimiter limiter = CreateLimiter(counter);

        LoginFailureRateLimitDecision decision = await limiter.RecordFailureAsync(
            "not-an-ip",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(LoginFailureRateLimitDisposition.Unavailable, decision.Disposition);
        Assert.Equal(1, decision.RetryAfterSeconds);
        Assert.Empty(counter.Requests);
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
    public async Task RejectedCredentialStopsBeforeAnyPostgresWriteWhenRedisDoesNotAllow(
        LoginFailureRateLimitDisposition disposition,
        string expectedCode,
        long expectedRetryAfter)
    {
        // Governing contract: section 7.4.1 and redis-contract.md. An unknown
        // account still traverses comparable password verification, but a failed
        // login cannot enter the PostgreSQL write/audit path until Redis allows
        // that failure to be counted.
        ReadOnlySessionRepository repository = new();
        ThrowingUnitOfWorkFactory unitOfWorkFactory = new();
        SessionAuthenticationUseCaseService service = new(
            repository,
            unitOfWorkFactory,
            new ThrowingAuditAppender(),
            new RejectingPasswordHasher(),
            new ThrowingRefreshHasher(),
            new ThrowingChallengeHasher(),
            new ThrowingTotpAuthenticator(),
            new ThrowingTotpEnvelope(),
            new ThrowingAccessTokenIssuer(),
            new FixedLoginRateLimiter(new(disposition, expectedRetryAfter)),
            new SessionPolicy(
                MaximumPasswordFailures: 5,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(10),
                TimeSpan.FromDays(30)),
            TimeProvider.System);

        Result<LoginResultView> result = await service.ExecuteAsync(
            new LoginCommand(
                EntityId.New(),
                "unknown@poolai.test",
                "Wrong-Password-123!",
                "192.0.2.10",
                "identity-session-unit-test"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(expectedRetryAfter, result.Error.RetryAfterSeconds);
        Assert.Equal(1, repository.FindCalls);
        Assert.Equal(0, unitOfWorkFactory.BeginCalls);
    }

    [Fact]
    public async Task UnknownWellFormedRefreshCredentialStopsBeforeOpeningAUnitOfWork()
    {
        ReadOnlySessionRepository repository = new();
        ThrowingUnitOfWorkFactory unitOfWorkFactory = new();
        PreflightRefreshHasher refreshHasher = new();
        SessionAuthenticationUseCaseService service = new(
            repository,
            unitOfWorkFactory,
            new ThrowingAuditAppender(),
            new RejectingPasswordHasher(),
            refreshHasher,
            new ThrowingChallengeHasher(),
            new ThrowingTotpAuthenticator(),
            new ThrowingTotpEnvelope(),
            new ThrowingAccessTokenIssuer(),
            new FixedLoginRateLimiter(new(LoginFailureRateLimitDisposition.Allowed, null)),
            new SessionPolicy(
                MaximumPasswordFailures: 5,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(10),
                TimeSpan.FromDays(30)),
            TimeProvider.System);

        Result<TokenPairView> result = await service.ExecuteAsync(
            new RefreshSessionCommand(
                EntityId.New(),
                new string('A', 43),
                "192.0.2.10",
                "identity-session-unit-test"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(IdentityErrorCodes.RefreshTokenInvalid, result.Error.Code);
        Assert.Equal(1, repository.RefreshPreflightCalls);
        Assert.Equal(0, refreshHasher.CreateCalls);
        Assert.Equal(0, unitOfWorkFactory.BeginCalls);
    }

    [Theory]
    [MemberData(nameof(TotpReplayRateLimitCases))]
    public async Task TotpReplayChecksRedisBeforeOpeningAuditTransaction(
        LoginFailureRateLimitDisposition disposition,
        long? retryAfterSeconds,
        string expectedCode,
        int expectedBeginCalls,
        int expectedAuditCalls)
    {
        const long matchedStep = 100;
        TotpChallengeSnapshot challenge = LoginTotpChallenge(lastAcceptedStep: 100);
        Assert.True(challenge.User.TotpLastAcceptedStep >= matchedStep);

        ReadOnlySessionRepository repository = new() { Challenge = challenge };
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingAuditAppender auditAppender = new();
        SessionAuthenticationUseCaseService service = new(
            repository,
            unitOfWorkFactory,
            auditAppender,
            new RejectingPasswordHasher(),
            new RecordingRefreshHasher(),
            new FixedChallengeHasher(),
            new FixedTotpAuthenticator(matchedStep),
            new FixedTotpEnvelope(),
            new ThrowingAccessTokenIssuer(),
            new FixedLoginRateLimiter(new(disposition, retryAfterSeconds)),
            new SessionPolicy(
                MaximumPasswordFailures: 5,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(10),
                TimeSpan.FromDays(30)),
            TimeProvider.System);

        Result<TokenPairView> result = await service.ExecuteAsync(
            new VerifyLoginTotpCommand(
                EntityId.New(),
                challenge.Id,
                "123456",
                "192.0.2.10",
                "identity-session-unit-test"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(retryAfterSeconds, result.Error.RetryAfterSeconds);
        Assert.Equal(expectedBeginCalls, unitOfWorkFactory.BeginCalls);
        Assert.Equal(expectedBeginCalls, unitOfWorkFactory.CommitCalls);
        Assert.Equal(expectedAuditCalls, auditAppender.Entries.Count);
        Assert.Equal(0, repository.CompleteMfaCalls);

        if (expectedAuditCalls == 1)
        {
            AuditEntry entry = Assert.Single(auditAppender.Entries);
            Assert.Equal("identity.login.totp_replayed", entry.Action);
            Assert.True(entry.AfterState.HasValue);
            Assert.Equal(
                "totp_replay",
                entry.AfterState.Value.GetProperty("outcome").GetString());
        }
    }

    public static TheoryData<
        FixedWindowCounterResult,
        LoginFailureRateLimitDisposition,
        long> RejectedAndUnavailable() => new()
        {
            {
                FixedWindowCounterResult.Rejected(21, TimeSpan.FromMilliseconds(1_001)),
                LoginFailureRateLimitDisposition.Rejected,
                2
            },
            {
                FixedWindowCounterResult.Unavailable,
                LoginFailureRateLimitDisposition.Unavailable,
                1
            },
        };

    public static TheoryData<
        LoginFailureRateLimitDisposition,
        long?,
        string,
        int,
        int> TotpReplayRateLimitCases() => new()
        {
            {
                LoginFailureRateLimitDisposition.Rejected,
                19,
                IdentityErrorCodes.RateLimitExceeded,
                0,
                0
            },
            {
                LoginFailureRateLimitDisposition.Unavailable,
                1,
                IdentityErrorCodes.CoordinationUnavailable,
                0,
                0
            },
            {
                LoginFailureRateLimitDisposition.Allowed,
                null,
                IdentityErrorCodes.TotpCodeInvalid,
                1,
                1
            },
        };

    private static TotpChallengeSnapshot LoginTotpChallenge(long lastAcceptedStep)
    {
        DateTimeOffset now = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);
        AuthenticationUserSnapshot user = new(
            EntityId.New(),
            "test@example.test",
            "test@example.test",
            "Test user",
            "encoded-password",
            SystemRole.User,
            UserLifecycle.Active,
            JsonSerializer.SerializeToElement(new { v = 1 }),
            lastAcceptedStep,
            EntityId.New(),
            TokenVersion: 1,
            FailedLoginCount: 0,
            LockedUntil: null,
            LastLoginAt: null,
            Version: 1,
            now.AddDays(-1),
            now);
        return new TotpChallengeSnapshot(
            EntityId.New(),
            user.Id,
            "login",
            SecretEnvelope: null,
            ResponseBodyEnvelope: null,
            user.SecurityStamp,
            user.TokenVersion,
            now.AddMinutes(5),
            user);
    }

    private static OperationsLoginFailureRateLimiter CreateLimiter(IFixedWindowCounter counter) =>
        new(counter, new LoginFailureRateLimitOptions(new byte[32], 20));

    private sealed class QueueCounter(params FixedWindowCounterResult[] results)
        : IFixedWindowCounter
    {
        private readonly Queue<FixedWindowCounterResult> _results = new(results);

        internal List<FixedWindowCounterRequest> Requests { get; } = [];

        public ValueTask<FixedWindowCounterResult> IncrementAsync(
            FixedWindowCounterRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed class FixedLoginRateLimiter(LoginFailureRateLimitDecision decision)
        : ILoginFailureRateLimiter
    {
        public ValueTask<LoginFailureRateLimitDecision> RecordFailureAsync(
            string ipAddress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class ReadOnlySessionRepository : IIdentitySessionRepository
    {
        internal TotpChallengeSnapshot? Challenge { get; init; }

        internal int FindCalls { get; private set; }

        internal int RefreshPreflightCalls { get; private set; }

        internal int CompleteMfaCalls { get; private set; }

        public ValueTask<AuthenticationUserSnapshot?> FindAuthenticationUserAsync(
            string normalizedEmail,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindCalls++;
            return ValueTask.FromResult<AuthenticationUserSnapshot?>(null);
        }

        public ValueTask<AuthenticationUserSnapshot?> GetAuthenticationUserAsync(
            EntityId userId,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<UserStatusSnapshot?> ReadCanonicalAuthorizationAsync(
            EntityId userId,
            EntityId familyId,
            long tokenVersion,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<bool> HasRefreshCredentialAsync(
            IReadOnlyList<CredentialHashCandidate> candidates,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshPreflightCalls++;
            Assert.NotEmpty(candidates);
            return ValueTask.FromResult(false);
        }

        public ValueTask<PasswordFailureDisposition> RecordPasswordFailureAsync(
            EntityId userId,
            EntityId expectedSecurityStamp,
            int maximumFailures,
            TimeSpan lockoutDuration,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<PasswordLoginPersistenceResult> CompletePasswordLoginAsync(
            PasswordLoginWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<TotpChallengeSnapshot?> FindTotpChallengeAsync(
            IReadOnlyList<CredentialHashCandidate> candidates,
            string kind,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotEmpty(candidates);
            Assert.Equal("login", kind);
            return ValueTask.FromResult(Challenge);
        }

        public ValueTask<MfaLoginPersistenceResult> CompleteMfaLoginAsync(
            IReadOnlyList<CredentialHashCandidate> challengeCandidates,
            long acceptedStep,
            RefreshSessionWrite session,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompleteMfaCalls++;
            return ValueTask.FromResult(new MfaLoginPersistenceResult(
                MfaLoginDisposition.TotpReplay,
                Challenge?.User,
                SessionFamilyId: null));
        }

        public ValueTask<RefreshRotationPersistenceResult> RotateRefreshSessionAsync(
            IReadOnlyList<CredentialHashCandidate> candidates,
            RefreshSessionWrite replacement,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<LogoutPersistenceResult> LogoutAsync(
            SessionActor actor,
            IReadOnlyList<CredentialHashCandidate> refreshCandidates,
            bool allSessions,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();

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

    private sealed class ThrowingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        internal int BeginCalls { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCalls++;
            throw Unexpected();
        }
    }

    private sealed class RecordingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCalls++;
            return ValueTask.FromResult<IUnitOfWork>(new UnitOfWork(this));
        }

        private sealed class UnitOfWork(RecordingUnitOfWorkFactory owner) : IUnitOfWork
        {
            public IUnitOfWorkContext Context { get; } = new UnitOfWorkContext();

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.CommitCalls++;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class UnitOfWorkContext : IUnitOfWorkContext;
    }

    private sealed class RejectingPasswordHasher : IVersionedPasswordHasher
    {
        public string Hash(string password) => "poolai-password-v1:dummy";

        public bool Verify(string encodedHash, string password) => false;
    }

    private sealed class ThrowingRefreshHasher : IRefreshCredentialHasher
    {
        public RefreshCredentialSecret Create() => throw Unexpected();

        public IReadOnlyList<RefreshCredentialCandidate> HashCandidates(string token) =>
            throw Unexpected();

        public bool Verify(string token, byte[] expectedHash, short pepperVersion) =>
            throw Unexpected();
    }

    private sealed class PreflightRefreshHasher : IRefreshCredentialHasher
    {
        internal int CreateCalls { get; private set; }

        public RefreshCredentialSecret Create()
        {
            CreateCalls++;
            throw Unexpected();
        }

        public IReadOnlyList<RefreshCredentialCandidate> HashCandidates(string token) =>
        [
            new RefreshCredentialCandidate(new byte[32], 1),
        ];

        public bool Verify(string token, byte[] expectedHash, short pepperVersion) =>
            throw Unexpected();
    }

    private sealed class RecordingRefreshHasher : IRefreshCredentialHasher
    {
        public RefreshCredentialSecret Create() => new(
            "refresh-token",
            new byte[32],
            PepperVersion: 1);

        public IReadOnlyList<RefreshCredentialCandidate> HashCandidates(string token) =>
            throw Unexpected();

        public bool Verify(string token, byte[] expectedHash, short pepperVersion) =>
            throw Unexpected();
    }

    private sealed class ThrowingChallengeHasher : IOneTimeChallengeHasher
    {
        public OneTimeChallengeSecret Create() => throw Unexpected();

        public IReadOnlyList<OneTimeChallengeCandidate> HashCandidates(EntityId challenge) =>
            throw Unexpected();

        public bool Verify(EntityId challenge, byte[] expectedHash, short pepperVersion) =>
            throw Unexpected();
    }

    private sealed class FixedChallengeHasher : IOneTimeChallengeHasher
    {
        public OneTimeChallengeSecret Create() => throw Unexpected();

        public IReadOnlyList<OneTimeChallengeCandidate> HashCandidates(EntityId challenge) =>
        [
            new OneTimeChallengeCandidate(new byte[32], PepperVersion: 1),
        ];

        public bool Verify(EntityId challenge, byte[] expectedHash, short pepperVersion) =>
            throw Unexpected();
    }

    private sealed class ThrowingTotpAuthenticator : ITotpAuthenticator
    {
        public TotpProvisioningSecret CreateProvisioningSecret(string accountName) =>
            throw Unexpected();

        public string BuildProvisioningUri(string base32Secret, string accountName) =>
            throw Unexpected();

        public bool TryMatchStep(
            string base32Secret,
            string code,
            DateTimeOffset timestamp,
            out long matchedStep) => throw Unexpected();
    }

    private sealed class FixedTotpAuthenticator(long matchedStep) : ITotpAuthenticator
    {
        public TotpProvisioningSecret CreateProvisioningSecret(string accountName) =>
            throw Unexpected();

        public string BuildProvisioningUri(string base32Secret, string accountName) =>
            throw Unexpected();

        public bool TryMatchStep(
            string base32Secret,
            string code,
            DateTimeOffset timestamp,
            out long actualMatchedStep)
        {
            actualMatchedStep = matchedStep;
            return true;
        }
    }

    private sealed class ThrowingTotpEnvelope : ITotpSecretEnvelope
    {
        public JsonElement Encrypt(
            string base32Secret,
            TotpSecretEnvelopeTarget target,
            EntityId targetId) => throw Unexpected();

        public string Decrypt(
            JsonElement envelope,
            TotpSecretEnvelopeTarget target,
            EntityId targetId) => throw Unexpected();
    }

    private sealed class FixedTotpEnvelope : ITotpSecretEnvelope
    {
        public JsonElement Encrypt(
            string base32Secret,
            TotpSecretEnvelopeTarget target,
            EntityId targetId) => throw Unexpected();

        public string Decrypt(
            JsonElement envelope,
            TotpSecretEnvelopeTarget target,
            EntityId targetId) => "BASE32SECRET";
    }

    private sealed class ThrowingAccessTokenIssuer : IAccessTokenIssuer
    {
        public AccessTokenSecret Issue(AccessTokenSubject subject) => throw Unexpected();
    }

    private sealed class ThrowingAuditAppender : IAuditAppender
    {
        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();
    }

    private sealed class RecordingAuditAppender : IAuditAppender
    {
        internal List<AuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private static InvalidOperationException Unexpected() => new(
        "The Redis fail-closed path must not invoke this dependency.");
}
