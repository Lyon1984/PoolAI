using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class DeliveryRetryPolicyTests
{
    [Fact]
    public void RetryDelayIsExponentialJitteredAndBounded()
    {
        DeliveryRetryPolicy policy = new(
            4,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(1), policy.Decide(1, 0).RetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(2.2), policy.Decide(2, 0.1).RetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(3), policy.Decide(3, 0.1).RetryDelay);
        Assert.True(policy.Decide(4, 0).IsDead);
    }

    [Fact]
    public void InvalidAttemptOrJitterIsRejected()
    {
        DeliveryRetryPolicy policy = new(
            2,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Decide(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Decide(1, -0.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Decide(1, 0.11));
    }
}
