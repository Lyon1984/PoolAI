#pragma warning disable MA0051 // Focused tests keep Account protocol defenses visible.
#pragma warning disable MA0048 // This partial test class shares the production-type-aligned file name.
using System.Buffers.Binary;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.UnitTests;

public sealed partial class AccountControlPlaneServiceTests
{
    [Fact]
    public void InputAndPolicyBoundariesFailClosed()
    {
        Assert.Throws<ArgumentException>(() => AccountInput.Name("\u0001"));
        Assert.Throws<ArgumentException>(() => AccountInput.Credential("too-short"));
        Assert.Throws<ArgumentException>(() => AccountInput.Reason("line one\nline two"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountInput.MaxConcurrency(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountInput.Priority(100_001));
        Assert.Throws<ArgumentOutOfRangeException>(() => AccountInput.Weight(0));
        Assert.Throws<ArgumentException>(() =>
            new AccountControlPlanePolicy(new byte[31]));

        byte[] pepper = Enumerable.Range(1, 32)
            .Select(static value => (byte)value)
            .ToArray();
        AccountControlPlanePolicy policy = new(pepper);
        pepper[0] = 0;
        Assert.Equal((byte)1, policy.RequestHashPepper[0]);
    }

    [Fact]
    public async Task EveryOperationAuthorizesBeforeWorkAndCommandsRejectInvalidInput()
    {
        EntityId accountId = EntityId.New();
        TestEnvironment environment = new();

        AssertFailure(
            await environment.Service.ExecuteAsync(
                new GetAccountQuery(
                    Admin with { Role = AccountControlRole.User },
                    accountId),
                TestContext.Current.CancellationToken),
            AccountErrorCodes.RoleRequired);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                CreateCommand() with
                {
                    Actor = Admin with { TokenVersion = 0 },
                },
                TestContext.Current.CancellationToken),
            AccountErrorCodes.RoleRequired);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                UpdateCommand(accountId) with
                {
                    Actor = Admin with { Role = AccountControlRole.Auditor },
                },
                TestContext.Current.CancellationToken),
            AccountErrorCodes.RoleRequired);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                RetireCommand(accountId) with
                {
                    Actor = Admin with { Role = AccountControlRole.User },
                },
                TestContext.Current.CancellationToken),
            AccountErrorCodes.RoleRequired);

        AssertFailure(
            await environment.Service.ExecuteAsync(
                CreateCommand() with { Name = "\u0001" },
                TestContext.Current.CancellationToken),
            AccountErrorCodes.ValidationFailed);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                CreateCommand() with { Provider = (UpstreamProvider)999 },
                TestContext.Current.CancellationToken),
            AccountErrorCodes.ValidationFailed);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                UpdateCommand(accountId) with
                {
                    CredentialSpecified = false,
                    Credential = null,
                    StatusSpecified = true,
                    Status = (AccountLifecycle)999,
                    Reason = "invalid lifecycle",
                },
                TestContext.Current.CancellationToken),
            AccountErrorCodes.ValidationFailed);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                RetireCommand(accountId) with { ExpectedVersion = 0 },
                TestContext.Current.CancellationToken),
            AccountErrorCodes.ValidationFailed);

        Assert.Equal(0, environment.UnitOfWork.BeginCalls);
        Assert.Equal(0, environment.Repository.CreateCalls);
        Assert.Equal(0, environment.Repository.UpdateCalls);
        Assert.Equal(0, environment.Repository.RetireCalls);
    }

    [Fact]
    public async Task AcquireFailuresAndUnknownDispositionsStopBeforeRepositoryMutation()
    {
        foreach ((CommandIdempotencyAcquireResult acquire, string code, long? retryAfter)
                 in new[]
                 {
                     (
                         CommandIdempotencyAcquireResult.Conflict,
                         AccountErrorCodes.IdempotencyConflict,
                         (long?)null),
                     (
                         CommandIdempotencyAcquireResult.Busy,
                         AccountErrorCodes.CoordinationUnavailable,
                         (long?)1),
                 })
        {
            TestEnvironment create = new();
            create.Idempotency.AcquireResult = acquire;
            Result<AccountCommandOutcome<AccountView>> createResult =
                await create.Service.ExecuteAsync(
                    CreateCommand(),
                    TestContext.Current.CancellationToken);
            AssertFailure(createResult, code);
            Assert.Equal(retryAfter, createResult.Error.RetryAfterSeconds);
            Assert.Equal(0, create.Repository.CreateCalls);
            Assert.Equal(0, create.UnitOfWork.CommitCalls);

            TestEnvironment retire = new();
            retire.Idempotency.AcquireResult = acquire;
            Result<AccountCommandOutcome> retireResult =
                await retire.Service.ExecuteAsync(
                    RetireCommand(EntityId.New()),
                    TestContext.Current.CancellationToken);
            AssertFailure(retireResult, code);
            Assert.Equal(retryAfter, retireResult.Error.RetryAfterSeconds);
            Assert.Equal(0, retire.Repository.RetireCalls);
            Assert.Equal(0, retire.UnitOfWork.CommitCalls);
        }

        TestEnvironment update = new();
        update.Idempotency.AcquireResult = CommandIdempotencyAcquireResult.Conflict;
        AssertFailure(
            await update.Service.ExecuteAsync(
                UpdateCommand(EntityId.New()),
                TestContext.Current.CancellationToken),
            AccountErrorCodes.IdempotencyConflict);
        Assert.Equal(0, update.Repository.UpdateCalls);

        CommandIdempotencyAcquireResult unknown = new(
            (CommandIdempotencyDisposition)999,
            Lease: null,
            Response: null);
        TestEnvironment invalidCreate = new();
        invalidCreate.Idempotency.AcquireResult = unknown;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => invalidCreate.Service
            .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);

        TestEnvironment invalidRetire = new();
        invalidRetire.Idempotency.AcquireResult = unknown;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => invalidRetire.Service
            .ExecuteAsync(
                RetireCommand(EntityId.New()),
                TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);
    }

    [Fact]
    public async Task MutationFailuresAreDurableAndReplayWithCanonicalPresentation()
    {
        (AccountMutationDisposition Disposition, string Code, int Status, string? ETag)[] cases =
        [
            (
                AccountMutationDisposition.ValidationFailed,
                AccountErrorCodes.ValidationFailed,
                422,
                null),
            (
                AccountMutationDisposition.Conflict,
                AccountErrorCodes.ResourceConflict,
                409,
                null),
            (
                AccountMutationDisposition.NotFound,
                AccountErrorCodes.ResourceNotFound,
                404,
                null),
            (
                AccountMutationDisposition.VersionConflict,
                AccountErrorCodes.VersionConflict,
                412,
                "\"v7\""),
            (
                AccountMutationDisposition.LifecycleConflict,
                AccountErrorCodes.ResourceConflict,
                409,
                null),
            (
                AccountMutationDisposition.AccountInUse,
                AccountErrorCodes.AccountInUse,
                409,
                null),
        ];
        CommandIdempotencyResponse? accountInUseReplay = null;

        foreach ((AccountMutationDisposition disposition, string code, int status, string? etag)
                 in cases)
        {
            TestEnvironment environment = new();
            environment.Repository.CreateFactory = _ => new AccountMutationResult(
                disposition,
                WasChanged: false,
                Value: null,
                Before: null,
                CurrentVersion: 7);
            CreateAccountCommand command = CreateCommand();

            Result<AccountCommandOutcome<AccountView>> result =
                await environment.Service.ExecuteAsync(
                    command,
                    TestContext.Current.CancellationToken);

            AssertFailure(result, code);
            Assert.Equal(etag, result.Error.ETag);
            ResultErrorPresentation presentation =
                Assert.IsType<ResultErrorPresentation>(result.Error.Presentation);
            Assert.Equal(status, presentation.Status);
            Assert.Equal(code, presentation.Code);
            CommandIdempotencyCompletion completion =
                Assert.Single(environment.Idempotency.Completions);
            Assert.Equal(CommandIdempotencyTerminalStatus.Failed, completion.TerminalStatus);
            Assert.Equal(status, completion.ResponseStatus);
            Assert.Equal(etag, CompletionHeader(completion, "ETag"));
            Assert.Null(completion.ResourceType);
            Assert.Null(completion.ResourceId);
            Assert.Equal(1, environment.UnitOfWork.CommitCalls);
            Assert.Empty(environment.Audit.Entries);
            Assert.Empty(environment.Outbox.Events);

            CommandIdempotencyResponse response = ResponseFrom(completion);
            TestEnvironment replay = new();
            replay.Idempotency.AcquireResult =
                CommandIdempotencyAcquireResult.Replay(response);
            Result<AccountCommandOutcome<AccountView>> replayResult =
                await replay.Service.ExecuteAsync(
                    command,
                    TestContext.Current.CancellationToken);
            AssertFailure(replayResult, code);
            Assert.Equal(etag, replayResult.Error.ETag);
            Assert.Equal(status, replayResult.Error.Presentation!.Status);
            Assert.Equal(0, replay.Repository.CreateCalls);
            Assert.Equal(0, replay.UnitOfWork.CommitCalls);

            if (disposition == AccountMutationDisposition.AccountInUse)
            {
                accountInUseReplay = response;
            }
        }

        TestEnvironment update = new();
        update.Repository.UpdateResults.Enqueue(new AccountMutationResult(
            AccountMutationDisposition.NotFound,
            WasChanged: false,
            Value: null,
            Before: null));
        AssertFailure(
            await update.Service.ExecuteAsync(
                UpdateCommand(EntityId.New()),
                TestContext.Current.CancellationToken),
            AccountErrorCodes.ResourceNotFound);
        Assert.Equal(1, update.Repository.UpdateCalls);
        Assert.Equal(1, update.UnitOfWork.CommitCalls);

        TestEnvironment retireReplay = new();
        retireReplay.Idempotency.AcquireResult =
            CommandIdempotencyAcquireResult.Replay(
                accountInUseReplay
                ?? throw new InvalidOperationException("The replay fixture was not captured."));
        AssertFailure(
            await retireReplay.Service.ExecuteAsync(
                RetireCommand(EntityId.New()),
                TestContext.Current.CancellationToken),
            AccountErrorCodes.AccountInUse);
        Assert.Equal(0, retireReplay.Repository.RetireCalls);

        TestEnvironment unknown = new();
        unknown.Repository.CreateFactory = _ => new AccountMutationResult(
            (AccountMutationDisposition)999,
            WasChanged: false,
            Value: null,
            Before: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unknown.Service
            .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);
    }

    [Fact]
    public async Task MaterialUpdateRecordsEveryChangedFieldAndEnumRepresentation()
    {
        EntityId accountId = EntityId.New();
        AccountResource before = Account(
            accountId,
            UpstreamProvider.OpenAi,
            "Before",
            "https://before.example/v1",
            "sha256:111111111111");
        AccountResource after = before with
        {
            Name = "After",
            UpstreamBaseUrl = "https://after.example/v2",
            CredentialPrefix = AccountInput.CredentialPrefix(Credential),
            Status = AccountResourceStatus.Active,
            Health = AccountHealth.Healthy,
            LastHealthAt = Now.AddSeconds(30),
            MaxConcurrency = 8,
            Priority = 4,
            Weight = 200,
            Version = 2,
            UpdatedAt = Now.AddMinutes(1),
        };
        TestEnvironment environment = new();
        environment.Repository.UpdateResults.Enqueue(new AccountMutationResult(
            AccountMutationDisposition.Written,
            WasChanged: true,
            after,
            before,
            CurrentVersion: 2));
        UpdateAccountCommand command = UpdateCommand(accountId) with
        {
            BaseUrlSpecified = true,
            BaseUrl = after.UpstreamBaseUrl,
            StatusSpecified = true,
            Status = AccountLifecycle.Active,
            MaxConcurrencySpecified = true,
            MaxConcurrency = after.MaxConcurrency,
            PrioritySpecified = true,
            Priority = after.Priority,
            WeightSpecified = true,
            Weight = after.Weight,
            Reason = "full account refresh",
        };

        Result<AccountCommandOutcome<AccountView>> result =
            await environment.Service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(AccountLifecycle.Active, result.Value.Value.Status);
        Assert.Equal(AccountHealth.Healthy, result.Value.Value.Health.Status);
        AccountUpdateWrite write =
            Assert.IsType<AccountUpdateWrite>(environment.Repository.LastUpdate);
        Assert.Equal(AccountResourceStatus.Active, write.Status);
        Assert.Equal(after.UpstreamBaseUrl, write.UpstreamBaseUrl);
        IntegrationEvent integrationEvent = Assert.Single(environment.Outbox.Events);
        Assert.Equal(
            [
                "name",
                "base_url",
                "credential",
                "status",
                "max_concurrency",
                "priority",
                "weight",
            ],
            integrationEvent.Payload
                .GetProperty("changed_fields")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray());

        AccountResource active = after;
        AccountResource disabled = active with
        {
            Status = AccountResourceStatus.Disabled,
            Version = 3,
            UpdatedAt = Now.AddMinutes(2),
        };
        TestEnvironment disable = new();
        disable.Repository.UpdateResults.Enqueue(new AccountMutationResult(
            AccountMutationDisposition.Written,
            WasChanged: true,
            disabled,
            active,
            CurrentVersion: 3));
        Result<AccountCommandOutcome<AccountView>> disabledResult =
            await disable.Service.ExecuteAsync(
                UpdateCommand(accountId) with
                {
                    ExpectedVersion = 2,
                    CredentialSpecified = false,
                    Credential = null,
                    StatusSpecified = true,
                    Status = AccountLifecycle.Disabled,
                    Reason = "pause upstream traffic",
                },
                TestContext.Current.CancellationToken);
        Assert.True(disabledResult.IsSuccess);
        Assert.Equal(AccountResourceStatus.Disabled, disable.Repository.LastUpdate!.Status);
    }

    [Fact]
    public async Task HealthStatesSerializeWithoutLeakingCredentialMaterial()
    {
        foreach (AccountHealth health in new[]
                 {
                     AccountHealth.Healthy,
                     AccountHealth.Degraded,
                     AccountHealth.Cooling,
                     AccountHealth.Unhealthy,
                 })
        {
            TestEnvironment environment = new();
            environment.Repository.CreateFactory = write => Written(
                Account(
                    write.AccountId,
                    write.Provider,
                    write.Name,
                    write.UpstreamBaseUrl,
                    write.CredentialPrefix) with
                {
                    Health = health,
                    UpstreamRateLimitedUntil = health == AccountHealth.Cooling
                        ? Now.AddMinutes(5)
                        : null,
                    LastHealthAt = Now,
                });

            Result<AccountCommandOutcome<AccountView>> result =
                await environment.Service.ExecuteAsync(
                    CreateCommand(),
                    TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess);
            Assert.Equal(health, result.Value.Value.Health.Status);
            Assert.DoesNotContain(
                Credential,
                Assert.Single(environment.Idempotency.Completions)
                    .ResponseBody!.Value.GetRawText(),
                StringComparison.Ordinal);
        }

        TestEnvironment invalidHealth = new();
        invalidHealth.Repository.CreateFactory = write => Written(
            Account(
                write.AccountId,
                write.Provider,
                write.Name,
                write.UpstreamBaseUrl,
                write.CredentialPrefix) with
            {
                Health = (AccountHealth)999,
            });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => invalidHealth.Service
            .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);
    }

    [Fact]
    public async Task SuccessReplaysValidateAllAccountEnumAndHeaderShapes()
    {
        (string Provider, string Status, string Health)[] cases =
        [
            ("openai", "active", "healthy"),
            ("openai_compatible", "disabled", "degraded"),
            ("openai", "retired", "cooling"),
            ("openai_compatible", "active", "unhealthy"),
            ("openai", "disabled", "unknown"),
        ];

        foreach ((string provider, string status, string health) in cases)
        {
            EntityId accountId = EntityId.New();
            TestEnvironment environment = new();
            environment.Idempotency.AcquireResult =
                CommandIdempotencyAcquireResult.Replay(
                    AccountSuccessReplay(
                        accountId,
                        statusCode: 201,
                        provider: provider,
                        status: status,
                        health: health));

            Result<AccountCommandOutcome<AccountView>> result =
                await environment.Service.ExecuteAsync(
                    CreateCommand(),
                    TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess);
            Assert.True(result.Value.IsReplay);
            Assert.Equal(accountId, result.Value.Value.Id);
            Assert.Equal("\"v5\"", result.Value.ETag);
            Assert.Equal(
                $"/api/v1/admin/accounts/{accountId.Value:D}",
                result.Value.Location);
            Assert.Equal(
                string.Equals(health, "cooling", StringComparison.Ordinal),
                result.Value.Value.Health.RetryAt is not null);
            Assert.Equal(0, environment.Repository.CreateCalls);
            Assert.Equal(0, environment.UnitOfWork.CommitCalls);
        }

        EntityId updateId = EntityId.New();
        TestEnvironment update = new();
        update.Idempotency.AcquireResult =
            CommandIdempotencyAcquireResult.Replay(
                AccountSuccessReplay(
                    updateId,
                    statusCode: 200,
                    provider: "openai",
                    status: "active",
                    health: "healthy"));
        Result<AccountCommandOutcome<AccountView>> updateResult =
            await update.Service.ExecuteAsync(
                UpdateCommand(updateId) with { ExpectedVersion = 999 },
                TestContext.Current.CancellationToken);
        Assert.True(updateResult.IsSuccess);
        Assert.True(updateResult.Value.IsReplay);
        Assert.Null(updateResult.Value.Location);
        Assert.Equal(0, update.Repository.UpdateCalls);
        Assert.Equal(0, update.UnitOfWork.CommitCalls);
    }

    [Fact]
    public async Task RetirementReplayAcceptsOnlyCanonicalTerminalEnvelope()
    {
        EntityId accountId = EntityId.New();
        TestEnvironment environment = new();
        environment.Idempotency.AcquireResult =
            CommandIdempotencyAcquireResult.Replay(
                new CommandIdempotencyResponse(
                    CommandIdempotencyTerminalStatus.Completed,
                    Status: 204,
                    Body: null,
                    BodyEnvelope: null,
                    Headers: AccountHeaders("\"v6\""),
                    ResourceType: "account",
                    ResourceId: accountId));

        Result<AccountCommandOutcome> result =
            await environment.Service.ExecuteAsync(
                RetireCommand(accountId),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsReplay);
        Assert.Equal("\"v6\"", result.Value.ETag);
        Assert.Equal(0, environment.Repository.RetireCalls);
        Assert.Equal(0, environment.UnitOfWork.CommitCalls);

        TestEnvironment malformed = new();
        malformed.Idempotency.AcquireResult =
            CommandIdempotencyAcquireResult.Replay(
                new CommandIdempotencyResponse(
                    CommandIdempotencyTerminalStatus.Completed,
                    Status: 204,
                    Body: null,
                    BodyEnvelope: null,
                    Headers: AccountHeaders("\"v01\""),
                    ResourceType: "account",
                    ResourceId: accountId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => malformed.Service
            .ExecuteAsync(
                RetireCommand(accountId),
                TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);
    }

    [Fact]
    public async Task MalformedSuccessAndFailureReplaysFailClosed()
    {
        EntityId accountId = EntityId.New();
        CommandIdempotencyResponse valid =
            AccountSuccessReplay(
                accountId,
                statusCode: 201,
                provider: "openai",
                status: "active",
                health: "healthy");
        string location = $"/api/v1/admin/accounts/{accountId.Value:D}";
        CommandIdempotencyResponse[] invalidSuccessResponses =
        [
            valid with { Body = null },
            valid with
            {
                Body = AccountReplayBody(
                    accountId,
                    name: " ",
                    provider: "openai",
                    status: "active",
                    health: "healthy"),
            },
            valid with { Status = 200 },
            valid with
            {
                Body = AccountReplayBody(
                    accountId,
                    provider: "openai",
                    status: "deleted",
                    health: "healthy"),
            },
            valid with
            {
                Body = AccountReplayBody(
                    accountId,
                    provider: "azure",
                    status: "active",
                    health: "healthy"),
            },
            valid with
            {
                Body = AccountReplayBody(
                    accountId,
                    provider: "openai",
                    status: "active",
                    health: "warming"),
            },
            valid with
            {
                Headers = AccountHeaders("\"v5\"", location, includeExtra: true),
            },
        ];
        foreach (CommandIdempotencyResponse response in invalidSuccessResponses)
        {
            TestEnvironment environment = new();
            environment.Idempotency.AcquireResult =
                CommandIdempotencyAcquireResult.Replay(response);
            await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Service
                .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
                .AsTask()).ConfigureAwait(true);
        }

        ResultErrorPresentation conflict = new(
            AccountErrorCodes.ResourceConflict,
            409,
            "Resource conflict",
            "The requested state conflicts with the current resource state.",
            Retryable: false);
        ResultErrorPresentation badTitle = conflict with { Title = "Changed" };
        ResultErrorPresentation unsupported = new(
            "unsupported_account_failure",
            409,
            "Unsupported",
            "Unsupported",
            Retryable: false);
        ResultErrorPresentation badValidationErrors = new(
            AccountErrorCodes.ValidationFailed,
            422,
            "Validation failed",
            "One or more fields failed validation.",
            Retryable: false,
            Errors: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["/"] = ["wrong validation message"],
            });
        ResultErrorPresentation versionConflict = new(
            AccountErrorCodes.VersionConflict,
            412,
            "Version conflict",
            "The resource version no longer matches; retrieve it again before retrying.",
            Retryable: true);
        CommandIdempotencyResponse validFailure =
            AccountFailureReplay("conflict", conflict);
        CommandIdempotencyResponse[] invalidFailureResponses =
        [
            validFailure with { Body = null },
            AccountFailureReplay("unsupported", unsupported),
            AccountFailureReplay("conflict", badTitle),
            validFailure with { ResourceType = "account" },
            AccountFailureReplay("validation", badValidationErrors),
            AccountFailureReplay(
                "version conflict",
                versionConflict,
                etag: "\"v01\""),
        ];
        foreach (CommandIdempotencyResponse response in invalidFailureResponses)
        {
            TestEnvironment environment = new();
            environment.Idempotency.AcquireResult =
                CommandIdempotencyAcquireResult.Replay(response);
            await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Service
                .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
                .AsTask()).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task LostLeasesAndInvalidCollaboratorResultsAbortWithoutCommit()
    {
        TestEnvironment failedMutationLease = new();
        failedMutationLease.Idempotency.CompleteResult = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => failedMutationLease.Service
            .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);
        Assert.Equal(0, failedMutationLease.UnitOfWork.CommitCalls);

        TestEnvironment successfulMutationLease = new();
        successfulMutationLease.Idempotency.CompleteResult = false;
        successfulMutationLease.Repository.CreateFactory = write => Written(
            Account(
                write.AccountId,
                write.Provider,
                write.Name,
                write.UpstreamBaseUrl,
                write.CredentialPrefix));
        await Assert.ThrowsAsync<InvalidOperationException>(() => successfulMutationLease.Service
            .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);
        Assert.Equal(0, successfulMutationLease.UnitOfWork.CommitCalls);

        EntityId accountId = EntityId.New();
        AccountResource before = Account(
            accountId,
            UpstreamProvider.OpenAi,
            "Retiring",
            "https://api.example/v1",
            "sha256:111111111111");
        AccountResource retired = before with
        {
            Status = AccountResourceStatus.Retired,
            Version = 2,
            UpdatedAt = Now.AddMinutes(1),
        };
        TestEnvironment retireLease = new();
        retireLease.Idempotency.CompleteResult = false;
        retireLease.Repository.RetireResults.Enqueue(new AccountMutationResult(
            AccountMutationDisposition.Written,
            WasChanged: true,
            retired,
            before,
            CurrentVersion: 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => retireLease.Service
            .ExecuteAsync(
                RetireCommand(accountId),
                TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);
        Assert.Equal(0, retireLease.UnitOfWork.CommitCalls);

        TestEnvironment invalidRetirement = new();
        invalidRetirement.Repository.RetireResults.Enqueue(new AccountMutationResult(
            AccountMutationDisposition.Written,
            WasChanged: false,
            retired,
            before,
            CurrentVersion: 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalidRetirement.Service
            .ExecuteAsync(
                RetireCommand(accountId),
                TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);
        Assert.Equal(0, invalidRetirement.UnitOfWork.CommitCalls);

        TestEnvironment invalidProtection = new();
        invalidProtection.Protector.Protection = new AccountCredentialProtection(
            JsonSerializer.SerializeToElement("not-an-envelope"),
            "test-key");
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalidProtection.Service
            .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);
        Assert.Equal(0, invalidProtection.UnitOfWork.BeginCalls);

        TestEnvironment invalidResource = new();
        invalidResource.Repository.GetResult = before with
        {
            Status = (AccountResourceStatus)999,
        };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => invalidResource.Service
            .ExecuteAsync(
                new GetAccountQuery(Admin, accountId),
                TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);
    }

    [Fact]
    public async Task MalformedCanonicalCursorsAreRejectedWithoutRepositoryAccess()
    {
        foreach (string cursor in new[]
                 {
                     Cursor(
                         version: 2,
                         unixMicroseconds: 0,
                         id: EntityId.New().Value),
                     Cursor(
                         version: 1,
                         unixMicroseconds: 0,
                         id: Guid.Empty),
                     Cursor(
                         version: 1,
                         unixMicroseconds: long.MaxValue,
                         id: EntityId.New().Value),
                 })
        {
            TestEnvironment environment = new();
            Result<AccountPage> result = await environment.Service.ExecuteAsync(
                new ListAccountsQuery(Admin, cursor),
                TestContext.Current.CancellationToken);
            AssertFailure(result, AccountErrorCodes.InvalidRequest);
            Assert.Null(environment.Repository.LastCursor);
        }
    }

    private static CommandIdempotencyResponse AccountSuccessReplay(
        EntityId accountId,
        int statusCode,
        string provider,
        string status,
        string health)
    {
        string? location = statusCode == 201
            ? $"/api/v1/admin/accounts/{accountId.Value:D}"
            : null;
        return new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Completed,
            statusCode,
            AccountReplayBody(accountId, provider: provider, status: status, health: health),
            BodyEnvelope: null,
            AccountHeaders("\"v5\"", location),
            ResourceType: "account",
            ResourceId: accountId);
    }

    private static JsonElement AccountReplayBody(
        EntityId accountId,
        string? name = "Replay",
        string provider = "openai_compatible",
        string status = "disabled",
        string health = "unknown",
        Guid? id = null) => JsonSerializer.SerializeToElement(new
        {
            Id = id ?? accountId.Value,
            Name = name,
            Provider = provider,
            BaseUrl = "https://replay.example/v1",
            CredentialPrefix = "sha256:222222222222",
            Status = status,
            Health = health,
            RetryAt = Now.AddMinutes(5),
            LastCheckedAt = Now,
            ActiveLeases = 0,
            MaxConcurrency = 4,
            Priority = 3,
            Weight = 100,
            Version = 5,
            CreatedAt = Now,
            UpdatedAt = Now.AddMinutes(1),
        });

    private static CommandIdempotencyResponse AccountFailureReplay(
        string description,
        ResultErrorPresentation presentation,
        string? etag = null) => new(
            CommandIdempotencyTerminalStatus.Failed,
            presentation.Status,
            JsonSerializer.SerializeToElement(new
            {
                Description = description,
                Presentation = presentation,
            }),
            BodyEnvelope: null,
            etag is null ? EmptyHeaders() : AccountHeaders(etag),
            ResourceType: null,
            ResourceId: null);

    private static CommandIdempotencyResponse ResponseFrom(
        CommandIdempotencyCompletion completion) => new(
            completion.TerminalStatus,
            completion.ResponseStatus,
            completion.ResponseBody,
            completion.ResponseBodyEnvelope,
            completion.ResponseHeaders,
            completion.ResourceType,
            completion.ResourceId);

    private static JsonElement AccountHeaders(
        string etag,
        string? location = null,
        bool includeExtra = false)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["ETag"] = etag,
        };
        if (location is not null)
        {
            headers["Location"] = location;
        }

        if (includeExtra)
        {
            headers["X-Unexpected"] = "value";
        }

        return JsonSerializer.SerializeToElement(headers);
    }

    private static JsonElement EmptyHeaders() =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static string? CompletionHeader(
        CommandIdempotencyCompletion completion,
        string name) =>
        completion.ResponseHeaders.TryGetProperty(name, out JsonElement value)
            ? value.GetString()
            : null;

    private static string Cursor(
        byte version,
        long unixMicroseconds,
        Guid id)
    {
        byte[] bytes = new byte[25];
        bytes[0] = version;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1, 8), unixMicroseconds);
        Convert.FromHexString(id.ToString("N")).CopyTo(bytes, 9);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
#pragma warning restore MA0051
#pragma warning restore MA0048
