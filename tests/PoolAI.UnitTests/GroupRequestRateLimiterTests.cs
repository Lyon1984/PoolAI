using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Application;

namespace PoolAI.UnitTests;

// Governing contract: docs/runtime/redis-contract.md, "Rate Limit Lua v1".
public sealed class GroupRequestRateLimiterTests
{
    private static readonly EntityId GroupId = new(
        Guid.Parse("018f3a4b-5c6d-7e8f-9123-456789abcdef"));

    [Fact]
    public async Task AllowedUsesCanonicalGroupKeyAndConfiguredLimit()
    {
        RecordingCounter counter = new(FixedWindowCounterResult.Allowed(17));
        GroupRequestRateLimiter limiter = new(counter);

        Result<GroupRequestRateLimitPermit> result = await limiter.AcquireAsync(
            GroupId,
            requestsPerMinute: 120,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(17, result.Value.Current);
        FixedWindowCounterRequest request = Assert.Single(counter.Requests);
        Assert.Equal(
            new FixedWindowCounterRequest(
                "rate:group:v1:{018f3a4b-5c6d-7e8f-9123-456789abcdef}",
                Limit: 120,
                Increment: 1),
            request);
        Assert.Equal(TestContext.Current.CancellationToken, counter.CancellationToken);
    }

    [Fact]
    public async Task RejectedMapsStableErrorAndRoundsRetryAfterUp()
    {
        RecordingCounter counter = new(
            FixedWindowCounterResult.Rejected(
                current: 121,
                retryAfter: TimeSpan.FromMilliseconds(1_001)));
        GroupRequestRateLimiter limiter = new(counter);

        Result<GroupRequestRateLimitPermit> result = await limiter.AcquireAsync(
            GroupId,
            requestsPerMinute: 120,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("group_rate_limited", result.Error.Code);
        Assert.Equal(2, result.Error.RetryAfterSeconds);
        Assert.Single(counter.Requests);
    }

    [Fact]
    public async Task RejectedAlwaysReturnsPositiveRetryAfter()
    {
        RecordingCounter counter = new(
            FixedWindowCounterResult.Rejected(
                current: 121,
                retryAfter: TimeSpan.Zero));
        GroupRequestRateLimiter limiter = new(counter);

        Result<GroupRequestRateLimitPermit> result = await limiter.AcquireAsync(
            GroupId,
            requestsPerMinute: 120,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("group_rate_limited", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
    }

    [Fact]
    public async Task UnavailableFailsClosedWithCoordinationError()
    {
        RecordingCounter counter = new(FixedWindowCounterResult.Unavailable);
        GroupRequestRateLimiter limiter = new(counter);

        Result<GroupRequestRateLimitPermit> result = await limiter.AcquireAsync(
            GroupId,
            requestsPerMinute: 120,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Single(counter.Requests);
    }

    [Fact]
    public async Task UnknownCounterDispositionFailsClosed()
    {
        RecordingCounter counter = new(
            new FixedWindowCounterResult(
                (FixedWindowCounterDisposition)int.MaxValue,
                Current: 1,
                RetryAfter: TimeSpan.Zero));
        GroupRequestRateLimiter limiter = new(counter);

        Result<GroupRequestRateLimitPermit> result = await limiter.AcquireAsync(
            GroupId,
            requestsPerMinute: 120,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveLimitIsRejectedBeforeCoordination(int requestsPerMinute)
    {
        RecordingCounter counter = new(FixedWindowCounterResult.Allowed(1));
        GroupRequestRateLimiter limiter = new(counter);

        Result<GroupRequestRateLimitPermit> result = await limiter.AcquireAsync(
            GroupId,
            requestsPerMinute,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_request", result.Error.Code);
        Assert.Null(result.Error.RetryAfterSeconds);
        Assert.Empty(counter.Requests);
    }

    private sealed class RecordingCounter(FixedWindowCounterResult result)
        : IFixedWindowCounter
    {
        internal List<FixedWindowCounterRequest> Requests { get; } = [];

        internal CancellationToken CancellationToken { get; private set; }

        public ValueTask<FixedWindowCounterResult> IncrementAsync(
            FixedWindowCounterRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            CancellationToken = cancellationToken;
            return ValueTask.FromResult(result);
        }
    }
}
