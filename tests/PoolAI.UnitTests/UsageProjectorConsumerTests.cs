using System.Numerics;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage.Abstractions;
using PoolAI.Modules.Usage.Application;
using PoolAI.Modules.Usage.Application.Ports;

namespace PoolAI.UnitTests;

public sealed class UsageProjectorConsumerTests
{
    [Fact]
    public async Task ReplayUsesLogicalSequenceAndExactPhysicalRedeliveryIsDuplicate()
    {
        // Governing contract: Proposed ADR 0012 normative physical 20/21/42 ordering.
        Scenario scenario = new();
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        OutboxDeliveryMessage replay = Message(
            "initialized", physicalSequence: 42, sourceSequence: 7, group, period);

        Assert.Equal(
            IntegrationEventConsumeDisposition.Processed,
            (await scenario.ConsumeAsync(replay, TestToken)).Disposition);
        Assert.Equal(
            IntegrationEventConsumeDisposition.Duplicate,
            (await scenario.ConsumeAsync(replay, TestToken)).Disposition);
        Assert.Equal(7, scenario.Checkpoint.Sequence(Partition(group)));
        Assert.Equal(1, scenario.Inbox.CommittedCount);

        OutboxDeliveryMessage next = Message(
            "initialized", physicalSequence: 21, sourceSequence: 8, group, period);
        Assert.Equal(
            IntegrationEventConsumeDisposition.Processed,
            (await scenario.ConsumeAsync(next, TestToken)).Disposition);
        Assert.Equal(8, scenario.Checkpoint.Sequence(Partition(group)));
        Assert.Equal(2, scenario.Inbox.CommittedCount);
        Assert.Equal(2, scenario.Factory.CommitCount);
    }

    [Fact]
    public async Task ExactCompletedLineageCommitsInboxWithoutReprojecting()
    {
        Scenario scenario = new();
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        OutboxDeliveryMessage original = Message(
            "initialized", physicalSequence: 20, sourceSequence: 7, group, period);
        OutboxDeliveryMessage redundantReplay = Message(
            "initialized",
            physicalSequence: 42,
            sourceSequence: 7,
            group,
            period,
            replayOf: original.Envelope.Lease.MessageId,
            lineageAlreadyPublished: true);

        Assert.Equal(
            IntegrationEventConsumeDisposition.Processed,
            (await scenario.ConsumeAsync(original, TestToken)).Disposition);
        Assert.Equal(
            IntegrationEventConsumeDisposition.Duplicate,
            (await scenario.ConsumeAsync(redundantReplay, TestToken)).Disposition);
        Assert.Equal(7, scenario.Checkpoint.Sequence(Partition(group)));
        Assert.Equal(2, scenario.Inbox.CommittedCount);
        Assert.Equal(2, scenario.Factory.CommitCount);
    }

    [Fact]
    public async Task ExactReplayPredecessorReceiptCommitsNewInboxAsDuplicate()
    {
        Scenario scenario = new();
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        OutboxDeliveryMessage original = Message(
            "initialized", physicalSequence: 20, sourceSequence: 7, group, period);
        OutboxDeliveryMessage replay = Replay(original, physicalSequence: 42);

        Assert.Equal(
            IntegrationEventConsumeDisposition.Processed,
            (await scenario.ConsumeAsync(original, TestToken)).Disposition);
        Assert.Equal(
            IntegrationEventConsumeDisposition.Duplicate,
            (await scenario.ConsumeAsync(replay, TestToken)).Disposition);
        Assert.Equal(2, scenario.Inbox.CommittedCount);
        Assert.Equal(2, scenario.Factory.CommitCount);
    }

    [Fact]
    public async Task ReplayWithoutExactPredecessorReceiptPoisonsAndRollsBack()
    {
        Scenario scenario = new();
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        OutboxDeliveryMessage original = Message(
            "initialized", physicalSequence: 20, sourceSequence: 7, group, period);
        OutboxDeliveryMessage changedReplay = Message(
            "initialized",
            physicalSequence: 42,
            sourceSequence: 7,
            group,
            period,
            replayOf: original.Envelope.Lease.MessageId);

        Assert.Equal(
            IntegrationEventConsumeDisposition.Processed,
            (await scenario.ConsumeAsync(original, TestToken)).Disposition);
        IntegrationEventConsumeResult result = await scenario.ConsumeAsync(
            changedReplay,
            TestToken);

        Assert.Equal(IntegrationEventConsumeDisposition.Poison, result.Disposition);
        Assert.Equal("source_sequence_stale", result.Reason);
        Assert.Equal(1, scenario.Inbox.CommittedCount);
        Assert.Equal(1, scenario.Factory.CommitCount);
    }

