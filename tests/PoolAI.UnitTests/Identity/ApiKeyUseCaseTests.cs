#pragma warning disable MA0048 // Test doubles are collocated with their focused use-case scenarios.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task UpdateCommitsAuditAndPlainReplayOnce()
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        UpdateApiKeyCommand command = new(
            EntityId.New(),
            environment.Actor,
            ApiKeyAccessMode.Self,
            environment.UserId,
            source.Id,
            $"api-key-update-{Guid.NewGuid():N}",
            ExpectedVersion: 1,
            SetName: true,
            Name: "  Updated key  ",
            SetStatus: false,
            Status: null,
            SetExpiresAt: false,
            ExpiresAt: null,
            SetAllowedCidrs: true,
            AllowedCidrs: ["192.168.1.42/24"],
            Reason: null,
            IpAddress: "192.0.2.10",
            UserAgent: "api-key-unit-test");

        Result<ApiKeyUpdatedOutcome> first = await environment.Service.UpdateAsync(
            command,
            source.ToSnapshot(),
            accessDecision: null,
            TestContext.Current.CancellationToken);
        Result<ApiKeyUpdatedOutcome> replay = await environment.Service.UpdateAsync(
            command,
            source.ToSnapshot(),
            accessDecision: null,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess, first.Error.Description);
        Assert.True(replay.IsSuccess, replay.Error.Description);
        Assert.False(first.Value.IsReplay);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal("\"v2\"", first.Value.ETag);
        Assert.Equal(first.Value.StatusCode, replay.Value.StatusCode);
        Assert.Equal(first.Value.ETag, replay.Value.ETag);
        Assert.Equal(
            first.Value.ApiKey with
            {
                AllowedCidrs = replay.Value.ApiKey.AllowedCidrs,
            },
            replay.Value.ApiKey);
        Assert.Equal("  Updated key  ", first.Value.ApiKey.Name);
        Assert.Equal(["192.168.1.0/24"], first.Value.ApiKey.AllowedCidrs);
        Assert.Equal(1, environment.Repository.UpdateCount);
        Assert.Single(environment.Audit.Entries);
        Assert.Equal("identity.api_key.updated", environment.Audit.Entries[0].Action);
        Assert.NotNull(environment.Idempotency.Completed!.Body);
        Assert.Null(environment.Idempotency.Completed.BodyEnvelope);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
    }

    [Fact]
    public async Task SemanticNoOpUpdateCompletesReplayWithoutAuditOrVersionAdvance()
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        UpdateApiKeyCommand command = new(
            EntityId.New(),
            environment.Actor,
            ApiKeyAccessMode.Self,
            environment.UserId,
            source.Id,
            $"api-key-update-noop-{Guid.NewGuid():N}",
            ExpectedVersion: 1,
            SetName: true,
            source.Name,
            SetStatus: false,
            Status: null,
            SetExpiresAt: false,
            ExpiresAt: null,
            SetAllowedCidrs: false,
            AllowedCidrs: null,
            Reason: null,
            IpAddress: null,
            UserAgent: null);

        Result<ApiKeyUpdatedOutcome> result = await environment.Service.UpdateAsync(
            command,
            source.ToSnapshot(),
            accessDecision: null,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error.Description);
        Assert.Equal("\"v1\"", result.Value.ETag);
        Assert.Equal(1, result.Value.ApiKey.Version);
        Assert.Empty(environment.Audit.Entries);
        Assert.Equal(1, environment.Repository.UpdateCount);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
    }

    [Fact]
    public async Task RevokeCommitsEmpty204ReplayAndAuditOnce()
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        RevokeApiKeyCommand command = new(
            EntityId.New(),
            environment.Actor,
            ApiKeyAccessMode.Self,
            environment.UserId,
            source.Id,
            $"api-key-revoke-{Guid.NewGuid():N}",
            ExpectedVersion: 1,
            Reason: "credential retired",
            IpAddress: null,
            UserAgent: null);

        Result<ApiKeyRevokedOutcome> first = await environment.Service.RevokeAsync(
            command,
            source.ToSnapshot(),
            TestContext.Current.CancellationToken);
        Result<ApiKeyRevokedOutcome> replay = await environment.Service.RevokeAsync(
            command,
            source.ToSnapshot(),
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess, first.Error.Description);
        Assert.True(replay.IsSuccess, replay.Error.Description);
        Assert.False(first.Value.IsReplay);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(204, first.Value.StatusCode);
        Assert.Equal("\"v2\"", first.Value.ETag);
        Assert.Equal(1, environment.Repository.RevokeCount);
        Assert.Single(environment.Audit.Entries);
        Assert.Equal("credential retired", environment.Audit.Entries[0].Reason);
        Assert.Null(environment.Idempotency.Completed!.Body);
        Assert.Null(environment.Idempotency.Completed.BodyEnvelope);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
    }

    [Fact]
    public async Task RotateCommitsTwoAuditsAndEncryptedSecretReplayOnce()
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        RotateApiKeyCommand command = new(
            EntityId.New(),
            environment.Actor,
            ApiKeyAccessMode.Self,
            environment.UserId,
            source.Id,
            $"api-key-rotate-{Guid.NewGuid():N}",
            ExpectedVersion: 1,
            Reason: "routine rotation",
            IpAddress: "192.0.2.10",
            UserAgent: "api-key-unit-test");
        ApiKeyAccessDecision authorized = new(
            ApiKeyAccessDecisionKind.Authorized,
            environment.UserId,
            environment.GroupId,
            EntityId.New(),
            TestTimestamp());

        Result<ApiKeyCreatedOutcome> first = await environment.Service.RotateAsync(
            command,
            source.ToSnapshot(),
            authorized,
            TestContext.Current.CancellationToken);
        Result<ApiKeyCreatedOutcome> replay = await environment.Service.RotateAsync(
            command,
            source.ToSnapshot(),
            authorized,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess, first.Error.Description);
        Assert.True(replay.IsSuccess, replay.Error.Description);
        Assert.False(first.Value.IsReplay);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(first.Value.Secret, replay.Value.Secret);
        Assert.Equal(first.Value.ApiKey.ApiKeyId, replay.Value.ApiKey.ApiKeyId);
        Assert.NotEqual(source.Id, first.Value.ApiKey.ApiKeyId);
        Assert.Equal("\"v1\"", first.Value.ETag);
        Assert.Equal(1, environment.Repository.RotateCount);
        Assert.Equal(1, environment.Credentials.CreateCount);
        Assert.Equal(2, environment.Audit.Entries.Count);
        Assert.Null(environment.Idempotency.Completed!.Body);
        Assert.NotNull(environment.Idempotency.Completed.BodyEnvelope);
        Assert.All(
            environment.Audit.Entries,
            entry => Assert.DoesNotContain(
                first.Value.Secret,
                JsonSerializer.Serialize(entry),
                StringComparison.Ordinal));
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
    }

    [Fact]
    public async Task StaleMutationVersionIsTerminalizedWithCurrentStrongEtag()
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New()) with
        {
            Version = 2,
        };
        environment.Repository.Items = [source];
        RevokeApiKeyCommand command = new(
            EntityId.New(),
            environment.Actor,
            ApiKeyAccessMode.Self,
            environment.UserId,
            source.Id,
            $"api-key-stale-{Guid.NewGuid():N}",
            ExpectedVersion: 1,
            Reason: "stale revoke",
            IpAddress: null,
            UserAgent: null);

        Result<ApiKeyRevokedOutcome> first = await environment.Service.RevokeAsync(
            command,
            source.ToSnapshot(),
            TestContext.Current.CancellationToken);
        Result<ApiKeyRevokedOutcome?> replay =
            await environment.Service.TryReplayRevokeAsync(
                command,
                TestContext.Current.CancellationToken);

        Assert.True(first.IsFailure);
        Assert.Equal(IdentityErrorCodes.VersionConflict, first.Error.Code);
        Assert.Equal("\"v2\"", first.Error.ETag);
        Assert.Equal(412, first.Error.Presentation!.Status);
        Assert.True(replay.IsFailure);
        Assert.Equal(first.Error.Code, replay.Error.Code);
        Assert.Equal(first.Error.ETag, replay.Error.ETag);
        Assert.Equal(0, environment.Repository.RevokeCount);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
    }

#pragma warning disable MA0051 // The complete replay-corruption evidence remains visible.
    [Theory]
    [InlineData("version")]
    [InlineData("name")]
    public async Task UpdateReplayRejectsOutcomeThatCannotComeFromTheBoundCommand(
        string mutation)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        UpdateApiKeyCommand command = new(
            EntityId.New(),
            environment.Actor,
            ApiKeyAccessMode.Self,
            environment.UserId,
            source.Id,
            $"api-key-corrupt-update-{Guid.NewGuid():N}",
            ExpectedVersion: 1,
            SetName: true,
            Name: "Bound update name",
            SetStatus: false,
            Status: null,
            SetExpiresAt: false,
            ExpiresAt: null,
            SetAllowedCidrs: false,
            AllowedCidrs: null,
            Reason: null,
            IpAddress: null,
            UserAgent: null);
        Result<ApiKeyUpdatedOutcome> first = await environment.Service.UpdateAsync(
            command,
            source.ToSnapshot(),
            accessDecision: null,
            TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess, first.Error.Description);

        CommandIdempotencyResponse completed = environment.Idempotency.Completed!;
        JsonObject body = JsonNode.Parse(
            completed.Body!.Value.GetRawText())!.AsObject();
        if (string.Equals(mutation, "version", StringComparison.Ordinal))
        {
            body["Version"] = 999L;
        }
        else
        {
            body["Name"] = "Different valid name";
        }

        environment.Idempotency.ForcedReplayResponse = completed with
        {
            Body = JsonSerializer.SerializeToElement(body),
        };
        int commitsBeforeReplay = environment.UnitOfWorkFactory.CommitCount;
        Result<ApiKeyUpdatedOutcome?> replay =
            await environment.Service.TryReplayUpdateAsync(
                command,
                TestContext.Current.CancellationToken);

        Assert.True(replay.IsFailure);
        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, replay.Error.Code);
        Assert.Equal(commitsBeforeReplay, environment.UnitOfWorkFactory.CommitCount);
        (string eventName, JsonElement payload) =
            Assert.Single(environment.OperationalEvents.Events);
        Assert.Equal(
            "identity.api_key.idempotency_replay_integrity_failed",
            eventName);
        Assert.Equal("update", payload.GetProperty("operation").GetString());
    }
#pragma warning restore MA0051

    [Fact]
    public async Task RevokeReplayRejectsEtagOutsideTheBoundVersionTransition()
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        RevokeApiKeyCommand command = new(
            EntityId.New(),
            environment.Actor,
            ApiKeyAccessMode.Self,
            environment.UserId,
            source.Id,
            $"api-key-corrupt-revoke-{Guid.NewGuid():N}",
            ExpectedVersion: 1,
            Reason: "credential retired",
            IpAddress: null,
            UserAgent: null);
        Result<ApiKeyRevokedOutcome> first = await environment.Service.RevokeAsync(
            command,
            source.ToSnapshot(),
            TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess, first.Error.Description);

        CommandIdempotencyResponse completed = environment.Idempotency.Completed!;
        environment.Idempotency.ForcedReplayResponse = completed with
        {
            Headers = JsonSerializer.SerializeToElement(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ETag"] = "\"v999\"",
                }),
        };
        int commitsBeforeReplay = environment.UnitOfWorkFactory.CommitCount;
        Result<ApiKeyRevokedOutcome?> replay =
            await environment.Service.TryReplayRevokeAsync(
                command,
                TestContext.Current.CancellationToken);

        Assert.True(replay.IsFailure);
        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, replay.Error.Code);
        Assert.Equal(commitsBeforeReplay, environment.UnitOfWorkFactory.CommitCount);
        (string eventName, JsonElement payload) =
            Assert.Single(environment.OperationalEvents.Events);
        Assert.Equal(
            "identity.api_key.idempotency_replay_integrity_failed",
            eventName);
        Assert.Equal("revoke", payload.GetProperty("operation").GetString());
    }

    [Fact]
    public async Task DisabledButExpiredRotateReturnsResourceConflictBeforeAccessDenial()
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        source = source with
        {
            Status = ApiKeyPersistentStatus.Disabled,
            EffectiveStatus = ApiKeyEffectiveStatus.Disabled,
            ExpiresAt = source.ObservedAt,
        };
        environment.Repository.Items = [source];
        RotateApiKeyCommand command = new(
            EntityId.New(),
            environment.Actor,
            ApiKeyAccessMode.Self,
            environment.UserId,
            source.Id,
            $"api-key-expired-disabled-{Guid.NewGuid():N}",
            ExpectedVersion: 1,
            Reason: "attempted rotation",
            IpAddress: null,
            UserAgent: null);
        ApiKeyAccessDecision denied = new(
            ApiKeyAccessDecisionKind.SubscriptionInactive,
            environment.UserId,
            environment.GroupId,
            SubscriptionId: null,
            ObservedAt: null);

        Result<ApiKeyCreatedOutcome> result = await environment.Service.RotateAsync(
            command,
            source.ToSnapshot(),
            denied,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.ResourceConflict, result.Error.Code);
        Assert.Equal(0, environment.Repository.RotateCount);
        Assert.Equal(0, environment.Credentials.CreateCount);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
    }

#pragma warning disable MA0051 // Both sides of the same expiry boundary stay paired.
    [Fact]
    public async Task CreateAndRotateAllowNaturalExpiryAfterTheirWriteLinearizationPoint()
    {
        DateTimeOffset expiration = DateTimeOffset.Parse(
            "2026-07-23T03:04:05.5000000Z",
            System.Globalization.CultureInfo.InvariantCulture);
        TestEnvironment createEnvironment = new();
        createEnvironment.Repository.ExpireCreatedResourceAfterWrite = true;
        CreateApiKeyCommand createCommand =
            createEnvironment.Command("expires-after-create") with
            {
                ExpiresAt = expiration,
            };

        Result<ApiKeyCreatedOutcome> created =
            await createEnvironment.Service.CreateAsync(
                createCommand,
                TestEnvironment.Authorized(createCommand),
                TestContext.Current.CancellationToken);

        Assert.True(created.IsSuccess, created.Error.Description);
        Assert.Equal(
            ApiKeyEffectiveStatus.Expired,
            created.Value.ApiKey.EffectiveStatus);
        new ApiKeyCreatedOutcomeValidator(createEnvironment.Credentials)
            .EnsureValid(createCommand, created.Value);

        TestEnvironment rotateEnvironment = new();
        ApiKeyResource source = FakeRepository.Resource(
            rotateEnvironment.UserId,
            rotateEnvironment.GroupId,
            EntityId.New()) with
        {
            ExpiresAt = expiration,
        };
        rotateEnvironment.Repository.Items = [source];
        rotateEnvironment.Repository.ExpireRotatedResourceAfterWrite = true;
        RotateApiKeyCommand rotateCommand = new(
            EntityId.New(),
            rotateEnvironment.Actor,
            ApiKeyAccessMode.Self,
            rotateEnvironment.UserId,
            source.Id,
            $"api-key-expires-after-rotate-{Guid.NewGuid():N}",
            ExpectedVersion: 1,
            Reason: "boundary rotation",
            IpAddress: null,
            UserAgent: null);
        ApiKeyAccessDecision authorized = new(
            ApiKeyAccessDecisionKind.Authorized,
            rotateEnvironment.UserId,
            rotateEnvironment.GroupId,
            EntityId.New(),
            TestTimestamp());

        Result<ApiKeyCreatedOutcome> rotated =
            await rotateEnvironment.Service.RotateAsync(
                rotateCommand,
                source.ToSnapshot(),
                authorized,
                TestContext.Current.CancellationToken);

        Assert.True(rotated.IsSuccess, rotated.Error.Description);
        Assert.Equal(
            ApiKeyEffectiveStatus.Expired,
            rotated.Value.ApiKey.EffectiveStatus);
        new ApiKeyCreatedOutcomeValidator(rotateEnvironment.Credentials)
            .EnsureValid(rotateCommand, rotated.Value);
    }
