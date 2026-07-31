using Microsoft.Extensions.Time.Testing;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Application;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.UnitTests;

// Governing contracts:
// - docs/runtime/redis-contract.md, "Account Circuit Breaker".
// - ADR 0011, "New unknown versus breaker half-open" and
//   "Persistence, observability, and readiness summary".
public sealed class AccountCircuitBreakerContractTests
{
    private static readonly EntityId AccountId = new(
        Guid.Parse("018f3a4b-5c6d-7e8f-9123-456789abcdef"));
    private static readonly DateTimeOffset Now = new(
        2030,
        1,
        2,
        3,
        4,
        5,
        TimeSpan.Zero);
    private static readonly DateTimeOffset OpenUntil = Now.AddMinutes(3);
    private static readonly DateTimeOffset ProbeExpiry = Now.AddSeconds(10);

    [Fact]
    public async Task ReadUsesIgnoredPassiveObservationWithoutVersionFence()
    {
        CoordinationStub coordination = new();
        HealthWriterStub writer = new();
        AccountCircuitBreaker breaker = Breaker(coordination, writer);

        Result<AccountBreakerSnapshot> result = await breaker.ReadAsync(
            AccountId,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        CoordinationBreakerRecordRequest request =
            Assert.Single(coordination.RecordRequests);
        Assert.Equal(AccountId, request.AccountId);
        Assert.Equal(CoordinationBreakerOutcome.Ignored, request.Outcome);
        Assert.Equal(
            CoordinationBreakerObservationMode.Passive,
            request.ObservationMode);
        Assert.Null(request.RetryAfter);
        Assert.Null(request.RetryAfterAt);
        Assert.Equal(0, request.JitterBasisPoints);
        Assert.Equal(0, request.SourceStatus);
        Assert.Empty(writer.Transitions);
    }

    [Theory]
    [MemberData(nameof(RecordMappings))]
    public async Task RecordMapsEveryPublicOutcomeAndObservationMode(
        AccountBreakerOutcome outcome,
        int status,
        TimeSpan? retryAfter,
        DateTimeOffset? retryAfterAt,
        AccountBreakerObservationMode mode,
        CoordinationBreakerOutcome expectedOutcome,
        CoordinationBreakerObservationMode expectedMode)
    {
        CoordinationStub coordination = new();
        HealthWriterStub writer = new();
        AccountCircuitBreaker breaker = Breaker(coordination, writer);

        Result<AccountBreakerSnapshot> result = await breaker.RecordAsync(
            Command(
                outcome,
                status,
                retryAfter,
                retryAfterAt,
                mode),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        CoordinationBreakerRecordRequest request =
            Assert.Single(coordination.RecordRequests);
        Assert.Equal(expectedOutcome, request.Outcome);
        Assert.Equal(expectedMode, request.ObservationMode);
        Assert.Equal(status, request.SourceStatus);
        Assert.Equal(retryAfter, request.RetryAfter);
        Assert.Equal(retryAfterAt, request.RetryAfterAt);
        if (outcome == AccountBreakerOutcome.TransientFailure)
        {
            Assert.InRange(request.JitterBasisPoints, 0, 1_000);
        }
        else
        {
            Assert.Equal(0, request.JitterBasisPoints);
        }
    }

    [Theory]
    [MemberData(nameof(SnapshotMappings))]
    public async Task RecordedStateAndActionAreMappedToPublicSnapshot(
        CoordinationBreakerState state,
        CoordinationBreakerAction action,
        AccountBreakerState expectedState,
        AccountBreakerAction expectedAction)
    {
        CoordinationStub coordination = new()
        {
            RecordResult = CoordinationBreakerRecordResult.Recorded(
                state,
                action,
                samples: 12,
                failures: 7,
                consecutiveFailures: 4,
                OpenUntil),
        };
        HealthWriterStub writer = new();
        AccountCircuitBreaker breaker = Breaker(coordination, writer);

        Result<AccountBreakerSnapshot> result = await breaker.RecordAsync(
            Command(AccountBreakerOutcome.Success, 200),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedState, result.Value.State);
        Assert.Equal(expectedAction, result.Value.Action);
        Assert.Equal(12, result.Value.Samples);
        Assert.Equal(7, result.Value.Failures);
        Assert.Equal(4, result.Value.ConsecutiveFailures);
        Assert.Equal(OpenUntil, result.Value.OpenUntil);
    }

    [Fact]
    public async Task DefaultOpenDeadlineMapsToNoPublicDeadline()
    {
        CoordinationStub coordination = new()
        {
            RecordResult = CoordinationBreakerRecordResult.Recorded(
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.None,
                samples: 0,
                failures: 0,
                consecutiveFailures: 0,
                openUntil: default),
        };
        AccountCircuitBreaker breaker = Breaker(
            coordination,
            new HealthWriterStub());

        Result<AccountBreakerSnapshot> result = await breaker.ReadAsync(
            AccountId,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.OpenUntil);
    }

    [Theory]
    [MemberData(nameof(HealthActionMappings))]
    public async Task RedisHealthActionUsesVersionFencedPostgresWriter(
        CoordinationBreakerAction action,
        AccountHealth expectedHealth,
        bool expectsRetryAt)
    {
        CoordinationStub coordination = new()
        {
            RecordResult = CoordinationBreakerRecordResult.Recorded(
                CoordinationBreakerState.Open,
                action,
                samples: 10,
                failures: 5,
                consecutiveFailures: 5,
                OpenUntil),
        };
        HealthWriterStub writer = new();
        AccountCircuitBreaker breaker = Breaker(coordination, writer);
        DateTimeOffset observedAt = Now.AddSeconds(-2);

        Result<AccountBreakerSnapshot> result = await breaker.RecordAsync(
            Command(
                AccountBreakerOutcome.TransientFailure,
                status: 503,
                observedAt: observedAt),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        AccountHealthTransition transition = Assert.Single(writer.Transitions);
        Assert.Equal(AccountId, transition.AccountId);
        Assert.Equal(expectedHealth, transition.Health);
        Assert.Equal(observedAt, transition.ObservedAt);
        Assert.Equal(expectsRetryAt ? OpenUntil : null, transition.RetryAt);
        Assert.Equal(7, transition.ExpectedAccountVersion);
        Assert.Equal(11, transition.ExpectedCredentialRevision);
    }

    [Fact]
    public async Task MissingObservedAtUsesInjectedClock()
    {
        CoordinationStub coordination = new()
        {
            RecordResult = CoordinationBreakerRecordResult.Recorded(
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.WriteHealthy,
                samples: 1,
                failures: 0,
                consecutiveFailures: 0,
                openUntil: default),
        };
        HealthWriterStub writer = new();
        AccountCircuitBreaker breaker = Breaker(coordination, writer);

        Result<AccountBreakerSnapshot> result = await breaker.RecordAsync(
            Command(AccountBreakerOutcome.Success, 200, observedAt: null),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, Assert.Single(writer.Transitions).ObservedAt);
    }

    [Theory]
    [InlineData(AccountHealthTransitionDisposition.Applied)]
    [InlineData(AccountHealthTransitionDisposition.Duplicate)]
    public async Task AppliedOrDuplicateHealthWriteCompletesBreakerRecord(
        AccountHealthTransitionDisposition disposition)
    {
        CoordinationStub coordination = CoordinationWithHealthAction();
        HealthWriterStub writer = new()
        {
            Result = HealthResult(disposition),
        };
        AccountCircuitBreaker breaker = Breaker(coordination, writer);

        Result<AccountBreakerSnapshot> result = await breaker.RecordAsync(
            Command(AccountBreakerOutcome.Success, 200),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [MemberData(nameof(StricterHealthResults))]
    public async Task PostgresHealthResultWinsWhenStricterThanRedis(
        AccountHealthTransitionDisposition disposition,
        string expectedCode)
    {
        CoordinationStub coordination = CoordinationWithHealthAction();
        HealthWriterStub writer = new()
        {
            Result = HealthResult(disposition),
        };
        AccountCircuitBreaker breaker = Breaker(coordination, writer);

        Result<AccountBreakerSnapshot> result = await breaker.RecordAsync(
            Command(AccountBreakerOutcome.Success, 200),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
    }

    [Fact]
    public async Task PostgresWriterFailureIsPreserved()
    {
        CoordinationStub coordination = CoordinationWithHealthAction();
        HealthWriterStub writer = new()
        {
            Result = Result.Failure<AccountHealthTransitionResult>(
                "dependency_unavailable",
                "PostgreSQL unavailable.",
                retryAfterSeconds: 4),
        };
        AccountCircuitBreaker breaker = Breaker(coordination, writer);

        Result<AccountBreakerSnapshot> result = await breaker.RecordAsync(
            Command(AccountBreakerOutcome.Success, 200),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Equal(4, result.Error.RetryAfterSeconds);
    }

    [Fact]
    public async Task RedisRecordFailureFailsClosedWithoutPostgresWrite()
    {
        CoordinationStub coordination = new()
        {
            RecordResult = CoordinationBreakerRecordResult.Unavailable,
        };
        HealthWriterStub writer = new();
        AccountCircuitBreaker breaker = Breaker(coordination, writer);

        Result<AccountBreakerSnapshot> result = await breaker.RecordAsync(
            Command(AccountBreakerOutcome.Success, 200),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Empty(writer.Transitions);
    }

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public async Task InvalidRecordIsRejectedBeforeRedis(
        AccountBreakerRecordCommand command)
    {
        CoordinationStub coordination = new();
        AccountCircuitBreaker breaker = Breaker(
            coordination,
            new HealthWriterStub());

        Result<AccountBreakerSnapshot> result = await breaker.RecordAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_request", result.Error.Code);
        Assert.Empty(coordination.RecordRequests);
    }

    [Theory]
    [MemberData(nameof(ValidEdgeCommands))]
    public async Task ValidRecordEdgeIsForwardedToRedis(
        AccountBreakerRecordCommand command)
    {
        CoordinationStub coordination = new();
        AccountCircuitBreaker breaker = Breaker(
            coordination,
            new HealthWriterStub());

        Result<AccountBreakerSnapshot> result = await breaker.RecordAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(coordination.RecordRequests);
    }

    [Fact]
    public async Task ProbeAcquireUnavailableFailsClosed()
    {
        CoordinationStub coordination = new()
        {
            AcquireResult = CoordinationProbeAcquireResult.Unavailable,
        };
        AccountCircuitBreaker breaker = Breaker(
            coordination,
            new HealthWriterStub());

        Result<AccountBreakerProbeAcquireResult> result =
            await breaker.TryAcquireProbeAsync(
                AccountId,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
    }

    [Fact]
    public async Task ProbeAcquireRejectionPreservesRedisRetryDelay()
    {
        CoordinationStub coordination = new()
        {
            AcquireResult = CoordinationProbeAcquireResult.Rejected(
                TimeSpan.FromMilliseconds(750)),
        };
        AccountCircuitBreaker breaker = Breaker(
            coordination,
            new HealthWriterStub());

        Result<AccountBreakerProbeAcquireResult> result =
            await breaker.TryAcquireProbeAsync(
                AccountId,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            AccountBreakerProbeAcquireDisposition.NotEligible,
            result.Value.Disposition);
        Assert.Null(result.Value.Probe);
        Assert.Equal(TimeSpan.FromMilliseconds(750), result.Value.RetryAfter);
    }

    [Fact]
    public async Task ProbeAcquireCreatesOpaqueOwnerAndExposesFixedExpiry()
    {
        CoordinationStub coordination = new()
        {
            AcquireResult = CoordinationProbeAcquireResult.Acquired(ProbeExpiry),
        };
        AccountCircuitBreaker breaker = Breaker(
            coordination,
            new HealthWriterStub());

        Result<AccountBreakerProbeAcquireResult> result =
            await breaker.TryAcquireProbeAsync(
                AccountId,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            AccountBreakerProbeAcquireDisposition.Acquired,
            result.Value.Disposition);
        IAccountBreakerProbe probe = Assert.IsAssignableFrom<IAccountBreakerProbe>(
            result.Value.Probe);
        Assert.Equal(AccountId, probe.AccountId);
        Assert.Equal(ProbeExpiry, probe.ExpiresAt);
        CoordinationProbeAcquireRequest request =
            Assert.Single(coordination.AcquireRequests);
        Assert.Equal(AccountId, request.AccountId);
        Assert.Matches("^[0-9a-f]{32}$", request.Owner);
        await probe.DisposeAsync();
    }

    [Fact]
    public async Task UnknownAcquireDispositionFailsClosed()
    {
        CoordinationStub coordination = new()
        {
            AcquireResult = new CoordinationProbeAcquireResult(
                (CoordinationProbeAcquireDisposition)999,
                default,
                TimeSpan.Zero),
        };
        AccountCircuitBreaker breaker = Breaker(
            coordination,
            new HealthWriterStub());

        Result<AccountBreakerProbeAcquireResult> result =
            await breaker.TryAcquireProbeAsync(
                AccountId,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
    }

    [Theory]
    [MemberData(nameof(ProbeCompletionMappings))]
    public async Task ProbeCompletionMapsOutcomeStateActionAndHealth(
        AccountBreakerOutcome outcome,
        int status,
        TimeSpan? retryAfter,
        DateTimeOffset? retryAfterAt,
        CoordinationBreakerOutcome expectedOutcome,
        CoordinationBreakerState redisState,
        CoordinationBreakerAction redisAction,
        AccountBreakerState expectedState,
        AccountBreakerAction expectedAction,
        AccountHealth expectedHealth)
    {
        CoordinationStub coordination = new()
        {
            AcquireResult = CoordinationProbeAcquireResult.Acquired(ProbeExpiry),
            CompleteResult = CoordinationProbeCompleteResult.Completed(
                redisState,
                redisAction,
                halfOpenSuccesses: 1,
                OpenUntil),
        };
        HealthWriterStub writer = new();
        AccountCircuitBreaker breaker = Breaker(coordination, writer);
        IAccountBreakerProbe probe = await AcquireProbeAsync(breaker);

        Result<AccountBreakerSnapshot> result = await probe.CompleteAsync(
            Completion(outcome, status, retryAfter, retryAfterAt),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedState, result.Value.State);
        Assert.Equal(expectedAction, result.Value.Action);
        Assert.Equal(
            redisAction == CoordinationBreakerAction.WriteCooling
                ? OpenUntil
                : null,
            Assert.Single(writer.Transitions).RetryAt);
        Assert.Equal(expectedHealth, writer.Transitions[0].Health);
        CoordinationProbeCompleteRequest request =
            Assert.Single(coordination.CompleteRequests);
        Assert.Equal(expectedOutcome, request.Outcome);
        Assert.Equal(status, request.SourceStatus);
        Assert.Equal(retryAfter, request.RetryAfter);
        Assert.Equal(retryAfterAt, request.RetryAfterAt);
        Assert.Equal(
            coordination.AcquireRequests[0].Owner,
            request.Owner);
        await probe.DisposeAsync();
    }

    [Fact]
    public async Task IgnoredProbeCompletionIsRejectedBeforeRedis()
    {
        CoordinationStub coordination = AcquiringCoordination();
        AccountCircuitBreaker breaker = Breaker(
            coordination,
            new HealthWriterStub());
        IAccountBreakerProbe probe = await AcquireProbeAsync(breaker);

        Result<AccountBreakerSnapshot> result = await probe.CompleteAsync(
            new AccountBreakerProbeCompletion(AccountBreakerOutcome.Ignored),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_request", result.Error.Code);
        Assert.Empty(coordination.CompleteRequests);
        await probe.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(InvalidCompletions))]
    public async Task InvalidProbeCompletionIsRejectedBeforeRedis(
        AccountBreakerProbeCompletion completion)
    {
        CoordinationStub coordination = AcquiringCoordination();
        AccountCircuitBreaker breaker = Breaker(
            coordination,
            new HealthWriterStub());
        IAccountBreakerProbe probe = await AcquireProbeAsync(breaker);

        Result<AccountBreakerSnapshot> result = await probe.CompleteAsync(
            completion,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_request", result.Error.Code);
        Assert.Empty(coordination.CompleteRequests);
        await probe.DisposeAsync();
    }

    [Fact]
    public async Task NonOwnerCompletionIsTerminalAndCannotBeRetried()
    {
        CoordinationStub coordination = AcquiringCoordination();
        coordination.CompleteResult = CoordinationProbeCompleteResult.NotOwner;
        AccountCircuitBreaker breaker = Breaker(
            coordination,
            new HealthWriterStub());
        IAccountBreakerProbe probe = await AcquireProbeAsync(breaker);
        AccountBreakerProbeCompletion completion =
            Completion(AccountBreakerOutcome.Success, 200);

        Result<AccountBreakerSnapshot> first = await probe.CompleteAsync(
            completion,
            TestContext.Current.CancellationToken);
        Result<AccountBreakerSnapshot> second = await probe.CompleteAsync(
            completion,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsFailure);
        Assert.Equal("account_probe_not_owned", first.Error.Code);
        Assert.Equal(1, first.Error.RetryAfterSeconds);
        Assert.True(second.IsFailure);
        Assert.Equal("account_probe_not_owned", second.Error.Code);
        Assert.Single(coordination.CompleteRequests);
        await probe.DisposeAsync();
    }

    [Fact]
    public async Task UnavailableCompletionCanBeRetriedWithSameOwner()
    {
        CoordinationStub coordination = AcquiringCoordination();
        coordination.CompleteResults.Enqueue(
            CoordinationProbeCompleteResult.Unavailable);
        coordination.CompleteResults.Enqueue(
            CoordinationProbeCompleteResult.Completed(
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.None,
                halfOpenSuccesses: 0,
                openUntil: default));
        AccountCircuitBreaker breaker = Breaker(
            coordination,
            new HealthWriterStub());
        IAccountBreakerProbe probe = await AcquireProbeAsync(breaker);
        AccountBreakerProbeCompletion completion =
            Completion(AccountBreakerOutcome.Success, 200);

        Result<AccountBreakerSnapshot> first = await probe.CompleteAsync(
            completion,
            TestContext.Current.CancellationToken);
        Result<AccountBreakerSnapshot> second = await probe.CompleteAsync(
            completion,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsFailure);
        Assert.Equal("coordination_unavailable", first.Error.Code);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, coordination.CompleteRequests.Count);
        Assert.Equal(
            coordination.CompleteRequests[0].Owner,
            coordination.CompleteRequests[1].Owner);
        await probe.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulCompletionCanOnlyBeSubmittedOnce()
    {
        CoordinationStub coordination = AcquiringCoordination();
        coordination.CompleteResult = CoordinationProbeCompleteResult.Completed(
            CoordinationBreakerState.Closed,
            CoordinationBreakerAction.None,
            halfOpenSuccesses: 0,
            openUntil: default);
        AccountCircuitBreaker breaker = Breaker(
            coordination,
            new HealthWriterStub());
        IAccountBreakerProbe probe = await AcquireProbeAsync(breaker);
        AccountBreakerProbeCompletion completion =
            Completion(AccountBreakerOutcome.Success, 200);

        Result<AccountBreakerSnapshot> first = await probe.CompleteAsync(
            completion,
            TestContext.Current.CancellationToken);
        Result<AccountBreakerSnapshot> second = await probe.CompleteAsync(
            completion,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsFailure);
        Assert.Equal("account_probe_not_owned", second.Error.Code);
        Assert.Single(coordination.CompleteRequests);
        await probe.DisposeAsync();
    }

    [Fact]
    public async Task ProbeHealthWriteFailureIsPreserved()
    {
        CoordinationStub coordination = AcquiringCoordination();
        coordination.CompleteResult = CoordinationProbeCompleteResult.Completed(
            CoordinationBreakerState.Open,
            CoordinationBreakerAction.WriteUnhealthy,
            halfOpenSuccesses: 0,
            openUntil: default);
        HealthWriterStub writer = new()
        {
            Result = Result.Failure<AccountHealthTransitionResult>(
                "dependency_unavailable",
                "PostgreSQL unavailable.",
                retryAfterSeconds: 2),
        };
        AccountCircuitBreaker breaker = Breaker(coordination, writer);
        IAccountBreakerProbe probe = await AcquireProbeAsync(breaker);

        Result<AccountBreakerSnapshot> result = await probe.CompleteAsync(
            Completion(AccountBreakerOutcome.AuthenticationFailure, 401),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Equal(2, result.Error.RetryAfterSeconds);
        await probe.DisposeAsync();
    }

    [Fact]
    public async Task ConstructorsRejectMissingDependencies()
    {
        CoordinationStub coordination = new();
        HealthWriterStub writer = new();
        FakeTimeProvider time = new(Now);

        Assert.Throws<ArgumentNullException>(
            "coordination",
            () => new AccountCircuitBreaker(null!, writer, time));
        Assert.Throws<ArgumentNullException>(
            "healthWriter",
            () => new AccountCircuitBreaker(coordination, null!, time));
        Assert.Throws<ArgumentNullException>(
            "timeProvider",
            () => new AccountCircuitBreaker(coordination, writer, null!));
    }

    public static TheoryData<
        AccountBreakerOutcome,
        int,
        TimeSpan?,
        DateTimeOffset?,
        AccountBreakerObservationMode,
        CoordinationBreakerOutcome,
        CoordinationBreakerObservationMode> RecordMappings() =>
        new()
        {
            {
                AccountBreakerOutcome.Success,
                200,
                null,
                null,
                AccountBreakerObservationMode.Passive,
                CoordinationBreakerOutcome.Success,
                CoordinationBreakerObservationMode.Passive
            },
            {
                AccountBreakerOutcome.TransientFailure,
                503,
                null,
                null,
                AccountBreakerObservationMode.ControlledActive,
                CoordinationBreakerOutcome.TransientFailure,
                CoordinationBreakerObservationMode.ControlledActive
            },
            {
                AccountBreakerOutcome.RateLimited,
                429,
                TimeSpan.FromSeconds(30),
                null,
                AccountBreakerObservationMode.Passive,
                CoordinationBreakerOutcome.RateLimited,
                CoordinationBreakerObservationMode.Passive
            },
            {
                AccountBreakerOutcome.RateLimited,
                429,
                null,
                Now.AddMinutes(1),
                AccountBreakerObservationMode.Passive,
                CoordinationBreakerOutcome.RateLimited,
                CoordinationBreakerObservationMode.Passive
            },
            {
                AccountBreakerOutcome.AuthenticationFailure,
                403,
                null,
                null,
                AccountBreakerObservationMode.Passive,
                CoordinationBreakerOutcome.AuthFailure,
                CoordinationBreakerObservationMode.Passive
            },
            {
                AccountBreakerOutcome.Ignored,
                422,
                null,
                null,
                AccountBreakerObservationMode.Passive,
                CoordinationBreakerOutcome.Ignored,
                CoordinationBreakerObservationMode.Passive
            },
        };

    public static TheoryData<
        CoordinationBreakerState,
        CoordinationBreakerAction,
        AccountBreakerState,
        AccountBreakerAction> SnapshotMappings() =>
        new()
        {
            {
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.None,
                AccountBreakerState.Closed,
                AccountBreakerAction.None
            },
            {
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.None,
                AccountBreakerState.Open,
                AccountBreakerAction.None
            },
            {
                CoordinationBreakerState.HalfOpen,
                CoordinationBreakerAction.None,
                AccountBreakerState.HalfOpen,
                AccountBreakerAction.None
            },
            {
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.WriteHealthy,
                AccountBreakerState.Closed,
                AccountBreakerAction.MarkHealthy
            },
            {
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.WriteDegraded,
                AccountBreakerState.Closed,
                AccountBreakerAction.MarkDegraded
            },
            {
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteCooling,
                AccountBreakerState.Open,
                AccountBreakerAction.MarkCooling
            },
            {
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteUnhealthy,
                AccountBreakerState.Open,
                AccountBreakerAction.MarkUnhealthy
            },
            {
                CoordinationBreakerState.HalfOpen,
                CoordinationBreakerAction.WriteUnknown,
                AccountBreakerState.HalfOpen,
                AccountBreakerAction.MarkUnknown
            },
        };

    public static TheoryData<
        CoordinationBreakerAction,
        AccountHealth,
        bool> HealthActionMappings() =>
        new()
        {
            {
                CoordinationBreakerAction.WriteHealthy,
                AccountHealth.Healthy,
                false
            },
            {
                CoordinationBreakerAction.WriteDegraded,
                AccountHealth.Degraded,
                false
            },
            {
                CoordinationBreakerAction.WriteCooling,
                AccountHealth.Cooling,
                true
            },
            {
                CoordinationBreakerAction.WriteUnhealthy,
                AccountHealth.Unhealthy,
                false
            },
            {
                CoordinationBreakerAction.WriteUnknown,
                AccountHealth.Unknown,
                false
            },
        };

    public static TheoryData<
        AccountHealthTransitionDisposition,
        string> StricterHealthResults() =>
        new()
        {
            {
                AccountHealthTransitionDisposition.StaleObservation,
                "resource_conflict"
            },
            {
                AccountHealthTransitionDisposition.AccountRetired,
                "not_found"
            },
            {
                (AccountHealthTransitionDisposition)999,
                "dependency_unavailable"
            },
        };

    public static TheoryData<AccountBreakerRecordCommand> InvalidCommands() =>
        new()
        {
            Command(AccountBreakerOutcome.Success, -1),
            Command(AccountBreakerOutcome.Success, 600),
            Command(
                AccountBreakerOutcome.RateLimited,
                429,
                TimeSpan.FromMilliseconds(999)),
            Command(
                AccountBreakerOutcome.RateLimited,
                429,
                TimeSpan.FromHours(24).Add(TimeSpan.FromMilliseconds(1))),
            Command(
                AccountBreakerOutcome.RateLimited,
                429,
                TimeSpan.FromSeconds(30),
                Now.AddMinutes(1)),
            Command(AccountBreakerOutcome.Success, 204, retryAfterAt: Now),
            Command(AccountBreakerOutcome.Success, 199),
            Command(AccountBreakerOutcome.TransientFailure, 400),
            Command(AccountBreakerOutcome.RateLimited, 503),
            Command(AccountBreakerOutcome.AuthenticationFailure, 429),
            Command(
                AccountBreakerOutcome.AuthenticationFailure,
                401,
                TimeSpan.FromSeconds(1)),
            Command(AccountBreakerOutcome.Ignored, 401),
            Command(AccountBreakerOutcome.Ignored, 403),
            Command(AccountBreakerOutcome.Ignored, 408),
            Command(AccountBreakerOutcome.Ignored, 429),
            Command(AccountBreakerOutcome.Ignored, 200),
            Command(AccountBreakerOutcome.Success, 200) with
            {
                ExpectedAccountVersion = 0,
            },
            Command(AccountBreakerOutcome.Success, 200) with
            {
                ExpectedCredentialRevision = 0,
            },
            Command((AccountBreakerOutcome)999, 0),
        };

    public static TheoryData<AccountBreakerRecordCommand> ValidEdgeCommands() =>
        new()
        {
            Command(AccountBreakerOutcome.Success, 299),
            Command(AccountBreakerOutcome.TransientFailure, 0),
            Command(AccountBreakerOutcome.TransientFailure, 200),
            Command(AccountBreakerOutcome.TransientFailure, 399),
            Command(AccountBreakerOutcome.TransientFailure, 408),
            Command(AccountBreakerOutcome.TransientFailure, 500),
            Command(AccountBreakerOutcome.TransientFailure, 599),
            Command(
                AccountBreakerOutcome.RateLimited,
                429,
                TimeSpan.FromSeconds(1)),
            Command(
                AccountBreakerOutcome.RateLimited,
                429,
                TimeSpan.FromHours(24)),
            Command(AccountBreakerOutcome.AuthenticationFailure, 401),
            Command(AccountBreakerOutcome.AuthenticationFailure, 403),
            Command(AccountBreakerOutcome.Ignored, 0) with
            {
                ExpectedAccountVersion = 0,
                ExpectedCredentialRevision = 0,
            },
            Command(AccountBreakerOutcome.Ignored, 400),
            Command(AccountBreakerOutcome.Ignored, 499),
        };

#pragma warning disable MA0051 // The complete mapping matrix is clearer as one table.
    public static TheoryData<
        AccountBreakerOutcome,
        int,
        TimeSpan?,
        DateTimeOffset?,
        CoordinationBreakerOutcome,
        CoordinationBreakerState,
        CoordinationBreakerAction,
        AccountBreakerState,
        AccountBreakerAction,
        AccountHealth> ProbeCompletionMappings() =>
        new()
        {
            {
                AccountBreakerOutcome.Success,
                200,
                null,
                null,
                CoordinationBreakerOutcome.Success,
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.WriteHealthy,
                AccountBreakerState.Closed,
                AccountBreakerAction.MarkHealthy,
                AccountHealth.Healthy
            },
            {
                AccountBreakerOutcome.TransientFailure,
                503,
                null,
                null,
                CoordinationBreakerOutcome.TransientFailure,
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteCooling,
                AccountBreakerState.Open,
                AccountBreakerAction.MarkCooling,
                AccountHealth.Cooling
            },
            {
                AccountBreakerOutcome.RateLimited,
                429,
                TimeSpan.FromSeconds(15),
                null,
                CoordinationBreakerOutcome.RateLimited,
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteCooling,
                AccountBreakerState.Open,
                AccountBreakerAction.MarkCooling,
                AccountHealth.Cooling
            },
            {
                AccountBreakerOutcome.RateLimited,
                429,
                null,
                Now.AddMinutes(2),
                CoordinationBreakerOutcome.RateLimited,
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteCooling,
                AccountBreakerState.Open,
                AccountBreakerAction.MarkCooling,
                AccountHealth.Cooling
            },
            {
                AccountBreakerOutcome.AuthenticationFailure,
                401,
                null,
                null,
                CoordinationBreakerOutcome.AuthFailure,
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteUnhealthy,
                AccountBreakerState.Open,
                AccountBreakerAction.MarkUnhealthy,
                AccountHealth.Unhealthy
            },
        };
#pragma warning restore MA0051

    public static TheoryData<AccountBreakerProbeCompletion>
        InvalidCompletions() =>
        new()
        {
            Completion(AccountBreakerOutcome.Success, 200) with
            {
                ExpectedAccountVersion = 0,
            },
            Completion(AccountBreakerOutcome.Success, 200) with
            {
                ExpectedCredentialRevision = 0,
            },
            Completion(AccountBreakerOutcome.Success, 500),
            Completion(
                AccountBreakerOutcome.RateLimited,
                429,
                TimeSpan.FromMilliseconds(1)),
            Completion(AccountBreakerOutcome.AuthenticationFailure, 200),
        };

    private static AccountCircuitBreaker Breaker(
        CoordinationStub coordination,
        HealthWriterStub healthWriter) =>
        new(coordination, healthWriter, new FakeTimeProvider(Now));

    private static AccountBreakerRecordCommand Command(
        AccountBreakerOutcome outcome,
        int status,
        TimeSpan? retryAfter = null,
        DateTimeOffset? retryAfterAt = null,
        AccountBreakerObservationMode mode =
            AccountBreakerObservationMode.Passive,
        DateTimeOffset? observedAt = null) =>
        new(
            AccountId,
            outcome,
            retryAfter,
            status,
            mode,
            observedAt ?? Now,
            ExpectedAccountVersion: 7,
            ExpectedCredentialRevision: 11,
            retryAfterAt);

    private static AccountBreakerProbeCompletion Completion(
        AccountBreakerOutcome outcome,
        int status,
        TimeSpan? retryAfter = null,
        DateTimeOffset? retryAfterAt = null) =>
        new(
            outcome,
            retryAfter,
            status,
            ObservedAt: Now,
            ExpectedAccountVersion: 7,
            ExpectedCredentialRevision: 11,
            retryAfterAt);

    private static CoordinationStub CoordinationWithHealthAction() =>
        new()
        {
            RecordResult = CoordinationBreakerRecordResult.Recorded(
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.WriteHealthy,
                samples: 1,
                failures: 0,
                consecutiveFailures: 0,
                openUntil: default),
        };

    private static CoordinationStub AcquiringCoordination() =>
        new()
        {
            AcquireResult = CoordinationProbeAcquireResult.Acquired(ProbeExpiry),
        };

    private static async Task<IAccountBreakerProbe> AcquireProbeAsync(
        AccountCircuitBreaker breaker)
    {
        Result<AccountBreakerProbeAcquireResult> acquired =
            await breaker.TryAcquireProbeAsync(
                AccountId,
                TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.True(acquired.IsSuccess);
        return Assert.IsAssignableFrom<IAccountBreakerProbe>(
            acquired.Value.Probe);
    }

    private static Result<AccountHealthTransitionResult> HealthResult(
        AccountHealthTransitionDisposition disposition)
    {
        AccountHealthState state = new(
            AccountHealth.Healthy,
            RetryAt: null,
            ObservedAt: Now,
            Version: 2);
        return Result.Success(new AccountHealthTransitionResult(
            disposition,
            WasChanged: disposition
                == AccountHealthTransitionDisposition.Applied,
            state,
            state));
    }

    private sealed class CoordinationStub : ICoordinationCircuitBreaker
    {
        internal CoordinationBreakerRecordResult RecordResult { get; set; } =
            CoordinationBreakerRecordResult.Recorded(
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.None,
                samples: 0,
                failures: 0,
                consecutiveFailures: 0,
                openUntil: default);

        internal CoordinationProbeAcquireResult AcquireResult { get; set; } =
            CoordinationProbeAcquireResult.Unavailable;

        internal CoordinationProbeCompleteResult CompleteResult { get; set; } =
            CoordinationProbeCompleteResult.Unavailable;

        internal Queue<CoordinationProbeCompleteResult> CompleteResults
        {
            get;
        } = [];

        internal List<CoordinationBreakerRecordRequest> RecordRequests
        {
            get;
        } = [];

        internal List<CoordinationProbeAcquireRequest> AcquireRequests
        {
            get;
        } = [];

        internal List<CoordinationProbeCompleteRequest> CompleteRequests
        {
            get;
        } = [];

        public ValueTask<CoordinationBreakerRecordResult> RecordAsync(
            CoordinationBreakerRecordRequest request,
            CancellationToken cancellationToken)
        {
            RecordRequests.Add(request);
            return ValueTask.FromResult(RecordResult);
        }

        public ValueTask<CoordinationProbeAcquireResult> AcquireProbeAsync(
            CoordinationProbeAcquireRequest request,
            CancellationToken cancellationToken)
        {
            AcquireRequests.Add(request);
            return ValueTask.FromResult(AcquireResult);
        }

        public ValueTask<CoordinationProbeCompleteResult> CompleteProbeAsync(
            CoordinationProbeCompleteRequest request,
            CancellationToken cancellationToken)
        {
            CompleteRequests.Add(request);
            return ValueTask.FromResult(
                CompleteResults.Count > 0
                    ? CompleteResults.Dequeue()
                    : CompleteResult);
        }
    }

    private sealed class HealthWriterStub : IAccountHealthWriter
    {
        internal Result<AccountHealthTransitionResult> Result { get; set; } =
            HealthResult(AccountHealthTransitionDisposition.Applied);

        internal List<AccountHealthTransition> Transitions { get; } = [];

        public ValueTask<Result<AccountHealthTransitionResult>> RecordAsync(
            AccountHealthTransition transition,
            CancellationToken cancellationToken)
        {
            Transitions.Add(transition);
            return ValueTask.FromResult(Result);
        }
    }
}