    [Fact]
    public async Task UnprovenNewPhysicalMessageAtCommittedSourcePoisonsAndRollsBack()
    {
        Scenario scenario = new();
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        OutboxDeliveryMessage original = Message(
            "initialized", physicalSequence: 20, sourceSequence: 7, group, period);
        OutboxDeliveryMessage contradictory = Message(
            "initialized", physicalSequence: 42, sourceSequence: 7, group, period);

        Assert.Equal(
            IntegrationEventConsumeDisposition.Processed,
            (await scenario.ConsumeAsync(original, TestToken)).Disposition);
        IntegrationEventConsumeResult result = await scenario.ConsumeAsync(
            contradictory,
            TestToken);
        Assert.Equal(IntegrationEventConsumeDisposition.Poison, result.Disposition);
        Assert.Equal("source_sequence_stale", result.Reason);
        Assert.Equal(1, scenario.Inbox.CommittedCount);
        Assert.Equal(1, scenario.Factory.CommitCount);
    }

    [Fact]
    public async Task PoisonInOneGroupRollsBackWhileAnotherGroupContinues()
    {
        Scenario scenario = new();
        EntityId blockedGroup = EntityId.New();
        EntityId healthyGroup = EntityId.New();
        EntityId period = EntityId.New();
        OutboxDeliveryMessage invalid = Message(
            "initialized", physicalSequence: 20, sourceSequence: 7, blockedGroup, period);
        invalid = new OutboxDeliveryMessage(
            invalid.Envelope,
            Partition(healthyGroup),
            partitionSequence: 7);

        IntegrationEventConsumeResult poison = await scenario.ConsumeAsync(
            invalid,
            TestToken);
        Assert.Equal(IntegrationEventConsumeDisposition.Poison, poison.Disposition);
        Assert.Equal("quota_partition_mismatch", poison.Reason);
        Assert.Equal(0, scenario.Inbox.CommittedCount);

        OutboxDeliveryMessage healthy = Message(
            "initialized", physicalSequence: 21, sourceSequence: 8, healthyGroup, period);
        Assert.Equal(
            IntegrationEventConsumeDisposition.Processed,
            (await scenario.ConsumeAsync(healthy, TestToken)).Disposition);
        Assert.Equal(8, scenario.Checkpoint.Sequence(Partition(healthyGroup)));
        Assert.Equal(0, scenario.Checkpoint.Sequence(Partition(blockedGroup)));
    }

    [Fact]
    public async Task SettledFactRebuildsCompleteGroupAndAccountBuckets()
    {
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        EntityId reservation = EntityId.New();
        EntityId attempt = EntityId.New();
        DateTimeOffset bucket = new(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);
        AttemptSettlementFact target = Fact(
            attempt,
            reservation,
            EntityId.New(),
            EntityId.New(),
            attemptIndex: 1,
            UsageAttemptOutcome.Failed,
            new TokenUsage(10, 5, 2, 1, 3),
            isEstimated: true,
            bucket.AddMinutes(10),
            group,
            period);
        AttemptSettlementFact sibling = Fact(
            EntityId.New(),
            EntityId.New(),
            target.RequestId,
            EntityId.New(),
            attemptIndex: 0,
            UsageAttemptOutcome.Succeeded,
            new TokenUsage(7, 3, 1, 0, 1),
            isEstimated: false,
            bucket.AddMinutes(20),
            group,
            period);
        Scenario scenario = new(new AttemptSettlementHourSnapshot(
            group,
            period,
            bucket,
            [target, sibling]));
        OutboxDeliveryMessage message = Message(
            "settled",
            physicalSequence: 20,
            sourceSequence: 7,
            group,
            period,
            reservation,
            attempt,
            deltaConsumed: 15);

        IntegrationEventConsumeResult result = await scenario.ConsumeAsync(
            message,
            TestToken);
        Assert.Equal(IntegrationEventConsumeDisposition.Processed, result.Disposition);
        UsageHourProjection projection = Assert.IsType<UsageHourProjection>(
            scenario.Writer.LastCommitted);
        Assert.Equal(2, projection.Group.AttemptCount);
        Assert.Equal(1, projection.Group.RequestCount);
        Assert.Equal(1, projection.Group.FailureCount);
        Assert.Equal(1, projection.Group.FailoverCount);
        Assert.Equal(1, projection.Group.EstimatedAttemptCount);
        Assert.Equal(new BigInteger(17), projection.Group.InputTokens);
        Assert.Equal(new BigInteger(8), projection.Group.OutputTokens);
        Assert.Equal(2, projection.Accounts.Count);
    }

