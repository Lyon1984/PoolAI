using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Application;
using PoolAI.Modules.Routing.Infrastructure;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.UnitTests;

// Governing contracts:
// - docs/runtime/redis-contract.md, "Account Lease Lua v1" and sticky routing.
// - docs/开发执行规格-v1.0.md, DEC-016/026/035 and M2-E3.
public sealed class AccountRouterTests
{
    private const string Model = "gpt-4.1-mini";
    private const string SessionHash = "0123456789abcdef0123456789abcdef";
    private static readonly EntityId GroupId = Id("018f3a4b-5c6d-7e8f-8123-456789abcdef");
    private static readonly EntityId OtherGroupId = Id("018f3a4b-5c6d-7e8f-8234-56789abcdef0");
    private static readonly EntityId ChannelAId = Id("018f3a4b-5c6d-7e8f-8345-6789abcdef01");
    private static readonly EntityId ChannelBId = Id("018f3a4b-5c6d-7e8f-8456-789abcdef012");
    private static readonly EntityId AccountAId = Id("018f3a4b-5c6d-7e8f-8567-89abcdef0123");
    private static readonly EntityId AccountBId = Id("018f3a4b-5c6d-7e8f-8678-9abcdef01234");
    private static readonly EntityId RequestId = Id("018f3a4b-5c6d-7e8f-8789-abcdef012345");
    private static readonly EntityId AttemptId = Id("018f3a4b-5c6d-7e8f-889a-bcdef0123456");
    private static readonly DateTimeOffset FirstExpiry =
        new(2030, 1, 1, 0, 1, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RenewedExpiry =
        new(2030, 1, 1, 0, 1, 20, TimeSpan.Zero);

    [Fact]
    public async Task CrossGroupCandidateFailsIntegrityCheckBeforeLeaseCoordination()
    {
        StubCandidateReader reader = new(SuccessCandidates(
            Candidate(AccountAId, groupId: OtherGroupId)));
        QueueLeaseSet leaseSet = new();
        RecordingAffinityStore affinities = new();
        AccountRouter router = Router(reader, leaseSet, affinities);

        Result<IAccountLease> result = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Empty(leaseSet.AcquireRequests);
        Assert.Empty(affinities.GetRequests);
    }

    [Fact]
    public async Task InvalidRouteFactsFailIntegrityCheckBeforeLeaseCoordination()
    {
        AccountCandidate valid = Candidate(AccountAId);
        AccountCandidate[] invalidCandidates =
        [
            valid with { Provider = (UpstreamProvider)int.MaxValue },
            valid with { UpstreamModel = " upstream-model" },
            valid with { UpstreamBaseUrl = "https://upstream.invalid/v1?secret=no" },
            valid with { UpstreamBaseUrl = "http://upstream.invalid/v1" },
            valid with { Capabilities = null! },
            valid with { CredentialRevision = 0 },
        ];

        foreach (AccountCandidate invalid in invalidCandidates)
        {
            QueueLeaseSet leaseSet = new();
            AccountRouter router = Router(
                new StubCandidateReader(SuccessCandidates(invalid)),
                leaseSet,
                new RecordingAffinityStore());

            Result<IAccountLease> result = await router.RouteAsync(
                Command(),
                TestContext.Current.CancellationToken);

            Assert.True(result.IsFailure);
            Assert.Equal("dependency_unavailable", result.Error.Code);
            Assert.Empty(leaseSet.AcquireRequests);
        }
    }

    [Fact]
    public async Task EmptyCandidateSetReturnsNoAvailableAccountBeforeCoordination()
    {
        StubCandidateReader reader = new(SuccessCandidates());
        QueueLeaseSet leaseSet = new();
        AccountRouter router = Router(reader, leaseSet, new RecordingAffinityStore());

        Result<IAccountLease> result = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("no_available_account", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Empty(leaseSet.AcquireRequests);
    }

    [Fact]
    public async Task CanonicalCandidateReadFailureIsPreservedWithoutCoordination()
    {
        StubCandidateReader reader = new(
            Result.Failure<IReadOnlyList<AccountCandidate>>(
                "dependency_unavailable",
                "PostgreSQL is unavailable.",
                retryAfterSeconds: 3));
        QueueLeaseSet leaseSet = new();
        AccountRouter router = Router(reader, leaseSet, new RecordingAffinityStore());

        Result<IAccountLease> result = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Equal(3, result.Error.RetryAfterSeconds);
        Assert.Empty(leaseSet.AcquireRequests);
    }

    [Theory]
    [MemberData(nameof(InvalidModels))]
    public async Task InvalidModelIsRejectedBeforeCandidateRead(string? model)
    {
        StubCandidateReader reader = new(
            SuccessCandidates(Candidate(AccountAId)));
        QueueLeaseSet leaseSet = new();
        AccountRouter router = Router(reader, leaseSet, new RecordingAffinityStore());
        RouteAccountCommand command = Command() with { Model = model! };

        Result<IAccountLease> result = await router.RouteAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_request", result.Error.Code);
        Assert.Empty(reader.Requests);
        Assert.Empty(leaseSet.AcquireRequests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveGroupPolicyVersionIsRejectedBeforeCandidateRead(
        long groupPolicyVersion)
    {
        StubCandidateReader reader = new(
            SuccessCandidates(Candidate(AccountAId)));
        QueueLeaseSet leaseSet = new();
        AccountRouter router = Router(reader, leaseSet, new RecordingAffinityStore());
        RouteAccountCommand command = Command() with
        {
            GroupPolicyVersion = groupPolicyVersion,
        };

        Result<IAccountLease> result = await router.RouteAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_request", result.Error.Code);
        Assert.Empty(reader.Requests);
        Assert.Empty(leaseSet.AcquireRequests);
    }

    [Theory]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("g123456789abcdef0123456789abcdef")]
    public async Task InvalidAffinityHashIsRejectedBeforeCandidateRead(
        string affinityHash)
    {
        StubCandidateReader reader = new(
            SuccessCandidates(Candidate(AccountAId)));
        QueueLeaseSet leaseSet = new();
        AccountRouter router = Router(reader, leaseSet, new RecordingAffinityStore());

        Result<IAccountLease> result = await router.RouteAsync(
            Command(sessionHash: affinityHash),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_request", result.Error.Code);
        Assert.Empty(reader.Requests);
        Assert.Empty(leaseSet.AcquireRequests);
    }

    [Fact]
    public async Task RedisUnavailableFailsClosedWithoutTryingAnotherCandidate()
    {
        AccountCandidate first = Candidate(AccountAId, priority: 20);
        AccountCandidate second = Candidate(
            AccountBId,
            channelId: ChannelBId,
            priority: 10);
        StubCandidateReader reader = new(SuccessCandidates(first, second));
        QueueLeaseSet leaseSet = new();
        leaseSet.AcquireResults.Enqueue(CoordinationLeaseAcquireResult.Unavailable);
        AccountRouter router = Router(reader, leaseSet, new RecordingAffinityStore());

        Result<IAccountLease> result = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        CoordinationLeaseAcquireRequest request =
            Assert.Single(leaseSet.AcquireRequests);
        Assert.Equal(AccountRouter.LeaseKey(AccountAId), request.KeyBase);
    }

    [Fact]
    public async Task OpenSharedBreakerIsExcludedBeforeAccountLeaseAcquisition()
    {
        StubCandidateReader reader = new(SuccessCandidates(
            Candidate(AccountAId, priority: 20),
            Candidate(AccountBId, channelId: ChannelBId, priority: 10)));
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        StubCircuitBreaker breakers = new();
        breakers.ReadResults.Enqueue(Result.Success(new AccountBreakerSnapshot(
            AccountBreakerState.Open,
            Samples: 10,
            Failures: 5,
            ConsecutiveFailures: 5,
            OpenUntil: FirstExpiry,
            AccountBreakerAction.MarkCooling)));
        breakers.ReadResults.Enqueue(StubCircuitBreaker.ClosedResult);
        AccountRouter router = Router(
            reader,
            leaseSet,
            new RecordingAffinityStore(),
            breakers);

        Result<IAccountLease> result = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(AccountBId, result.Value.Route.AccountId);
        CoordinationLeaseAcquireRequest request =
            Assert.Single(leaseSet.AcquireRequests);
        Assert.Equal(AccountRouter.LeaseKey(AccountBId), request.KeyBase);
        Assert.Equal([AccountAId, AccountBId], breakers.ReadAccountIds);
    }

    [Fact]
    public async Task SharedBreakerReadFailureFailsClosedBeforeAccountLease()
    {
        StubCandidateReader reader = new(
            SuccessCandidates(Candidate(AccountAId)));
        QueueLeaseSet leaseSet = new();
        StubCircuitBreaker breakers = new();
        breakers.ReadResults.Enqueue(
            Result.Failure<AccountBreakerSnapshot>(
                "coordination_unavailable",
                "Redis coordination is unavailable.",
                retryAfterSeconds: 1));
        AccountRouter router = Router(
            reader,
            leaseSet,
            new RecordingAffinityStore(),
            breakers);

        Result<IAccountLease> result = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
        Assert.Empty(leaseSet.AcquireRequests);
    }

    [Fact]
    public async Task HalfOpenSharedBreakerCannotBeUsedAsARegularRoute()
    {
        StubCandidateReader reader = new(
            SuccessCandidates(Candidate(AccountAId)));
        QueueLeaseSet leaseSet = new();
        StubCircuitBreaker breakers = new();
        breakers.ReadResults.Enqueue(Result.Success(new AccountBreakerSnapshot(
            AccountBreakerState.HalfOpen,
            Samples: 10,
            Failures: 5,
            ConsecutiveFailures: 5,
            OpenUntil: FirstExpiry,
            AccountBreakerAction.None)));
        AccountRouter router = Router(
            reader,
            leaseSet,
            new RecordingAffinityStore(),
            breakers);

        Result<IAccountLease> result = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("no_available_account", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Equal([AccountAId], breakers.ReadAccountIds);
        Assert.Empty(leaseSet.AcquireRequests);
    }

    [Fact]
    public async Task CapacityFullFallsBackToNextSameGroupCandidate()
    {
        AccountCandidate first = Candidate(
            AccountAId,
            concurrencyLimit: 2,
            priority: 20);
        AccountCandidate second = Candidate(
            AccountBId,
            channelId: ChannelBId,
            concurrencyLimit: 7,
            priority: 10);
        StubCandidateReader reader = new(SuccessCandidates(first, second));
        QueueLeaseSet leaseSet = new();
        leaseSet.AcquireResults.Enqueue(
            CoordinationLeaseAcquireResult.CapacityExceeded(
                activeCount: 2,
                retryAfter: TimeSpan.FromSeconds(20)));
        leaseSet.AcquireResults.Enqueue(
            CoordinationLeaseAcquireResult.Acquired(
                activeCount: 1,
                expiresAt: FirstExpiry,
                renewed: false));
        AccountRouter router = Router(reader, leaseSet, new RecordingAffinityStore());

        Result<IAccountLease> result = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(AccountBId, result.Value.Route.AccountId);
        Assert.Collection(
            leaseSet.AcquireRequests,
            request =>
            {
                Assert.Equal(AccountRouter.LeaseKey(AccountAId), request.KeyBase);
                Assert.Equal(2, request.Limit);
            },
            request =>
            {
                Assert.Equal(AccountRouter.LeaseKey(AccountBId), request.KeyBase);
                Assert.Equal(7, request.Limit);
            });
        Assert.Equal(
            leaseSet.AcquireRequests[0].Owner,
            leaseSet.AcquireRequests[1].Owner);
    }

    [Fact]
    public async Task AllAccountsAtCapacityReturnsShortestRoundedRetryAfter()
    {
        StubCandidateReader reader = new(SuccessCandidates(
            Candidate(AccountAId, priority: 20),
            Candidate(AccountBId, channelId: ChannelBId, priority: 10)));
        QueueLeaseSet leaseSet = new();
        leaseSet.AcquireResults.Enqueue(
            CoordinationLeaseAcquireResult.CapacityExceeded(
                activeCount: 2,
                retryAfter: TimeSpan.FromMilliseconds(7_500)));
        leaseSet.AcquireResults.Enqueue(
            CoordinationLeaseAcquireResult.CapacityExceeded(
                activeCount: 2,
                retryAfter: TimeSpan.FromMilliseconds(1_001)));
        AccountRouter router = Router(reader, leaseSet, new RecordingAffinityStore());

        Result<IAccountLease> result = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("account_capacity_unavailable", result.Error.Code);
        Assert.Equal(2, result.Error.RetryAfterSeconds);
        Assert.Equal(2, leaseSet.AcquireRequests.Count);
    }

    [Fact]
    public async Task UnknownAcquireDispositionFailsClosed()
    {
        StubCandidateReader reader = new(SuccessCandidates(Candidate(AccountAId)));
        QueueLeaseSet leaseSet = new();
        leaseSet.AcquireResults.Enqueue(
            new CoordinationLeaseAcquireResult(
                (CoordinationLeaseAcquireDisposition)int.MaxValue,
                ActiveCount: 0,
                ExpiresAt: default,
                RetryAfter: TimeSpan.Zero));
        AccountRouter router = Router(reader, leaseSet, new RecordingAffinityStore());

        Result<IAccountLease> result = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
    }

    [Fact]
    public void SelectionIsDeterministicAndPriorityDominatesWeightAndHealth()
    {
        AccountCandidate higherPriority = Candidate(
            AccountAId,
            health: AccountHealth.Degraded,
            priority: 20,
            weight: 1);
        AccountCandidate lowerPriority = Candidate(
            AccountBId,
            channelId: ChannelBId,
            health: AccountHealth.Healthy,
            priority: 10,
            weight: 1_000_000);
        RouteAccountCommand command = Command();

        IReadOnlyList<AccountCandidate> first =
            AccountSelectionStrategy.Order(
                [lowerPriority, higherPriority],
                command,
                stickyAccountId: null);
        IReadOnlyList<AccountCandidate> second =
            AccountSelectionStrategy.Order(
                [lowerPriority, higherPriority],
                command,
                stickyAccountId: null);

        Assert.Equal(AccountAId, first[0].AccountId);
        Assert.Equal(
            first.Select(candidate => candidate.AccountId),
            second.Select(candidate => candidate.AccountId));
    }

    [Fact]
    public void HigherWeightDeterministicallyLowersTheSameCandidatesScore()
    {
        AccountCandidate light = Candidate(AccountAId, weight: 1);
        AccountCandidate heavy = light with { Weight = 100 };

        IReadOnlyList<AccountCandidate> ordered =
            AccountSelectionStrategy.Order(
                [light, heavy],
                Command(),
                stickyAccountId: null);

        Assert.Same(heavy, ordered[0]);
        Assert.Same(light, ordered[1]);
    }

    [Fact]
    public void DegradedHealthDeterministicallyPenalizesTheSameCandidate()
    {
        AccountCandidate healthy = Candidate(
            AccountAId,
            health: AccountHealth.Healthy,
            weight: 10);
        AccountCandidate degraded = healthy with { Health = AccountHealth.Degraded };

        IReadOnlyList<AccountCandidate> ordered =
            AccountSelectionStrategy.Order(
                [degraded, healthy],
                Command(),
                stickyAccountId: null);

        Assert.Same(healthy, ordered[0]);
        Assert.Same(degraded, ordered[1]);
    }

    [Fact]
    public async Task MatchingStickyAffinityIsTriedFirstButStillAcquiresHardLease()
    {
        AccountCandidate preferredByPriority = Candidate(AccountAId, priority: 20);
        AccountCandidate sticky = Candidate(
            AccountBId,
            channelId: ChannelBId,
            priority: 1,
            configurationVersion: 9);
        StubCandidateReader reader = new(
            SuccessCandidates(preferredByPriority, sticky));
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        RecordingAffinityStore affinities = new()
        {
            ReadResult = new RouteAffinity(
                AccountBId,
                GroupPolicyVersion: 11,
                SupplyConfigurationVersion: 9),
        };
        AccountRouter router = Router(reader, leaseSet, affinities);

        Result<IAccountLease> result = await router.RouteAsync(
            Command(sessionHash: SessionHash),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(AccountBId, result.Value.Route.AccountId);
        Assert.Equal(
            AccountRouter.LeaseKey(AccountBId),
            Assert.Single(leaseSet.AcquireRequests).KeyBase);
        AffinitySetRequest persisted = Assert.Single(affinities.SetRequests);
        Assert.Equal(AccountBId, persisted.Affinity.AccountId);
        Assert.Equal(11, persisted.Affinity.GroupPolicyVersion);
        Assert.Equal(9, persisted.Affinity.SupplyConfigurationVersion);
    }

    [Fact]
    public async Task MissingStickyAffinityUsesDeterministicSelectionAndPersistsChoice()
    {
        AccountCandidate expected = Candidate(AccountAId, priority: 20);
        AccountCandidate other = Candidate(
            AccountBId,
            channelId: ChannelBId,
            priority: 10);
        StubCandidateReader reader = new(SuccessCandidates(expected, other));
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        RecordingAffinityStore affinities = new();
        AccountRouter router = Router(reader, leaseSet, affinities);

        Result<IAccountLease> result = await router.RouteAsync(
            Command(sessionHash: SessionHash),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected.AccountId, result.Value.Route.AccountId);
        Assert.Single(affinities.GetRequests);
        Assert.Equal(
            expected.AccountId,
            Assert.Single(affinities.SetRequests).Affinity.AccountId);
    }

    [Fact]
    public async Task GroupPolicyVersionDriftInvalidatesStickyAffinity()
    {
        AccountCandidate expected = Candidate(AccountAId, priority: 20);
        AccountCandidate stale = Candidate(
            AccountBId,
            channelId: ChannelBId,
            priority: 1);
        StubCandidateReader reader = new(SuccessCandidates(expected, stale));
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        RecordingAffinityStore affinities = new()
        {
            ReadResult = new RouteAffinity(
                AccountBId,
                GroupPolicyVersion: 10,
                SupplyConfigurationVersion: stale.ConfigurationVersion),
        };
        AccountRouter router = Router(reader, leaseSet, affinities);

        Result<IAccountLease> result = await router.RouteAsync(
            Command(sessionHash: SessionHash),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected.AccountId, result.Value.Route.AccountId);
    }

    [Fact]
    public async Task SupplyConfigurationVersionDriftInvalidatesStickyAffinity()
    {
        AccountCandidate expected = Candidate(AccountAId, priority: 20);
        AccountCandidate stale = Candidate(
            AccountBId,
            channelId: ChannelBId,
            priority: 1,
            configurationVersion: 9);
        StubCandidateReader reader = new(SuccessCandidates(expected, stale));
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        RecordingAffinityStore affinities = new()
        {
            ReadResult = new RouteAffinity(
                AccountBId,
                GroupPolicyVersion: 11,
                SupplyConfigurationVersion: 8),
        };
        AccountRouter router = Router(reader, leaseSet, affinities);

        Result<IAccountLease> result = await router.RouteAsync(
            Command(sessionHash: SessionHash),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected.AccountId, result.Value.Route.AccountId);
    }

    [Fact]
    public async Task StickyCacheReadAndWriteErrorsAreSoft()
    {
        RecordingCoordinationValueStore values = new()
        {
            ReadResult = CoordinationValueReadResult.Unavailable,
            WriteResult = CoordinationValueWriteResult.Unavailable,
        };
        CoordinationRouteAffinityStore affinities = new(values);
        StubCandidateReader reader = new(
            SuccessCandidates(Candidate(AccountAId)));
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        AccountRouter router = Router(reader, leaseSet, affinities);

        Result<IAccountLease> result = await router.RouteAsync(
            Command(sessionHash: SessionHash),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(AccountAId, result.Value.Route.AccountId);
        CoordinationValueGetRequest get = Assert.Single(values.GetRequests);
        Assert.Equal(
            CoordinationRouteAffinityStore.Key(GroupId, SessionHash),
            get.KeyBase);
        Assert.Equal(TimeSpan.FromMinutes(60), get.TimeToLive);
        CoordinationValueSetRequest set = Assert.Single(values.SetRequests);
        Assert.Equal(get.KeyBase, set.KeyBase);
        Assert.Equal(TimeSpan.FromMinutes(60), set.TimeToLive);
    }

    [Fact]
    public async Task MatchingStickyAffinityCannotBypassUnavailableLeaseCoordination()
    {
        AccountCandidate sticky = Candidate(AccountBId, channelId: ChannelBId);
        StubCandidateReader reader = new(SuccessCandidates(sticky));
        QueueLeaseSet leaseSet = new();
        leaseSet.AcquireResults.Enqueue(CoordinationLeaseAcquireResult.Unavailable);
        RecordingAffinityStore affinities = new()
        {
            ReadResult = new RouteAffinity(
                sticky.AccountId,
                GroupPolicyVersion: 11,
                SupplyConfigurationVersion: sticky.ConfigurationVersion),
        };
        AccountRouter router = Router(reader, leaseSet, affinities);

        Result<IAccountLease> result = await router.RouteAsync(
            Command(sessionHash: SessionHash),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
        Assert.Empty(affinities.SetRequests);
        Assert.Single(leaseSet.AcquireRequests);
    }

    [Fact]
    public async Task CancellationAfterLeaseAcquisitionReleasesBeforePropagating()
    {
        using CancellationTokenSource cancellation = new();
        StubCandidateReader reader = new(
            SuccessCandidates(Candidate(AccountAId)));
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        leaseSet.ReleaseResults.Enqueue(CoordinationLeaseReleaseResult.Released);
        AccountRouter router = Router(
            reader,
            leaseSet,
            new CancelingAffinityStore(cancellation));

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                router.RouteAsync(
                        Command(sessionHash: SessionHash),
                        cancellation.Token)
                    .AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        CoordinationLeaseAcquireRequest acquired =
            Assert.Single(leaseSet.AcquireRequests);
        CoordinationLeaseOwner released =
            Assert.Single(leaseSet.ReleaseRequests);
        Assert.Equal(acquired.Owner, released.Owner);
        Assert.Equal(acquired.KeyBase, released.KeyBase);
    }

    [Fact]
    public async Task LeaseOwnerIsRandom128BitLowerHexAndNotExposedByRoute()
    {
        StubCandidateReader reader = new(
            SuccessCandidates(Candidate(AccountAId)));
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        AccountRouter router = Router(reader, leaseSet, new RecordingAffinityStore());

        Result<IAccountLease> result = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        string owner = Assert.Single(leaseSet.AcquireRequests).Owner;
        Assert.Equal(32, owner.Length);
        Assert.All(
            owner,
            character => Assert.True(character is
                >= '0' and <= '9'
                or >= 'a' and <= 'f'));
        Assert.DoesNotContain(owner, result.Value.Route.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(AccountRoute).GetProperties(),
            property => property.Name.Contains("Owner", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(IAccountLease).GetProperties(),
            property => property.Name.Contains("Owner", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));

        AccountRoute route = result.Value.Route;
        Assert.Equal(AccountRouteProvider.OpenAi, route.Provider);
        Assert.Equal(Model, route.ClientModel);
        Assert.Equal("gpt-4.1-mini", route.UpstreamModel);
        Assert.Equal(
            new Uri("https://api.openai.example.test/v1"),
            route.UpstreamBaseUri);
        Assert.True(route.Capabilities.Responses);
        Assert.True(route.Capabilities.ChatCompletions);
        Assert.True(route.Capabilities.FunctionTools);
        Assert.True(route.Capabilities.Streaming);
        Assert.Equal(13, route.CredentialRevision);
    }

    [Fact]
    public async Task RenewUpdatesExpiryAndUsesTheOriginalOwner()
    {
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        leaseSet.RenewResults.Enqueue(
            CoordinationLeaseRenewResult.Renewed(RenewedExpiry));
        AccountRouter router = Router(
            new StubCandidateReader(SuccessCandidates(Candidate(AccountAId))),
            leaseSet,
            new RecordingAffinityStore());
        Result<IAccountLease> routed = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        AccountLeaseRenewResult renewed = await routed.Value.RenewAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountLeaseRenewDisposition.Renewed, renewed.Disposition);
        Assert.Equal(RenewedExpiry, Assert.IsType<AccountRoute>(renewed.Route).LeaseExpiresAt);
        Assert.Equal(RenewedExpiry, routed.Value.Route.LeaseExpiresAt);
        CoordinationLeaseOwner renewRequest =
            Assert.Single(leaseSet.RenewRequests);
        Assert.Equal(AccountRouter.LeaseKey(AccountAId), renewRequest.KeyBase);
        Assert.Equal(
            Assert.Single(leaseSet.AcquireRequests).Owner,
            renewRequest.Owner);
    }

    [Fact]
    public async Task LostRenewalMarksLeaseReleasedWithTypedDisposition()
    {
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        leaseSet.RenewResults.Enqueue(CoordinationLeaseRenewResult.Lost);
        AccountRouter router = Router(
            new StubCandidateReader(SuccessCandidates(Candidate(AccountAId))),
            leaseSet,
            new RecordingAffinityStore());
        Result<IAccountLease> routed = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        AccountLeaseRenewResult renewed = await routed.Value.RenewAsync(
            TestContext.Current.CancellationToken);
        Result<bool> released = await routed.Value.ReleaseAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountLeaseRenewDisposition.Lost, renewed.Disposition);
        Assert.Null(renewed.Route);
        Assert.True(released.IsSuccess);
        Assert.False(released.Value);
        Assert.Single(leaseSet.RenewRequests);
        Assert.Empty(leaseSet.ReleaseRequests);
    }

    [Fact]
    public async Task UnavailableRenewalFailsClosedWithoutLosingRetryableLeaseState()
    {
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        leaseSet.RenewResults.Enqueue(CoordinationLeaseRenewResult.Unavailable);
        leaseSet.RenewResults.Enqueue(
            CoordinationLeaseRenewResult.Renewed(RenewedExpiry));
        AccountRouter router = Router(
            new StubCandidateReader(SuccessCandidates(Candidate(AccountAId))),
            leaseSet,
            new RecordingAffinityStore());
        Result<IAccountLease> routed = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        AccountLeaseRenewResult unavailable = await routed.Value.RenewAsync(
            TestContext.Current.CancellationToken);
        AccountLeaseRenewResult recovered = await routed.Value.RenewAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountLeaseRenewDisposition.CoordinationUnavailable,
            unavailable.Disposition);
        Assert.Null(unavailable.Route);
        Assert.Equal(AccountLeaseRenewDisposition.Renewed, recovered.Disposition);
        Assert.Equal(
            RenewedExpiry,
            Assert.IsType<AccountRoute>(recovered.Route).LeaseExpiresAt);
        Assert.Equal(2, leaseSet.RenewRequests.Count);
    }

    [Fact]
    public async Task UnknownRenewDispositionFailsClosed()
    {
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        leaseSet.RenewResults.Enqueue(
            new CoordinationLeaseRenewResult(
                (CoordinationLeaseRenewDisposition)int.MaxValue,
                ExpiresAt: default));
        AccountRouter router = Router(
            new StubCandidateReader(SuccessCandidates(Candidate(AccountAId))),
            leaseSet,
            new RecordingAffinityStore());
        Result<IAccountLease> routed = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        AccountLeaseRenewResult renewed = await routed.Value.RenewAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountLeaseRenewDisposition.CoordinationUnavailable,
            renewed.Disposition);
        Assert.Null(renewed.Route);
        Assert.Single(leaseSet.RenewRequests);
    }

    [Fact]
    public async Task ReleaseIsIdempotentAndDisposeDoesNotReleaseAgain()
    {
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        leaseSet.ReleaseResults.Enqueue(CoordinationLeaseReleaseResult.Released);
        AccountRouter router = Router(
            new StubCandidateReader(SuccessCandidates(Candidate(AccountAId))),
            leaseSet,
            new RecordingAffinityStore());
        Result<IAccountLease> routed = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Result<bool> first = await routed.Value.ReleaseAsync(
            TestContext.Current.CancellationToken);
        Result<bool> second = await routed.Value.ReleaseAsync(
            TestContext.Current.CancellationToken);
        await routed.Value.DisposeAsync();

        Assert.True(first.IsSuccess);
        Assert.True(first.Value);
        Assert.True(second.IsSuccess);
        Assert.False(second.Value);
        CoordinationLeaseOwner releaseRequest =
            Assert.Single(leaseSet.ReleaseRequests);
        Assert.Equal(
            Assert.Single(leaseSet.AcquireRequests).Owner,
            releaseRequest.Owner);
    }

    [Fact]
    public async Task NotOwnedReleaseIsSuccessfulIdempotentNoOp()
    {
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        leaseSet.ReleaseResults.Enqueue(CoordinationLeaseReleaseResult.NotOwned);
        AccountRouter router = Router(
            new StubCandidateReader(SuccessCandidates(Candidate(AccountAId))),
            leaseSet,
            new RecordingAffinityStore());
        Result<IAccountLease> routed = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Result<bool> first = await routed.Value.ReleaseAsync(
            TestContext.Current.CancellationToken);
        Result<bool> second = await routed.Value.ReleaseAsync(
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.False(first.Value);
        Assert.True(second.IsSuccess);
        Assert.False(second.Value);
        Assert.Single(leaseSet.ReleaseRequests);
    }

    [Fact]
    public async Task UnavailableReleaseFailsClosedAndCanBeRetried()
    {
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        leaseSet.ReleaseResults.Enqueue(CoordinationLeaseReleaseResult.Unavailable);
        leaseSet.ReleaseResults.Enqueue(CoordinationLeaseReleaseResult.Released);
        AccountRouter router = Router(
            new StubCandidateReader(SuccessCandidates(Candidate(AccountAId))),
            leaseSet,
            new RecordingAffinityStore());
        Result<IAccountLease> routed = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Result<bool> first = await routed.Value.ReleaseAsync(
            TestContext.Current.CancellationToken);
        Result<bool> second = await routed.Value.ReleaseAsync(
            TestContext.Current.CancellationToken);

        Assert.True(first.IsFailure);
        Assert.Equal("coordination_unavailable", first.Error.Code);
        Assert.Equal(1, first.Error.RetryAfterSeconds);
        Assert.True(second.IsSuccess);
        Assert.True(second.Value);
        Assert.Equal(2, leaseSet.ReleaseRequests.Count);
    }

    [Fact]
    public async Task DisposeIsIdempotentWhenCoordinationObjectIsAlreadyDisposed()
    {
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        leaseSet.ReleaseException = new ObjectDisposedException("coordination");
        AccountRouter router = Router(
            new StubCandidateReader(SuccessCandidates(Candidate(AccountAId))),
            leaseSet,
            new RecordingAffinityStore());
        Result<IAccountLease> routed = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);

        await routed.Value.DisposeAsync();
        await routed.Value.DisposeAsync();

        Assert.Equal(2, leaseSet.ReleaseRequests.Count);
    }

    [Fact]
    public async Task AffinityWriteExceptionReleasesAcquiredLeaseAndFailsClosed()
    {
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        leaseSet.ReleaseResults.Enqueue(CoordinationLeaseReleaseResult.Released);
        AccountRouter router = Router(
            new StubCandidateReader(SuccessCandidates(Candidate(AccountAId))),
            leaseSet,
            new ThrowingAffinityStore());

        Result<IAccountLease> result = await router.RouteAsync(
            Command(SessionHash),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Single(leaseSet.AcquireRequests);
        Assert.Single(leaseSet.ReleaseRequests);
    }

    [Fact]
    public async Task RenewAfterReleaseFailsWithoutCallingRedisAgain()
    {
        QueueLeaseSet leaseSet = AcquiringLeaseSet();
        leaseSet.ReleaseResults.Enqueue(CoordinationLeaseReleaseResult.Released);
        AccountRouter router = Router(
            new StubCandidateReader(SuccessCandidates(Candidate(AccountAId))),
            leaseSet,
            new RecordingAffinityStore());
        Result<IAccountLease> routed = await router.RouteAsync(
            Command(),
            TestContext.Current.CancellationToken);
        _ = await routed.Value.ReleaseAsync(TestContext.Current.CancellationToken);

        AccountLeaseRenewResult renewed = await routed.Value.RenewAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountLeaseRenewDisposition.Lost, renewed.Disposition);
        Assert.Null(renewed.Route);
        Assert.Empty(leaseSet.RenewRequests);
    }

    private static QueueLeaseSet AcquiringLeaseSet()
    {
        QueueLeaseSet leaseSet = new();
        leaseSet.AcquireResults.Enqueue(
            CoordinationLeaseAcquireResult.Acquired(
                activeCount: 1,
                expiresAt: FirstExpiry,
                renewed: false));
        return leaseSet;
    }

    private static AccountRouter Router(
        IAccountCandidateReader candidates,
        ICoordinationLeaseSet leases,
        IRouteAffinityStore affinities,
        IAccountCircuitBreaker? breakers = null) =>
        new(
            candidates,
            leases,
            affinities,
            breakers ?? new StubCircuitBreaker());

    private static Result<IReadOnlyList<AccountCandidate>> SuccessCandidates(
        params AccountCandidate[] candidates) =>
        Result.Success<IReadOnlyList<AccountCandidate>>(candidates);

    public static TheoryData<string?> InvalidModels() => new()
    {
        null!,
        string.Empty,
        "   ",
        new string('m', 201),
        "gpt\u0000model",
    };

    private static RouteAccountCommand Command(string? sessionHash = null) =>
        new(
            GroupId,
            Model,
            RequestId,
            AttemptId,
            GroupPolicyVersion: 11,
            SessionAffinityHash: sessionHash);

    private static AccountCandidate Candidate(
        EntityId accountId,
        EntityId? groupId = null,
        EntityId? channelId = null,
        AccountHealth health = AccountHealth.Healthy,
        int concurrencyLimit = 2,
        int priority = 10,
        int weight = 10,
        long configurationVersion = 7,
        long credentialRevision = 13) =>
        new(
            groupId ?? GroupId,
            channelId ?? ChannelAId,
            accountId,
            UpstreamProvider.OpenAi,
            Model,
            "gpt-4.1-mini",
            "https://api.openai.example.test/v1",
            new ChannelCapabilitiesSnapshot(
                Responses: true,
                ChatCompletions: true,
                FunctionTools: true,
                Streaming: true),
            health,
            concurrencyLimit,
            priority,
            weight,
            configurationVersion,
            ChannelVersion: 5,
            AccountVersion: 6,
            CredentialRevision: credentialRevision);

    private static EntityId Id(string value) => new(Guid.Parse(value));

    private sealed class StubCandidateReader(
        Result<IReadOnlyList<AccountCandidate>> result) : IAccountCandidateReader
    {
        internal List<CandidateReadRequest> Requests { get; } = [];

        public ValueTask<Result<IReadOnlyList<AccountCandidate>>> GetCandidatesAsync(
            EntityId groupId,
            string model,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new CandidateReadRequest(groupId, model, cancellationToken));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StubCircuitBreaker : IAccountCircuitBreaker
    {
        internal static Result<AccountBreakerSnapshot> ClosedResult { get; } =
            Result.Success(new AccountBreakerSnapshot(
                AccountBreakerState.Closed,
                Samples: 0,
                Failures: 0,
                ConsecutiveFailures: 0,
                OpenUntil: null,
                AccountBreakerAction.None));

        internal Queue<Result<AccountBreakerSnapshot>> ReadResults { get; } = [];

        internal List<EntityId> ReadAccountIds { get; } = [];

        public ValueTask<Result<AccountBreakerSnapshot>> ReadAsync(
            EntityId accountId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadAccountIds.Add(accountId);
            return ValueTask.FromResult(
                ReadResults.Count == 0
                    ? ClosedResult
                    : ReadResults.Dequeue());
        }

        public ValueTask<Result<AccountBreakerSnapshot>> RecordAsync(
            AccountBreakerRecordCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<AccountBreakerProbeAcquireResult>>
            TryAcquireProbeAsync(
                EntityId accountId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class QueueLeaseSet : ICoordinationLeaseSet
    {
        internal Queue<CoordinationLeaseAcquireResult> AcquireResults { get; } = [];

        internal Queue<CoordinationLeaseRenewResult> RenewResults { get; } = [];

        internal Queue<CoordinationLeaseReleaseResult> ReleaseResults { get; } = [];

        internal List<CoordinationLeaseAcquireRequest> AcquireRequests { get; } = [];

        internal List<CoordinationLeaseOwner> RenewRequests { get; } = [];

        internal List<CoordinationLeaseOwner> ReleaseRequests { get; } = [];

        internal Exception? ReleaseException { get; set; }

        public ValueTask<CoordinationLeaseAcquireResult> AcquireAsync(
            CoordinationLeaseAcquireRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireRequests.Add(request);
            return ValueTask.FromResult(
                AcquireResults.Count == 0
                    ? CoordinationLeaseAcquireResult.Unavailable
                    : AcquireResults.Dequeue());
        }

        public ValueTask<CoordinationLeaseRenewResult> RenewAsync(
            CoordinationLeaseOwner request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenewRequests.Add(request);
            return ValueTask.FromResult(
                RenewResults.Count == 0
                    ? CoordinationLeaseRenewResult.Lost
                    : RenewResults.Dequeue());
        }

        public ValueTask<CoordinationLeaseReleaseResult> ReleaseAsync(
            CoordinationLeaseOwner request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleaseRequests.Add(request);
            if (ReleaseException is { } exception)
            {
                throw exception;
            }

            return ValueTask.FromResult(
                ReleaseResults.Count == 0
                    ? CoordinationLeaseReleaseResult.NotOwned
                    : ReleaseResults.Dequeue());
        }
    }

    private sealed class RecordingAffinityStore : IRouteAffinityStore
    {
        internal RouteAffinity? ReadResult { get; init; }

        internal List<AffinityGetRequest> GetRequests { get; } = [];

        internal List<AffinitySetRequest> SetRequests { get; } = [];

        public ValueTask<RouteAffinity?> GetAsync(
            EntityId groupId,
            string sessionHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetRequests.Add(
                new AffinityGetRequest(groupId, sessionHash, cancellationToken));
            return ValueTask.FromResult(ReadResult);
        }

        public ValueTask SetAsync(
            EntityId groupId,
            string sessionHash,
            RouteAffinity affinity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetRequests.Add(
                new AffinitySetRequest(
                    groupId,
                    sessionHash,
                    affinity,
                    cancellationToken));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelingAffinityStore(
        CancellationTokenSource cancellation) : IRouteAffinityStore
    {
        public ValueTask<RouteAffinity?> GetAsync(
            EntityId groupId,
            string sessionHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<RouteAffinity?>(null);
        }

        public ValueTask SetAsync(
            EntityId groupId,
            string sessionHash,
            RouteAffinity affinity,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private sealed class ThrowingAffinityStore : IRouteAffinityStore
    {
        public ValueTask<RouteAffinity?> GetAsync(
            EntityId groupId,
            string sessionHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<RouteAffinity?>(null);
        }

        public ValueTask SetAsync(
            EntityId groupId,
            string sessionHash,
            RouteAffinity affinity,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(
                new InvalidOperationException("injected affinity failure"));
    }

    private sealed class RecordingCoordinationValueStore : ICoordinationValueStore
    {
        internal CoordinationValueReadResult ReadResult { get; init; } =
            CoordinationValueReadResult.Missing;

        internal CoordinationValueWriteResult WriteResult { get; init; } =
            CoordinationValueWriteResult.Stored;

        internal List<CoordinationValueGetRequest> GetRequests { get; } = [];

        internal List<CoordinationValueSetRequest> SetRequests { get; } = [];

        public ValueTask<CoordinationValueReadResult> GetAndRefreshAsync(
            string keyBase,
            TimeSpan timeToLive,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetRequests.Add(
                new CoordinationValueGetRequest(
                    keyBase,
                    timeToLive,
                    cancellationToken));
            return ValueTask.FromResult(ReadResult);
        }

        public ValueTask<CoordinationValueWriteResult> SetAsync(
            string keyBase,
            string value,
            TimeSpan timeToLive,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetRequests.Add(
                new CoordinationValueSetRequest(
                    keyBase,
                    value,
                    timeToLive,
                    cancellationToken));
            return ValueTask.FromResult(WriteResult);
        }
    }

    private sealed record CandidateReadRequest(
        EntityId GroupId,
        string Model,
        CancellationToken CancellationToken);

    private sealed record AffinityGetRequest(
        EntityId GroupId,
        string SessionHash,
        CancellationToken CancellationToken);

    private sealed record AffinitySetRequest(
        EntityId GroupId,
        string SessionHash,
        RouteAffinity Affinity,
        CancellationToken CancellationToken);

    private sealed record CoordinationValueGetRequest(
        string KeyBase,
        TimeSpan TimeToLive,
        CancellationToken CancellationToken);

    private sealed record CoordinationValueSetRequest(
        string KeyBase,
        string Value,
        TimeSpan TimeToLive,
        CancellationToken CancellationToken);
}
