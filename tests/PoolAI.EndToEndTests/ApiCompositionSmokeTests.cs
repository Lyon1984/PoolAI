using System.Net;

namespace PoolAI.EndToEndTests;

public sealed class ApiCompositionSmokeTests : IAsyncDisposable
{
    private readonly PoolAiApiFactory factory = new();

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpointsReportSuccessForTheM0Composition(string path)
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PublicRegistrationRouteIsAbsent()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/register",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void InvalidTimezonePreventsTheApiHostFromStarting()
    {
        using InvalidTimeZoneApiFactory invalidFactory = new();

        Exception exception = Assert.ThrowsAny<Exception>(() => invalidFactory.CreateClient());

        Assert.Contains("App:TimeZone", exception.ToString(), StringComparison.Ordinal);
    }

    public ValueTask DisposeAsync() => factory.DisposeAsync();
}
