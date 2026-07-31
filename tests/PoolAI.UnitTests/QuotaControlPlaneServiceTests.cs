#pragma warning disable MA0051 // Test doubles keep the complete transactional protocol visible.
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Domain;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class QuotaControlPlaneServiceTests
{
    private static readonly EntityId GroupId = EntityId.New();
    private static readonly EntityId ActorId = EntityId.New();
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CurrentQueryUsesReadPolicyAndPreservesBigIntegerOverage()
    {
        BigInteger consumed = BigInteger.Parse(
            "999999999999999999999999999999999999999999999999999999999999999999999999999999",
            CultureInfo.InvariantCulture);
        RecordingQuotaRepository repository = new()
        {
            Current = Resource(
                total: 100,
                consumed,
                reserved: 7,
                GroupPoolQuotaStatus.Exhausted,
                version: 4),
        };
        TestContext context = CreateContext(repository);

        foreach (GroupControlRole role in new[]
                 {
                     GroupControlRole.Admin,
                     GroupControlRole.Operator,
                     GroupControlRole.Auditor,
                 })
        {
            Result<GroupQuotaView> result = await context.Service.ExecuteAsync(
                new GetGroupQuotaQuery(new GroupActor(ActorId, role, 1), GroupId),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(consumed, result.Value.ConsumedTokens);
            Assert.Equal(consumed - 100, result.Value.OverageTokens);
            Assert.Equal(BigInteger.Zero, result.Value.RemainingTokens);
        }

        Result<GroupQuotaView> denied = await context.Service.ExecuteAsync(
            new GetGroupQuotaQuery(
                new GroupActor(ActorId, GroupControlRole.User, 1),
                GroupId),
            CancellationToken.None);

        Assert.True(denied.IsFailure);
        Assert.Equal(GroupErrorCodes.RoleRequired, denied.Error.Code);
        Assert.Equal(3, repository.GetCalls);
        Assert.Empty(context.Audit.Entries);
        Assert.Equal(0, context.Units.BeginCalls);
    }

    [Fact]
    public async Task NonAdminMutationsCommitExactAppendOnlyDenialAudits()
    {
        RecordingQuotaRepository repository = new();
        TestContext context = CreateContext(repository);
        (GroupControlRole Role, AuditActorType ActorType, bool Reset)[] cases =
        [
            (GroupControlRole.Operator, AuditActorType.Operator, false),
            (GroupControlRole.Auditor, AuditActorType.Auditor, true),
            (GroupControlRole.User, AuditActorType.User, false),
        ];

        foreach ((GroupControlRole role, AuditActorType actorType, bool reset) in cases)
        {
            GroupActor actor = new(ActorId, role, 1);
            Result<GroupQuotaCommandOutcome> result = reset
                ? await context.Service.ExecuteAsync(
                    Reset(actor, "denied-reset"),
                    CancellationToken.None)
                : await context.Service.ExecuteAsync(
                    Adjust(actor, "denied-adjust"),
                    CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal(GroupErrorCodes.RoleRequired, result.Error.Code);
            AuditEntry entry = context.Audit.Entries[^1];
            Assert.Equal(actorType, entry.ActorType);
            Assert.Equal(ActorId, entry.ActorUserId);
            Assert.Equal(GroupId, entry.TargetId);
            Assert.Equal("group_quota", entry.TargetType);
            Assert.Equal(
                reset
                    ? "groupquota.quota.period_reset_denied"
                    : "groupquota.quota.total_adjust_denied",
                entry.Action);
            Assert.Null(entry.BeforeState);
            Assert.Null(entry.AfterState);
            Assert.Equal(
                GroupErrorCodes.RoleRequired,
                entry.Metadata.GetProperty("denial_code").GetString());
        }

        Assert.Equal(cases.Length, context.Units.BeginCalls);
        Assert.Equal(cases.Length, context.Units.CommitCalls);
        Assert.Equal(0, repository.AdjustCalls);
        Assert.Equal(0, repository.ResetCalls);
        Assert.Empty(context.Idempotency.Requests);
    }

    [Fact]
    public async Task AuthorizationPreflightAuditsBeforeTransportValidation()
    {
        RecordingQuotaRepository repository = new();
        TestContext context = CreateContext(repository);
        GroupActor actor = new(ActorId, GroupControlRole.Operator, 1);

        Result<bool> result = await context.Service.ExecuteAsync(
            new AuthorizeQuotaMutationCommand(
                EntityId.New(),
                actor,
                GroupId,
                QuotaMutationOperation.AdjustTotal,
                "127.0.0.1",
                "sha256:test"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(GroupErrorCodes.RoleRequired, result.Error.Code);
        Assert.Equal(1, context.Units.CommitCalls);
        AuditEntry entry = Assert.Single(context.Audit.Entries);
        Assert.Equal("groupquota.quota.total_adjust_denied", entry.Action);
        Assert.Equal(AuditActorType.Operator, entry.ActorType);
        Assert.Empty(context.Idempotency.Requests);
        Assert.Equal(0, repository.AdjustCalls);
    }

    [Fact]
    public async Task AdjustAndResetCommitCanonicalAuditAndIdempotencyOnce()
    {
        GroupQuotaResource adjustBefore = Resource(
            total: 100,
            consumed: 40,
            reserved: 10,
            GroupPoolQuotaStatus.Active,
            version: 3);
        GroupQuotaResource adjustAfter = adjustBefore with
        {
            Status = GroupPoolQuotaStatus.Exhausted,
            TotalTokens = 30,
            RemainingTokens = BigInteger.Zero,
            OverageTokens = 10,
            Version = 4,
            UpdatedAt = Now.AddMinutes(1),
        };
        RecordingQuotaRepository adjustRepository = new()
        {
            NextAdjust = new QuotaWriteResult(
                QuotaWriteDisposition.Written,
                adjustBefore,
                adjustAfter,
                4),
        };
        TestContext adjustContext = CreateContext(adjustRepository);
        const string adjustKey = "adjust-secret-idempotency-key";

        string exactReason = string.Concat(
            "  authorized\n",
            string.Concat(Enumerable.Repeat("😀", 300)),
            "  ");
        Result<GroupQuotaCommandOutcome> adjusted = await adjustContext.Service.ExecuteAsync(
            Adjust(Admin(), adjustKey, expectedVersion: 3, total: 30) with
            {
                Reason = exactReason,
            },
            CancellationToken.None);

        Assert.True(adjusted.IsSuccess);
        Assert.False(adjusted.Value.IsReplay);
        Assert.Equal("\"v4\"", adjusted.Value.ETag);
        Assert.Equal(BigInteger.Zero, adjusted.Value.Value.RemainingTokens);
        Assert.Equal(new BigInteger(10), adjusted.Value.Value.OverageTokens);
        Assert.Equal(1, adjustContext.Units.CommitCalls);
        Assert.Single(adjustContext.Idempotency.Completions);
        Assert.Equal(
            CommandIdempotencyTerminalStatus.Completed,
            adjustContext.Idempotency.Completions[0].TerminalStatus);
        Assert.Single(adjustContext.Audit.Entries);
        AuditEntry adjustAudit = adjustContext.Audit.Entries[0];
        Assert.Equal("groupquota.quota.total_adjusted", adjustAudit.Action);
        Assert.Equal("100", adjustAudit.BeforeState!.Value.GetProperty("total_tokens").GetString());
        Assert.Equal("30", adjustAudit.AfterState!.Value.GetProperty("total_tokens").GetString());
        Assert.DoesNotContain(
            adjustKey,
            adjustAudit.Metadata.GetRawText(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            adjustKey,
            adjustRepository.LastAdjust!.EventIdempotencyKey,
            StringComparison.Ordinal);
        Assert.Equal(exactReason.Trim(), adjustRepository.LastAdjust.Reason);
        Assert.Equal(64 + "total_adjusted:".Length, adjustRepository.LastAdjust.EventIdempotencyKey.Length);

        EntityId newPeriodId = EntityId.New();
        GroupQuotaResource resetBefore = adjustAfter;
        GroupQuotaResource resetAfter = new(
            GroupId,
            newPeriodId,
            GroupPoolQuotaStatus.Disabled,
            new BigInteger(200),
            BigInteger.Zero,
            BigInteger.Zero,
            new BigInteger(200),
            BigInteger.Zero,
            Now.AddMinutes(2),
            null,
            5,
            Now.AddMinutes(2));
        RecordingQuotaRepository resetRepository = new()
        {
            ResetFactory = write =>
            {
                resetAfter = resetAfter with { PeriodId = write.NewPeriodId };
                return new QuotaWriteResult(
                    QuotaWriteDisposition.Written,
                    resetBefore,
                    resetAfter,
                    5);
            },
        };
        TestContext resetContext = CreateContext(resetRepository);
        resetContext.Idempotency.ReplayCompletedRequests = true;
        const string resetKey = "reset-secret-idempotency-key";
        const string resetReason = "  authorized quota reset  ";
        ResetGroupQuotaCommand resetCommand = Reset(
            Admin(),
            resetKey,
            expectedVersion: 4,
            total: 200) with
        {
            Reason = resetReason,
        };

        Result<GroupQuotaCommandOutcome> reset = await resetContext.Service.ExecuteAsync(
            resetCommand,
            CancellationToken.None);

        Assert.True(reset.IsSuccess);
        Assert.False(reset.Value.IsReplay);
        Assert.Equal("\"v5\"", reset.Value.ETag);
        Assert.Equal(5, reset.Value.Value.Version);
        Assert.Equal(BigInteger.Zero, reset.Value.Value.ConsumedTokens);
        Assert.Equal(1, resetContext.Units.CommitCalls);
        CommandIdempotencyCompletion resetCompletion =
            Assert.Single(resetContext.Idempotency.Completions);
        Assert.Equal(CommandIdempotencyTerminalStatus.Completed, resetCompletion.TerminalStatus);
        Assert.Equal(200, resetCompletion.ResponseStatus);
        Assert.Equal("group_quota", resetCompletion.ResourceType);
        Assert.Equal(GroupId, resetCompletion.ResourceId);
        AuditEntry resetAudit = Assert.Single(resetContext.Audit.Entries);
        Assert.Equal("groupquota.quota.period_reset", resetAudit.Action);
        Assert.Equal("30", resetAudit.BeforeState!.Value.GetProperty("total_tokens").GetString());
        Assert.Equal("200", resetAudit.AfterState!.Value.GetProperty("total_tokens").GetString());
        Assert.Equal(resetReason.Trim(), resetAudit.Reason);
        Assert.DoesNotContain(resetKey, resetAudit.Metadata.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(resetReason.Trim(), resetRepository.LastReset!.Reason);
        Assert.DoesNotContain(
            resetKey,
            resetRepository.LastReset.EventIdempotencyKey,
            StringComparison.Ordinal);
        Assert.Equal(
            64 + "period_reset:".Length,
            resetRepository.LastReset.EventIdempotencyKey.Length);
        Assert.NotEqual(resetBefore.PeriodId, reset.Value.Value.PeriodId);

        resetRepository.ThrowOnWrite = true;
        Result<GroupQuotaCommandOutcome> resetReplay =
            await resetContext.Service.ExecuteAsync(
                resetCommand,
                CancellationToken.None);
        Assert.True(resetReplay.IsSuccess);
        Assert.True(resetReplay.Value.IsReplay);
        Assert.Equal(reset.Value.Value, resetReplay.Value.Value);
        Assert.Equal(reset.Value.ETag, resetReplay.Value.ETag);
        Assert.Equal(1, resetRepository.ResetCalls);
        Assert.Equal(1, resetContext.Units.CommitCalls);
        Assert.Single(resetContext.Idempotency.Completions);
        Assert.Single(resetContext.Audit.Entries);
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public async Task QuotaMutationRejectsIsolatedUtf16SurrogatesBeforeWriting(
        int invalidCodeUnit)
    {
        RecordingQuotaRepository repository = new();
        TestContext context = CreateContext(repository);
        string invalidReason = new((char)invalidCodeUnit, 1);

        Result<GroupQuotaCommandOutcome> result = await context.Service.ExecuteAsync(
            Adjust(Admin(), "invalid-unicode") with
            {
                Reason = invalidReason,
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(GroupErrorCodes.ValidationFailed, result.Error.Code);
        Assert.Equal(0, context.Units.BeginCalls);
        Assert.Empty(context.Idempotency.Requests);
        Assert.Empty(context.Audit.Entries);
        Assert.Equal(0, repository.AdjustCalls);
    }

    [Fact]
    public async Task ExactReplayPrecedesRepositoryAndMalformedReplayFailsClosed()
    {
        GroupQuotaResource before = Resource(
            total: 100,
            consumed: 10,
            reserved: 5,
            GroupPoolQuotaStatus.Active,
            version: 7);
        GroupQuotaResource after = before with
        {
            TotalTokens = 120,
            RemainingTokens = 105,
            Version = 8,
            UpdatedAt = Now.AddMinutes(1),
        };
        RecordingQuotaRepository repository = new()
        {
            NextAdjust = new QuotaWriteResult(
                QuotaWriteDisposition.Written,
                before,
                after,
                8),
        };
        TestContext context = CreateContext(repository);
        context.Idempotency.ReplayCompletedRequests = true;
        AdjustGroupQuotaCommand command = Adjust(
            Admin(),
            "exact-replay",
            expectedVersion: 7,
            total: 120);

        Result<GroupQuotaCommandOutcome> first =
            await context.Service.ExecuteAsync(command, CancellationToken.None);
        repository.ThrowOnWrite = true;
        Result<GroupQuotaCommandOutcome> replay =
            await context.Service.ExecuteAsync(command, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(1, repository.AdjustCalls);
        Assert.Equal(1, context.Units.CommitCalls);
        Assert.Collection(
            context.Trace,
            value => Assert.Equal("idempotency", value),
            value => Assert.Equal("repository", value),
            value => Assert.Equal("idempotency", value),
            value => Assert.Equal("idempotency", value));

        CommandIdempotencyResponse stored = context.Idempotency.CompletedResponse!;
        JsonElement maliciousHeaders = JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = "\"v999\"",
            });
        context.Idempotency.NextAcquire = CommandIdempotencyAcquireResult.Replay(
            stored with { Headers = maliciousHeaders });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await context.Service
                .ExecuteAsync(command, CancellationToken.None)
                .ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(1, repository.AdjustCalls);
    }

    [Fact]
    public async Task RepositoryDispositionsBecomeDurable404409And412Failures()
    {
        (QuotaWriteDisposition Disposition, int Status, string Code, long? Version)[] cases =
        [
            (QuotaWriteDisposition.NotFound, 404, GroupErrorCodes.ResourceNotFound, null),
            (QuotaWriteDisposition.Archived, 409, GroupErrorCodes.ResourceConflict, null),
            (
                QuotaWriteDisposition.IdempotencyConflict,
                409,
                GroupErrorCodes.IdempotencyConflict,
                null),
            (QuotaWriteDisposition.Conflict, 409, GroupErrorCodes.ResourceConflict, null),
            (QuotaWriteDisposition.VersionConflict, 412, GroupErrorCodes.VersionConflict, 17),
        ];

        foreach ((QuotaWriteDisposition disposition, int status, string code, long? version) in cases)
        {
            RecordingQuotaRepository repository = new()
            {
                NextAdjust = new QuotaWriteResult(
                    disposition,
                    CurrentVersion: version),
            };
            TestContext context = CreateContext(repository);

            Result<GroupQuotaCommandOutcome> result = await context.Service.ExecuteAsync(
                Adjust(Admin(), $"failure-{status}-{disposition}"),
                CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal(code, result.Error.Code);
            Assert.Equal(version is null ? null : $"\"v{version}\"", result.Error.ETag);
            Assert.Equal(1, context.Units.CommitCalls);
            CommandIdempotencyCompletion completion =
                Assert.Single(context.Idempotency.Completions);
            Assert.Equal(CommandIdempotencyTerminalStatus.Failed, completion.TerminalStatus);
            Assert.Equal(status, completion.ResponseStatus);
            Assert.Empty(context.Audit.Entries);
        }
    }

    [Fact]
    public void ConstructorRejectsEveryMissingDependency()
    {
        RecordingQuotaRepository repository = new();
        RecordingUnitOfWorkFactory units = new();
        RecordingIdempotencyStore idempotency = new([]);
        RecordingAuditAppender audit = new();
        GroupQuotaPolicy policy = Policy();

        Assert.Throws<ArgumentNullException>(() =>
            _ = new QuotaControlPlaneService(null!, units, idempotency, audit, policy));
        Assert.Throws<ArgumentNullException>(() =>
            _ = new QuotaControlPlaneService(repository, null!, idempotency, audit, policy));
        Assert.Throws<ArgumentNullException>(() =>
            _ = new QuotaControlPlaneService(repository, units, null!, audit, policy));
        Assert.Throws<ArgumentNullException>(() =>
            _ = new QuotaControlPlaneService(repository, units, idempotency, null!, policy));
        Assert.Throws<ArgumentNullException>(() =>
            _ = new QuotaControlPlaneService(repository, units, idempotency, audit, null!));
    }

    [Fact]
    public async Task CurrentQueryRejectsInvalidIdentityAndMissingQuota()
    {
        RecordingQuotaRepository repository = new();
        TestContext context = CreateContext(repository);

        Result<GroupQuotaView> invalidGroup = await context.Service.ExecuteAsync(
            new GetGroupQuotaQuery(
                Admin(),
                default),
            CancellationToken.None);
        Result<GroupQuotaView> invalidTokenVersion = await context.Service.ExecuteAsync(
            new GetGroupQuotaQuery(
                Admin() with { TokenVersion = 0 },
                GroupId),
            CancellationToken.None);
        Result<GroupQuotaView> missing = await context.Service.ExecuteAsync(
            new GetGroupQuotaQuery(Admin(), GroupId),
            CancellationToken.None);

        Assert.True(invalidGroup.IsFailure);
        Assert.Equal(GroupErrorCodes.InvalidRequest, invalidGroup.Error.Code);
        Assert.True(invalidTokenVersion.IsFailure);
        Assert.Equal(GroupErrorCodes.RoleRequired, invalidTokenVersion.Error.Code);
        Assert.True(missing.IsFailure);
        Assert.Equal(GroupErrorCodes.ResourceNotFound, missing.Error.Code);
        Assert.Equal(1, repository.GetCalls);
    }

    [Fact]
    public async Task CurrentQueryFailsClosedForEveryMalformedCanonicalSnapshot()
    {
        GroupQuotaResource valid = Resource(
            total: 100,
            consumed: 10,
            reserved: 5,
            GroupPoolQuotaStatus.Active,
            version: 2);
        GroupQuotaResource[] invalid =
        [
            valid with { GroupId = default },
            valid with { PeriodId = default },
            valid with { TotalTokens = BigInteger.Zero },
            valid with
            {
                TotalTokens = new BigInteger(9_007_199_254_740_992),
                RemainingTokens = new BigInteger(9_007_199_254_740_977),
            },
            valid with
            {
                ConsumedTokens = BigInteger.MinusOne,
                RemainingTokens = new BigInteger(96),
            },
            valid with
            {
                ReservedTokens = BigInteger.MinusOne,
                RemainingTokens = new BigInteger(91),
            },
            valid with { RemainingTokens = new BigInteger(86) },
            valid with { OverageTokens = BigInteger.One },
            valid with { PeriodEndedAt = Now.AddMinutes(1) },
            valid with { Version = 0 },
            valid with { UpdatedAt = Now.AddTicks(-1) },
            valid with { Status = (GroupPoolQuotaStatus)int.MaxValue },
        ];

        foreach (GroupQuotaResource snapshot in invalid)
        {
            RecordingQuotaRepository repository = new() { Current = snapshot };
            TestContext context = CreateContext(repository);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await context.Service.ExecuteAsync(
                    new GetGroupQuotaQuery(Admin(), GroupId),
                    CancellationToken.None).ConfigureAwait(true))
                .ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task MutationValidationRejectsUnsafeNumbersAndReasonsBeforeAcquire()
    {
        AdjustGroupQuotaCommand valid = Adjust(Admin(), "validation");
        AdjustGroupQuotaCommand[] invalidAdjustments =
        [
            valid with { ExpectedVersion = 0 },
            valid with { NewTotalTokens = 0 },
            valid with { NewTotalTokens = -1 },
            valid with { NewTotalTokens = long.MaxValue },
            valid with { Reason = null! },
            valid with { Reason = " \r\n\t " },
            valid with { Reason = new string('a', 501) },
        ];

        foreach (AdjustGroupQuotaCommand command in invalidAdjustments)
        {
            RecordingQuotaRepository repository = new();
            TestContext context = CreateContext(repository);

            Result<GroupQuotaCommandOutcome> result = await context.Service.ExecuteAsync(
                command,
                CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal(GroupErrorCodes.ValidationFailed, result.Error.Code);
            Assert.Equal(0, context.Units.BeginCalls);
            Assert.Empty(context.Idempotency.Requests);
            Assert.Equal(0, repository.AdjustCalls);
        }

        TestContext resetContext = CreateContext(new RecordingQuotaRepository());
        Result<GroupQuotaCommandOutcome> invalidReset =
            await resetContext.Service.ExecuteAsync(
                Reset(Admin(), "invalid-reset") with { Reason = "   " },
                CancellationToken.None);

        Assert.True(invalidReset.IsFailure);
        Assert.Equal(GroupErrorCodes.ValidationFailed, invalidReset.Error.Code);
        Assert.Equal(0, resetContext.Units.BeginCalls);
    }

    [Fact]
    public async Task AuthorizationRejectsEveryMalformedPreflightWithoutAudit()
    {
        AuthorizeQuotaMutationCommand valid = new(
            EntityId.New(),
            Admin(),
            GroupId,
            QuotaMutationOperation.AdjustTotal,
            "127.0.0.1",
            "sha256:test");
        AuthorizeQuotaMutationCommand[] invalid =
        [
            valid with { RequestId = default },
            valid with { Actor = Admin() with { UserId = default } },
            valid with { Actor = Admin() with { TokenVersion = 0 } },
            valid with { Actor = Admin() with { Role = (GroupControlRole)int.MaxValue } },
            valid with { GroupId = default },
            valid with { Operation = (QuotaMutationOperation)int.MaxValue },
        ];

        foreach (AuthorizeQuotaMutationCommand command in invalid)
        {
            TestContext context = CreateContext(new RecordingQuotaRepository());

            Result<bool> result = await context.Service.ExecuteAsync(
                command,
                CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal(GroupErrorCodes.InvalidRequest, result.Error.Code);
            Assert.Equal(0, context.Units.BeginCalls);
            Assert.Empty(context.Audit.Entries);
        }
    }

    [Fact]
    public async Task AcquireConflictBusyAndUnknownDispositionFailBeforeRepository()
    {
        (CommandIdempotencyAcquireResult Acquire, string Code, long? RetryAfter)[] cases =
        [
            (
                CommandIdempotencyAcquireResult.Conflict,
                GroupErrorCodes.IdempotencyConflict,
                null),
            (
                CommandIdempotencyAcquireResult.Busy,
                GroupErrorCodes.CoordinationUnavailable,
                1),
        ];

        foreach ((CommandIdempotencyAcquireResult acquire, string code, long? retryAfter) in cases)
        {
            RecordingQuotaRepository repository = new();
            TestContext context = CreateContext(repository);
            context.Idempotency.NextAcquire = acquire;

            Result<GroupQuotaCommandOutcome> result = await context.Service.ExecuteAsync(
                Adjust(Admin(), $"acquire-{code}"),
                CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal(code, result.Error.Code);
            Assert.Equal(retryAfter, result.Error.RetryAfterSeconds);
            Assert.Equal(0, repository.AdjustCalls);
            Assert.Equal(0, context.Units.CommitCalls);
        }

        TestContext invalidContext = CreateContext(new RecordingQuotaRepository());
        invalidContext.Idempotency.NextAcquire = new CommandIdempotencyAcquireResult(
            (CommandIdempotencyDisposition)int.MaxValue,
            Lease: null,
            Response: null);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await invalidContext.Service.ExecuteAsync(
                Adjust(Admin(), "acquire-invalid"),
                CancellationToken.None).ConfigureAwait(true))
            .ConfigureAwait(true);
    }

    [Theory]
    [InlineData((int)QuotaWriteDisposition.NotFound, null)]
    [InlineData((int)QuotaWriteDisposition.VersionConflict, 17L)]
    public async Task DurableFailuresReplayWithoutASecondWrite(
        int dispositionValue,
        long? currentVersion)
    {
        QuotaWriteDisposition disposition = (QuotaWriteDisposition)dispositionValue;
        RecordingQuotaRepository repository = new()
        {
            NextAdjust = new QuotaWriteResult(
                disposition,
                CurrentVersion: currentVersion),
        };
        TestContext context = CreateContext(repository);
        context.Idempotency.ReplayCompletedRequests = true;
        AdjustGroupQuotaCommand command = Adjust(
            Admin(),
            $"durable-{disposition}");

        Result<GroupQuotaCommandOutcome> first =
            await context.Service.ExecuteAsync(command, CancellationToken.None);
        Result<GroupQuotaCommandOutcome> replay =
            await context.Service.ExecuteAsync(command, CancellationToken.None);

        Assert.True(first.IsFailure);
        Assert.True(replay.IsFailure);
        Assert.Equal(first.Error, replay.Error);
        Assert.Equal(1, repository.AdjustCalls);
        Assert.Equal(1, context.Units.CommitCalls);
    }

    [Fact]
    public async Task FailureReplayRejectsMalformedStoredResponses()
    {
        CommandIdempotencyResponse notFound = FailureReplayResponse(
            404,
            GroupErrorCodes.ResourceNotFound);
        CommandIdempotencyResponse versionConflict = FailureReplayResponse(
            412,
            GroupErrorCodes.VersionConflict,
            "\"v17\"");
        JsonElement nonNullEnvelope = JsonSerializer.SerializeToElement(new
        {
            forbidden = true,
        });
        JsonElement extraHeaders = JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = "\"v17\"",
                ["X-Forbidden"] = "true",
            });
        ResultErrorPresentation wrongPresentation = new(
            GroupErrorCodes.ResourceNotFound,
            404,
            "Wrong title",
            "The requested resource was not found.",
            Retryable: false);
        CommandIdempotencyResponse[] malformed =
        [
            notFound with { Body = null },
            notFound with { Body = JsonSerializer.SerializeToElement<object?>(null) },
            notFound with { BodyEnvelope = nonNullEnvelope },
            notFound with { ResourceType = "group_quota" },
            notFound with { ResourceId = GroupId },
            notFound with { Status = 409 },
            notFound with { Body = FailureReplayBody(wrongPresentation) },
            notFound with
            {
                Headers = JsonSerializer.SerializeToElement(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ETag"] = "\"v1\"",
                    }),
            },
            notFound with { Headers = JsonSerializer.SerializeToElement("not-an-object") },
            versionConflict with { Headers = EmptyHeaders() },
            versionConflict with { Headers = extraHeaders },
        ];

        foreach (CommandIdempotencyResponse response in malformed)
        {
            TestContext context = ReplayContext(response);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await context.Service.ExecuteAsync(
                    Adjust(Admin(), "malformed-failure"),
                    CancellationToken.None).ConfigureAwait(true))
                .ConfigureAwait(true);
        }

        ResultErrorPresentation unsupported = new(
            "unsupported",
            418,
            "Unsupported",
            "Unsupported",
            Retryable: false);
        TestContext unsupportedContext = ReplayContext(new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Failed,
            418,
            FailureReplayBody(unsupported),
            BodyEnvelope: null,
            EmptyHeaders(),
            ResourceType: null,
            ResourceId: null));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await unsupportedContext.Service.ExecuteAsync(
                Adjust(Admin(), "unsupported-failure"),
                CancellationToken.None).ConfigureAwait(true))
            .ConfigureAwait(true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("\"x1\"")]
    [InlineData("\"v01\"")]
    [InlineData("\"v1x")]
    [InlineData("\"v999999999999999999999999999999999999999999999999999999\"")]
    public async Task FailureReplayRejectsEveryNonCanonicalEtagShape(string etag)
    {
        TestContext context = ReplayContext(FailureReplayResponse(
            412,
            GroupErrorCodes.VersionConflict,
            etag));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await context.Service.ExecuteAsync(
                Adjust(Admin(), $"bad-etag-{etag.Length}"),
                CancellationToken.None).ConfigureAwait(true))
            .ConfigureAwait(true);
    }

    [Theory]
    [InlineData("active", "100", "10", "5", "85", "0")]
    [InlineData("exhausted", "100", "100", "0", "0", "0")]
    [InlineData("disabled", "100", "0", "0", "100", "0")]
    public async Task SuccessReplayAcceptsEveryCanonicalQuotaStatus(
        string status,
        string total,
        string consumed,
        string reserved,
        string remaining,
        string overage)
    {
        TestContext context = ReplayContext(SuccessReplayResponse(
            status,
            total,
            consumed,
            reserved,
            remaining,
            overage));

        Result<GroupQuotaCommandOutcome> result = await context.Service.ExecuteAsync(
            Adjust(Admin(), $"success-{status}"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsReplay);
        Assert.Equal(status, result.Value.Value.Status switch
        {
            GroupPoolQuotaStatus.Active => "active",
            GroupPoolQuotaStatus.Exhausted => "exhausted",
            GroupPoolQuotaStatus.Disabled => "disabled",
            _ => throw new InvalidOperationException("Unexpected quota status."),
        });
    }

    [Fact]
    public async Task SuccessReplayRejectsMalformedBodyMetadataAndTokenText()
    {
        CommandIdempotencyResponse valid = SuccessReplayResponse(
            "active",
            "100",
            "10",
            "5",
            "85",
            "0");
        CommandIdempotencyResponse[] malformed =
        [
            valid with { Body = null },
            valid with { Body = JsonSerializer.SerializeToElement<object?>(null) },
            valid with { TerminalStatus = (CommandIdempotencyTerminalStatus)int.MaxValue },
            valid with { Status = 201 },
            valid with
            {
                BodyEnvelope = JsonSerializer.SerializeToElement(new { forbidden = true }),
            },
            valid with { ResourceType = "group" },
            valid with { ResourceId = EntityId.New() },
            valid with { ResourceId = null },
            valid with { Headers = EmptyHeaders() },
            valid with { Headers = JsonSerializer.SerializeToElement("not-an-object") },
            valid with
            {
                Headers = JsonSerializer.SerializeToElement(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ETag"] = "\"v2\"",
                        ["X-Forbidden"] = "true",
                    }),
            },
            SuccessReplayResponse("unknown", "100", "10", "5", "85", "0"),
            SuccessReplayResponse("active", "", "0", "0", "0", "0"),
            SuccessReplayResponse(
                "active",
                new string('9', 79),
                "0",
                "0",
                "0",
                "0"),
            SuccessReplayResponse("active", "01", "0", "0", "0", "0"),
            SuccessReplayResponse("active", "1a", "0", "0", "0", "0"),
            SuccessReplayResponse("active", "0", "0", "0", "0", "0"),
            SuccessReplayResponse(
                "active",
                new string('9', 78),
                "0",
                "0",
                new string('9', 78),
                "0"),
        ];

        foreach (CommandIdempotencyResponse response in malformed)
        {
            TestContext context = ReplayContext(response);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await context.Service.ExecuteAsync(
                    Adjust(Admin(), "malformed-success"),
                    CancellationToken.None).ConfigureAwait(true))
                .ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task MutationFailsClosedWhenCanonicalWriteStateOrLeaseIsLost()
    {
        GroupQuotaResource before = Resource(
            total: 100,
            consumed: 10,
            reserved: 5,
            GroupPoolQuotaStatus.Active,
            version: 1);
        GroupQuotaResource after = before with
        {
            TotalTokens = 120,
            RemainingTokens = 105,
            Version = 2,
            UpdatedAt = Now.AddMinutes(1),
        };
        QuotaWriteResult[] malformedWrites =
        [
            new(QuotaWriteDisposition.Written, Before: null, After: after),
            new(QuotaWriteDisposition.Written, before, After: null),
            new((QuotaWriteDisposition)int.MaxValue),
        ];

        foreach (QuotaWriteResult write in malformedWrites)
        {
            RecordingQuotaRepository repository = new() { NextAdjust = write };
            TestContext context = CreateContext(repository);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await context.Service.ExecuteAsync(
                    Adjust(Admin(), "malformed-write"),
                    CancellationToken.None).ConfigureAwait(true))
                .ConfigureAwait(true);
        }

        RecordingQuotaRepository successRepository = new()
        {
            NextAdjust = new QuotaWriteResult(
                QuotaWriteDisposition.Written,
                before,
                after,
                2),
        };
        TestContext successContext = CreateContext(successRepository);
        successContext.Idempotency.CompleteResult = false;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await successContext.Service.ExecuteAsync(
                Adjust(Admin(), "lost-success-lease"),
                CancellationToken.None).ConfigureAwait(true))
            .ConfigureAwait(true);

        RecordingQuotaRepository failureRepository = new()
        {
            NextAdjust = new QuotaWriteResult(QuotaWriteDisposition.NotFound),
        };
        TestContext failureContext = CreateContext(failureRepository);
        failureContext.Idempotency.CompleteResult = false;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await failureContext.Service.ExecuteAsync(
                Adjust(Admin(), "lost-failure-lease"),
                CancellationToken.None).ConfigureAwait(true))
            .ConfigureAwait(true);

        Assert.Equal(0, successContext.Units.CommitCalls);
        Assert.Equal(0, failureContext.Units.CommitCalls);
    }

    private static TestContext CreateContext(RecordingQuotaRepository repository)
    {
        List<string> trace = [];
        repository.Trace = trace;
        RecordingUnitOfWorkFactory units = new();
        RecordingIdempotencyStore idempotency = new(trace);
        RecordingAuditAppender audit = new();
        QuotaControlPlaneService service = new(
            repository,
            units,
            idempotency,
            audit,
            Policy());
        return new TestContext(service, units, idempotency, audit, trace);
    }

    private static GroupQuotaPolicy Policy() =>
        new(Enumerable.Repeat((byte)0x5a, 32).ToArray());

    private static TestContext ReplayContext(CommandIdempotencyResponse response)
    {
        TestContext context = CreateContext(new RecordingQuotaRepository());
        context.Idempotency.NextAcquire =
            CommandIdempotencyAcquireResult.Replay(response);
        return context;
    }

    private static CommandIdempotencyResponse FailureReplayResponse(
        int status,
        string code,
        string? etag = null)
    {
        ResultErrorPresentation presentation = (code, status) switch
        {
            (GroupErrorCodes.ResourceNotFound, 404) => new(
                code,
                status,
                "Resource not found",
                "The requested resource was not found.",
                Retryable: false),
            (GroupErrorCodes.VersionConflict, 412) => new(
                code,
                status,
                "Version conflict",
                "The resource version no longer matches; retrieve it again before retrying.",
                Retryable: true),
            _ => throw new ArgumentOutOfRangeException(nameof(code)),
        };
        return new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Failed,
            status,
            FailureReplayBody(presentation),
            BodyEnvelope: null,
            etag is null ? EmptyHeaders() : Headers(etag),
            ResourceType: null,
            ResourceId: null);
    }

    private static JsonElement FailureReplayBody(
        ResultErrorPresentation presentation) =>
        JsonSerializer.SerializeToElement(new
        {
            Description = "Stored quota failure.",
            Presentation = presentation,
        });

    private static CommandIdempotencyResponse SuccessReplayResponse(
        string status,
        string total,
        string consumed,
        string reserved,
        string remaining,
        string overage,
        long version = 1)
    {
        JsonElement body = JsonSerializer.SerializeToElement(new
        {
            GroupId = GroupId.Value,
            PeriodId = EntityId.New().Value,
            Status = status,
            TotalTokens = total,
            ConsumedTokens = consumed,
            ReservedTokens = reserved,
            RemainingTokens = remaining,
            OverageTokens = overage,
            PeriodStartedAt = Now,
            PeriodEndedAt = (DateTimeOffset?)null,
            Version = version,
            UpdatedAt = Now,
        });
        return new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Completed,
            200,
            body,
            BodyEnvelope: null,
            Headers($"\"v{version}\""),
            "group_quota",
            GroupId);
    }

    private static JsonElement Headers(string etag) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = etag,
            });

    private static JsonElement EmptyHeaders() =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static GroupActor Admin() =>
        new(ActorId, GroupControlRole.Admin, TokenVersion: 1);

    private static AdjustGroupQuotaCommand Adjust(
        GroupActor actor,
        string key,
        long expectedVersion = 1,
        long total = 100) => new(
        EntityId.New(),
        actor,
        key,
        GroupId,
        expectedVersion,
        total,
        "authorized quota adjustment",
        "127.0.0.1",
        "sha256:test");

    private static ResetGroupQuotaCommand Reset(
        GroupActor actor,
        string key,
        long expectedVersion = 1,
        long total = 100) => new(
        EntityId.New(),
        actor,
        key,
        GroupId,
        expectedVersion,
        total,
        "authorized quota reset",
        "127.0.0.1",
        "sha256:test");

    private static GroupQuotaResource Resource(
        BigInteger total,
        BigInteger consumed,
        BigInteger reserved,
        GroupPoolQuotaStatus status,
        long version)
    {
        BigInteger remaining = BigInteger.Max(total - consumed - reserved, BigInteger.Zero);
        BigInteger overage = BigInteger.Max(consumed - total, BigInteger.Zero);
        return new GroupQuotaResource(
            GroupId,
            EntityId.New(),
            status,
            total,
            consumed,
            reserved,
            remaining,
            overage,
            Now,
            null,
            version,
            Now);
    }

    private sealed record TestContext(
        QuotaControlPlaneService Service,
        RecordingUnitOfWorkFactory Units,
        RecordingIdempotencyStore Idempotency,
        RecordingAuditAppender Audit,
        List<string> Trace);

    private sealed class RecordingQuotaRepository : IQuotaRepository
    {
        internal GroupQuotaResource? Current { get; set; }

        internal QuotaWriteResult? NextAdjust { get; set; }

        internal Func<ResetQuotaWrite, QuotaWriteResult>? ResetFactory { get; set; }

        internal AdjustQuotaWrite? LastAdjust { get; private set; }

        internal ResetQuotaWrite? LastReset { get; private set; }

        internal int GetCalls { get; private set; }

        internal int AdjustCalls { get; private set; }

        internal int ResetCalls { get; private set; }

        internal bool ThrowOnWrite { get; set; }

        internal List<string> Trace { get; set; } = [];

        public ValueTask<GroupQuotaResource?> GetCurrentAsync(
            EntityId groupId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            return ValueTask.FromResult(Current);
        }

        public ValueTask<QuotaWriteResult> AdjustTotalAsync(
            AdjustQuotaWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trace.Add("repository");
            AdjustCalls++;
            LastAdjust = write;
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException(
                    "The repository must not be called during replay.");
            }

            return ValueTask.FromResult(
                NextAdjust
                ?? new QuotaWriteResult(QuotaWriteDisposition.NotFound));
        }

        public ValueTask<QuotaWriteResult> ResetAsync(
            ResetQuotaWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trace.Add("repository");
            ResetCalls++;
            LastReset = write;
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException(
                    "The repository must not be called during replay.");
            }

            return ValueTask.FromResult(
                ResetFactory?.Invoke(write)
                ?? new QuotaWriteResult(QuotaWriteDisposition.NotFound));
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

    private sealed class RecordingIdempotencyStore(List<string> trace) :
        ICommandIdempotencyStore
    {
        private CommandIdempotencyRequest? _activeRequest;
        private CommandIdempotencyRequest? _completedRequest;

        internal List<CommandIdempotencyRequest> Requests { get; } = [];

        internal List<CommandIdempotencyCompletion> Completions { get; } = [];

        internal bool ReplayCompletedRequests { get; set; }

        internal CommandIdempotencyAcquireResult? NextAcquire { get; set; }

        internal bool CompleteResult { get; set; } = true;

        internal CommandIdempotencyResponse? CompletedResponse { get; private set; }

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("idempotency");
            Requests.Add(request);
            if (NextAcquire is not null)
            {
                CommandIdempotencyAcquireResult result = NextAcquire;
                NextAcquire = null;
                return ValueTask.FromResult(result);
            }

            if (ReplayCompletedRequests && CompletedResponse is not null)
            {
                bool exact = _completedRequest is not null
                    && string.Equals(
                        _completedRequest.Scope,
                        request.Scope,
                        StringComparison.Ordinal)
                    && string.Equals(
                        _completedRequest.Key,
                        request.Key,
                        StringComparison.Ordinal)
                    && string.Equals(
                        _completedRequest.ActorFingerprint,
                        request.ActorFingerprint,
                        StringComparison.Ordinal)
                    && _completedRequest.RequestHash.Span.SequenceEqual(request.RequestHash.Span);
                return ValueTask.FromResult(exact
                    ? CommandIdempotencyAcquireResult.Replay(CompletedResponse)
                    : CommandIdempotencyAcquireResult.Conflict);
            }

            _activeRequest = request;
            return ValueTask.FromResult(CommandIdempotencyAcquireResult.Acquired(
                new CommandIdempotencyLease(
                    request.Scope,
                    request.Key,
                    request.Owner,
                    Generation: 1,
                    Version: 1)));
        }

        public ValueTask<bool> HeartbeatAsync(
            CommandIdempotencyHeartbeat heartbeat,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                "Quota command tests do not heartbeat idempotency leases.");

        public ValueTask<bool> CompleteAsync(
            CommandIdempotencyCompletion completion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("idempotency");
            Completions.Add(completion);
            _completedRequest = _activeRequest
                ?? throw new InvalidOperationException(
                    "The quota completion did not have an active request.");
            CompletedResponse = new CommandIdempotencyResponse(
                completion.TerminalStatus,
                completion.ResponseStatus,
                completion.ResponseBody,
                completion.ResponseBodyEnvelope,
                completion.ResponseHeaders,
                completion.ResourceType,
                completion.ResourceId);
            return ValueTask.FromResult(CompleteResult);
        }
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
}
#pragma warning restore MA0051
