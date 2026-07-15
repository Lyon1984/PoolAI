using System.Net;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.EndToEndTests;

public sealed class RuntimeDependencyHealthTests
{
    [Theory]
    [InlineData("dependency_unavailable")]
    [InlineData("schema_manifest_incompatible")]
    [InlineData("redis_manifest_incompatible")]
    public async Task RuntimeDependencyFailureAffectsReadinessButNotLivenessAndRecovers(
        string failureCode)
    {
        using PoolAiApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        factory.DependencyReadiness.Result = new RuntimeDependencyReadiness(false, failureCode);

        using HttpResponseMessage notReady = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage live = await client.GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        factory.DependencyReadiness.Result = new RuntimeDependencyReadiness(true, null);
        using HttpResponseMessage recovered = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }
}
