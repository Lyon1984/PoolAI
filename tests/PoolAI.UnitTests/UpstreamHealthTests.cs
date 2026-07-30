using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PoolAI.Modules.Routing.Infrastructure.Workers;
using PoolAI.Modules.Routing.Worker;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Infrastructure.Health;

namespace PoolAI.UnitTests;

public sealed class UpstreamHealthTests
{
    private static readonly DateTimeOffset FirstObservation = new(
        2026,
        7,
        30,
        8,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void HealthOptionsUseTheFixedContractDefaults()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        AccountHealthWorkerOptions worker =
            AccountHealthWorkerOptions.FromConfiguration(configuration);
        AccountHealthProbeHttpOptions production =
            AccountHealthProbeHttpOptions.FromConfiguration(
                configuration,
                "Production");
        AccountHealthProbeHttpOptions test =
            AccountHealthProbeHttpOptions.FromConfiguration(
                configuration,
                "Test");

        Assert.Equal(TimeSpan.FromSeconds(30), worker.ProbeInterval);
        Assert.Equal(8, worker.MaximumConcurrency);
        Assert.Equal(TimeSpan.FromSeconds(10), production.Timeout);
        Assert.Equal(1_048_576, production.MaximumResponseBytes);
        Assert.False(production.AllowLoopbackHttp);
        Assert.True(test.AllowLoopbackHttp);
        Assert.Empty(production.PrivateEgressRules);
        Assert.Empty(test.PrivateEgressRules);
    }