#pragma warning restore MA0051

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

    // Section 4.2 and AC-040 require every signed-function terminal result to be
    // completed and replayed with the same public failure, never re-executed.
    [Theory]
    [InlineData("not_found", IdentityErrorCodes.ResourceNotFound, 404, null)]
    [InlineData("revoked", "api_key_revoked", 409, null)]
    [InlineData("version_conflict", IdentityErrorCodes.VersionConflict, 412, "\"v7\"")]
    [InlineData("resource_conflict", IdentityErrorCodes.ResourceConflict, 409, null)]
    [InlineData("validation_failed", IdentityErrorCodes.ValidationFailed, 422, null)]
    public async Task UpdateFunctionFailuresAreTerminalizedAndReplayExactly(
        string disposition,
        string expectedCode,
        int expectedStatus,
        string? expectedEtag)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        environment.Repository.ForcedUpdateResult = new ApiKeyUpdateResult(
            UpdateDisposition(disposition),
            Changed: false,
            CurrentVersion: expectedEtag is null ? null : 7,
            ApiKey: null);
        UpdateApiKeyCommand command = UpdateNameCommand(
            environment,
            source,
            $"forced-{disposition}");

        Result<ApiKeyUpdatedOutcome> first = await environment.Service.UpdateAsync(
            command,
            source.ToSnapshot(),
            accessDecision: null,
            TestContext.Current.CancellationToken);
        Result<ApiKeyUpdatedOutcome?> replay =
            await environment.Service.TryReplayUpdateAsync(
                command,
                TestContext.Current.CancellationToken);

        AssertMutationFailure(first.Error, expectedCode, expectedStatus, expectedEtag);
        AssertMutationFailure(replay.Error, expectedCode, expectedStatus, expectedEtag);
        Assert.Equal(1, environment.Repository.UpdateCount);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
        Assert.Empty(environment.Audit.Entries);
        Assert.Equal(
            CommandIdempotencyTerminalStatus.Failed,
            environment.Idempotency.Completed!.TerminalStatus);
        Assert.Equal(expectedStatus, environment.Idempotency.Completed.Status);
        Assert.Empty(environment.OperationalEvents.Events);
    }

    [Theory]
    [InlineData("not_found", IdentityErrorCodes.ResourceNotFound, 404, null)]
    [InlineData("already_revoked", "api_key_revoked", 409, null)]
    [InlineData("version_conflict", IdentityErrorCodes.VersionConflict, 412, "\"v7\"")]
    [InlineData("validation_failed", IdentityErrorCodes.InvalidRequest, 400, null)]
    public async Task RevokeFunctionFailuresAreTerminalizedAndReplayExactly(
        string disposition,
        string expectedCode,
        int expectedStatus,
        string? expectedEtag)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        environment.Repository.ForcedRevokeResult = new ApiKeyRevokeResult(
            RevokeDisposition(disposition),
            CurrentVersion: expectedEtag is null ? null : 7,
            ApiKey: null);
        RevokeApiKeyCommand command = RevokeCommand(
            environment,
            source,
            $"forced-{disposition}");

        Result<ApiKeyRevokedOutcome> first = await environment.Service.RevokeAsync(
            command,
            source.ToSnapshot(),
            TestContext.Current.CancellationToken);
        Result<ApiKeyRevokedOutcome?> replay =
            await environment.Service.TryReplayRevokeAsync(
                command,
                TestContext.Current.CancellationToken);

        AssertMutationFailure(first.Error, expectedCode, expectedStatus, expectedEtag);
        AssertMutationFailure(replay.Error, expectedCode, expectedStatus, expectedEtag);
        Assert.Equal(1, environment.Repository.RevokeCount);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
        Assert.Empty(environment.Audit.Entries);
        Assert.Equal(
            CommandIdempotencyTerminalStatus.Failed,
            environment.Idempotency.Completed!.TerminalStatus);
        Assert.Equal(expectedStatus, environment.Idempotency.Completed.Status);
        Assert.Empty(environment.OperationalEvents.Events);
    }

    [Theory]
    [InlineData("not_found", IdentityErrorCodes.ResourceNotFound, 404, null)]
    [InlineData("revoked", "api_key_revoked", 409, null)]
    [InlineData("version_conflict", IdentityErrorCodes.VersionConflict, 412, "\"v7\"")]
    [InlineData("resource_conflict", IdentityErrorCodes.ResourceConflict, 409, null)]
    [InlineData("conflict", IdentityErrorCodes.ResourceConflict, 409, null)]
    [InlineData("validation_failed", IdentityErrorCodes.ValidationFailed, 422, null)]
    public async Task RotateFunctionFailuresAreTerminalizedAndReplayExactly(
        string disposition,
        string expectedCode,
        int expectedStatus,
        string? expectedEtag)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        environment.Repository.ForcedRotateResult = new ApiKeyRotateResult(
            RotateDisposition(disposition),
            OldCurrentVersion: expectedEtag is null ? null : 7,
            OldApiKey: null,
            NewApiKey: null);
        RotateApiKeyCommand command = RotateCommand(
            environment,
            source,
            $"forced-{disposition}");

        Result<ApiKeyCreatedOutcome> first = await environment.Service.RotateAsync(
            command,
            source.ToSnapshot(),
            Authorized(environment),
            TestContext.Current.CancellationToken);
        Result<ApiKeyCreatedOutcome?> replay =
            await environment.Service.TryReplayRotateAsync(
                command,
                TestContext.Current.CancellationToken);

        AssertMutationFailure(first.Error, expectedCode, expectedStatus, expectedEtag);
        AssertMutationFailure(replay.Error, expectedCode, expectedStatus, expectedEtag);
        Assert.Equal(1, environment.Repository.RotateCount);
        Assert.Equal(1, environment.Credentials.CreateCount);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
        Assert.Empty(environment.Audit.Entries);
        Assert.Equal(
            CommandIdempotencyTerminalStatus.Failed,
            environment.Idempotency.Completed!.TerminalStatus);
        Assert.Equal(expectedStatus, environment.Idempotency.Completed.Status);
        Assert.Empty(environment.OperationalEvents.Events);
    }

    // DEC-026 and section 4.2 freeze the post-lock precedence before any
    // Subscription decision or signed mutation may run.
    [Theory]
    [InlineData("missing", IdentityErrorCodes.ResourceNotFound, null)]
    [InlineData("revoked", "api_key_revoked", null)]
    [InlineData("group", IdentityErrorCodes.ResourceConflict, null)]
    [InlineData("version", IdentityErrorCodes.VersionConflict, "\"v2\"")]
    [InlineData("lifecycle", IdentityErrorCodes.ResourceConflict, null)]
    public async Task UpdateRejectsEveryLockedStateDriftBeforeWriting(
        string state,
        string expectedCode,
        string? expectedEtag)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        environment.Repository.UseForcedLockResult = true;
        environment.Repository.ForcedLockResult = LockedState(source, state);
        UpdateApiKeyCommand command = UpdateNameCommand(
            environment,
            source,
            $"locked-{state}");

        Result<ApiKeyUpdatedOutcome> result = await environment.Service.UpdateAsync(
            command,
            source.ToSnapshot(),
            accessDecision: null,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(expectedEtag, result.Error.ETag);
        Assert.Equal(0, environment.Repository.UpdateCount);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
        Assert.Empty(environment.Audit.Entries);
    }

    [Theory]
    [InlineData("missing", IdentityErrorCodes.ResourceNotFound, null)]
    [InlineData("revoked", "api_key_revoked", null)]
    [InlineData("group", IdentityErrorCodes.ResourceConflict, null)]
    [InlineData("version", IdentityErrorCodes.VersionConflict, "\"v2\"")]
    [InlineData("lifecycle", IdentityErrorCodes.ResourceConflict, null)]
    public async Task RotateRejectsEveryLockedStateDriftBeforeGeneratingASecret(
        string state,
        string expectedCode,
        string? expectedEtag)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        environment.Repository.UseForcedLockResult = true;
        environment.Repository.ForcedLockResult = LockedState(source, state);
        RotateApiKeyCommand command = RotateCommand(
            environment,
            source,
            $"locked-{state}");

        Result<ApiKeyCreatedOutcome> result = await environment.Service.RotateAsync(
            command,
            source.ToSnapshot(),
            Authorized(environment),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(expectedEtag, result.Error.ETag);
        Assert.Equal(0, environment.Repository.RotateCount);
        Assert.Equal(0, environment.Credentials.CreateCount);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
        Assert.Empty(environment.Audit.Entries);
    }

    [Theory]
    [InlineData("missing", IdentityErrorCodes.ResourceNotFound, null)]
    [InlineData("revoked", "api_key_revoked", null)]
    [InlineData("version", IdentityErrorCodes.VersionConflict, "\"v2\"")]
    [InlineData("snapshot", IdentityErrorCodes.ResourceConflict, null)]
    public async Task RevokeRejectsEveryLockedStateDriftBeforeWriting(
        string state,
        string expectedCode,
        string? expectedEtag)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        environment.Repository.UseForcedLockResult = true;
        bool snapshotDrift = string.Equals(
            state,
            "snapshot",
            StringComparison.Ordinal);
        environment.Repository.ForcedLockResult = snapshotDrift
            ? source with
            {
                Version = 2,
            }
            : LockedState(source, state);
        RevokeApiKeyCommand command = RevokeCommand(
            environment,
            source,
            $"locked-{state}") with
        {
            ExpectedVersion = snapshotDrift ? 2 : 1,
        };

        Result<ApiKeyRevokedOutcome> result = await environment.Service.RevokeAsync(
            command,
            source.ToSnapshot(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(expectedEtag, result.Error.ETag);
        Assert.Equal(0, environment.Repository.RevokeCount);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
        Assert.Empty(environment.Audit.Entries);
    }

    [Theory]
    [InlineData("authorized", null, true, true)]
    [InlineData("null", IdentityErrorCodes.DependencyUnavailable, false, false)]
    [InlineData("wrong_user", IdentityErrorCodes.DependencyUnavailable, false, false)]
    [InlineData("wrong_group", IdentityErrorCodes.DependencyUnavailable, false, false)]
    [InlineData("authorized_missing_subscription", IdentityErrorCodes.DependencyUnavailable, false, false)]
    [InlineData("authorized_empty_subscription", IdentityErrorCodes.DependencyUnavailable, false, false)]
    [InlineData("authorized_missing_observed_at", IdentityErrorCodes.DependencyUnavailable, false, false)]
    [InlineData("authorized_default_observed_at", IdentityErrorCodes.DependencyUnavailable, false, false)]
    [InlineData("required_with_subscription", IdentityErrorCodes.DependencyUnavailable, false, false)]
    [InlineData("inactive_with_observed_at", IdentityErrorCodes.DependencyUnavailable, false, false)]
    [InlineData("required", "subscription_required", true, false)]
    [InlineData("inactive", "subscription_inactive", true, false)]
    public async Task EnablingAKeyValidatesTheCompleteSubscriptionDecision(
        string decisionCase,
        string? expectedCode,
        bool expectedCommit,
        bool expectedSuccess)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New()) with
        {
            Status = ApiKeyPersistentStatus.Disabled,
            EffectiveStatus = ApiKeyEffectiveStatus.Disabled,
        };
        environment.Repository.Items = [source];
        UpdateApiKeyCommand command = UpdateStatusCommand(
            environment,
            source,
            $"subscription-{decisionCase}",
            ApiKeyPersistentStatus.Active);
        ApiKeyAccessDecision? decision = Decision(
            environment,
            decisionCase);

        Result<ApiKeyUpdatedOutcome> result = await environment.Service.UpdateAsync(
            command,
            source.ToSnapshot(),
            decision,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedSuccess, result.IsSuccess);
        Assert.Equal(expectedCommit ? 1 : 0, environment.UnitOfWorkFactory.CommitCount);
        if (expectedSuccess)
        {
            Assert.Equal(ApiKeyPersistentStatus.Active, result.Value.ApiKey.Status);
            Assert.Equal(ApiKeyEffectiveStatus.Active, result.Value.ApiKey.EffectiveStatus);
            Assert.Equal(1, environment.Repository.UpdateCount);
            Assert.Single(environment.Audit.Entries);
            Assert.Equal(
                CommandIdempotencyTerminalStatus.Completed,
                environment.Idempotency.Completed!.TerminalStatus);
        }
        else
        {
            Assert.Equal(expectedCode, result.Error.Code);
            Assert.Equal(0, environment.Repository.UpdateCount);
            Assert.Empty(environment.Audit.Entries);
            if (expectedCommit)
            {
                Assert.Equal(
                    CommandIdempotencyTerminalStatus.Failed,
                    environment.Idempotency.Completed!.TerminalStatus);
            }
            else
            {
                Assert.Null(environment.Idempotency.Completed);
            }
        }
    }

    [Fact]
    public async Task RestoringAnExpiredKeyRequiresBoundAuthorization()
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        source = source with
        {
            EffectiveStatus = ApiKeyEffectiveStatus.Expired,
            ExpiresAt = source.ObservedAt,
        };
        environment.Repository.Items = [source];
        UpdateApiKeyCommand command = new(
            EntityId.New(),
            environment.Actor,
            ApiKeyAccessMode.Self,
            environment.UserId,
            source.Id,
            $"api-key-restore-expired-{Guid.NewGuid():N}",
            ExpectedVersion: 1,
            SetName: false,
            Name: null,
            SetStatus: false,
            Status: null,
            SetExpiresAt: true,
            ExpiresAt: source.ObservedAt.AddDays(1),
            SetAllowedCidrs: false,
            AllowedCidrs: null,
            Reason: null,
            IpAddress: null,
            UserAgent: null);

        Result<ApiKeyUpdatedOutcome> result = await environment.Service.UpdateAsync(
            command,
            source.ToSnapshot(),
            Authorized(environment),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error.Description);
        Assert.Equal(ApiKeyEffectiveStatus.Active, result.Value.ApiKey.EffectiveStatus);
        Assert.Equal(source.ObservedAt.AddDays(1), result.Value.ApiKey.ExpiresAt);
        Assert.Equal(1, environment.Repository.UpdateCount);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
    }

    [Theory]
    [InlineData("update", "conflict", IdentityErrorCodes.IdempotencyConflict, null)]
    [InlineData("update", "busy", IdentityErrorCodes.CoordinationUnavailable, 1L)]
    [InlineData("revoke", "conflict", IdentityErrorCodes.IdempotencyConflict, null)]
    [InlineData("revoke", "busy", IdentityErrorCodes.CoordinationUnavailable, 1L)]
    [InlineData("rotate", "conflict", IdentityErrorCodes.IdempotencyConflict, null)]
    [InlineData("rotate", "busy", IdentityErrorCodes.CoordinationUnavailable, 1L)]
    public async Task MutationPreflightStopsOnConflictOrBusy(
        string operation,
        string disposition,
        string expectedCode,
        long? expectedRetryAfter)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        environment.Idempotency.ForcedAcquireResult =
            string.Equals(disposition, "busy", StringComparison.Ordinal)
                ? CommandIdempotencyAcquireResult.Busy
                : CommandIdempotencyAcquireResult.Conflict;

        ResultError error = await InvokeMutationPreflightAsync(
            environment,
            source,
            operation);

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedRetryAfter, error.RetryAfterSeconds);
        Assert.Equal(1, environment.UnitOfWorkFactory.BeginCount);
        Assert.Equal(0, environment.UnitOfWorkFactory.CommitCount);
        Assert.Equal(0, environment.Repository.UpdateCount);
        Assert.Equal(0, environment.Repository.RevokeCount);
        Assert.Equal(0, environment.Repository.RotateCount);
        Assert.Empty(environment.OperationalEvents.Events);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("revoke")]
    [InlineData("rotate")]
    public async Task MalformedMutationTerminalReplayFailsClosedAndAlerts(
        string operation)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        environment.Idempotency.ForcedReplayResponse = new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Completed,
            Status: 299,
            Body: null,
            BodyEnvelope: null,
            Headers: JsonSerializer.SerializeToElement(new { }),
            ResourceType: "wrong-resource",
            ResourceId: source.Id);

        ResultError error = await InvokeMutationPreflightAsync(
            environment,
            source,
            operation);

        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, error.Code);
        Assert.Equal(1, error.RetryAfterSeconds);
        Assert.Equal(0, environment.UnitOfWorkFactory.CommitCount);
        Assert.Equal(0, environment.Repository.UpdateCount);
        Assert.Equal(0, environment.Repository.RevokeCount);
        Assert.Equal(0, environment.Repository.RotateCount);
        Assert.True(environment.OperationalEvents.WasUnitOfWorkDisposedBeforeWrite);
        (string eventName, JsonElement payload) =
            Assert.Single(environment.OperationalEvents.Events);
        Assert.Equal(
            "identity.api_key.idempotency_replay_integrity_failed",
            eventName);
        Assert.Equal(operation, payload.GetProperty("operation").GetString());
        Assert.DoesNotContain(
            environment.Idempotency.LastRequest!.Key,
            payload.GetRawText(),
            StringComparison.Ordinal);
    }

