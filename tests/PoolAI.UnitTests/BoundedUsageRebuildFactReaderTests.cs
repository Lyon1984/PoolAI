using System.Numerics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

namespace PoolAI.UnitTests;

public sealed class BoundedUsageRebuildFactReaderTests
{
    private static readonly DateTimeOffset BucketStart = new(
        2030,
        1,
        2,
        3,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void SnapshotRepresentsAnEmptyHourAndCopiesItsFacts()
    {
        EntityId groupId = EntityId.New();
        EntityId periodId = EntityId.New();
        List<AttemptSettlementFact> facts =
        [
            CreateFact(groupId, periodId, BucketStart.AddMinutes(10)),
        ];
        BoundedUsageRebuildHourSnapshot populated = new(
            groupId,
            periodId,
            BucketStart,
            checkpointSourceSequence: 17,
            facts);
        facts.Clear();
        BoundedUsageRebuildHourSnapshot empty = new(
            groupId,
            periodId,
            BucketStart,
            checkpointSourceSequence: 17,
            []);

        Assert.Single(populated.Facts);
        Assert.Empty(empty.Facts);
        Assert.Equal(groupId, empty.GroupId);
        Assert.Equal(periodId, empty.PeriodId);
        Assert.Equal(BucketStart, empty.BucketStart);
        Assert.Equal(17, empty.CheckpointSourceSequence);
    }

    [Fact]
    public void SnapshotRejectsInvalidBoundaryAndDuplicateAttemptFacts()
    {
        EntityId groupId = EntityId.New();
        EntityId periodId = EntityId.New();
        AttemptSettlementFact fact = CreateFact(
            groupId,
            periodId,
            BucketStart.AddMinutes(10));

        Assert.Throws<ArgumentException>(() => new BoundedUsageRebuildHourSnapshot(
            default,
            periodId,
            BucketStart,
            checkpointSourceSequence: 1,
            []));
        Assert.Throws<ArgumentException>(() => new BoundedUsageRebuildHourSnapshot(
            groupId,
            default,
            BucketStart,
            checkpointSourceSequence: 1,
            []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedUsageRebuildHourSnapshot(
                groupId,
                periodId,
                BucketStart,
                checkpointSourceSequence: 0,
                []));
        Assert.Throws<ArgumentException>(() => new BoundedUsageRebuildHourSnapshot(
            groupId,
            periodId,
            BucketStart.AddTicks(1),
            checkpointSourceSequence: 1,
            []));
        Assert.Throws<ArgumentException>(() => new BoundedUsageRebuildHourSnapshot(
            groupId,
            periodId,
            new DateTimeOffset(2030, 1, 2, 3, 0, 0, TimeSpan.FromHours(1)),
            checkpointSourceSequence: 1,
            []));
        Assert.Throws<ArgumentException>(() => new BoundedUsageRebuildHourSnapshot(
            groupId,
            periodId,
            BucketStart,
            checkpointSourceSequence: 1,
            [fact, fact]));
        Assert.Throws<ArgumentException>(() => new BoundedUsageRebuildHourSnapshot(
            groupId,
            periodId,
            BucketStart,
            checkpointSourceSequence: 1,
            [fact with { CompletedAt = BucketStart.AddHours(1) }]));
    }

    [Fact]
    public async Task InvalidReadBoundaryFailsBeforeOpeningPostgres()
    {
        PostgresBoundedUsageRebuildFactReader reader = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadHourAsync(
            default,
            EntityId.New(),
            BucketStart,
            checkpointSourceSequence: 1,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadHourAsync(
            EntityId.New(),
            default,
            BucketStart,
            checkpointSourceSequence: 1,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadHourAsync(
            EntityId.New(),
            EntityId.New(),
            BucketStart.AddMinutes(1),
            checkpointSourceSequence: 1,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadHourAsync(
            EntityId.New(),
            EntityId.New(),
            new DateTimeOffset(2030, 1, 2, 3, 0, 0, TimeSpan.FromHours(1)),
            checkpointSourceSequence: 1,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadHourAsync(
            EntityId.New(),
            EntityId.New(),
            BucketStart,
            checkpointSourceSequence: 0,
            null!,
            cancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadHourAsync(
            EntityId.New(),
            EntityId.New(),
            BucketStart,
            checkpointSourceSequence: -1,
            null!,
            cancellationToken).AsTask());
    }

    [Fact]
    public void SqlIsReadOnlyAndUsesOnlyGroupQuotaOwnedImmutableFacts()
    {
        FieldInfo? reflectedField = typeof(PostgresBoundedUsageRebuildFactReader)
            .GetField("ReadHourSql", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(reflectedField);
        FieldInfo field = reflectedField;
        string sql = Assert.IsType<string>(field.GetRawConstantValue());
        string normalized = sql.ToLowerInvariant();

        Assert.Contains("public.usage_attempts", normalized, StringComparison.Ordinal);
        Assert.Contains(
            "public.usage_attempt_adjustments",
            normalized,
            StringComparison.Ordinal);
        Assert.Contains(
            "public.group_quota_events",
            normalized,
            StringComparison.Ordinal);
        Assert.Contains("event_sequence <= $4", normalized, StringComparison.Ordinal);
        Assert.Contains("attempt.completed_at >= $3", normalized, StringComparison.Ordinal);
        Assert.Contains(
            "attempt.completed_at < $3 + interval '1 hour'",
            normalized,
            StringComparison.Ordinal);
        Assert.DoesNotContain("outbox", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("inbox", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("insert ", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("update ", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("delete ", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void ModuleRegistersOneSingletonBoundedRebuildReader()
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
                == typeof(IBoundedUsageRebuildFactReader));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(
            typeof(PostgresBoundedUsageRebuildFactReader),
            descriptor.ImplementationType);
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("invalid_cast")]
    [InlineData("overflow")]
    public void AbiReaderFailuresAreNormalizedWithoutLeakingData(string kind)
    {
        Exception source = kind switch
        {
            "argument" => new ArgumentException("sensitive argument", nameof(kind)),
            "invalid_cast" => new InvalidCastException("sensitive cast"),
            "overflow" => new OverflowException("sensitive overflow"),
            _ => throw new InvalidOperationException("Unknown failure kind."),
        };

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => PostgresBoundedUsageRebuildFactReader.ReadAbi<int>(
                () => throw source));

        Assert.Equal("The PostgreSQL bounded rebuild fact violated its ABI.", failure.Message);
        Assert.Same(source, failure.InnerException);
        Assert.Equal(17, PostgresBoundedUsageRebuildFactReader.ReadAbi(() => 17));
    }

    [Fact]
    public void TerminalValidationAcceptsSettledAndCanonicalExpiredFacts()
    {
        AttemptSettlementFact settled = CreateFact(
            EntityId.New(),
            EntityId.New(),
            BucketStart.AddMinutes(10));
        PostgresBoundedUsageRebuildFactReader.TerminalEvent terminal = Terminal(
            settled,
            sourceSequence: 10,
            eventType: "settled");
        PostgresBoundedUsageRebuildFactReader.ValidateTerminalEvent(
            settled,
            terminal,
            checkpointSourceSequence: 10,
            reservationStatus: "settled");

        AttemptSettlementFact expired = settled with
        {
            Usage = new AttemptUsage(
                settled.Usage.Tokens,
                SettlementUsageSource.ConservativeEstimate,
                IsEstimated: true),
        };
        PostgresBoundedUsageRebuildFactReader.ValidateTerminalEvent(
            expired,
            Terminal(expired, sourceSequence: 11, eventType: "expired"),
            checkpointSourceSequence: 11,
            reservationStatus: "expired");
    }

    [Fact]
    public void TerminalValidationRejectsDuplicateAndEveryIdentityContradiction()
    {
        AttemptSettlementFact fact = CreateFact(
            EntityId.New(),
            EntityId.New(),
            BucketStart.AddMinutes(10));
        PostgresBoundedUsageRebuildFactReader.TerminalEvent valid = Terminal(
            fact,
            sourceSequence: 10,
            eventType: "settled");
        InvalidOperationException duplicate = Assert.Throws<InvalidOperationException>(() =>
            PostgresBoundedUsageRebuildFactReader.ValidateTerminalEvent(
                fact,
                valid with { MatchingCount = 2 },
                10,
                "settled"));
        Assert.Contains("duplicate terminal events", duplicate.Message, StringComparison.Ordinal);

        PostgresBoundedUsageRebuildFactReader.TerminalEvent[] invalid =
        [
            valid with { SourceSequence = 0 },
            valid with { SourceSequence = 11 },
            valid with { GroupId = EntityId.New() },
            valid with { PeriodId = EntityId.New() },
            valid with { ReservationId = EntityId.New() },
            valid with { AttemptId = EntityId.New() },
            valid with { DeltaConsumedTokens = valid.DeltaConsumedTokens + 1 },
            valid with { EventType = "released" },
        ];
        Assert.All(invalid, terminal => Assert.Throws<InvalidOperationException>(() =>
            PostgresBoundedUsageRebuildFactReader.ValidateTerminalEvent(
                fact,
                terminal,
                10,
                "settled")));
        Assert.Throws<InvalidOperationException>(() =>
            PostgresBoundedUsageRebuildFactReader.ValidateTerminalEvent(
                fact,
                valid,
                10,
                "expired"));
    }

    [Fact]
    public void AdjustmentValidationAcceptsAbsentInvisibleAndVisibleStates()
    {
        AttemptSettlementFact fact = CreateFact(
            EntityId.New(),
            EntityId.New(),
            BucketStart.AddMinutes(10));
        PostgresBoundedUsageRebuildFactReader.TerminalEvent terminal = Terminal(
            fact,
            sourceSequence: 10,
            eventType: "settled");
        PostgresBoundedUsageRebuildFactReader.ValidateMissingAdjustment(
            hasUnexpectedEventColumn: false,
            factAdjustment: null);
        PostgresBoundedUsageRebuildFactReader.AdjustmentEvent invisible = Adjustment(
            fact,
            sourceSequence: 12,
            eventId: EntityId.New());
        PostgresBoundedUsageRebuildFactReader.ValidateAdjustmentEvent(
            fact,
            terminal,
            invisible,
            checkpointSourceSequence: 11);

        AttemptSettlementFact adjusted = WithAdjustment(fact, invisible);
        PostgresBoundedUsageRebuildFactReader.ValidateAdjustmentEvent(
            adjusted,
            terminal,
            invisible,
            checkpointSourceSequence: 12);
    }

    [Fact]
    public void AdjustmentValidationRejectsMissingDuplicateAndIdentityContradictions()
    {
        AttemptSettlementFact fact = CreateFact(
            EntityId.New(),
            EntityId.New(),
            BucketStart.AddMinutes(10));
        PostgresBoundedUsageRebuildFactReader.TerminalEvent terminal = Terminal(
            fact,
            sourceSequence: 10,
            eventType: "settled");
        PostgresBoundedUsageRebuildFactReader.AdjustmentEvent valid = Adjustment(
            fact,
            sourceSequence: 12,
            eventId: EntityId.New());
        AttemptSettlementFact adjusted = WithAdjustment(fact, valid);

        Assert.Throws<InvalidOperationException>(() =>
            PostgresBoundedUsageRebuildFactReader.ValidateMissingAdjustment(true, null));
        Assert.Throws<InvalidOperationException>(() =>
            PostgresBoundedUsageRebuildFactReader.ValidateMissingAdjustment(
                false,
                adjusted.Adjustment));
        InvalidOperationException duplicate = Assert.Throws<InvalidOperationException>(() =>
            PostgresBoundedUsageRebuildFactReader.ValidateAdjustmentEvent(
                adjusted,
                terminal,
                valid with { MatchingCount = 2 },
                12));
        Assert.Contains("duplicate adjustment identities", duplicate.Message, StringComparison.Ordinal);

        PostgresBoundedUsageRebuildFactReader.AdjustmentEvent[] invalid =
        [
            valid with { SourceSequence = terminal.SourceSequence },
            valid with { EventType = "settled" },
            valid with { GroupId = EntityId.New() },
            valid with { PeriodId = EntityId.New() },
            valid with { ReservationId = EntityId.New() },
            valid with { AttemptId = EntityId.New() },
            valid with { EventDeltaTokens = valid.FactDeltaTokens + 1 },
            valid with { EventId = EntityId.New() },
            valid with { FactDeltaTokens = valid.FactDeltaTokens - 1 },
        ];
        Assert.All(invalid, adjustment => Assert.Throws<InvalidOperationException>(() =>
            PostgresBoundedUsageRebuildFactReader.ValidateAdjustmentEvent(
                adjusted,
                terminal,
                adjustment,
                12)));
        Assert.Throws<InvalidOperationException>(() =>
            PostgresBoundedUsageRebuildFactReader.ValidateAdjustmentEvent(
                fact,
                terminal,
                valid,
                12));
        Assert.Throws<InvalidOperationException>(() =>
            PostgresBoundedUsageRebuildFactReader.ValidateAdjustmentEvent(
                adjusted,
                terminal,
                valid,
                11));
    }

    private static PostgresBoundedUsageRebuildFactReader.TerminalEvent Terminal(
        AttemptSettlementFact fact,
        long sourceSequence,
        string eventType) => new(
            EntityId.New(),
            sourceSequence,
            eventType,
            fact.GroupId,
            fact.PeriodId,
            fact.ReservationId,
            fact.AttemptId,
            fact.Usage.Tokens.TotalTokens,
            MatchingCount: 1);

    private static PostgresBoundedUsageRebuildFactReader.AdjustmentEvent Adjustment(
        AttemptSettlementFact fact,
        long sourceSequence,
        EntityId eventId) => new(
            eventId,
            sourceSequence,
            "usage_adjusted",
            fact.GroupId,
            fact.PeriodId,
            fact.ReservationId,
            fact.AttemptId,
            EventDeltaTokens: -3,
            FactDeltaTokens: -3,
            MatchingCount: 1);

    private static AttemptSettlementFact WithAdjustment(
        AttemptSettlementFact fact,
        PostgresBoundedUsageRebuildFactReader.AdjustmentEvent adjustment) => fact with
        {
            Adjustment = new AttemptUsageAdjustment(
                adjustment.EventId,
                fact.Usage.Tokens.TotalTokens,
                new TokenUsage(8, 4, 0, 0, 0),
                SettlementUsageSource.Upstream,
                adjustment.FactDeltaTokens,
                fact.CompletedAt.AddMinutes(1)),
        };

    private static AttemptSettlementFact CreateFact(
        EntityId groupId,
        EntityId periodId,
        DateTimeOffset completedAt) => new(
            EntityId.New(),
            EntityId.New(),
            AttemptIndex: 0,
            EntityId.New(),
            groupId,
            periodId,
            EntityId.New(),
            EntityId.New(),
            SettlementProvider.OpenAi,
            RequestedModel: "requested-model",
            UpstreamModel: "upstream-model",
            UsageAttemptOutcome.Succeeded,
            UpstreamHttpStatus: 200,
            ErrorCode: null,
            IsStreaming: false,
            new AttemptUsage(
                new TokenUsage(10, 5, 1, 1, 2),
                SettlementUsageSource.Upstream,
                IsEstimated: false),
            Adjustment: null,
            completedAt.AddMinutes(-1),
            FirstTokenAt: null,
            completedAt);
}
