using System.Numerics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

namespace PoolAI.UnitTests;

public sealed class GroupQuotaReconciliationFactReaderTests
{
    private static readonly DateTimeOffset CheckedAt = new(
        2030,
        1,
        2,
        3,
        4,
        5,
        TimeSpan.Zero);

    [Fact]
    public void SnapshotAndCandidatePreserveTheLosslessPublicAbi()
    {
        EntityId groupId = EntityId.New();
        EntityId periodId = EntityId.New();
        GroupQuotaReconciliationFactSnapshot snapshot = new(
            groupId,
            periodId,
            CheckpointSourceEventSequence: 17,
            LedgerTotalTokens: new BigInteger(9_007_199_254_740_991L),
            LedgerConsumedTokens: new BigInteger(37),
            LedgerReservedTokens: new BigInteger(11),
            FactConsumedTokens: new BigInteger(37),
            PendingReservationTokens: new BigInteger(11),
            PendingReservationCount: 2,
            OverdueReservationCount: 1,
            OldestOverdueAt: CheckedAt.AddMinutes(-2),
            ExpectedConsumedAtCheckpoint: new BigInteger(31),
            CheckpointBelongsToGroup: true,
            LatestPeriodEventSequence: 19,
            LatestPeriodEventOccurredAt: CheckedAt.AddMinutes(-1),
            EventChainConsistent: true,
            FactEventCoverageConsistent: true,
            LatestEventMatchesLedger: true,
            OverageTokens: BigInteger.Zero,
            CheckedAt: CheckedAt,
            IsCurrentPeriod: true,
            FirstPeriodEventSequence: 11,
            LatestGroupEventSequence: 23,
            PeriodEventCount: 9);
        GroupQuotaReconciliationCandidate candidate = new(groupId, periodId);

        Assert.Equal(groupId, snapshot.GroupId);
        Assert.Equal(periodId, snapshot.PeriodId);
        Assert.Equal(17, snapshot.CheckpointSourceEventSequence);
        Assert.Equal(new BigInteger(9_007_199_254_740_991L), snapshot.LedgerTotalTokens);
        Assert.Equal(new BigInteger(37), snapshot.LedgerConsumedTokens);
        Assert.Equal(new BigInteger(11), snapshot.LedgerReservedTokens);
        Assert.Equal(new BigInteger(37), snapshot.FactConsumedTokens);
        Assert.Equal(new BigInteger(11), snapshot.PendingReservationTokens);
        Assert.Equal(2, snapshot.PendingReservationCount);
        Assert.Equal(1, snapshot.OverdueReservationCount);
        Assert.Equal(CheckedAt.AddMinutes(-2), snapshot.OldestOverdueAt);
        Assert.Equal(new BigInteger(31), snapshot.ExpectedConsumedAtCheckpoint);
        Assert.True(snapshot.CheckpointBelongsToGroup);
        Assert.Equal(19, snapshot.LatestPeriodEventSequence);
        Assert.Equal(CheckedAt.AddMinutes(-1), snapshot.LatestPeriodEventOccurredAt);
        Assert.True(snapshot.EventChainConsistent);
        Assert.True(snapshot.FactEventCoverageConsistent);
        Assert.True(snapshot.LatestEventMatchesLedger);
        Assert.Equal(BigInteger.Zero, snapshot.OverageTokens);
        Assert.Equal(CheckedAt, snapshot.CheckedAt);
        Assert.True(snapshot.IsCurrentPeriod);
        Assert.Equal(11, snapshot.FirstPeriodEventSequence);
        Assert.Equal(23, snapshot.LatestGroupEventSequence);
        Assert.Equal(9, snapshot.PeriodEventCount);
        Assert.Equal(groupId, candidate.GroupId);
        Assert.Equal(periodId, candidate.PeriodId);
    }