#pragma warning disable MA0051 // The three mutation preflight shapes are intentionally compared together.
    [Fact]
    public async Task SuccessfulMutationPreflightsReturnTheExactStoredOutcome()
    {
        TestEnvironment update = new();
        ApiKeyResource updateSource = FakeRepository.Resource(
            update.UserId,
            update.GroupId,
            EntityId.New());
        update.Repository.Items = [updateSource];
        UpdateApiKeyCommand updateCommand = UpdateNameCommand(
            update,
            updateSource,
            "successful-preflight");
        Result<ApiKeyUpdatedOutcome> updated = await update.Service.UpdateAsync(
            updateCommand,
            updateSource.ToSnapshot(),
            accessDecision: null,
            TestContext.Current.CancellationToken);
        Result<ApiKeyUpdatedOutcome?> updateReplay =
            await update.Service.TryReplayUpdateAsync(
                updateCommand,
                TestContext.Current.CancellationToken);

        Assert.True(updated.IsSuccess, updated.Error.Description);
        Assert.True(updateReplay.IsSuccess, updateReplay.Error.Description);
        Assert.NotNull(updateReplay.Value);
        Assert.True(updateReplay.Value.IsReplay);
        Assert.Equal(updated.Value.ETag, updateReplay.Value.ETag);

        TestEnvironment revoke = new();
        ApiKeyResource revokeSource = FakeRepository.Resource(
            revoke.UserId,
            revoke.GroupId,
            EntityId.New());
        revoke.Repository.Items = [revokeSource];
        RevokeApiKeyCommand revokeCommand = RevokeCommand(
            revoke,
            revokeSource,
            "successful-preflight");
        Result<ApiKeyRevokedOutcome> revoked = await revoke.Service.RevokeAsync(
            revokeCommand,
            revokeSource.ToSnapshot(),
            TestContext.Current.CancellationToken);
        Result<ApiKeyRevokedOutcome?> revokeReplay =
            await revoke.Service.TryReplayRevokeAsync(
                revokeCommand,
                TestContext.Current.CancellationToken);

        Assert.True(revoked.IsSuccess, revoked.Error.Description);
        Assert.True(revokeReplay.IsSuccess, revokeReplay.Error.Description);
        Assert.NotNull(revokeReplay.Value);
        Assert.True(revokeReplay.Value.IsReplay);
        Assert.Equal(revoked.Value.ETag, revokeReplay.Value.ETag);

        TestEnvironment rotate = new();
        ApiKeyResource rotateSource = FakeRepository.Resource(
            rotate.UserId,
            rotate.GroupId,
            EntityId.New());
        rotate.Repository.Items = [rotateSource];
        RotateApiKeyCommand rotateCommand = RotateCommand(
            rotate,
            rotateSource,
            "successful-preflight");
        Result<ApiKeyCreatedOutcome> rotated = await rotate.Service.RotateAsync(
            rotateCommand,
            rotateSource.ToSnapshot(),
            Authorized(rotate),
            TestContext.Current.CancellationToken);
        Result<ApiKeyCreatedOutcome?> rotateReplay =
            await rotate.Service.TryReplayRotateAsync(
                rotateCommand,
                TestContext.Current.CancellationToken);

        Assert.True(rotated.IsSuccess, rotated.Error.Description);
        Assert.True(rotateReplay.IsSuccess, rotateReplay.Error.Description);
        Assert.NotNull(rotateReplay.Value);
        Assert.True(rotateReplay.Value.IsReplay);
        Assert.Equal(rotated.Value.Secret, rotateReplay.Value.Secret);
        Assert.Equal(rotated.Value.ApiKey.ApiKeyId, rotateReplay.Value.ApiKey.ApiKeyId);
    }
#pragma warning restore MA0051

    // The cursor is an opaque, canonical base64url encoding. Invalid values
    // must fail before the repository can observe a pagination request.
    public static TheoryData<string> InvalidApiKeyCursors => new()
    {
        string.Empty,
        "=",
        "abc*",
        "A",
        "AB",
        CursorText(version: 2, unixMicroseconds: 0, hasId: true),
        CursorText(version: 1, unixMicroseconds: 0, hasId: false),
        CursorText(version: 1, unixMicroseconds: long.MaxValue, hasId: true),
    };

    [Theory]
    [MemberData(nameof(InvalidApiKeyCursors))]
    public async Task InvalidCursorFailsBeforeRepositoryAccess(string cursor)
    {
        TestEnvironment environment = new();

        Result<ApiKeyPage> result = await environment.Service.ListAsync(
            new ListApiKeysQuery(
                environment.Actor,
                ApiKeyAccessMode.Self,
                environment.UserId,
                cursor),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.InvalidRequest, result.Error.Code);
        Assert.Equal(0, environment.Repository.ListCount);
    }

    [Fact]
    public async Task CursorRoundTripsTheLastItemAndSelectsOnlyTheNextSlice()
    {
        TestEnvironment environment = new();
        DateTimeOffset newerAt = DateTimeOffset.Parse(
            "2026-07-23T06:07:08Z",
            System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset olderAt = newerAt.AddMinutes(-1);
        ApiKeyResource newer = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            new EntityId(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"))) with
        {
            CreatedAt = newerAt,
            UpdatedAt = newerAt,
            ObservedAt = newerAt,
        };
        ApiKeyResource older = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            new EntityId(Guid.Parse("11111111-1111-1111-1111-111111111111"))) with
        {
            CreatedAt = olderAt,
            UpdatedAt = olderAt,
            ObservedAt = olderAt,
        };
        environment.Repository.Items = [older, newer];

        Result<ApiKeyPage> first = await environment.Service.ListAsync(
            new ListApiKeysQuery(
                environment.Actor,
                ApiKeyAccessMode.Self,
                environment.UserId,
                Cursor: null,
                Limit: 1),
            TestContext.Current.CancellationToken);
        Result<ApiKeyPage> second = await environment.Service.ListAsync(
            new ListApiKeysQuery(
                environment.Actor,
                ApiKeyAccessMode.Self,
                environment.UserId,
                first.Value.NextCursor,
                Limit: 1),
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess, first.Error.Description);
        Assert.True(first.Value.HasMore);
        Assert.Equal(newer.Id, Assert.Single(first.Value.Data).ApiKeyId);
        Assert.NotNull(first.Value.NextCursor);
        Assert.True(second.IsSuccess, second.Error.Description);
        Assert.False(second.Value.HasMore);
        Assert.Equal(older.Id, Assert.Single(second.Value.Data).ApiKeyId);
        Assert.Null(second.Value.NextCursor);
        Assert.NotNull(environment.Repository.LastCursor);
        Assert.Equal(newer.CreatedAt, environment.Repository.LastCursor.CreatedAt);
        Assert.Equal(newer.Id, environment.Repository.LastCursor.Id);
    }

    [Theory]
    [InlineData("update", "no_fields")]
    [InlineData("update", "name_null")]
    [InlineData("update", "name_omitted")]
    [InlineData("update", "status_null")]
    [InlineData("update", "status_revoked")]
    [InlineData("update", "status_omitted")]
    [InlineData("update", "expires_omitted")]
    [InlineData("update", "cidrs_null")]
    [InlineData("update", "cidrs_omitted")]
    [InlineData("update", "self_reason")]
    [InlineData("update", "empty_actor")]
    [InlineData("update", "empty_user")]
    [InlineData("update", "empty_api_key")]
    [InlineData("update", "empty_request")]
    [InlineData("update", "zero_version")]
    [InlineData("update", "bad_idempotency")]
    [InlineData("revoke", "blank_reason")]
    [InlineData("rotate", "blank_reason")]
    public async Task InvalidMutationCommandsFailBothPreflightAndMainBeforeAcquire(
        string operation,
        string violation)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];

        await AssertInvalidMutationBothAsync(
            environment,
            source,
            operation,
            violation);

        Assert.Equal(0, environment.UnitOfWorkFactory.BeginCount);
        Assert.Null(environment.Idempotency.LastRequest);
        Assert.Equal(0, environment.Repository.LockCount);
    }

    [Theory]
    [InlineData("update", "acquired", null, null)]
    [InlineData("update", "conflict", IdentityErrorCodes.IdempotencyConflict, null)]
    [InlineData("update", "busy", IdentityErrorCodes.CoordinationUnavailable, 1L)]
    [InlineData("revoke", "acquired", null, null)]
    [InlineData("revoke", "conflict", IdentityErrorCodes.IdempotencyConflict, null)]
    [InlineData("revoke", "busy", IdentityErrorCodes.CoordinationUnavailable, 1L)]
    [InlineData("rotate", "acquired", null, null)]
    [InlineData("rotate", "conflict", IdentityErrorCodes.IdempotencyConflict, null)]
    [InlineData("rotate", "busy", IdentityErrorCodes.CoordinationUnavailable, 1L)]
    public async Task MutationPreflightCoversEveryNonReplayDisposition(
        string operation,
        string disposition,
        string? expectedCode,
        long? retryAfter)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        environment.Idempotency.ForcedAcquireResult = AcquireResult(disposition);

        ResultError? error = await InvokeMutationPreflightResultAsync(
            environment,
            source,
            operation);

        if (expectedCode is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.NotNull(error);
            Assert.Equal(expectedCode, error.Code);
            Assert.Equal(retryAfter, error.RetryAfterSeconds);
        }

        Assert.Equal(1, environment.UnitOfWorkFactory.BeginCount);
        Assert.Equal(0, environment.UnitOfWorkFactory.CommitCount);
        Assert.Equal(0, environment.Repository.LockCount);
    }

    [Theory]
    [InlineData("update", "conflict", IdentityErrorCodes.IdempotencyConflict, null)]
    [InlineData("update", "busy", IdentityErrorCodes.CoordinationUnavailable, 1L)]
    [InlineData("revoke", "conflict", IdentityErrorCodes.IdempotencyConflict, null)]
    [InlineData("revoke", "busy", IdentityErrorCodes.CoordinationUnavailable, 1L)]
    [InlineData("rotate", "conflict", IdentityErrorCodes.IdempotencyConflict, null)]
    [InlineData("rotate", "busy", IdentityErrorCodes.CoordinationUnavailable, 1L)]
    public async Task MutationMainPathStopsOnEveryAcquireFailure(
        string operation,
        string disposition,
        string expectedCode,
        long? retryAfter)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        environment.Idempotency.ForcedAcquireResult = AcquireResult(disposition);

        ResultError error = await InvokeMutationMainFailureAsync(
            environment,
            source,
            operation);

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(retryAfter, error.RetryAfterSeconds);
        Assert.Equal(0, environment.Repository.LockCount);
        Assert.Equal(0, environment.UnitOfWorkFactory.CommitCount);
    }

    [Theory]
    [InlineData("update", "preflight")]
    [InlineData("revoke", "preflight")]
    [InlineData("rotate", "preflight")]
    [InlineData("update", "main")]
    [InlineData("revoke", "main")]
    [InlineData("rotate", "main")]
    public async Task InvalidIdempotencyDispositionFailsClosed(
        string operation,
        string path)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        environment.Idempotency.ForcedAcquireResult = new CommandIdempotencyAcquireResult(
            (CommandIdempotencyDisposition)999,
            Lease: null,
            Response: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                if (string.Equals(path, "preflight", StringComparison.Ordinal))
                {
                    _ = await InvokeMutationPreflightResultAsync(
                        environment,
                        source,
                        operation).ConfigureAwait(false);
                }
                else
                {
                    _ = await InvokeMutationMainFailureAsync(
                        environment,
                        source,
                        operation).ConfigureAwait(false);
                }
            });
    }

    [Theory]
    [InlineData("update")]
    [InlineData("revoke")]
    [InlineData("rotate")]
    public async Task MalformedMutationReplayOnMainPathRollsBackAndAlerts(
        string operation)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        environment.Idempotency.ForcedReplayResponse = new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Completed,
            Status: 299,
            Body: null,
            BodyEnvelope: null,
            Headers: JsonSerializer.SerializeToElement(new { }),
            ResourceType: "wrong-resource",
            ResourceId: source.Id);

        ResultError error = await InvokeMutationMainFailureAsync(
            environment,
            source,
            operation);

        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, error.Code);
        Assert.Equal(1, error.RetryAfterSeconds);
        Assert.Equal(0, environment.Repository.LockCount);
        Assert.Equal(0, environment.UnitOfWorkFactory.CommitCount);
        Assert.Single(environment.OperationalEvents.Events);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("revoke")]
    [InlineData("rotate")]
    public async Task InconsistentMutationSnapshotFailsBeforeUnitOfWork(string operation)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        ApiKeyControlPlaneSnapshot snapshot = source.ToSnapshot() with
        {
            ApiKeyId = EntityId.New(),
        };

        ResultError error = await InvokeMutationMainFailureAsync(
            environment,
            source,
            operation,
            snapshot);

        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, error.Code);
        Assert.Equal(1, error.RetryAfterSeconds);
        Assert.Equal(0, environment.UnitOfWorkFactory.BeginCount);
    }

