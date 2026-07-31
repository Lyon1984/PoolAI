using Microsoft.Extensions.Configuration;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.GroupQuota.Infrastructure;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class QuotaMutationDenialRateLimiterTests
{
    [Fact]
    public async Task AdapterUsesOneActorScopeAndMapsEveryCounterDisposition()
    {
        EntityId actorId = new(Guid.Parse("01900000-0000-7000-8000-000000000123"));
        RecordingCounter counter = new();
        OperationsQuotaMutationDenialRateLimiter limiter = new(
            counter,
            new QuotaMutationDenialRateLimitOptions(5));

        counter.Next = FixedWindowCounterResult.Allowed(1);
        QuotaMutationDenialRateLimitDecision allowed = await limiter.AcquireAsync(
            actorId,
            CancellationToken.None);
        Assert.Equal(QuotaMutationDenialRateLimitDecision.Allowed, allowed);
        Assert.Equal(
            new FixedWindowCounterRequest(
                "rate:quota-denial:v1:{01900000-0000-7000-8000-000000000123}",
                5),
            counter.LastRequest);

        counter.Next = FixedWindowCounterResult.Rejected(
            6,
            TimeSpan.FromMilliseconds(1_001));
        QuotaMutationDenialRateLimitDecision rejected = await limiter.AcquireAsync(
            actorId,
            CancellationToken.None);
        Assert.Equal(QuotaMutationDenialRateLimitDisposition.Rejected, rejected.Disposition);
        Assert.Equal(2, rejected.RetryAfterSeconds);

        counter.Next = FixedWindowCounterResult.Unavailable;
        Assert.Equal(
            QuotaMutationDenialRateLimitDecision.Unavailable,
            await limiter.AcquireAsync(actorId, CancellationToken.None));
    }

    [Fact]
    public async Task AdapterRejectsInvalidInputsAndDependencies()
    {
        RecordingCounter counter = new();
        QuotaMutationDenialRateLimitOptions options = new(5);
        Assert.Throws<ArgumentNullException>(() =>
            _ = new OperationsQuotaMutationDenialRateLimiter(null!, options));
        Assert.Throws<ArgumentNullException>(() =>
            _ = new OperationsQuotaMutationDenialRateLimiter(counter, null!));

        OperationsQuotaMutationDenialRateLimiter limiter = new(counter, options);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await limiter.AcquireAsync(default, CancellationToken.None).ConfigureAwait(true));
    }

    [Fact]
    public void OptionsUseTheFrozenDefaultAndBounds()
    {
        IConfiguration empty = new ConfigurationBuilder().Build();
        Assert.Equal(
            5,
            QuotaMutationDenialRateLimitOptions.FromConfiguration(empty)
                .AttemptsPerMinute);

        foreach (int valid in new[] { 1, 5, 20 })
        {
            IConfiguration configuration = Configuration(valid);
            Assert.Equal(
                valid,
                QuotaMutationDenialRateLimitOptions.FromConfiguration(configuration)
                    .AttemptsPerMinute);
        }

        foreach (int invalid in new[] { 0, 21 })
        {
            Assert.Throws<InvalidOperationException>(() =>
                QuotaMutationDenialRateLimitOptions.FromConfiguration(
                    Configuration(invalid)));
        }
    }

    private static IConfiguration Configuration(int limit) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Quota:DeniedMutationAttemptsPerMinute"] = limit.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

    private sealed class RecordingCounter : IFixedWindowCounter
    {
        internal FixedWindowCounterResult Next { get; set; } =
            FixedWindowCounterResult.Allowed(1);

        internal FixedWindowCounterRequest? LastRequest { get; private set; }

        public ValueTask<FixedWindowCounterResult> IncrementAsync(
            FixedWindowCounterRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return ValueTask.FromResult(Next);
        }
    }
}
