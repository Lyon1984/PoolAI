using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Infrastructure.Persistence;
using StackExchange.Redis;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class AccountLeaseRedisRuntimeTests(PostgresRuntimeFixture fixture)
{
    private const string KeyPrefix = "poolai:r1:integration:";
    private const string AffinityHash = "0123456789abcdef0123456789abcdef";
    private const string RoutingSupplySql = """
        WITH inserted_group AS (
            INSERT INTO public.groups (id, name, status)
            VALUES ($1, $2, 'disabled')
            RETURNING id
        ),
        inserted_channel AS (
            INSERT INTO public.channels (
                id, provider, name, model_rules, capabilities, status
            ) VALUES (
                $3, 'openai', $4, '{"gpt-test":"gpt-test"}'::jsonb,
                '{"responses":true,"chat_completions":true,"function_tools":true,"streaming":true}'::jsonb,
                'active'
            )
            RETURNING id
        ),
        inserted_accounts AS (
            INSERT INTO public.accounts (
                id, provider, name, auth_type, upstream_base_url,
                credential_envelope, credential_prefix, status,
                priority, weight, max_concurrency,
                last_health_at, last_health_status
            ) VALUES
            (
                $5, 'openai', $6, 'api_key', 'https://example.test/v1',
                '{}'::jsonb, 'sk-route-a', 'active',
                10, 100, 2, clock_timestamp(), 'healthy'
            ),
            (
                $7, 'openai', $8, 'api_key', 'https://example.test/v1',
                '{}'::jsonb, 'sk-route-b', 'active',
                0, 100, 2, clock_timestamp(), 'healthy'
            )
            RETURNING id
        ),
        inserted_configuration AS (
            INSERT INTO public.group_supply_configurations (group_id, channel_id)
            SELECT inserted_group.id, inserted_channel.id
            FROM inserted_group
            CROSS JOIN inserted_channel
            RETURNING group_id
        )
        INSERT INTO public.group_accounts (
            group_id, account_id, priority_override, is_enabled
        )
        SELECT
            inserted_configuration.group_id,
            inserted_accounts.id,
            CASE WHEN inserted_accounts.id = $5 THEN 100 ELSE 0 END,
            true
        FROM inserted_configuration
        CROSS JOIN inserted_accounts;
        """;
    private readonly PostgresRuntimeFixture _fixture =
        fixture ?? throw new ArgumentNullException(nameof(fixture));

    [Fact]
    [Trait("Category", "Redis")]
    public async Task TwoApiInstancesShareAccountCapacityWithoutOverAcquire()
    {
        EntityId groupId = EntityId.New();
        AccountCandidate candidate = Candidate(groupId, concurrencyLimit: 10);
        ServiceProvider first = BuildServices(
            _fixture.RedisConnectionString,
            [candidate]);
        await using ConfiguredAsyncDisposable firstLease = first.ConfigureAwait(true);
        ServiceProvider second = BuildServices(
            _fixture.RedisConnectionString,
            [candidate]);
        await using ConfiguredAsyncDisposable secondLease = second.ConfigureAwait(true);
        IAccountRouter firstRouter = first.GetRequiredService<IAccountRouter>();
        IAccountRouter secondRouter = second.GetRequiredService<IAccountRouter>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Task<Result<IAccountLease>>[] tasks = Enumerable
            .Range(0, 100)
            .Select(index => (index % 2 == 0 ? firstRouter : secondRouter)
                .RouteAsync(
                    Command(groupId, index),
                    cancellationToken)
                .AsTask())
            .ToArray();
        Result<IAccountLease>[] results = await Task.WhenAll(tasks).ConfigureAwait(true);
        IAccountLease[] acquired = results
            .Where(static result => result.IsSuccess)
            .Select(static result => result.Value)
            .ToArray();

        Assert.Equal(10, acquired.Length);
        Assert.All(
            results.Where(static result => result.IsFailure),
            result => Assert.Equal("account_capacity_unavailable", result.Error.Code));
        ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable redisLease = redis.ConfigureAwait(true);
        long active = await redis
            .GetDatabase()
            .SortedSetLengthAsync(FullLeaseKey(candidate.AccountId))
            .ConfigureAwait(true);
        Assert.Equal(10, active);

        foreach (IAccountLease lease in acquired)
        {
            Result<bool> released = await lease
                .ReleaseAsync(cancellationToken)
                .ConfigureAwait(true);
            Assert.True(released.IsSuccess);
        }
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task AccountLeaseOwnerIsIdempotentRenewableAndReleaseIsOwnerSafe()
    {
        EntityId accountId = EntityId.New();
        string key = $"lease:account:v1:{{{accountId.Value:D}}}";
        string owner = "11111111111111111111111111111111";
        string otherOwner = "22222222222222222222222222222222";
        ServiceProvider first = BuildServices(
            _fixture.RedisConnectionString,
            []);
        await using ConfiguredAsyncDisposable firstLease = first.ConfigureAwait(true);
        ServiceProvider second = BuildServices(
            _fixture.RedisConnectionString,
            []);
        await using ConfiguredAsyncDisposable secondLease = second.ConfigureAwait(true);
        ICoordinationLeaseSet firstStore =
            first.GetRequiredService<ICoordinationLeaseSet>();
        ICoordinationLeaseSet secondStore =
            second.GetRequiredService<ICoordinationLeaseSet>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        CoordinationLeaseAcquireResult acquired = await firstStore.AcquireAsync(
            new CoordinationLeaseAcquireRequest(key, owner, Limit: 1),
            cancellationToken).ConfigureAwait(true);
        CoordinationLeaseAcquireResult idempotent = await secondStore.AcquireAsync(
            new CoordinationLeaseAcquireRequest(key, owner, Limit: 1),
            cancellationToken).ConfigureAwait(true);
        CoordinationLeaseRenewResult renewed = await firstStore.RenewAsync(
            new CoordinationLeaseOwner(key, owner),
            cancellationToken).ConfigureAwait(true);
        CoordinationLeaseReleaseResult nonOwner = await secondStore.ReleaseAsync(
            new CoordinationLeaseOwner(key, otherOwner),
            cancellationToken).ConfigureAwait(true);
        CoordinationLeaseReleaseResult released = await firstStore.ReleaseAsync(
            new CoordinationLeaseOwner(key, owner),
            cancellationToken).ConfigureAwait(true);
        CoordinationLeaseReleaseResult duplicate = await firstStore.ReleaseAsync(
            new CoordinationLeaseOwner(key, owner),
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(CoordinationLeaseAcquireDisposition.Acquired, acquired.Disposition);
        Assert.Equal(CoordinationLeaseAcquireDisposition.Renewed, idempotent.Disposition);
        Assert.Equal(1, idempotent.ActiveCount);
        Assert.Equal(CoordinationLeaseRenewDisposition.Renewed, renewed.Disposition);
        Assert.True(renewed.ExpiresAt >= acquired.ExpiresAt);
        Assert.Equal(CoordinationLeaseReleaseResult.NotOwned, nonOwner);
        Assert.Equal(CoordinationLeaseReleaseResult.Released, released);
        Assert.Equal(CoordinationLeaseReleaseResult.NotOwned, duplicate);
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task CallerCancellationPropagatesAcrossAccountLeaseOperations()
    {
        EntityId accountId = EntityId.New();
        string leaseKey = $"lease:account:v1:{{{accountId.Value:D}}}";
        const string owner = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        ServiceProvider services = BuildServices(
            _fixture.RedisConnectionString,
            []);
        await using ConfiguredAsyncDisposable serviceLease =
            services.ConfigureAwait(true);
        ICoordinationLeaseSet leaseSet =
            services.GetRequiredService<ICoordinationLeaseSet>();
        CoordinationLeaseAcquireResult primed = await leaseSet.AcquireAsync(
            new CoordinationLeaseAcquireRequest(leaseKey, owner, Limit: 1),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(CoordinationLeaseAcquireDisposition.Acquired, primed.Disposition);
        using CancellationTokenSource callerCancellation = new();
        callerCancellation.Cancel();

        OperationCanceledException acquireException =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                leaseSet.AcquireAsync(
                        new CoordinationLeaseAcquireRequest(
                            leaseKey,
                            owner,
                            Limit: 1),
                        callerCancellation.Token)
                    .AsTask());
        OperationCanceledException renewException =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                leaseSet.RenewAsync(
                        new CoordinationLeaseOwner(leaseKey, owner),
                        callerCancellation.Token)
                    .AsTask());
        OperationCanceledException releaseException =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                leaseSet.ReleaseAsync(
                        new CoordinationLeaseOwner(leaseKey, owner),
                        callerCancellation.Token)
                    .AsTask());

        Assert.All(
            [acquireException, renewException, releaseException],
            exception => Assert.Equal(
                callerCancellation.Token,
                exception.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task CallerCancellationPropagatesAcrossStickyValueOperations()
    {
        EntityId groupId = EntityId.New();
        string stickyKey =
            $"sticky:v1:{{{groupId.Value:D}}}:{{{AffinityHash}}}";
        ServiceProvider services = BuildServices(
            _fixture.RedisConnectionString,
            []);
        await using ConfiguredAsyncDisposable serviceLease =
            services.ConfigureAwait(true);
        ICoordinationValueStore valueStore =
            services.GetRequiredService<ICoordinationValueStore>();
        using CancellationTokenSource callerCancellation = new();
        callerCancellation.Cancel();

        OperationCanceledException getException =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                valueStore.GetAndRefreshAsync(
                        stickyKey,
                        TimeSpan.FromMinutes(60),
                        callerCancellation.Token)
                    .AsTask());
        OperationCanceledException setException =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                valueStore.SetAsync(
                        stickyKey,
                        """{"account_id":"canceled"}""",
                        TimeSpan.FromMinutes(60),
                        callerCancellation.Token)
                    .AsTask());

        Assert.Equal(callerCancellation.Token, getException.CancellationToken);
        Assert.Equal(callerCancellation.Token, setException.CancellationToken);
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task AccountLeaseRejectsOutOfContractInputsBeforeDispatch()
    {
        EntityId accountId = EntityId.New();
        string leaseKey = $"lease:account:v1:{{{accountId.Value:D}}}";
        const string owner = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        ServiceProvider services = BuildServices(
            _fixture.RedisConnectionString,
            []);
        await using ConfiguredAsyncDisposable serviceLease =
            services.ConfigureAwait(true);
        ICoordinationLeaseSet leaseSet =
            services.GetRequiredService<ICoordinationLeaseSet>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            leaseSet.AcquireAsync(
                    new CoordinationLeaseAcquireRequest(
                        "lease:account:v1:{not-a-guid}",
                        owner,
                        Limit: 1),
                    cancellationToken)
                .AsTask());
        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            leaseSet.RenewAsync(
                    new CoordinationLeaseOwner(
                        leaseKey,
                        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"),
                    cancellationToken)
                .AsTask());
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            leaseSet.AcquireAsync(
                    new CoordinationLeaseAcquireRequest(
                        leaseKey,
                        owner,
                        Limit: 0),
                    cancellationToken)
                .AsTask());
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task StickyValueStoreRejectsMalformedKeyTtlAndOversizeValue()
    {
        EntityId groupId = EntityId.New();
        string stickyKey =
            $"sticky:v1:{{{groupId.Value:D}}}:{{{AffinityHash}}}";
        ServiceProvider services = BuildServices(
            _fixture.RedisConnectionString,
            []);
        await using ConfiguredAsyncDisposable serviceLease =
            services.ConfigureAwait(true);
        ICoordinationValueStore valueStore =
            services.GetRequiredService<ICoordinationValueStore>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            valueStore.GetAndRefreshAsync(
                    $"sticky:v1:{{{groupId.Value:D}}}:{{NOT-A-DIGEST}}",
                    TimeSpan.FromMinutes(60),
                    cancellationToken)
                .AsTask());
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            valueStore.GetAndRefreshAsync(
                    stickyKey,
                    TimeSpan.FromMinutes(59),
                    cancellationToken)
                .AsTask());
        ArgumentException oversized =
            await Assert.ThrowsAsync<ArgumentException>(() =>
                valueStore.SetAsync(
                        stickyKey,
                        new string('x', 1_025),
                        TimeSpan.FromMinutes(60),
                        cancellationToken)
                    .AsTask());

        Assert.Equal("value", oversized.ParamName);
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task ExpiredMembersAreRemovedBeforeCapacityIsGranted()
    {
        EntityId accountId = EntityId.New();
        string key = $"lease:account:v1:{{{accountId.Value:D}}}";
        ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync($"{_fixture.RedisConnectionString},allowAdmin=true")
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable redisLease = redis.ConfigureAwait(true);
        IDatabase database = redis.GetDatabase();
        await database.SortedSetAddAsync(
            FullLeaseKey(accountId),
            "33333333333333333333333333333333",
            score: 0).ConfigureAwait(true);
        await database.KeyExpireAsync(
            FullLeaseKey(accountId),
            TimeSpan.FromMinutes(2)).ConfigureAwait(true);
        ServiceProvider services = BuildServices(
            _fixture.RedisConnectionString,
            []);
        await using ConfiguredAsyncDisposable serviceLease = services.ConfigureAwait(true);
        ICoordinationLeaseSet store =
            services.GetRequiredService<ICoordinationLeaseSet>();

        CoordinationLeaseAcquireResult result = await store.AcquireAsync(
            new CoordinationLeaseAcquireRequest(
                key,
                "44444444444444444444444444444444",
                Limit: 1),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(CoordinationLeaseAcquireDisposition.Acquired, result.Disposition);
        RedisValue[] owners = await database
            .SortedSetRangeByRankAsync(FullLeaseKey(accountId))
            .ConfigureAwait(true);
        Assert.Single(owners);
        Assert.Equal("44444444444444444444444444444444", owners[0].ToString());
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task ScriptFlushTriggersManifestBoundReload()
    {
        EntityId accountId = EntityId.New();
        string key = $"lease:account:v1:{{{accountId.Value:D}}}";
        string owner = "55555555555555555555555555555555";
        ServiceProvider services = BuildServices(
            _fixture.RedisConnectionString,
            []);
        await using ConfiguredAsyncDisposable serviceLease = services.ConfigureAwait(true);
        ICoordinationLeaseSet store =
            services.GetRequiredService<ICoordinationLeaseSet>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CoordinationLeaseAcquireResult acquired = await store.AcquireAsync(
            new CoordinationLeaseAcquireRequest(key, owner, Limit: 1),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(CoordinationLeaseAcquireDisposition.Acquired, acquired.Disposition);

        ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync($"{_fixture.RedisConnectionString},allowAdmin=true")
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable redisLease = redis.ConfigureAwait(true);
        IServer server = redis.GetServer(redis.GetEndPoints().Single());
        await server.ScriptFlushAsync().ConfigureAwait(true);

        CoordinationLeaseRenewResult renewed = await store.RenewAsync(
            new CoordinationLeaseOwner(key, owner),
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(CoordinationLeaseRenewDisposition.Renewed, renewed.Disposition);
        Assert.True(await server.ScriptExistsAsync(
            Convert.FromHexString("e25462f689f5b9ad2ca119a5571c5d3891654474"))
            .ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task StickyAffinityUsesVersionedPayloadAndRenewsOneHourTtl()
    {
        EntityId groupId = EntityId.New();
        AccountCandidate candidate = Candidate(groupId, concurrencyLimit: 2);
        ServiceProvider services = BuildServices(
            _fixture.RedisConnectionString,
            [candidate]);
        await using ConfiguredAsyncDisposable serviceLease = services.ConfigureAwait(true);
        IAccountRouter router = services.GetRequiredService<IAccountRouter>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<IAccountLease> result = await router.RouteAsync(
            Command(groupId, index: 1) with { SessionAffinityHash = AffinityHash },
            cancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        IAccountLease lease = result.Value;
        await using ConfiguredAsyncDisposable accountLease = lease.ConfigureAwait(true);
        ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable redisLease = redis.ConfigureAwait(true);
        RedisKey key =
            $"{KeyPrefix}sticky:v1:{{{groupId.Value:D}}}:{{{AffinityHash}}}";
        RedisValue payload = await redis.GetDatabase().StringGetAsync(key)
            .ConfigureAwait(true);
        TimeSpan? timeToLive = await redis.GetDatabase().KeyTimeToLiveAsync(key)
            .ConfigureAwait(true);

        Assert.Contains(
            $"\"account_id\":\"{candidate.AccountId.Value:D}\"",
            payload.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"group_policy_version\":7",
            payload.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"supply_configuration_version\":11",
            payload.ToString(),
            StringComparison.Ordinal);
        Assert.NotNull(timeToLive);
        Assert.InRange(
            timeToLive.Value,
            TimeSpan.FromMinutes(59),
            TimeSpan.FromMinutes(60));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task CanonicalSupplyReadInvalidatesStickyAfterBindingChange()
    {
        RoutingSupplyScenario scenario = await SeedRoutingSupplyAsync(
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        NpgsqlDataSource apiDataSource = _fixture.ApiServices
            .GetRequiredService<NpgsqlDataSource>();
        ServiceProvider services = BuildServices(
            _fixture.RedisConnectionString,
            new PostgresAccountCandidateReader(apiDataSource));
        await using ConfiguredAsyncDisposable serviceLease = services.ConfigureAwait(true);
        IAccountRouter router = services.GetRequiredService<IAccountRouter>();
        RouteAccountCommand command = Command(scenario.GroupId, index: 8) with
        {
            SessionAffinityHash = AffinityHash,
        };
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<IAccountLease> first = await router
            .RouteAsync(command, cancellationToken)
            .ConfigureAwait(true);
        Assert.True(first.IsSuccess);
        Assert.Equal(scenario.PreferredAccountId, first.Value.Route.AccountId);
        _ = await first.Value.ReleaseAsync(cancellationToken).ConfigureAwait(true);

        using (NpgsqlCommand disable = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.group_accounts
            SET is_enabled = false,
                updated_at = clock_timestamp()
            WHERE group_id = $1
              AND account_id = $2;
            """))
        {
            disable.Parameters.AddWithValue(scenario.GroupId.Value);
            disable.Parameters.AddWithValue(scenario.PreferredAccountId.Value);
            Assert.Equal(
                1,
                await disable.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(true));
        }

        Result<IAccountLease> rerouted = await router
            .RouteAsync(command with { AttemptId = EntityId.New() }, cancellationToken)
            .ConfigureAwait(true);

        Assert.True(rerouted.IsSuccess);
        Assert.Equal(scenario.FallbackAccountId, rerouted.Value.Route.AccountId);
        _ = await rerouted.Value.ReleaseAsync(cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task AccountLeaseAndGroupRpmUseRedisTime()
    {
        EntityId groupId = EntityId.New();
        AccountCandidate candidate = Candidate(groupId, concurrencyLimit: 1);
        ServiceProvider services = BuildServices(
            _fixture.RedisConnectionString,
            [candidate]);
        await using ConfiguredAsyncDisposable serviceLease = services.ConfigureAwait(true);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<IAccountLease> route = await services
            .GetRequiredService<IAccountRouter>()
            .RouteAsync(Command(groupId, index: 2), cancellationToken)
            .ConfigureAwait(true);
        Assert.True(route.IsSuccess);
        IAccountLease lease = route.Value;
        await using ConfiguredAsyncDisposable accountLease = lease.ConfigureAwait(true);
        Result<GroupRequestRateLimitPermit> first = await services
            .GetRequiredService<IGroupRequestRateLimiter>()
            .AcquireAsync(groupId, requestsPerMinute: 1, cancellationToken)
            .ConfigureAwait(true);
        Result<GroupRequestRateLimitPermit> rejected = await services
            .GetRequiredService<IGroupRequestRateLimiter>()
            .AcquireAsync(groupId, requestsPerMinute: 1, cancellationToken)
            .ConfigureAwait(true);

        ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable redisLease = redis.ConfigureAwait(true);
        long redisMilliseconds = await RedisMillisecondsAsync(
            redis.GetDatabase(),
            cancellationToken).ConfigureAwait(true);
        long redisMinute = redisMilliseconds / 60_000;
        RedisValue count = await redis.GetDatabase().StringGetAsync(
            $"{KeyPrefix}rate:group:v1:{{{groupId.Value:D}}}:{redisMinute}")
            .ConfigureAwait(true);

        Assert.InRange(
            lease.Route.LeaseExpiresAt.ToUnixTimeMilliseconds() - redisMilliseconds,
            55_000,
            60_000);
        Assert.True(first.IsSuccess);
        Assert.Equal("group_rate_limited", rejected.Error.Code);
        Assert.Equal("2", count.ToString());
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task RoutingNeverSelectsAnAccountFromAnotherGroup()
    {
        EntityId requestedGroup = EntityId.New();
        AccountCandidate foreign = Candidate(EntityId.New(), concurrencyLimit: 1);
        ServiceProvider services = BuildServices(
            _fixture.RedisConnectionString,
            [foreign]);
        await using ConfiguredAsyncDisposable serviceLease = services.ConfigureAwait(true);

        Result<IAccountLease> result = await services
            .GetRequiredService<IAccountRouter>()
            .RouteAsync(
                Command(requestedGroup, index: 3),
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable redisLease = redis.ConfigureAwait(true);
        Assert.False(await redis
            .GetDatabase()
            .KeyExistsAsync(FullLeaseKey(foreign.AccountId))
            .ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task OnlyAccountLeaseAndGroupRpmExistAndRedisFailureFailsClosed()
    {
        EntityId groupId = EntityId.New();
        AccountCandidate candidate = Candidate(groupId, concurrencyLimit: 1);
        const string unavailableRedis =
            "127.0.0.1:1,connectRetry=0,connectTimeout=100,asyncTimeout=100,syncTimeout=100";
        ServiceProvider services = BuildServices(
            unavailableRedis,
            [candidate],
            timeoutSeconds: 1);
        await using ConfiguredAsyncDisposable serviceLease = services.ConfigureAwait(true);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Result<IAccountLease> route = await services
            .GetRequiredService<IAccountRouter>()
            .RouteAsync(Command(groupId, index: 4), cancellationToken)
            .ConfigureAwait(true);
        Result<GroupRequestRateLimitPermit> rpm = await services
            .GetRequiredService<IGroupRequestRateLimiter>()
            .AcquireAsync(groupId, requestsPerMinute: 1, cancellationToken)
            .ConfigureAwait(true);

        Assert.Equal("coordination_unavailable", route.Error.Code);
        Assert.Equal(1, route.Error.RetryAfterSeconds);
        Assert.Equal("coordination_unavailable", rpm.Error.Code);
        Assert.Equal(1, rpm.Error.RetryAfterSeconds);
    }

    private static ServiceProvider BuildServices(
        string redisConnectionString,
        IReadOnlyList<AccountCandidate> candidates,
        int timeoutSeconds = 3) =>
        BuildServices(
            redisConnectionString,
            new FixedCandidateReader(candidates),
            timeoutSeconds);

    private static ServiceProvider BuildServices(
        string redisConnectionString,
        IAccountCandidateReader candidateReader,
        int timeoutSeconds = 3)
    {
        ConfigurationManager configuration = new();
        configuration["Data:Redis:ConnectionString"] = redisConnectionString;
        configuration["Data:Redis:KeyPrefix"] = KeyPrefix;
        configuration["Health:ReadinessTimeoutSeconds"] =
            timeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOperationsModule(configuration, "Integration");
        services.AddSingleton(candidateReader);
        services.AddSingleton<IAccountHealthWriter,
            FailClosedAccountHealthWriter>();
        services.AddRoutingModule();
        return services.BuildServiceProvider();
    }

    private async ValueTask<RoutingSupplyScenario> SeedRoutingSupplyAsync(
        CancellationToken cancellationToken)
    {
        EntityId groupId = EntityId.New();
        EntityId channelId = EntityId.New();
        EntityId preferredAccountId = EntityId.New();
        EntityId fallbackAccountId = EntityId.New();
        using NpgsqlCommand command =
            _fixture.AdministratorDataSource.CreateCommand(RoutingSupplySql);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue($"M2-E3 routing {groupId.Value:N}");
        command.Parameters.AddWithValue(channelId.Value);
        command.Parameters.AddWithValue($"M2-E3 channel {channelId.Value:N}");
        command.Parameters.AddWithValue(preferredAccountId.Value);
        command.Parameters.AddWithValue($"M2-E3 preferred {preferredAccountId.Value:N}");
        command.Parameters.AddWithValue(fallbackAccountId.Value);
        command.Parameters.AddWithValue($"M2-E3 fallback {fallbackAccountId.Value:N}");
        Assert.Equal(
            2,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        return new RoutingSupplyScenario(
            groupId,
            preferredAccountId,
            fallbackAccountId);
    }

    private static AccountCandidate Candidate(
        EntityId groupId,
        int concurrencyLimit) =>
        new(
            groupId,
            EntityId.New(),
            EntityId.New(),
            UpstreamProvider.OpenAi,
            "gpt-test",
            "gpt-test",
            "https://example.invalid/v1",
            new ChannelCapabilitiesSnapshot(
                Responses: true,
                ChatCompletions: true,
                FunctionTools: true,
                Streaming: true),
            AccountHealth.Healthy,
            concurrencyLimit,
            Priority: 10,
            Weight: 100,
            ConfigurationVersion: 11,
            ChannelVersion: 13,
            AccountVersion: 17);

    private static RouteAccountCommand Command(EntityId groupId, int index) =>
        new(
            groupId,
            "gpt-test",
            new EntityId(Guid.CreateVersion7()),
            new EntityId(Guid.CreateVersion7()),
            GroupPolicyVersion: 7);

    private static RedisKey FullLeaseKey(EntityId accountId) =>
        $"{KeyPrefix}lease:account:v1:{{{accountId.Value:D}}}";

    private static async ValueTask<long> RedisMillisecondsAsync(
        IDatabase database,
        CancellationToken cancellationToken)
    {
        RedisResult result = await database
            .ExecuteAsync("TIME")
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        RedisResult[]? parts = (RedisResult[]?)result;
        Assert.NotNull(parts);
        Assert.Equal(2, parts.Length);
        long seconds = long.Parse(parts[0].ToString(), CultureInfo.InvariantCulture);
        long microseconds = long.Parse(parts[1].ToString(), CultureInfo.InvariantCulture);
        return checked(seconds * 1_000 + microseconds / 1_000);
    }

    private sealed class FixedCandidateReader(
        IReadOnlyList<AccountCandidate> candidates) : IAccountCandidateReader
    {
        private readonly IReadOnlyList<AccountCandidate> _candidates =
            candidates ?? throw new ArgumentNullException(nameof(candidates));

        public ValueTask<Result<IReadOnlyList<AccountCandidate>>> GetCandidatesAsync(
            EntityId groupId,
            string model,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success(_candidates));
    }

    private sealed class FailClosedAccountHealthWriter : IAccountHealthWriter
    {
        public ValueTask<Result<AccountHealthTransitionResult>> RecordAsync(
            AccountHealthTransition transition,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transition);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                Result.Failure<AccountHealthTransitionResult>(
                    "dependency_unavailable",
                    "The Account lease fixture does not permit health writes.",
                    retryAfterSeconds: 1));
        }
    }

    private sealed record RoutingSupplyScenario(
        EntityId GroupId,
        EntityId PreferredAccountId,
        EntityId FallbackAccountId);
}
