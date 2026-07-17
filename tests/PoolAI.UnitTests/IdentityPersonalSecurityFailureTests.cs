#pragma warning disable MA0051 // Failure matrices keep the complete security-command arrangement visible.
#pragma warning disable MA0004 // xUnit owns the synchronization context used by assertion delegates.
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class IdentityPersonalSecurityFailureTests
{
    private const int PasswordCommand = 0;
    private const int SetupCommand = 1;
    private const int ConfirmCommand = 2;
    private const int DisableCommand = 3;

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-07-17T03:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task PasswordValidationRejectsMalformedCredentialsBeforeOpeningAUnitOfWork()
    {
        AuthenticationUserSnapshot user = User();
        (ChangePasswordCommand Command, string Code)[] cases =
        [
            (ChangePassword(user) with { ExpectedVersion = 0 }, IdentityErrorCodes.ValidationFailed),
            (ChangePassword(user) with { CurrentPassword = string.Empty }, IdentityErrorCodes.ValidationFailed),
            (ChangePassword(user) with { CurrentPassword = new string('x', 1025) }, IdentityErrorCodes.ValidationFailed),
            (ChangePassword(user) with { NewPassword = "too-short" }, IdentityErrorCodes.PasswordPolicyFailed),
            (ChangePassword(user) with { IdempotencyKey = string.Empty }, IdentityErrorCodes.ValidationFailed),
            (ChangePassword(user) with { Reason = "\r\n" }, IdentityErrorCodes.ValidationFailed),
        ];

        foreach ((ChangePasswordCommand command, string code) in cases)
        {
            Fixture fixture = new(user);
            Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken);

            Assert.Equal(code, result.Error.Code);
            Assert.Equal(0, fixture.UnitOfWork.BeginCalls);
            AssertNoMutation(fixture);
        }
    }

    [Fact]
    public async Task SetupValidationRejectsMalformedCredentialsBeforeGeneratingASecret()
    {
        AuthenticationUserSnapshot user = User();
        SetupTotpCommand[] commands =
        [
            SetupTotp(user) with { IdempotencyKey = string.Empty },
            SetupTotp(user) with { CurrentPassword = string.Empty },
            SetupTotp(user) with { CurrentPassword = new string('x', 1025) },
        ];

        foreach (SetupTotpCommand command in commands)
        {
            Fixture fixture = new(user);
            Result<IdentityCommandOutcome<TotpSetupView>> result = await fixture.Service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken);

            Assert.Equal(IdentityErrorCodes.ValidationFailed, result.Error.Code);
            Assert.Equal(0, fixture.UnitOfWork.BeginCalls);
            Assert.Equal(0, fixture.ChallengeHasher.CreateCalls);
            Assert.Equal(0, fixture.SetupResponse.EncryptCalls);
            AssertNoMutation(fixture);
        }
    }

    [Fact]
    public async Task ConfirmValidationRejectsInvalidVersionAndEveryMalformedTotpShape()
    {
        AuthenticationUserSnapshot user = User();
        ConfirmTotpCommand[] commands =
        [
            ConfirmTotp(user, EntityId.New()) with { ExpectedVersion = 0 },
            ConfirmTotp(user, EntityId.New()) with { IdempotencyKey = string.Empty },
            ConfirmTotp(user, EntityId.New()) with { TotpCode = null! },
            ConfirmTotp(user, EntityId.New()) with { TotpCode = string.Empty },
            ConfirmTotp(user, EntityId.New()) with { TotpCode = "12345" },
            ConfirmTotp(user, EntityId.New()) with { TotpCode = "12345x" },
            ConfirmTotp(user, EntityId.New()) with { TotpCode = "1234567" },
        ];

        foreach (ConfirmTotpCommand command in commands)
        {
            Fixture fixture = new(user);
            Result<IdentityCommandOutcome<TotpConfirmView>> result = await fixture.Service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken);

            Assert.Equal(IdentityErrorCodes.ValidationFailed, result.Error.Code);
            Assert.Equal(0, fixture.Repository.FindChallengeCalls);
            Assert.Equal(0, fixture.UnitOfWork.BeginCalls);
            AssertNoMutation(fixture);
        }
    }

    [Fact]
    public async Task DisableValidationRejectsInvalidVersionPasswordAndTotpBeforeReadingSecrets()
    {
        AuthenticationUserSnapshot user = User(totpEnabled: true);
        DisableTotpCommand[] commands =
        [
            DisableTotp(user) with { ExpectedVersion = 0 },
            DisableTotp(user) with { IdempotencyKey = string.Empty },
            DisableTotp(user) with { CurrentPassword = string.Empty },
            DisableTotp(user) with { CurrentPassword = new string('x', 1025) },
            DisableTotp(user) with { TotpCode = "abc123" },
        ];

        foreach (DisableTotpCommand command in commands)
        {
            Fixture fixture = new(user);
            Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken);

            Assert.Equal(IdentityErrorCodes.ValidationFailed, result.Error.Code);
            Assert.Equal(0, fixture.SecretEnvelope.DecryptCalls);
            Assert.Equal(0, fixture.UnitOfWork.BeginCalls);
            AssertNoMutation(fixture);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PasswordRejectsMissingInactiveOrTokenStaleActorInsideOneUnitOfWork(int scenario)
    {
        AuthenticationUserSnapshot baseline = User();
        AuthenticationUserSnapshot? stored = scenario switch
        {
            0 => null,
            1 => baseline with { Status = UserLifecycle.Disabled },
            _ => baseline,
        };
        SessionActor actor = Actor(baseline) with
        {
            TokenVersion = scenario == 2 ? baseline.TokenVersion + 1 : baseline.TokenVersion,
        };
        Fixture fixture = new(stored, actor);

        Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
            ChangePassword(actor, baseline.Version),
            TestContext.Current.CancellationToken);

        Assert.Equal(IdentityErrorCodes.InvalidUserToken, result.Error.Code);
        Assert.Equal(1, fixture.UnitOfWork.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWork.CommitCalls);
        Assert.Equal(1, fixture.Idempotency.CompleteCalls);
        AssertNoMutation(fixture);
    }

    [Theory]
    [InlineData(PasswordCommand)]
    [InlineData(SetupCommand)]
    [InlineData(DisableCommand)]
    public async Task PersonalCommandsRejectInactiveActorWithoutWritingSecrets(int commandKind)
    {
        AuthenticationUserSnapshot active = User(totpEnabled: commandKind == DisableCommand);
        AuthenticationUserSnapshot disabled = active with { Status = UserLifecycle.Disabled };
        Fixture fixture = new(disabled, Actor(active));

        Invocation result = await InvokeAsync(fixture, commandKind);

        Assert.Equal(IdentityErrorCodes.InvalidUserToken, result.ErrorCode);
        Assert.Equal(1, fixture.UnitOfWork.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWork.CommitCalls);
        Assert.Equal(1, fixture.Idempotency.CompleteCalls);
        Assert.Equal(0, fixture.SetupResponse.EncryptCalls);
        Assert.Equal(0, fixture.RecoveryEnvelope.EncryptCalls);
        AssertNoMutation(fixture);
    }

    [Theory]
    [InlineData(0, IdentityErrorCodes.InvalidCredentials)]
    [InlineData(1, IdentityErrorCodes.InvalidCredentials)]
    [InlineData(2, IdentityErrorCodes.TotpAlreadyEnabled)]
    [InlineData(3, IdentityErrorCodes.InvalidCredentials)]
    [InlineData(4, IdentityErrorCodes.TotpNotEnabled)]
    [InlineData(5, IdentityErrorCodes.TotpCodeInvalid)]
    public async Task CredentialAndTotpStateFailuresCompleteWithoutSecurityMutation(
        int scenario,
        string expectedCode)
    {
        bool enabled = scenario is 2 or 3 or 5;
        AuthenticationUserSnapshot user = User(totpEnabled: enabled);
        Fixture fixture = new(user);
        int commandKind = scenario switch
        {
            0 => PasswordCommand,
            1 or 2 => SetupCommand,
            _ => DisableCommand,
        };
        fixture.PasswordHasher.VerifyResult = scenario is not 0 and not 1 and not 3;
        fixture.Totp.MatchResult = scenario != 5;

        Invocation result = await InvokeAsync(fixture, commandKind);

        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Equal(1, fixture.UnitOfWork.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWork.CommitCalls);
        Assert.Equal(1, fixture.Idempotency.CompleteCalls);
        AssertNoMutation(fixture);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ConfirmRejectsEveryInvalidChallengeSnapshotWithoutDecryptingSeed(
        int scenario)
    {
        AuthenticationUserSnapshot user = User();
        SessionActor actor = Actor(user);
        EntityId challengeId = EntityId.New();
        TotpChallengeSnapshot? challenge = SetupChallenge(user, challengeId);
        switch (scenario)
        {
            case 0:
                challenge = null;
                break;
            case 1:
                challenge = challenge with { UserId = EntityId.New() };
                break;
            case 2:
                challenge = challenge with { User = user with { Status = UserLifecycle.Disabled } };
                break;
            case 3:
                challenge = challenge with { User = User(user.Id, totpEnabled: true) };
                break;
            case 4:
                challenge = challenge with { SecurityStamp = EntityId.New() };
                break;
            case 5:
                challenge = challenge with { TokenVersion = user.TokenVersion + 1 };
                break;
            default:
                actor = actor with { TokenVersion = user.TokenVersion + 1 };
                break;
        }

        Fixture fixture = new(user, actor) { ChallengeId = challengeId };
        fixture.Repository.Challenge = challenge;

        Result<IdentityCommandOutcome<TotpConfirmView>> result = await fixture.Service.ExecuteAsync(
            ConfirmTotp(actor, user.Version, challengeId),
            TestContext.Current.CancellationToken);

        Assert.Equal(IdentityErrorCodes.TotpSetupExpired, result.Error.Code);
        Assert.Equal(0, fixture.SecretEnvelope.DecryptCalls);
        Assert.Equal(0, fixture.RecoveryGenerator.CreateCalls);
        Assert.Equal(0, fixture.Repository.ConfirmCalls);
        Assert.Equal(1, fixture.UnitOfWork.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWork.CommitCalls);
    }

    [Fact]
    public async Task ConfirmRejectsWrongTotpWithoutGeneratingOrPersistingRecoveryCodes()
    {
        Fixture fixture = ForCommand(ConfirmCommand);
        fixture.Totp.MatchResult = false;

        Invocation result = await InvokeAsync(fixture, ConfirmCommand);

        Assert.Equal(IdentityErrorCodes.TotpCodeInvalid, result.ErrorCode);
        Assert.Equal(1, fixture.SecretEnvelope.DecryptCalls);
        Assert.Equal(0, fixture.RecoveryGenerator.CreateCalls);
        Assert.Equal(0, fixture.RecoveryEnvelope.EncryptCalls);
        Assert.Equal(1, fixture.UnitOfWork.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWork.CommitCalls);
        AssertNoMutation(fixture);
    }

    [Fact]
    public async Task ConfirmFailsClosedForMissingSeedEnvelopeAndNonEightRecoveryBatch()
    {
        Fixture missingEnvelope = ForCommand(ConfirmCommand);
        missingEnvelope.Repository.Challenge = missingEnvelope.Repository.Challenge! with
        {
            SecretEnvelope = null,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(missingEnvelope, ConfirmCommand));
        Assert.Equal(0, missingEnvelope.UnitOfWork.BeginCalls);
        AssertNoMutation(missingEnvelope);

        Fixture wrongBatch = ForCommand(ConfirmCommand);
        wrongBatch.RecoveryGenerator.Count = 7;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(wrongBatch, ConfirmCommand));
        Assert.Equal(0, wrongBatch.UnitOfWork.BeginCalls);
        Assert.Equal(0, wrongBatch.RecoveryEnvelope.EncryptCalls);
        AssertNoMutation(wrongBatch);
    }

    [Theory]
    [InlineData(1, IdentityErrorCodes.InvalidUserToken, 401)]
    [InlineData(2, IdentityErrorCodes.VersionConflict, 412)]
    [InlineData(3, IdentityErrorCodes.InvalidCredentials, 401)]
    [InlineData(4, IdentityErrorCodes.TotpAlreadyEnabled, 409)]
    [InlineData(5, IdentityErrorCodes.TotpNotEnabled, 409)]
    [InlineData(6, IdentityErrorCodes.TotpSetupExpired, 409)]
    [InlineData(7, IdentityErrorCodes.TotpSetupExpired, 409)]
    [InlineData(8, IdentityErrorCodes.TotpCodeInvalid, 401)]
    public async Task PersistenceDispositionsBecomeDurableIdempotentFailures(
        int dispositionValue,
        string expectedCode,
        int expectedStatus)
    {
        AuthenticationUserSnapshot user = User();
        AuthenticationUserSnapshot current = User(user.Id, version: 7);
        Fixture fixture = new(user);
        SecurityMutationDisposition disposition = (SecurityMutationDisposition)dispositionValue;
        fixture.Repository.ChangePasswordResult = new(
            disposition,
            current,
            disposition == SecurityMutationDisposition.VersionConflict ? 7 : null);

        Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
            ChangePassword(user),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(expectedStatus, fixture.Idempotency.Completion!.ResponseStatus);
        Assert.Equal(CommandIdempotencyTerminalStatus.Failed, fixture.Idempotency.Completion.TerminalStatus);
        Assert.Equal(1, fixture.Repository.ChangePasswordCalls);
        Assert.Equal(0, fixture.Audit.Calls);
        Assert.Equal(1, fixture.UnitOfWork.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWork.CommitCalls);
        Assert.Same(fixture.Repository.MutationContext, fixture.Idempotency.CompletionContext);
    }

    [Fact]
    public async Task VersionConflictFallsBackToSnapshotVersionWhenCurrentVersionIsAbsent()
    {
        AuthenticationUserSnapshot user = User();
        AuthenticationUserSnapshot current = User(user.Id, version: 9);
        Fixture fixture = new(user);
        fixture.Repository.ChangePasswordResult = new(
            SecurityMutationDisposition.VersionConflict,
            current,
            CurrentVersion: null);

        Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
            ChangePassword(user),
            TestContext.Current.CancellationToken);

        Assert.Equal(IdentityErrorCodes.VersionConflict, result.Error.Code);
        Assert.Equal("\"v9\"", result.Error.ETag);
        Assert.Equal("\"v9\"", fixture.Idempotency.Completion!.ResponseHeaders.GetProperty("ETag").GetString());
    }

    [Fact]
    public async Task UnknownPersistenceDispositionFailsClosedWithoutCompletingOrCommitting()
    {
        AuthenticationUserSnapshot user = User();
        Fixture fixture = new(user);
        fixture.Repository.ChangePasswordResult = new(
            (SecurityMutationDisposition)int.MaxValue,
            user);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.ExecuteAsync(
                ChangePassword(user),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, fixture.UnitOfWork.BeginCalls);
        Assert.Equal(0, fixture.UnitOfWork.CommitCalls);
        Assert.Equal(0, fixture.Idempotency.CompleteCalls);
        Assert.Equal(0, fixture.Audit.Calls);
    }

    [Theory]
    [InlineData(PasswordCommand, 0)]
    [InlineData(PasswordCommand, 1)]
    [InlineData(SetupCommand, 0)]
    [InlineData(SetupCommand, 1)]
    [InlineData(ConfirmCommand, 0)]
    [InlineData(ConfirmCommand, 1)]
    [InlineData(DisableCommand, 0)]
    [InlineData(DisableCommand, 1)]
    public async Task ConflictAndBusyAcquiresNeverPersistPreparedSecurityMaterial(
        int commandKind,
        int disposition)
    {
        Fixture fixture = ForCommand(commandKind);
        fixture.Idempotency.AcquireResult = disposition == 0
            ? CommandIdempotencyAcquireResult.Conflict
            : CommandIdempotencyAcquireResult.Busy;

        Invocation result = await InvokeAsync(fixture, commandKind);

        Assert.Equal(
            disposition == 0
                ? IdentityErrorCodes.IdempotencyConflict
                : IdentityErrorCodes.CoordinationUnavailable,
            result.ErrorCode);
        Assert.Equal(1, fixture.UnitOfWork.BeginCalls);
        Assert.Equal(0, fixture.UnitOfWork.CommitCalls);
        Assert.Equal(0, fixture.Idempotency.CompleteCalls);
        AssertNoMutation(fixture);
    }

    [Theory]
    [InlineData(SetupCommand, IdentityErrorCodes.InvalidCredentials, 401)]
    [InlineData(ConfirmCommand, IdentityErrorCodes.TotpSetupExpired, 409)]
    [InlineData(PasswordCommand, IdentityErrorCodes.VersionConflict, 412)]
    [InlineData(DisableCommand, IdentityErrorCodes.TotpNotEnabled, 409)]
    [InlineData(PasswordCommand, IdentityErrorCodes.InvalidUserToken, 401)]
    [InlineData(PasswordCommand, IdentityErrorCodes.TotpCodeInvalid, 401)]
    [InlineData(PasswordCommand, IdentityErrorCodes.TotpAlreadyEnabled, 409)]
    public async Task FailedReplaysReturnOriginalFailureWithoutMutation(
        int commandKind,
        string code,
        int status)
    {
        Fixture fixture = ForCommand(commandKind);
        fixture.Idempotency.AcquireResult = CommandIdempotencyAcquireResult.Replay(
            FailedResponse(code, status));

        Invocation result = await InvokeAsync(fixture, commandKind);

        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(
            string.Equals(code, IdentityErrorCodes.VersionConflict, StringComparison.Ordinal)
                ? "\"v7\""
                : null,
            result.ETag);
        Assert.Equal(1, fixture.UnitOfWork.BeginCalls);
        Assert.Equal(0, fixture.UnitOfWork.CommitCalls);
        Assert.Equal(0, fixture.Idempotency.CompleteCalls);
        AssertNoMutation(fixture);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task CorruptFailedReplayIsRejectedBeforeAnyMutation(int scenario)
    {
        Fixture fixture = ForCommand(PasswordCommand);
        CommandIdempotencyResponse valid = FailedResponse(
            IdentityErrorCodes.InvalidCredentials,
            401);
        fixture.Idempotency.AcquireResult = CommandIdempotencyAcquireResult.Replay(scenario switch
        {
            0 => valid with { Body = null },
            1 => valid with { BodyEnvelope = Envelope("unexpected") },
            2 => valid with { ResourceType = "user" },
            3 => valid with { ResourceId = fixture.Actor.UserId },
            4 => valid with { Status = 409 },
            5 => valid with { Headers = Headers("\"v7\"") },
            6 => FailedResponse(IdentityErrorCodes.VersionConflict, 412) with { Headers = EmptyHeaders() },
            _ => valid with { Headers = JsonSerializer.SerializeToElement(new { trace = "unexpected" }) },
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(fixture, PasswordCommand));

        Assert.Equal(0, fixture.UnitOfWork.CommitCalls);
        Assert.Equal(0, fixture.Idempotency.CompleteCalls);
        AssertNoMutation(fixture);
    }

    [Fact]
    public async Task UnsupportedFailedReplayCodeIsRejectedFailClosed()
    {
        Fixture fixture = ForCommand(PasswordCommand);
        fixture.Idempotency.AcquireResult = CommandIdempotencyAcquireResult.Replay(new(
            CommandIdempotencyTerminalStatus.Failed,
            400,
            FailureBody("unsupported_security_failure"),
            BodyEnvelope: null,
            EmptyHeaders(),
            ResourceType: null,
            ResourceId: null));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(fixture, PasswordCommand));

        AssertNoMutation(fixture);
        Assert.Equal(0, fixture.UnitOfWork.CommitCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task CorruptPasswordOrDisableReplayNeverAuthorizesACompletedCommand(int scenario)
    {
        Fixture fixture = ForCommand(PasswordCommand);
        CommandIdempotencyResponse valid = NonBodyResponse(fixture.Actor.UserId, "\"v2\"");
        fixture.Idempotency.AcquireResult = CommandIdempotencyAcquireResult.Replay(scenario switch
        {
            0 => valid with { Status = 200 },
            1 => valid with { Body = JsonSerializer.SerializeToElement(new { ok = true }) },
            2 => valid with { BodyEnvelope = Envelope("unexpected") },
            3 => valid with { ResourceType = "totp_setup" },
            4 => valid with { ResourceId = EntityId.New() },
            5 => valid with { Headers = EmptyHeaders() },
            _ => valid with { Headers = JsonSerializer.SerializeToElement(new { ETag = "\"v2\"", extra = "x" }) },
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(fixture, PasswordCommand));

        Assert.Equal(0, fixture.Repository.ChangePasswordCalls);
        Assert.Equal(0, fixture.UnitOfWork.CommitCalls);
        Assert.Equal(0, fixture.Audit.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"x1\"")]
    [InlineData("\"v1x")]
    [InlineData("\"vxx\"")]
    [InlineData("\"v0\"")]
    public async Task NonCanonicalReplayEtagsAreRejected(string etag)
    {
        Fixture fixture = ForCommand(PasswordCommand);
        fixture.Idempotency.AcquireResult = CommandIdempotencyAcquireResult.Replay(
            NonBodyResponse(fixture.Actor.UserId, etag));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(fixture, PasswordCommand));

        AssertNoMutation(fixture);
        Assert.Equal(0, fixture.UnitOfWork.CommitCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task CorruptSetupReplayNeverReturnsOrPersistsASeed(int scenario)
    {
        Fixture fixture = ForCommand(SetupCommand);
        EntityId resourceId = EntityId.New();
        fixture.SetupResponse.DecryptResult = new(
            resourceId,
            "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP",
            "otpauth://totp/PoolAI:test%40example.test?secret=opaque",
            SessionPolicy.TotpSetupSeconds);
        CommandIdempotencyResponse valid = SetupResponse(resourceId);
        if (scenario == 6)
        {
            fixture.SetupResponse.DecryptResult = fixture.SetupResponse.DecryptResult with
            {
                Challenge = EntityId.New(),
            };
        }
        else if (scenario == 7)
        {
            fixture.SetupResponse.DecryptResult = fixture.SetupResponse.DecryptResult with
            {
                ExpiresInSeconds = SessionPolicy.TotpSetupSeconds - 1,
            };
        }

        fixture.Idempotency.AcquireResult = CommandIdempotencyAcquireResult.Replay(scenario switch
        {
            0 => valid with { Status = 204 },
            1 => valid with { Body = JsonSerializer.SerializeToElement(new { secret = "forbidden" }) },
            2 => valid with { BodyEnvelope = null },
            3 => valid with { ResourceId = null },
            4 => valid with { ResourceType = "user" },
            5 => valid with { Headers = Headers("\"v1\"") },
            _ => valid,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(fixture, SetupCommand));

        Assert.Equal(0, fixture.Repository.CreateSetupCalls);
        Assert.Equal(0, fixture.UnitOfWork.CommitCalls);
        Assert.Equal(0, fixture.Audit.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task CorruptConfirmReplayNeverReturnsOrPersistsRecoveryCodes(int scenario)
    {
        Fixture fixture = ForCommand(ConfirmCommand);
        EntityId resourceId = fixture.ChallengeId;
        fixture.RecoveryEnvelope.DecryptResult = RecoveryCodes();
        CommandIdempotencyResponse valid = ConfirmResponse(resourceId, "\"v2\"");
        if (scenario == 8)
        {
            fixture.RecoveryEnvelope.DecryptResult = RecoveryCodes()[..7];
        }

        fixture.Idempotency.AcquireResult = CommandIdempotencyAcquireResult.Replay(scenario switch
        {
            0 => valid with { Status = 204 },
            1 => valid with { Body = JsonSerializer.SerializeToElement(new { codes = "forbidden" }) },
            2 => valid with { BodyEnvelope = null },
            3 => valid with { ResourceId = null },
            4 => valid with { ResourceId = EntityId.New() },
            5 => valid with { ResourceType = "user" },
            6 => valid with { Headers = EmptyHeaders() },
            7 => valid with { Headers = JsonSerializer.SerializeToElement(new { ETag = "\"v2\"", extra = "x" }) },
            _ => valid,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(fixture, ConfirmCommand));

        Assert.Equal(0, fixture.Repository.ConfirmCalls);
        Assert.Equal(0, fixture.UnitOfWork.CommitCalls);
        Assert.Equal(0, fixture.Audit.Calls);
    }

    [Fact]
    public async Task LostIdempotencyLeaseRollsBackPreparedFailureAndSuccessfulMutation()
    {
        AuthenticationUserSnapshot user = User();
        Fixture preparedFailure = new(user);
        preparedFailure.PasswordHasher.VerifyResult = false;
        preparedFailure.Idempotency.CompleteResult = false;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await preparedFailure.Service.ExecuteAsync(
                ChangePassword(user),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, preparedFailure.UnitOfWork.BeginCalls);
        Assert.Equal(0, preparedFailure.UnitOfWork.CommitCalls);
        AssertNoMutation(preparedFailure);

        AuthenticationUserSnapshot updated = User(user.Id, version: 2, tokenVersion: 2);
        Fixture successfulMutation = new(user);
        successfulMutation.Repository.ChangePasswordResult = new(
            SecurityMutationDisposition.Updated,
            updated);
        successfulMutation.Idempotency.CompleteResult = false;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await successfulMutation.Service.ExecuteAsync(
                ChangePassword(user),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, successfulMutation.Repository.ChangePasswordCalls);
        Assert.Equal(1, successfulMutation.Audit.Calls);
        Assert.Equal(1, successfulMutation.Idempotency.CompleteCalls);
        Assert.Equal(0, successfulMutation.UnitOfWork.CommitCalls);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    public async Task SuccessfulPasswordChangeMapsActorAndCommitsExactlyOneUnitOfWork(
        int roleValue,
        int expectedActorTypeValue)
    {
        SystemRole role = (SystemRole)roleValue;
        AuthenticationUserSnapshot user = User(role: role);
        AuthenticationUserSnapshot updated = User(
            user.Id,
            version: 2,
            tokenVersion: 2,
            role: role);
        Fixture fixture = new(user);
        fixture.Repository.ChangePasswordResult = new(
            SecurityMutationDisposition.Updated,
            updated);

        Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
            ChangePassword(user),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("\"v2\"", result.Value.ETag);
        Assert.Equal((AuditActorType)expectedActorTypeValue, fixture.Audit.Entries.Single().ActorType);
        Assert.Equal(1, fixture.Repository.ChangePasswordCalls);
        Assert.Equal(1, fixture.UnitOfWork.BeginCalls);
        Assert.Equal(1, fixture.UnitOfWork.CommitCalls);
        Assert.Same(fixture.Repository.MutationContext, fixture.Audit.Context);
        Assert.Same(fixture.Repository.MutationContext, fixture.Idempotency.CompletionContext);
    }

    [Fact]
    public async Task UnknownActorRoleFailsClosedBeforeAuditOrCommit()
    {
        AuthenticationUserSnapshot user = User(role: (SystemRole)int.MaxValue);
        Fixture fixture = new(user);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await fixture.Service.ExecuteAsync(
                ChangePassword(user),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, fixture.Repository.ChangePasswordCalls);
        Assert.Equal(0, fixture.Audit.Calls);
        Assert.Equal(0, fixture.Idempotency.CompleteCalls);
        Assert.Equal(0, fixture.UnitOfWork.CommitCalls);
    }

    private static Fixture ForCommand(int commandKind)
    {
        AuthenticationUserSnapshot user = User(totpEnabled: commandKind == DisableCommand);
        Fixture fixture = new(user);
        if (commandKind == ConfirmCommand)
        {
            fixture.Repository.Challenge = SetupChallenge(user, fixture.ChallengeId);
        }

        return fixture;
    }

    private static async Task<Invocation> InvokeAsync(Fixture fixture, int commandKind)
    {
        switch (commandKind)
        {
            case PasswordCommand:
            {
                Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
                    ChangePassword(fixture.Actor, fixture.ExpectedVersion),
                    TestContext.Current.CancellationToken);
                return Probe(result);
            }
            case SetupCommand:
            {
                Result<IdentityCommandOutcome<TotpSetupView>> result = await fixture.Service.ExecuteAsync(
                    SetupTotp(fixture.Actor),
                    TestContext.Current.CancellationToken);
                return Probe(result);
            }
            case ConfirmCommand:
            {
                Result<IdentityCommandOutcome<TotpConfirmView>> result = await fixture.Service.ExecuteAsync(
                    ConfirmTotp(fixture.Actor, fixture.ExpectedVersion, fixture.ChallengeId),
                    TestContext.Current.CancellationToken);
                return Probe(result);
            }
            case DisableCommand:
            {
                Result<IdentityCommandOutcome> result = await fixture.Service.ExecuteAsync(
                    DisableTotp(fixture.Actor, fixture.ExpectedVersion),
                    TestContext.Current.CancellationToken);
                return Probe(result);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(commandKind));
        }
    }

    private static Invocation Probe<T>(Result<T> result) => result.IsSuccess
        ? new Invocation(true, null, null)
        : new Invocation(false, result.Error.Code, result.Error.ETag);

    private static void AssertNoMutation(Fixture fixture)
    {
        Assert.Equal(0, fixture.Repository.ChangePasswordCalls);
        Assert.Equal(0, fixture.Repository.CreateSetupCalls);
        Assert.Equal(0, fixture.Repository.ConfirmCalls);
        Assert.Equal(0, fixture.Repository.DisableCalls);
        Assert.Equal(0, fixture.Audit.Calls);
    }

    private static ChangePasswordCommand ChangePassword(AuthenticationUserSnapshot user) =>
        ChangePassword(Actor(user), user.Version);

    private static ChangePasswordCommand ChangePassword(SessionActor actor, long version) => new(
        EntityId.New(),
        actor,
        "password-key",
        version,
        "old password material",
        "new password material",
        "user requested rotation",
        "192.0.2.10",
        "unit-test");

    private static SetupTotpCommand SetupTotp(AuthenticationUserSnapshot user) =>
        SetupTotp(Actor(user));

    private static SetupTotpCommand SetupTotp(SessionActor actor) => new(
        EntityId.New(),
        actor,
        "setup-key",
        "old password material",
        "192.0.2.10",
        "unit-test");

    private static ConfirmTotpCommand ConfirmTotp(
        AuthenticationUserSnapshot user,
        EntityId challengeId) => ConfirmTotp(Actor(user), user.Version, challengeId);

    private static ConfirmTotpCommand ConfirmTotp(
        SessionActor actor,
        long version,
        EntityId challengeId) => new(
        EntityId.New(),
        actor,
        "confirm-key",
        version,
        challengeId,
        "123456",
        "192.0.2.10",
        "unit-test");

    private static DisableTotpCommand DisableTotp(AuthenticationUserSnapshot user) =>
        DisableTotp(Actor(user), user.Version);

    private static DisableTotpCommand DisableTotp(SessionActor actor, long version) => new(
        EntityId.New(),
        actor,
        "disable-key",
        version,
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
        long tokenVersion = 1,
        SystemRole role = SystemRole.User) => new(
        id ?? EntityId.New(),
        "test@example.test",
        "test@example.test",
        "Test user",
        "encoded-password",
        role,
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

    private static CommandIdempotencyResponse FailedResponse(
        string code,
        int status) => new(
        CommandIdempotencyTerminalStatus.Failed,
        status,
        FailureBody(code),
        BodyEnvelope: null,
        string.Equals(code, IdentityErrorCodes.VersionConflict, StringComparison.Ordinal)
            ? Headers("\"v7\"")
            : EmptyHeaders(),
        ResourceType: null,
        ResourceId: null);

    private static CommandIdempotencyResponse NonBodyResponse(
        EntityId resourceId,
        string etag) => new(
        CommandIdempotencyTerminalStatus.Completed,
        204,
        Body: null,
        BodyEnvelope: null,
        Headers(etag),
        "user",
        resourceId);

    private static CommandIdempotencyResponse SetupResponse(EntityId resourceId) => new(
        CommandIdempotencyTerminalStatus.Completed,
        200,
        Body: null,
        Envelope("setup-response"),
        EmptyHeaders(),
        "totp_setup",
        resourceId);

    private static CommandIdempotencyResponse ConfirmResponse(
        EntityId resourceId,
        string etag) => new(
        CommandIdempotencyTerminalStatus.Completed,
        200,
        Body: null,
        Envelope("recovery-response"),
        Headers(etag),
        "totp_setup",
        resourceId);

    private static JsonElement FailureBody(string code) => JsonSerializer.SerializeToElement(new
    {
        Code = code,
        Description = "replayed security failure",
    });

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

    private static JsonElement Headers(string etag) => JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ETag"] = etag,
        });

    private sealed record Invocation(bool IsSuccess, string? ErrorCode, string? ETag);

    private sealed class Fixture
    {
        internal Fixture(
            AuthenticationUserSnapshot? user,
            SessionActor? actor = null)
        {
            AuthenticationUserSnapshot identity = user ?? User();
            Actor = actor ?? IdentityPersonalSecurityFailureTests.Actor(identity);
            ExpectedVersion = identity.Version;
            Repository = new RecordingRepository { User = user };
            UnitOfWork = new RecordingUnitOfWorkFactory();
            Idempotency = new RecordingIdempotencyStore();
            Audit = new RecordingAuditAppender();
            PasswordHasher = new RecordingPasswordHasher();
            ChallengeHasher = new RecordingChallengeHasher();
            Totp = new RecordingTotpAuthenticator();
            SecretEnvelope = new RecordingSecretEnvelope();
            RecoveryGenerator = new RecordingRecoveryGenerator();
            RecoveryEnvelope = new RecordingRecoveryEnvelope();
            SetupResponse = new RecordingSetupResponseEnvelope();
            Service = new PersonalSecurityUseCaseService(
                Repository,
                UnitOfWork,
                Idempotency,
                Audit,
                PasswordHasher,
                ChallengeHasher,
                Totp,
                SecretEnvelope,
                RecoveryGenerator,
                RecoveryEnvelope,
                SetupResponse,
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

        internal SessionActor Actor { get; }

        internal long ExpectedVersion { get; }

        internal EntityId ChallengeId { get; set; } = EntityId.New();

        internal RecordingRepository Repository { get; }

        internal RecordingUnitOfWorkFactory UnitOfWork { get; }

        internal RecordingIdempotencyStore Idempotency { get; }

        internal RecordingAuditAppender Audit { get; }

        internal RecordingPasswordHasher PasswordHasher { get; }

        internal RecordingChallengeHasher ChallengeHasher { get; }

        internal RecordingTotpAuthenticator Totp { get; }

        internal RecordingSecretEnvelope SecretEnvelope { get; }

        internal RecordingRecoveryGenerator RecoveryGenerator { get; }

        internal RecordingRecoveryEnvelope RecoveryEnvelope { get; }

        internal RecordingSetupResponseEnvelope SetupResponse { get; }
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
            MutationContext = unitOfWorkContext;
            return ValueTask.FromResult(DisableResult
                ?? new SecurityMutationPersistenceResult(SecurityMutationDisposition.Updated, User));
        }

        public ValueTask<AuthenticationUserSnapshot?> FindAuthenticationUserAsync(
            string normalizedEmail,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<bool> IsSessionFamilyActiveAsync(
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

    private sealed class RecordingIdempotencyStore : ICommandIdempotencyStore
    {
        internal CommandIdempotencyAcquireResult? AcquireResult { get; set; }

        internal bool CompleteResult { get; set; } = true;

        internal int AcquireCalls { get; private set; }

        internal int CompleteCalls { get; private set; }

        internal CommandIdempotencyCompletion? Completion { get; private set; }

        internal IUnitOfWorkContext? CompletionContext { get; private set; }

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCalls++;
            return ValueTask.FromResult(AcquireResult
                ?? CommandIdempotencyAcquireResult.Acquired(new CommandIdempotencyLease(
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
            CompleteCalls++;
            Completion = completion;
            CompletionContext = unitOfWorkContext;
            return ValueTask.FromResult(CompleteResult);
        }

        public ValueTask<bool> HeartbeatAsync(
            CommandIdempotencyHeartbeat heartbeat,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();
    }

    private sealed class RecordingAuditAppender : IAuditAppender
    {
        internal List<AuditEntry> Entries { get; } = [];

        internal int Calls => Entries.Count;

        internal IUnitOfWorkContext? Context { get; private set; }

        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            Context = unitOfWorkContext;
            Assert.DoesNotContain("password material", entry.Metadata.GetRawText(), StringComparison.Ordinal);
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

        internal int CreateCalls { get; private set; }

        public OneTimeChallengeSecret Create()
        {
            CreateCalls++;
            return Secret;
        }

        public IReadOnlyList<OneTimeChallengeCandidate> HashCandidates(EntityId challenge) =>
            [new OneTimeChallengeCandidate(Secret.Hash, Secret.PepperVersion)];

        public bool Verify(EntityId challenge, byte[] expectedHash, short pepperVersion) =>
            throw Unexpected();
    }

    private sealed class RecordingTotpAuthenticator : ITotpAuthenticator
    {
        internal bool MatchResult { get; set; } = true;

        internal long MatchedStep { get; } = 2_000_000;

        internal int MatchCalls { get; private set; }

        public TotpProvisioningSecret CreateProvisioningSecret(string accountName) => new(
            "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP",
            "otpauth://totp/PoolAI:test%40example.test?secret=opaque");

        public string BuildProvisioningUri(string base32Secret, string accountName) =>
            "otpauth://totp/PoolAI:test%40example.test?secret=opaque";

        public bool TryMatchStep(
            string base32Secret,
            string code,
            DateTimeOffset timestamp,
            out long matchedStep)
        {
            MatchCalls++;
            matchedStep = MatchedStep;
            return MatchResult;
        }
    }

    private sealed class RecordingSecretEnvelope : ITotpSecretEnvelope
    {
        internal int EncryptCalls { get; private set; }

        internal int DecryptCalls { get; private set; }

        public JsonElement Encrypt(
            string base32Secret,
            TotpSecretEnvelopeTarget target,
            EntityId targetId)
        {
            EncryptCalls++;
            return Envelope(target.ToString());
        }

        public string Decrypt(
            JsonElement envelope,
            TotpSecretEnvelopeTarget target,
            EntityId targetId)
        {
            DecryptCalls++;
            return "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
        }
    }

    private sealed class RecordingRecoveryGenerator : ITotpRecoveryCodeGenerator
    {
        internal int Count { get; set; } = 8;

        internal int CreateCalls { get; private set; }

        public IReadOnlyList<TotpRecoveryCodeSecret> CreateBatch()
        {
            CreateCalls++;
            return RecoveryCodes()
                .Take(Count)
                .Select((code, index) => new TotpRecoveryCodeSecret(
                    code,
                    Enumerable.Repeat(checked((byte)(index + 1)), 32).ToArray(),
                    PepperVersion: 1))
                .ToArray();
        }
    }

    private sealed class RecordingRecoveryEnvelope : ITotpRecoveryCodeEnvelope
    {
        internal IReadOnlyList<string> DecryptResult { get; set; } = RecoveryCodes();

        internal int EncryptCalls { get; private set; }

        internal int DecryptCalls { get; private set; }

        public JsonElement Encrypt(
            IReadOnlyList<string> recoveryCodes,
            EntityId oneTimeTokenId,
            IdempotencySecretBinding binding)
        {
            EncryptCalls++;
            return Envelope("recovery");
        }

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

        internal int EncryptCalls { get; private set; }

        internal int DecryptCalls { get; private set; }

        public JsonElement Encrypt(
            TotpSetupResponseSecret response,
            EntityId oneTimeTokenId,
            IdempotencySecretBinding binding)
        {
            EncryptCalls++;
            return Envelope("setup-response");
        }

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
        "The personal-security failure scenario called an unexpected dependency.");
}
#pragma warning restore MA0051
#pragma warning restore MA0004
