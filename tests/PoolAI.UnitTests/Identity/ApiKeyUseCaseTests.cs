#pragma warning disable MA0048 // Test doubles are collocated with their focused use-case scenarios.
using System.Security.Cryptography;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;
using PoolAI.Modules.Identity.Infrastructure.Security;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests.Identity;

public sealed class ApiKeyUseCaseTests
{
    [Fact]
    public async Task CreateCommitsKeyAuditAndEncryptedResponseOnceThenReplaysExactly()
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = environment.Command(
            "exact-replay",
            name: "  Primary key  ",
            allowedCidrs: ["192.168.1.42/24"]);
        ApiKeyAccessDecision authorized = TestEnvironment.Authorized(command);

        Result<ApiKeyCreatedOutcome> first = await environment.Service.CreateAsync(
            command,
            authorized,
            TestContext.Current.CancellationToken);
        Result<ApiKeyCreatedOutcome> replay = await environment.Service.CreateAsync(
            command,
            authorized,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess, first.Error.Description);
        Assert.True(replay.IsSuccess, replay.Error.Description);
        Assert.False(first.Value.IsReplay);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(first.Value.Secret, replay.Value.Secret);
        Assert.Equal(first.Value.ApiKey.ApiKeyId, replay.Value.ApiKey.ApiKeyId);
        Assert.Equal(first.Value.ETag, replay.Value.ETag);
        Assert.Equal(first.Value.Location, replay.Value.Location);
        Assert.Equal(1, environment.Repository.CreateCount);
        Assert.Equal("  Primary key  ", environment.Repository.LastWrite!.Name);
        Assert.Equal("  Primary key  ", first.Value.ApiKey.Name);
        Assert.Equal("  Primary key  ", replay.Value.ApiKey.Name);
        Assert.Equal(
            ["192.168.1.0/24"],
            environment.Repository.LastWrite.AllowedCidrs);
        Assert.Single(environment.Audit.Entries);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
        Assert.Empty(environment.OperationalEvents.Events);
        Assert.Null(environment.Idempotency.Completed!.Body);
        Assert.NotNull(environment.Idempotency.Completed.BodyEnvelope);
        string audit = environment.Audit.Entries[0].AfterState!.Value.GetRawText();
        Assert.DoesNotContain(first.Value.Secret, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(environment.Repository.LastWrite.SecretHash),
            audit,
            StringComparison.OrdinalIgnoreCase);