    [Fact]
    public async Task ConservativeExpiryFactRebuildsTheAttemptHour()
    {
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        EntityId reservation = EntityId.New();
        EntityId attempt = EntityId.New();
        DateTimeOffset bucket = new(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);
        AttemptSettlementFact fact = Fact(
            attempt,
            reservation,
            EntityId.New(),
            EntityId.New(),
            attemptIndex: 0,
            UsageAttemptOutcome.Failed,
            new TokenUsage(10, 5, 0, 0, 0),
            isEstimated: true,
            bucket.AddMinutes(10),
            group,
            period);
        fact = fact with
        {
            Usage = fact.Usage with
            {
                Source = SettlementUsageSource.ConservativeEstimate,
            },
        };
        Scenario scenario = new(new AttemptSettlementHourSnapshot(
            group,
            period,
            bucket,
            [fact]));
        OutboxDeliveryMessage message = Message(
            "expired",
            physicalSequence: 20,
            sourceSequence: 7,
            group,
            period,
            reservation,
            attempt,
            deltaConsumed: 15,
            conservativeExpiry: true);

        IntegrationEventConsumeResult result = await scenario.ConsumeAsync(
            message,
            TestToken);

        Assert.Equal(IntegrationEventConsumeDisposition.Processed, result.Disposition);
        UsageHourProjection projection = Assert.IsType<UsageHourProjection>(
            scenario.Writer.LastCommitted);
        Assert.Equal(1, projection.Group.AttemptCount);
        Assert.Equal(1, projection.Group.EstimatedAttemptCount);
        Assert.Equal(new BigInteger(15), projection.Group.TotalTokens);
        Assert.Single(projection.Accounts);
    }

    [Fact]
    public async Task ContradictorySettlementFactPoisonsAndRollsBackEveryWrite()
    {
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        EntityId reservation = EntityId.New();
        EntityId attempt = EntityId.New();
        DateTimeOffset bucket = new(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);
        AttemptSettlementFact fact = Fact(
            attempt,
            reservation,
            EntityId.New(),
            EntityId.New(),
            attemptIndex: 0,
            UsageAttemptOutcome.Succeeded,
            new TokenUsage(10, 5, 0, 0, 0),
            isEstimated: false,
            bucket.AddMinutes(1),
            group,
            period);
        Scenario scenario = new(new AttemptSettlementHourSnapshot(
            group,
            period,
            bucket,
            [fact]));
        OutboxDeliveryMessage message = Message(
            "settled",
            physicalSequence: 20,
            sourceSequence: 7,
            group,
            period,
            reservation,
            attempt,
            deltaConsumed: 99);

        IntegrationEventConsumeResult result = await scenario.ConsumeAsync(
            message,
            TestToken);
        Assert.Equal(IntegrationEventConsumeDisposition.Poison, result.Disposition);
        Assert.Equal("quota_fact_mismatch", result.Reason);
        Assert.Equal(0, scenario.Factory.CommitCount);
        Assert.Equal(0, scenario.Inbox.CommittedCount);
        Assert.Equal(0, scenario.Checkpoint.Sequence(Partition(group)));
        Assert.Null(scenario.Writer.LastCommitted);
    }