    [Theory]
    [InlineData("Supply:Health:ProbeIntervalSeconds", "29")]
    [InlineData("Supply:Health:ProbeTimeoutSeconds", "11")]
    [InlineData("Supply:Health:ProbeMaxResponseBytes", "1048575")]
    [InlineData("Supply:Health:ProbeMaxConcurrency", "9")]
    public void HealthOptionsRejectDriftFromTheFixedContract(
        string key,
        string value)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(
                StringComparer.Ordinal)
            {
                [key] = value,
            })
            .Build();

        _ = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = AccountHealthWorkerOptions.FromConfiguration(configuration);
            _ = AccountHealthProbeHttpOptions.FromConfiguration(
                configuration,
                "Production");
        });
    }

    [Fact]
    public void ExactHttpLoopbackIsAllowedOnlyByTheTestAddressPolicy()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        AccountHealthProbeHttpOptions production =
            AccountHealthProbeHttpOptions.FromConfiguration(
                configuration,
                "Production");
        AccountHealthProbeHttpOptions test =
            AccountHealthProbeHttpOptions.FromConfiguration(
                configuration,
                "Test");
        Uri endpoint = new("http://localhost:8080/v1/models");

        Assert.False(UpstreamAddressClassifier.IsAllowed(
            endpoint,
            IPAddress.Loopback,
            production.AllowLoopbackHttp));
        Assert.True(UpstreamAddressClassifier.IsAllowed(
            endpoint,
            IPAddress.Loopback,
            test.AllowLoopbackHttp));
        Assert.True(UpstreamAddressClassifier.IsAllowed(
            new Uri("http://127.0.0.1:8080/v1/models"),
            IPAddress.Parse("::ffff:127.0.0.1"),
            test.AllowLoopbackHttp));
        Assert.True(UpstreamAddressClassifier.IsAllowed(
            new Uri("http://[::1]:8080/v1/models"),
            IPAddress.IPv6Loopback,
            test.AllowLoopbackHttp));
    }

    [Fact]
    public void TestAddressPolicyDoesNotTrustLoopbackForAnotherHostOrScheme()
    {
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            new Uri("http://upstream.example.test/v1/models"),
            IPAddress.Loopback,
            allowLoopbackHttp: true));
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            new Uri("https://localhost/v1/models"),
            IPAddress.Loopback,
            allowLoopbackHttp: true));
    }

    [Fact]
    public void ProductionHttpsAllowsPublicButRejectsUnlistedPrivateAndReservedAddresses()
    {
        AccountHealthProbeHttpOptions options =
            AccountHealthProbeHttpOptions.FromConfiguration(
                new ConfigurationBuilder().Build(),
                "Production");
        Uri endpoint = new("https://upstream.example.com/v1/models");

        Assert.True(UpstreamAddressClassifier.IsAllowed(
            endpoint,
            IPAddress.Parse("8.8.8.8"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
        Assert.True(UpstreamAddressClassifier.IsAllowed(
            endpoint,
            IPAddress.Parse("2606:4700:4700::1111"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            endpoint,
            IPAddress.Parse("10.20.30.40"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            endpoint,
            IPAddress.Parse("172.16.1.2"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            endpoint,
            IPAddress.Parse("192.168.1.2"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            endpoint,
            IPAddress.Parse("fd12:3456::20"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            endpoint,
            IPAddress.Parse("192.0.2.20"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
    }

    [Fact]
    public void PrivateHttpsRequiresTheExactNormalizedAuthorityAndCidr()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(
                StringComparer.Ordinal)
            {
                ["Supply:Health:PrivateEgressRules:0"] =
                    "https://upstream.internal.example:8443|10.20.0.0/16",
            })
            .Build();
        AccountHealthProbeHttpOptions options =
            AccountHealthProbeHttpOptions.FromConfiguration(
                configuration,
                "Production");

        Assert.Single(options.PrivateEgressRules);
        Assert.True(UpstreamAddressClassifier.IsAllowed(
            new Uri(
                "https://upstream.internal.example:8443/v1/models"),
            IPAddress.Parse("10.20.30.40"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            new Uri("https://another.internal.example:8443/v1/models"),
            IPAddress.Parse("10.20.30.40"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            new Uri(
                "https://upstream.internal.example:443/v1/models"),
            IPAddress.Parse("10.20.30.40"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            new Uri(
                "https://upstream.internal.example:8443/v1/models"),
            IPAddress.Parse("10.21.30.40"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
    }

    [Fact]
    public void PrivateIpv6RuleUsesBracketSafeAuthorityAndStillRejectsLoopback()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(
                StringComparer.Ordinal)
            {
                ["Supply:Health:PrivateEgressRules:0"] =
                    "https://[fd12:3456::10]|fd12:3456::/32",
            })
            .Build();
        AccountHealthProbeHttpOptions options =
            AccountHealthProbeHttpOptions.FromConfiguration(
                configuration,
                "Production");

        Assert.True(UpstreamAddressClassifier.IsAllowed(
            new Uri("https://[fd12:3456::10]/v1/models"),
            IPAddress.Parse("fd12:3456::20"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            new Uri("https://[fd12:3456::10]/v1/models"),
            IPAddress.IPv6Loopback,
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            new Uri("https://[fd12:3457::10]/v1/models"),
            IPAddress.Parse("fd12:3456::20"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
    }

    [Fact]
    public void MixedDnsAnswersRejectTheWholeResolutionWhenAnyAnswerIsForbidden()
    {
        AccountHealthProbeHttpOptions defaultOptions =
            AccountHealthProbeHttpOptions.FromConfiguration(
                new ConfigurationBuilder().Build(),
                "Production");
        Uri endpoint = new(
            "https://upstream.internal.example:8443/v1/models");

        Assert.False(UpstreamAddressClassifier.AreAllAllowed(
            endpoint,
            [
                IPAddress.Parse("8.8.8.8"),
                IPAddress.Parse("10.20.30.40"),
            ],
            defaultOptions));

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(
                StringComparer.Ordinal)
            {
                ["Supply:Health:PrivateEgressRules:0"] =
                    "https://upstream.internal.example:8443|10.20.0.0/16",
            })
            .Build();
        AccountHealthProbeHttpOptions privateOptions =
            AccountHealthProbeHttpOptions.FromConfiguration(
                configuration,
                "Production");
        Assert.True(UpstreamAddressClassifier.AreAllAllowed(
            endpoint,
            [
                IPAddress.Parse("8.8.8.8"),
                IPAddress.Parse("10.20.30.40"),
            ],
            privateOptions));
    }

    [Fact]
    public void PrivateEgressRulesRejectSemanticDuplicates()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(
                StringComparer.Ordinal)
            {
                ["Supply:Health:PrivateEgressRules:0"] =
                    "https://upstream.internal.example|10.20.0.0/16",
                ["Supply:Health:PrivateEgressRules:1"] =
                    "https://upstream.internal.example/|10.20.0.0/16",
            })
            .Build();

        _ = Assert.Throws<InvalidOperationException>(() =>
            AccountHealthProbeHttpOptions.FromConfiguration(
                configuration,
                "Production"));
    }

    [Fact]
    public void NonByteAlignedPrivateCidrIsMaskedWithoutOverflow()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(
                StringComparer.Ordinal)
            {
                ["Supply:Health:PrivateEgressRules:0"] =
                    "https://upstream.internal.example|10.0.0.0/9",
            })
            .Build();
        AccountHealthProbeHttpOptions options =
            AccountHealthProbeHttpOptions.FromConfiguration(
                configuration,
                "Production");
        Uri endpoint = new(
            "https://upstream.internal.example/v1/models");

        Assert.True(UpstreamAddressClassifier.IsAllowed(
            endpoint,
            IPAddress.Parse("10.64.0.1"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));
        Assert.False(UpstreamAddressClassifier.IsAllowed(
            endpoint,
            IPAddress.Parse("10.128.0.1"),
            options.AllowLoopbackHttp,
            options.PrivateEgressRules));

        IConfiguration overlyBroad = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(
                StringComparer.Ordinal)
            {
                ["Supply:Health:PrivateEgressRules:0"] =
                    "https://upstream.internal.example|10.0.0.0/7",
            })
            .Build();
        _ = Assert.Throws<InvalidOperationException>(() =>
            AccountHealthProbeHttpOptions.FromConfiguration(
                overlyBroad,
                "Production"));
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("15", 15)]
    [InlineData("999999", 86400)]
    public async Task RetryAfterDeltaUsesStrictBoundedSeconds(
        string rawValue,
        int expectedSeconds)
    {
        AccountHealthProbeResult result = await ProbeRateLimitAsync(
            [rawValue]).ConfigureAwait(true);

        Assert.Equal(AccountHealthProbeOutcome.RateLimited, result.Outcome);
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            result.RetryAfter);
        Assert.Null(result.RetryAfterAt);
    }

    [Fact]
    public async Task RetryAfterImfFixdateRemainsAbsoluteForRedisTime()
    {
        DateTimeOffset retryAt = FirstObservation.AddMinutes(2);
        AccountHealthProbeResult result = await ProbeRateLimitAsync(
            [retryAt.ToString("r", CultureInfo.InvariantCulture)])
            .ConfigureAwait(true);

        Assert.Null(result.RetryAfter);
        Assert.Equal(retryAt, result.RetryAfterAt);
    }

    [Theory]
    [InlineData("Wed, 30 Jul 2026 08:02:00 UTC")]
    [InlineData("+15")]
    [InlineData("15.0")]
    public async Task RetryAfterRejectsNonCanonicalSyntax(string rawValue)
    {
        AccountHealthProbeResult result = await ProbeRateLimitAsync(
            [rawValue]).ConfigureAwait(true);

        Assert.Null(result.RetryAfter);
        Assert.Null(result.RetryAfterAt);
    }

    [Fact]
    public async Task RetryAfterRejectsMultipleHeaderValues()
    {
        AccountHealthProbeResult result = await ProbeRateLimitAsync(
            ["10", "20"]).ConfigureAwait(true);

        Assert.Null(result.RetryAfter);
        Assert.Null(result.RetryAfterAt);
    }

    [Fact]
    public void ReadinessStoreStartsWithABoundedStandbySnapshot()
    {
        SupplyHealthReadinessSummaryStore store = new(
            new FakeTimeProvider(FirstObservation));

        SupplyHealthReadinessSummary current = store.Current;

        Assert.Equal(FirstObservation, current.ObservedAt);
        Assert.Equal(SupplyHealthCycleStatus.Standby, current.CycleStatus);
        Assert.Equal(SupplyHealthFailureCode.NotOwner, current.FailureCode);
        Assert.Equal(0, current.AccountsSeen);
        Assert.Equal(0, current.ProbeEligibleCount);
        Assert.Equal(0, current.AttemptedCount);
        Assert.Equal(0, current.SucceededCount);
        Assert.Equal(0, current.FailedCount);
    }

    [Fact]
    public void ReadinessStoreRetainsOnlyTheLatestFixedShapeSnapshot()
    {
        SupplyHealthReadinessSummaryStore store = new(
            new FakeTimeProvider(FirstObservation));
        SupplyHealthReadinessSummary first = Summary(
            FirstObservation,
            SupplyHealthCycleStatus.Partial,
            SupplyHealthFailureCode.UpstreamProbeFailed,
            1);
        SupplyHealthReadinessSummary latest = Summary(
            FirstObservation.AddSeconds(30),
            SupplyHealthCycleStatus.Succeeded,
            SupplyHealthFailureCode.None,
            2);

        store.Update(first);
        store.Update(latest);

        Assert.Same(latest, store.Current);
        Assert.All(
            typeof(SupplyHealthReadinessSummary).GetProperties(),
            property => Assert.True(
                property.PropertyType == typeof(DateTimeOffset)
                || property.PropertyType == typeof(SupplyHealthCycleStatus)
                || property.PropertyType == typeof(SupplyHealthFailureCode)
                || property.PropertyType == typeof(int),
                $"Readiness property '{property.Name}' is not bounded."));
    }

    private static SupplyHealthReadinessSummary Summary(
        DateTimeOffset observedAt,
        SupplyHealthCycleStatus status,
        SupplyHealthFailureCode failureCode,
        int count) =>
        new(
            observedAt,
            status,
            failureCode,
            AccountsSeen: count,
            UnknownCount: count,
            HealthyCount: count,
            DegradedCount: count,
            CoolingCount: count,
            UnhealthyCount: count,
            AuthBlockedCount: count,
            ProbeEligibleCount: count,
            AttemptedCount: count,
            SucceededCount: count,
            FailedCount: count);

    private static async ValueTask<AccountHealthProbeResult>
        ProbeRateLimitAsync(string[] retryAfterValues)
    {
        FakeTimeProvider timeProvider = new(FirstObservation);
        ServiceCollection services = new();
        services.AddHttpClient(AccountHealthProbeHttpTransport.ClientName)
            .ConfigurePrimaryHttpMessageHandler(
                () => new StubHttpMessageHandler(() =>
                {
                    HttpResponseMessage response = new(
                        HttpStatusCode.TooManyRequests)
                    {
                        Content = new ByteArrayContent([]),
                    };
                    Assert.True(response.Headers.TryAddWithoutValidation(
                        "Retry-After",
                        retryAfterValues));
                    return response;
                }));
        using ServiceProvider provider = services.BuildServiceProvider();
        AccountHealthProbeHttpTransport transport = new(
            new(
                Timeout: TimeSpan.FromSeconds(10),
                MaximumResponseBytes: 1_048_576,
                AllowLoopbackHttp: false),
            timeProvider,
            provider.GetRequiredService<IHttpClientFactory>());
        return await transport.ProbeAsync(
            new Uri("https://upstream.example.test/v1"),
            Encoding.UTF8.GetBytes("deterministic-test-credential"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responseFactory =
            responseFactory
            ?? throw new ArgumentNullException(nameof(responseFactory));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responseFactory());
        }
    }
}