// All successful repository result shapes share one invariant matrix.
#pragma warning disable MA0051
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("revoke")]
    [InlineData("rotate_old")]
    [InlineData("rotate_new")]
    public async Task SuccessfulRepositoryDispositionMustContainItsResource(
        string operation)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];

        switch (operation)
        {
            case "create":
                environment.Repository.ForcedCreateResult = new ApiKeyCreateResult(
                    ApiKeyCreateDisposition.Created,
                    ApiKey: null);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await environment.Service.CreateAsync(
                        environment.Command("null-created"),
                        AuthorizedCreate(environment),
                        TestContext.Current.CancellationToken).ConfigureAwait(false));
                break;
            case "update":
                environment.Repository.ForcedUpdateResult = new ApiKeyUpdateResult(
                    ApiKeyUpdateDisposition.Updated,
                    Changed: true,
                    CurrentVersion: 2,
                    ApiKey: null);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await environment.Service.UpdateAsync(
                        UpdateNameCommand(environment, source, "null-current"),
                        source.ToSnapshot(),
                        accessDecision: null,
                        TestContext.Current.CancellationToken).ConfigureAwait(false));
                break;
            case "revoke":
                environment.Repository.ForcedRevokeResult = new ApiKeyRevokeResult(
                    ApiKeyRevokeDisposition.Revoked,
                    CurrentVersion: 2,
                    ApiKey: null);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await environment.Service.RevokeAsync(
                        RevokeCommand(environment, source, "null-current"),
                        source.ToSnapshot(),
                        TestContext.Current.CancellationToken).ConfigureAwait(false));
                break;
            case "rotate_old":
                environment.Repository.ForcedRotateResult = new ApiKeyRotateResult(
                    ApiKeyRotateDisposition.Rotated,
                    OldCurrentVersion: 2,
                    OldApiKey: null,
                    NewApiKey: null);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await environment.Service.RotateAsync(
                        RotateCommand(environment, source, "null-old"),
                        source.ToSnapshot(),
                        Authorized(environment),
                        TestContext.Current.CancellationToken).ConfigureAwait(false));
                break;
            case "rotate_new":
                ApiKeyResource revoked = RevokedResource(source);
                environment.Repository.ForcedRotateResult = new ApiKeyRotateResult(
                    ApiKeyRotateDisposition.Rotated,
                    OldCurrentVersion: 2,
                    revoked,
                    NewApiKey: null);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await environment.Service.RotateAsync(
                        RotateCommand(environment, source, "null-new"),
                        source.ToSnapshot(),
                        Authorized(environment),
                        TestContext.Current.CancellationToken).ConfigureAwait(false));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        Assert.Equal(0, environment.UnitOfWorkFactory.CommitCount);
    }
#pragma warning restore MA0051

    [Theory]
    [InlineData("conflict", IdentityErrorCodes.ResourceConflict, 409)]
    [InlineData("validation", IdentityErrorCodes.ValidationFailed, 422)]
    public async Task CreateRepositoryFailuresAreTerminalizedAndReplayExactly(
        string disposition,
        string expectedCode,
        int expectedStatus)
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = environment.Command(
            $"forced-create-{disposition}");
        environment.Repository.ForcedCreateResult = new ApiKeyCreateResult(
            string.Equals(disposition, "conflict", StringComparison.Ordinal)
                ? ApiKeyCreateDisposition.Conflict
                : ApiKeyCreateDisposition.ValidationFailed,
            ApiKey: null);

        Result<ApiKeyCreatedOutcome> first = await environment.Service.CreateAsync(
            command,
            TestEnvironment.Authorized(command),
            TestContext.Current.CancellationToken);
        Result<ApiKeyCreatedOutcome?> replay = await environment.Service.TryReplayAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsFailure);
        Assert.Equal(expectedCode, first.Error.Code);
        Assert.Equal(expectedStatus, first.Error.Presentation!.Status);
        Assert.True(replay.IsFailure);
        Assert.Equal(expectedCode, replay.Error.Code);
        Assert.Equal(1, environment.Repository.CreateCount);
        Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
    }

    [Theory]
    [InlineData("create_success")]
    [InlineData("create_failure")]
    [InlineData("update_success")]
    [InlineData("update_failure")]
    public async Task LostIdempotencyLeasePreventsCommit(string path)
    {
        TestEnvironment environment = new();
        environment.Idempotency.CompleteResult = false;

        if (path.StartsWith("create", StringComparison.Ordinal))
        {
            CreateApiKeyCommand command = environment.Command($"lost-{path}");
            if (path.EndsWith("failure", StringComparison.Ordinal))
            {
                environment.Repository.ForcedCreateResult = new ApiKeyCreateResult(
                    ApiKeyCreateDisposition.Conflict,
                    ApiKey: null);
            }

            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await environment.Service.CreateAsync(
                    command,
                    TestEnvironment.Authorized(command),
                    TestContext.Current.CancellationToken).ConfigureAwait(false));
        }
        else
        {
            ApiKeyResource source = FakeRepository.Resource(
                environment.UserId,
                environment.GroupId,
                EntityId.New());
            environment.Repository.Items = [source];
            if (path.EndsWith("failure", StringComparison.Ordinal))
            {
                environment.Repository.ForcedUpdateResult = new ApiKeyUpdateResult(
                    ApiKeyUpdateDisposition.NotFound,
                    Changed: false,
                    CurrentVersion: null,
                    ApiKey: null);
            }

            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await environment.Service.UpdateAsync(
                    UpdateNameCommand(environment, source, $"lost-{path}"),
                    source.ToSnapshot(),
                    accessDecision: null,
                    TestContext.Current.CancellationToken).ConfigureAwait(false));
        }

        Assert.Equal(0, environment.UnitOfWorkFactory.CommitCount);
    }

    [Theory]
    [InlineData("create", "id")]
    [InlineData("create", "user")]
    [InlineData("create", "group")]
    [InlineData("create", "name")]
    [InlineData("create", "prefix")]
    [InlineData("create", "status")]
    [InlineData("create", "expires")]
    [InlineData("create", "cidrs")]
    [InlineData("create", "last_used")]
    [InlineData("create", "version")]
    [InlineData("create", "created")]
    [InlineData("create", "updated")]
    [InlineData("update", "id")]
    [InlineData("update", "user")]
    [InlineData("update", "group")]
    [InlineData("update", "name")]
    [InlineData("update", "prefix")]
    [InlineData("update", "status")]
    [InlineData("update", "expires")]
    [InlineData("update", "cidrs")]
    [InlineData("update", "last_used")]
    [InlineData("update", "version")]
    [InlineData("update", "created")]
    [InlineData("update", "updated")]
    [InlineData("revoke", "id")]
    [InlineData("revoke", "user")]
    [InlineData("revoke", "group")]
    [InlineData("revoke", "name")]
    [InlineData("revoke", "prefix")]
    [InlineData("revoke", "status")]
    [InlineData("revoke", "expires")]
    [InlineData("revoke", "cidrs")]
    [InlineData("revoke", "last_used")]
    [InlineData("revoke", "version")]
    [InlineData("revoke", "created")]
    [InlineData("revoke", "updated")]
    [InlineData("rotate", "id")]
    [InlineData("rotate", "user")]
    [InlineData("rotate", "group")]
    [InlineData("rotate", "name")]
    [InlineData("rotate", "prefix")]
    [InlineData("rotate", "status")]
    [InlineData("rotate", "expires")]
    [InlineData("rotate", "cidrs")]
    [InlineData("rotate", "last_used")]
    [InlineData("rotate", "version")]
    [InlineData("rotate", "created")]
    [InlineData("rotate", "updated")]
    [InlineData("rotate", "rotation_time")]
#pragma warning disable MA0051 // Every repository postcondition is exercised through one public-behavior harness.
    public async Task RepositoryResourceContractViolationsFailClosed(
        string operation,
        string mutation)
    {
        TestEnvironment environment = new();
        ApiKeyResource source = FakeRepository.Resource(
            environment.UserId,
            environment.GroupId,
            EntityId.New());
        environment.Repository.Items = [source];
        Func<ApiKeyResource, ApiKeyResource> transform =
            value => CorruptRepositoryResource(
                value,
                mutation,
                operation is "create" or "rotate");

        switch (operation)
        {
            case "create":
            {
                environment.Repository.CreatedTransform = transform;
                CreateApiKeyCommand command = environment.Command(
                    $"contract-{mutation}");
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await environment.Service.CreateAsync(
                        command,
                        TestEnvironment.Authorized(command),
                        TestContext.Current.CancellationToken).ConfigureAwait(false));
                break;
            }

            case "update":
                environment.Repository.UpdatedTransform = transform;
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await environment.Service.UpdateAsync(
                        UpdateNameCommand(environment, source, $"contract-{mutation}"),
                        source.ToSnapshot(),
                        accessDecision: null,
                        TestContext.Current.CancellationToken).ConfigureAwait(false));
                break;
            case "revoke":
                environment.Repository.RevokedTransform = transform;
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await environment.Service.RevokeAsync(
                        RevokeCommand(environment, source, $"contract-{mutation}"),
                        source.ToSnapshot(),
                        TestContext.Current.CancellationToken).ConfigureAwait(false));
                break;
            case "rotate":
                environment.Repository.RotatedNewTransform = transform;
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await environment.Service.RotateAsync(
                        RotateCommand(environment, source, $"contract-{mutation}"),
                        source.ToSnapshot(),
                        Authorized(environment),
                        TestContext.Current.CancellationToken).ConfigureAwait(false));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        Assert.Equal(0, environment.UnitOfWorkFactory.CommitCount);
        Assert.Null(environment.Idempotency.Completed);
    }
