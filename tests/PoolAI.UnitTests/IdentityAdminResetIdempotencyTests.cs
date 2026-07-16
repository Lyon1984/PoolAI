using System.Buffers.Binary;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class IdentityAdminResetIdempotencyTests
{
    [Fact]
    public async Task AuthorizationFailuresShortCircuitEveryAdminUseCase()
    {
        ThrowingIdentityRepository repository = new();
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        IdentityUseCaseService service = CreateService(
            repository,
            unitOfWorkFactory,
            new ReplayIdempotencyStore(),
            new RecordingRateLimiter());
        IdentityActor user = new(EntityId.New(), SystemRole.User, 1);
        IdentityActor expiredAdmin = new(EntityId.New(), SystemRole.Admin, 0);
        EntityId target = EntityId.New();

        Result<UserPage> list = await service.ExecuteAsync(
            new ListUsersQuery(user, Cursor: null),
            TestContext.Current.CancellationToken);
        Result<UserView> get = await service.ExecuteAsync(
            new GetUserQuery(expiredAdmin, target),
            TestContext.Current.CancellationToken);
        Result<IdentityCommandOutcome<UserView>> create = await service.ExecuteAsync(
            CreateCommand() with { Actor = user },
            TestContext.Current.CancellationToken);
        Result<IdentityCommandOutcome<UserView>> update = await service.ExecuteAsync(
            UpdateCommand(target) with { Actor = expiredAdmin },
            TestContext.Current.CancellationToken);
        Result<IdentityCommandOutcome> reset = await service.ExecuteAsync(
            new AdminPasswordResetCommand(
                EntityId.New(), user, "reset-key", target, "reason", null, null),
            TestContext.Current.CancellationToken);

        Assert.All(
            new[] { list.Error, get.Error, create.Error, update.Error, reset.Error },
            static error => Assert.Equal(IdentityErrorCodes.RoleRequired, error.Code));
        Assert.Equal(0, unitOfWorkFactory.BeginCalls);
        Assert.Equal(0, repository.ReadCalls);
    }

    [Fact]
    public async Task UserQueriesValidatePaginationAndRoundTripOpaqueCursor()
    {
        DateTimeOffset createdAt = DateTimeOffset.Parse(
            "2026-07-17T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        IdentityUser first = User(
            Guid.Parse("019bd5e8-30e0-7d4c-a7f2-bb1db0634041"),
            createdAt);
        IdentityUser second = User(
            Guid.Parse("019bd5e8-30e0-7d4c-a7f2-bb1db0634042"),
            createdAt.AddTicks(10));
        ThrowingIdentityRepository repository = new();
        repository.ListResults.Enqueue(new UserSlice([first, second], HasMore: true));
        repository.ListResults.Enqueue(new UserSlice([], HasMore: false));
        IdentityUseCaseService service = CreateService(
            repository,
            new RecordingUnitOfWorkFactory(),
            new ReplayIdempotencyStore(),
            new RecordingRateLimiter());
        IdentityActor auditor = new(EntityId.New(), SystemRole.Auditor, 1);

        Result<UserPage> firstPage = await service.ExecuteAsync(
            new ListUsersQuery(auditor, Cursor: null, Limit: 2),
            TestContext.Current.CancellationToken);
        Result<UserPage> secondPage = await service.ExecuteAsync(
            new ListUsersQuery(auditor, firstPage.Value.NextCursor, Limit: 2),
            TestContext.Current.CancellationToken);

        Assert.True(firstPage.IsSuccess);
        Assert.Equal(2, firstPage.Value.Data.Count);
        Assert.True(firstPage.Value.HasMore);
        Assert.NotNull(firstPage.Value.NextCursor);
        Assert.True(secondPage.IsSuccess);
        Assert.Empty(secondPage.Value.Data);
        Assert.False(secondPage.Value.HasMore);
        Assert.Null(secondPage.Value.NextCursor);
        Assert.Equal(second.CreatedAt, repository.LastCursor!.CreatedAt);
        Assert.Equal(second.Id, repository.LastCursor.Id);
    }

    [Fact]
    public async Task UserQueriesRejectInvalidLimitsAndEveryMalformedCursorShape()
    {
        IdentityUseCaseService service = CreateService(
            new ThrowingIdentityRepository(),
            new RecordingUnitOfWorkFactory(),
            new ReplayIdempotencyStore(),
            new RecordingRateLimiter());
        IdentityActor admin = new(EntityId.New(), SystemRole.Admin, 1);
        byte[] emptyIdCursor = new byte[25];
        emptyIdCursor[0] = 0x01;
        byte[] overflowingCursor = new byte[25];
        overflowingCursor[0] = 0x01;
        BinaryPrimitives.WriteInt64BigEndian(overflowingCursor.AsSpan(1, 8), long.MaxValue);
        overflowingCursor[^1] = 0x01;
        string[] malformedCursors =
        [
            string.Empty,
            new string('A', 33) + "=",
            new string('A', 33) + "!",
            new string('A', 34),
            Base64Url(emptyIdCursor),
            Base64Url(overflowingCursor),
        ];

        foreach (int limit in new[] { 0, 101 })
        {
            Result<UserPage> result = await service.ExecuteAsync(
                new ListUsersQuery(admin, Cursor: null, limit),
                TestContext.Current.CancellationToken);
            Assert.Equal(IdentityErrorCodes.InvalidRequest, result.Error.Code);
        }

        foreach (string cursor in malformedCursors)
        {
            Result<UserPage> result = await service.ExecuteAsync(
                new ListUsersQuery(admin, cursor),
                TestContext.Current.CancellationToken);
            Assert.Equal(IdentityErrorCodes.InvalidRequest, result.Error.Code);
        }
    }

    [Fact]
    public async Task GetUserDistinguishesPresentAndMissingResources()
    {
        IdentityActor operatorActor = new(EntityId.New(), SystemRole.Operator, 1);
        IdentityUser user = User(
            EntityId.New().Value,
            DateTimeOffset.Parse(
                "2026-07-17T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture));
        ThrowingIdentityRepository presentRepository = new()
        {
            DirectGetConfigured = true,
            DirectGetResult = user,
        };
        ThrowingIdentityRepository missingRepository = new()
        {
            DirectGetConfigured = true,
            DirectGetResult = null,
        };
        IdentityUseCaseService presentService = CreateService(
            presentRepository,
            new RecordingUnitOfWorkFactory(),
            new ReplayIdempotencyStore(),
            new RecordingRateLimiter());
        IdentityUseCaseService missingService = CreateService(
            missingRepository,
            new RecordingUnitOfWorkFactory(),
            new ReplayIdempotencyStore(),
            new RecordingRateLimiter());

        Result<UserView> present = await presentService.ExecuteAsync(
            new GetUserQuery(operatorActor, user.Id),
            TestContext.Current.CancellationToken);
        Result<UserView> missing = await missingService.ExecuteAsync(
            new GetUserQuery(operatorActor, EntityId.New()),
            TestContext.Current.CancellationToken);

        Assert.Equal(user.Id, present.Value.Id);
        Assert.Equal(IdentityErrorCodes.ResourceNotFound, missing.Error.Code);
    }

    [Theory]
    [InlineData("create-email")]
    [InlineData("create-password")]
    [InlineData("update-version")]
    [InlineData("update-empty")]
    [InlineData("update-reason")]
    [InlineData("admin-reason")]
    public async Task InvalidAdminCommandsFailBeforeOpeningAUnitOfWork(string scenario)
    {
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        IdentityUseCaseService service = CreateService(
            new ThrowingIdentityRepository(),
            unitOfWorkFactory,
            new ReplayIdempotencyStore(),
            new RecordingRateLimiter());
        IdentityActor admin = new(EntityId.New(), SystemRole.Admin, 1);
        EntityId userId = EntityId.New();

        ResultError error = scenario switch
        {
            "create-email" => (await service.ExecuteAsync(
                CreateCommand() with { Email = "not-an-email" },
                TestContext.Current.CancellationToken)).Error,
            "create-password" => (await service.ExecuteAsync(
                CreateCommand() with { TemporaryPassword = "short" },
                TestContext.Current.CancellationToken)).Error,
            "update-version" => (await service.ExecuteAsync(
                UpdateCommand(userId) with { ExpectedVersion = 0 },
                TestContext.Current.CancellationToken)).Error,
            "update-empty" => (await service.ExecuteAsync(
                UpdateCommand(userId) with { DisplayName = null },
                TestContext.Current.CancellationToken)).Error,
            "update-reason" => (await service.ExecuteAsync(
                UpdateCommand(userId) with
                {
                    DisplayName = null,
                    Role = SystemRole.User,
                    Reason = null,
                },
                TestContext.Current.CancellationToken)).Error,
            "admin-reason" => (await service.ExecuteAsync(
                new AdminPasswordResetCommand(
                    EntityId.New(), admin, "reset-key", userId, string.Empty, null, null),
                TestContext.Current.CancellationToken)).Error,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Equal(
            string.Equals(scenario, "create-password", StringComparison.Ordinal)
                ? IdentityErrorCodes.PasswordPolicyFailed
                : IdentityErrorCodes.ValidationFailed,
            error.Code);
        Assert.Equal(0, unitOfWorkFactory.BeginCalls);
    }

    [Theory]
    [InlineData("create-conflict", "resource_conflict", 409)]
    [InlineData("update-missing", "resource_not_found", 404)]
    [InlineData("update-version", "version_conflict", 412)]
    [InlineData("update-last-admin", "resource_conflict", 409)]
    public async Task PersistenceConflictsAreCompletedAsDurableIdempotentFailures(
        string scenario,
        string expectedCode,
        int expectedStatus)
    {
        IdentityUser before = User(
            Guid.Parse("019bd5e8-30e0-7d4c-a7f2-bb1db0634050"),
            DateTimeOffset.Parse(
                "2026-07-17T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture));
        ThrowingIdentityRepository repository = new()
        {
            CreateConfigured = true,
            CreateResult = null,
            UpdateConfigured = true,
            UpdateResult = scenario switch
            {
                "update-missing" => new UpdateUserPersistenceResult(
                    UpdateUserDisposition.NotFound, null, null, Changed: false),
                "update-version" => new UpdateUserPersistenceResult(
                    UpdateUserDisposition.VersionConflict, null, before, Changed: false),
                "update-last-admin" => new UpdateUserPersistenceResult(
                    UpdateUserDisposition.LastActiveAdminConflict, null, before, Changed: false),
                _ => null,
            },
        };
        CompletingIdempotencyStore idempotency = new();
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        IdentityUseCaseService service = CreateService(
            repository,
            unitOfWorkFactory,
            idempotency,
            new RecordingRateLimiter());

        ResultError error = string.Equals(scenario, "create-conflict", StringComparison.Ordinal)
            ? (await service.ExecuteAsync(
                CreateCommand(),
                TestContext.Current.CancellationToken)).Error
            : (await service.ExecuteAsync(
                UpdateCommand(before.Id),
                TestContext.Current.CancellationToken)).Error;

        Assert.Equal(expectedCode, error.Code);
        CommandIdempotencyCompletion completion = Assert.IsType<CommandIdempotencyCompletion>(
            idempotency.Completion);
        Assert.Equal(expectedStatus, completion.ResponseStatus);
        Assert.Equal(CommandIdempotencyTerminalStatus.Failed, completion.TerminalStatus);
        Assert.Equal(1, unitOfWorkFactory.CommitCalls);
        if (string.Equals(scenario, "update-version", StringComparison.Ordinal))
        {
            Assert.Equal("\"v1\"", error.ETag);
        }
    }

    [Theory]
    [InlineData("generic-conflict", "idempotency_conflict")]
    [InlineData("generic-busy", "coordination_unavailable")]
    [InlineData("admin-conflict", "idempotency_conflict")]
    [InlineData("admin-busy", "coordination_unavailable")]
    public async Task IdempotencyAcquisitionFailuresShortCircuitPersistence(
        string scenario,
        string expectedCode)
    {
        CommandIdempotencyAcquireResult acquire = scenario.EndsWith(
            "conflict",
            StringComparison.Ordinal)
            ? CommandIdempotencyAcquireResult.Conflict
            : CommandIdempotencyAcquireResult.Busy;
        CompletingIdempotencyStore idempotency = new(acquire);
        ThrowingIdentityRepository repository = new();
        IdentityUseCaseService service = CreateService(
            repository,
            new RecordingUnitOfWorkFactory(),
            idempotency,
            new RecordingRateLimiter());
        bool adminReset = scenario.StartsWith("admin", StringComparison.Ordinal);

        ResultError error = adminReset
            ? (await service.ExecuteAsync(
                new AdminPasswordResetCommand(
                    EntityId.New(),
                    new IdentityActor(EntityId.New(), SystemRole.Admin, 1),
                    "admin-reset-key",
                    EntityId.New(),
                    "operator reason",
                    null,
                    null),
                TestContext.Current.CancellationToken)).Error
            : (await service.ExecuteAsync(
                CreateCommand(),
                TestContext.Current.CancellationToken)).Error;

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(0, repository.ReadCalls);
    }

    [Fact]
    public async Task FailedIdempotencyReplaysPreserveFrozenSemanticPresentation()
    {
        JsonElement presentation = JsonSerializer.SerializeToElement(
            new ResultErrorPresentation(
                IdentityErrorCodes.ResourceNotFound,
                404,
                "Resource not found",
                "The requested resource was not found.",
                Retryable: false));
        JsonElement body = JsonSerializer.SerializeToElement(new
        {
            Description = "The user does not exist.",
            Presentation = presentation,
        });
        JsonElement headers = JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal));
        CommandIdempotencyResponse response = new(
            CommandIdempotencyTerminalStatus.Failed,
            404,
            body,
            BodyEnvelope: null,
            headers,
            ResourceType: null,
            ResourceId: null);
        IdentityUseCaseService service = CreateService(
            new ThrowingIdentityRepository(),
            new RecordingUnitOfWorkFactory(),
            new ReplayIdempotencyStore(response),
            new RecordingRateLimiter());

        Result<IdentityCommandOutcome<UserView>> create = await service.ExecuteAsync(
            CreateCommand(),
            TestContext.Current.CancellationToken);
        Result<IdentityCommandOutcome> reset = await service.ExecuteAsync(
            new AdminPasswordResetCommand(
                EntityId.New(),
                new IdentityActor(EntityId.New(), SystemRole.Admin, 1),
                "failed-reset-replay",
                EntityId.New(),
                "operator reason",
                null,
                null),
            TestContext.Current.CancellationToken);

        Assert.Equal(IdentityErrorCodes.ResourceNotFound, create.Error.Code);
        Assert.Equal(IdentityErrorCodes.ResourceNotFound, reset.Error.Code);
        Assert.Equal("Resource not found", create.Error.Presentation!.Title);
    }

    [Theory]
    [InlineData("invalid-email", "validation_failed")]
    [InlineData("rate-rejected", "rate_limit_exceeded")]
    [InlineData("rate-unavailable", "coordination_unavailable")]
    public async Task ForgotPasswordRejectsInvalidOrRateLimitedRequestsBeforeDatabaseWork(
        string scenario,
        string expectedCode)
    {
        PasswordResetRateLimitDecision? forgotDecision = scenario switch
        {
            "rate-rejected" => new PasswordResetRateLimitDecision(
                PasswordResetRateLimitDisposition.Rejected, 17),
            "rate-unavailable" => new PasswordResetRateLimitDecision(
                PasswordResetRateLimitDisposition.Unavailable, null),
            _ => null,
        };
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        IdentityUseCaseService service = CreateService(
            new ThrowingIdentityRepository(),
            unitOfWorkFactory,
            new ReplayIdempotencyStore(),
            new RecordingRateLimiter(forgotDecision));

        Result<IdentityCommandOutcome> result = await service.ExecuteAsync(
            new ForgotPasswordCommand(
                EntityId.New(),
                string.Equals(scenario, "invalid-email", StringComparison.Ordinal)
                    ? "not-an-email"
                    : "person@example.test",
                "192.0.2.10",
                "unit-test"),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(0, unitOfWorkFactory.BeginCalls);
    }

    [Theory]
    [InlineData("short-password", "password_policy_failed")]
    [InlineData("invalid-token", "validation_failed")]
    [InlineData("empty-candidates", "password_reset_token_invalid")]
    [InlineData("consume-race", "password_reset_token_invalid")]
    public async Task CompletePasswordResetRejectsEveryNonConsumablePath(
        string scenario,
        string expectedCode)
    {
        ThrowingIdentityRepository repository = new(
            consumablePasswordReset: string.Equals(
                scenario,
                "consume-race",
                StringComparison.Ordinal))
        {
            ConsumeConfigured = true,
            ConsumeResult = null,
        };
        IPasswordResetTokenHasher tokenHasher = scenario switch
        {
            "invalid-token" => new InvalidCandidateTokenHasher(),
            "empty-candidates" => new EmptyCandidateTokenHasher(),
            _ => new CandidateTokenHasher(),
        };
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        IdentityUseCaseService service = CreateService(
            repository,
            unitOfWorkFactory,
            new ReplayIdempotencyStore(),
            new RecordingRateLimiter(),
            new RecordingPasswordHasher(),
            tokenHasher);

        Result<IdentityCommandOutcome> result = await service.ExecuteAsync(
            new CompletePasswordResetCommand(
                EntityId.New(),
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                string.Equals(scenario, "short-password", StringComparison.Ordinal)
                    ? "short"
                    : "correct horse battery staple",
                "192.0.2.10",
                "unit-test"),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(
            string.Equals(scenario, "consume-race", StringComparison.Ordinal) ? 1 : 0,
            unitOfWorkFactory.BeginCalls);
    }

    [Theory]
    [InlineData("rate-limit")]
    [InlineData("missing")]
    [InlineData("activation-race")]
    [InlineData("second-acquire-busy")]
    public async Task AdminResetPreflightCoversEveryEarlyExit(string scenario)
    {
        IdentityUser active = User(
            Guid.Parse("019bd5e8-30e0-7d4c-a7f2-bb1db0634060"),
            DateTimeOffset.Parse(
                "2026-07-17T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture));
        IdentityUseCaseService service = CreateAdminResetEarlyExitService(scenario, active);

        Result<IdentityCommandOutcome> result = await service.ExecuteAsync(
            new AdminPasswordResetCommand(
                EntityId.New(),
                new IdentityActor(EntityId.New(), SystemRole.Admin, 1),
                "admin-reset-preflight",
                active.Id,
                "operator reason",
                null,
                null),
            TestContext.Current.CancellationToken);

        string expected = scenario switch
        {
            "rate-limit" => IdentityErrorCodes.RateLimitExceeded,
            "missing" => IdentityErrorCodes.ResourceNotFound,
            "activation-race" => IdentityErrorCodes.ResourceConflict,
            "second-acquire-busy" => IdentityErrorCodes.CoordinationUnavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Assert.Equal(expected, result.Error.Code);
    }

    private static IdentityUseCaseService CreateAdminResetEarlyExitService(
        string scenario,
        IdentityUser active)
    {
        ThrowingIdentityRepository repository = new()
        {
            DirectGetConfigured = true,
            DirectGetResult = string.Equals(
                scenario,
                "rate-limit",
                StringComparison.Ordinal) ? active : null,
            TransactionalGetConfigured = true,
            TransactionalGetResult = string.Equals(
                scenario,
                "activation-race",
                StringComparison.Ordinal) ? active : null,
        };
        PasswordResetRateLimitDecision adminDecision = string.Equals(
            scenario,
            "rate-limit",
            StringComparison.Ordinal)
            ? new PasswordResetRateLimitDecision(
                PasswordResetRateLimitDisposition.Rejected,
                RetryAfterSeconds: 11)
            : new PasswordResetRateLimitDecision(
                PasswordResetRateLimitDisposition.Allowed,
                RetryAfterSeconds: null);
        CompletingIdempotencyStore idempotency = string.Equals(
            scenario,
            "second-acquire-busy",
            StringComparison.Ordinal)
            ? new CompletingIdempotencyStore(
                CommandIdempotencyAcquireResult.Acquired(new CommandIdempotencyLease(
                    "preflight",
                    "key",
                    EntityId.New(),
                    1,
                    1)),
                CommandIdempotencyAcquireResult.Busy)
            : new CompletingIdempotencyStore();
        return CreateService(
            repository,
            new RecordingUnitOfWorkFactory(),
            idempotency,
            new RecordingRateLimiter(adminDecision: adminDecision));
    }

    [Fact]
    public async Task CreateReplayRejectsMissingBodyAndMismatchedFrozenFailure()
    {
        JsonElement headers = JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal));
        CommandIdempotencyResponse missingBody = new(
            CommandIdempotencyTerminalStatus.Completed,
            201,
            Body: null,
            BodyEnvelope: null,
            headers,
            "user",
            EntityId.New());
        IdentityUseCaseService missingBodyService = CreateService(
            new ThrowingIdentityRepository(),
            new RecordingUnitOfWorkFactory(),
            new ReplayIdempotencyStore(missingBody),
            new RecordingRateLimiter());
        await Assert.ThrowsAsync<InvalidOperationException>(() => missingBodyService.ExecuteAsync(
            CreateCommand(),
            TestContext.Current.CancellationToken).AsTask());

        JsonElement body = JsonSerializer.SerializeToElement(new
        {
            Description = "mismatch",
            Presentation = new ResultErrorPresentation(
                IdentityErrorCodes.ResourceConflict,
                404,
                "Wrong",
                "Wrong",
                Retryable: false),
        });
        CommandIdempotencyResponse mismatch = missingBody with
        {
            TerminalStatus = CommandIdempotencyTerminalStatus.Failed,
            Status = 404,
            Body = body,
            ResourceType = null,
            ResourceId = null,
        };
        IdentityUseCaseService mismatchService = CreateService(
            new ThrowingIdentityRepository(),
            new RecordingUnitOfWorkFactory(),
            new ReplayIdempotencyStore(mismatch),
            new RecordingRateLimiter());
        await Assert.ThrowsAsync<InvalidOperationException>(() => mismatchService.ExecuteAsync(
            CreateCommand(),
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task LostIdempotencyLeaseRejectsDurableFailureCompletion()
    {
        ThrowingIdentityRepository repository = new()
        {
            CreateConfigured = true,
            CreateResult = null,
        };
        CompletingIdempotencyStore idempotency = new()
        {
            CompleteResult = false,
        };
        IdentityUseCaseService service = CreateService(
            repository,
            new RecordingUnitOfWorkFactory(),
            idempotency,
            new RecordingRateLimiter());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(
            CreateCommand(),
            TestContext.Current.CancellationToken).AsTask());
    }

    private static IdentityUser User(Guid id, DateTimeOffset createdAt) => new(
        new EntityId(id),
        "person@example.test",
        "person@example.test",
        "Person",
        "poolai-password-v1:test",
        SystemRole.User,
        UserLifecycle.Active,
        TokenVersion: 1,
        Version: 1,
        createdAt,
        createdAt);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    [Fact]
    public async Task NonConsumableAnonymousResetSkipsPasswordHashAndUnitOfWork()
    {
        ThrowingIdentityRepository repository = new(consumablePasswordReset: false);
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        ReplayIdempotencyStore idempotency = new();
        RecordingPasswordHasher passwordHasher = new();
        IdentityUseCaseService service = CreateService(
            repository,
            unitOfWorkFactory,
            idempotency,
            new RecordingRateLimiter(),
            passwordHasher,
            new CandidateTokenHasher());

        Result<IdentityCommandOutcome> result = await service.ExecuteAsync(
            new CompletePasswordResetCommand(
                EntityId.New(),
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                "correct horse battery staple",
                "192.0.2.10",
                "unit-test"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.PasswordResetTokenInvalid, result.Error.Code);
        Assert.Equal(0, passwordHasher.HashCalls);
        Assert.Equal(0, unitOfWorkFactory.BeginCalls);
        Assert.Equal(0, idempotency.AcquireCalls);
        Assert.Equal(1, repository.ReadCalls);
    }

    [Fact]
    public async Task TerminalReplayBypassesTargetLookupAndRedisCounter()
    {
        EntityId targetUserId = EntityId.New();
        JsonElement emptyHeaders = JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal));
        ReplayIdempotencyStore idempotency = new(new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Completed,
            202,
            Body: null,
            BodyEnvelope: null,
            emptyHeaders,
            "user",
            targetUserId));
        RecordingRateLimiter limiter = new();
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        ThrowingIdentityRepository repository = new();
        IdentityUseCaseService service = CreateService(
            repository,
            unitOfWorkFactory,
            idempotency,
            limiter);

        Result<IdentityCommandOutcome> result = await service.ExecuteAsync(
            new AdminPasswordResetCommand(
                EntityId.New(),
                new IdentityActor(EntityId.New(), SystemRole.Admin, 1),
                "admin-reset-replay-key",
                targetUserId,
                "operator requested reset",
                "192.0.2.10",
                "unit-test"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsReplay);
        Assert.Equal(202, result.Value.StatusCode);
        Assert.Equal(1, idempotency.AcquireCalls);
        Assert.Equal(1, unitOfWorkFactory.BeginCalls);
        Assert.Equal(1, unitOfWorkFactory.DisposeCalls);
        Assert.Equal(0, unitOfWorkFactory.CommitCalls);
        Assert.Equal(0, repository.ReadCalls);
        Assert.Equal(0, limiter.AdminCalls);
    }

    [Fact]
    public async Task AdminResetReplayRejectsUnexpectedCompletedResponseShape()
    {
        EntityId targetUserId = EntityId.New();
        JsonElement emptyHeaders = JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal));
        ReplayIdempotencyStore idempotency = new(new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Completed,
            204,
            Body: null,
            BodyEnvelope: null,
            emptyHeaders,
            "user",
            targetUserId));
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        ThrowingIdentityRepository repository = new();
        IdentityUseCaseService service = CreateService(
            repository,
            unitOfWorkFactory,
            idempotency,
            new RecordingRateLimiter());
        AdminPasswordResetCommand command = new(
            EntityId.New(),
            new IdentityActor(EntityId.New(), SystemRole.Admin, 1),
            "admin-reset-invalid-replay-key",
            targetUserId,
            "operator requested reset",
            "192.0.2.10",
            "unit-test");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(0, repository.ReadCalls);
        Assert.Equal(1, idempotency.AcquireCalls);
    }

    [Fact]
    public async Task CreateReplayRehydratesEntityIdAndValidatesFrozenHeaders()
    {
        EntityId userId = EntityId.New();
        DateTimeOffset createdAt = DateTimeOffset.Parse(
            "2026-07-16T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        JsonElement body = JsonSerializer.SerializeToElement(new
        {
            Id = userId.Value,
            Email = "person@example.test",
            DisplayName = "Replay user",
            Role = SystemRole.User,
            Status = UserLifecycle.Active,
            Version = 2L,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        JsonElement headers = JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = "\"v2\"",
                ["Location"] = $"/api/v1/admin/users/{userId.Value:D}",
            });
        ReplayIdempotencyStore idempotency = new(new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Completed,
            201,
            body,
            BodyEnvelope: null,
            headers,
            "user",
            userId));
        RecordingRateLimiter limiter = new();
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        ThrowingIdentityRepository repository = new();
        IdentityUseCaseService service = CreateService(
            repository,
            unitOfWorkFactory,
            idempotency,
            limiter);

        Result<IdentityCommandOutcome<UserView>> result = await service.ExecuteAsync(
            new CreateUserCommand(
                EntityId.New(),
                new IdentityActor(EntityId.New(), SystemRole.Admin, 1),
                "create-replay-key",
                "person@example.test",
                "Replay user",
                SystemRole.User,
                "correct horse battery staple",
                "192.0.2.10",
                "unit-test"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsReplay);
        Assert.Equal(userId, result.Value.Value.Id);
        Assert.Equal("\"v2\"", result.Value.ETag);
        Assert.Equal($"/api/v1/admin/users/{userId.Value:D}", result.Value.Location);
        Assert.Equal(0, repository.ReadCalls);
    }

    [Fact]
    public async Task UpdateReplayAcceptsCompletedUserShapeWithOnlyETag()
    {
        EntityId userId = EntityId.New();
        CommandIdempotencyResponse response = UserReplayResponse(userId, 200);
        ThrowingIdentityRepository repository = new();
        IdentityUseCaseService service = CreateService(
            repository,
            new RecordingUnitOfWorkFactory(),
            new ReplayIdempotencyStore(response),
            new RecordingRateLimiter());

        Result<IdentityCommandOutcome<UserView>> result = await service.ExecuteAsync(
            UpdateCommand(userId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsReplay);
        Assert.Equal(200, result.Value.StatusCode);
        Assert.Equal(userId, result.Value.Value.Id);
        Assert.Equal("\"v2\"", result.Value.ETag);
        Assert.Null(result.Value.Location);
        Assert.Equal(0, repository.ReadCalls);
    }

    [Theory]
    [InlineData("terminal-status")]
    [InlineData("body-envelope")]
    [InlineData("resource-type")]
    [InlineData("resource-id")]
    [InlineData("headers-kind")]
    [InlineData("missing-etag")]
    [InlineData("extra-header")]
    [InlineData("missing-location")]
    [InlineData("status")]
    public async Task UserReplayRejectsMalformedPersistedSuccessShape(string corruption)
    {
        EntityId userId = EntityId.New();
        CommandIdempotencyResponse valid = UserReplayResponse(userId, 201);
        CommandIdempotencyResponse response = corruption switch
        {
            "terminal-status" => valid with
            {
                TerminalStatus = (CommandIdempotencyTerminalStatus)(-1),
            },
            "body-envelope" => valid with
            {
                BodyEnvelope = JsonSerializer.SerializeToElement(new { ciphertext = "unexpected" }),
            },
            "resource-type" => valid with { ResourceType = "group" },
            "resource-id" => valid with { ResourceId = EntityId.New() },
            "headers-kind" => valid with
            {
                Headers = JsonSerializer.SerializeToElement(Array.Empty<string>()),
            },
            "missing-etag" => valid with
            {
                Headers = Headers(("Location", UserLocation(userId))),
            },
            "extra-header" => valid with
            {
                Headers = Headers(
                    ("ETag", "\"v2\""),
                    ("Location", UserLocation(userId)),
                    ("X-Request-Id", EntityId.New().Value.ToString("D"))),
            },
            "missing-location" => valid with { Headers = Headers(("ETag", "\"v2\"")) },
            "status" => valid with { Status = 202 },
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
        IdentityUseCaseService service = CreateService(
            new ThrowingIdentityRepository(),
            new RecordingUnitOfWorkFactory(),
            new ReplayIdempotencyStore(response),
            new RecordingRateLimiter());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecuteAsync(
                CreateCommand(),
                TestContext.Current.CancellationToken).AsTask());
    }

    private static CommandIdempotencyResponse UserReplayResponse(EntityId userId, int status)
    {
        DateTimeOffset timestamp = DateTimeOffset.Parse(
            "2026-07-16T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        JsonElement body = JsonSerializer.SerializeToElement(new
        {
            Id = userId.Value,
            Email = "person@example.test",
            DisplayName = "Replay user",
            Role = SystemRole.User,
            Status = UserLifecycle.Active,
            Version = 2L,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        });
        JsonElement headers = status == 201
            ? Headers(("ETag", "\"v2\""), ("Location", UserLocation(userId)))
            : Headers(("ETag", "\"v2\""));
        return new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Completed,
            status,
            body,
            BodyEnvelope: null,
            headers,
            "user",
            userId);
    }

    private static JsonElement Headers(params (string Name, string Value)[] headers) =>
        JsonSerializer.SerializeToElement(headers.ToDictionary(
            static header => header.Name,
            static header => header.Value,
            StringComparer.Ordinal));

    private static string UserLocation(EntityId userId) =>
        $"/api/v1/admin/users/{userId.Value:D}";

    private static CreateUserCommand CreateCommand() => new(
        EntityId.New(),
        new IdentityActor(EntityId.New(), SystemRole.Admin, 1),
        "create-replay-key",
        "person@example.test",
        "Replay user",
        SystemRole.User,
        "correct horse battery staple",
        "192.0.2.10",
        "unit-test");

    private static UpdateUserCommand UpdateCommand(EntityId userId) => new(
        EntityId.New(),
        new IdentityActor(EntityId.New(), SystemRole.Admin, 1),
        "update-replay-key",
        userId,
        1,
        "Replay user",
        Role: null,
        Status: null,
        Reason: null,
        "192.0.2.10",
        "unit-test");

    private static IdentityUseCaseService CreateService(
        IIdentityRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        ICommandIdempotencyStore idempotency,
        IPasswordResetRateLimiter limiter,
        IVersionedPasswordHasher? passwordHasher = null,
        IPasswordResetTokenHasher? tokenHasher = null) => new(
            repository,
            unitOfWorkFactory,
            idempotency,
            new ThrowingAuditAppender(),
            new ThrowingOutboxAppender(),
            passwordHasher ?? new ThrowingPasswordHasher(),
            tokenHasher ?? new ThrowingTokenHasher(),
            new ThrowingEmailEnvelope(),
            limiter,
            new IdentityPolicy(
                new Uri("https://poolai.example.test/", UriKind.Absolute),
                12,
                TimeSpan.FromMinutes(30),
                "example.test",
                new byte[32]),
            TimeProvider.System);

    private sealed class RecordingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

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

            public ValueTask DisposeAsync()
            {
                owner.DisposeCalls++;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class UnitOfWorkContext : IUnitOfWorkContext;
    }

    private sealed class ReplayIdempotencyStore : ICommandIdempotencyStore
    {
        private readonly CommandIdempotencyResponse? _response;

        internal ReplayIdempotencyStore(CommandIdempotencyResponse? response = null)
        {
            _response = response;
        }

        internal int AcquireCalls { get; private set; }

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(unitOfWorkContext);
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCalls++;
            JsonElement emptyHeaders = JsonSerializer.SerializeToElement(
                new Dictionary<string, string>(StringComparer.Ordinal));
            return ValueTask.FromResult(CommandIdempotencyAcquireResult.Replay(
                _response ?? new CommandIdempotencyResponse(
                    CommandIdempotencyTerminalStatus.Completed,
                    202,
                    Body: null,
                    BodyEnvelope: null,
                    emptyHeaders,
                    "user",
                    EntityId.New())));
        }

        public ValueTask<bool> HeartbeatAsync(
            CommandIdempotencyHeartbeat heartbeat,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                "Replay must not heartbeat the idempotency record.");

        public ValueTask<bool> CompleteAsync(
            CommandIdempotencyCompletion completion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                "Replay must not complete the idempotency record again.");
    }

    private sealed class RecordingRateLimiter : IPasswordResetRateLimiter
    {
        private readonly PasswordResetRateLimitDecision? _forgotDecision;
        private readonly PasswordResetRateLimitDecision _adminDecision;

        internal RecordingRateLimiter(
            PasswordResetRateLimitDecision? forgotDecision = null,
            PasswordResetRateLimitDecision? adminDecision = null)
        {
            _forgotDecision = forgotDecision;
            _adminDecision = adminDecision ?? new PasswordResetRateLimitDecision(
                PasswordResetRateLimitDisposition.Allowed,
                RetryAfterSeconds: null);
        }

        internal int AdminCalls { get; private set; }

        public ValueTask<PasswordResetRateLimitDecision> CheckForgotAsync(
            string ipAddress,
            string normalizedAccount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _forgotDecision is null
                ? throw new InvalidOperationException(
                    "Admin reset must not use the anonymous limiter path.")
                : ValueTask.FromResult(_forgotDecision);
        }

        public ValueTask<PasswordResetRateLimitDecision> CheckAdminAsync(
            string normalizedAccount,
            CancellationToken cancellationToken)
        {
            AdminCalls++;
            return ValueTask.FromResult(_adminDecision);
        }
    }

    private sealed class ThrowingIdentityRepository : IIdentityRepository
    {
        private readonly bool? _consumablePasswordReset;

        internal ThrowingIdentityRepository(bool? consumablePasswordReset = null)
        {
            _consumablePasswordReset = consumablePasswordReset;
        }

        internal int ReadCalls { get; private set; }

        internal Queue<UserSlice> ListResults { get; } = [];

        internal UserCursor? LastCursor { get; private set; }

        internal bool DirectGetConfigured { get; init; }

        internal IdentityUser? DirectGetResult { get; init; }

        internal bool TransactionalGetConfigured { get; init; }

        internal IdentityUser? TransactionalGetResult { get; init; }

        internal bool CreateConfigured { get; init; }

        internal IdentityUser? CreateResult { get; init; }

        internal bool UpdateConfigured { get; init; }

        internal UpdateUserPersistenceResult? UpdateResult { get; init; }

        internal bool ConsumeConfigured { get; init; }

        internal PasswordResetConsumeResult? ConsumeResult { get; init; }

        public ValueTask<UserSlice> ListAsync(
            UserCursor? cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ListResults.Count == 0)
            {
                throw Unexpected();
            }

            LastCursor = cursor;
            return ValueTask.FromResult(ListResults.Dequeue());
        }

        public ValueTask<IdentityUser?> GetAsync(
            EntityId userId,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            if (DirectGetConfigured)
            {
                return ValueTask.FromResult(DirectGetResult);
            }

            throw Unexpected();
        }

        public ValueTask<IdentityUser?> GetAsync(
            EntityId userId,
            IUnitOfWorkContext unitOfWorkContext,
            bool forUpdate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TransactionalGetConfigured
                ? ValueTask.FromResult(TransactionalGetResult)
                : throw Unexpected();
        }

        public ValueTask<IdentityUser?> FindByNormalizedEmailAsync(
            string normalizedEmail,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<IdentityUser?> CreateAsync(
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
            cancellationToken.ThrowIfCancellationRequested();
            return CreateConfigured
                ? ValueTask.FromResult(CreateResult)
                : throw Unexpected();
        }

        public ValueTask<UpdateUserPersistenceResult> UpdateAsync(
            EntityId userId,
            long expectedVersion,
            string? displayName,
            SystemRole? role,
            UserLifecycle? status,
            EntityId assignedBy,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return UpdateConfigured
                ? ValueTask.FromResult(UpdateResult!)
                : throw Unexpected();
        }

        public ValueTask InsertPasswordResetAsync(
            PasswordResetOutboxWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<bool> HasConsumablePasswordResetAsync(
            IReadOnlyList<PasswordResetTokenCandidate> candidates,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            return _consumablePasswordReset is bool consumable
                ? ValueTask.FromResult(consumable)
                : throw Unexpected();
        }

        public ValueTask<PasswordResetConsumeResult?> ConsumePasswordResetAsync(
            IReadOnlyList<PasswordResetTokenCandidate> candidates,
            string passwordHash,
            EntityId securityStamp,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ConsumeConfigured
                ? ValueTask.FromResult(ConsumeResult)
                : throw Unexpected();
        }

        private static InvalidOperationException Unexpected() => new(
            "A terminal idempotency replay must bypass the Identity repository.");
    }

    private sealed class ThrowingPasswordHasher : IVersionedPasswordHasher
    {
        public string Hash(string password) => "poolai-password-v1:replay-test";

        public bool Verify(string encodedHash, string password) => throw Unexpected();
    }

    private sealed class RecordingPasswordHasher : IVersionedPasswordHasher
    {
        internal int HashCalls { get; private set; }

        public string Hash(string password)
        {
            HashCalls++;
            return "poolai-password-v1:unexpected";
        }

        public bool Verify(string encodedHash, string password) => throw Unexpected();
    }

    private sealed class CandidateTokenHasher : IPasswordResetTokenHasher
    {
        public PasswordResetTokenSecret Create() => throw Unexpected();

        public IReadOnlyList<PasswordResetTokenCandidate> HashCandidates(string token) =>
            [new PasswordResetTokenCandidate(new byte[32], 1)];
    }

    private sealed class EmptyCandidateTokenHasher : IPasswordResetTokenHasher
    {
        public PasswordResetTokenSecret Create() => throw Unexpected();

        public IReadOnlyList<PasswordResetTokenCandidate> HashCandidates(string token) => [];
    }

    private sealed class InvalidCandidateTokenHasher : IPasswordResetTokenHasher
    {
        public PasswordResetTokenSecret Create() => throw Unexpected();

        public IReadOnlyList<PasswordResetTokenCandidate> HashCandidates(string token) =>
            throw new ArgumentException("The token is invalid.", nameof(token));
    }

    private sealed class CompletingIdempotencyStore(
        params CommandIdempotencyAcquireResult[] acquire) : ICommandIdempotencyStore
    {
        private readonly Queue<CommandIdempotencyAcquireResult> _acquire = new(acquire);

        internal CommandIdempotencyCompletion? Completion { get; private set; }

        internal bool CompleteResult { get; init; } = true;

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_acquire.Count == 0
                ? CommandIdempotencyAcquireResult.Acquired(
                    new CommandIdempotencyLease(
                        request.Scope,
                        request.Key,
                        EntityId.New(),
                        Generation: 1,
                        Version: 1))
                : _acquire.Dequeue());
        }

        public ValueTask<bool> HeartbeatAsync(
            CommandIdempotencyHeartbeat heartbeat,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<bool> CompleteAsync(
            CommandIdempotencyCompletion completion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completion = completion;
            return ValueTask.FromResult(CompleteResult);
        }
    }

    private sealed class ThrowingTokenHasher : IPasswordResetTokenHasher
    {
        public PasswordResetTokenSecret Create() => throw Unexpected();

        public IReadOnlyList<PasswordResetTokenCandidate> HashCandidates(string token) =>
            throw Unexpected();
    }

    private sealed class ThrowingEmailEnvelope : IEmailSecretEnvelope
    {
        public PasswordResetEmailEnvelopes Encrypt(
            EmailSecretEnvelopePlaintext plaintext,
            EntityId emailOutboxId) => throw Unexpected();

        public EmailSecretEnvelopePlaintext Decrypt(
            JsonElement recipientEnvelope,
            JsonElement deliverySecretEnvelope,
            EntityId emailOutboxId) => throw Unexpected();
    }

    private sealed class ThrowingAuditAppender : IAuditAppender
    {
        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();
    }

    private sealed class ThrowingOutboxAppender : IOutboxAppender
    {
        public ValueTask AppendAsync(
            IntegrationEvent integrationEvent,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();
    }

    private static InvalidOperationException Unexpected() => new(
        "A terminal idempotency replay must bypass command side effects.");
}
