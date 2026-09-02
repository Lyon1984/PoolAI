using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Gateway;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.GroupQuota;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Identity;
using PoolAI.Modules.Operations;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.SubscriptionAccess;
using PoolAI.Modules.Supply;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using StackExchange.Redis;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class M4E1GatewayPipelinePostgresRedisTests(
    PostgresRuntimeFixture fixture)
{
    private const string Model = "gpt-m4-e1";
    private const string KeyPrefix = "sk-pool-";
    private const string ProductionEnvironment = "Production";
    private const string LoopbackTestEnvironment = "Test";
    private static readonly byte[] ApiKeyPepper =
        Enumerable.Repeat((byte)0x4d, 32).ToArray();
    private static readonly byte[] EnvelopeKey =
        Enumerable.Repeat((byte)0x73, 32).ToArray();
    private readonly PostgresRuntimeFixture _fixture = fixture
        ?? throw new ArgumentNullException(nameof(fixture));

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task ProductionPortsCompleteOneAttemptAndReleaseEveryResource()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using SingleRequestHttpServer upstream = new();
        GatewayScenario scenario = GatewayScenario.Create(upstream.BaseUri);
        await using PipelineRuntime runtime = await CreateRuntimeAsync(
                scenario,
                _fixture.RedisConnectionString,
                cancellationToken)
            .ConfigureAwait(true);
        Task<string> upstreamRequest = upstream.ServeAsync(
            """
            {"id":"response-m4-e1","object":"response","usage":{"input_tokens":11,"output_tokens":7}}
            """,
            cancellationToken);
        GatewayRequestProcess process = runtime.Services
            .GetRequiredService<GatewayRequestProcess>();
        GatewayAuthorizedRequest authorization = await AuthorizeAsync(
                process,
                scenario,
                cancellationToken)
            .ConfigureAwait(true);
        EntityId requestId = EntityId.New();
        Task<Result<GatewaySingleAttemptOutcome>> execution = ExecuteAttemptAsync(
            process,
            authorization,
            requestId,
            "integration",
            "m4-e1-combined-dependencies",
            cancellationToken);
        PausedAttemptEvidence paused = await CapturePausedAttemptAsync(
                upstream,
                runtime,
                scenario,
                requestId,
                execution,
                cancellationToken)
            .ConfigureAwait(true);

        Result<GatewaySingleAttemptOutcome> executed = await execution
            .ConfigureAwait(true);
        Assert.Equal(
            paused.ObservedRequest,
            await upstreamRequest.ConfigureAwait(true));
        GatewaySingleAttemptOutcome outcome = AssertSuccessfulOutcome(
            executed,
            requestId);
        GatewayDatabaseFootprint footprint = await ReadFootprintAsync(
                scenario, outcome.AttemptId, cancellationToken)
            .ConfigureAwait(true);
        AssertMidFlightEvidence(paused.Footprint, paused.ExecutionCompleted);
        AssertOutboundEvidence(
            paused.ObservedRequest,
            upstream,
            scenario,
            runtime);
        AssertTerminalDatabaseFootprint(footprint, outcome.AttemptId);
        AssertProductionPortCalls(runtime, scenario, expectedDownstreamCalls: 1);
        await AssertRedisResourcesAsync(
                runtime.RedisKeyPrefix, scenario, cancellationToken)
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task RedisAdmissionFailureFailsClosedBeforeRouteReserveOrUpstream()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using SingleRequestHttpServer upstream = new();
        GatewayScenario scenario = GatewayScenario.Create(upstream.BaseUri);
        const string unavailableRedis =
            "127.0.0.1:1,abortConnect=false,connectRetry=0,connectTimeout=100,asyncTimeout=100,syncTimeout=100";
        await using PipelineRuntime runtime = await CreateRuntimeAsync(
                scenario,
                unavailableRedis,
                cancellationToken)
            .ConfigureAwait(true);
        GatewayRequestProcess process = runtime.Services
            .GetRequiredService<GatewayRequestProcess>();
        EntityId requestId = EntityId.New();
        Result<GatewayAuthorizedRequest> authorization = await process
            .AuthorizeAsync(
                scenario.PresentedApiKey,
                IPAddress.Loopback,
                forwardedForFieldValues: null,
                cancellationToken)
            .ConfigureAwait(true);

        Assert.True(authorization.IsFailure);
        Assert.Equal("coordination_unavailable", authorization.Error.Code);
        Assert.Equal(1, authorization.Error.RetryAfterSeconds);
        AssertNoAttemptWork(runtime, upstream);
        AssertProductionPortCalls(runtime, scenario, expectedDownstreamCalls: 0);

        GatewayPreDispatchFootprint footprint = await ReadPreDispatchFootprintAsync(
                requestId,
                scenario.AccountId,
                cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(0, footprint.RequestCount);
        Assert.Equal(0, footprint.ReservationCount);
        Assert.Equal(0, footprint.AttemptCount);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task PostCommitNewAdmissionStrongReadsAndRejectsDisabledApiKey()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using SingleRequestHttpServer upstream = new();
        GatewayScenario scenario = GatewayScenario.Create(upstream.BaseUri);
        await using PipelineRuntime runtime = await CreateRuntimeAsync(
                scenario,
                _fixture.RedisConnectionString,
                cancellationToken)
            .ConfigureAwait(true);
        GatewayRequestProcess process = runtime.Services
            .GetRequiredService<GatewayRequestProcess>();

        _ = await AuthorizeAsync(process, scenario, cancellationToken)
            .ConfigureAwait(true);
        await DisableApiKeyAsync(scenario, cancellationToken)
            .ConfigureAwait(true);
        Result<GatewayAuthorizedRequest> afterCommit = await process.AuthorizeAsync(
                scenario.PresentedApiKey,
                IPAddress.Loopback,
                forwardedForFieldValues: null,
                cancellationToken)
            .ConfigureAwait(true);

        Assert.True(afterCommit.IsFailure);
        Assert.Equal("invalid_api_key", afterCommit.Error.Code);
        AssertNoAttemptWork(runtime, upstream);
        AssertProductionPortCalls(
            runtime,
            scenario,
            expectedDownstreamCalls: 0,
            expectedRpmCalls: 1);
        await AssertNoPreDispatchPersistenceAsync(
                EntityId.New(), scenario.AccountId, cancellationToken)
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task NewAttemptRereadsCurrentSupplyRouteAndCredentialRevision()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using SingleRequestHttpServer initialUpstream = new();
        await using SingleRequestHttpServer replacementUpstream = new();
        GatewayScenario scenario = GatewayScenario.Create(initialUpstream.BaseUri);
        await using PipelineRuntime runtime = await CreateRuntimeAsync(
                scenario,
                _fixture.RedisConnectionString,
                cancellationToken)
            .ConfigureAwait(true);
        GatewayRequestProcess process = runtime.Services
            .GetRequiredService<GatewayRequestProcess>();

        CompletedAttemptEvidence initial = await CompleteAttemptAsync(
                process, initialUpstream, scenario, "initial", cancellationToken)
            .ConfigureAwait(true);
        AccountRoute initialRoute = Assert.IsType<AccountRoute>(
            runtime.Router.LastRoute);
        RouteCredentialLeaseRequest initialCredentialRequest = Assert.IsType<
            RouteCredentialLeaseRequest>(runtime.CredentialSource.LastRequest);
        string replacementCredential = $"replacement-{Guid.NewGuid():N}";
        await ReplaceSupplyRouteAndCredentialAsync(
                runtime,
                scenario,
                replacementUpstream.BaseUri,
                replacementCredential,
                cancellationToken)
            .ConfigureAwait(true);
        CompletedAttemptEvidence replacement = await CompleteAttemptAsync(
                process,
                replacementUpstream,
                scenario,
                "replacement",
                cancellationToken)
            .ConfigureAwait(true);

        AssertReplacementAttemptEvidence(
            runtime,
            scenario,
            initialUpstream,
            replacementUpstream,
            initial,
            replacement,
            initialRoute,
            initialCredentialRequest,
            replacementCredential);
        await AssertRedisResourcesAsync(
                runtime.RedisKeyPrefix,
                scenario,
                cancellationToken,
                expectedRpmCount: 2)
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task PostgresAdmissionFailureFailsClosedBeforeRpmRouteReserveOrUpstream()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using SingleRequestHttpServer upstream = new();
        GatewayScenario scenario = GatewayScenario.Create(upstream.BaseUri);
        string unavailablePostgres = UnavailablePostgresConnectionString();
        await using PipelineRuntime runtime = await CreateRuntimeAsync(
                scenario,
                _fixture.RedisConnectionString,
                cancellationToken,
                unavailablePostgres)
            .ConfigureAwait(true);
        GatewayRequestProcess process = runtime.Services
            .GetRequiredService<GatewayRequestProcess>();

        Result<GatewayAuthorizedRequest> authorization = await process.AuthorizeAsync(
                scenario.PresentedApiKey,
                IPAddress.Loopback,
                forwardedForFieldValues: null,
                cancellationToken)
            .ConfigureAwait(true);

        Assert.True(authorization.IsFailure);
        Assert.Equal("dependency_unavailable", authorization.Error.Code);
        Assert.Equal(1, authorization.Error.RetryAfterSeconds);
        AssertNoAttemptWork(runtime, upstream);
        AssertProductionPortCalls(
            runtime,
            scenario,
            expectedDownstreamCalls: 0,
            expectedRpmCalls: 0);
        await AssertNoPreDispatchPersistenceAsync(
                EntityId.New(), scenario.AccountId, cancellationToken)
            .ConfigureAwait(true);
    }

    private static async ValueTask<GatewayAuthorizedRequest> AuthorizeAsync(
        GatewayRequestProcess process,
        GatewayScenario scenario,
        CancellationToken cancellationToken)
    {
        Result<GatewayAuthorizedRequest> authorization = await process
            .AuthorizeAsync(
                scenario.PresentedApiKey,
                IPAddress.Loopback,
                forwardedForFieldValues: null,
                cancellationToken)
            .ConfigureAwait(false);
        Assert.True(authorization.IsSuccess, authorization.Error.Description);
        return authorization.Value;
    }

    private static Task<Result<GatewaySingleAttemptOutcome>> ExecuteAttemptAsync(
        GatewayRequestProcess process,
        GatewayAuthorizedRequest authorization,
        EntityId requestId,
        string input,
        string? clientRequestId,
        CancellationToken cancellationToken)
    {
        JsonElement payload = CreatePayload(input);
        return process.ExecuteInitialAttemptAsync(
                authorization,
                InboundProtocol.Responses,
                new NormalizedGatewayRequest(
                    requestId,
                    Model,
                    Stream: false,
                    payload),
                clientRequestId,
                TimeProvider.System.GetUtcNow().AddSeconds(20),
                sessionAffinityHash: null,
                cancellationToken)
            .AsTask();
    }

    private static async ValueTask<CompletedAttemptEvidence> CompleteAttemptAsync(
        GatewayRequestProcess process,
        SingleRequestHttpServer upstream,
        GatewayScenario scenario,
        string input,
        CancellationToken cancellationToken)
    {
        Task<string> upstreamRequest = upstream.ServeAsync(
            """
            {"id":"response-m4-e1","object":"response","usage":{"input_tokens":11,"output_tokens":7}}
            """,
            cancellationToken);
        GatewayAuthorizedRequest authorization = await AuthorizeAsync(
                process, scenario, cancellationToken)
            .ConfigureAwait(false);
        EntityId requestId = EntityId.New();
        Task<Result<GatewaySingleAttemptOutcome>> execution = ExecuteAttemptAsync(
            process,
            authorization,
            requestId,
            input,
            $"m4-e1-{input}",
            cancellationToken);
        string observedRequest;
        try
        {
            observedRequest = await upstream.WaitForRequestAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            upstream.ReleaseResponse();
        }

        Result<GatewaySingleAttemptOutcome> executed = await execution
            .ConfigureAwait(false);
        Assert.Equal(
            observedRequest,
            await upstreamRequest.ConfigureAwait(false));
        return new CompletedAttemptEvidence(
            observedRequest,
            AssertSuccessfulOutcome(executed, requestId));
    }

    private static JsonElement CreatePayload(string input)
    {
        using JsonDocument document = JsonDocument.Parse(
            $$"""
            {"model":"{{Model}}","input":"{{input}}","max_output_tokens":8}
            """);
        return document.RootElement.Clone();
    }

    private static void AssertNoAttemptWork(
        PipelineRuntime runtime,
        SingleRequestHttpServer upstream)
    {
        Assert.False(upstream.HasPendingConnection);
        Assert.Equal(0, upstream.AcceptedConnections);
        Assert.Equal(0, runtime.Adapter.PrepareCalls);
        Assert.Equal(0, runtime.Adapter.CreateRequestCalls);
        Assert.Equal(0, runtime.Adapter.ParseResponseCalls);
        Assert.Equal(0, runtime.Adapter.DisposeCalls);
    }

    private static void AssertCurrentRouteRevision(
        PipelineRuntime runtime,
        AccountRoute initialRoute,
        RouteCredentialLeaseRequest initialCredentialRequest,
        Uri expectedBaseUri)
    {
        AccountRoute currentRoute = Assert.IsType<AccountRoute>(
            runtime.Router.LastRoute);
        RouteCredentialLeaseRequest currentCredentialRequest = Assert.IsType<
            RouteCredentialLeaseRequest>(runtime.CredentialSource.LastRequest);
        Assert.Equal(initialRoute.AccountVersion, initialCredentialRequest.AccountVersion);
        Assert.Equal(
            initialRoute.CredentialRevision,
            initialCredentialRequest.CredentialRevision);
        Assert.Equal(expectedBaseUri, currentRoute.UpstreamBaseUri);
        Assert.Equal(
            initialRoute.SupplyConfigurationVersion + 1,
            currentRoute.SupplyConfigurationVersion);
        Assert.Equal(initialRoute.AccountVersion + 1, currentRoute.AccountVersion);
        Assert.Equal(
            initialRoute.CredentialRevision + 1,
            currentRoute.CredentialRevision);
        Assert.Equal(currentRoute.AccountId, currentCredentialRequest.AccountId);
        Assert.Equal(currentRoute.AccountVersion, currentCredentialRequest.AccountVersion);
        Assert.Equal(
            currentRoute.CredentialRevision,
            currentCredentialRequest.CredentialRevision);
        Assert.Equal(currentRoute.UpstreamBaseUri, currentCredentialRequest.UpstreamBaseUri);
    }

    private static void AssertReplacementAttemptEvidence(
        PipelineRuntime runtime,
        GatewayScenario scenario,
        SingleRequestHttpServer initialUpstream,
        SingleRequestHttpServer replacementUpstream,
        CompletedAttemptEvidence initial,
        CompletedAttemptEvidence replacement,
        AccountRoute initialRoute,
        RouteCredentialLeaseRequest initialCredentialRequest,
        string replacementCredential)
    {
        AssertCurrentRouteRevision(
            runtime,
            initialRoute,
            initialCredentialRequest,
            replacementUpstream.BaseUri);
        Assert.Contains(
            $"Authorization: Bearer {replacementCredential}\r\n",
            replacement.ObservedRequest,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"Authorization: Bearer {scenario.UpstreamCredential}\r\n",
            replacement.ObservedRequest,
            StringComparison.Ordinal);
        Assert.Equal(1, initialUpstream.AcceptedConnections);
        Assert.Equal(1, replacementUpstream.AcceptedConnections);
        Assert.NotEqual(initial.Outcome.RequestId, replacement.Outcome.RequestId);
        Assert.Equal(2, runtime.Adapter.PrepareCalls);
        Assert.Equal(2, runtime.Adapter.CreateRequestCalls);
        Assert.Equal(2, runtime.Adapter.ParseResponseCalls);
        Assert.Equal(2, runtime.Adapter.DisposeCalls);
        AssertProductionPortCalls(
            runtime,
            scenario,
            expectedDownstreamCalls: 2,
            expectedRpmCalls: 2);
    }

    private async ValueTask<PausedAttemptEvidence> CapturePausedAttemptAsync(
        SingleRequestHttpServer upstream,
        PipelineRuntime runtime,
        GatewayScenario scenario,
        EntityId requestId,
        Task<Result<GatewaySingleAttemptOutcome>> execution,
        CancellationToken cancellationToken)
    {
        try
        {
            string request = await upstream.WaitForRequestAsync(cancellationToken)
                .ConfigureAwait(true);
            GatewayMidFlightFootprint footprint = await ReadMidFlightFootprintAsync(
                    runtime.RedisKeyPrefix,
                    scenario,
                    requestId,
                    cancellationToken)
                .ConfigureAwait(true);
            return new PausedAttemptEvidence(
                request,
                footprint,
                execution.IsCompleted);
        }
        finally
        {
            upstream.ReleaseResponse();
        }
    }

    private static GatewaySingleAttemptOutcome AssertSuccessfulOutcome(
        Result<GatewaySingleAttemptOutcome> result,
        EntityId requestId)
    {
        Assert.True(result.IsSuccess, result.Error.Description);
        GatewaySingleAttemptOutcome outcome = result.Value;
        Assert.Equal(requestId, outcome.RequestId);
        Assert.Equal(0, outcome.AttemptIndex);
        Assert.Equal(GatewaySingleAttemptDisposition.Succeeded, outcome.Disposition);
        Assert.Equal(GatewayAttemptPhase.DispatchedNoDownstreamHeaders, outcome.Phase);
        Assert.Equal(ReservationLifetimeStopReason.Completed, outcome.Lifetime.StopReason);
        Assert.Equal(
            AccountLeaseLifetimeStopReason.Completed,
            outcome.AccountLeaseStopReason);
        Assert.False(outcome.Lifetime.SettledConservatively);
        Assert.Null(outcome.ErrorCode);
        Assert.NotNull(outcome.UpstreamResult?.Usage);
        Assert.Equal(
            new BigInteger(18),
            outcome.UpstreamResult.Usage.TotalTokens);
        return outcome;
    }

    private static void AssertMidFlightEvidence(
        GatewayMidFlightFootprint footprint,
        bool completedBeforeRelease)
    {
        Assert.False(completedBeforeRelease);
        Assert.True(footprint.AccountLeaseExists);
        Assert.Equal("pending", footprint.ReservationStatus);
        Assert.True(footprint.DispatchStarted);
        Assert.Equal("openai_compatible", footprint.DispatchProvider);
        Assert.Equal(Model, footprint.DispatchModel);
        Assert.True(BigInteger.Parse(
            footprint.ReservedTokens,
            CultureInfo.InvariantCulture) > BigInteger.Zero);
        Assert.Equal("accepted", footprint.RequestStatus);
        Assert.Equal(0, footprint.RequestAttemptCount);
        Assert.True(footprint.FinalAttemptIdIsNull);
        Assert.True(footprint.EffectiveModelIsNull);
        Assert.Equal(0, footprint.PersistedAttemptCount);
    }

    private static void AssertOutboundEvidence(
        string observedRequest,
        SingleRequestHttpServer upstream,
        GatewayScenario scenario,
        PipelineRuntime runtime)
    {
        Assert.Contains(
            "POST /v1/responses HTTP/1.1\r\n",
            observedRequest,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Authorization: Bearer {scenario.UpstreamCredential}\r\n",
            observedRequest,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"model\":\"{Model}\"",
            observedRequest,
            StringComparison.Ordinal);
        Assert.Equal(1, upstream.AcceptedConnections);
        Assert.Equal(1, runtime.Adapter.PrepareCalls);
        Assert.Equal(1, runtime.Adapter.CreateRequestCalls);
        Assert.Equal(1, runtime.Adapter.ParseResponseCalls);
        Assert.Equal(1, runtime.Adapter.DisposeCalls);
    }

    private static void AssertTerminalDatabaseFootprint(
        GatewayDatabaseFootprint footprint,
        EntityId attemptId)
    {
        Assert.Equal("settled", footprint.ReservationStatus);
        Assert.Equal("18", footprint.ReservationActualTokens);
        Assert.Equal("upstream", footprint.ReservationUsageSource);
        Assert.Equal("18", footprint.ConsumedTokens);
        Assert.Equal("0", footprint.ReservedTokens);
        Assert.Equal("succeeded", footprint.AttemptStatus);
        Assert.Equal("11", footprint.InputTokens);
        Assert.Equal("7", footprint.OutputTokens);
        Assert.Equal("18", footprint.AttemptTotalTokens);
        Assert.Equal("upstream", footprint.AttemptUsageSource);
        Assert.False(footprint.IsEstimated);
        Assert.Equal(200, footprint.UpstreamStatus);
        Assert.Equal("upstream-m4-e1", footprint.UpstreamRequestId);
        Assert.Equal("succeeded", footprint.RequestStatus);
        Assert.Equal(1, footprint.RequestAttemptCount);
        Assert.Equal(attemptId.Value, footprint.RequestFinalAttemptId);
        Assert.Equal(Model, footprint.RequestEffectiveModel);
        Assert.Equal(1, footprint.RequestCount);
        Assert.Equal(1, footprint.ReservationCount);
        Assert.Equal(1, footprint.AttemptCount);
        AssertRawUsage(footprint.RawUpstreamUsage);
    }

    private static void AssertRawUsage(string rawUpstreamUsage)
    {
        using JsonDocument document = JsonDocument.Parse(rawUpstreamUsage);
        JsonElement usage = document.RootElement;
        Assert.Equal(JsonValueKind.Object, usage.ValueKind);
        Assert.Equal(2, usage.EnumerateObject().Count());
        Assert.Equal(11, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(7, usage.GetProperty("output_tokens").GetInt32());
    }

    private static void AssertProductionPortCalls(
        PipelineRuntime runtime,
        GatewayScenario scenario,
        int expectedDownstreamCalls,
        int expectedRpmCalls = 1)
    {
        Assert.Equal(expectedRpmCalls, runtime.Counter.Calls);
        if (expectedRpmCalls > 0)
        {
            FixedWindowCounterRequest counterRequest = Assert.IsType<
                FixedWindowCounterRequest>(runtime.Counter.LastRequest);
            Assert.Equal(
                $"rate:group:v1:{{{scenario.GroupId.Value:D}}}",
                counterRequest.KeyBase);
            Assert.Equal(17, counterRequest.Limit);
            Assert.Equal(1, counterRequest.Increment);
        }

        Assert.Equal(expectedDownstreamCalls, runtime.Router.Calls);
        Assert.Equal(expectedDownstreamCalls, runtime.QuotaLedger.ReserveCalls);
        Assert.Equal(expectedDownstreamCalls, runtime.CredentialSource.Calls);
        if (expectedDownstreamCalls > 0)
        {
            Assert.Equal(scenario.GroupId, runtime.Router.LastCommand?.GroupId);
            Assert.Equal(Model, runtime.Router.LastCommand?.Model);
            Assert.Equal(
                scenario.AccountId,
                runtime.CredentialSource.LastRequest?.AccountId);
        }
    }

    private async ValueTask<PipelineRuntime> CreateRuntimeAsync(
        GatewayScenario scenario,
        string redisConnectionString,
        CancellationToken cancellationToken,
        string? postgresConnectionStringOverride = null)
    {
        string postgresConnectionString = postgresConnectionStringOverride
            ?? ApiPostgresConnectionString();
        string redisSuffix = Guid.NewGuid().ToString("N")[..8];
        string redisKeyPrefix = $"poolai:r1:m4e1-{redisSuffix}:";
        ConfigurationManager configuration = TestConfiguration(
            postgresConnectionString,
            redisConnectionString,
            redisKeyPrefix);
        PipelineAdapter adapter = new();
        AdapterCapability capability = PipelineAdapter.PipelineCapability;
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPoolAiPostgresRuntime(postgresConnectionString);
        services.AddOperationsModule(configuration, ProductionEnvironment);
        services.AddIdentityModule(configuration);
        services.AddSubscriptionAccessModule(configuration);
        services.AddGroupQuotaModule();
        services.AddSupplyModule(configuration, ProductionEnvironment);
        services.AddRoutingModule();
        DecorateProductionPorts(services);
        services.AddSingleton(capability);
        services.AddSingleton<IUpstreamAdapter>(adapter);
        // All implementations remain the production graph. Test is used only
        // for the explicit loopback-HTTP allowance; HTTPS/TLS evidence belongs
        // to GatewayOutboundTransportIntegrationTests.
        services.AddGatewayModule(
            configuration,
            LoopbackTestEnvironment,
            disconnectDrainSeconds: 5);
        ServiceProvider provider = BuildProvider(services);
        try
        {
            AccountCredentialProtection protection = provider
                .GetRequiredService<IAccountCredentialProtector>()
                .Protect(scenario.UpstreamCredential, scenario.AccountId);
            await SeedScenarioAsync(
                    scenario,
                    protection,
                    cancellationToken)
                .ConfigureAwait(true);
            return new PipelineRuntime(
                provider,
                adapter,
                redisKeyPrefix,
                provider.GetRequiredService<RecordingFixedWindowCounter>(),
                provider.GetRequiredService<RecordingAccountRouter>(),
                provider.GetRequiredService<RecordingCredentialLeaseSource>(),
                provider.GetRequiredService<RecordingGroupQuotaLedger>());
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(true);
            throw;
        }
    }

    private string ApiPostgresConnectionString() => _fixture.ApiServices
        .GetRequiredService<IConfiguration>()["Data:Postgres:ConnectionString"]
        ?? throw new InvalidOperationException(
            "The PostgreSQL fixture did not expose its API connection string.");

    private string UnavailablePostgresConnectionString()
    {
        NpgsqlConnectionStringBuilder builder = new(ApiPostgresConnectionString())
        {
            Host = IPAddress.Loopback.ToString(),
            Port = 1,
            Timeout = 1,
            CommandTimeout = 1,
            Pooling = false,
        };
        return builder.ConnectionString;
    }

    private static ServiceProvider BuildProvider(ServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

    private static void DecorateProductionPorts(ServiceCollection services)
    {
        DecorateSingleton<IFixedWindowCounter, RecordingFixedWindowCounter>(
            services,
            static inner => new RecordingFixedWindowCounter(inner));
        DecorateSingleton<IAccountRouter, RecordingAccountRouter>(
            services,
            static inner => new RecordingAccountRouter(inner));
        DecorateSingleton<IRouteCredentialLeaseSource,
            RecordingCredentialLeaseSource>(
            services,
            static inner => new RecordingCredentialLeaseSource(inner));
        DecorateSingleton<IGroupQuotaLedger, RecordingGroupQuotaLedger>(
            services,
            static inner => new RecordingGroupQuotaLedger(inner));
    }

    private static void DecorateSingleton<TService, TDecorator>(
        ServiceCollection services,
        Func<TService, TDecorator> decorate)
        where TService : class
        where TDecorator : class, TService
    {
        ServiceDescriptor registration = services.Last(descriptor =>
            descriptor.ServiceType == typeof(TService));
        if (registration.Lifetime != ServiceLifetime.Singleton
            || !services.Remove(registration))
        {
            throw new InvalidOperationException(
                $"{typeof(TService).Name} must have one movable singleton registration.");
        }

        Func<IServiceProvider, TService> inner = MoveInnerRegistration<TService>(
            services,
            registration);
        services.AddSingleton<TDecorator>(provider => decorate(inner(provider)));
        services.AddSingleton<TService>(provider =>
            provider.GetRequiredService<TDecorator>());
    }

    private static Func<IServiceProvider, TService>
        MoveInnerRegistration<TService>(
            ServiceCollection services,
            ServiceDescriptor registration)
        where TService : class
    {
        if (registration.ImplementationType is { } implementationType)
        {
            services.AddSingleton(implementationType, implementationType);
            return provider => (TService)provider.GetRequiredService(
                implementationType);
        }

        if (registration.ImplementationFactory is { } implementationFactory)
        {
            return provider => (TService)implementationFactory(provider);
        }

        if (registration.ImplementationInstance is TService instance)
        {
            return _ => instance;
        }

        throw new InvalidOperationException(
            $"{typeof(TService).Name} has an unsupported registration.");
    }

    private ValueTask DisableApiKeyAsync(
        GatewayScenario scenario,
        CancellationToken cancellationToken) => CommitMutationsAsync(
            cancellationToken,
            new DatabaseMutation(
                """
                UPDATE public.api_keys
                SET status = 'disabled',
                    version = version + 1,
                    updated_at = clock_timestamp()
                WHERE id = $1
                  AND status = 'active';
                """,
                [scenario.ApiKeyId.Value]));

    private async ValueTask ReplaceSupplyRouteAndCredentialAsync(
        PipelineRuntime runtime,
        GatewayScenario scenario,
        Uri replacementBaseUri,
        string replacementCredential,
        CancellationToken cancellationToken)
    {
        AccountCredentialProtection protection = runtime.Services
            .GetRequiredService<IAccountCredentialProtector>()
            .Protect(replacementCredential, scenario.AccountId);
        await CommitMutationsAsync(
                cancellationToken,
                new DatabaseMutation(
                    """
                    UPDATE public.group_supply_configurations
                    SET version = version + 1,
                        updated_at = clock_timestamp()
                    WHERE group_id = $1;
                    """,
                    [scenario.GroupId.Value]),
                new DatabaseMutation(
                    """
                    UPDATE public.accounts
                    SET upstream_base_url = $2,
                        credential_envelope = $3::jsonb,
                        credential_prefix = 'test-m4e1-r2',
                        credential_hint = 'combined dependency replacement',
                        credential_revision = credential_revision + 1,
                        version = version + 1,
                        updated_at = clock_timestamp()
                    WHERE id = $1
                      AND status = 'active'
                      AND deleted_at IS NULL;
                    """,
                    [
                        scenario.AccountId.Value,
                        replacementBaseUri.AbsoluteUri,
                        protection.Envelope.GetRawText(),
                    ]))
            .ConfigureAwait(true);
    }

    private async ValueTask CommitMutationsAsync(
        CancellationToken cancellationToken,
        params DatabaseMutation[] mutations)
    {
        NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(true);
        NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable transactionLease =
            transaction.ConfigureAwait(true);
        foreach (DatabaseMutation mutation in mutations)
        {
            await ExecuteSeedStatementAsync(
                    connection,
                    transaction,
                    mutation.Statement,
                    cancellationToken,
                    mutation.Parameters)
                .ConfigureAwait(true);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask SeedScenarioAsync(
        GatewayScenario scenario,
        AccountCredentialProtection protection,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(true);
        NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable transactionLease =
            transaction.ConfigureAwait(true);
        await SeedIdentityAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(true);
        await SeedQuotaAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(true);
        await SeedAccountAndChannelAsync(
                connection,
                transaction,
                scenario,
                protection,
                cancellationToken)
            .ConfigureAwait(true);
        await SeedSupplyBindingsAsync(
                connection,
                transaction,
                scenario,
                cancellationToken)
            .ConfigureAwait(true);
        await SeedSubscriptionAsync(
                connection,
                transaction,
                scenario,
                cancellationToken)
            .ConfigureAwait(true);
        await SeedApiKeyAsync(connection, transaction, scenario, cancellationToken)
            .ConfigureAwait(true);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask SeedIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GatewayScenario scenario,
        CancellationToken cancellationToken)
    {
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            INSERT INTO public.users (
                id, email, normalized_email, display_name,
                password_hash, security_stamp
            ) VALUES ($1, $2, $2, $3, 'poolai-password-v1:integration', $4);
            """,
            cancellationToken,
            scenario.UserId.Value,
            scenario.Email,
            scenario.UserName,
            scenario.SecurityStamp.Value).ConfigureAwait(true);
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            INSERT INTO public.user_roles (user_id, role_id, assigned_by)
            VALUES (
                $1,
                '01900000-0000-7000-8000-000000000004'::uuid,
                $1
            );
            """,
            cancellationToken,
            scenario.UserId.Value).ConfigureAwait(true);
    }

    private static async ValueTask SeedQuotaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GatewayScenario scenario,
        CancellationToken cancellationToken)
    {
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            INSERT INTO public.groups (
                id, name, status, runtime_policy
            ) VALUES (
                $1, $2, 'disabled',
                '{"schema_version":1,"requests_per_minute":17}'::jsonb
            );
            """,
            cancellationToken,
            scenario.GroupId.Value,
            scenario.GroupName).ConfigureAwait(true);
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            INSERT INTO public.group_token_quotas (group_id, current_period_id)
            VALUES ($1, $2);
            """,
            cancellationToken,
            scenario.GroupId.Value,
            scenario.PeriodId.Value).ConfigureAwait(true);
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            INSERT INTO public.group_quota_periods (
                id, group_id, period_number, total_tokens
            ) VALUES ($1, $2, 1, 1000000);
            """,
            cancellationToken,
            scenario.PeriodId.Value,
            scenario.GroupId.Value).ConfigureAwait(true);
    }

    private static async ValueTask SeedAccountAndChannelAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GatewayScenario scenario,
        AccountCredentialProtection protection,
        CancellationToken cancellationToken)
    {
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            INSERT INTO public.accounts (
                id, provider, name, auth_type, upstream_base_url,
                credential_envelope, credential_prefix, credential_hint,
                status, priority, weight, max_concurrency,
                last_health_at, last_health_status
            ) VALUES (
                $1, 'openai_compatible', $2, 'api_key', $3,
                $4::jsonb, 'test-m4e1', 'combined dependency fixture',
                'active', 100, 100, 1,
                clock_timestamp(), 'healthy'
            );
            """,
            cancellationToken,
            scenario.AccountId.Value,
            scenario.AccountName,
            scenario.UpstreamBaseUri.AbsoluteUri,
            protection.Envelope.GetRawText()).ConfigureAwait(true);
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            INSERT INTO public.channels (
                id, provider, name, model_rules, capabilities, status
            ) VALUES (
                $1, 'openai_compatible', $2,
                '{"gpt-m4-e1":"gpt-m4-e1"}'::jsonb,
                '{"responses":true,"chat_completions":true,"function_tools":true,"streaming":true}'::jsonb,
                'active'
            );
            """,
            cancellationToken,
            scenario.ChannelId.Value,
            scenario.ChannelName).ConfigureAwait(true);
    }

    private static async ValueTask SeedSupplyBindingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GatewayScenario scenario,
        CancellationToken cancellationToken)
    {
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            INSERT INTO public.group_supply_configurations (group_id, channel_id)
            VALUES ($1, $2);
            """,
            cancellationToken,
            scenario.GroupId.Value,
            scenario.ChannelId.Value).ConfigureAwait(true);
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            INSERT INTO public.group_accounts (group_id, account_id, is_enabled)
            VALUES ($1, $2, true);
            """,
            cancellationToken,
            scenario.GroupId.Value,
            scenario.AccountId.Value).ConfigureAwait(true);
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            UPDATE public.groups
            SET status = 'active',
                activation_supply_readiness_token = 'supply.m4e1',
                activation_supply_observed_at = clock_timestamp(),
                version = version + 1,
                updated_at = clock_timestamp()
            WHERE id = $1;
            """,
            cancellationToken,
            scenario.GroupId.Value).ConfigureAwait(true);
    }

    private static async ValueTask SeedSubscriptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GatewayScenario scenario,
        CancellationToken cancellationToken)
    {
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            INSERT INTO public.subscription_templates (
                id, group_id, name, default_duration_days
            ) VALUES ($1, $2, $3, 30);
            """,
            cancellationToken,
            scenario.TemplateId.Value,
            scenario.GroupId.Value,
            scenario.TemplateName).ConfigureAwait(true);
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            INSERT INTO public.subscriptions (
                id, user_id, group_id, template_id,
                template_name_snapshot, status, starts_at, expires_at,
                assigned_by, change_reason
            ) VALUES (
                $1, $2, $3, $4, $5, 'active',
                clock_timestamp() - interval '1 minute',
                clock_timestamp() + interval '1 day',
                $2, 'M4-E1 combined dependency fixture'
            );
            """,
            cancellationToken,
            scenario.SubscriptionId.Value,
            scenario.UserId.Value,
            scenario.GroupId.Value,
            scenario.TemplateId.Value,
            scenario.TemplateName).ConfigureAwait(true);
    }

    private static async ValueTask SeedApiKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GatewayScenario scenario,
        CancellationToken cancellationToken) =>
        await ExecuteSeedStatementAsync(
            connection,
            transaction,
            """
            INSERT INTO public.api_keys (
                id, user_id, group_id, name, key_prefix,
                secret_hash, pepper_version, status, ip_acl
            ) VALUES (
                $1, $2, $3, $4, $5,
                $6, 7, 'active', '[]'::jsonb
            );
            """,
            cancellationToken,
            scenario.ApiKeyId.Value,
            scenario.UserId.Value,
            scenario.GroupId.Value,
            scenario.ApiKeyName,
            scenario.ApiKeyDisplayPrefix,
            scenario.ApiKeyHash).ConfigureAwait(true);

    private static async ValueTask ExecuteSeedStatementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string statement,
        CancellationToken cancellationToken,
        params object[] parameters)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = statement;
        foreach (object parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(true));
    }

    private async ValueTask<GatewayDatabaseFootprint> ReadFootprintAsync(
        GatewayScenario scenario,
        EntityId attemptId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                reservation.status,
                reservation.actual_tokens::text,
                reservation.usage_source,
                period.consumed_tokens::text,
                period.reserved_tokens::text,
                attempt.status,
                attempt.input_tokens::text,
                attempt.output_tokens::text,
                attempt.total_tokens::text,
                attempt.usage_source,
                attempt.is_estimated,
                attempt.upstream_http_status,
                attempt.upstream_request_id,
                attempt.raw_upstream_usage::text,
                request.status,
                request.attempt_count,
                request.final_attempt_id,
                request.effective_model,
                (SELECT count(*)::integer
                 FROM public.usage_requests AS observed_request
                 WHERE observed_request.request_id = reservation.request_id),
                (SELECT count(*)::integer
                 FROM public.group_token_reservations AS observed_reservation
                 WHERE observed_reservation.request_id = reservation.request_id),
                (SELECT count(*)::integer
                 FROM public.usage_attempts AS observed_attempt
                 WHERE observed_attempt.request_id = reservation.request_id)
            FROM public.group_token_reservations AS reservation
            JOIN public.group_quota_periods AS period
              ON period.id = reservation.period_id
             AND period.group_id = reservation.group_id
            JOIN public.usage_attempts AS attempt
              ON attempt.attempt_id = reservation.attempt_id
             AND attempt.reservation_id = reservation.id
            JOIN public.usage_requests AS request
              ON request.request_id = reservation.request_id
            WHERE reservation.group_id = $1
              AND reservation.account_id = $2
              AND reservation.attempt_id = $3;
            """);
        command.Parameters.AddWithValue(scenario.GroupId.Value);
        command.Parameters.AddWithValue(scenario.AccountId.Value);
        command.Parameters.AddWithValue(attemptId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        GatewayDatabaseFootprint result = ReadTerminalFootprint(reader);
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        return result;
    }

    private static GatewayDatabaseFootprint ReadTerminalFootprint(
        NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetBoolean(10),
        reader.GetInt32(11),
        reader.GetString(12),
        reader.GetString(13),
        reader.GetString(14),
        reader.GetInt32(15),
        reader.GetGuid(16),
        reader.GetString(17),
        reader.GetInt32(18),
        reader.GetInt32(19),
        reader.GetInt32(20));

    private async ValueTask<GatewayMidFlightFootprint>
        ReadMidFlightFootprintAsync(
            string redisKeyPrefix,
            GatewayScenario scenario,
            EntityId requestId,
            CancellationToken cancellationToken)
    {
        GatewayMidFlightFootprint footprint = await ReadMidFlightDatabaseAsync(
                scenario,
                requestId,
                cancellationToken)
            .ConfigureAwait(true);
        using ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        bool leaseExists = await redis.GetDatabase().KeyExistsAsync(
                AccountLeaseKey(redisKeyPrefix, scenario.AccountId))
            .ConfigureAwait(true);
        return footprint with { AccountLeaseExists = leaseExists };
    }

    private async ValueTask<GatewayMidFlightFootprint> ReadMidFlightDatabaseAsync(
        GatewayScenario scenario,
        EntityId requestId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT reservation.status,
                   reservation.dispatch_started_at IS NOT NULL,
                   reservation.dispatch_provider,
                   reservation.dispatch_model,
                   period.reserved_tokens::text,
                   request.status,
                   request.attempt_count,
                   request.final_attempt_id IS NULL,
                   request.effective_model IS NULL,
                   (SELECT count(*)::integer FROM public.usage_attempts AS attempt
                    WHERE attempt.request_id = request.request_id)
            FROM public.group_token_reservations AS reservation
            JOIN public.group_quota_periods AS period
              ON period.id = reservation.period_id
             AND period.group_id = reservation.group_id
            JOIN public.usage_requests AS request
              ON request.request_id = reservation.request_id
            WHERE reservation.group_id = $1
              AND reservation.account_id = $2
              AND reservation.request_id = $3;
            """);
        command.Parameters.AddWithValue(scenario.GroupId.Value);
        command.Parameters.AddWithValue(scenario.AccountId.Value);
        command.Parameters.AddWithValue(requestId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        GatewayMidFlightFootprint result = new(
            reader.GetString(0), reader.GetBoolean(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetInt32(6), reader.GetBoolean(7), reader.GetBoolean(8),
            reader.GetInt32(9), AccountLeaseExists: false);
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        return result;
    }

    private async ValueTask<GatewayPreDispatchFootprint>
        ReadPreDispatchFootprintAsync(
            EntityId requestId,
            EntityId accountId,
            CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*)::integer
                 FROM public.usage_requests
                 WHERE request_id = $1),
                (SELECT count(*)::integer
                 FROM public.group_token_reservations
                 WHERE request_id = $1),
                (SELECT count(*)::integer
                 FROM public.usage_attempts AS attempt
                 WHERE attempt.request_id = $1),
                (SELECT count(*)::integer
                 FROM public.group_token_reservations AS reservation
                 WHERE reservation.account_id = $2
                   AND reservation.request_id = $1);
            """);
        command.Parameters.AddWithValue(requestId.Value);
        command.Parameters.AddWithValue(accountId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        GatewayPreDispatchFootprint result = new(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(true));
        return result;
    }

    private async ValueTask AssertNoPreDispatchPersistenceAsync(
        EntityId requestId,
        EntityId accountId,
        CancellationToken cancellationToken)
    {
        GatewayPreDispatchFootprint footprint = await ReadPreDispatchFootprintAsync(
                requestId,
                accountId,
                cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(0, footprint.RequestCount);
        Assert.Equal(0, footprint.ReservationCount);
        Assert.Equal(0, footprint.AttemptCount);
    }

    private async ValueTask AssertRedisResourcesAsync(
        string redisKeyPrefix,
        GatewayScenario scenario,
        CancellationToken cancellationToken,
        long expectedRpmCount = 1)
    {
        using ConnectionMultiplexer redis = await ConnectionMultiplexer
            .ConnectAsync(_fixture.RedisConnectionString)
            .ConfigureAwait(true);
        IDatabase database = redis.GetDatabase();
        long redisMinute = await RedisMinuteAsync(database, cancellationToken)
            .ConfigureAwait(true);
        RedisValue current = await database.StringGetAsync(
                $"{redisKeyPrefix}rate:group:v1:{{{scenario.GroupId.Value:D}}}:{redisMinute}")
            .ConfigureAwait(true);
        RedisValue previous = await database.StringGetAsync(
                $"{redisKeyPrefix}rate:group:v1:{{{scenario.GroupId.Value:D}}}:{redisMinute - 1}")
            .ConfigureAwait(true);
        long rpmCount = ParseRedisCount(current) + ParseRedisCount(previous);
        Assert.Equal(expectedRpmCount, rpmCount);
        Assert.False(await database.KeyExistsAsync(
                AccountLeaseKey(redisKeyPrefix, scenario.AccountId))
            .ConfigureAwait(true));
    }

    private static string AccountLeaseKey(
        string redisKeyPrefix,
        EntityId accountId) =>
        $"{redisKeyPrefix}lease:account:v1:{{{accountId.Value:D}}}";

    private static async ValueTask<long> RedisMinuteAsync(
        IDatabase database,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RedisResult result = await database.ExecuteAsync("TIME").ConfigureAwait(true);
        RedisResult[] parts = (RedisResult[]?)result
            ?? throw new InvalidOperationException("Redis TIME returned an invalid result.");
        if (parts.Length != 2
            || !long.TryParse(
                parts[0].ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long seconds))
        {
            throw new InvalidOperationException("Redis TIME returned an invalid result.");
        }

        return seconds / 60;
    }

    private static long ParseRedisCount(RedisValue value) => value.IsNull
        ? 0
        : long.Parse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);

    private static ConfigurationManager TestConfiguration(
        string postgresConnectionString,
        string redisConnectionString,
        string redisKeyPrefix)
    {
        ConfigurationManager configuration = new();
        AddRuntimeConfiguration(
            configuration,
            postgresConnectionString,
            redisConnectionString,
            redisKeyPrefix);
        AddSecretConfiguration(configuration);
        return configuration;
    }

    private static void AddRuntimeConfiguration(
        ConfigurationManager configuration,
        string postgresConnectionString,
        string redisConnectionString,
        string redisKeyPrefix)
    {
        configuration["Data:Postgres:ConnectionString"] = postgresConnectionString;
        configuration["Data:Redis:ConnectionString"] = redisConnectionString;
        configuration["Data:Redis:KeyPrefix"] = redisKeyPrefix;
        configuration["Health:ReadinessTimeoutSeconds"] = "1";
        configuration["Health:Ntp:Server"] = "127.0.0.1";
        configuration["Health:Ntp:Port"] = "123";
        configuration["App:PublicBaseUrl"] = "https://poolai.integration.test";
        configuration["Email:FromAddress"] = "no-reply@poolai.test";
        configuration["Gateway:ConnectTimeoutSeconds"] = "5";
        configuration["Gateway:FirstByteTimeoutSeconds"] = "5";
        configuration["Gateway:StreamIdleTimeoutSeconds"] = "15";
        configuration["Gateway:MaxConnectionsPerServer"] = "16";
    }

    private static void AddSecretConfiguration(
        ConfigurationManager configuration)
    {
        string envelopeKey = Convert.ToBase64String(EnvelopeKey);
        configuration["Auth:Jwt:SigningKey"] = SecretBase64(0x11);
        configuration["Auth:Password:MinLength"] = "12";
        configuration["Auth:PasswordReset:TokenMinutes"] = "30";
        configuration["Auth:RefreshToken:CurrentPepperVersion"] = "7";
        configuration["Auth:RefreshToken:CurrentPepper"] = SecretBase64(0x12);
        configuration["Auth:TokenHash:CurrentPepperVersion"] = "7";
        configuration["Auth:TokenHash:CurrentPepper"] = SecretBase64(0x13);
        configuration["Auth:PasswordReset:RateLimitScopePepper"] =
            SecretBase64(0x14);
        configuration["Auth:PasswordReset:IpRequestsPerMinute"] = "5";
        configuration["Auth:PasswordReset:AccountRequestsPerMinute"] = "3";
        configuration["Auth:TOTP:RecoveryCodePepperVersion"] = "7";
        configuration["Auth:TOTP:RecoveryCodePepper"] = SecretBase64(0x15);
        configuration["Auth:Login:IpFailuresPerMinute"] = "20";
        configuration["Auth:Login:RateLimitScopePepper"] = SecretBase64(0x16);
        configuration["ApiKeys:Prefix"] = KeyPrefix;
        configuration["ApiKeys:CurrentPepperVersion"] = "7";
        configuration["ApiKeys:CurrentPepper"] = Convert.ToBase64String(ApiKeyPepper);
        configuration["Idempotency:RequestHashPepper"] = SecretBase64(0x17);
        configuration["Secrets:Envelope:CurrentKeyId"] = "m4-e1-test-k1";
        configuration["Secrets:Envelope:CurrentKey"] = envelopeKey;
        configuration["Secrets:Envelope:DecryptKeyRing:m4-e1-test-k1"] = envelopeKey;
    }

    private static string SecretBase64(byte value) =>
        Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray());

    private static byte[] HashApiKey(string presentedApiKey)
    {
        byte[] input = Encoding.UTF8.GetBytes(
            "PoolAI:ApiKey:v1:" + presentedApiKey);
        try
        {
            return HMACSHA256.HashData(ApiKeyPepper, input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private sealed record GatewayScenario(
        EntityId UserId,
        EntityId SecurityStamp,
        string Email,
        string UserName,
        EntityId GroupId,
        string GroupName,
        EntityId PeriodId,
        EntityId AccountId,
        string AccountName,
        Uri UpstreamBaseUri,
        string UpstreamCredential,
        EntityId ChannelId,
        string ChannelName,
        EntityId TemplateId,
        string TemplateName,
        EntityId SubscriptionId,
        EntityId ApiKeyId,
        string ApiKeyName,
        string PresentedApiKey,
        string ApiKeyDisplayPrefix,
        byte[] ApiKeyHash)
    {
        internal static GatewayScenario Create(Uri upstreamBaseUri)
        {
            string suffix = Guid.NewGuid().ToString("N");
            string payload = Convert.ToBase64String(
                    SHA256.HashData(Encoding.UTF8.GetBytes(suffix)))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            string presentedApiKey = KeyPrefix + payload;
            return new GatewayScenario(
                EntityId.New(),
                EntityId.New(),
                $"m4-e1-{suffix}@poolai.test",
                $"M4-E1 user {suffix}",
                EntityId.New(),
                $"M4-E1 group {suffix}",
                EntityId.New(),
                EntityId.New(),
                $"M4-E1 account {suffix}",
                upstreamBaseUri,
                $"upstream-test-{suffix}",
                EntityId.New(),
                $"M4-E1 channel {suffix}",
                EntityId.New(),
                $"M4-E1 template {suffix}",
                EntityId.New(),
                EntityId.New(),
                $"M4-E1 API key {suffix}",
                presentedApiKey,
                presentedApiKey[..(KeyPrefix.Length + 8)],
                HashApiKey(presentedApiKey));
        }
    }

    private sealed record GatewayDatabaseFootprint(
        string ReservationStatus,
        string ReservationActualTokens,
        string ReservationUsageSource,
        string ConsumedTokens,
        string ReservedTokens,
        string AttemptStatus,
        string InputTokens,
        string OutputTokens,
        string AttemptTotalTokens,
        string AttemptUsageSource,
        bool IsEstimated,
        int UpstreamStatus,
        string UpstreamRequestId,
        string RawUpstreamUsage,
        string RequestStatus,
        int RequestAttemptCount,
        Guid RequestFinalAttemptId,
        string RequestEffectiveModel,
        int RequestCount,
        int ReservationCount,
        int AttemptCount);

    private sealed record GatewayMidFlightFootprint(
        string ReservationStatus,
        bool DispatchStarted,
        string DispatchProvider,
        string DispatchModel,
        string ReservedTokens,
        string RequestStatus,
        int RequestAttemptCount,
        bool FinalAttemptIdIsNull,
        bool EffectiveModelIsNull,
        int PersistedAttemptCount,
        bool AccountLeaseExists);

    private sealed record PausedAttemptEvidence(
        string ObservedRequest,
        GatewayMidFlightFootprint Footprint,
        bool ExecutionCompleted);

    private sealed record CompletedAttemptEvidence(
        string ObservedRequest,
        GatewaySingleAttemptOutcome Outcome);

    private sealed record GatewayPreDispatchFootprint(
        int RequestCount,
        int ReservationCount,
        int AttemptCount);

    private sealed record DatabaseMutation(
        string Statement,
        object[] Parameters);

    private sealed record PipelineRuntime(
        ServiceProvider Services,
        PipelineAdapter Adapter,
        string RedisKeyPrefix,
        RecordingFixedWindowCounter Counter,
        RecordingAccountRouter Router,
        RecordingCredentialLeaseSource CredentialSource,
        RecordingGroupQuotaLedger QuotaLedger) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private sealed class RecordingFixedWindowCounter(
        IFixedWindowCounter inner) : IFixedWindowCounter
    {
        private readonly IFixedWindowCounter _inner = inner
            ?? throw new ArgumentNullException(nameof(inner));
        private FixedWindowCounterRequest? _lastRequest;
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal FixedWindowCounterRequest? LastRequest =>
            Volatile.Read(ref _lastRequest);

        public async ValueTask<FixedWindowCounterResult> IncrementAsync(
            FixedWindowCounterRequest request,
            CancellationToken cancellationToken)
        {
            Volatile.Write(ref _lastRequest, request);
            Interlocked.Increment(ref _calls);
            return await _inner.IncrementAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class RecordingAccountRouter(
        IAccountRouter inner) : IAccountRouter
    {
        private readonly IAccountRouter _inner = inner
            ?? throw new ArgumentNullException(nameof(inner));
        private RouteAccountCommand? _lastCommand;
        private AccountRoute? _lastRoute;
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal RouteAccountCommand? LastCommand =>
            Volatile.Read(ref _lastCommand);

        internal AccountRoute? LastRoute => Volatile.Read(ref _lastRoute);

        public async ValueTask<Result<IAccountLease>> RouteAsync(
            RouteAccountCommand command,
            CancellationToken cancellationToken)
        {
            Volatile.Write(ref _lastCommand, command);
            Interlocked.Increment(ref _calls);
            Result<IAccountLease> result = await _inner
                .RouteAsync(command, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess)
            {
                Volatile.Write(ref _lastRoute, result.Value.Route);
            }

            return result;
        }
    }

    private sealed class RecordingCredentialLeaseSource(
        IRouteCredentialLeaseSource inner) : IRouteCredentialLeaseSource
    {
        private readonly IRouteCredentialLeaseSource _inner = inner
            ?? throw new ArgumentNullException(nameof(inner));
        private RouteCredentialLeaseRequest? _lastRequest;
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal RouteCredentialLeaseRequest? LastRequest =>
            Volatile.Read(ref _lastRequest);

        public async ValueTask<Result<IRouteCredentialLease>> AcquireAsync(
            RouteCredentialLeaseRequest request,
            CancellationToken cancellationToken)
        {
            Volatile.Write(ref _lastRequest, request);
            Interlocked.Increment(ref _calls);
            return await _inner.AcquireAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class RecordingGroupQuotaLedger(
        IGroupQuotaLedger inner) : IGroupQuotaLedger
    {
        private readonly IGroupQuotaLedger _inner = inner
            ?? throw new ArgumentNullException(nameof(inner));
        private int _reserveCalls;

        internal int ReserveCalls => Volatile.Read(ref _reserveCalls);

        public async ValueTask<Result<ReserveQuotaResult>> ReserveAsync(
            ReserveQuotaCommand command,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reserveCalls);
            return await _inner.ReserveAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }

        public ValueTask<Result<DispatchedReservationHandle>> MarkDispatchedAsync(
            MarkReservationDispatchedCommand command,
            CancellationToken cancellationToken) =>
            _inner.MarkDispatchedAsync(command, cancellationToken);

        public ValueTask<Result<ReservationHandle>> RenewAsync(
            RenewReservationCommand command,
            CancellationToken cancellationToken) =>
            _inner.RenewAsync(command, cancellationToken);

        public ValueTask<Result<QuotaTransitionResult>> SettleAsync(
            SettleReservationCommand command,
            CancellationToken cancellationToken) =>
            _inner.SettleAsync(command, cancellationToken);

        public ValueTask<Result<QuotaTransitionResult>> ReleaseAsync(
            ReleaseReservationCommand command,
            CancellationToken cancellationToken) =>
            _inner.ReleaseAsync(command, cancellationToken);
    }

    private sealed class PipelineAdapter : IUpstreamAdapter
    {
        internal static readonly AdapterCapability PipelineCapability = new(
            InboundProtocol.Responses,
            UpstreamType.OpenAiCompatible,
            AdapterOperation.NonStream,
            CanProveNoRequestBytesWritten: true,
            SupportsVerifiedIdempotentReplay: false);

        internal int PrepareCalls { get; private set; }

        internal int CreateRequestCalls { get; private set; }

        internal int ParseResponseCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        public AdapterCapability Capability => PipelineCapability;

        public ValueTask<Result<IPreparedUpstreamAttempt>> PrepareAsync(
            AdapterAttemptContext attempt,
            NormalizedGatewayRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCalls++;
            return ValueTask.FromResult(Result.Success<IPreparedUpstreamAttempt>(
                new PipelinePreparedAttempt(this, attempt, request)));
        }

        private sealed class PipelinePreparedAttempt(
            PipelineAdapter owner,
            AdapterAttemptContext attempt,
            NormalizedGatewayRequest request) : IPreparedUpstreamAttempt
        {
            public ValueTask<Result<PreparedUpstreamRequest>> CreateRequestAsync(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.CreateRequestCalls++;
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(request.Payload);
                try
                {
                    return ValueTask.FromResult(Result.Success(
                        new PreparedUpstreamRequest(
                            HttpMethod.Post,
                            new Uri(attempt.Route.UpstreamBaseUri, "responses"),
                            body,
                            [new PreparedUpstreamHeader("Accept", "application/json")])));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(body);
                }
            }

            public async ValueTask<Result<NormalizedUpstreamResult>> ParseResponseAsync(
                AdapterUpstreamResponse response,
                CancellationToken cancellationToken)
            {
                owner.ParseResponseCalls++;
                using JsonDocument document = await JsonDocument.ParseAsync(
                        response.Content,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                JsonElement payload = document.RootElement.Clone();
                JsonElement usage = payload.GetProperty("usage").Clone();
                return Result.Success(new NormalizedUpstreamResult(
                    response.StatusCode,
                    payload,
                    new NormalizedUpstreamUsage(
                        new BigInteger(11),
                        new BigInteger(7),
                        BigInteger.Zero,
                        BigInteger.Zero,
                        BigInteger.Zero,
                        usage),
                    ErrorCode: null,
                    UpstreamRequestId: "upstream-m4-e1"));
            }

            public ValueTask DisposeAsync()
            {
                owner.DisposeCalls++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class SingleRequestHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> _requestReceived = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _responseRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _serveCancellation;
        private Task<string>? _serveTask;

        internal SingleRequestHttpServer()
        {
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUri = new Uri($"http://127.0.0.1:{port}/v1/");
        }

        internal Uri BaseUri { get; }

        internal int AcceptedConnections { get; private set; }

        internal bool HasPendingConnection => _listener.Pending();

        internal Task<string> ServeAsync(
            string responseBody,
            CancellationToken cancellationToken)
        {
            if (_serveTask is not null)
            {
                throw new InvalidOperationException(
                    "The loopback server already owns a request.");
            }

            _serveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            _serveCancellation.CancelAfter(TimeSpan.FromSeconds(20));
            _serveTask = ServeCoreAsync(responseBody, _serveCancellation.Token);
            return _serveTask;
        }

        internal Task<string> WaitForRequestAsync(
            CancellationToken cancellationToken) =>
            _requestReceived.Task.WaitAsync(cancellationToken);

        internal void ReleaseResponse() =>
            _responseRelease.TrySetResult(true);

        public async ValueTask DisposeAsync()
        {
            _serveCancellation?.Cancel();
            ReleaseResponse();
            _listener.Stop();
            if (_serveTask is not null)
            {
                try
                {
                    _ = await _serveTask.ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException
                        or ObjectDisposedException
                        or SocketException)
                {
                }
            }

            _serveCancellation?.Dispose();
        }

        private async Task<string> ServeCoreAsync(
            string responseBody,
            CancellationToken cancellationToken)
        {
            try
            {
                using TcpClient client = await _listener
                    .AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                AcceptedConnections++;
                using NetworkStream stream = client.GetStream();
                string request = await ReadRequestAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                _requestReceived.TrySetResult(request);
                await _responseRelease.Task.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                await WriteResponseAsync(stream, responseBody, cancellationToken)
                    .ConfigureAwait(false);
                return request;
            }
            catch (Exception exception)
            {
                _requestReceived.TrySetException(exception);
                throw;
            }
        }

        private static async ValueTask WriteResponseAsync(
            Stream stream,
            string responseBody,
            CancellationToken cancellationToken)
        {
            byte[] body = Encoding.UTF8.GetBytes(responseBody);
            byte[] headers = Encoding.ASCII.GetBytes(string.Create(
                CultureInfo.InvariantCulture,
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<string> ReadRequestAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            byte[] received = new byte[16 * 1024];
            int length = 0;
            int expectedLength = int.MaxValue;
            while (length < expectedLength)
            {
                int read = await stream.ReadAsync(
                        received.AsMemory(length),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
                int headerEnd = HeaderEnd(received.AsSpan(0, length));
                if (headerEnd >= 0)
                {
                    expectedLength = checked(
                        headerEnd + 4 + ContentLength(received.AsSpan(0, headerEnd)));
                }

                if (length == received.Length && length < expectedLength)
                {
                    throw new InvalidOperationException(
                        "The loopback request exceeded the bounded test buffer.");
                }
            }

            return Encoding.UTF8.GetString(received, 0, length);
        }

        private static int HeaderEnd(ReadOnlySpan<byte> value)
        {
            for (int index = 0; index <= value.Length - 4; index++)
            {
                if (value[index] == '\r'
                    && value[index + 1] == '\n'
                    && value[index + 2] == '\r'
                    && value[index + 3] == '\n')
                {
                    return index;
                }
            }

            return -1;
        }

        private static int ContentLength(ReadOnlySpan<byte> headers)
        {
            string text = Encoding.ASCII.GetString(headers);
            foreach (string line in text.Split("\r\n", StringSplitOptions.None))
            {
                const string prefix = "Content-Length:";
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(
                        line.AsSpan(prefix.Length).Trim(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int length))
                {
                    return length;
                }
            }

            return 0;
        }
    }
}