    [Fact]
    public async Task ReleasedEventWithAttemptFactPoisonsAndRollsBack()
    {
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        EntityId reservation = EntityId.New();
        EntityId attempt = EntityId.New();
        Scenario scenario = new(factExists: true);
        OutboxDeliveryMessage message = Message(
            "released",
            physicalSequence: 20,
            sourceSequence: 7,
            group,
            period,
            reservation,
            attempt);

        IntegrationEventConsumeResult result = await scenario.ConsumeAsync(
            message,
            TestToken);
        Assert.Equal(IntegrationEventConsumeDisposition.Poison, result.Disposition);
        Assert.Equal("quota_fact_mismatch", result.Reason);
        Assert.Equal(0, scenario.Factory.CommitCount);
        Assert.Equal(0, scenario.Inbox.CommittedCount);
        Assert.Equal(0, scenario.Checkpoint.Sequence(Partition(group)));
    }

    [Fact]
    public async Task PreDispatchExpiryUsesLedgerReferencesWhenPayloadOmitsThem()
    {
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        EntityId reservation = EntityId.New();
        EntityId attempt = EntityId.New();
        Scenario scenario = new(factExists: true);
        OutboxDeliveryMessage message = Message(
            "expired",
            physicalSequence: 20,
            sourceSequence: 7,
            group,
            period,
            attemptId: attempt,
            conservativeExpiry: false,
            omitPayloadReferences: true);

        IntegrationEventConsumeResult result = await scenario.ConsumeAsync(
            message,
            TestToken,
            fact => fact with
            {
                ReservationId = reservation,
                AttemptId = attempt,
            });

        Assert.Equal(IntegrationEventConsumeDisposition.Poison, result.Disposition);
        Assert.Equal("quota_fact_mismatch", result.Reason);
        Assert.Equal(0, scenario.Factory.CommitCount);
        Assert.Equal(0, scenario.Inbox.CommittedCount);
    }

    [Theory]
    [InlineData("event_id")]
    [InlineData("source_event_sequence")]
    [InlineData("metadata")]
    public async Task QuotaLedgerContradictionPoisonsAndRollsBack(string contradiction)
    {
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        Scenario scenario = new();
        OutboxDeliveryMessage message = Message(
            "initialized", physicalSequence: 20, sourceSequence: 7, group, period);

        IntegrationEventConsumeResult result = await scenario.ConsumeAsync(
            message,
            TestToken,
            fact => Contradict(fact, contradiction));

        Assert.Equal(IntegrationEventConsumeDisposition.Poison, result.Disposition);
        Assert.Equal("quota_event_fact_mismatch", result.Reason);
        Assert.Equal(0, scenario.Factory.CommitCount);
        Assert.Equal(0, scenario.Inbox.CommittedCount);
        Assert.Equal(0, scenario.Checkpoint.Sequence(Partition(group)));
    }

    [Theory]
    [InlineData("minute")]
    [InlineData("second")]
    [InlineData("millisecond")]
    [InlineData("tick")]
    public void SettlementHourSnapshotRejectsEveryNonHourAlignedBucket(
        string misalignment)
    {
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        DateTimeOffset exactHour = new(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);
        DateTimeOffset bucketStart = misalignment switch
        {
            "minute" => exactHour.AddMinutes(1),
            "second" => exactHour.AddSeconds(1),
            "millisecond" => exactHour.AddMilliseconds(1),
            "tick" => exactHour.AddTicks(1),
            _ => throw new InvalidOperationException("Unknown test misalignment."),
        };
        AttemptSettlementFact fact = Fact(
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            attemptIndex: 0,
            UsageAttemptOutcome.Succeeded,
            new TokenUsage(1, 1, 0, 0, 0),
            isEstimated: false,
            exactHour.AddMinutes(1),
            group,
            period);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new AttemptSettlementHourSnapshot(
                group,
                period,
                bucketStart,
                [fact]));