        Result<ApiKeyCreatedOutcome> conflict = await environment.Service.CreateAsync(
            command with
            {
                Name = "Different normalized request",
            },
            authorized,
            TestContext.Current.CancellationToken);
        Assert.True(conflict.IsFailure);
        Assert.Equal(IdentityErrorCodes.IdempotencyConflict, conflict.Error.Code);
        Assert.Equal(1, environment.Repository.CreateCount);
    }

    [Fact]
    public async Task SevenFractionalDigitsAreNormalizedToPostgresMicrosecondsBeforeWriteAndReplay()
    {
        TestEnvironment environment = new();
        DateTimeOffset requestedExpiration = DateTimeOffset.Parse(
            "2026-08-01T02:03:04.1234567Z",
            System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset expectedExpiration = DateTimeOffset.Parse(
            "2026-08-01T02:03:04.1234560Z",
            System.Globalization.CultureInfo.InvariantCulture);
        CreateApiKeyCommand command = environment.Command(
            "microsecond-expiration") with
        {
            ExpiresAt = requestedExpiration,
        };

        Result<ApiKeyCreatedOutcome> first = await environment.Service.CreateAsync(
            command,
            TestEnvironment.Authorized(command),
            TestContext.Current.CancellationToken);
        Result<ApiKeyCreatedOutcome> replay = await environment.Service.CreateAsync(
            command,
            TestEnvironment.Authorized(command),
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess, first.Error.Description);
        Assert.True(replay.IsSuccess, replay.Error.Description);
        Assert.Equal(expectedExpiration, environment.Repository.LastWrite!.ExpiresAt);
        Assert.Equal(expectedExpiration, first.Value.ApiKey.ExpiresAt);
        Assert.Equal(expectedExpiration, replay.Value.ApiKey.ExpiresAt);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(1, environment.Repository.CreateCount);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("scope")]
    [InlineData("idempotency_key")]
    [InlineData("request")]
    [InlineData("resource_id")]
    [InlineData("headers")]
    [InlineData("body")]
    [InlineData("ciphertext")]
    public async Task TerminalReplayTransplantFailsAfterRollbackAlertsAndNeverCreates(
        string mutation)
    {
        TestEnvironment source = new();
        CreateApiKeyCommand sourceCommand = source.Command("replay-transplant");
        Result<ApiKeyCreatedOutcome> created = await source.Service.CreateAsync(
            sourceCommand,
            TestEnvironment.Authorized(sourceCommand),
            TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess, created.Error.Description);
        CommandIdempotencyResponse stored = source.Idempotency.Completed!;
        CreateApiKeyCommand replayCommand = MutateCommand(sourceCommand, mutation);
        CommandIdempotencyResponse replayResponse = MutateResponse(
            stored,
            created.Value,
            mutation);
        TestEnvironment replay = new();
        replay.Idempotency.ForcedReplayResponse = replayResponse;

        Result<ApiKeyCreatedOutcome> result = await replay.Service.CreateAsync(
            replayCommand,
            TestEnvironment.Authorized(replayCommand),
            TestContext.Current.CancellationToken);

        AssertReplayIntegrityFailure(
            result,
            replay,
            replayCommand,
            replayResponse,
            created.Value.Secret);
    }

    [Fact]
    public async Task StableSubscriptionDenialIsTerminalizedAndPreflightReplaysBeforeWork()
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = environment.Command("inactive-denial");
        ApiKeyAccessDecision inactive = new(
            ApiKeyAccessDecisionKind.SubscriptionInactive,
            command.UserId,
            command.GroupId,
            SubscriptionId: null,
            ObservedAt: null);

        Result<ApiKeyCreatedOutcome> first = await environment.Service.CreateAsync(
            command,
            inactive,
            TestContext.Current.CancellationToken);
        Result<ApiKeyCreatedOutcome?> preflight = await environment.Service.TryReplayAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsFailure);
        Assert.Equal("subscription_inactive", first.Error.Code);
        Assert.Equal(403, first.Error.Presentation!.Status);
        Assert.True(preflight.IsFailure);
        Assert.Equal("subscription_inactive", preflight.Error.Code);
        Assert.Equal(0, environment.Repository.CreateCount);
        Assert.Empty(environment.Audit.Entries);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
        Assert.Equal(
            CommandIdempotencyTerminalStatus.Failed,
            environment.Idempotency.Completed!.TerminalStatus);
    }

    [Fact]
    public async Task AdminReasonIsPreservedExactlyInAuditAndIdempotencyHash()
    {
        TestEnvironment environment = new();
        string reason = " \u00a0approved \U0001f600\u3000";
        CreateApiKeyCommand command = environment.Command("admin-reason") with
        {
            Actor = new ApiKeyActor(
                EntityId.New(),
                SystemRole.Admin,
                TokenVersion: 1),
            AccessMode = ApiKeyAccessMode.AdminProxy,
            Reason = reason,
        };

        Result<ApiKeyCreatedOutcome> created = await environment.Service.CreateAsync(
            command,
            TestEnvironment.Authorized(command),
            TestContext.Current.CancellationToken);
        Result<ApiKeyCreatedOutcome> changedReason =
            await environment.Service.CreateAsync(
                command with
                {
                    Reason = reason.Trim(),
                },
                TestEnvironment.Authorized(command),
                TestContext.Current.CancellationToken);

        Assert.True(created.IsSuccess, created.Error.Description);
        Assert.Equal(reason, Assert.Single(environment.Audit.Entries).Reason);
        Assert.True(changedReason.IsFailure);
        Assert.Equal(
            IdentityErrorCodes.IdempotencyConflict,
            changedReason.Error.Code);
        Assert.Equal(1, environment.Repository.CreateCount);
    }

    [Fact]
    public async Task ReadersEnforceSelfAndAdminOwnerBoundariesBeforeRepositoryAccess()
    {
        TestEnvironment environment = new();
        ApiKeyResource resource = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [resource];

        Result<ApiKeyPage> own = await environment.Service.ListAsync(
            new ListApiKeysQuery(
                environment.Actor,
                ApiKeyAccessMode.Self,
                environment.UserId,
                Cursor: null),
            TestContext.Current.CancellationToken);
        Result<ApiKeyPage> crossUserSelf = await environment.Service.ListAsync(
            new ListApiKeysQuery(
                environment.Actor,
                ApiKeyAccessMode.Self,
                EntityId.New(),
                Cursor: null),
            TestContext.Current.CancellationToken);
        ApiKeyActor admin = new(EntityId.New(), SystemRole.Admin, TokenVersion: 1);
        Result<ApiKeyControlPlaneSnapshot> adminGet =
            await environment.Service.GetAsync(
                new GetApiKeyQuery(
                    admin,
                    ApiKeyAccessMode.AdminProxy,
                    environment.UserId,
                    resource.Id),
                TestContext.Current.CancellationToken);
        Result<ApiKeyControlPlaneSnapshot> wrongOwner =
            await environment.Service.GetAsync(
                new GetApiKeyQuery(
                    admin,
                    ApiKeyAccessMode.AdminProxy,
                    EntityId.New(),
                    resource.Id),
                TestContext.Current.CancellationToken);

        Assert.True(own.IsSuccess, own.Error.Description);
        Assert.Single(own.Value.Data);
        Assert.True(crossUserSelf.IsFailure);
        Assert.Equal(IdentityErrorCodes.RoleRequired, crossUserSelf.Error.Code);
        Assert.True(adminGet.IsSuccess, adminGet.Error.Description);
        Assert.True(wrongOwner.IsFailure);
        Assert.Equal(IdentityErrorCodes.ResourceNotFound, wrongOwner.Error.Code);
    }

    [Fact]
    public async Task AuthorizedDecisionWithEmptyEvidenceFailsClosedBeforeUnitOfWork()
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = environment.Command("empty-evidence");
        ApiKeyAccessDecision invalid = new(
            ApiKeyAccessDecisionKind.Authorized,
            command.UserId,
            command.GroupId,
            SubscriptionId: default(EntityId),
            ObservedAt: TestTimestamp());

        Result<ApiKeyCreatedOutcome> result = await environment.Service.CreateAsync(
            command,
            invalid,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, result.Error.Code);
        Assert.Equal(0, environment.UnitOfWorkFactory.BeginCount);
        Assert.Equal(0, environment.Repository.CreateCount);
    }

    [Fact]
    public async Task ReaderRejectsRepositoryCrossOwnerLeakage()
    {
        TestEnvironment environment = new();
        environment.Repository.Items =
        [
            FakeRepository.Resource(
                EntityId.New(),
                environment.GroupId,
                EntityId.New()),
        ];
        environment.Repository.ReturnItemsWithoutOwnerFilter = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await environment.Service.ListAsync(
                new ListApiKeysQuery(
                    environment.Actor,
                    ApiKeyAccessMode.Self,
                    environment.UserId,
                    Cursor: null),
                TestContext.Current.CancellationToken).ConfigureAwait(true))
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task ReaderRejectsEmptyPageThatClaimsMoreRows()
    {
        TestEnvironment environment = new();
        environment.Repository.ForceHasMore = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await environment.Service.ListAsync(
                new ListApiKeysQuery(
                    environment.Actor,
                    ApiKeyAccessMode.Self,
                    environment.UserId,
                    Cursor: null),
                TestContext.Current.CancellationToken).ConfigureAwait(true))
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task CreateRejectsRepositoryResourceThatDoesNotMatchGeneratedWrite()
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = environment.Command("corrupt-created-row");
        environment.Repository.CorruptCreatedOwner = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await environment.Service.CreateAsync(
                command,
                TestEnvironment.Authorized(command),
                TestContext.Current.CancellationToken).ConfigureAwait(true))
            .ConfigureAwait(true);

        Assert.Equal(0, environment.UnitOfWorkFactory.CommitCount);
        Assert.Empty(environment.Audit.Entries);
        Assert.Null(environment.Idempotency.Completed);
    }

    [Fact]
    public async Task NullFailurePresentationAlertsAfterRollbackWithoutCredentialGeneration()
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = environment.Command("null-failure-presentation");
        environment.Idempotency.ForcedReplayResponse = new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Failed,
            Status: 403,
            Body: JsonSerializer.SerializeToElement(new
            {
                description = "Stored failure.",
                presentation = (object?)null,
            }),
            BodyEnvelope: null,
            Headers: JsonSerializer.SerializeToElement(new { }),
            ResourceType: null,
            ResourceId: null);

        Result<ApiKeyCreatedOutcome> result = await environment.Service.CreateAsync(
            command,
            TestEnvironment.Authorized(command),
            TestContext.Current.CancellationToken);

        AssertReplayIntegrityFailure(
            result,
            environment,
            command,
            environment.Idempotency.ForcedReplayResponse,
            forbiddenSecret: "never-generated-secret");
    }

    [Fact]
    public async Task NullSuccessApiKeyAlertsAfterRollbackWithoutCredentialGeneration()
    {
        TestEnvironment source = new();
        CreateApiKeyCommand command = source.Command("null-success-api-key");
        Result<ApiKeyCreatedOutcome> created = await source.Service.CreateAsync(
            command,
            TestEnvironment.Authorized(command),
            TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess, created.Error.Description);

        TestEnvironment replay = new(new NullApiKeyResponseEnvelope());
        replay.Idempotency.ForcedReplayResponse = source.Idempotency.Completed;
        Result<ApiKeyCreatedOutcome> result = await replay.Service.CreateAsync(
            command,
            TestEnvironment.Authorized(command),
            TestContext.Current.CancellationToken);

        AssertReplayIntegrityFailure(
            result,
            replay,
            command,
            replay.Idempotency.ForcedReplayResponse!,
            created.Value.Secret);
    }

    private static CreateApiKeyCommand MutateCommand(
        CreateApiKeyCommand command,
        string mutation) => mutation switch
        {
            "actor" => command with
            {
                Actor = new ApiKeyActor(
                    EntityId.New(),
                    SystemRole.Admin,
                    TokenVersion: 1),
                AccessMode = ApiKeyAccessMode.AdminProxy,
                Reason = "Security replay test",
            },
            "scope" => command with
            {
                Actor = new ApiKeyActor(
                    command.Actor.UserId,
                    SystemRole.Admin,
                    TokenVersion: 1),
                AccessMode = ApiKeyAccessMode.AdminProxy,
                Reason = "Security replay test",
            },
            "idempotency_key" => command with
            {
                IdempotencyKey = command.IdempotencyKey + "-other",
            },
            "request" => command with
            {
                Name = command.Name + " changed",
            },
            _ => command,
        };

    private static CommandIdempotencyResponse MutateResponse(
        CommandIdempotencyResponse response,
        ApiKeyCreatedOutcome created,
        string mutation) => mutation switch
        {
            "resource_id" => response with
            {
                ResourceId = EntityId.New(),
            },
            "headers" => response with
            {
                Headers = JsonSerializer.SerializeToElement(new
                {
                    ETag = created.ETag,
                }),
            },
            "body" => response with
            {
                Body = JsonSerializer.SerializeToElement(new
                {
                    invalid = true,
                }),
            },
            "ciphertext" => response with
            {
                BodyEnvelope = JsonSerializer.SerializeToElement(new
                {
                    invalid = true,
                }),
            },
            _ => response,
        };

    private static void AssertReplayIntegrityFailure(
        Result<ApiKeyCreatedOutcome> result,
        TestEnvironment environment,
        CreateApiKeyCommand command,
        CommandIdempotencyResponse? response,
        string forbiddenSecret)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Equal(0, environment.Repository.CreateCount);
        Assert.Equal(0, environment.Credentials.CreateCount);
        Assert.Empty(environment.Audit.Entries);
        Assert.Equal(0, environment.UnitOfWorkFactory.CommitCount);
        Assert.True(environment.OperationalEvents.WasUnitOfWorkDisposedBeforeWrite);
        (string eventName, JsonElement payload) =
            Assert.Single(environment.OperationalEvents.Events);
        Assert.Equal(
            "identity.api_key.idempotency_replay_integrity_failed",
            eventName);
        AssertSafeReplayAlert(payload, command, response, forbiddenSecret);
    }

    private static void AssertSafeReplayAlert(
        JsonElement payload,
        CreateApiKeyCommand command,
        CommandIdempotencyResponse? response,
        string forbiddenSecret)
    {
        Assert.Equal(
        [
            "access_mode",
            "actor_user_id",
            "group_id",
            "request_id",
            "response_resource_id",
            "target_user_id",
        ],
            payload.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        string eventPayload = payload.GetRawText();
        Assert.DoesNotContain(
            forbiddenSecret,
            eventPayload,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            command.IdempotencyKey,
            eventPayload,
            StringComparison.Ordinal);
        string? envelope = response?.BodyEnvelope?.GetRawText();
        if (!string.IsNullOrEmpty(envelope))
        {
            Assert.DoesNotContain(envelope, eventPayload, StringComparison.Ordinal);
        }
    }

    private sealed class TestEnvironment
    {
        internal TestEnvironment(
            IApiKeyCreateResponseEnvelope? responseEnvelope = null)
        {
            UserId = EntityId.New();
            GroupId = EntityId.New();
            Actor = new ApiKeyActor(UserId, SystemRole.User, TokenVersion: 1);
            Repository = new FakeRepository();
            UnitOfWorkFactory = new RecordingUnitOfWorkFactory();
            Idempotency = new MemoryIdempotencyStore();
            Audit = new RecordingAuditAppender();
            OperationalEvents = new RecordingOperationalEventWriter(UnitOfWorkFactory);
            byte[] apiPepper = Enumerable.Repeat((byte)0x41, 32).ToArray();
            ApiKeyCredentialService credentialService = new(new ApiKeyHashOptions(
                "sk-unit-",
                new ApiKeyPepper(1, apiPepper),
                previous: null));
            Credentials = new CountingCredentialService(credentialService);
            byte[] envelopeKey = Enumerable.Repeat((byte)0x45, 32).ToArray();
            ApiKeyCreateResponseEnvelopeV1 envelope = new(new EnvelopeKeyRingOptions(
                "unit-v1",
                envelopeKey,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["unit-v1"] = envelopeKey,
                }));
            IdentityPolicy policy = new(
                new Uri("https://poolai.example"),
                passwordMinimumLength: 12,
                TimeSpan.FromMinutes(30),
                "poolai.example",
                Enumerable.Repeat((byte)0x49, 32).ToArray());
            Service = new ApiKeyUseCaseService(
                Repository,
                UnitOfWorkFactory,
                Idempotency,
                Audit,
                Credentials,
                responseEnvelope ?? envelope,
                OperationalEvents,
                policy);
        }

        internal EntityId UserId { get; }

        internal EntityId GroupId { get; }

        internal ApiKeyActor Actor { get; }

        internal FakeRepository Repository { get; }

        internal RecordingUnitOfWorkFactory UnitOfWorkFactory { get; }

        internal MemoryIdempotencyStore Idempotency { get; }

        internal RecordingAuditAppender Audit { get; }

        internal RecordingOperationalEventWriter OperationalEvents { get; }

        internal CountingCredentialService Credentials { get; }

        internal ApiKeyUseCaseService Service { get; }

        internal CreateApiKeyCommand Command(
            string suffix,
            string name = "Unit API Key",
            IReadOnlyList<string>? allowedCidrs = null) => new(
            EntityId.New(),
            Actor,
            ApiKeyAccessMode.Self,
            UserId,
            GroupId,
            $"api-key-{suffix}-{Guid.NewGuid():N}",
            name,
            ExpiresAt: null,
            allowedCidrs ?? [],
            Reason: null,
            IpAddress: "192.0.2.10",
            UserAgent: "api-key-unit-test");

        internal static ApiKeyAccessDecision Authorized(CreateApiKeyCommand command) => new(
            ApiKeyAccessDecisionKind.Authorized,
            command.UserId,
            command.GroupId,
            EntityId.New(),
            TestTimestamp());
    }

    private sealed class FakeRepository : IApiKeyRepository
    {
        internal int CreateCount { get; private set; }

        internal ApiKeyCreateWrite? LastWrite { get; private set; }

        internal IReadOnlyList<ApiKeyResource> Items { get; set; } = [];

        internal bool ReturnItemsWithoutOwnerFilter { get; set; }

        internal bool CorruptCreatedOwner { get; set; }

        internal bool ForceHasMore { get; set; }

        public ValueTask<ApiKeySlice> ListAsync(
            EntityId userId,
            ApiKeyCursor? cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            _ = cursor;
            _ = cancellationToken;
            return ValueTask.FromResult(new ApiKeySlice(
                (ReturnItemsWithoutOwnerFilter
                        ? Items
                        : Items.Where(value => value.UserId == userId))
                    .Take(limit)
                    .ToArray(),
                HasMore: ForceHasMore));
        }

        public ValueTask<ApiKeyResource?> GetAsync(
            EntityId userId,
            EntityId apiKeyId,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return ValueTask.FromResult(
                Items.SingleOrDefault(value =>
                    value.UserId == userId && value.Id == apiKeyId));
        }

        public ValueTask<IReadOnlyList<ApiKeyAuthenticationCandidate>>
            ListAuthenticationCandidatesAsync(
            string displayPrefix,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ApiKeyCreateResult> CreateAsync(
            ApiKeyCreateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            _ = unitOfWorkContext;
            _ = cancellationToken;
            CreateCount++;
            LastWrite = write;
            ApiKeyResource resource = Resource(
                CorruptCreatedOwner ? EntityId.New() : write.UserId,
                write.GroupId,
                write.ApiKeyId,
                write);
            Items = [.. Items, resource];
            return ValueTask.FromResult(new ApiKeyCreateResult(
                ApiKeyCreateDisposition.Created,
                resource));
        }

        internal static ApiKeyResource Resource(
            EntityId userId,
            EntityId groupId,
            EntityId apiKeyId,
            ApiKeyCreateWrite? write = null)
        {
            DateTimeOffset observed = DateTimeOffset.Parse(
                "2026-07-23T03:04:05Z",
                System.Globalization.CultureInfo.InvariantCulture);
            return new ApiKeyResource(
                apiKeyId,
                userId,
                groupId,
                write?.Name ?? "Reader key",
                write?.Prefix ?? "sk-unit-AAAAAAAA",
                ApiKeyPersistentStatus.Active,
                ApiKeyEffectiveStatus.Active,
                write?.ExpiresAt,
                write?.AllowedCidrs ?? [],
                LastUsedAt: null,
                Version: 1,
                observed,
                observed,
                observed);
        }
    }

    private sealed class RecordingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        internal int BeginCount { get; private set; }

        internal int CommitCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            BeginCount++;
            return ValueTask.FromResult<IUnitOfWork>(
                new RecordingUnitOfWork(
                    () => CommitCount++,
                    () => DisposeCount++));
        }
    }

    private sealed class RecordingUnitOfWork(
        Action committed,
        Action disposed) : IUnitOfWork
    {
        public IUnitOfWorkContext Context { get; } = new TestUnitOfWorkContext();

        public ValueTask CommitAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            committed();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            disposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestUnitOfWorkContext : IUnitOfWorkContext;

    private sealed class MemoryIdempotencyStore : ICommandIdempotencyStore
    {
        private Pending? pending;
        private Stored? stored;

        internal CommandIdempotencyResponse? Completed => stored?.Response;

        internal CommandIdempotencyResponse? ForcedReplayResponse { get; set; }

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            _ = unitOfWorkContext;
            _ = cancellationToken;
            if (ForcedReplayResponse is not null)
            {
                return ValueTask.FromResult(
                    CommandIdempotencyAcquireResult.Replay(ForcedReplayResponse));
            }

            if (stored is not null
                && string.Equals(stored.Scope, request.Scope, StringComparison.Ordinal)
                && string.Equals(stored.Key, request.Key, StringComparison.Ordinal))
            {
                bool exact = string.Equals(
                        stored.ActorFingerprint,
                        request.ActorFingerprint,
                        StringComparison.Ordinal)
                    && CryptographicOperations.FixedTimeEquals(
                        stored.RequestHash,
                        request.RequestHash.Span);
                return ValueTask.FromResult(
                    exact
                        ? CommandIdempotencyAcquireResult.Replay(stored.Response)
                        : CommandIdempotencyAcquireResult.Conflict);
            }

            CommandIdempotencyLease lease = new(
                request.Scope,
                request.Key,
                request.Owner,
                Generation: 1,
                Version: 1);
            pending = new Pending(
                request.Scope,
                request.Key,
                request.ActorFingerprint,
                request.RequestHash.ToArray(),
                lease);
            return ValueTask.FromResult(
                CommandIdempotencyAcquireResult.Acquired(lease));
        }

        public ValueTask<bool> HeartbeatAsync(
            CommandIdempotencyHeartbeat heartbeat,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<bool> CompleteAsync(
            CommandIdempotencyCompletion completion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            _ = unitOfWorkContext;
            _ = cancellationToken;
            Assert.NotNull(pending);
            Assert.Equal(pending.Lease, completion.Lease);
            stored = new Stored(
                pending.Scope,
                pending.Key,
                pending.ActorFingerprint,
                pending.RequestHash,
                new CommandIdempotencyResponse(
                    completion.TerminalStatus,
                    completion.ResponseStatus,
                    completion.ResponseBody?.Clone(),
                    completion.ResponseBodyEnvelope?.Clone(),
                    completion.ResponseHeaders.Clone(),
                    completion.ResourceType,
                    completion.ResourceId));
            pending = null;
            return ValueTask.FromResult(true);
        }

        private sealed record Pending(
            string Scope,
            string Key,
            string ActorFingerprint,
            byte[] RequestHash,
            CommandIdempotencyLease Lease);

        private sealed record Stored(
            string Scope,
            string Key,
            string ActorFingerprint,
            byte[] RequestHash,
            CommandIdempotencyResponse Response);
    }

    private sealed class RecordingAuditAppender : IAuditAppender
    {
        internal List<AuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            _ = unitOfWorkContext;
            _ = cancellationToken;
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOperationalEventWriter(
        RecordingUnitOfWorkFactory unitOfWorkFactory) : IOperationalEventWriter
    {
        internal List<(string Name, JsonElement Payload)> Events { get; } = [];

        internal bool WasUnitOfWorkDisposedBeforeWrite { get; private set; }

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasUnitOfWorkDisposedBeforeWrite =
                unitOfWorkFactory.DisposeCount >= unitOfWorkFactory.BeginCount;
            Events.Add((eventName, payload.Clone()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingCredentialService(
        IApiKeyCredentialService inner) : IApiKeyCredentialService
    {
        internal int CreateCount { get; private set; }

        public ApiKeyCredential Create()
        {
            CreateCount++;
            return inner.Create();
        }

        public bool TryGetDisplayPrefix(
            string presentedKey,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? displayPrefix) =>
            inner.TryGetDisplayPrefix(presentedKey, out displayPrefix);

        public bool Verify(
            string presentedKey,
            byte[] expectedHash,
            short pepperVersion) =>
            inner.Verify(presentedKey, expectedHash, pepperVersion);
    }

    private sealed class NullApiKeyResponseEnvelope : IApiKeyCreateResponseEnvelope
    {
        public JsonElement Encrypt(
            ApiKeyCreateResponseSecret response,
            EntityId apiKeyId,
            IdempotencySecretBinding binding) =>
            throw new NotSupportedException();

        public ApiKeyCreateResponseSecret Decrypt(
            JsonElement envelope,
            EntityId apiKeyId,
            IdempotencySecretBinding binding) => new(
            ApiKey: null!,
            Secret: "not-a-credential",
            ETag: "\"v1\"",
            Location: $"/api/v1/me/api-keys/{apiKeyId.Value:D}");
    }

    private static DateTimeOffset TestTimestamp() => DateTimeOffset.Parse(
        "2026-07-23T06:07:08Z",
        System.Globalization.CultureInfo.InvariantCulture);
}
#pragma warning restore MA0048