    [Fact]
    public async Task InvalidReadBoundaryFailsBeforeOpeningPostgres()
    {
        PostgresGroupQuotaReconciliationFactReader reader = new();

        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(
            default,
            null,
            checkpointSourceEventSequence: 0,
            null!,
            TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(
            EntityId.New(),
            null,
            checkpointSourceEventSequence: -1,
            null!,
            TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task InvalidCandidatePageBoundFailsBeforeOpeningPostgres(int maximumCount)
    {
        PostgresGroupQuotaReconciliationFactReader reader = new();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => reader.ListCurrentCandidatesAsync(
                afterGroupId: null,
                maximumCount,
                null!,
                TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task InvalidPeriodSequenceIdentityAndCursorFailBeforePostgres()
    {
        PostgresGroupQuotaReconciliationFactReader reader = new();
        EntityId groupId = EntityId.New();
        EntityId periodId = EntityId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => reader
            .ListPeriodSourceEventSequencesAsync(
                default,
                periodId,
                1,
                0,
                1,
                null!,
                cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => reader
            .ListPeriodSourceEventSequencesAsync(
                groupId,
                default,
                1,
                0,
                1,
                null!,
                cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader
            .ListPeriodSourceEventSequencesAsync(
                groupId,
                periodId,
                0,
                0,
                1,
                null!,
                cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader
            .ListPeriodSourceEventSequencesAsync(
                groupId,
                periodId,
                1,
                -1,
                1,
                null!,
                cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader
            .ListPeriodSourceEventSequencesAsync(
                groupId,
                periodId,
                1,
                2,
                1,
                null!,
                cancellationToken).AsTask());
    }

    [Fact]
    public async Task InvalidPeriodSequenceBoundAndContextFailBeforePostgres()
    {
        PostgresGroupQuotaReconciliationFactReader reader = new();
        EntityId groupId = EntityId.New();
        EntityId periodId = EntityId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader
            .ListPeriodSourceEventSequencesAsync(
                groupId,
                periodId,
                1,
                0,
                0,
                null!,
                cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader
            .ListPeriodSourceEventSequencesAsync(
                groupId,
                periodId,
                1,
                0,
                1001,
                null!,
                cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => reader
            .ListPeriodSourceEventSequencesAsync(
                groupId,
                periodId,
                1,
                0,
                1,
                null!,
                cancellationToken).AsTask());
    }

    [Fact]
    public void ModuleRegistersOneSingletonReconciliationFactReader()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Idempotency:RequestHashPepper"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddGroupQuotaModule();

        ServiceDescriptor descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType
                == typeof(IGroupQuotaReconciliationFactReader));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(
            typeof(PostgresGroupQuotaReconciliationFactReader),
            descriptor.ImplementationType);
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("invalid_cast")]
    [InlineData("overflow")]
    public void AbiFailuresAreNormalizedWithoutLeakingTheSourceMessage(string kind)
    {
        Exception source = kind switch
        {
            "argument" => new ArgumentException("sensitive", nameof(kind)),
            "invalid_cast" => new InvalidCastException("sensitive"),
            "overflow" => new OverflowException("sensitive"),
            _ => throw new InvalidOperationException("Unknown failure kind."),
        };

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ReadAbi<int>(
                () => throw source,
                "canonical ABI failure"));

        Assert.Equal("canonical ABI failure", failure.Message);
        Assert.Same(source, failure.InnerException);
        Assert.Equal(17, PostgresGroupQuotaReconciliationFactReader.ReadAbi(
            () => 17,
            "canonical ABI failure"));
        Assert.Throws<ArgumentNullException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ReadAbi<int>(
                null!,
                "canonical ABI failure"));
        Assert.Throws<ArgumentException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ReadAbi(
                () => 17,
                " "));
    }

    [Fact]
    public void CandidateAndSequenceValidatorsEnforceStrictKeysetOrder()
    {
        EntityId first = EntityId.New();
        EntityId second = EntityId.New();
        EntityId lower = StringComparer.Ordinal.Compare(
            first.Value.ToString("N"),
            second.Value.ToString("N")) < 0
            ? first
            : second;
        EntityId higher = lower == first ? second : first;
        GroupQuotaReconciliationCandidate candidate = new(higher, EntityId.New());

        PostgresGroupQuotaReconciliationFactReader.ValidateCandidate(candidate, null);
        PostgresGroupQuotaReconciliationFactReader.ValidateCandidate(candidate, lower);
        Assert.Throws<ArgumentException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ValidateCandidate(
                candidate with { GroupId = default },
                null));
        Assert.Throws<ArgumentException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ValidateCandidate(
                candidate with { PeriodId = default },
                null));
        Assert.Throws<InvalidOperationException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ValidateCandidate(
                candidate,
                candidate.GroupId));

        PostgresGroupQuotaReconciliationFactReader.ValidateSourceEventSequence(
            sourceEventSequence: 11,
            prior: 10,
            throughSourceEventSequence: 11);
        Assert.Throws<InvalidOperationException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ValidateSourceEventSequence(
                10,
                10,
                11));
        Assert.Throws<InvalidOperationException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ValidateSourceEventSequence(
                12,
                10,
                11));