#pragma warning restore MA0051

    [Theory]
    [InlineData("outer_terminal")]
    [InlineData("outer_status")]
    [InlineData("outer_body")]
    [InlineData("outer_envelope")]
    [InlineData("outer_resource_id")]
    [InlineData("outer_resource_type")]
    [InlineData("headers_kind")]
    [InlineData("headers_count")]
    [InlineData("header_value")]
    [InlineData("cache_control")]
    [InlineData("inner_id")]
    [InlineData("inner_user")]
    [InlineData("inner_group")]
    [InlineData("inner_name")]
    [InlineData("inner_expires")]
    [InlineData("inner_cidrs")]
    [InlineData("inner_secret")]
    [InlineData("inner_prefix")]
    [InlineData("inner_status")]
    [InlineData("inner_version")]
    [InlineData("inner_last_used")]
    [InlineData("inner_created_updated")]
    [InlineData("inner_etag_header")]
    [InlineData("inner_etag_version")]
    [InlineData("inner_location_header")]
    [InlineData("inner_location_expected")]
    public async Task CreateReplayRejectsEveryIndependentEnvelopeCorruption(
        string mutation)
    {
        TestEnvironment source = new();
        CreateApiKeyCommand command = source.Command($"replay-core-{mutation}");
        Result<ApiKeyCreatedOutcome> created = await source.Service.CreateAsync(
            command,
            TestEnvironment.Authorized(command),
            TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess, created.Error.Description);

        (ApiKeyCreateResponseSecret secret, CommandIdempotencyResponse response) =
            CorruptCreateReplay(
                created.Value,
                source.Idempotency.Completed!,
                mutation);
        TestEnvironment replay = new(new FixedApiKeyResponseEnvelope(secret));
        replay.Idempotency.ForcedReplayResponse = response;

        Result<ApiKeyCreatedOutcome> result = await replay.Service.CreateAsync(
            command,
            TestEnvironment.Authorized(command),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Equal(0, replay.Repository.CreateCount);
        Assert.Single(replay.OperationalEvents.Events);
    }

    [Theory]
    [InlineData("outer_terminal")]
    [InlineData("outer_status")]
    [InlineData("outer_body")]
    [InlineData("outer_envelope")]
    [InlineData("outer_resource_id")]
    [InlineData("outer_resource_type")]
    [InlineData("headers_kind")]
    [InlineData("headers_count")]
    [InlineData("cache_control")]
    [InlineData("inner_id")]
    [InlineData("inner_user")]
    [InlineData("inner_status")]
    [InlineData("inner_effective")]
    [InlineData("inner_version")]
    [InlineData("inner_last_used")]
    [InlineData("inner_created_updated")]
    [InlineData("inner_secret")]
    [InlineData("inner_prefix")]
    [InlineData("inner_etag_header")]
    [InlineData("inner_etag_version")]
    [InlineData("inner_location_header")]
    [InlineData("inner_location_expected")]
    public async Task RotateReplayRejectsEveryIndependentEnvelopeCorruption(
        string mutation)
    {
        TestEnvironment source = new();
        ApiKeyResource original = FakeRepository.Resource(
            source.UserId,
            source.GroupId,
            EntityId.New());
        ApiKeyControlPlaneSnapshot snapshot = original.ToSnapshot();
        source.Repository.Items = [original];
        RotateApiKeyCommand command = RotateCommand(
            source,
            original,
            $"replay-rotate-{mutation}");
        Result<ApiKeyCreatedOutcome> rotated = await source.Service.RotateAsync(
            command,
            snapshot,
            Authorized(source),
            TestContext.Current.CancellationToken);
        Assert.True(rotated.IsSuccess, rotated.Error.Description);

        (ApiKeyCreateResponseSecret secret, CommandIdempotencyResponse response) =
            CorruptRotateReplay(
                rotated.Value,
                source.Idempotency.Completed!,
                mutation);
        TestEnvironment replay = new(new FixedApiKeyResponseEnvelope(secret));
        replay.Idempotency.ForcedReplayResponse = response;

        Result<ApiKeyCreatedOutcome> result = await replay.Service.RotateAsync(
            command,
            snapshot,
            Authorized(source),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Equal(0, replay.Repository.RotateCount);
        Assert.Single(replay.OperationalEvents.Events);
    }

    [Theory]
    [InlineData("empty_actor")]
    [InlineData("empty_request")]
    [InlineData("empty_group")]
    [InlineData("self_reason")]
    [InlineData("admin_reason")]
    [InlineData("bad_idempotency")]
    public async Task InvalidCreateCommandFailsBothEntrypointsBeforeUnitOfWork(
        string violation)
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = InvalidCreateCommand(environment, violation);

        Result<ApiKeyCreatedOutcome?> preflight = await environment.Service.TryReplayAsync(
            command,
            TestContext.Current.CancellationToken);
        Result<ApiKeyCreatedOutcome> create = await environment.Service.CreateAsync(
            command,
            AuthorizedCreate(environment),
            TestContext.Current.CancellationToken);

        Assert.True(preflight.IsFailure);
        Assert.Equal(IdentityErrorCodes.ValidationFailed, preflight.Error.Code);
        Assert.True(create.IsFailure);
        Assert.Equal(IdentityErrorCodes.ValidationFailed, create.Error.Code);
        Assert.Equal(0, environment.UnitOfWorkFactory.BeginCount);
    }

    [Theory]
    [InlineData("acquired", null, null)]
    [InlineData("conflict", IdentityErrorCodes.IdempotencyConflict, null)]
    [InlineData("busy", IdentityErrorCodes.CoordinationUnavailable, 1L)]
    public async Task CreatePreflightCoversEveryNonReplayDisposition(
        string disposition,
        string? expectedCode,
        long? retryAfter)
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = environment.Command(
            $"create-preflight-{disposition}");
        environment.Idempotency.ForcedAcquireResult = AcquireResult(disposition);

        Result<ApiKeyCreatedOutcome?> result = await environment.Service.TryReplayAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedCode is null, result.IsSuccess);
        if (expectedCode is null)
        {
            Assert.Null(result.Value);
        }
        else
        {
            Assert.Equal(expectedCode, result.Error.Code);
            Assert.Equal(retryAfter, result.Error.RetryAfterSeconds);
        }
    }

    [Fact]
    public async Task CreatePreflightMissingReplayResponseRollsBackAndAlerts()
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = environment.Command("missing-replay-response");
        environment.Idempotency.ForcedAcquireResult = new CommandIdempotencyAcquireResult(
            CommandIdempotencyDisposition.Replay,
            Lease: null,
            Response: null);

        Result<ApiKeyCreatedOutcome?> result = await environment.Service.TryReplayAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Single(environment.OperationalEvents.Events);
        Assert.Equal(0, environment.UnitOfWorkFactory.CommitCount);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("invalid")]
    public async Task CreateMainPathCoversRemainingAcquireDispositions(string disposition)
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = environment.Command(
            $"create-main-{disposition}");
        environment.Idempotency.ForcedAcquireResult = string.Equals(
            disposition,
            "busy",
            StringComparison.Ordinal)
            ? CommandIdempotencyAcquireResult.Busy
            : new CommandIdempotencyAcquireResult(
                (CommandIdempotencyDisposition)999,
                Lease: null,
                Response: null);

        if (string.Equals(disposition, "busy", StringComparison.Ordinal))
        {
            Result<ApiKeyCreatedOutcome> result = await environment.Service.CreateAsync(
                command,
                TestEnvironment.Authorized(command),
                TestContext.Current.CancellationToken);
            Assert.True(result.IsFailure);
            Assert.Equal(IdentityErrorCodes.CoordinationUnavailable, result.Error.Code);
            Assert.Equal(1, result.Error.RetryAfterSeconds);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                async () => await environment.Service.CreateAsync(
                    command,
                    TestEnvironment.Authorized(command),
                    TestContext.Current.CancellationToken).ConfigureAwait(false));
        }
    }

    [Theory]
    [InlineData("required", "subscription_required")]
    [InlineData("invalid", null)]
    public async Task CreateAccessFailureCoversRequiredAndInvalidKinds(
        string decisionKind,
        string? expectedCode)
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = environment.Command(
            $"create-access-{decisionKind}");
        ApiKeyAccessDecision decision = new(
            string.Equals(decisionKind, "required", StringComparison.Ordinal)
                ? ApiKeyAccessDecisionKind.SubscriptionRequired
                : (ApiKeyAccessDecisionKind)999,
            command.UserId,
            command.GroupId,
            SubscriptionId: null,
            ObservedAt: null);

        if (expectedCode is not null)
        {
            Result<ApiKeyCreatedOutcome> result = await environment.Service.CreateAsync(
                command,
                decision,
                TestContext.Current.CancellationToken);
            Assert.True(result.IsFailure);
            Assert.Equal(expectedCode, result.Error.Code);
            Assert.Equal(403, result.Error.Presentation!.Status);
            Assert.Equal(1, environment.UnitOfWorkFactory.CommitCount);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                async () => await environment.Service.CreateAsync(
                    command,
                    decision,
                    TestContext.Current.CancellationToken).ConfigureAwait(false));
        }
    }

    [Theory]
    [InlineData("wrong_user")]
    [InlineData("wrong_group")]
    public async Task CreateRejectsAccessDecisionForAnotherTarget(string mismatch)
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = environment.Command(
            $"decision-target-{mismatch}");
        ApiKeyAccessDecision decision = new(
            ApiKeyAccessDecisionKind.Authorized,
            string.Equals(mismatch, "wrong_user", StringComparison.Ordinal)
                ? EntityId.New()
                : command.UserId,
            string.Equals(mismatch, "wrong_group", StringComparison.Ordinal)
                ? EntityId.New()
                : command.GroupId,
            EntityId.New(),
            TestTimestamp());

        Result<ApiKeyCreatedOutcome> result = await environment.Service.CreateAsync(
            command,
            decision,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Equal(0, environment.UnitOfWorkFactory.BeginCount);
    }

    [Fact]
    public async Task GetRejectsUnauthorizedAndMismatchedRepositoryResources()
    {
        TestEnvironment unauthorized = new();
        Result<ApiKeyControlPlaneSnapshot> denied = await unauthorized.Service.GetAsync(
            new GetApiKeyQuery(
                unauthorized.Actor,
                ApiKeyAccessMode.Self,
                EntityId.New(),
                EntityId.New()),
            TestContext.Current.CancellationToken);
        Assert.True(denied.IsFailure);
        Assert.Equal(IdentityErrorCodes.RoleRequired, denied.Error.Code);

        TestEnvironment mismatch = new();
        EntityId requestedId = EntityId.New();
        mismatch.Repository.UseForcedGetResult = true;
        mismatch.Repository.ForcedGetResult = FakeRepository.Resource(
            mismatch.UserId,
            mismatch.GroupId,
            EntityId.New());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await mismatch.Service.GetAsync(
                new GetApiKeyQuery(
                    mismatch.Actor,
                    ApiKeyAccessMode.Self,
                    mismatch.UserId,
                    requestedId),
                TestContext.Current.CancellationToken).ConfigureAwait(false));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("unit_of_work")]
    [InlineData("idempotency")]
    [InlineData("audit")]
    [InlineData("credential")]
    [InlineData("envelope")]
    [InlineData("events")]
    [InlineData("policy")]
    public void ConstructorRejectsEveryNullDependency(string dependency)
    {
        TestEnvironment environment = new();

        Assert.Throws<ArgumentNullException>(
            () => CreateServiceWithNullDependency(environment, dependency));
    }

    [Theory]
    [InlineData("response_status")]
    [InlineData("title")]
    [InlineData("detail")]
    [InlineData("retryable")]
    [InlineData("retry_after")]
    [InlineData("errors_added")]
    [InlineData("envelope")]
    [InlineData("resource_type")]
    [InlineData("resource_id")]
    [InlineData("headers")]
    [InlineData("validation_errors_null")]
    [InlineData("validation_errors_count")]
    [InlineData("validation_errors_key")]
    [InlineData("validation_errors_value")]
    [InlineData("unsupported_required")]
    [InlineData("unsupported_inactive")]
    [InlineData("unsupported_validation")]
    [InlineData("unsupported_conflict")]
    [InlineData("unsupported_unknown")]
    public async Task CreateFailureReplayRejectsEveryPresentationCorruption(
        string mutation)
    {
        bool validation = mutation.StartsWith(
            "validation_",
            StringComparison.Ordinal);
        TestEnvironment source = new();
        CreateApiKeyCommand command = source.Command(
            $"create-failure-replay-{mutation}");
        source.Repository.ForcedCreateResult = new ApiKeyCreateResult(
            validation
                ? ApiKeyCreateDisposition.ValidationFailed
                : ApiKeyCreateDisposition.Conflict,
            ApiKey: null);
        Result<ApiKeyCreatedOutcome> failed = await source.Service.CreateAsync(
            command,
            TestEnvironment.Authorized(command),
            TestContext.Current.CancellationToken);
        Assert.True(failed.IsFailure);

        TestEnvironment replay = new();
        replay.Idempotency.ForcedReplayResponse = CorruptFailureReplay(
            source.Idempotency.Completed!,
            mutation,
            mutationPresentation: false);
        Result<ApiKeyCreatedOutcome> result = await replay.Service.CreateAsync(
            command,
            TestEnvironment.Authorized(command),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Single(replay.OperationalEvents.Events);
    }

    [Theory]
    [InlineData("description")]
    [InlineData("response_status")]
    [InlineData("title")]
    [InlineData("detail")]
    [InlineData("retryable")]
    [InlineData("retry_after")]
    [InlineData("errors_added")]
    [InlineData("envelope")]
    [InlineData("resource_type")]
    [InlineData("resource_id")]
    [InlineData("headers")]
    [InlineData("validation_errors_null")]
    [InlineData("validation_errors_count")]
    [InlineData("validation_errors_key")]
    [InlineData("validation_errors_value")]
    [InlineData("version_headers_kind")]
    [InlineData("version_headers_count")]
    [InlineData("version_header_value")]
    [InlineData("version_etag_short")]
    [InlineData("version_etag_quote")]
    [InlineData("version_etag_marker")]
    [InlineData("version_etag_digit")]
    [InlineData("version_etag_text")]
    [InlineData("version_etag_zero")]
    [InlineData("unsupported_required")]
    [InlineData("unsupported_inactive")]
    [InlineData("unsupported_not_found")]
    [InlineData("unsupported_revoked")]
    [InlineData("unsupported_conflict")]
    [InlineData("unsupported_version")]
    [InlineData("unsupported_validation")]
    [InlineData("unsupported_invalid")]
    [InlineData("unsupported_unknown")]
    public async Task MutationFailureReplayRejectsEveryPresentationAndHeaderCorruption(
        string mutation)
    {
        bool validation = mutation.StartsWith(
            "validation_",
            StringComparison.Ordinal);
        bool version = mutation.StartsWith("version_", StringComparison.Ordinal);
        TestEnvironment source = new();
        ApiKeyResource resource = FakeRepository.Resource(
            source.UserId,
            source.GroupId,
            EntityId.New());
        source.Repository.Items = [resource];
        source.Repository.ForcedUpdateResult = new ApiKeyUpdateResult(
            validation
                ? ApiKeyUpdateDisposition.ValidationFailed
                : version
                    ? ApiKeyUpdateDisposition.VersionConflict
                    : ApiKeyUpdateDisposition.NotFound,
            Changed: false,
            CurrentVersion: version ? 7 : null,
            ApiKey: null);
        UpdateApiKeyCommand command = UpdateNameCommand(
            source,
            resource,
            $"failure-replay-{mutation}");
        Result<ApiKeyUpdatedOutcome> failed = await source.Service.UpdateAsync(
            command,
            resource.ToSnapshot(),
            accessDecision: null,
            TestContext.Current.CancellationToken);
        Assert.True(failed.IsFailure);

        TestEnvironment replay = new();
        replay.Idempotency.ForcedReplayResponse = CorruptFailureReplay(
            source.Idempotency.Completed!,
            mutation,
            mutationPresentation: true);
        Result<ApiKeyUpdatedOutcome?> result =
            await replay.Service.TryReplayUpdateAsync(
                command,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(IdentityErrorCodes.DependencyUnavailable, result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Single(replay.OperationalEvents.Events);
    }

    [Theory]
    [InlineData(SystemRole.Operator)]
    [InlineData(SystemRole.Auditor)]
    public async Task SelfCreateMapsEveryNonAdministrativeAuditActor(SystemRole role)
    {
        TestEnvironment environment = new();
        CreateApiKeyCommand command = environment.Command($"audit-role-{role}") with
        {
            Actor = new ApiKeyActor(environment.UserId, role, TokenVersion: 1),
        };

        Result<ApiKeyCreatedOutcome> result = await environment.Service.CreateAsync(
            command,
            TestEnvironment.Authorized(command),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error.Description);
        Assert.Single(environment.Audit.Entries);
    }

    private static CreateApiKeyCommand InvalidCreateCommand(
        TestEnvironment environment,
        string violation)
    {
        CreateApiKeyCommand command = environment.Command(
            $"invalid-create-{violation}");
        return violation switch
        {
            "empty_actor" => command with
            {
                Actor = command.Actor with
                {
                    UserId = default,
                },
            },
            "empty_request" => command with
            {
                RequestId = default,
            },
            "empty_group" => command with
            {
                GroupId = default,
            },
            "self_reason" => command with
            {
                Reason = "unexpected self reason",
            },
            "admin_reason" => command with
            {
                Actor = new ApiKeyActor(
                    EntityId.New(),
                    SystemRole.Admin,
                    TokenVersion: 1),
                AccessMode = ApiKeyAccessMode.AdminProxy,
                Reason = null,
            },
            "bad_idempotency" => command with
            {
                IdempotencyKey = string.Empty,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(violation)),
        };
    }

    private static ApiKeyUseCaseService CreateServiceWithNullDependency(
        TestEnvironment environment,
        string dependency)
    {
        IApiKeyRepository repository = string.Equals(
            dependency,
            "repository",
            StringComparison.Ordinal)
            ? null!
            : environment.Repository;
        IUnitOfWorkFactory unitOfWorkFactory = string.Equals(
            dependency,
            "unit_of_work",
            StringComparison.Ordinal)
            ? null!
            : environment.UnitOfWorkFactory;
        ICommandIdempotencyStore idempotency = string.Equals(
            dependency,
            "idempotency",
            StringComparison.Ordinal)
            ? null!
            : environment.Idempotency;
        IAuditAppender audit = string.Equals(
            dependency,
            "audit",
            StringComparison.Ordinal)
            ? null!
            : environment.Audit;
        IApiKeyCredentialService credential = string.Equals(
            dependency,
            "credential",
            StringComparison.Ordinal)
            ? null!
            : environment.Credentials;
        IApiKeyCreateResponseEnvelope envelope = string.Equals(
            dependency,
            "envelope",
            StringComparison.Ordinal)
            ? null!
            : environment.ResponseEnvelope;
        IOperationalEventWriter events = string.Equals(
            dependency,
            "events",
            StringComparison.Ordinal)
            ? null!
            : environment.OperationalEvents;
        IdentityPolicy policy = string.Equals(
            dependency,
            "policy",
            StringComparison.Ordinal)
            ? null!
            : environment.Policy;
        return new ApiKeyUseCaseService(
            repository,
            unitOfWorkFactory,
            idempotency,
            audit,
            credential,
            envelope,
            events,
            policy);
    }

#pragma warning disable MA0051 // One mutation name represents one stored-failure contract clause.
    private static CommandIdempotencyResponse CorruptFailureReplay(
        CommandIdempotencyResponse stored,
        string mutation,
        bool mutationPresentation)
    {
        CommandIdempotencyResponse response = stored;
        JsonObject body = JsonNode.Parse(
            stored.Body!.Value.GetRawText())!.AsObject();
        JsonObject presentation = RequiredObject(body, "Presentation");
        switch (mutation)
        {
            case "description":
                SetProperty(body, "Description", " ");
                break;
            case "response_status":
                response = response with
                {
                    Status = stored.Status + 1,
                };
                break;
            case "title":
                SetProperty(presentation, "Title", "Corrupted title");
                break;
            case "detail":
                SetProperty(presentation, "Detail", "Corrupted detail");
                break;
            case "retryable":
                SetProperty(presentation, "Retryable", true);
                break;
            case "retry_after":
                SetProperty(presentation, "RetryAfterSeconds", 9);
                break;
            case "errors_added":
                SetProperty(
                    presentation,
                    "Errors",
                    new JsonObject
                    {
                        ["/unexpected"] = new JsonArray("unexpected"),
                    });
                break;
            case "envelope":
                response = response with
                {
                    BodyEnvelope = JsonSerializer.SerializeToElement(
                        new { invalid = true }),
                };
                break;
            case "resource_type":
                response = response with
                {
                    ResourceType = "api_key",
                };
                break;
            case "resource_id":
                response = response with
                {
                    ResourceId = EntityId.New(),
                };
                break;
            case "headers":
                response = response with
                {
                    Headers = JsonSerializer.SerializeToElement(
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["Unexpected"] = "value",
                        }),
                };
                break;
            case "validation_errors_null":
                SetProperty(presentation, "Errors", value: null);
                break;
            case "validation_errors_count":
            {
                JsonObject errors = RequiredObject(presentation, "Errors");
                errors["/extra"] = new JsonArray("extra");
                break;
            }
            case "validation_errors_key":
            {
                JsonObject errors = RequiredObject(presentation, "Errors");
                KeyValuePair<string, JsonNode?> first = errors.First();
                errors.Remove(first.Key);
                errors["/different"] = first.Value?.DeepClone();
                break;
            }
            case "validation_errors_value":
            {
                JsonObject errors = RequiredObject(presentation, "Errors");
                string firstKey = errors.First().Key;
                errors[firstKey] = new JsonArray("different");
                break;
            }
            case "version_headers_kind":
                response = response with
                {
                    Headers = JsonSerializer.SerializeToElement("invalid"),
                };
                break;
            case "version_headers_count":
                response = response with
                {
                    Headers = JsonSerializer.SerializeToElement(
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["ETag"] = "\"v7\"",
                            ["Extra"] = "value",
                        }),
                };
                break;
            case "version_header_value":
                response = response with
                {
                    Headers = JsonSerializer.SerializeToElement(
                        new Dictionary<string, int>(StringComparer.Ordinal)
                        {
                            ["ETag"] = 7,
                        }),
                };
                break;
            case "version_etag_short":
                response = response with
                {
                    Headers = MutationReplayHeaders("x"),
                };
                break;
            case "version_etag_quote":
                response = response with
                {
                    Headers = MutationReplayHeaders("\"v7"),
                };
                break;
            case "version_etag_marker":
                response = response with
                {
                    Headers = MutationReplayHeaders("\"x7\""),
                };
                break;
            case "version_etag_digit":
            case "version_etag_zero":
                response = response with
                {
                    Headers = MutationReplayHeaders("\"v0\""),
                };
                break;
            case "version_etag_text":
                response = response with
                {
                    Headers = MutationReplayHeaders("\"v7x\""),
                };
                break;
            default:
                ApplyUnsupportedPresentation(
                    presentation,
                    mutation,
                    mutationPresentation,
                    ref response);
                break;
        }

        return response with
        {
            Body = JsonSerializer.SerializeToElement(body),
        };
    }
#pragma warning restore MA0051

    private static void ApplyUnsupportedPresentation(
        JsonObject presentation,
        string mutation,
        bool mutationPresentation,
        ref CommandIdempotencyResponse response)
    {
        (string code, int status) = (mutation, mutationPresentation) switch
        {
            ("unsupported_required", _) => ("subscription_required", 400),
            ("unsupported_inactive", _) => ("subscription_inactive", 400),
            ("unsupported_not_found", true) =>
                (IdentityErrorCodes.ResourceNotFound, 400),
            ("unsupported_revoked", true) => ("api_key_revoked", 400),
            ("unsupported_conflict", _) =>
                (IdentityErrorCodes.ResourceConflict, 400),
            ("unsupported_version", true) =>
                (IdentityErrorCodes.VersionConflict, 400),
            ("unsupported_validation", false) =>
                (IdentityErrorCodes.ValidationFailed, 400),
            ("unsupported_validation", true) =>
                (IdentityErrorCodes.ValidationFailed, 409),
            ("unsupported_invalid", true) =>
                (IdentityErrorCodes.InvalidRequest, 422),
            ("unsupported_unknown", _) => ("unknown_failure", 499),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        SetProperty(presentation, "Code", code);
        SetProperty(presentation, "Status", status);
        response = response with
        {
            Status = status,
        };
    }

    private static JsonElement MutationReplayHeaders(string etag) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = etag,
            });

    private static JsonObject RequiredObject(JsonObject parent, string name)
    {
        string key = parent
            .Select(static pair => pair.Key)
            .Single(value => string.Equals(
                value,
                name,
                StringComparison.OrdinalIgnoreCase));
        return parent[key]!.AsObject();
    }

    private static void SetProperty(
        JsonObject parent,
        string name,
        JsonNode? value)
    {
        string key = parent
            .Select(static pair => pair.Key)
            .Single(item => string.Equals(
                item,
                name,
                StringComparison.OrdinalIgnoreCase));
        parent[key] = value;
    }

