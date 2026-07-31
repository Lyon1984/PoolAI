using System.Numerics;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.UnitTests;

// Governing contracts:
// - docs/README.md, D-001/D-002/D-004: Group-only quota ownership,
//   lossless Token facts, and distinct request/attempt identities.
// - docs/开发执行规格-v1.0.md, M3-E1..M3-E4 delivery boundaries.
public sealed class GroupQuotaAbstractionsContractTests
{
    private static readonly EntityId GroupId = Id(1);
    private static readonly EntityId PeriodId = Id(2);
    private static readonly EntityId RequestId = Id(3);
    private static readonly EntityId AttemptId = Id(4);
    private static readonly EntityId AccountId = Id(5);
    private static readonly EntityId ReservationId = Id(6);
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 31, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AttemptSettlementFactPreservesEveryPublishedUsageFact()
    {
        BigInteger inputTokens = BigInteger.Parse(
            "123456789012345678901234567890",
            System.Globalization.CultureInfo.InvariantCulture);
        BigInteger outputTokens = BigInteger.Parse(
            "987654321098765432109876543210",
            System.Globalization.CultureInfo.InvariantCulture);
        AttemptSettlementFact fact = new(
            AttemptId,
            RequestId,
            GroupId,
            AccountId,
            inputTokens,
            outputTokens,
            "upstream_usage",
            ObservedAt);

        Assert.Equal(AttemptId, fact.AttemptId);
        Assert.Equal(RequestId, fact.RequestId);
        Assert.Equal(GroupId, fact.GroupId);
        Assert.Equal(AccountId, fact.AccountId);
        Assert.Equal(inputTokens, fact.InputTokens);
        Assert.Equal(outputTokens, fact.OutputTokens);
        Assert.Equal("upstream_usage", fact.UsageSource);
        Assert.Equal(ObservedAt, fact.CompletedAt);
    }

    [Fact]
    public void GroupActivationResultPreservesResourceAndVersionFence()
    {
        GroupResourceSnapshot resource = new(
            GroupId,
            "Research",
            "Primary research Group",
            GroupLifecycle.Active,
            17,
            ObservedAt.AddDays(-1),
            ObservedAt);
        GroupActivationResult result = new(
            GroupId,
            GroupLifecycle.Active,
            17,
            resource);

        Assert.Equal(GroupId, result.GroupId);
        Assert.Equal(GroupLifecycle.Active, result.Lifecycle);
        Assert.Equal(17, result.Version);
        Assert.Same(resource, result.Resource);

        GroupActivationResult withoutResource = new(
            GroupId,
            GroupLifecycle.Disabled,
            18);
        Assert.Null(withoutResource.Resource);
    }

    [Fact]
    public void GroupSnapshotPreservesQuotaReadinessObservation()
    {
        GroupSnapshot snapshot = new(
            GroupId,
            GroupLifecycle.Disabled,
            21,
            HasCurrentQuotaPeriod: true,
            ObservedAt);

        Assert.Equal(GroupId, snapshot.GroupId);
        Assert.Equal(GroupLifecycle.Disabled, snapshot.Lifecycle);
        Assert.Equal(21, snapshot.Version);
        Assert.True(snapshot.HasCurrentQuotaPeriod);
        Assert.Equal(ObservedAt, snapshot.ObservedAt);
    }

    [Fact]
    public void QuotaSnapshotPreservesLosslessAuthorityState()
    {
        BigInteger total = BigInteger.Parse(
            "900719925474099199999999999999",
            System.Globalization.CultureInfo.InvariantCulture);
        BigInteger consumed = total - 200;
        BigInteger reserved = new(75);
        QuotaSnapshot snapshot = new(
            GroupId,
            PeriodId,
            total,
            consumed,
            reserved,
            34);

        Assert.Equal(GroupId, snapshot.GroupId);
        Assert.Equal(PeriodId, snapshot.PeriodId);
        Assert.Equal(total, snapshot.Total);
        Assert.Equal(consumed, snapshot.Consumed);
        Assert.Equal(reserved, snapshot.Reserved);
        Assert.Equal(34, snapshot.Version);
    }

    [Fact]
    public void ReservationHandlePreservesPeriodFenceAndLeaseDeadline()
    {
        DateTimeOffset leaseExpiresAt = ObservedAt.AddMinutes(5);
        ReservationHandle handle = new(
            ReservationId,
            PeriodId,
            leaseExpiresAt);

        Assert.Equal(ReservationId, handle.ReservationId);
        Assert.Equal(PeriodId, handle.PeriodId);
        Assert.Equal(leaseExpiresAt, handle.LeaseExpiresAt);
    }

    [Fact]
    public void ReserveQuotaCommandPreservesAttemptAndGroupBoundaries()
    {
        BigInteger estimatedTokens = BigInteger.Parse(
            "900719925474099112345678901234",
            System.Globalization.CultureInfo.InvariantCulture);
        ReserveQuotaCommand command = new(
            RequestId,
            AttemptId,
            GroupId,
            AccountId,
            estimatedTokens,
            IsStream: true);

        Assert.Equal(RequestId, command.RequestId);
        Assert.Equal(AttemptId, command.AttemptId);
        Assert.Equal(GroupId, command.GroupId);
        Assert.Equal(AccountId, command.AccountId);
        Assert.Equal(estimatedTokens, command.EstimatedTokens);
        Assert.True(command.IsStream);
    }

    private static EntityId Id(int suffix) => new(
        Guid.Parse($"018f3a4b-5c6d-7e8f-9123-{suffix:D12}"));
}
