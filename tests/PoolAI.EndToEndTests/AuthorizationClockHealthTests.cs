using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoolAI.EndToEndTests;

public sealed class AuthorizationClockHealthTests
{
    [Fact]
    public async Task InRangeClockIsReadyAndLive()
    {
        await using PoolAiApiFactory factory = new();
        factory.NtpOffsetProbe.SetAvailable(TimeSpan.FromSeconds(5));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage ready = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage live = await client.GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task ExcessiveOffsetFailsOnlyReadinessAndRecovers()
    {
        await using PoolAiApiFactory factory = new();
        factory.NtpOffsetProbe.SetAvailable(TimeSpan.FromMilliseconds(5_001));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage failedReady = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage live = await client.GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken);
        HealthReport report = await CheckAuthorizationClockAsync(factory);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedReady.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(
            "clock_offset_exceeded",
            report.Entries["authorization-clock"].Description);

        factory.NtpOffsetProbe.SetAvailable(TimeSpan.Zero);
        using HttpResponseMessage recoveredReady = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, recoveredReady.StatusCode);
    }

    [Fact]
    public async Task UnavailableSourceFailsOnlyReadinessAndRecovers()
    {
        await using PoolAiApiFactory factory = new();
        factory.NtpOffsetProbe.SetUnavailable();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage failedReady = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage live = await client.GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken);
        HealthReport report = await CheckAuthorizationClockAsync(factory);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedReady.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(
            "ntp_source_unavailable",
            report.Entries["authorization-clock"].Description);

        factory.NtpOffsetProbe.SetAvailable(TimeSpan.Zero);
        using HttpResponseMessage recoveredReady = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, recoveredReady.StatusCode);
    }

    private static Task<HealthReport> CheckAuthorizationClockAsync(PoolAiApiFactory factory)
    {
        HealthCheckService healthChecks = factory.Services
            .GetRequiredService<HealthCheckService>();
        return healthChecks.CheckHealthAsync(
            static registration => string.Equals(
                registration.Name,
                "authorization-clock",
                StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
    }
}
