using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure;
using PoolAI.Modules.Operations.Infrastructure.Redis;

namespace PoolAI.UnitTests;

// Governing contract: docs/runtime/redis-contract.md, sections 2-4.
public sealed class RedisCoordinationBoundaryTests
{
    private const string AccountId = "018f3a4b-5c6d-7e8f-9123-456789abcdef";
    private const string LeaseKey =
        "lease:account:v1:{018f3a4b-5c6d-7e8f-9123-456789abcdef}";
    private const string StickyKey =
        "sticky:v1:{018f3a4b-5c6d-7e8f-9123-456789abcdef}:{0123456789abcdef0123456789abcdef}";
    private const string Owner = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void CanonicalLeaseStickyAndOwnerFormatsAreAccepted()
    {
        RedisCoordinationKeyGuard.ValidateAccountLease(LeaseKey);
        RedisCoordinationKeyGuard.ValidateSticky(StickyKey);
        RedisCoordinationKeyGuard.ValidateOwner(Owner);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("lease:account:v2:{018f3a4b-5c6d-7e8f-9123-456789abcdef}")]
    [InlineData("lease:account:v1:{018f3a4b-5c6d-7e8f-9123-456789abcdef")]
    [InlineData("lease:account:v1:{018F3A4B-5C6D-7E8F-9123-456789ABCDEF}")]
    [InlineData("lease:account:v1:{not-a-guid}")]
    public void InvalidLeaseKeyFormatsAreRejected(string keyBase)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => RedisCoordinationKeyGuard.ValidateAccountLease(keyBase));

        Assert.Equal("keyBase", exception.ParamName);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(
        "sticky:v2:{018f3a4b-5c6d-7e8f-9123-456789abcdef}:{0123456789abcdef0123456789abcdef}")]
    [InlineData(
        "sticky:v1:{018f3a4b-5c6d-7e8f-9123-456789abcdef}:{0123456789abcdef0123456789abcdef")]
    [InlineData("sticky:v1:{018f3a4b-5c6d-7e8f-9123-456789abcdef}")]
    [InlineData(
        "sticky:v1:{018F3A4B-5C6D-7E8F-9123-456789ABCDEF}:{0123456789abcdef0123456789abcdef}")]
    [InlineData(
        "sticky:v1:{018f3a4b-5c6d-7e8f-9123-456789abcdef}:{0123456789abcdef0123456789abcde}")]
    [InlineData(
        "sticky:v1:{018f3a4b-5c6d-7e8f-9123-456789abcdef}:{0123456789abcdef0123456789abcdeg}")]
    public void InvalidStickyKeyFormatsAreRejected(string keyBase)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => RedisCoordinationKeyGuard.ValidateSticky(keyBase));

        Assert.Equal("keyBase", exception.ParamName);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    public void InvalidLeaseOwnerFormatsAreRejected(string owner)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => RedisCoordinationKeyGuard.ValidateOwner(owner));

        Assert.Equal("owner", exception.ParamName);
    }

    [Fact]
    public void LeaseRequestFormattingRedactsOwnerAndKeyMaterial()
    {
        CoordinationLeaseAcquireRequest acquire =
            new(LeaseKey, Owner, Limit: 10);
        CoordinationLeaseOwner owner = new(LeaseKey, Owner);

        Assert.Equal(nameof(CoordinationLeaseAcquireRequest), acquire.ToString());
        Assert.Equal(nameof(CoordinationLeaseOwner), owner.ToString());
        Assert.DoesNotContain(AccountId, acquire.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Owner, acquire.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AccountId, owner.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Owner, owner.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LeaseRenewFactoriesPreserveOnlyTheirContractState()
    {
        DateTimeOffset expiresAt =
            new(2026, 7, 30, 12, 34, 56, TimeSpan.Zero);

        CoordinationLeaseRenewResult renewed =
            CoordinationLeaseRenewResult.Renewed(expiresAt);
        CoordinationLeaseRenewResult lost =
            CoordinationLeaseRenewResult.Lost;
        CoordinationLeaseRenewResult unavailable =
            CoordinationLeaseRenewResult.Unavailable;

        Assert.Equal(CoordinationLeaseRenewDisposition.Renewed, renewed.Disposition);
        Assert.Equal(expiresAt, renewed.ExpiresAt);
        Assert.Equal(CoordinationLeaseRenewDisposition.Lost, lost.Disposition);
        Assert.Equal(default, lost.ExpiresAt);
        Assert.Equal(
            CoordinationLeaseRenewDisposition.Unavailable,
            unavailable.Disposition);
        Assert.Equal(default, unavailable.ExpiresAt);
    }

    [Fact]
    public async Task StickyStoreRejectsWrongTtlAndOversizedValueBeforeRedisIo()
    {
        RuntimeDependencyOptions options = new(
            "127.0.0.1:6379",
            "poolai:r1:unit:",
            TimeSpan.FromSeconds(1));
        await using RedisConnectionProvider connections = new(options);
        RedisCoordinationValueStore store = new(connections, options);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.GetAndRefreshAsync(
                StickyKey,
                TimeSpan.FromMinutes(59),
                TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SetAsync(
                StickyKey,
                new string('x', 1_025),
                TimeSpan.FromMinutes(60),
                TestContext.Current.CancellationToken).AsTask());
    }
}