#pragma warning disable MA0051 // Each corruption maps one-to-one to a ReplayCore contract clause.
    private static (
        ApiKeyCreateResponseSecret Secret,
        CommandIdempotencyResponse Response) CorruptCreateReplay(
        ApiKeyCreatedOutcome outcome,
        CommandIdempotencyResponse stored,
        string mutation)
    {
        ApiKeyControlPlaneSnapshot snapshot = outcome.ApiKey;
        ApiKeyCreateResponseSecret secret = new(
            snapshot,
            outcome.Secret,
            outcome.ETag,
            outcome.Location);
        if (IsOuterReplayMutation(mutation))
        {
            return (secret, CorruptOuterReplay(stored, outcome, mutation));
        }

        secret = mutation switch
        {
            "inner_id" => secret with
            {
                ApiKey = snapshot with
                {
                    ApiKeyId = EntityId.New(),
                },
            },
            "inner_user" => secret with
            {
                ApiKey = snapshot with
                {
                    UserId = EntityId.New(),
                },
            },
            "inner_group" => secret with
            {
                ApiKey = snapshot with
                {
                    GroupId = EntityId.New(),
                },
            },
            "inner_name" => secret with
            {
                ApiKey = snapshot with
                {
                    Name = "Different replay name",
                },
            },
            "inner_expires" => secret with
            {
                ApiKey = snapshot with
                {
                    ExpiresAt = snapshot.ObservedAt.AddYears(1),
                },
            },
            "inner_cidrs" => secret with
            {
                ApiKey = snapshot with
                {
                    AllowedCidrs = ["198.51.100.0/24"],
                },
            },
            "inner_secret" => secret with
            {
                Secret = "not-an-api-key",
            },
            "inner_prefix" => secret with
            {
                ApiKey = snapshot with
                {
                    Prefix = "sk-unit-BBBBBBBB",
                },
            },
            "inner_status" => secret with
            {
                ApiKey = snapshot with
                {
                    Status = ApiKeyPersistentStatus.Disabled,
                    EffectiveStatus = ApiKeyEffectiveStatus.Disabled,
                },
            },
            "inner_version" => secret with
            {
                ApiKey = snapshot with
                {
                    Version = 2,
                },
            },
            "inner_last_used" => secret with
            {
                ApiKey = snapshot with
                {
                    LastUsedAt = snapshot.CreatedAt,
                },
            },
            "inner_created_updated" => secret with
            {
                ApiKey = snapshot with
                {
                    UpdatedAt = snapshot.UpdatedAt.AddMilliseconds(1),
                    ObservedAt = snapshot.ObservedAt.AddMilliseconds(1),
                },
            },
            "inner_etag_header" => secret with
            {
                ETag = "\"v9\"",
            },
            "inner_etag_version" => secret with
            {
                ETag = "\"v9\"",
            },
            "inner_location_header" => secret with
            {
                Location = "/api/v1/me/api-keys/00000000-0000-0000-0000-000000000001",
            },
            "inner_location_expected" => secret with
            {
                Location = "/api/v1/me/api-keys/00000000-0000-0000-0000-000000000001",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        CommandIdempotencyResponse response = mutation switch
        {
            "inner_etag_version" => stored with
            {
                Headers = ReplayHeaders("\"v9\"", outcome.Location),
            },
            "inner_location_expected" => stored with
            {
                Headers = ReplayHeaders(secret.ETag, secret.Location),
            },
            _ => stored,
        };
        return (secret, response);
    }
#pragma warning restore MA0051

#pragma warning disable MA0051 // Each corruption maps one-to-one to a ReplayRotate contract clause.
    private static (
        ApiKeyCreateResponseSecret Secret,
        CommandIdempotencyResponse Response) CorruptRotateReplay(
        ApiKeyCreatedOutcome outcome,
        CommandIdempotencyResponse stored,
        string mutation)
    {
        ApiKeyControlPlaneSnapshot snapshot = outcome.ApiKey;
        ApiKeyCreateResponseSecret secret = new(
            snapshot,
            outcome.Secret,
            outcome.ETag,
            outcome.Location);
        if (IsOuterReplayMutation(mutation))
        {
            return (secret, CorruptOuterReplay(stored, outcome, mutation));
        }

        secret = mutation switch
        {
            "inner_id" => secret with
            {
                ApiKey = snapshot with
                {
                    ApiKeyId = EntityId.New(),
                },
            },
            "inner_user" => secret with
            {
                ApiKey = snapshot with
                {
                    UserId = EntityId.New(),
                },
            },
            "inner_status" => secret with
            {
                ApiKey = snapshot with
                {
                    Status = ApiKeyPersistentStatus.Disabled,
                    EffectiveStatus = ApiKeyEffectiveStatus.Disabled,
                },
            },
            "inner_effective" => secret with
            {
                ApiKey = snapshot with
                {
                    EffectiveStatus = ApiKeyEffectiveStatus.Disabled,
                },
            },
            "inner_version" => secret with
            {
                ApiKey = snapshot with
                {
                    Version = 2,
                },
            },
            "inner_last_used" => secret with
            {
                ApiKey = snapshot with
                {
                    LastUsedAt = snapshot.CreatedAt,
                },
            },
            "inner_created_updated" => secret with
            {
                ApiKey = snapshot with
                {
                    UpdatedAt = snapshot.UpdatedAt.AddMilliseconds(1),
                    ObservedAt = snapshot.ObservedAt.AddMilliseconds(1),
                },
            },
            "inner_secret" => secret with
            {
                Secret = "not-an-api-key",
            },
            "inner_prefix" => secret with
            {
                ApiKey = snapshot with
                {
                    Prefix = "sk-unit-BBBBBBBB",
                },
            },
            "inner_etag_header" => secret with
            {
                ETag = "\"v9\"",
            },
            "inner_etag_version" => secret with
            {
                ETag = "\"v9\"",
            },
            "inner_location_header" => secret with
            {
                Location = "/api/v1/me/api-keys/00000000-0000-0000-0000-000000000001",
            },
            "inner_location_expected" => secret with
            {
                Location = "/api/v1/me/api-keys/00000000-0000-0000-0000-000000000001",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        CommandIdempotencyResponse response = mutation switch
        {
            "inner_etag_version" => stored with
            {
                Headers = ReplayHeaders("\"v9\"", outcome.Location),
            },
            "inner_location_expected" => stored with
            {
                Headers = ReplayHeaders(secret.ETag, secret.Location),
            },
            _ => stored,
        };
        return (secret, response);
    }
#pragma warning restore MA0051

    private static bool IsOuterReplayMutation(string mutation) =>
        mutation.StartsWith("outer_", StringComparison.Ordinal)
        || mutation.StartsWith("headers_", StringComparison.Ordinal)
        || mutation is "header_value" or "cache_control";

    private static CommandIdempotencyResponse CorruptOuterReplay(
        CommandIdempotencyResponse stored,
        ApiKeyCreatedOutcome outcome,
        string mutation) => mutation switch
    {
        "outer_terminal" => stored with
        {
            TerminalStatus = (CommandIdempotencyTerminalStatus)999,
        },
        "outer_status" => stored with
        {
            Status = 202,
        },
        "outer_body" => stored with
        {
            Body = JsonSerializer.SerializeToElement(new { invalid = true }),
        },
        "outer_envelope" => stored with
        {
            BodyEnvelope = null,
        },
        "outer_resource_id" => stored with
        {
            ResourceId = null,
        },
        "outer_resource_type" => stored with
        {
            ResourceType = "wrong-resource",
        },
        "headers_kind" => stored with
        {
            Headers = JsonSerializer.SerializeToElement("invalid"),
        },
        "headers_count" => stored with
        {
            Headers = JsonSerializer.SerializeToElement(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ETag"] = outcome.ETag,
                }),
        },
        "header_value" => stored with
        {
            Headers = JsonSerializer.SerializeToElement(
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ETag"] = 1,
                    ["Location"] = outcome.Location,
                    ["Cache-Control"] = "no-store",
                }),
        },
        "cache_control" => stored with
        {
            Headers = ReplayHeaders(
                outcome.ETag,
                outcome.Location,
                cacheControl: "public"),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
    };

    private static JsonElement ReplayHeaders(
        string etag,
        string location,
        string cacheControl = "no-store") =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = etag,
                ["Location"] = location,
                ["Cache-Control"] = cacheControl,
            });

    private static ApiKeyResource CorruptRepositoryResource(
        ApiKeyResource value,
        string mutation,
        bool isFreshResource) => mutation switch
    {
        "id" => value with
        {
            Id = EntityId.New(),
        },
        "user" => value with
        {
            UserId = EntityId.New(),
        },
        "group" => value with
        {
            GroupId = EntityId.New(),
        },
        "name" => value with
        {
            Name = "Different valid name",
        },
        "prefix" => value with
        {
            Prefix = "sk-unit-BBBBBBBB",
        },
        "status" => value with
        {
            Status = ApiKeyPersistentStatus.Disabled,
            EffectiveStatus = ApiKeyEffectiveStatus.Disabled,
        },
        _ => CorruptRepositoryState(value, mutation, isFreshResource),
    };

    private static ApiKeyResource CorruptRepositoryState(
        ApiKeyResource value,
        string mutation,
        bool isFreshResource) => mutation switch
    {
        "expires" => value with
        {
            ExpiresAt = TestTimestamp().AddYears(10),
            EffectiveStatus = value.Status == ApiKeyPersistentStatus.Revoked
                ? ApiKeyEffectiveStatus.Revoked
                : value.Status == ApiKeyPersistentStatus.Disabled
                    ? ApiKeyEffectiveStatus.Disabled
                    : ApiKeyEffectiveStatus.Active,
        },
        "cidrs" => value with
        {
            AllowedCidrs = ["198.51.100.0/24"],
        },
        "last_used" => value with
        {
            LastUsedAt = value.CreatedAt,
        },
        "version" => value with
        {
            Version = value.Version + 10,
        },
        "created" => value with
        {
            CreatedAt = value.CreatedAt.AddDays(-1),
        },
        "updated" when isFreshResource => value with
        {
            UpdatedAt = value.UpdatedAt.AddMilliseconds(1),
            ObservedAt = value.ObservedAt.AddMilliseconds(1),
        },
        "updated" => value with
        {
            UpdatedAt = value.CreatedAt,
        },
        "rotation_time" => value with
        {
            CreatedAt = value.CreatedAt.AddMilliseconds(1),
            UpdatedAt = value.UpdatedAt.AddMilliseconds(1),
            ObservedAt = value.ObservedAt.AddMilliseconds(1),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
    };

    private static CommandIdempotencyAcquireResult AcquireResult(
        string disposition) => disposition switch
    {
        "acquired" => CommandIdempotencyAcquireResult.Acquired(
            new CommandIdempotencyLease(
                "test-scope",
                "test-key",
                EntityId.New(),
                Generation: 1,
                Version: 1)),
        "conflict" => CommandIdempotencyAcquireResult.Conflict,
        "busy" => CommandIdempotencyAcquireResult.Busy,
        _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
    };

    private static ApiKeyAccessDecision AuthorizedCreate(
        TestEnvironment environment) => new(
        ApiKeyAccessDecisionKind.Authorized,
        environment.UserId,
        environment.GroupId,
        EntityId.New(),
        TestTimestamp());

    private static ApiKeyResource RevokedResource(ApiKeyResource source)
    {
        DateTimeOffset changedAt = source.UpdatedAt.AddSeconds(1);
        return source with
        {
            Status = ApiKeyPersistentStatus.Revoked,
            EffectiveStatus = ApiKeyEffectiveStatus.Revoked,
            Version = source.Version + 1,
            UpdatedAt = changedAt,
            ObservedAt = changedAt,
        };
    }

    private static async ValueTask<ResultError?> InvokeMutationPreflightResultAsync(
        TestEnvironment environment,
        ApiKeyResource source,
        string operation)
    {
        switch (operation)
        {
            case "update":
            {
                Result<ApiKeyUpdatedOutcome?> result =
                    await environment.Service.TryReplayUpdateAsync(
                        UpdateNameCommand(environment, source, "disposition"),
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                return result.IsFailure ? result.Error : null;
            }

            case "revoke":
            {
                Result<ApiKeyRevokedOutcome?> result =
                    await environment.Service.TryReplayRevokeAsync(
                        RevokeCommand(environment, source, "disposition"),
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                return result.IsFailure ? result.Error : null;
            }

            case "rotate":
            {
                Result<ApiKeyCreatedOutcome?> result =
                    await environment.Service.TryReplayRotateAsync(
                        RotateCommand(environment, source, "disposition"),
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                return result.IsFailure ? result.Error : null;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static async ValueTask<ResultError> InvokeMutationMainFailureAsync(
        TestEnvironment environment,
        ApiKeyResource source,
        string operation,
        ApiKeyControlPlaneSnapshot? suppliedSnapshot = null)
    {
        ApiKeyControlPlaneSnapshot snapshot = suppliedSnapshot ?? source.ToSnapshot();
        switch (operation)
        {
            case "update":
            {
                Result<ApiKeyUpdatedOutcome> result =
                    await environment.Service.UpdateAsync(
                        UpdateNameCommand(environment, source, "main"),
                        snapshot,
                        accessDecision: null,
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                Assert.True(result.IsFailure);
                return result.Error;
            }

            case "revoke":
            {
                Result<ApiKeyRevokedOutcome> result =
                    await environment.Service.RevokeAsync(
                        RevokeCommand(environment, source, "main"),
                        snapshot,
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                Assert.True(result.IsFailure);
                return result.Error;
            }

            case "rotate":
            {
                Result<ApiKeyCreatedOutcome> result =
                    await environment.Service.RotateAsync(
                        RotateCommand(environment, source, "main"),
                        snapshot,
                        Authorized(environment),
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                Assert.True(result.IsFailure);
                return result.Error;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

#pragma warning disable MA0051 // Invalid command cases deliberately exercise both public entry points.
    private static async ValueTask AssertInvalidMutationBothAsync(
        TestEnvironment environment,
        ApiKeyResource source,
        string operation,
        string violation)
    {
        switch (operation)
        {
            case "update":
            {
                UpdateApiKeyCommand command = InvalidUpdateCommand(
                    environment,
                    source,
                    violation);
                Result<ApiKeyUpdatedOutcome?> preflight =
                    await environment.Service.TryReplayUpdateAsync(
                        command,
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                Result<ApiKeyUpdatedOutcome> main =
                    await environment.Service.UpdateAsync(
                        command,
                        source.ToSnapshot(),
                        accessDecision: null,
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                Assert.True(preflight.IsFailure);
                Assert.True(main.IsFailure);
                Assert.Equal(IdentityErrorCodes.ValidationFailed, preflight.Error.Code);
                Assert.Equal(IdentityErrorCodes.ValidationFailed, main.Error.Code);
                break;
            }

            case "revoke":
            {
                RevokeApiKeyCommand command = RevokeCommand(
                    environment,
                    source,
                    "invalid") with
                {
                    Reason = " ",
                };
                Result<ApiKeyRevokedOutcome?> preflight =
                    await environment.Service.TryReplayRevokeAsync(
                        command,
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                Result<ApiKeyRevokedOutcome> main =
                    await environment.Service.RevokeAsync(
                        command,
                        source.ToSnapshot(),
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                Assert.True(preflight.IsFailure);
                Assert.True(main.IsFailure);
                Assert.Equal(IdentityErrorCodes.InvalidRequest, preflight.Error.Code);
                Assert.Equal(IdentityErrorCodes.InvalidRequest, main.Error.Code);
                break;
            }

            case "rotate":
            {
                RotateApiKeyCommand command = RotateCommand(
                    environment,
                    source,
                    "invalid") with
                {
                    Reason = " ",
                };
                Result<ApiKeyCreatedOutcome?> preflight =
                    await environment.Service.TryReplayRotateAsync(
                        command,
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                Result<ApiKeyCreatedOutcome> main =
                    await environment.Service.RotateAsync(
                        command,
                        source.ToSnapshot(),
                        Authorized(environment),
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                Assert.True(preflight.IsFailure);
                Assert.True(main.IsFailure);
                Assert.Equal(IdentityErrorCodes.ValidationFailed, preflight.Error.Code);
                Assert.Equal(IdentityErrorCodes.ValidationFailed, main.Error.Code);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }
#pragma warning restore MA0051

#pragma warning disable MA0051 // The switch is the readable executable invalid-field matrix.
    private static UpdateApiKeyCommand InvalidUpdateCommand(
        TestEnvironment environment,
        ApiKeyResource source,
        string violation)
    {
        UpdateApiKeyCommand command = UpdateNameCommand(
            environment,
            source,
            $"invalid-{violation}");
        return violation switch
        {
            "no_fields" => command with
            {
                SetName = false,
                Name = null,
            },
            "name_null" => command with
            {
                Name = null,
            },
            "name_omitted" => command with
            {
                SetName = false,
                Name = "Unexpected",
            },
            "status_null" => command with
            {
                SetName = false,
                Name = null,
                SetStatus = true,
                Status = null,
            },
            "status_revoked" => command with
            {
                SetName = false,
                Name = null,
                SetStatus = true,
                Status = ApiKeyPersistentStatus.Revoked,
            },
            "status_omitted" => command with
            {
                Status = ApiKeyPersistentStatus.Active,
            },
            "expires_omitted" => command with
            {
                ExpiresAt = TestTimestamp().AddDays(1),
            },
            "cidrs_null" => command with
            {
                SetAllowedCidrs = true,
                AllowedCidrs = null,
            },
            "cidrs_omitted" => command with
            {
                AllowedCidrs = ["192.0.2.0/24"],
            },
            "self_reason" => command with
            {
                Reason = "unexpected",
            },
            "empty_actor" => command with
            {
                Actor = command.Actor with
                {
                    UserId = default,
                },
            },
            "empty_user" => command with
            {
                UserId = default,
            },
            "empty_api_key" => command with
            {
                ApiKeyId = default,
            },
            "empty_request" => command with
            {
                RequestId = default,
            },
            "zero_version" => command with
            {
                ExpectedVersion = 0,
            },
            "bad_idempotency" => command with
            {
                IdempotencyKey = string.Empty,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(violation)),
        };
    }
#pragma warning restore MA0051

    private static ApiKeyUpdateDisposition UpdateDisposition(string value) => value switch
    {
        "not_found" => ApiKeyUpdateDisposition.NotFound,
        "revoked" => ApiKeyUpdateDisposition.Revoked,
        "version_conflict" => ApiKeyUpdateDisposition.VersionConflict,
        "resource_conflict" => ApiKeyUpdateDisposition.ResourceConflict,
        "validation_failed" => ApiKeyUpdateDisposition.ValidationFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static ApiKeyRevokeDisposition RevokeDisposition(string value) => value switch
    {
        "not_found" => ApiKeyRevokeDisposition.NotFound,
        "already_revoked" => ApiKeyRevokeDisposition.AlreadyRevoked,
        "version_conflict" => ApiKeyRevokeDisposition.VersionConflict,
        "validation_failed" => ApiKeyRevokeDisposition.ValidationFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static ApiKeyRotateDisposition RotateDisposition(string value) => value switch
    {
        "not_found" => ApiKeyRotateDisposition.NotFound,
        "revoked" => ApiKeyRotateDisposition.Revoked,
        "version_conflict" => ApiKeyRotateDisposition.VersionConflict,
        "resource_conflict" => ApiKeyRotateDisposition.ResourceConflict,
        "conflict" => ApiKeyRotateDisposition.Conflict,
        "validation_failed" => ApiKeyRotateDisposition.ValidationFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static void AssertMutationFailure(
        ResultError error,
        string expectedCode,
        int expectedStatus,
        string? expectedEtag)
    {
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedEtag, error.ETag);
        Assert.Null(error.RetryAfterSeconds);
        Assert.NotNull(error.Presentation);
        Assert.Equal(expectedStatus, error.Presentation.Status);
        Assert.Equal(expectedCode, error.Presentation.Code);
        if (string.Equals(
                expectedCode,
                IdentityErrorCodes.ValidationFailed,
                StringComparison.Ordinal))
        {
            Assert.NotNull(error.Presentation.Errors);
            Assert.Equal(
                ["The request failed application validation."],
                error.Presentation.Errors["/"]);
        }
        else
        {
            Assert.Null(error.Presentation.Errors);
        }
    }

    private static UpdateApiKeyCommand UpdateNameCommand(
        TestEnvironment environment,
        ApiKeyResource source,
        string suffix) => new(
        EntityId.New(),
        environment.Actor,
        ApiKeyAccessMode.Self,
        environment.UserId,
        source.Id,
        $"api-key-update-{suffix}-{Guid.NewGuid():N}",
        ExpectedVersion: 1,
        SetName: true,
        Name: $"Updated {suffix}",
        SetStatus: false,
        Status: null,
        SetExpiresAt: false,
        ExpiresAt: null,
        SetAllowedCidrs: false,
        AllowedCidrs: null,
        Reason: null,
        IpAddress: null,
        UserAgent: null);

    private static UpdateApiKeyCommand UpdateStatusCommand(
        TestEnvironment environment,
        ApiKeyResource source,
        string suffix,
        ApiKeyPersistentStatus status) => new(
        EntityId.New(),
        environment.Actor,
        ApiKeyAccessMode.Self,
        environment.UserId,
        source.Id,
        $"api-key-update-{suffix}-{Guid.NewGuid():N}",
        ExpectedVersion: source.Version,
        SetName: false,
        Name: null,
        SetStatus: true,
        Status: status,
        SetExpiresAt: false,
        ExpiresAt: null,
        SetAllowedCidrs: false,
        AllowedCidrs: null,
        Reason: null,
        IpAddress: null,
        UserAgent: null);

    private static RevokeApiKeyCommand RevokeCommand(
        TestEnvironment environment,
        ApiKeyResource source,
        string suffix) => new(
        EntityId.New(),
        environment.Actor,
        ApiKeyAccessMode.Self,
        environment.UserId,
        source.Id,
        $"api-key-revoke-{suffix}-{Guid.NewGuid():N}",
        ExpectedVersion: source.Version,
        Reason: $"revoke {suffix}",
        IpAddress: null,
        UserAgent: null);

    private static RotateApiKeyCommand RotateCommand(
        TestEnvironment environment,
        ApiKeyResource source,
        string suffix) => new(
        EntityId.New(),
        environment.Actor,
        ApiKeyAccessMode.Self,
        environment.UserId,
        source.Id,
        $"api-key-rotate-{suffix}-{Guid.NewGuid():N}",
        ExpectedVersion: source.Version,
        Reason: $"rotate {suffix}",
        IpAddress: null,
        UserAgent: null);

    private static ApiKeyAccessDecision Authorized(TestEnvironment environment) => new(
        ApiKeyAccessDecisionKind.Authorized,
        environment.UserId,
        environment.GroupId,
        EntityId.New(),
        TestTimestamp());

#pragma warning disable MA0051 // Keeping the complete access-evidence matrix adjacent makes omissions visible.
    private static ApiKeyAccessDecision? Decision(
        TestEnvironment environment,
        string value) => value switch
    {
        "authorized" => Authorized(environment),
        "null" => null,
        "wrong_user" => new(
            ApiKeyAccessDecisionKind.Authorized,
            EntityId.New(),
            environment.GroupId,
            EntityId.New(),
            TestTimestamp()),
        "wrong_group" => new(
            ApiKeyAccessDecisionKind.Authorized,
            environment.UserId,
            EntityId.New(),
            EntityId.New(),
            TestTimestamp()),
        "authorized_missing_subscription" => new(
            ApiKeyAccessDecisionKind.Authorized,
            environment.UserId,
            environment.GroupId,
            SubscriptionId: null,
            TestTimestamp()),
        "authorized_empty_subscription" => new(
            ApiKeyAccessDecisionKind.Authorized,
            environment.UserId,
            environment.GroupId,
            default(EntityId),
            TestTimestamp()),
        "authorized_missing_observed_at" => new(
            ApiKeyAccessDecisionKind.Authorized,
            environment.UserId,
            environment.GroupId,
            EntityId.New(),
            ObservedAt: null),
        "authorized_default_observed_at" => new(
            ApiKeyAccessDecisionKind.Authorized,
            environment.UserId,
            environment.GroupId,
            EntityId.New(),
            default(DateTimeOffset)),
        "required_with_subscription" => new(
            ApiKeyAccessDecisionKind.SubscriptionRequired,
            environment.UserId,
            environment.GroupId,
            EntityId.New(),
            ObservedAt: null),
        "inactive_with_observed_at" => new(
            ApiKeyAccessDecisionKind.SubscriptionInactive,
            environment.UserId,
            environment.GroupId,
            SubscriptionId: null,
            TestTimestamp()),
        "required" => new(
            ApiKeyAccessDecisionKind.SubscriptionRequired,
            environment.UserId,
            environment.GroupId,
            SubscriptionId: null,
            ObservedAt: null),
        "inactive" => new(
            ApiKeyAccessDecisionKind.SubscriptionInactive,
            environment.UserId,
            environment.GroupId,
            SubscriptionId: null,
            ObservedAt: null),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
#pragma warning restore MA0051

    private static ApiKeyResource? LockedState(
        ApiKeyResource source,
        string state) => state switch
    {
        "missing" => null,
        "revoked" => source with
        {
            Status = ApiKeyPersistentStatus.Revoked,
            EffectiveStatus = ApiKeyEffectiveStatus.Revoked,
        },
        "group" => source with
        {
            GroupId = EntityId.New(),
        },
        "version" => source with
        {
            Version = 2,
        },
        "lifecycle" => source with
        {
            EffectiveStatus = ApiKeyEffectiveStatus.Expired,
            ExpiresAt = source.ObservedAt,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static async ValueTask<ResultError> InvokeMutationPreflightAsync(
        TestEnvironment environment,
        ApiKeyResource source,
        string operation)
    {
        switch (operation)
        {
            case "update":
            {
                Result<ApiKeyUpdatedOutcome?> result =
                    await environment.Service.TryReplayUpdateAsync(
                        UpdateNameCommand(environment, source, "preflight"),
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                Assert.True(result.IsFailure);
                return result.Error;
            }

            case "revoke":
            {
                Result<ApiKeyRevokedOutcome?> result =
                    await environment.Service.TryReplayRevokeAsync(
                        RevokeCommand(environment, source, "preflight"),
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                Assert.True(result.IsFailure);
                return result.Error;
            }

            case "rotate":
            {
                Result<ApiKeyCreatedOutcome?> result =
                    await environment.Service.TryReplayRotateAsync(
                        RotateCommand(environment, source, "preflight"),
                        TestContext.Current.CancellationToken)
                        .ConfigureAwait(false);
                Assert.True(result.IsFailure);
                return result.Error;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static string CursorText(
        byte version,
        long unixMicroseconds,
        bool hasId)
    {
        byte[] bytes = new byte[25];
        bytes[0] = version;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1, 8), unixMicroseconds);
        if (hasId)
        {
            bytes[^1] = 1;
        }

        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
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
            ResponseEnvelope = responseEnvelope ?? envelope;
            Policy = new IdentityPolicy(
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
                ResponseEnvelope,
                OperationalEvents,
                Policy);
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

        internal IApiKeyCreateResponseEnvelope ResponseEnvelope { get; }

        internal IdentityPolicy Policy { get; }

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
        internal int ListCount { get; private set; }

        internal int CreateCount { get; private set; }

        internal int LockCount { get; private set; }

        internal int UpdateCount { get; private set; }

        internal int RevokeCount { get; private set; }

        internal int RotateCount { get; private set; }

        internal ApiKeyCreateWrite? LastWrite { get; private set; }

        internal IReadOnlyList<ApiKeyResource> Items { get; set; } = [];

        internal ApiKeyCursor? LastCursor { get; private set; }

        internal bool UseForcedGetResult { get; set; }

        internal ApiKeyResource? ForcedGetResult { get; set; }

        internal bool ReturnItemsWithoutOwnerFilter { get; set; }

        internal bool CorruptCreatedOwner { get; set; }

        internal ApiKeyCreateResult? ForcedCreateResult { get; set; }

        internal Func<ApiKeyResource, ApiKeyResource>? CreatedTransform { get; set; }

        internal bool ExpireCreatedResourceAfterWrite { get; set; }

        internal bool ExpireRotatedResourceAfterWrite { get; set; }

        internal bool ForceHasMore { get; set; }

        internal bool UseForcedLockResult { get; set; }

        internal ApiKeyResource? ForcedLockResult { get; set; }

        internal ApiKeyUpdateResult? ForcedUpdateResult { get; set; }

        internal Func<ApiKeyResource, ApiKeyResource>? UpdatedTransform { get; set; }

        internal ApiKeyRevokeResult? ForcedRevokeResult { get; set; }

        internal Func<ApiKeyResource, ApiKeyResource>? RevokedTransform { get; set; }

        internal ApiKeyRotateResult? ForcedRotateResult { get; set; }

        internal Func<ApiKeyResource, ApiKeyResource>? RotatedOldTransform { get; set; }

        internal Func<ApiKeyResource, ApiKeyResource>? RotatedNewTransform { get; set; }

        public ValueTask<ApiKeySlice> ListAsync(
            EntityId userId,
            ApiKeyCursor? cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ListCount++;
            LastCursor = cursor;
            IEnumerable<ApiKeyResource> query = ReturnItemsWithoutOwnerFilter
                ? Items
                : Items.Where(value => value.UserId == userId);
            query = query
                .OrderByDescending(static value => value.CreatedAt)
                .ThenByDescending(
                    static value => value.Id.Value.ToString("N"),
                    StringComparer.Ordinal);
            if (cursor is not null)
            {
                query = query.Where(value =>
                    value.CreatedAt < cursor.CreatedAt
                    || value.CreatedAt == cursor.CreatedAt
                    && string.CompareOrdinal(
                        value.Id.Value.ToString("N"),
                        cursor.Id.Value.ToString("N")) < 0);
            }

            ApiKeyResource[] remaining = query.ToArray();
            return ValueTask.FromResult(new ApiKeySlice(
                remaining.Take(limit).ToArray(),
                HasMore: ForceHasMore || remaining.Length > limit));
        }

        public ValueTask<ApiKeyResource?> GetAsync(
            EntityId userId,
            EntityId apiKeyId,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (UseForcedGetResult)
            {
                return ValueTask.FromResult(ForcedGetResult);
            }

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
            if (ForcedCreateResult is not null)
            {
                return ValueTask.FromResult(ForcedCreateResult);
            }

            ApiKeyResource resource = Resource(
                CorruptCreatedOwner ? EntityId.New() : write.UserId,
                write.GroupId,
                write.ApiKeyId,
                write);
            if (ExpireCreatedResourceAfterWrite)
            {
                DateTimeOffset expiration = write.ExpiresAt
                    ?? throw new InvalidOperationException(
                        "The expiry-boundary fake requires an expiration.");
                resource = resource with
                {
                    EffectiveStatus = ApiKeyEffectiveStatus.Expired,
                    ObservedAt = expiration.AddMilliseconds(1),
                };
            }

            resource = CreatedTransform?.Invoke(resource) ?? resource;
            Items = [.. Items, resource];
            return ValueTask.FromResult(new ApiKeyCreateResult(
                ApiKeyCreateDisposition.Created,
                resource));
        }

        public ValueTask<ApiKeyResource?> LockForMutationAsync(
            EntityId userId,
            EntityId apiKeyId,
            EntityId expectedGroupId,
            long expectedVersion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            _ = expectedGroupId;
            _ = expectedVersion;
            _ = unitOfWorkContext;
            _ = cancellationToken;
            LockCount++;
            if (UseForcedLockResult)
            {
                return ValueTask.FromResult(ForcedLockResult);
            }

            return ValueTask.FromResult(
                Items.SingleOrDefault(value =>
                    value.UserId == userId && value.Id == apiKeyId));
        }

        public ValueTask<ApiKeyUpdateResult> UpdateAsync(
            ApiKeyUpdateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            _ = unitOfWorkContext;
            _ = cancellationToken;
            UpdateCount++;
            if (ForcedUpdateResult is not null)
            {
                return ValueTask.FromResult(ForcedUpdateResult);
            }

            ApiKeyResource before = Items.Single(value =>
                value.UserId == write.UserId && value.Id == write.ApiKeyId);
            string name = write.SetName ? write.Name! : before.Name;
            ApiKeyPersistentStatus status =
                write.SetStatus ? write.Status!.Value : before.Status;
            DateTimeOffset? expiresAt =
                write.SetExpiresAt ? write.ExpiresAt : before.ExpiresAt;
            IReadOnlyList<string> allowedCidrs =
                write.SetAllowedCidrs ? write.AllowedCidrs! : before.AllowedCidrs;
            bool changed = !string.Equals(name, before.Name, StringComparison.Ordinal)
                || status != before.Status
                || expiresAt != before.ExpiresAt
                || !allowedCidrs.SequenceEqual(
                    before.AllowedCidrs,
                    StringComparer.Ordinal);
            DateTimeOffset updatedAt = changed
                ? before.UpdatedAt.AddSeconds(1)
                : before.UpdatedAt;
            DateTimeOffset observedAt = changed
                ? updatedAt
                : before.ObservedAt;
            ApiKeyResource current = before with
            {
                Name = name,
                Status = status,
                EffectiveStatus = EffectiveStatus(status, expiresAt, observedAt),
                ExpiresAt = expiresAt,
                AllowedCidrs = allowedCidrs.ToArray(),
                Version = before.Version + (changed ? 1 : 0),
                UpdatedAt = updatedAt,
                ObservedAt = observedAt,
            };
            current = UpdatedTransform?.Invoke(current) ?? current;
            Replace(before, current);
            return ValueTask.FromResult(new ApiKeyUpdateResult(
                ApiKeyUpdateDisposition.Updated,
                changed,
                current.Version,
                current));
        }

        public ValueTask<ApiKeyRevokeResult> RevokeAsync(
            ApiKeyRevokeWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            _ = unitOfWorkContext;
            _ = cancellationToken;
            RevokeCount++;
            if (ForcedRevokeResult is not null)
            {
                return ValueTask.FromResult(ForcedRevokeResult);
            }

            ApiKeyResource before = Items.Single(value =>
                value.UserId == write.UserId && value.Id == write.ApiKeyId);
            DateTimeOffset changedAt = before.UpdatedAt.AddSeconds(1);
            ApiKeyResource current = before with
            {
                Status = ApiKeyPersistentStatus.Revoked,
                EffectiveStatus = ApiKeyEffectiveStatus.Revoked,
                Version = before.Version + 1,
                UpdatedAt = changedAt,
                ObservedAt = changedAt,
            };
            current = RevokedTransform?.Invoke(current) ?? current;
            Replace(before, current);
            return ValueTask.FromResult(new ApiKeyRevokeResult(
                ApiKeyRevokeDisposition.Revoked,
                current.Version,
                current));
        }

        public ValueTask<ApiKeyRotateResult> RotateAsync(
            ApiKeyRotateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            _ = unitOfWorkContext;
            _ = cancellationToken;
            RotateCount++;
            if (ForcedRotateResult is not null)
            {
                return ValueTask.FromResult(ForcedRotateResult);
            }

            ApiKeyResource before = Items.Single(value =>
                value.UserId == write.UserId && value.Id == write.ApiKeyId);
            DateTimeOffset changedAt = ExpireRotatedResourceAfterWrite
                ? before.UpdatedAt.AddMilliseconds(100)
                : before.UpdatedAt.AddSeconds(1);
            DateTimeOffset newObservedAt = ExpireRotatedResourceAfterWrite
                ? (before.ExpiresAt
                    ?? throw new InvalidOperationException(
                        "The expiry-boundary fake requires an expiration."))
                    .AddMilliseconds(1)
                : changedAt;
            ApiKeyResource oldApiKey = before with
            {
                Status = ApiKeyPersistentStatus.Revoked,
                EffectiveStatus = ApiKeyEffectiveStatus.Revoked,
                Version = before.Version + 1,
                UpdatedAt = changedAt,
                ObservedAt = changedAt,
            };
            ApiKeyResource newApiKey = new(
                write.NewApiKeyId,
                before.UserId,
                before.GroupId,
                before.Name,
                write.NewPrefix,
                ApiKeyPersistentStatus.Active,
                EffectiveStatus(
                    ApiKeyPersistentStatus.Active,
                    before.ExpiresAt,
                    newObservedAt),
                before.ExpiresAt,
                before.AllowedCidrs.ToArray(),
                LastUsedAt: null,
                Version: 1,
                changedAt,
                changedAt,
                newObservedAt);
            oldApiKey = RotatedOldTransform?.Invoke(oldApiKey) ?? oldApiKey;
            newApiKey = RotatedNewTransform?.Invoke(newApiKey) ?? newApiKey;
            Replace(before, oldApiKey);
            Items = [.. Items, newApiKey];
            return ValueTask.FromResult(new ApiKeyRotateResult(
                ApiKeyRotateDisposition.Rotated,
                oldApiKey.Version,
                oldApiKey,
                newApiKey));
        }

        private void Replace(ApiKeyResource before, ApiKeyResource current)
        {
            Items = Items
                .Select(value => ReferenceEquals(value, before) ? current : value)
                .ToArray();
        }

        private static ApiKeyEffectiveStatus EffectiveStatus(
            ApiKeyPersistentStatus status,
            DateTimeOffset? expiresAt,
            DateTimeOffset observedAt) => status switch
        {
            ApiKeyPersistentStatus.Revoked => ApiKeyEffectiveStatus.Revoked,
            ApiKeyPersistentStatus.Disabled => ApiKeyEffectiveStatus.Disabled,
            _ when expiresAt is DateTimeOffset expiry && expiry <= observedAt =>
                ApiKeyEffectiveStatus.Expired,
            _ => ApiKeyEffectiveStatus.Active,
        };

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

        internal CommandIdempotencyRequest? LastRequest { get; private set; }

        internal CommandIdempotencyAcquireResult? ForcedAcquireResult { get; set; }

        internal CommandIdempotencyResponse? ForcedReplayResponse { get; set; }

        internal bool CompleteResult { get; set; } = true;

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            _ = unitOfWorkContext;
            _ = cancellationToken;
            LastRequest = request;
            if (ForcedAcquireResult is not null)
            {
                return ValueTask.FromResult(ForcedAcquireResult);
            }

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
            if (!CompleteResult)
            {
                return ValueTask.FromResult(false);
            }

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

        public JsonElement EncryptRotate(
            ApiKeyCreateResponseSecret response,
            EntityId apiKeyId,
            IdempotencySecretBinding binding) =>
            throw new NotSupportedException();

        public ApiKeyCreateResponseSecret DecryptRotate(
            JsonElement envelope,
            EntityId apiKeyId,
            IdempotencySecretBinding binding) =>
            throw new NotSupportedException();
    }

    private sealed class FixedApiKeyResponseEnvelope(
        ApiKeyCreateResponseSecret response) : IApiKeyCreateResponseEnvelope
    {
        public JsonElement Encrypt(
            ApiKeyCreateResponseSecret value,
            EntityId apiKeyId,
            IdempotencySecretBinding binding) =>
            throw new NotSupportedException();

        public ApiKeyCreateResponseSecret Decrypt(
            JsonElement envelope,
            EntityId apiKeyId,
            IdempotencySecretBinding binding) => response;

        public JsonElement EncryptRotate(
            ApiKeyCreateResponseSecret value,
            EntityId apiKeyId,
            IdempotencySecretBinding binding) =>
            throw new NotSupportedException();

        public ApiKeyCreateResponseSecret DecryptRotate(
            JsonElement envelope,
            EntityId apiKeyId,
            IdempotencySecretBinding binding) => response;
    }

    private static DateTimeOffset TestTimestamp() => DateTimeOffset.Parse(
        "2026-07-23T06:07:08Z",
        System.Globalization.CultureInfo.InvariantCulture);
}
#pragma warning restore MA0048
