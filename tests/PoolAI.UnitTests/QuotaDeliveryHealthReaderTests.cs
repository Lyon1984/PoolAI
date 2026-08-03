using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure.Persistence;

namespace PoolAI.UnitTests;

public sealed class QuotaDeliveryHealthReaderTests
{
    [Fact]
    public void SnapshotExposesOnlyTheFrozenPayloadFreeAbi()
    {
        DateTimeOffset checkedAt = new(
            2030,
            1,
            2,
            3,
            4,
            5,
            TimeSpan.Zero);
        QuotaDeliveryHealthSnapshot snapshot = new(
            originalCount: 7,
            missingOriginalCount: 1,
            duplicateOriginalCount: 2,
            pendingLineageCount: 1,
            processingLineageCount: 1,
            deadLineageCount: 1,
            expectedInboxReceiptCount: 5,
            missingInboxReceiptCount: 1,
            conflictingInboxReceiptCount: 1,
            oldestUnresolvedAgeSeconds: 125.5,
            blockingSourceEventSequence: 17,
            checkedAt);

        Assert.Equal(7, snapshot.OriginalCount);
        Assert.Equal(1, snapshot.MissingOriginalCount);
        Assert.Equal(2, snapshot.DuplicateOriginalCount);
        Assert.Equal(1, snapshot.PendingLineageCount);
        Assert.Equal(1, snapshot.ProcessingLineageCount);
        Assert.Equal(1, snapshot.DeadLineageCount);
        Assert.Equal(5, snapshot.ExpectedInboxReceiptCount);
        Assert.Equal(1, snapshot.MissingInboxReceiptCount);
        Assert.Equal(1, snapshot.ConflictingInboxReceiptCount);
        Assert.Equal(125.5, snapshot.OldestUnresolvedAgeSeconds);
        Assert.Equal(17, snapshot.BlockingSourceEventSequence);
        Assert.Equal(checkedAt, snapshot.CheckedAt);

        Type[] propertyTypes = typeof(QuotaDeliveryHealthSnapshot)
            .GetProperties()
            .Select(static property => property.PropertyType)
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                typeof(DateTimeOffset),
                typeof(double),
                typeof(long),
                typeof(long),
                typeof(long),
                typeof(long),
                typeof(long),
                typeof(long),
                typeof(long),
                typeof(long),
                typeof(long),
                typeof(long?),
            ],
            propertyTypes);
        Assert.All(
            typeof(QuotaDeliveryHealthSnapshot).GetProperties(),
            static property => Assert.False(property.CanWrite));
    }

    [Fact]
    public void ReaderPreservesTheFrozenParameterOrderAndTypes()
    {
        System.Reflection.MethodInfo read = Assert.Single(
            typeof(IQuotaDeliveryHealthReader).GetMethods());
        Assert.Equal(nameof(IQuotaDeliveryHealthReader.ReadAsync), read.Name);
        Assert.Equal(typeof(ValueTask<QuotaDeliveryHealthSnapshot>), read.ReturnType);
        Assert.Equal(
            [
                typeof(EntityId),
                typeof(IReadOnlyList<long>),
                typeof(long),
                typeof(IUnitOfWorkContext),
                typeof(CancellationToken),
            ],
            read.GetParameters().Select(static parameter => parameter.ParameterType));
        Assert.Equal(
            [
                "groupId",
                "expectedSourceEventSequences",
                "checkpointSourceEventSequence",
                "unitOfWorkContext",
                "cancellationToken",
            ],
            read.GetParameters().Select(static parameter => parameter.Name));
    }

    [Fact]
    public void SnapshotRejectsContradictoryLineageDiagnostics()
    {
        DateTimeOffset checkedAt = DateTimeOffset.UnixEpoch.AddDays(1);

        Assert.Throws<ArgumentException>(() => new QuotaDeliveryHealthSnapshot(
            originalCount: 1,
            missingOriginalCount: 0,
            duplicateOriginalCount: 0,
            pendingLineageCount: 1,
            processingLineageCount: 1,
            deadLineageCount: 0,
            oldestUnresolvedAgeSeconds: 1,
            blockingSourceEventSequence: 1,
            checkedAt));
        Assert.Throws<ArgumentException>(() => new QuotaDeliveryHealthSnapshot(
            originalCount: 1,
            missingOriginalCount: 0,
            duplicateOriginalCount: 0,
            pendingLineageCount: 0,
            processingLineageCount: 0,
            deadLineageCount: 0,
            oldestUnresolvedAgeSeconds: 1,
            blockingSourceEventSequence: null,
            checkedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QuotaDeliveryHealthSnapshot(
                originalCount: 1,
                missingOriginalCount: 0,
                duplicateOriginalCount: 0,
                pendingLineageCount: 1,
                processingLineageCount: 0,
                deadLineageCount: 0,
                oldestUnresolvedAgeSeconds: double.PositiveInfinity,
                blockingSourceEventSequence: 1,
                checkedAt));
    }

    [Fact]
    public void SnapshotRejectsContradictoryInboxDiagnostics()
    {
        DateTimeOffset checkedAt = DateTimeOffset.UnixEpoch.AddDays(1);

        Assert.Throws<ArgumentException>(() => InboxSnapshot(
            expected: 2,
            missing: 0,
            conflicting: 0,
            blocking: null,
            checkedAt,
            originalCount: 1));
        Assert.Throws<ArgumentException>(() => InboxSnapshot(
            expected: 1,
            missing: 1,
            conflicting: 1,
            blocking: 1,
            checkedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => InboxSnapshot(
            expected: 1,
            missing: -1,
            conflicting: 0,
            blocking: 1,
            checkedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => InboxSnapshot(
            expected: 1,
            missing: 1,
            conflicting: 0,
            blocking: null,
            checkedAt));
    }

    private static QuotaDeliveryHealthSnapshot InboxSnapshot(
        long expected,
        long missing,
        long conflicting,
        long? blocking,
        DateTimeOffset checkedAt,
        long originalCount = 2) => new(
            originalCount,
            missingOriginalCount: 0,
            duplicateOriginalCount: 0,
            pendingLineageCount: 0,
            processingLineageCount: 0,
            deadLineageCount: 0,
            expected,
            missing,
            conflicting,
            oldestUnresolvedAgeSeconds: blocking is null ? 0 : 1,
            blocking,
            checkedAt);

    [Fact]
    public async Task InvalidReadBoundaryFailsBeforeOpeningPostgres()
    {
        PostgresQuotaDeliveryHealthReader reader = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(
            default,
            [1],
            0,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(
            new EntityId(Guid.NewGuid()),
            [1],
            0,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => reader.ReadAsync(
            EntityId.New(),
            null!,
            0,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(
            EntityId.New(),
            [],
            0,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(
            EntityId.New(),
            Enumerable.Range(1, 1001).Select(static value => (long)value).ToArray(),
            0,
            null!,
            cancellationToken).AsTask());
    }

    [Fact]
    public async Task InvalidSequenceCheckpointAndContextFailBeforePostgres()
    {
        PostgresQuotaDeliveryHealthReader reader = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(
            EntityId.New(),
            [0],
            0,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(
            EntityId.New(),
            [2, 1],
            0,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(
            EntityId.New(),
            [1, 1],
            0,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => reader.ReadAsync(
            EntityId.New(),
            [1, 2],
            0,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(
            EntityId.New(),
            [1, 2],
            checkpointSourceEventSequence: -1,
            null!,
            cancellationToken).AsTask());
    }

    [Fact]
    public void DeliveryHealthReaderIsRegisteredOnlyByTheWorkerBoundary()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        ServiceCollection services = new();

        services.AddOperationsModule(configuration, "Development");
        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType
                == typeof(IQuotaDeliveryHealthReader));

        services.AddOperationsOutboxPublisher(configuration);
        services.AddOperationsOutboxPublisher(configuration);

        ServiceDescriptor descriptor = Assert.Single(
            services,
            static candidate => candidate.ServiceType
                == typeof(IQuotaDeliveryHealthReader));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(
            typeof(PostgresQuotaDeliveryHealthReader),
            descriptor.ImplementationType);
    }
}