        Assert.Equal("bucketStart", exception.ParamName);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("group")]
    [InlineData("period")]
    [InlineData("hour")]
    public void SettlementHourSnapshotRejectsEveryFactMembershipContradiction(
        string contradiction)
    {
        EntityId group = EntityId.New();
        EntityId period = EntityId.New();
        DateTimeOffset bucket = new(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);
        AttemptSettlementFact valid = Fact(
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            attemptIndex: 0,
            UsageAttemptOutcome.Succeeded,
            new TokenUsage(1, 1, 0, 0, 0),
            isEstimated: false,
            bucket.AddMinutes(1),
            group,
            period);
        IReadOnlyList<AttemptSettlementFact> facts = contradiction switch
        {
            "empty" => [],
            "group" => [valid with { GroupId = EntityId.New() }],
            "period" => [valid with { PeriodId = EntityId.New() }],
            "hour" => [valid with { CompletedAt = bucket.AddHours(1) }],
            _ => throw new InvalidOperationException("Unknown test contradiction."),
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new AttemptSettlementHourSnapshot(group, period, bucket, facts));

        Assert.Equal("facts", exception.ParamName);
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static AttemptSettlementFact Fact(
        EntityId attemptId,
        EntityId reservationId,
        EntityId requestId,
        EntityId accountId,
        int attemptIndex,
        UsageAttemptOutcome outcome,
        TokenUsage usage,
        bool isEstimated,
        DateTimeOffset completedAt,
        EntityId groupId,
        EntityId periodId) => new(
            attemptId,
            requestId,
            attemptIndex,
            reservationId,
            groupId,
            periodId,
            accountId,
            EntityId.New(),
            SettlementProvider.OpenAi,
            "requested-model",
            "upstream-model",
            outcome,
            outcome == UsageAttemptOutcome.Succeeded ? 200 : 500,
            outcome == UsageAttemptOutcome.Succeeded ? null : "upstream_failed",
            IsStreaming: false,
            new AttemptUsage(
                usage,
                isEstimated
                    ? SettlementUsageSource.LocalTokenizer
                    : SettlementUsageSource.Upstream,
                isEstimated),
            Adjustment: null,
            completedAt.AddSeconds(-10),
            FirstTokenAt: null,
            completedAt);

    private static OutboxDeliveryMessage Message(
        string eventType,
        long physicalSequence,
        long sourceSequence,
        EntityId groupId,
        EntityId periodId,
        EntityId? reservationId = null,
        EntityId? attemptId = null,
        long deltaConsumed = 0,
        EntityId? replayOf = null,
        bool lineageAlreadyPublished = false,
        bool conservativeExpiry = true,
        bool omitPayloadReferences = false)
    {
        EntityId messageId = EntityId.New();
        EntityId eventId = EntityId.New();
        EntityId correlationId = eventId;
        DateTimeOffset occurredAt = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            schema_version = 1,
            event_id = eventId.ToString(),
            source_event_sequence = sourceSequence,
            correlation_id = correlationId.ToString(),
            causation_id = attemptId?.ToString(),
            group_id = groupId.ToString(),
            period_id = periodId.ToString(),
            reservation_id = omitPayloadReferences ? null : reservationId?.ToString(),
            attempt_id = omitPayloadReferences ? null : attemptId?.ToString(),
            event_type = eventType,
            delta_total_tokens = "0",
            delta_consumed_tokens = deltaConsumed.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            delta_reserved_tokens = "0",
            total_tokens = "1000",
            consumed_tokens = "100",
            reserved_tokens = "0",
            occurred_at = occurredAt,
            metadata = string.Equals(eventType, "expired", StringComparison.Ordinal)
                ? JsonSerializer.SerializeToElement(new { conservative_expiry = conservativeExpiry })
                : JsonSerializer.SerializeToElement(new { }),
        });
        OutboxMessageEnvelope envelope = new(
            new OutboxDeliveryLease(messageId, EntityId.New(), Generation: 1, Attempt: 1),
            physicalSequence,
            $"quota:test:{messageId.Value:N}",
            GroupQuotaEventV1Codec.Topic,
            GroupQuotaEventV1Codec.SchemaVersion,
            GroupQuotaEventV1Codec.AggregateType,
            groupId,
            AggregateVersion: null,
            eventType,
            sourceSequence,
            correlationId,
            attemptId,
            payload,
            occurredAt,
            replayOf);
        return new OutboxDeliveryMessage(
            envelope,
            Partition(groupId),
            sourceSequence,
            lineageAlreadyPublished);
    }

    private static OutboxDeliveryMessage Replay(
        OutboxDeliveryMessage original,
        long physicalSequence)
    {
        OutboxMessageEnvelope source = original.Envelope;
        EntityId messageId = EntityId.New();
        OutboxMessageEnvelope replay = source with
        {
            Lease = new OutboxDeliveryLease(
                messageId,
                EntityId.New(),
                Generation: 1,
                Attempt: 1),
            EventSequence = physicalSequence,
            DeduplicationKey = $"quota:replay:{messageId.Value:N}",
            ReplayOf = source.Lease.MessageId,
        };
        return new OutboxDeliveryMessage(
            replay,
            original.PartitionKey,
            original.PartitionSequence);
    }

    private static string Partition(EntityId groupId) =>
        $"{GroupQuotaEventV1Codec.Topic}:group:{groupId}";

    private static GroupQuotaEventFactSnapshot Contradict(
        GroupQuotaEventFactSnapshot fact,
        string contradiction) => contradiction switch
        {
            "event_id" => fact with { EventId = EntityId.New() },
            "source_event_sequence" => fact with
            {
                SourceEventSequence = fact.SourceEventSequence + 1,
            },
            "metadata" => fact with
            {
                Metadata = JsonSerializer.SerializeToElement(new { tampered = true }),
            },
            _ => throw new InvalidOperationException("Unknown test contradiction."),
        };

    private static GroupQuotaEventFactSnapshot EventFact(OutboxDeliveryMessage message)
    {
        OutboxMessageEnvelope envelope = message.Envelope;
        JsonElement payload = envelope.Payload;
        return new GroupQuotaEventFactSnapshot(
            ReadId(payload, "event_id"),
            envelope.SourceEventSequence!.Value,
            envelope.CorrelationId,
            envelope.CausationId,
            envelope.AggregateId,
            ReadId(payload, "period_id"),
            ReadOptionalId(payload, "reservation_id"),
            ReadOptionalId(payload, "attempt_id"),
            envelope.EventType,
            ReadTokens(payload, "delta_total_tokens"),
            ReadTokens(payload, "delta_consumed_tokens"),
            ReadTokens(payload, "delta_reserved_tokens"),
            ReadTokens(payload, "total_tokens"),
            ReadTokens(payload, "consumed_tokens"),
            ReadTokens(payload, "reserved_tokens"),
            envelope.OccurredAt,
            payload.GetProperty("metadata").Clone());
    }

    private static EntityId ReadId(JsonElement value, string propertyName) =>
        new(Guid.ParseExact(value.GetProperty(propertyName).GetString()!, "D"));

    private static EntityId? ReadOptionalId(JsonElement value, string propertyName) =>
        value.GetProperty(propertyName).ValueKind == JsonValueKind.Null
            ? null
            : ReadId(value, propertyName);

    private static BigInteger ReadTokens(JsonElement value, string propertyName) =>
        BigInteger.Parse(
            value.GetProperty(propertyName).GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Scenario
    {
        private readonly StaticEventFactReader _eventFactReader = new();

        internal Scenario(
            AttemptSettlementHourSnapshot? snapshot = null,
            bool factExists = false)
        {
            Factory = new TransactionFactory();
            Inbox = new TransactionalInbox();
            Checkpoint = new TransactionalCheckpoint();
            Writer = new TransactionalWriter();
            Consumer = new GroupQuotaUsageProjectorConsumer(
                Factory,
                Inbox,
                Inbox,
                _eventFactReader,
                new StaticFactReader(snapshot),
                new StaticExistenceReader(factExists),
                Writer,
                Checkpoint);
        }

        internal ValueTask<IntegrationEventConsumeResult> ConsumeAsync(
            OutboxDeliveryMessage message,
            CancellationToken cancellationToken,
            Func<GroupQuotaEventFactSnapshot, GroupQuotaEventFactSnapshot>? alterFact = null)
        {
            GroupQuotaEventFactSnapshot fact = EventFact(message);
            _eventFactReader.Set(alterFact is null ? fact : alterFact(fact));
            return Consumer.ConsumeAsync(message, cancellationToken);
        }

        internal TransactionFactory Factory { get; }

        internal TransactionalInbox Inbox { get; }

        internal TransactionalCheckpoint Checkpoint { get; }

        internal TransactionalWriter Writer { get; }

        internal GroupQuotaUsageProjectorConsumer Consumer { get; }
    }

    private sealed class StaticEventFactReader : IGroupQuotaEventFactReader
    {
        private readonly Dictionary<(EntityId GroupId, long Sequence),
            GroupQuotaEventFactSnapshot> _facts = [];

        internal void Set(GroupQuotaEventFactSnapshot fact) =>
            _facts[(fact.GroupId, fact.SourceEventSequence)] = fact;

        public ValueTask<GroupQuotaEventFactSnapshot?> ReadAsync(
            EntityId groupId,
            long sourceEventSequence,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _facts.TryGetValue((groupId, sourceEventSequence), out GroupQuotaEventFactSnapshot? fact);
            return ValueTask.FromResult(fact);
        }
    }

    private sealed class TransactionFactory : IUnitOfWorkFactory
    {
        internal int CommitCount { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IUnitOfWork>(new Transaction(this));
        }

        private sealed class Transaction(TransactionFactory owner) : IUnitOfWork
        {
            private bool _committed;

            public IUnitOfWorkContext Context { get; } = new TransactionContext();

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.False(_committed);
                _committed = true;
                foreach (Action action in ((TransactionContext)Context).CommitActions)
                {
                    action();
                }

                owner.CommitCount++;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TransactionContext : IUnitOfWorkContext
    {
        internal List<Action> CommitActions { get; } = [];

        internal Dictionary<string, MutableWatermark> Watermarks { get; } =
            new(StringComparer.Ordinal);
    }

    private sealed class TransactionalInbox :
        IInboxReceiptAppender,
        IInboxReplayPredecessorVerifier
    {
        private readonly Dictionary<(string Consumer, EntityId Message), InboxReceipt>
            _committed = [];
        private readonly Dictionary<(string Consumer, string Topic, long Sequence), EntityId>
            _sequences = [];

        internal int CommittedCount => _committed.Count;

        public ValueTask<InboxReceiptAppendResult> AppendAsync(
            InboxReceipt receipt,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransactionContext context = Assert.IsType<TransactionContext>(unitOfWorkContext);
            (string Consumer, EntityId Message) key = (receipt.ConsumerName, receipt.MessageId);
            if (_committed.TryGetValue(key, out InboxReceipt? existing))
            {
                bool exact = string.Equals(
                        existing.Topic,
                        receipt.Topic,
                        StringComparison.Ordinal)
                    && existing.EventSequence == receipt.EventSequence
                    && existing.SchemaVersion == receipt.SchemaVersion
                    && existing.PayloadHash.Span.SequenceEqual(receipt.PayloadHash.Span);
                return ValueTask.FromResult(new InboxReceiptAppendResult(
                    exact
                        ? InboxReceiptDisposition.Duplicate
                        : InboxReceiptDisposition.MessageConflict));
            }

            (string Consumer, string Topic, long Sequence) sequence = (
                receipt.ConsumerName,
                receipt.Topic,
                receipt.EventSequence);
            if (_sequences.ContainsKey(sequence))
            {
                return ValueTask.FromResult(new InboxReceiptAppendResult(
                    InboxReceiptDisposition.SequenceConflict));
            }

            context.CommitActions.Add(() =>
            {
                _committed.Add(key, receipt);
                _sequences.Add(sequence, receipt.MessageId);
            });
            return ValueTask.FromResult(new InboxReceiptAppendResult(
                InboxReceiptDisposition.Inserted));
        }

        public ValueTask<bool> HasExactReceiptAsync(
            InboxReplayPredecessorProof proof,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool exact = _committed.TryGetValue(
                    (proof.ConsumerName, proof.PredecessorMessageId),
                    out InboxReceipt? receipt)
                && string.Equals(receipt.Topic, proof.Topic, StringComparison.Ordinal)
                && receipt.SchemaVersion == proof.SchemaVersion
                && receipt.PayloadHash.Span.SequenceEqual(proof.PayloadHash.Span);
            return ValueTask.FromResult(exact);
        }
    }

    private sealed class TransactionalCheckpoint : IUsageAggregationCheckpoint
    {
        private readonly Dictionary<string, MutableWatermark> _committed =
            new(StringComparer.Ordinal);

        internal long Sequence(string partition) =>
            _committed.TryGetValue(partition, out MutableWatermark? value)
                ? value.LastSequence
                : 0;

        public ValueTask<UsageAggregationClaimResult> ClaimAsync(
            UsageAggregationClaimRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransactionContext context = Assert.IsType<TransactionContext>(unitOfWorkContext);
            MutableWatermark state = _committed.TryGetValue(
                request.PartitionKey,
                out MutableWatermark? current)
                    ? current.Copy(version: current.Version + 1)
                    : new MutableWatermark(0, null, version: 1);
            context.Watermarks.Add(request.PartitionKey, state);
            context.CommitActions.Add(() => _committed[request.PartitionKey] = state.Copy());
            return ValueTask.FromResult(UsageAggregationClaimResult.Acquired(
                Lease(request, state)));
        }

        public ValueTask<bool> HeartbeatAsync(
            UsageAggregationLease lease,
            TimeSpan leaseDuration,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<UsageAggregationLease?> AdvanceAsync(
            UsageAggregationAdvanceRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransactionContext context = Assert.IsType<TransactionContext>(unitOfWorkContext);
            MutableWatermark state = context.Watermarks[request.Lease.PartitionKey];
            if (state.Version != request.Lease.Version
                || state.LastSequence != request.Lease.LastEventSequence)
            {
                return ValueTask.FromResult<UsageAggregationLease?>(null);
            }

            state.LastSequence = request.NextEventSequence;
            state.CompletedThrough = request.CompletedThrough;
            state.Version++;
            return ValueTask.FromResult<UsageAggregationLease?>(request.Lease with
            {
                LastEventSequence = state.LastSequence,
                CompletedThrough = state.CompletedThrough,
                Version = state.Version,
            });
        }

        public ValueTask<bool> ReleaseAsync(
            UsageAggregationLease lease,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransactionContext context = Assert.IsType<TransactionContext>(unitOfWorkContext);
            MutableWatermark state = context.Watermarks[lease.PartitionKey];
            if (state.Version != lease.Version)
            {
                return ValueTask.FromResult(false);
            }

            state.Version++;
            return ValueTask.FromResult(true);
        }

        private static UsageAggregationLease Lease(
            UsageAggregationClaimRequest request,
            MutableWatermark state) => new(
                request.ProjectorName,
                request.PartitionKey,
                request.Owner,
                state.Version,
                state.LastSequence,
                state.CompletedThrough);
    }

    private sealed class MutableWatermark(
        long lastSequence,
        DateTimeOffset? completedThrough,
        long version)
    {
        internal long LastSequence { get; set; } = lastSequence;

        internal DateTimeOffset? CompletedThrough { get; set; } = completedThrough;

        internal long Version { get; set; } = version;

        internal MutableWatermark Copy(long? version = null) => new(
            LastSequence,
            CompletedThrough,
            version ?? Version);
    }

    private sealed class StaticFactReader(AttemptSettlementHourSnapshot? snapshot) :
        IAttemptSettlementHourFactReader
    {
        public ValueTask<AttemptSettlementHourSnapshot?> ReadForAttemptAsync(
            EntityId groupId,
            EntityId periodId,
            EntityId attemptId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshot);
        }
    }

    private sealed class StaticExistenceReader(bool exists) :
        IAttemptSettlementFactExistenceReader
    {
        public ValueTask<bool> ExistsForReservationAsync(
            EntityId groupId,
            EntityId periodId,
            EntityId reservationId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(exists);
        }
    }

    private sealed class TransactionalWriter : IUsageHourlyProjectionWriter
    {
        internal UsageHourProjection? LastCommitted { get; private set; }

        public ValueTask ReplaceAsync(
            UsageHourProjection projection,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransactionContext context = Assert.IsType<TransactionContext>(unitOfWorkContext);
            context.CommitActions.Add(() => LastCommitted = projection);
            return ValueTask.CompletedTask;
        }
    }
}
