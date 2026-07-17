#pragma warning disable MA0051 // Test scenarios keep complete security-command arrangements visible.
using System.Text;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class IdentityPersonalSecurityUseCaseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-07-17T03:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task PasswordReplayAndConflictUseOneUnitOfWorkAndSkipEveryWriteSideEffect()
    {
        AuthenticationUserSnapshot user = User();
        CommandIdempotencyResponse replay = new(
            CommandIdempotencyTerminalStatus.Completed,
            204,
            Body: null,
            BodyEnvelope: null,
            Headers(("ETag", "\"v2\"")),
            "user",
            user.Id);
        Fixture replayFixture = new(
            user,
            new QueueIdempotencyStore(CommandIdempotencyAcquireResult.Replay(replay)));

        Result<IdentityCommandOutcome> replayed = await replayFixture.Service.ExecuteAsync(
            ChangePassword(user) with { ExpectedVersion = 1 },
            TestContext.Current.CancellationToken);

        Assert.True(replayed.IsSuccess);
        Assert.True(replayed.Value.IsReplay);
        Assert.Equal("\"v2\"", replayed.Value.ETag);
        Assert.Equal(0, replayFixture.Repository.ChangePasswordCalls);
        Assert.Equal(1, replayFixture.PasswordHasher.VerifyCalls);
        Assert.Equal(1, replayFixture.PasswordHasher.HashCalls);
        Assert.Equal(0, replayFixture.AuditAppender.Calls);
        Assert.Equal(1, replayFixture.UnitOfWorkFactory.BeginCalls);
        Assert.Equal(0, replayFixture.UnitOfWorkFactory.CommitCalls);

        Fixture conflictFixture = new(
            user,
            new QueueIdempotencyStore(CommandIdempotencyAcquireResult.Conflict));
        Result<IdentityCommandOutcome> conflict = await conflictFixture.Service.ExecuteAsync(
            ChangePassword(user),
            TestContext.Current.CancellationToken);

        Assert.Equal(IdentityErrorCodes.IdempotencyConflict, conflict.Error.Code);
        Assert.Equal(0, conflictFixture.Repository.ChangePasswordCalls);
        Assert.Equal(1, conflictFixture.PasswordHasher.VerifyCalls);
        Assert.Equal(1, conflictFixture.UnitOfWorkFactory.BeginCalls);
        Assert.Equal(0, conflictFixture.UnitOfWorkFactory.CommitCalls);
    }

    [Fact]
    public async Task SetupAndConfirmSecretReplaysDecryptOnlyTheirResponseEnvelopes()
    {
        AuthenticationUserSnapshot user = User();
        EntityId challengeId = EntityId.New();
        TotpSetupResponseSecret setupSecret = new(
            challengeId,
            "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP",
            "otpauth://totp/PoolAI:test%40example.test?secret=seed",
            SessionPolicy.TotpSetupSeconds);
        Fixture setupFixture = new(
            user,
            new QueueIdempotencyStore(CommandIdempotencyAcquireResult.Replay(new(
                CommandIdempotencyTerminalStatus.Completed,
                200,
                Body: null,
                BodyEnvelope: Envelope("setup-replay"),
                EmptyHeaders(),
                "totp_setup",
                challengeId))));
        setupFixture.SetupResponseEnvelope.DecryptResult = setupSecret;

        Result<IdentityCommandOutcome<TotpSetupView>> setup = await setupFixture.Service.ExecuteAsync(
            SetupTotp(user),
            TestContext.Current.CancellationToken);

        Assert.True(setup.Value.IsReplay);
        Assert.Equal(challengeId, setup.Value.Value.ChallengeId);
        Assert.Equal(setupSecret.Base32Secret, setup.Value.Value.Secret);
        Assert.Equal(1, setupFixture.SetupResponseEnvelope.DecryptCalls);
        Assert.Equal(1, setupFixture.PasswordHasher.VerifyCalls);
        Assert.Equal(1, setupFixture.UnitOfWorkFactory.BeginCalls);
        Assert.Equal(0, setupFixture.UnitOfWorkFactory.CommitCalls);

        string[] recoveryCodes = RecoveryCodes();
        Fixture confirmFixture = new(
            user,
            new QueueIdempotencyStore(CommandIdempotencyAcquireResult.Replay(new(
                CommandIdempotencyTerminalStatus.Completed,
                200,
                Body: null,
                BodyEnvelope: Envelope("recovery-replay"),
                Headers(("ETag", "\"v2\"")),
                "totp_setup",
                challengeId))));
        confirmFixture.RecoveryEnvelope.DecryptResult = recoveryCodes;

        Result<IdentityCommandOutcome<TotpConfirmView>> confirm =
            await confirmFixture.Service.ExecuteAsync(
                ConfirmTotp(user, challengeId),
                TestContext.Current.CancellationToken);

        Assert.True(confirm.Value.IsReplay);
        Assert.Equal("\"v2\"", confirm.Value.ETag);
        Assert.Equal(recoveryCodes, confirm.Value.Value.RecoveryCodes);
        Assert.Equal(1, confirmFixture.RecoveryEnvelope.DecryptCalls);
        Assert.Equal(1, confirmFixture.Repository.FindChallengeCalls);
        Assert.Equal(1, confirmFixture.UnitOfWorkFactory.BeginCalls);
        Assert.Equal(0, confirmFixture.UnitOfWorkFactory.CommitCalls);
    }

    [Fact]
    public async Task WrongCurrentPasswordStopsBeforeMutationAndPersistsOnlyOpaqueRequestHash()
    {
        AuthenticationUserSnapshot user = User();
        QueueIdempotencyStore idempotency = new();
        Fixture fixture = new(user, idempotency);
        fixture.PasswordHasher.VerifyResult = false;
        ChangePasswordCommand command = ChangePassword(user);

        Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.Equal(IdentityErrorCodes.InvalidCredentials, result.Error.Code);
        Assert.Equal(1, fixture.PasswordHasher.VerifyCalls);
        Assert.Equal(0, fixture.PasswordHasher.HashCalls);
        Assert.Equal(0, fixture.Repository.ChangePasswordCalls);
        Assert.Equal(0, fixture.AuditAppender.Calls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        Assert.Same(idempotency.AcquireContexts[0], idempotency.CompletionContext);
        Assert.Single(idempotency.Requests);
        byte[] requestHash = idempotency.Requests[0].RequestHash.ToArray();
        Assert.Equal(32, requestHash.Length);
        Assert.DoesNotContain(
            command.CurrentPassword,
            Encoding.UTF8.GetString(requestHash),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            command.NewPassword,
            Encoding.UTF8.GetString(requestHash),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasswordVersionConflictIsCompletedWithCurrentEtag()
    {
        AuthenticationUserSnapshot user = User(version: 1);
        AuthenticationUserSnapshot current = User(
            id: user.Id,
            version: 7,
            tokenVersion: user.TokenVersion);
        QueueIdempotencyStore idempotency = new();
        Fixture fixture = new(user, idempotency);
        fixture.Repository.ChangePasswordResult = new(
            SecurityMutationDisposition.VersionConflict,
            current,
            CurrentVersion: 7);

        Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
            ChangePassword(user),
            TestContext.Current.CancellationToken);

        Assert.Equal(IdentityErrorCodes.VersionConflict, result.Error.Code);
        Assert.Equal("\"v7\"", result.Error.ETag);
        Assert.Equal(1, fixture.Repository.ChangePasswordCalls);
        Assert.Equal(1, fixture.PasswordHasher.HashCalls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        Assert.NotNull(idempotency.Completion);
        Assert.Equal(CommandIdempotencyTerminalStatus.Failed, idempotency.Completion.TerminalStatus);
        Assert.Equal(412, idempotency.Completion.ResponseStatus);
        Assert.Equal("\"v7\"", idempotency.Completion.ResponseHeaders.GetProperty("ETag").GetString());
        Assert.Null(idempotency.Completion.ResponseBodyEnvelope);
        Assert.Same(fixture.Repository.MutationContext, idempotency.AcquireContexts[0]);
    }

    [Fact]
    public async Task TotpSetupStoresOnlyEnvelopesAndAuditsInTheMutationUnitOfWork()
    {
        AuthenticationUserSnapshot user = User();
        QueueIdempotencyStore idempotency = new();
        Fixture fixture = new(user, idempotency);
        fixture.Repository.CreateSetupResult = new(SecurityMutationDisposition.Updated, user);

        Result<IdentityCommandOutcome<TotpSetupView>> result = await fixture.Service.ExecuteAsync(
            SetupTotp(user),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsReplay);
        Assert.Equal(fixture.ChallengeHasher.Secret.Challenge, result.Value.Value.ChallengeId);
        Assert.Equal(fixture.TotpAuthenticator.Provisioning.Base32Secret, result.Value.Value.Secret);
        Assert.Equal(SessionPolicy.TotpSetupSeconds, result.Value.Value.ExpiresIn);
        Assert.Equal(1, fixture.Repository.CreateSetupCalls);
        Assert.NotNull(fixture.Repository.SetupWrite);
        Assert.NotNull(fixture.Repository.SetupWrite.SecretEnvelope);
        Assert.NotNull(fixture.Repository.SetupWrite.ResponseBodyEnvelope);
        Assert.Equal(1, fixture.AuditAppender.Calls);
        Assert.Same(fixture.Repository.MutationContext, fixture.AuditAppender.Context);
        Assert.Same(fixture.Repository.MutationContext, idempotency.CompletionContext);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.BeginCalls);
        Assert.Same(fixture.Repository.MutationContext, idempotency.AcquireContexts[0]);
        Assert.Null(idempotency.Completion!.ResponseBody);
        Assert.NotNull(idempotency.Completion.ResponseBodyEnvelope);
        Assert.Equal("totp_setup", idempotency.Completion.ResourceType);
        Assert.Equal(result.Value.Value.ChallengeId, idempotency.Completion.ResourceId);
        Assert.DoesNotContain(
            fixture.TotpAuthenticator.Provisioning.Base32Secret,
            idempotency.Completion.ResponseBodyEnvelope!.Value.GetRawText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TotpConfirmPersistsEightHashesAndReturnsEncryptedReplayWithEtag()
    {
        AuthenticationUserSnapshot user = User();
        EntityId challengeId = EntityId.New();
        TotpChallengeSnapshot challenge = SetupChallenge(user, challengeId);
        AuthenticationUserSnapshot updated = User(
            id: user.Id,
            totpEnabled: true,
            version: 2,
            tokenVersion: 2);
        QueueIdempotencyStore idempotency = new();
        Fixture fixture = new(user, idempotency);
        fixture.Repository.Challenge = challenge;
        fixture.Repository.ConfirmResult = new(SecurityMutationDisposition.Updated, updated);

        Result<IdentityCommandOutcome<TotpConfirmView>> result = await fixture.Service.ExecuteAsync(
            ConfirmTotp(user, challengeId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("\"v2\"", result.Value.ETag);
        Assert.Equal(RecoveryCodes(), result.Value.Value.RecoveryCodes);
        Assert.Equal(1, fixture.Repository.ConfirmCalls);
        Assert.NotNull(fixture.Repository.ConfirmWrite);
        Assert.Equal(8, fixture.Repository.ConfirmWrite.RecoveryCodes.Count);
        Assert.All(
            fixture.Repository.ConfirmWrite.RecoveryCodes,
            static item => Assert.Equal(32, item.CodeHash.Length));
        Assert.Equal(fixture.TotpAuthenticator.MatchedStep, fixture.Repository.ConfirmWrite.AcceptedStep);
        Assert.Equal(1, fixture.AuditAppender.Calls);
        Assert.Same(fixture.Repository.MutationContext, fixture.AuditAppender.Context);
        Assert.Null(idempotency.Completion!.ResponseBody);
        Assert.NotNull(idempotency.Completion.ResponseBodyEnvelope);
        Assert.Equal("\"v2\"", idempotency.Completion.ResponseHeaders.GetProperty("ETag").GetString());
        Assert.DoesNotContain(
            RecoveryCodes()[0],
            idempotency.Completion.ResponseBodyEnvelope!.Value.GetRawText(),
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.UnitOfWorkFactory.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        Assert.Same(fixture.Repository.MutationContext, idempotency.AcquireContexts[0]);
    }

    [Fact]
    public async Task TotpDisableVerifiesBothCredentialsAndReturnsCommittedEtag()
    {
        AuthenticationUserSnapshot user = User(totpEnabled: true);
        AuthenticationUserSnapshot updated = User(
            id: user.Id,
            totpEnabled: false,
            version: 2,
            tokenVersion: 2);
        QueueIdempotencyStore idempotency = new();
        Fixture fixture = new(user, idempotency);
        fixture.Repository.DisableResult = new(SecurityMutationDisposition.Updated, updated);

        Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
            DisableTotp(user),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("\"v2\"", result.Value.ETag);
        Assert.Equal(1, fixture.PasswordHasher.VerifyCalls);
        Assert.Equal(1, fixture.TotpAuthenticator.MatchCalls);
        Assert.Equal(1, fixture.Repository.DisableCalls);
        Assert.Equal(fixture.TotpAuthenticator.MatchedStep, fixture.Repository.DisableAcceptedStep);
        Assert.Equal(1, fixture.AuditAppender.Calls);
        Assert.Same(fixture.Repository.MutationContext, fixture.AuditAppender.Context);
        Assert.Equal(204, idempotency.Completion!.ResponseStatus);
        Assert.Null(idempotency.Completion.ResponseBody);
        Assert.Null(idempotency.Completion.ResponseBodyEnvelope);
        Assert.Equal("\"v2\"", idempotency.Completion.ResponseHeaders.GetProperty("ETag").GetString());
        Assert.Equal(1, fixture.UnitOfWorkFactory.CommitCalls);
        Assert.Equal(1, fixture.UnitOfWorkFactory.BeginCalls);
        Assert.Same(fixture.Repository.MutationContext, idempotency.AcquireContexts[0]);
    }

    private static ChangePasswordCommand ChangePassword(AuthenticationUserSnapshot user) => new(
        EntityId.New(),
        Actor(user),
        "password-key",
        user.Version,
        "old password material",
        "new password material",
        "user requested rotation",
        "192.0.2.10",
        "unit-test");

    private static SetupTotpCommand SetupTotp(AuthenticationUserSnapshot user) => new(
        EntityId.New(),
        Actor(user),
        "setup-key",
        "old password material",
        "192.0.2.10",
        "unit-test");

    private static ConfirmTotpCommand ConfirmTotp(
        AuthenticationUserSnapshot user,
        EntityId challengeId) => new(
            EntityId.New(),
            Actor(user),
            "confirm-key",
            user.Version,
            challengeId,
            "123456",
            "192.0.2.10",
            "unit-test");

    private static DisableTotpCommand DisableTotp(AuthenticationUserSnapshot user) => new(
        EntityId.New(),
        Actor(user),
        "disable-key",
        user.Version,
        "old password material",
        "123456",
        "192.0.2.10",
        "unit-test");

    private static SessionActor Actor(AuthenticationUserSnapshot user) => new(
        user.Id,
        user.Role,
        user.TokenVersion,
        EntityId.New());

    private static AuthenticationUserSnapshot User(
        EntityId? id = null,
        bool totpEnabled = false,
        long version = 1,
        long tokenVersion = 1) => new(
            id ?? EntityId.New(),
            "test@example.test",
            "test@example.test",
            "Test user",
            "encoded-password",
            SystemRole.User,
            UserLifecycle.Active,
            totpEnabled ? Envelope("user-seed") : null,
            totpEnabled ? 100L : null,
            EntityId.New(),
            tokenVersion,
            FailedLoginCount: 0,
            LockedUntil: null,
            LastLoginAt: null,
            version,
            Now.AddDays(-1),
            Now);

    private static TotpChallengeSnapshot SetupChallenge(
        AuthenticationUserSnapshot user,
        EntityId challengeId) => new(
            challengeId,
            user.Id,
            "setup",
            Envelope("pending-seed"),
            Envelope("setup-response"),
            user.SecurityStamp,
            user.TokenVersion,
            Now.AddMinutes(10),
            user);

    private static string[] RecoveryCodes() =>
    [
        "AAAA-BBBB", "CCCC-DDDD", "EEEE-FFFF", "GGGG-HHHH",
        "JJJJ-KKKK", "MMMM-NNNN", "PPPP-QQQQ", "RRRR-SSSS",
    ];

    private static JsonElement Envelope(string kind) => JsonSerializer.SerializeToElement(new
    {
        v = 1,
        kind,
        ciphertext = "opaque-ciphertext",
    });

    private static JsonElement EmptyHeaders() => JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static JsonElement Headers(params (string Name, string Value)[] headers) =>
        JsonSerializer.SerializeToElement(headers.ToDictionary(
            static header => header.Name,
            static header => header.Value,
            StringComparer.Ordinal));

    private sealed class Fixture
    {
        internal Fixture(
            AuthenticationUserSnapshot user,
            QueueIdempotencyStore idempotencyStore)
        {
            Repository = new RecordingRepository { User = user };
            UnitOfWorkFactory = new RecordingUnitOfWorkFactory();
            IdempotencyStore = idempotencyStore;
            AuditAppender = new RecordingAuditAppender();
            PasswordHasher = new RecordingPasswordHasher();
            ChallengeHasher = new RecordingChallengeHasher();
            TotpAuthenticator = new RecordingTotpAuthenticator();
            SecretEnvelope = new RecordingSecretEnvelope();
            RecoveryGenerator = new RecordingRecoveryGenerator();
            RecoveryEnvelope = new RecordingRecoveryEnvelope();
            SetupResponseEnvelope = new RecordingSetupResponseEnvelope();
            Service = new PersonalSecurityUseCaseService(
                Repository,
                UnitOfWorkFactory,
                IdempotencyStore,
                AuditAppender,
                PasswordHasher,
                ChallengeHasher,
                TotpAuthenticator,
                SecretEnvelope,
                RecoveryGenerator,
                RecoveryEnvelope,
                SetupResponseEnvelope,
                new SessionPolicy(
                    MaximumPasswordFailures: 5,
                    TimeSpan.FromMinutes(15),
                    TimeSpan.FromMinutes(5),
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromDays(30)),
                new IdentityPolicy(
                    new Uri("https://poolai.example.test/", UriKind.Absolute),
                    12,
                    TimeSpan.FromMinutes(30),
                    "example.test",
                    Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray()),
                new FixedTimeProvider(Now));
        }

        internal PersonalSecurityUseCaseService Service { get; }

        internal RecordingRepository Repository { get; }

        internal RecordingUnitOfWorkFactory UnitOfWorkFactory { get; }

        internal QueueIdempotencyStore IdempotencyStore { get; }

        internal RecordingAuditAppender AuditAppender { get; }

        internal RecordingPasswordHasher PasswordHasher { get; }

        internal RecordingChallengeHasher ChallengeHasher { get; }

        internal RecordingTotpAuthenticator TotpAuthenticator { get; }

        internal RecordingSecretEnvelope SecretEnvelope { get; }

        internal RecordingRecoveryGenerator RecoveryGenerator { get; }

        internal RecordingRecoveryEnvelope RecoveryEnvelope { get; }

        internal RecordingSetupResponseEnvelope SetupResponseEnvelope { get; }
    }

    private sealed class RecordingRepository : IIdentitySessionRepository
    {
        internal AuthenticationUserSnapshot? User { get; init; }

        internal TotpChallengeSnapshot? Challenge { get; set; }

        internal SecurityMutationPersistenceResult? ChangePasswordResult { get; set; }

        internal SecurityMutationPersistenceResult? CreateSetupResult { get; set; }

        internal SecurityMutationPersistenceResult? ConfirmResult { get; set; }

        internal SecurityMutationPersistenceResult? DisableResult { get; set; }

        internal int ChangePasswordCalls { get; private set; }

        internal int CreateSetupCalls { get; private set; }

        internal int FindChallengeCalls { get; private set; }

        internal int ConfirmCalls { get; private set; }

        internal int DisableCalls { get; private set; }

        internal TotpChallengeWrite? SetupWrite { get; private set; }

        internal TotpConfirmWrite? ConfirmWrite { get; private set; }

        internal long? DisableAcceptedStep { get; private set; }

        internal IUnitOfWorkContext? MutationContext { get; private set; }

        public ValueTask<AuthenticationUserSnapshot?> GetAuthenticationUserAsync(
            EntityId userId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(User);
        }

        public ValueTask<TotpChallengeSnapshot?> FindTotpChallengeAsync(
            IReadOnlyList<CredentialHashCandidate> candidates,
            string kind,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindChallengeCalls++;
            return ValueTask.FromResult(Challenge);
        }

        public ValueTask<SecurityMutationPersistenceResult> ChangePasswordAsync(
            EntityId userId,
            long expectedVersion,
            EntityId expectedSecurityStamp,
            string passwordHash,
            EntityId newSecurityStamp,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChangePasswordCalls++;
            MutationContext = unitOfWorkContext;
            return ValueTask.FromResult(ChangePasswordResult
                ?? new SecurityMutationPersistenceResult(SecurityMutationDisposition.Updated, User));
        }

        public ValueTask<SecurityMutationPersistenceResult> CreateTotpSetupAsync(
            EntityId userId,
            EntityId expectedSecurityStamp,
            TotpChallengeWrite challenge,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateSetupCalls++;
            SetupWrite = challenge;
            MutationContext = unitOfWorkContext;
            return ValueTask.FromResult(CreateSetupResult
                ?? new SecurityMutationPersistenceResult(SecurityMutationDisposition.Updated, User));
        }

        public ValueTask<SecurityMutationPersistenceResult> ConfirmTotpAsync(
            TotpConfirmWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmCalls++;
            ConfirmWrite = write;
            MutationContext = unitOfWorkContext;
            return ValueTask.FromResult(ConfirmResult
                ?? new SecurityMutationPersistenceResult(SecurityMutationDisposition.Updated, User));
        }

        public ValueTask<SecurityMutationPersistenceResult> DisableTotpAsync(
            EntityId userId,
            long expectedVersion,
            EntityId expectedSecurityStamp,
            long acceptedStep,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisableCalls++;
            DisableAcceptedStep = acceptedStep;
            MutationContext = unitOfWorkContext;
            return ValueTask.FromResult(DisableResult
                ?? new SecurityMutationPersistenceResult(SecurityMutationDisposition.Updated, User));
        }

        public ValueTask<AuthenticationUserSnapshot?> FindAuthenticationUserAsync(
            string normalizedEmail,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<UserStatusSnapshot?> ReadCanonicalAuthorizationAsync(
            EntityId userId,
            EntityId familyId,
            long tokenVersion,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<bool> HasRefreshCredentialAsync(
            IReadOnlyList<CredentialHashCandidate> candidates,
            CancellationToken cancellationToken) => throw Unexpected();

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

        public ValueTask<MfaLoginPersistenceResult> CompleteMfaLoginAsync(
            IReadOnlyList<CredentialHashCandidate> challengeCandidates,
            long acceptedStep,
            RefreshSessionWrite session,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();

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

    private sealed class QueueIdempotencyStore(
        params CommandIdempotencyAcquireResult[] configured) : ICommandIdempotencyStore
    {
        private readonly Queue<CommandIdempotencyAcquireResult> _configured = new(configured);

        internal List<CommandIdempotencyRequest> Requests { get; } = [];

        internal List<IUnitOfWorkContext> AcquireContexts { get; } = [];

        internal CommandIdempotencyCompletion? Completion { get; private set; }

        internal IUnitOfWorkContext? CompletionContext { get; private set; }

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            AcquireContexts.Add(unitOfWorkContext);
            return ValueTask.FromResult(_configured.Count > 0
                ? _configured.Dequeue()
                : CommandIdempotencyAcquireResult.Acquired(new CommandIdempotencyLease(
                    request.Scope,
                    request.Key,
                    request.Owner,
                    Generation: 1,
                    Version: 1)));
        }

        public ValueTask<bool> CompleteAsync(
            CommandIdempotencyCompletion completion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completion = completion;
            CompletionContext = unitOfWorkContext;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> HeartbeatAsync(
            CommandIdempotencyHeartbeat heartbeat,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();
    }

    private sealed class RecordingAuditAppender : IAuditAppender
    {
        internal int Calls { get; private set; }

        internal IUnitOfWorkContext? Context { get; private set; }

        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Context = unitOfWorkContext;
            Assert.DoesNotContain("password", entry.Metadata.GetRawText(), StringComparison.OrdinalIgnoreCase);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPasswordHasher : IVersionedPasswordHasher
    {
        internal bool VerifyResult { get; set; } = true;

        internal int VerifyCalls { get; private set; }

        internal int HashCalls { get; private set; }

        public string Hash(string password)
        {
            HashCalls++;
            return "encoded-new-password";
        }

        public bool Verify(string encodedHash, string password)
        {
            VerifyCalls++;
            return VerifyResult;
        }
    }

    private sealed class RecordingChallengeHasher : IOneTimeChallengeHasher
    {
        internal OneTimeChallengeSecret Secret { get; } = new(
            EntityId.New(),
            Enumerable.Repeat((byte)0x11, 32).ToArray(),
            PepperVersion: 1);

        public OneTimeChallengeSecret Create() => Secret;

        public IReadOnlyList<OneTimeChallengeCandidate> HashCandidates(EntityId challenge) =>
            [new OneTimeChallengeCandidate(Secret.Hash, Secret.PepperVersion)];

        public bool Verify(EntityId challenge, byte[] expectedHash, short pepperVersion) =>
            throw Unexpected();
    }

    private sealed class RecordingTotpAuthenticator : ITotpAuthenticator
    {
        internal TotpProvisioningSecret Provisioning { get; } = new(
            "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP",
            "otpauth://totp/PoolAI:test%40example.test?secret=opaque");

        internal long MatchedStep { get; } = 2_000_000;

        internal int MatchCalls { get; private set; }

        public TotpProvisioningSecret CreateProvisioningSecret(string accountName) => Provisioning;

        public string BuildProvisioningUri(string base32Secret, string accountName) =>
            Provisioning.OtpAuthUri;

        public bool TryMatchStep(
            string base32Secret,
            string code,
            DateTimeOffset timestamp,
            out long matchedStep)
        {
            MatchCalls++;
            matchedStep = MatchedStep;
            return true;
        }
    }

    private sealed class RecordingSecretEnvelope : ITotpSecretEnvelope
    {
        public JsonElement Encrypt(
            string base32Secret,
            TotpSecretEnvelopeTarget target,
            EntityId targetId) => Envelope(target.ToString());

        public string Decrypt(
            JsonElement envelope,
            TotpSecretEnvelopeTarget target,
            EntityId targetId) => "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    }

    private sealed class RecordingRecoveryGenerator : ITotpRecoveryCodeGenerator
    {
        public IReadOnlyList<TotpRecoveryCodeSecret> CreateBatch() => RecoveryCodes()
            .Select((code, index) => new TotpRecoveryCodeSecret(
                code,
                Enumerable.Repeat(checked((byte)(index + 1)), 32).ToArray(),
                PepperVersion: 1))
            .ToArray();
    }

    private sealed class RecordingRecoveryEnvelope : ITotpRecoveryCodeEnvelope
    {
        internal IReadOnlyList<string> DecryptResult { get; set; } = RecoveryCodes();

        internal int DecryptCalls { get; private set; }

        public JsonElement Encrypt(
            IReadOnlyList<string> recoveryCodes,
            EntityId oneTimeTokenId,
            IdempotencySecretBinding binding) => Envelope("recovery");

        public IReadOnlyList<string> Decrypt(
            JsonElement envelope,
            EntityId oneTimeTokenId,
            IdempotencySecretBinding binding)
        {
            DecryptCalls++;
            return DecryptResult;
        }
    }

    private sealed class RecordingSetupResponseEnvelope : ITotpSetupResponseEnvelope
    {
        internal TotpSetupResponseSecret? DecryptResult { get; set; }

        internal int DecryptCalls { get; private set; }

        public JsonElement Encrypt(
            TotpSetupResponseSecret response,
            EntityId oneTimeTokenId,
            IdempotencySecretBinding binding) => Envelope("setup-response");

        public TotpSetupResponseSecret Decrypt(
            JsonElement envelope,
            EntityId oneTimeTokenId,
            IdempotencySecretBinding binding)
        {
            DecryptCalls++;
            return DecryptResult
                ?? throw new InvalidOperationException("A setup replay result was not configured.");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static InvalidOperationException Unexpected() => new(
        "The personal-security scenario called an unexpected dependency.");
}
#pragma warning restore MA0051
