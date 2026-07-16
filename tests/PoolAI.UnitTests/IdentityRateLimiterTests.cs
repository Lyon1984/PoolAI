using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Infrastructure;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class IdentityRateLimiterTests
{
    private const string IpKey = "rate:password-reset:v1:{41aa02b92bf8238ab208f319f0d98325}";
    private const string UnknownIpKey = "rate:password-reset:v1:{786b6b8e43451b5bdf36df360562021d}";
    private const string AccountKey = "rate:password-reset:v1:{1e7ec98eb84384cfcae8646905a6eaff}";

    [Fact]
    public async Task ForgotUsesExactCanonicalKeysInIpThenAccountOrder()
    {
        QueueCounter counter = new(
            FixedWindowCounterResult.Allowed(1),
            FixedWindowCounterResult.Allowed(1));
        OperationsPasswordResetRateLimiter limiter = CreateLimiter(counter);

        PasswordResetRateLimitDecision result = await limiter.CheckForgotAsync(
            "192.0.2.10",
            "person@example.test",
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordResetRateLimitDisposition.Allowed, result.Disposition);
        Assert.Null(result.RetryAfterSeconds);
        Assert.Collection(
            counter.Requests,
            request => Assert.Equal(new FixedWindowCounterRequest(IpKey, 5), request),
            request => Assert.Equal(new FixedWindowCounterRequest(AccountKey, 3), request));
    }

    [Theory]
    [MemberData(nameof(RejectedOrUnavailable))]
    public async Task ForgotStopsBeforeAccountWhenIpDoesNotAllow(
        FixedWindowCounterResult counterResult,
        PasswordResetRateLimitDisposition expected)
    {
        QueueCounter counter = new(counterResult);
        OperationsPasswordResetRateLimiter limiter = CreateLimiter(counter);

        PasswordResetRateLimitDecision result = await limiter.CheckForgotAsync(
            "192.0.2.10",
            "person@example.test",
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Disposition);
        Assert.Collection(
            counter.Requests,
            request => Assert.Equal(new FixedWindowCounterRequest(IpKey, 5), request));
    }

    [Fact]
    public async Task ForgotRetainsConsumedIpCountWhenAccountRejects()
    {
        QueueCounter counter = new(
            FixedWindowCounterResult.Allowed(1),
            FixedWindowCounterResult.Rejected(4, TimeSpan.FromSeconds(30)));
        OperationsPasswordResetRateLimiter limiter = CreateLimiter(counter);

        PasswordResetRateLimitDecision result = await limiter.CheckForgotAsync(
            "192.0.2.10",
            "person@example.test",
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordResetRateLimitDisposition.Rejected, result.Disposition);
        Assert.Equal(30, result.RetryAfterSeconds);
        Assert.Equal(2, counter.Requests.Count);
    }

    [Fact]
    public async Task AdminUsesOnlyExactAccountKey()
    {
        QueueCounter counter = new(FixedWindowCounterResult.Allowed(1));
        OperationsPasswordResetRateLimiter limiter = CreateLimiter(counter);

        PasswordResetRateLimitDecision result = await limiter.CheckAdminAsync(
            "person@example.test",
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordResetRateLimitDisposition.Allowed, result.Disposition);
        Assert.Collection(
            counter.Requests,
            request => Assert.Equal(new FixedWindowCounterRequest(AccountKey, 3), request));
    }

    [Fact]
    public async Task Ipv4MappedAddressUsesSameCanonicalNetworkBytes()
    {
        QueueCounter counter = new(
            FixedWindowCounterResult.Allowed(1),
            FixedWindowCounterResult.Allowed(1));
        OperationsPasswordResetRateLimiter limiter = CreateLimiter(counter);

        await limiter.CheckForgotAsync(
            "::ffff:192.0.2.10",
            "person@example.test",
            TestContext.Current.CancellationToken);

        Assert.Equal(IpKey, counter.Requests[0].KeyBase);
    }

    [Fact]
    public async Task AbsentIpUsesOnlyTheFrozenUnknownSentinel()
    {
        QueueCounter counter = new(
            FixedWindowCounterResult.Allowed(1),
            FixedWindowCounterResult.Allowed(1));
        OperationsPasswordResetRateLimiter limiter = CreateLimiter(counter);

        await limiter.CheckForgotAsync(
            string.Empty,
            "person@example.test",
            TestContext.Current.CancellationToken);

        Assert.Equal(UnknownIpKey, counter.Requests[0].KeyBase);
    }

    [Fact]
    public async Task MalformedNonEmptyIpFailsClosedWithoutConsumingAKey()
    {
        QueueCounter counter = new();
        OperationsPasswordResetRateLimiter limiter = CreateLimiter(counter);

        PasswordResetRateLimitDecision decision = await limiter.CheckForgotAsync(
            "not-an-ip",
            "person@example.test",
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordResetRateLimitDisposition.Unavailable, decision.Disposition);
        Assert.Equal(1, decision.RetryAfterSeconds);
        Assert.Empty(counter.Requests);
    }

    [Fact]
    public async Task RejectedRetryAfterRoundsUpToWholeSeconds()
    {
        QueueCounter counter = new(
            FixedWindowCounterResult.Rejected(6, TimeSpan.FromMilliseconds(1_001)));
        OperationsPasswordResetRateLimiter limiter = CreateLimiter(counter);

        PasswordResetRateLimitDecision result = await limiter.CheckAdminAsync(
            "person@example.test",
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordResetRateLimitDisposition.Rejected, result.Disposition);
        Assert.Equal(2, result.RetryAfterSeconds);
    }

    public static TheoryData<FixedWindowCounterResult, PasswordResetRateLimitDisposition>
        RejectedOrUnavailable() => new()
        {
            {
                FixedWindowCounterResult.Rejected(6, TimeSpan.FromSeconds(30)),
                PasswordResetRateLimitDisposition.Rejected
            },
            {
                FixedWindowCounterResult.Unavailable,
                PasswordResetRateLimitDisposition.Unavailable
            },
        };

    private static OperationsPasswordResetRateLimiter CreateLimiter(IFixedWindowCounter counter) =>
        new OperationsPasswordResetRateLimiter(
            counter,
            new PasswordResetRateLimitOptions(new byte[32], 5, 3));

    private sealed class QueueCounter(params FixedWindowCounterResult[] results)
        : IFixedWindowCounter
    {
        private readonly Queue<FixedWindowCounterResult> _results = new(results);

        internal List<FixedWindowCounterRequest> Requests { get; } = [];

        public ValueTask<FixedWindowCounterResult> IncrementAsync(
            FixedWindowCounterRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(_results.Dequeue());
        }
    }
}