        PostgresGroupQuotaReconciliationFactReader.ValidatePageCount(1, 1, "bound");
        InvalidOperationException pageFailure = Assert.Throws<InvalidOperationException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ValidatePageCount(2, 1, "bound"));
        Assert.Equal("bound", pageFailure.Message);
        PostgresGroupQuotaReconciliationFactReader.ValidateSingleSnapshot(false);
        Assert.Throws<InvalidOperationException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ValidateSingleSnapshot(true));
    }

    [Fact]
    public void SnapshotValidatorAcceptsCanonicalBoundaryVariants()
    {
        GroupQuotaReconciliationFactSnapshot snapshot = ValidSnapshot();
        PostgresGroupQuotaReconciliationFactReader.ValidateSnapshot(snapshot);
        PostgresGroupQuotaReconciliationFactReader.ValidateSnapshot(snapshot with
        {
            CheckpointSourceEventSequence = 0,
            ExpectedConsumedAtCheckpoint = BigInteger.Zero,
            PendingReservationTokens = BigInteger.Zero,
            PendingReservationCount = 0,
            OverdueReservationCount = 0,
            OldestOverdueAt = null,
        });
        PostgresGroupQuotaReconciliationFactReader.ValidateSnapshot(snapshot with
        {
            LedgerConsumedTokens = new BigInteger(110),
            OverageTokens = new BigInteger(10),
        });
    }

    [Fact]
    public void SnapshotValidatorRejectsNumericAndIdentityContradictions()
    {
        GroupQuotaReconciliationFactSnapshot valid = ValidSnapshot();
        BigInteger tooLarge = BigInteger.Pow(10, 78);
        Assert.Throws<ArgumentException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ValidateSnapshot(
                valid with { GroupId = default }));
        Assert.Throws<ArgumentException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ValidateSnapshot(
                valid with { PeriodId = default }));
        IReadOnlyList<GroupQuotaReconciliationFactSnapshot> invalid =
        [
            valid with { CheckpointSourceEventSequence = -1 },
            valid with { LedgerTotalTokens = BigInteger.Zero },
            valid with { LedgerTotalTokens = new BigInteger(9_007_199_254_740_992L) },
            valid with { LedgerConsumedTokens = BigInteger.MinusOne },
            valid with { LedgerConsumedTokens = tooLarge },
            valid with { LedgerReservedTokens = BigInteger.MinusOne },
            valid with { LedgerReservedTokens = tooLarge },
            valid with { FactConsumedTokens = BigInteger.MinusOne },
            valid with { FactConsumedTokens = tooLarge },
            valid with { PendingReservationTokens = BigInteger.MinusOne },
            valid with { PendingReservationTokens = tooLarge },
            valid with { PendingReservationCount = -1 },
            valid with { PendingReservationCount = 0 },
            valid with { PendingReservationTokens = BigInteger.Zero },
            valid with { OverdueReservationCount = -1 },
            valid with { OverdueReservationCount = 2 },
            valid with { ExpectedConsumedAtCheckpoint = BigInteger.MinusOne },
            valid with { ExpectedConsumedAtCheckpoint = tooLarge },
            valid with
            {
                CheckpointSourceEventSequence = 0,
                ExpectedConsumedAtCheckpoint = BigInteger.One,
            },
            valid with { LatestPeriodEventSequence = 0 },
            valid with { FirstPeriodEventSequence = 0 },
            valid with { FirstPeriodEventSequence = 20 },
            valid with { LatestGroupEventSequence = 18 },
            valid with { PeriodEventCount = 0 },
            valid with { OverageTokens = BigInteger.MinusOne },
            valid with { OverageTokens = tooLarge },
            valid with { OverageTokens = BigInteger.One },
        ];

        AssertInvalidSnapshots(invalid);
    }

    [Fact]
    public void SnapshotValidatorRejectsTemporalAndOverdueContradictions()
    {
        GroupQuotaReconciliationFactSnapshot valid = ValidSnapshot();
        IReadOnlyList<GroupQuotaReconciliationFactSnapshot> invalid =
        [
            valid with { CheckedAt = DateTimeOffset.UnixEpoch.AddTicks(-1) },
            valid with { CheckedAt = CheckedAt.ToOffset(TimeSpan.FromHours(1)) },
            valid with
            {
                LatestPeriodEventOccurredAt = DateTimeOffset.UnixEpoch.AddTicks(-1),
            },
            valid with
            {
                LatestPeriodEventOccurredAt = valid.LatestPeriodEventOccurredAt
                    .ToOffset(TimeSpan.FromHours(1)),
            },
            valid with { LatestPeriodEventOccurredAt = CheckedAt.AddTicks(1) },
            valid with { OverdueReservationCount = 0 },
            valid with { OldestOverdueAt = null },
            valid with { OldestOverdueAt = DateTimeOffset.UnixEpoch.AddTicks(-1) },
            valid with
            {
                OldestOverdueAt = valid.OldestOverdueAt!.Value
                    .ToOffset(TimeSpan.FromHours(1)),
            },
            valid with { OldestOverdueAt = CheckedAt.AddSeconds(-59) },
        ];

        AssertInvalidSnapshots(invalid);
    }

    private static void AssertInvalidSnapshots(
        IReadOnlyList<GroupQuotaReconciliationFactSnapshot> invalid)
    {
        Assert.All(invalid, snapshot => Assert.Throws<InvalidOperationException>(() =>
            PostgresGroupQuotaReconciliationFactReader.ValidateSnapshot(snapshot)));
    }

    private static GroupQuotaReconciliationFactSnapshot ValidSnapshot() => new(
        EntityId.New(),
        EntityId.New(),
        CheckpointSourceEventSequence: 17,
        LedgerTotalTokens: new BigInteger(100),
        LedgerConsumedTokens: new BigInteger(37),
        LedgerReservedTokens: new BigInteger(11),
        FactConsumedTokens: new BigInteger(37),
        PendingReservationTokens: new BigInteger(11),
        PendingReservationCount: 1,
        OverdueReservationCount: 1,
        OldestOverdueAt: CheckedAt.AddMinutes(-2),
        ExpectedConsumedAtCheckpoint: new BigInteger(31),
        CheckpointBelongsToGroup: true,
        LatestPeriodEventSequence: 19,
        LatestPeriodEventOccurredAt: CheckedAt.AddMinutes(-1),
        EventChainConsistent: true,
        FactEventCoverageConsistent: true,
        LatestEventMatchesLedger: true,
        OverageTokens: BigInteger.Zero,
        CheckedAt: CheckedAt,
        IsCurrentPeriod: true,
        FirstPeriodEventSequence: 11,
        LatestGroupEventSequence: 23,
        PeriodEventCount: 9);
}
