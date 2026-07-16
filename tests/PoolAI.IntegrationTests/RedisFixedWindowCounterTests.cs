using Microsoft.Extensions.DependencyInjection;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class RedisFixedWindowCounterTests(PostgresRuntimeFixture fixture)
{
    private readonly PostgresRuntimeFixture _fixture =
        fixture ?? throw new ArgumentNullException(nameof(fixture));

    [Fact]
    [Trait("Category", "Redis")]
    public async Task FixedWindowUsesRegisteredScriptAndRejectsAfterTheLimit()
    {
        IFixedWindowCounter counter = _fixture.ApiServices
            .GetRequiredService<IFixedWindowCounter>();
        string scope = Convert.ToHexStringLower(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        FixedWindowCounterRequest request = new(
            $"rate:password-reset:v1:{{{scope}}}",
            Limit: 2);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        FixedWindowCounterResult first = await counter
            .IncrementAsync(request, cancellationToken)
            .ConfigureAwait(true);
        FixedWindowCounterResult second = await counter
            .IncrementAsync(request, cancellationToken)
            .ConfigureAwait(true);
        FixedWindowCounterResult rejected = await counter
            .IncrementAsync(request, cancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(FixedWindowCounterDisposition.Allowed, first.Disposition);
        Assert.Equal(1, first.Current);
        Assert.Equal(FixedWindowCounterDisposition.Allowed, second.Disposition);
        Assert.Equal(2, second.Current);
        Assert.Equal(FixedWindowCounterDisposition.Rejected, rejected.Disposition);
        Assert.Equal(3, rejected.Current);
        Assert.InRange(rejected.RetryAfter, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(1));
    }
}
