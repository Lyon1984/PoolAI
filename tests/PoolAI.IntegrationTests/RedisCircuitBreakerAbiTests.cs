using System.Globalization;
using System.Runtime.CompilerServices;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure.Redis;
using StackExchange.Redis;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class RedisCircuitBreakerAbiTests(PostgresRuntimeFixture fixture)
{
    private const string FirstOwner = "11111111111111111111111111111111";
    private const string SecondOwner = "22222222222222222222222222222222";
    private const string ThirdOwner = "33333333333333333333333333333333";
    private const string FourthOwner = "44444444444444444444444444444444";
    private const string FifthOwner = "55555555555555555555555555555555";
    private const string SixthOwner = "66666666666666666666666666666666";
    private const string RecordScript = "breaker_record_v1.lua";
    private const string AcquireScript = "breaker_probe_acquire_v1.lua";
    private const string CompleteScript = "breaker_probe_complete_v1.lua";
    private const string LeaseAcquireScript = "lease_acquire_v1.lua";
    private const string LeaseReleaseScript = "lease_release_v1.lua";
    private readonly PostgresRuntimeFixture _fixture =
        fixture ?? throw new ArgumentNullException(nameof(fixture));

    [Fact]
    [Trait("Category", "Redis")]
    public async Task RecordUsesSharedThresholdsAndDoesNotMutateAnOpenGeneration()
    {
        ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable redisLease = redis.ConfigureAwait(true);
        IDatabase database = redis.GetDatabase();

        await AssertConsecutiveThresholdAsync(database).ConfigureAwait(true);
        await AssertRatioThresholdAsync(database).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task RecordIgnoredPreservesTtlAndControlledActiveResetDeletesGeneration()
    {
        ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable redisLease = redis.ConfigureAwait(true);
        IDatabase database = redis.GetDatabase();
        BreakerKeys keys = NewKeys();

        await AssertIgnoredDoesNotRenewAsync(database, keys).ConfigureAwait(true);
        await AssertControlledResetAsync(database, keys).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task AcquireReturnsMaximumActiveTtlAndNeverRenewsAnOwner()
    {
        ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable redisLease = redis.ConfigureAwait(true);
        IDatabase database = redis.GetDatabase();
        BreakerKeys keys = NewKeys();
        await SeedHalfOpenAsync(database, keys, openCount: 1).ConfigureAwait(true);
        Assert.True(await database.StringSetAsync(
            keys.Cooldown,
            """{"reason":"rate_limited","retry_at":1,"source_status":429}""",
            TimeSpan.FromSeconds(2)).ConfigureAwait(true));
        Assert.True(await database.StringSetAsync(
            keys.Probe,
            FirstOwner,
            TimeSpan.FromSeconds(5)).ConfigureAwait(true));

        long[] busy = await AcquireAsync(
            database,
            keys,
            SecondOwner).ConfigureAwait(true);
        Assert.Equal(0, busy[0]);
        Assert.InRange(busy[1], 4_000, 5_000);

        Assert.True(await database.KeyDeleteAsync(keys.Cooldown).ConfigureAwait(true));
        TimeSpan ownerTtlBefore = Assert.IsType<TimeSpan>(
            await database.KeyTimeToLiveAsync(keys.Probe).ConfigureAwait(true));
        await Task.Delay(
            TimeSpan.FromMilliseconds(25),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        long[] sameOwner = await AcquireAsync(
            database,
            keys,
            FirstOwner).ConfigureAwait(true);
        TimeSpan ownerTtlAfter = Assert.IsType<TimeSpan>(
            await database.KeyTimeToLiveAsync(keys.Probe).ConfigureAwait(true));

        Assert.Equal(0, sameOwner[0]);
        Assert.InRange(sameOwner[1], 1, 5_000);
        Assert.True(ownerTtlAfter < ownerTtlBefore);
        Assert.Equal(
            FirstOwner,
            (await database.StringGetAsync(keys.Probe).ConfigureAwait(true)).ToString());

        Assert.True(await database.KeyDeleteAsync(keys.Probe).ConfigureAwait(true));
        long[] acquired = await AcquireAsync(
            database,
            keys,
            SecondOwner).ConfigureAwait(true);
        Assert.Equal(1, acquired[0]);
        Assert.True(acquired[1] > 0);
        Assert.Equal(
            SecondOwner,
            (await database.StringGetAsync(keys.Probe).ConfigureAwait(true)).ToString());
        TimeSpan acquiredTtl = Assert.IsType<TimeSpan>(
            await database.KeyTimeToLiveAsync(keys.Probe).ConfigureAwait(true));
        Assert.InRange(acquiredTtl, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10));
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task ScriptsRejectNonCanonicalIntegersTypesAndCounterOverflowWithoutWrites()
    {
        ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable redisLease = redis.ConfigureAwait(true);
        IDatabase database = redis.GetDatabase();

        await AssertCanonicalArgumentsAsync(database).ConfigureAwait(true);
        await AssertKeyAndHashShapesAsync(database).ConfigureAwait(true);
        await AssertWrongRedisTypesAsync(database).ConfigureAwait(true);
        await AssertCounterBoundsAsync(database).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task CompleteNeedsTwoOwnedSuccessesAndFencesStaleOrReopenedOwners()
    {
        ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable redisLease = redis.ConfigureAwait(true);
        IDatabase database = redis.GetDatabase();

        await AssertTwoSuccessRecoveryAsync(database).ConfigureAwait(true);
        await AssertStaleAndReopenFenceAsync(database).ConfigureAwait(true);
        await AssertFailureReopenAsync(database).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task OnlyOneGlobalHalfOpenProbeClosesOrReopensTheBreaker()
    {
        // Governing contract: AC-042 and redis-contract.md section 7.3.
        // These are three independent clients representing two Api instances
        // and the Worker. A winning half-open owner must still acquire the
        // ordinary Account lease before it can complete the shared probe.
        ConnectionMultiplexer first = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable firstLease =
            first.ConfigureAwait(true);
        ConnectionMultiplexer second = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable secondLease =
            second.ConfigureAwait(true);
        ConnectionMultiplexer worker = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable workerLease =
            worker.ConfigureAwait(true);
        IDatabase firstClient = first.GetDatabase();
        IDatabase secondClient = second.GetDatabase();
        IDatabase workerClient = worker.GetDatabase();

        await AssertUnknownAndAuthBlockedRemainIneligibleAsync(
            firstClient,
            secondClient,
            workerClient).ConfigureAwait(true);
        await AssertThreeClientsNeedTwoOwnedSuccessesAsync(
            firstClient,
            secondClient,
            workerClient).ConfigureAwait(true);
        await AssertThreeClientsReopenOnFailureAsync(
            firstClient,
            secondClient,
            workerClient).ConfigureAwait(true);
    }

    private static async Task AssertUnknownAndAuthBlockedRemainIneligibleAsync(
        IDatabase firstClient,
        IDatabase secondClient,
        IDatabase workerClient)
    {
        BreakerKeys newUnknown = NewKeys();
        long[] unknownAcquire = await AcquireAsync(
            firstClient,
            newUnknown,
            FirstOwner).ConfigureAwait(true);
        Assert.Equal(new long[] { 0, 0 }, unknownAcquire);

        BreakerKeys authBlockedKeys = NewKeys();
        await SeedAsync(
            firstClient,
            authBlockedKeys,
            samples: 0,
            failures: 0,
            consecutiveFailures: 0,
            openUntilMilliseconds: 0,
            openCount: 1,
            halfOpenSuccesses: 0,
            authBlocked: 1).ConfigureAwait(true);
        long[] authAcquire = await AcquireAsync(
            secondClient,
            authBlockedKeys,
            SecondOwner).ConfigureAwait(true);
        Assert.Equal(new long[] { 0, 0 }, authAcquire);
        long[] passiveSuccess = await RecordAsync(
            workerClient,
            authBlockedKeys,
            "success",
            "0",
            "0",
            "200",
            "passive").ConfigureAwait(true);
        Assert.Equal(
            new long[] { 1, 0, 0, 0, 0, 4 },
            passiveSuccess);
        _ = await firstClient.KeyDeleteAsync(
            newUnknown.All.Concat(authBlockedKeys.All).ToArray())
            .ConfigureAwait(true);
    }

    private static async Task AssertThreeClientsNeedTwoOwnedSuccessesAsync(
        IDatabase firstClient,
        IDatabase secondClient,
        IDatabase workerClient)
    {
        BreakerKeys keys = NewKeys();
        await SeedHalfOpenAsync(firstClient, keys, openCount: 1)
            .ConfigureAwait(true);
        await CompleteSuccessfulRoundAsync(
            keys,
            new ProbeContender[]
            {
                new(firstClient, FirstOwner),
                new(secondClient, SecondOwner),
                new(workerClient, ThirdOwner),
            },
            "200",
            new long[] { 1, 2, 1, 0, 5 }).ConfigureAwait(true);
        await CompleteSuccessfulRoundAsync(
            keys,
            new ProbeContender[]
            {
                new(firstClient, FourthOwner),
                new(secondClient, FifthOwner),
                new(workerClient, SixthOwner),
            },
            "204",
            new long[] { 1, 0, 0, 0, 1 }).ConfigureAwait(true);
        Assert.False(
            await firstClient.KeyExistsAsync(keys.Breaker)
                .ConfigureAwait(true));
        _ = await firstClient.KeyDeleteAsync(
            keys.All.Append(AccountLeaseKey(keys)).ToArray())
            .ConfigureAwait(true);
    }

    private static async Task CompleteSuccessfulRoundAsync(
        BreakerKeys keys,
        ProbeContender[] contenders,
        string sourceStatus,
        long[] expected)
    {
        ProbeWinner winner = await AcquireOnlyOneAsync(keys, contenders)
            .ConfigureAwait(true);
        await AssertAccountLeaseAsync(winner, keys).ConfigureAwait(true);
        long[] completed = await CompleteAsync(
            winner.Database,
            keys,
            winner.Owner,
            "success",
            "0",
            "0",
            sourceStatus).ConfigureAwait(true);
        Assert.Equal(expected, completed);
        await ReleaseAccountLeaseAsync(winner, keys).ConfigureAwait(true);
    }

    private static async Task AssertThreeClientsReopenOnFailureAsync(
        IDatabase firstClient,
        IDatabase secondClient,
        IDatabase workerClient)
    {
        BreakerKeys keys = NewKeys();
        await SeedHalfOpenAsync(firstClient, keys, openCount: 1)
            .ConfigureAwait(true);
        ProbeWinner winner = await AcquireOnlyOneAsync(
            keys,
            new ProbeContender[]
            {
                new(firstClient, FirstOwner),
                new(secondClient, SecondOwner),
                new(workerClient, ThirdOwner),
            }).ConfigureAwait(true);
        await AssertAccountLeaseAsync(winner, keys).ConfigureAwait(true);
        long[] reopened = await CompleteAsync(
            winner.Database,
            keys,
            winner.Owner,
            "transient_failure",
            "0",
            "0",
            "500").ConfigureAwait(true);
        Assert.Equal(new long[] { 1, 1, 0 }, reopened[..3]);
        Assert.True(reopened[3] > 0);
        Assert.Equal(3, reopened[4]);
        Assert.Equal(
            "2",
            (await firstClient.HashGetAsync(
                keys.Breaker,
                "open_count").ConfigureAwait(true)).ToString());
        Assert.False(
            await firstClient.KeyExistsAsync(keys.Probe)
                .ConfigureAwait(true));
        await ReleaseAccountLeaseAsync(winner, keys)
            .ConfigureAwait(true);
        _ = await firstClient.KeyDeleteAsync(
            keys.All.Append(AccountLeaseKey(keys)).ToArray())
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Redis")]
    public void AdapterRequiresRespIntegersAndExactReturnSemantics()
    {
        CoordinationBreakerRecordResult validRecord =
            RedisCoordinationCircuitBreaker.ParseRecord(
                IntegerArray(0, 1, 0, 0, 0, 1));
        Assert.Equal(
            CoordinationBreakerRecordDisposition.Recorded,
            validRecord.Disposition);

        Assert.Equal(
            CoordinationBreakerRecordDisposition.Unavailable,
            RedisCoordinationCircuitBreaker.ParseRecord(
                BulkStringArray("0", "1", "0", "0", "0", "1"))
                .Disposition);
        Assert.Equal(
            CoordinationBreakerRecordDisposition.Unavailable,
            RedisCoordinationCircuitBreaker.ParseRecord(
                IntegerArray(1, 0, 0, 0, 0, 3))
                .Disposition);
        Assert.Equal(
            CoordinationBreakerRecordDisposition.Unavailable,
            RedisCoordinationCircuitBreaker.ParseRecord(
                IntegerArray(0, 1, 0, 1, 0, 1))
                .Disposition);

        Assert.Equal(
            CoordinationProbeAcquireDisposition.Unavailable,
            RedisCoordinationCircuitBreaker.ParseProbeAcquire(
                BulkStringArray("0", "1"))
                .Disposition);
        Assert.Equal(
            CoordinationProbeAcquireDisposition.Rejected,
            RedisCoordinationCircuitBreaker.ParseProbeAcquire(
                IntegerArray(0, 0))
                .Disposition);

        Assert.Equal(
            CoordinationProbeCompleteDisposition.NotOwner,
            RedisCoordinationCircuitBreaker.ParseProbeComplete(
                IntegerArray(0, 0, 0, 0, 0))
                .Disposition);
        Assert.Equal(
            CoordinationProbeCompleteDisposition.Unavailable,
            RedisCoordinationCircuitBreaker.ParseProbeComplete(
                IntegerArray(0, 2, 1, 0, 0))
                .Disposition);
        Assert.Equal(
            CoordinationProbeCompleteDisposition.Unavailable,
            RedisCoordinationCircuitBreaker.ParseProbeComplete(
                IntegerArray(1, 1, 0, 0, 3))
                .Disposition);
        Assert.Equal(
            CoordinationProbeCompleteDisposition.Completed,
            RedisCoordinationCircuitBreaker.ParseProbeComplete(
                IntegerArray(1, 2, 1, 0, 5))
                .Disposition);
    }

    private static async Task AssertConsecutiveThresholdAsync(IDatabase database)
    {
        BreakerKeys keys = NewKeys();
        for (int sample = 1; sample <= 4; sample++)
        {
            long[] closed = await RecordAsync(
                database,
                keys,
                "transient_failure",
                "0",
                "0",
                "500",
                "passive").ConfigureAwait(true);
            Assert.Equal([0, sample, sample, sample, 0, 2], closed);
        }

        long[] opened = await RecordAsync(
            database,
            keys,
            "transient_failure",
            "0",
            "0",
            "500",
            "passive").ConfigureAwait(true);
        Assert.Equal(1, opened[0]);
        Assert.Equal([5, 5, 5], opened[1..4]);
        Assert.True(opened[4] > 0);
        Assert.Equal(3, opened[5]);

        long[] lateSuccess = await RecordAsync(
            database,
            keys,
            "success",
            "0",
            "0",
            "200",
            "passive").ConfigureAwait(true);
        Assert.Equal(opened, lateSuccess);
    }

    private static async Task AssertRatioThresholdAsync(IDatabase database)
    {
        BreakerKeys keys = NewKeys();
        string[] outcomes =
        [
            "transient_failure",
            "success",
            "transient_failure",
            "success",
            "transient_failure",
            "success",
            "transient_failure",
            "success",
            "success",
            "transient_failure",
        ];
        long[] result = [];
        foreach (string outcome in outcomes)
        {
            result = await RecordAsync(
                database,
                keys,
                outcome,
                "0",
                "0",
                string.Equals(outcome, "success", StringComparison.Ordinal)
                    ? "200"
                    : "500",
                "passive").ConfigureAwait(true);
        }

        Assert.Equal(1, result[0]);
        Assert.Equal(10, result[1]);
        Assert.Equal(5, result[2]);
        Assert.Equal(1, result[3]);
        Assert.True(result[4] > 0);
        Assert.Equal(3, result[5]);
        Assert.Equal(
            "1",
            (await database.HashGetAsync(
                keys.Breaker,
                "open_count").ConfigureAwait(true)).ToString());
    }

    private static async Task AssertIgnoredDoesNotRenewAsync(
        IDatabase database,
        BreakerKeys keys)
    {
        long[] recorded = await RecordAsync(
            database,
            keys,
            "success",
            "0",
            "0",
            "204",
            "passive").ConfigureAwait(true);
        Assert.Equal([0, 1, 0, 0, 0, 1], recorded);
        await AssertBreakerTtlAsync(database, keys.Breaker).ConfigureAwait(true);
        Assert.True(await database
            .KeyExpireAsync(keys.Breaker, TimeSpan.FromMinutes(2))
            .ConfigureAwait(true));
        TimeSpan before = Assert.IsType<TimeSpan>(
            await database.KeyTimeToLiveAsync(keys.Breaker).ConfigureAwait(true));

        await Task.Delay(
            TimeSpan.FromMilliseconds(25),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        long[] ignored = await RecordAsync(
            database,
            keys,
            "ignored",
            "0",
            "0",
            "400",
            "passive").ConfigureAwait(true);
        TimeSpan after = Assert.IsType<TimeSpan>(
            await database.KeyTimeToLiveAsync(keys.Breaker).ConfigureAwait(true));

        Assert.Equal([0, 1, 0, 0, 0, 0], ignored);
        Assert.True(after < before);
        Assert.InRange(after, TimeSpan.FromSeconds(115), TimeSpan.FromMinutes(2));
    }

    private static async Task AssertControlledResetAsync(
        IDatabase database,
        BreakerKeys keys)
    {
        _ = await database.KeyDeleteAsync(keys.All).ConfigureAwait(true);
        await SeedHalfOpenAsync(database, keys, openCount: 1).ConfigureAwait(true);
        Assert.Equal(
            1,
            (await AcquireAsync(database, keys, FirstOwner).ConfigureAwait(true))[0]);
        Assert.True(await database.KeyExistsAsync(keys.Probe).ConfigureAwait(true));

        long[] reset = await RecordAsync(
            database,
            keys,
            "success",
            "0",
            "0",
            "200",
            "controlled_active").ConfigureAwait(true);
        Assert.Equal([0, 0, 0, 0, 0, 1], reset);
        Assert.False(await database.KeyExistsAsync(keys.Breaker).ConfigureAwait(true));
        Assert.False(await database.KeyExistsAsync(keys.Cooldown).ConfigureAwait(true));
        Assert.False(await database.KeyExistsAsync(keys.Probe).ConfigureAwait(true));

        await SeedHalfOpenAsync(database, keys, openCount: 1).ConfigureAwait(true);
        await AssertResultAsync(
            [0, 0, 0, 0, 0],
            CompleteAsync(
                database,
                keys,
                FirstOwner,
                "success",
                "0",
                "0",
                "200")).ConfigureAwait(true);
        Assert.Equal(
            "0",
            (await database.HashGetAsync(
                keys.Breaker,
                "half_open_successes").ConfigureAwait(true)).ToString());
    }

    private static async Task AssertCanonicalArgumentsAsync(IDatabase database)
    {
        foreach (string retryAfter in new[] { "00", "01", "+1", "1.0", "1e0", " 1" })
        {
            BreakerKeys keys = NewKeys();
            long[] invalid = await RecordAsync(
                database,
                keys,
                "rate_limited",
                retryAfter,
                "0",
                "429",
                "passive").ConfigureAwait(true);
            Assert.Equal([-1, 0, 0, 0, 0, 0], invalid);
            Assert.False(await database.KeyExistsAsync(keys.Breaker).ConfigureAwait(true));
            Assert.False(await database.KeyExistsAsync(keys.Cooldown).ConfigureAwait(true));
        }

        await AssertOtherScriptCanonicalArgumentsAsync(database).ConfigureAwait(true);
        await AssertInvalidRecordCombinationsAsync(database).ConfigureAwait(true);
    }

    private static async Task AssertOtherScriptCanonicalArgumentsAsync(
        IDatabase database)
    {
        BreakerKeys acquireKeys = NewKeys();
        long[] invalidAcquire = await EvaluateAsync(
            database,
            AcquireScript,
            acquireKeys.All,
            FirstOwner,
            "010000",
            "1").ConfigureAwait(true);
        Assert.Equal([-1, 0], invalidAcquire);

        BreakerKeys completeKeys = NewKeys();
        long[] invalidComplete = await EvaluateAsync(
            database,
            CompleteScript,
            completeKeys.All,
            FirstOwner,
            "rate_limited",
            "01",
            "0",
            "429",
            "1").ConfigureAwait(true);
        Assert.Equal([-1, 0, 0, 0, 0], invalidComplete);
    }

    private static async Task AssertInvalidRecordCombinationsAsync(
        IDatabase database)
    {
        foreach (string[] arguments in InvalidRecordCombinations())
        {
            BreakerKeys keys = NewKeys();
            await AssertResultAsync(
                [-1, 0, 0, 0, 0, 0],
                EvaluateAsync(
                    database,
                    RecordScript,
                    keys.All,
                    arguments)).ConfigureAwait(true);
        }
    }

    private static string[][] InvalidRecordCombinations() =>
    [
        ["success", "0", "00", "200", "passive", "1"],
        ["success", "0", "0", "0200", "passive", "1"],
        ["success", "0", "0", "200", "passive", "01"],
        ["success", "1", "0", "200", "passive", "1"],
        ["success", "0", "0", "199", "passive", "1"],
        ["success", "0", "0", "300", "passive", "1"],
        ["transient_failure", "1", "0", "500", "passive", "1"],
        ["transient_failure", "0", "0", "400", "passive", "1"],
        ["transient_failure", "0", "1001", "500", "passive", "1"],
        ["rate_limited", "0", "1", "429", "passive", "1"],
        ["rate_limited", "0", "0", "428", "passive", "1"],
        ["auth_failure", "1", "0", "401", "passive", "1"],
        ["auth_failure", "0", "0", "400", "passive", "1"],
        ["ignored", "0", "0", "401", "passive", "1"],
        ["ignored", "0", "0", "403", "passive", "1"],
        ["ignored", "0", "0", "408", "passive", "1"],
        ["ignored", "0", "0", "429", "passive", "1"],
        ["ignored", "0", "0", "500", "passive", "1"],
        ["ignored", "0", "0", "400", "active", "1"],
        ["other", "0", "0", "0", "passive", "1"],
    ];

    private static async Task AssertKeyAndHashShapesAsync(IDatabase database)
    {
        await AssertInvalidKeyShapesAsync(database).ConfigureAwait(true);
        await AssertInvalidHashShapesAsync(database).ConfigureAwait(true);
    }

    private static async Task AssertInvalidKeyShapesAsync(IDatabase database)
    {
        BreakerKeys keys = NewKeys();
        BreakerKeys otherTag = NewKeys();
        await AssertResultAsync(
            [-1, 0, 0, 0, 0, 0],
            EvaluateAsync(
                database,
                RecordScript,
                [keys.Breaker, otherTag.Cooldown, keys.Probe],
                "success",
                "0",
                "0",
                "200",
                "passive",
                "1")).ConfigureAwait(true);
        await AssertResultAsync(
            [-1, 0],
            EvaluateAsync(
                database,
                AcquireScript,
                [keys.Breaker, keys.Cooldown],
                FirstOwner,
                "10000",
                "1")).ConfigureAwait(true);
    }

    private static async Task AssertInvalidHashShapesAsync(IDatabase database)
    {
        BreakerKeys decimalKeys = NewKeys();
        await SeedHalfOpenAsync(database, decimalKeys, openCount: 1)
            .ConfigureAwait(true);
        await database.HashSetAsync(
            decimalKeys.Breaker,
            "samples",
            "01").ConfigureAwait(true);
        await AssertResultAsync(
            [-1, 0],
            AcquireAsync(database, decimalKeys, FirstOwner)).ConfigureAwait(true);

        BreakerKeys extraFieldKeys = NewKeys();
        await SeedHalfOpenAsync(database, extraFieldKeys, openCount: 1)
            .ConfigureAwait(true);
        await database.HashSetAsync(
            extraFieldKeys.Breaker,
            "extra",
            "0").ConfigureAwait(true);
        await AssertResultAsync(
            [-1, 0],
            AcquireAsync(database, extraFieldKeys, FirstOwner)).ConfigureAwait(true);

        BreakerKeys impossibleClosedKeys = NewKeys();
        await SeedAsync(
            database,
            impossibleClosedKeys,
            0,
            0,
            0,
            0,
            0,
            0,
            1).ConfigureAwait(true);
        await AssertResultAsync(
            [-1, 0],
            AcquireAsync(
                database,
                impossibleClosedKeys,
                FirstOwner)).ConfigureAwait(true);

        await AssertClosedCooldownContradictionAsync(database).ConfigureAwait(true);
    }

    private static async Task AssertClosedCooldownContradictionAsync(
        IDatabase database)
    {
        BreakerKeys closedWithCooldownKeys = NewKeys();
        await SeedAsync(
            database,
            closedWithCooldownKeys,
            0,
            0,
            0,
            0,
            0,
            0,
            0).ConfigureAwait(true);
        Assert.True(await database.StringSetAsync(
            closedWithCooldownKeys.Cooldown,
            """{"reason":"rate_limited","retry_at":1,"source_status":429}""",
            TimeSpan.FromSeconds(30)).ConfigureAwait(true));
        await AssertResultAsync(
            [-1, 0, 0, 0, 0, 0],
            RecordAsync(
                database,
                closedWithCooldownKeys,
                "ignored",
                "0",
                "0",
                "400",
                "passive")).ConfigureAwait(true);
    }

    private static async Task AssertWrongRedisTypesAsync(IDatabase database)
    {
        BreakerKeys breakerKeys = NewKeys();
        Assert.True(await database.StringSetAsync(
            breakerKeys.Breaker,
            "not-a-hash").ConfigureAwait(true));
        await AssertResultAsync(
            [-1, 0, 0, 0, 0, 0],
            RecordAsync(
                database,
                breakerKeys,
                "ignored",
                "0",
                "0",
                "0",
                "passive")).ConfigureAwait(true);

        await AssertWrongCooldownTypeAsync(database).ConfigureAwait(true);
        await AssertWrongProbeTypeAsync(database).ConfigureAwait(true);
    }

    private static async Task AssertWrongCooldownTypeAsync(IDatabase database)
    {
        BreakerKeys keys = NewKeys();
        await SeedHalfOpenAsync(database, keys, openCount: 1).ConfigureAwait(true);
        _ = await database.ListRightPushAsync(
            keys.Cooldown,
            "not-a-string").ConfigureAwait(true);
        Assert.True(await database.KeyExpireAsync(
            keys.Cooldown,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true));
        await AssertResultAsync(
            [-1, 0],
            AcquireAsync(database, keys, FirstOwner)).ConfigureAwait(true);
    }

    private static async Task AssertWrongProbeTypeAsync(IDatabase database)
    {
        BreakerKeys keys = NewKeys();
        await SeedHalfOpenAsync(database, keys, openCount: 1).ConfigureAwait(true);
        await database.HashSetAsync(
            keys.Probe,
            "owner",
            FirstOwner).ConfigureAwait(true);
        Assert.True(await database.KeyExpireAsync(
            keys.Probe,
            TimeSpan.FromSeconds(30)).ConfigureAwait(true));
        await AssertResultAsync(
            [-1, 0, 0, 0, 0],
            CompleteAsync(
                database,
                keys,
                FirstOwner,
                "success",
                "0",
                "0",
                "200")).ConfigureAwait(true);
    }

    private static async Task AssertCounterBoundsAsync(IDatabase database)
    {
        foreach (string counterField in new[]
        {
            "samples",
            "failures",
            "consecutive_failures",
            "open_count",
        })
        {
            BreakerKeys keys = NewKeys();
            await SeedHalfOpenAsync(database, keys, openCount: 1)
                .ConfigureAwait(true);
            await database.HashSetAsync(
                keys.Breaker,
                counterField,
                "2147483648").ConfigureAwait(true);
            await AssertResultAsync(
                [-1, 0],
                AcquireAsync(database, keys, FirstOwner)).ConfigureAwait(true);
        }

        await AssertCounterIncrementOverflowAsync(database).ConfigureAwait(true);
    }

    private static async Task AssertCounterIncrementOverflowAsync(IDatabase database)
    {
        BreakerKeys keys = NewKeys();
        await AssertResultAsync(
            [0, 1, 0, 0, 0, 1],
            RecordAsync(
                database,
                keys,
                "success",
                "0",
                "0",
                "200",
                "passive")).ConfigureAwait(true);
        await database.HashSetAsync(
            keys.Breaker,
            "samples",
            int.MaxValue.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);
        TimeSpan ttlBefore = Assert.IsType<TimeSpan>(
            await database.KeyTimeToLiveAsync(keys.Breaker).ConfigureAwait(true));

        await AssertResultAsync(
            [-1, 0, 0, 0, 0, 0],
            RecordAsync(
                database,
                keys,
                "success",
                "0",
                "0",
                "200",
                "passive")).ConfigureAwait(true);
        Assert.Equal(
            int.MaxValue.ToString(CultureInfo.InvariantCulture),
            (await database.HashGetAsync(
                keys.Breaker,
                "samples").ConfigureAwait(true)).ToString());
        TimeSpan ttlAfter = Assert.IsType<TimeSpan>(
            await database.KeyTimeToLiveAsync(keys.Breaker).ConfigureAwait(true));
        Assert.True(ttlAfter <= ttlBefore);
    }

    private static async Task AssertTwoSuccessRecoveryAsync(IDatabase database)
    {
        BreakerKeys keys = NewKeys();
        await SeedHalfOpenAsync(database, keys, openCount: 1).ConfigureAwait(true);
        Assert.Equal(
            1,
            (await AcquireAsync(database, keys, FirstOwner).ConfigureAwait(true))[0]);
        long[] firstSuccess = await CompleteAsync(
            database,
            keys,
            FirstOwner,
            "success",
            "0",
            "0",
            "200").ConfigureAwait(true);
        Assert.Equal([1, 2, 1, 0, 5], firstSuccess);
        await AssertBreakerTtlAsync(database, keys.Breaker).ConfigureAwait(true);
        Assert.False(await database.KeyExistsAsync(keys.Probe).ConfigureAwait(true));
        Assert.Equal(
            "0",
            (await database.HashGetAsync(
                keys.Breaker,
                "open_until_ms").ConfigureAwait(true)).ToString());

        Assert.Equal(
            1,
            (await AcquireAsync(database, keys, SecondOwner).ConfigureAwait(true))[0]);
        long[] secondSuccess = await CompleteAsync(
            database,
            keys,
            SecondOwner,
            "success",
            "0",
            "0",
            "204").ConfigureAwait(true);
        Assert.Equal([1, 0, 0, 0, 1], secondSuccess);
        Assert.False(await database.KeyExistsAsync(keys.Breaker).ConfigureAwait(true));
        Assert.False(await database.KeyExistsAsync(keys.Cooldown).ConfigureAwait(true));
        Assert.False(await database.KeyExistsAsync(keys.Probe).ConfigureAwait(true));
    }

    private static async Task AssertStaleAndReopenFenceAsync(IDatabase database)
    {
        BreakerKeys keys = NewKeys();
        await SeedHalfOpenAsync(database, keys, openCount: 1).ConfigureAwait(true);
        Assert.Equal(
            1,
            (await AcquireAsync(database, keys, FirstOwner).ConfigureAwait(true))[0]);
        await AssertResultAsync(
            [0, 0, 0, 0, 0],
            CompleteAsync(
                database,
                keys,
                SecondOwner,
                "success",
                "0",
                "0",
                "200")).ConfigureAwait(true);
        Assert.Equal(
            FirstOwner,
            (await database.StringGetAsync(keys.Probe).ConfigureAwait(true)).ToString());

        long[] reopened = await RecordAsync(
            database,
            keys,
            "rate_limited",
            "2000",
            "0",
            "429",
            "passive").ConfigureAwait(true);
        Assert.Equal(1, reopened[0]);
        Assert.Equal(3, reopened[5]);
        await AssertResultAsync(
            [0, 0, 0, 0, 0],
            CompleteAsync(
                database,
                keys,
                FirstOwner,
                "success",
                "0",
                "0",
                "200")).ConfigureAwait(true);
        Assert.Equal(
            FirstOwner,
            (await database.StringGetAsync(keys.Probe).ConfigureAwait(true)).ToString());
        Assert.Equal(
            reopened[4].ToString(CultureInfo.InvariantCulture),
            (await database.HashGetAsync(
                keys.Breaker,
                "open_until_ms").ConfigureAwait(true)).ToString());
    }

    private static async Task AssertFailureReopenAsync(IDatabase database)
    {
        BreakerKeys keys = NewKeys();
        await SeedHalfOpenAsync(database, keys, openCount: 1).ConfigureAwait(true);
        Assert.Equal(
            1,
            (await AcquireAsync(database, keys, FirstOwner).ConfigureAwait(true))[0]);
        long[] failedProbe = await CompleteAsync(
            database,
            keys,
            FirstOwner,
            "transient_failure",
            "0",
            "1000",
            "500").ConfigureAwait(true);
        Assert.Equal(1, failedProbe[0]);
        Assert.Equal(1, failedProbe[1]);
        Assert.Equal(0, failedProbe[2]);
        Assert.True(failedProbe[3] > 0);
        Assert.Equal(3, failedProbe[4]);
        await AssertBreakerTtlAsync(database, keys.Breaker).ConfigureAwait(true);
        Assert.Equal(
            "2",
            (await database.HashGetAsync(
                keys.Breaker,
                "open_count").ConfigureAwait(true)).ToString());
        Assert.Equal(
            "0",
            (await database.HashGetAsync(
                keys.Breaker,
                "samples").ConfigureAwait(true)).ToString());
        Assert.Equal(
            "1",
            (await database.HashGetAsync(
                keys.Breaker,
                "consecutive_failures").ConfigureAwait(true)).ToString());
        Assert.Equal(
            string.Concat(
                """{"reason":"transient_failure","retry_at":""",
                failedProbe[3].ToString(CultureInfo.InvariantCulture),
                ""","source_status":500}"""),
            (await database.StringGetAsync(keys.Cooldown)
                .ConfigureAwait(true)).ToString());
        Assert.False(await database.KeyExistsAsync(keys.Probe).ConfigureAwait(true));
    }

    private static async Task AssertResultAsync(
        long[] expected,
        Task<long[]> actualTask)
    {
        long[] actual = await actualTask.ConfigureAwait(true);
        Assert.Equal(expected, actual);
    }

    private static Task<long[]> RecordAsync(
        IDatabase database,
        BreakerKeys keys,
        string outcome,
        string retryAfterMilliseconds,
        string jitterBasisPoints,
        string sourceStatus,
        string observationMode) =>
        EvaluateAsync(
            database,
            RecordScript,
            keys.All,
            outcome,
            retryAfterMilliseconds,
            jitterBasisPoints,
            sourceStatus,
            observationMode,
            "1");

    private static Task<long[]> AcquireAsync(
        IDatabase database,
        BreakerKeys keys,
        string owner) =>
        EvaluateAsync(
            database,
            AcquireScript,
            keys.All,
            owner,
            "10000",
            "1");

    private static Task<long[]> CompleteAsync(
        IDatabase database,
        BreakerKeys keys,
        string owner,
        string outcome,
        string retryAfterMilliseconds,
        string jitterBasisPoints,
        string sourceStatus) =>
        EvaluateAsync(
            database,
            CompleteScript,
            keys.All,
            owner,
            outcome,
            retryAfterMilliseconds,
            jitterBasisPoints,
            sourceStatus,
            "1");

    private static async Task<ProbeWinner> AcquireOnlyOneAsync(
        BreakerKeys keys,
        ProbeContender[] contenders)
    {
        long[][] results = await Task.WhenAll(
            contenders.Select(contender =>
                AcquireAsync(
                    contender.Database,
                    keys,
                    contender.Owner))).ConfigureAwait(true);
        int[] winnerIndexes = results
            .Select((result, index) => (result, index))
            .Where(static item => item.result[0] == 1)
            .Select(static item => item.index)
            .ToArray();
        Assert.Single(winnerIndexes);
        for (int index = 0; index < results.Length; index++)
        {
            if (index == winnerIndexes[0])
            {
                Assert.True(results[index][1] > 0);
                continue;
            }

            Assert.Equal(0, results[index][0]);
            Assert.True(results[index][1] > 0);
        }

        ProbeContender winner = contenders[winnerIndexes[0]];
        return new(winner.Database, winner.Owner);
    }

    private static async Task AssertAccountLeaseAsync(
        ProbeWinner winner,
        BreakerKeys keys)
    {
        long[] acquired = await EvaluateAsync(
            winner.Database,
            LeaseAcquireScript,
            [AccountLeaseKey(keys)],
            winner.Owner,
            "1",
            "60000",
            "120000").ConfigureAwait(true);
        Assert.Equal(1, acquired[0]);
        Assert.Equal(1, acquired[1]);
        Assert.True(acquired[2] > 0);
        Assert.Equal(0, acquired[3]);
    }

    private static async Task ReleaseAccountLeaseAsync(
        ProbeWinner winner,
        BreakerKeys keys)
    {
        long[] released = await EvaluateAsync(
            winner.Database,
            LeaseReleaseScript,
            [AccountLeaseKey(keys)],
            winner.Owner).ConfigureAwait(true);
        Assert.Equal(new long[] { 1 }, released);
    }

    private static RedisKey AccountLeaseKey(BreakerKeys keys) =>
        keys.Breaker.ToString().Replace(
            "breaker:account:v1:",
            "lease:account:v1:",
            StringComparison.Ordinal);

    private static async Task<long[]> EvaluateAsync(
        IDatabase database,
        string scriptName,
        RedisKey[] keys,
        params string[] arguments)
    {
        string script = File.ReadAllText(Path.Combine(
            MigrationCatalogTests.FindRepositoryRoot(),
            "docs",
            "runtime",
            "scripts",
            scriptName));
        RedisValue[] values = arguments
            .Select(static argument => (RedisValue)argument)
            .ToArray();
        RedisResult result = await database
            .ScriptEvaluateAsync(script, keys, values)
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        RedisResult[] parts = (RedisResult[]?)result
            ?? throw new InvalidOperationException(
                $"{scriptName} returned a non-array result.");
        return parts.Select(static part => (long)part).ToArray();
    }

    private static RedisResult IntegerArray(params long[] values) =>
        RedisResult.Create(
            values
                .Select(static value => RedisResult.Create(
                    (RedisValue)value,
                    ResultType.Integer))
                .ToArray(),
            ResultType.Array);

    private static RedisResult BulkStringArray(params string[] values) =>
        RedisResult.Create(
            values
                .Select(static value => RedisResult.Create(
                    (RedisValue)value,
                    ResultType.BulkString))
                .ToArray(),
            ResultType.Array);

    private static async Task AssertBreakerTtlAsync(
        IDatabase database,
        RedisKey breaker)
    {
        TimeSpan ttl = Assert.IsType<TimeSpan>(
            await database.KeyTimeToLiveAsync(breaker).ConfigureAwait(true));
        Assert.InRange(
            ttl,
            TimeSpan.FromHours(47.99),
            TimeSpan.FromHours(48));
    }

    private static async Task SeedHalfOpenAsync(
        IDatabase database,
        BreakerKeys keys,
        int openCount,
        int halfOpenSuccesses = 0)
    {
        await SeedAsync(
            database,
            keys,
            samples: 0,
            failures: 0,
            consecutiveFailures: 0,
            openUntilMilliseconds: 0,
            openCount,
            halfOpenSuccesses,
            authBlocked: 0).ConfigureAwait(true);
    }

    private static async Task SeedAsync(
        IDatabase database,
        BreakerKeys keys,
        int samples,
        int failures,
        int consecutiveFailures,
        long openUntilMilliseconds,
        int openCount,
        int halfOpenSuccesses,
        int authBlocked)
    {
        await database.HashSetAsync(
            keys.Breaker,
            [
                new HashEntry("window_started_at_ms", "0"),
                new HashEntry(
                    "samples",
                    samples.ToString(CultureInfo.InvariantCulture)),
                new HashEntry(
                    "failures",
                    failures.ToString(CultureInfo.InvariantCulture)),
                new HashEntry(
                    "consecutive_failures",
                    consecutiveFailures.ToString(CultureInfo.InvariantCulture)),
                new HashEntry(
                    "open_until_ms",
                    openUntilMilliseconds.ToString(CultureInfo.InvariantCulture)),
                new HashEntry(
                    "open_count",
                    openCount.ToString(CultureInfo.InvariantCulture)),
                new HashEntry(
                    "half_open_successes",
                    halfOpenSuccesses.ToString(CultureInfo.InvariantCulture)),
                new HashEntry(
                    "auth_blocked",
                    authBlocked.ToString(CultureInfo.InvariantCulture)),
            ]).ConfigureAwait(true);
        Assert.True(await database
            .KeyExpireAsync(keys.Breaker, TimeSpan.FromDays(1))
            .ConfigureAwait(true));
    }

    private static BreakerKeys NewKeys()
    {
        string unique = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string prefix = $"poolai:r1:breaker-abi:{unique}:";
        string tag = $"{{{unique}}}";
        return new BreakerKeys(
            $"{prefix}breaker:account:v1:{tag}",
            $"{prefix}cooldown:account:v1:{tag}",
            $"{prefix}breaker-probe:account:v1:{tag}");
    }

    private readonly record struct BreakerKeys(
        RedisKey Breaker,
        RedisKey Cooldown,
        RedisKey Probe)
    {
        public RedisKey[] All => [Breaker, Cooldown, Probe];
    }

    private readonly record struct ProbeContender(
        IDatabase Database,
        string Owner);

    private readonly record struct ProbeWinner(
        IDatabase Database,
        string Owner);
}
