using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Application.Ports;
using PoolAI.Modules.Operations.Infrastructure;
using PoolAI.Modules.Operations.Infrastructure.Persistence;
using PoolAI.Modules.Operations.Worker;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresOutboxPublisherOrderingTests
{
    private const string QuotaTopic = "poolai.quota.v1";
    private static long _nextSourceSequence = TimeProvider.System.GetUtcNow().UtcTicks;
    private readonly PostgresRuntimeFixture _fixture;

    public PostgresOutboxPublisherOrderingTests(PostgresRuntimeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task DeadQuotaLineageBlocksOnlyItsGroupUntilLaterPhysicalReplayPublishes()
    {
        // Governing contract: ADR 0012 partition order and AC-041/AC-045.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId firstGroup = EntityId.New();
        EntityId secondGroup = EntityId.New();
        long firstSourceSequence = NextSourceSequenceBlock();
        IntegrationEvent firstSource = CreateQuotaEvent(firstGroup, firstSourceSequence);
        IntegrationEvent secondSource = CreateQuotaEvent(firstGroup, firstSourceSequence + 1);
        IntegrationEvent otherGroup = CreateQuotaEvent(secondGroup, firstSourceSequence + 2);
        await AppendAndIsolateAsync(
            [firstSource, secondSource, otherGroup],
            cancellationToken).ConfigureAwait(true);

        IReadOnlyList<OutboxDeliveryMessage> initial = await ClaimRoutedAndCommitAsync(
            maximumCount: 10,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(2, initial.Count);
        OutboxDeliveryMessage first = Assert.Single(initial, message =>
            message.Envelope.AggregateId == firstGroup);
        OutboxDeliveryMessage independent = Assert.Single(initial, message =>
            message.Envelope.AggregateId == secondGroup);
        Assert.Equal(firstSource.MessageId, first.Envelope.Lease.MessageId);
        Assert.Equal(firstSourceSequence, first.PartitionSequence);
        Assert.Equal(otherGroup.MessageId, independent.Envelope.Lease.MessageId);
        await CompleteInitialClaimsAsync(first, independent, cancellationToken)
            .ConfigureAwait(true);

        Assert.Empty(await ClaimRoutedAndCommitAsync(
            maximumCount: 10,
            cancellationToken).ConfigureAwait(true));

        ReplayReceipt replay = await ReplayAndCommitAsync(
            firstSource.MessageId,
            cancellationToken).ConfigureAwait(true);
        OutboxDeliveryMessage replayClaim = Assert.Single(await ClaimRoutedAndCommitAsync(
            maximumCount: 10,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(replay.MessageId, replayClaim.Envelope.Lease.MessageId);
        Assert.True(replayClaim.Envelope.EventSequence > first.Envelope.EventSequence);
        Assert.Equal(firstSourceSequence, replayClaim.Envelope.SourceEventSequence);
        Assert.Equal(firstSourceSequence, replayClaim.PartitionSequence);
        Assert.Equal(
            $"{QuotaTopic}:group:{firstGroup.Value:D}",
            replayClaim.PartitionKey);
        await MarkPublishedAndCommitAsync(replayClaim, cancellationToken).ConfigureAwait(true);

        OutboxDeliveryMessage resumed = Assert.Single(await ClaimRoutedAndCommitAsync(
            maximumCount: 10,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(secondSource.MessageId, resumed.Envelope.Lease.MessageId);
        Assert.Equal(firstSourceSequence + 1, resumed.PartitionSequence);
        Assert.Equal(
            "dead",
            await ReadStatusAsync(firstSource.MessageId, cancellationToken).ConfigureAwait(true));
        await MarkPublishedAndCommitAsync(resumed, cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RoutedClaimNeverClaimsAnUnregisteredTopic()
    {
        // Governing contract: ADR 0012 explicit consumer routes.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        long firstSourceSequence = NextSourceSequenceBlock();
        IntegrationEvent quota = CreateQuotaEvent(EntityId.New(), firstSourceSequence);
        IntegrationEvent identity = CreateEvent(
            EntityId.New(),
            "poolai.identity.v1",
            "user",
            firstSourceSequence + 1);
        await AppendAndIsolateAsync([quota, identity], cancellationToken).ConfigureAwait(true);

        OutboxDeliveryMessage claimed = Assert.Single(await ClaimRoutedAndCommitAsync(
            maximumCount: 10,
            cancellationToken).ConfigureAwait(true));

        Assert.Equal(quota.MessageId, claimed.Envelope.Lease.MessageId);
        OutboxRuntimeState identityState = await ReadRuntimeStateAsync(
            identity.MessageId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("pending", identityState.Status);
        Assert.Equal(0, identityState.PublishAttempts);
        Assert.Equal(0, identityState.LockGeneration);
        await MarkPublishedAndCommitAsync(claimed, cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task MalformedQuotaAggregateTypeCannotEscapeItsGroupPartition()
    {
        // Governing contract: quota partition identity is topic + Group id, not envelope type.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId groupId = EntityId.New();
        long firstSourceSequence = NextSourceSequenceBlock();
        IntegrationEvent malformed = CreateQuotaEvent(groupId, firstSourceSequence) with
        {
            AggregateType = "spoofed-group",
        };
        IntegrationEvent laterValid = CreateQuotaEvent(groupId, firstSourceSequence + 1);
        await AppendAndIsolateAsync([malformed, laterValid], cancellationToken)
            .ConfigureAwait(true);

        OutboxDeliveryMessage first = Assert.Single(await ClaimRoutedAndCommitAsync(
            maximumCount: 10,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(malformed.MessageId, first.Envelope.Lease.MessageId);
        Assert.Equal(
            $"{QuotaTopic}:group:{groupId.Value:D}",
            first.PartitionKey);
        await MarkDeadAndCommitAsync(first, cancellationToken).ConfigureAwait(true);

        Assert.Empty(await ClaimRoutedAndCommitAsync(
            maximumCount: 10,
            cancellationToken).ConfigureAwait(true));
        OutboxRuntimeState laterState = await ReadRuntimeStateAsync(
            laterValid.MessageId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("pending", laterState.Status);
        Assert.Equal(0, laterState.PublishAttempts);
        Assert.Equal(0, laterState.LockGeneration);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RoutedTakeoverFencesEveryMutationFromTheStaleOwner()
    {
        // Governing contract: AC-041 owner/generation CAS fencing on takeover.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IntegrationEvent integrationEvent = CreateQuotaEvent(
            EntityId.New(),
            NextSourceSequenceBlock());
        await AppendAndIsolateAsync([integrationEvent], cancellationToken).ConfigureAwait(true);
        RoutedTakeoverClaims claims = await ClaimInitialAndTakeoverAsync(
            integrationEvent,
            cancellationToken).ConfigureAwait(true);
        await AssertRoutedStaleLeaseIsFencedAsync(claims, cancellationToken)
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ExactCompletedReplayConvergesButContradictoryReplayPoisons()
    {
        // Governing contract: ADR 0012 exact replay convergence and poison P0.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IntegrationEvent source = CreateQuotaEvent(EntityId.New(), NextSourceSequenceBlock());
        await AppendAndIsolateAsync([source], cancellationToken).ConfigureAwait(true);
        OutboxDeliveryMessage sourceClaim = Assert.Single(await ClaimRoutedAndCommitAsync(
            maximumCount: 1,
            cancellationToken).ConfigureAwait(true));
        await MarkDeadAndCommitAsync(sourceClaim, cancellationToken).ConfigureAwait(true);

        ReplayReceipt first = await ReplayAndCommitAsync(
            source.MessageId,
            cancellationToken).ConfigureAwait(true);
        ReplayReceipt exactRedundant = await ReplayAndCommitAsync(
            source.MessageId,
            cancellationToken).ConfigureAwait(true);
        ReplayReceipt contradictory = await ReplayAndCommitAsync(
            source.MessageId,
            cancellationToken).ConfigureAwait(true);
        await CorruptReplayPayloadAsync(contradictory.MessageId, cancellationToken)
            .ConfigureAwait(true);

        RecordingOperationalEventWriter events = new();
        OutboxPublisherProcessor processor = CreateProcessor(events);
        Assert.Equal(
            OutboxPublishProcessResult.Processed,
            await processor.ProcessNextAsync(
                new OwnedJobLock(),
                cancellationToken).ConfigureAwait(true));
        Assert.Equal("published", await ReadStatusAsync(
            first.MessageId,
            cancellationToken).ConfigureAwait(true));
        Assert.True(await InboxExistsAsync(first.MessageId, cancellationToken)
            .ConfigureAwait(true));

        Assert.Equal(
            OutboxPublishProcessResult.Processed,
            await processor.ProcessNextAsync(
                new OwnedJobLock(),
                cancellationToken).ConfigureAwait(true));
        Assert.Equal("published", await ReadStatusAsync(
            exactRedundant.MessageId,
            cancellationToken).ConfigureAwait(true));
        Assert.True(await InboxExistsAsync(exactRedundant.MessageId, cancellationToken)
            .ConfigureAwait(true));

        Assert.Equal(
            OutboxPublishProcessResult.Processed,
            await processor.ProcessNextAsync(
                new OwnedJobLock(),
                cancellationToken).ConfigureAwait(true));
        Assert.Equal("dead", await ReadStatusAsync(
            contradictory.MessageId,
            cancellationToken).ConfigureAwait(true));
        Assert.False(await InboxExistsAsync(contradictory.MessageId, cancellationToken)
            .ConfigureAwait(true));
        RecordedOperationalEvent poison = Assert.Single(events.Events);
        Assert.Equal("outbox_poison_dead", poison.Name);
        Assert.Equal("P0", poison.Payload.GetProperty("severity").GetString());
        Assert.DoesNotContain("must-not-leak", poison.Payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReplayResumesAfterAnEarlierConsumerCommittedBeforeLaterPoison()
    {
        // Governing contract: every consumer owns an exact replay predecessor receipt.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IntegrationEvent source = CreateQuotaEvent(EntityId.New(), NextSourceSequenceBlock());
        await AppendAndIsolateAsync([source], cancellationToken).ConfigureAwait(true);
        RollbackThenProcessConsumer laterConsumer = new(
            _fixture.WorkerServices.GetRequiredService<IUnitOfWorkFactory>(),
            _fixture.WorkerServices.GetRequiredService<IInboxReceiptAppender>());
        RecordingOperationalEventWriter events = new();
        OutboxPublisherProcessor processor = CreateProcessor(events, laterConsumer);

        Assert.Equal(
            OutboxPublishProcessResult.Processed,
            await processor.ProcessNextAsync(
                new OwnedJobLock(),
                cancellationToken).ConfigureAwait(true));
        Assert.Equal("dead", await ReadStatusAsync(
            source.MessageId,
            cancellationToken).ConfigureAwait(true));
        Assert.True(await InboxExistsAsync(source.MessageId, cancellationToken)
            .ConfigureAwait(true));
        Assert.False(await InboxExistsAsync(
            source.MessageId,
            cancellationToken,
            RollbackThenProcessConsumer.ConsumerName).ConfigureAwait(true));

        ReplayReceipt replay = await ReplayAndCommitAsync(
            source.MessageId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            OutboxPublishProcessResult.Processed,
            await processor.ProcessNextAsync(
                new OwnedJobLock(),
                cancellationToken).ConfigureAwait(true));
        Assert.Equal("published", await ReadStatusAsync(
            replay.MessageId,
            cancellationToken).ConfigureAwait(true));
        Assert.True(await InboxExistsAsync(replay.MessageId, cancellationToken)
            .ConfigureAwait(true));
        Assert.True(await InboxExistsAsync(
            replay.MessageId,
            cancellationToken,
            RollbackThenProcessConsumer.ConsumerName).ConfigureAwait(true));
        Assert.Equal(2, laterConsumer.Calls);
        Assert.Single(events.Events);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ObservabilitySqlCollapsesUnknownLabelsBeforeReturningThem()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        EntityId messageId = EntityId.New();
        using (NpgsqlCommand seed = _fixture.AdministratorDataSource.CreateCommand("""
            INSERT INTO public.outbox_messages (
                id, deduplication_key, topic, schema_version, aggregate_type,
                aggregate_id, event_type, correlation_id, payload, occurred_at,
                status, next_attempt_at, publish_attempts, lock_generation,
                dead_at, last_error
            ) VALUES (
                $1, $2, 'poolai.forged-sensitive.v1', 1, 'group',
                $3, 'forged_sensitive_event', $4, '{}'::jsonb, clock_timestamp(),
                'dead', NULL, 1, 1, clock_timestamp(), 'forged_sensitive_reason'
            );
            """))
        {
            seed.Parameters.AddWithValue(messageId.Value);
            seed.Parameters.AddWithValue($"integration:observability:{messageId}");
            seed.Parameters.AddWithValue(EntityId.New().Value);
            seed.Parameters.AddWithValue(EntityId.New().Value);
            Assert.Equal(
                1,
                await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
        }

        IUnitOfWorkFactory factory = _fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        await using (unitOfWork.ConfigureAwait(true))
        {
            OutboxObservabilitySnapshot snapshot = await new PostgresOutboxObservabilityStore()
                .ReadAsync(unitOfWork.Context, cancellationToken).ConfigureAwait(true);
            Assert.Contains(snapshot.Dead, static metric =>
                string.Equals(metric.Topic, "other", StringComparison.Ordinal)
                && string.Equals(metric.EventType, "other", StringComparison.Ordinal)
                && string.Equals(metric.Reason, "unknown", StringComparison.Ordinal)
                && metric.Count >= 1);
        }
    }

    private async ValueTask<RoutedTakeoverClaims> ClaimInitialAndTakeoverAsync(
        IntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        EntityId firstOwner = EntityId.New();
        OutboxDeliveryMessage first = Assert.Single(await ClaimRoutedAndCommitAsync(
            maximumCount: 1,
            cancellationToken,
            firstOwner).ConfigureAwait(false));
        Assert.Equal(firstOwner, first.Envelope.Lease.Owner);
        Assert.Equal(1, first.Envelope.Lease.Attempt);
        Assert.Equal(1, first.Envelope.Lease.Generation);
        await _fixture.ForceOutboxLeaseExpiredAsync(
            integrationEvent.MessageId.Value,
            cancellationToken).ConfigureAwait(false);

        EntityId takeoverOwner = EntityId.New();
        OutboxDeliveryMessage takeover = Assert.Single(await ClaimRoutedAndCommitAsync(
            maximumCount: 1,
            cancellationToken,
            takeoverOwner).ConfigureAwait(false));
        Assert.Equal(takeoverOwner, takeover.Envelope.Lease.Owner);
        Assert.Equal(2, takeover.Envelope.Lease.Attempt);
        Assert.Equal(2, takeover.Envelope.Lease.Generation);
        return new RoutedTakeoverClaims(first, takeover);
    }

    private async ValueTask AssertRoutedStaleLeaseIsFencedAsync(
        RoutedTakeoverClaims claims,
        CancellationToken cancellationToken)
    {
        IOutboxDeliveryStore store = _fixture.WorkerServices
            .GetRequiredService<IOutboxDeliveryStore>();
        IUnitOfWorkFactory factory = _fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            Assert.False(await store.HeartbeatAsync(
                claims.First.Envelope.Lease,
                TimeSpan.FromMinutes(1),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            Assert.False(await store.MarkPublishedAsync(
                claims.First.Envelope.Lease,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            Assert.False(await store.ReleaseForRetryAsync(
                claims.First.Envelope.Lease,
                TimeSpan.FromMinutes(1),
                "stale_owner",
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            Assert.False(await store.MarkDeadAsync(
                claims.First.Envelope.Lease,
                "stale_owner",
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            Assert.True(await store.MarkPublishedAsync(
                claims.Takeover.Envelope.Lease,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask AppendAndIsolateAsync(
        IReadOnlyCollection<IntegrationEvent> events,
        CancellationToken cancellationToken)
    {
        await SeedQuotaEventFactsAsync(
            events.Where(static item => string.Equals(
                item.Topic,
                QuotaTopic,
                StringComparison.Ordinal)),
            cancellationToken).ConfigureAwait(false);
        IUnitOfWorkFactory factory = _fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IOutboxAppender appender = _fixture.ApiServices.GetRequiredService<IOutboxAppender>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            foreach (IntegrationEvent integrationEvent in events)
            {
                await appender.AppendAsync(
                    integrationEvent,
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
            }

            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await _fixture.DeferOtherPendingOutboxAsync(
            events.Select(static item => item.MessageId.Value).ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SeedQuotaEventFactsAsync(
        IEnumerable<IntegrationEvent> events,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (IntegrationEvent integrationEvent in events)
        {
            JsonElement payload = integrationEvent.Payload;
            EntityId groupId = integrationEvent.AggregateId;
            Guid periodId = payload.GetProperty("period_id").GetGuid();
            Assert.InRange(await ExecuteSeedAsync(
                connection,
                transaction,
                SeedQuotaGroupSql,
                [groupId.Value],
                cancellationToken).ConfigureAwait(false), 0, 1);
            Assert.InRange(await ExecuteSeedAsync(
                connection,
                transaction,
                SeedQuotaAccountSql,
                [groupId.Value, periodId],
                cancellationToken).ConfigureAwait(false), 0, 1);
            Assert.InRange(await ExecuteSeedAsync(
                connection,
                transaction,
                SeedQuotaPeriodSql,
                [periodId, groupId.Value, integrationEvent.OccurredAt],
                cancellationToken).ConfigureAwait(false), 0, 1);
            Assert.Equal(1, await ExecuteSeedAsync(
                connection,
                transaction,
                SeedQuotaEventFactSql,
                [
                    payload.GetProperty("event_id").GetGuid(),
                    integrationEvent.SourceEventSequence!.Value,
                    groupId.Value,
                    periodId,
                    integrationEvent.EventType,
                    integrationEvent.DeduplicationKey,
                    payload.GetProperty("metadata").GetRawText(),
                    integrationEvent.OccurredAt,
                ],
                cancellationToken).ConfigureAwait(false));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ExecuteSeedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        IReadOnlyList<object> parameters,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (object value in parameters)
        {
            command.Parameters.AddWithValue(value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<OutboxDeliveryMessage>> ClaimRoutedAndCommitAsync(
        int maximumCount,
        CancellationToken cancellationToken,
        EntityId? owner = null)
    {
        IUnitOfWorkFactory factory = _fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IOutboxDeliveryStore store = _fixture.WorkerServices
            .GetRequiredService<IOutboxDeliveryStore>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            IReadOnlyList<OutboxDeliveryMessage> claimed = await store.ClaimDueAsync(
                new OutboxClaimRequest(
                    owner ?? EntityId.New(),
                    [QuotaTopic],
                    maximumCount,
                    TimeSpan.FromMinutes(5)),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return claimed;
        }
    }

    private async ValueTask CompleteInitialClaimsAsync(
        OutboxDeliveryMessage dead,
        OutboxDeliveryMessage published,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IOutboxDeliveryStore store = _fixture.WorkerServices
            .GetRequiredService<IOutboxDeliveryStore>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            Assert.True(await store.MarkDeadAsync(
                dead.Envelope.Lease,
                "contract_mismatch",
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            Assert.True(await store.MarkPublishedAsync(
                published.Envelope.Lease,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask MarkPublishedAndCommitAsync(
        OutboxDeliveryMessage message,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IOutboxDeliveryStore store = _fixture.WorkerServices
            .GetRequiredService<IOutboxDeliveryStore>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            Assert.True(await store.MarkPublishedAsync(
                message.Envelope.Lease,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask MarkDeadAndCommitAsync(
        OutboxDeliveryMessage message,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IOutboxDeliveryStore store = _fixture.WorkerServices
            .GetRequiredService<IOutboxDeliveryStore>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            Assert.True(await store.MarkDeadAsync(
                message.Envelope.Lease,
                "contract_mismatch",
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask CorruptReplayPayloadAsync(
        EntityId messageId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.outbox_messages
            SET payload = jsonb_set(
                payload,
                '{tampered}',
                '"must-not-leak"'::jsonb,
                true)
            WHERE id = $1 AND status = 'pending';
            """);
        command.Parameters.AddWithValue(messageId.Value);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private OutboxPublisherProcessor CreateProcessor(
        IOperationalEventWriter operationalEventWriter,
        params IIntegrationEventConsumer[] additionalConsumers)
    {
        IIntegrationEventConsumer[] consumers =
        [
            .. _fixture.WorkerServices.GetServices<IIntegrationEventConsumer>(),
            .. additionalConsumers,
        ];
        return new OutboxPublisherProcessor(
            _fixture.WorkerServices.GetRequiredService<IUnitOfWorkFactory>(),
            _fixture.WorkerServices.GetRequiredService<IOutboxDeliveryStore>(),
            new IntegrationEventDispatcher(
                consumers,
                new PostgresIntegrationEventConsumerExceptionClassifier()),
            new ZeroJitter(),
            operationalEventWriter,
            new OutboxPublisherOptions(
                maximumAttempts: 3,
                pollInterval: TimeSpan.FromMilliseconds(10),
                claimDuration: TimeSpan.FromSeconds(30),
                retryBaseDelay: TimeSpan.FromSeconds(1),
                retryMaximumDelay: TimeSpan.FromMinutes(1)));
    }

    private async ValueTask<bool> InboxExistsAsync(
        EntityId messageId,
        CancellationToken cancellationToken,
        string consumerName = "usage-hourly-v1")
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM public.inbox_messages
                WHERE consumer_name = $1 AND message_id = $2
            );
            """);
        command.Parameters.AddWithValue(consumerName);
        command.Parameters.AddWithValue(messageId.Value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false) ?? false);
    }

    private async ValueTask<ReplayReceipt> ReplayAndCommitAsync(
        EntityId sourceMessageId,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IOutboxReplayRepository repository = _fixture.ApiServices
            .GetRequiredService<IOutboxReplayRepository>();
        EntityId replayMessageId = EntityId.New();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            OutboxReplayWriteResult write = await repository.ReplayDeadAsync(
                new OutboxReplayWrite(
                    sourceMessageId,
                    replayMessageId,
                    $"integration:ordering-replay:{replayMessageId}"),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            Assert.Equal(OutboxReplayPersistenceDisposition.Created, write.Disposition);
            Assert.Equal(replayMessageId, write.MessageId);
            Assert.True(write.EventSequence is > 0);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ReplayReceipt(replayMessageId, write.EventSequence!.Value);
        }
    }

    private async ValueTask<string> ReadStatusAsync(
        EntityId messageId,
        CancellationToken cancellationToken) =>
        (await ReadRuntimeStateAsync(messageId, cancellationToken).ConfigureAwait(false)).Status;

    private async ValueTask<OutboxRuntimeState> ReadRuntimeStateAsync(
        EntityId messageId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT status, publish_attempts, lock_generation
            FROM public.outbox_messages
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(messageId.Value);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new OutboxRuntimeState(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt64(2));
    }

    private static IntegrationEvent CreateQuotaEvent(EntityId groupId, long sourceSequence)
    {
        EntityId messageId = EntityId.New();
        EntityId correlationId = EntityId.New();
        DateTimeOffset occurredAt = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        return new IntegrationEvent(
            messageId,
            $"integration:ordering:{messageId}",
            QuotaTopic,
            SchemaVersion: 1,
            AggregateType: "group",
            AggregateId: groupId,
            AggregateVersion: null,
            EventType: "reserved",
            SourceEventSequence: sourceSequence,
            CorrelationId: correlationId,
            CausationId: null,
            Payload: JsonSerializer.SerializeToElement(new
            {
                schema_version = 1,
                event_id = EntityId.New().Value,
                source_event_sequence = sourceSequence,
                correlation_id = correlationId.Value,
                group_id = groupId.Value,
                period_id = groupId.Value,
                event_type = "reserved",
                delta_total_tokens = "0",
                delta_consumed_tokens = "0",
                delta_reserved_tokens = "1",
                total_tokens = "100",
                consumed_tokens = "0",
                reserved_tokens = "1",
                occurred_at = occurredAt,
                metadata = new { request_id = correlationId.Value.ToString("D") },
            }),
            OccurredAt: occurredAt);
    }

    private static long NextSourceSequenceBlock() =>
        Interlocked.Add(ref _nextSourceSequence, 10);

    private const string SeedQuotaGroupSql = """
        INSERT INTO public.groups (id, name, status)
        VALUES ($1, 'outbox-ordering-' || $1::text, 'disabled')
        ON CONFLICT (id) DO NOTHING;
        """;

    private const string SeedQuotaAccountSql = """
        INSERT INTO public.group_token_quotas (group_id, current_period_id)
        VALUES ($1, $2)
        ON CONFLICT (group_id) DO NOTHING;
        """;

    private const string SeedQuotaPeriodSql = """
        INSERT INTO public.group_quota_periods (
            id, group_id, period_number, total_tokens,
            consumed_tokens, reserved_tokens, status, opened_at
        ) VALUES ($1, $2, 1, 100, 0, 1, 'current', $3)
        ON CONFLICT (id) DO NOTHING;
        """;

    private const string SeedQuotaEventFactSql = """
        INSERT INTO public.group_quota_events (
            id, event_sequence, group_id, period_id, event_type,
            delta_total_tokens, delta_consumed_tokens, delta_reserved_tokens,
            total_tokens_after, consumed_tokens_after, reserved_tokens_after,
            actor_type, idempotency_key, metadata, occurred_at
        ) OVERRIDING SYSTEM VALUE VALUES (
            $1, $2, $3, $4, $5,
            0, 0, 1, 100, 0, 1,
            'worker', $6, $7::jsonb, $8
        );
        """;

    private static IntegrationEvent CreateEvent(
        EntityId aggregateId,
        string topic,
        string aggregateType,
        long sourceSequence)
    {
        EntityId messageId = EntityId.New();
        return new IntegrationEvent(
            messageId,
            $"integration:ordering:{messageId}",
            topic,
            SchemaVersion: 1,
            aggregateType,
            aggregateId,
            AggregateVersion: null,
            EventType: "settled",
            SourceEventSequence: sourceSequence,
            CorrelationId: EntityId.New(),
            CausationId: null,
            JsonSerializer.SerializeToElement(new
            {
                schema_version = 1,
                event_type = "settled",
                group_id = aggregateId.Value,
                source_event_sequence = sourceSequence,
            }),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private sealed record OutboxRuntimeState(
        string Status,
        int PublishAttempts,
        long LockGeneration);

    private sealed record RoutedTakeoverClaims(
        OutboxDeliveryMessage First,
        OutboxDeliveryMessage Takeover);

    private sealed class RecordingOperationalEventWriter : IOperationalEventWriter
    {
        internal List<RecordedOperationalEvent> Events { get; } = [];

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(new RecordedOperationalEvent(eventName, payload.Clone()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RollbackThenProcessConsumer(
        IUnitOfWorkFactory unitOfWorkFactory,
        IInboxReceiptAppender inboxReceiptAppender) : IIntegrationEventConsumer
    {
        internal const string ConsumerName = "zz-secondary-v1";
        private int _calls;

        public IntegrationEventSubscription Subscription { get; } = new(
            ConsumerName,
            QuotaTopic,
            schemaVersion: 1);

        internal int Calls => Volatile.Read(ref _calls);

        public async ValueTask<IntegrationEventConsumeResult> ConsumeAsync(
            OutboxDeliveryMessage message,
            CancellationToken cancellationToken)
        {
            byte[] payloadHash = SHA256.HashData(
                Encoding.UTF8.GetBytes(message.Envelope.Payload.GetRawText()));
            IUnitOfWork unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (unitOfWork.ConfigureAwait(false))
            {
                InboxReceiptAppendResult receipt = await inboxReceiptAppender.AppendAsync(
                    new InboxReceipt(
                        ConsumerName,
                        message.Envelope.Lease.MessageId,
                        message.Envelope.Topic,
                        message.Envelope.EventSequence,
                        message.Envelope.SchemaVersion,
                        payloadHash),
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
                if (receipt.Disposition != InboxReceiptDisposition.Inserted)
                {
                    return IntegrationEventConsumeResult.Poison("secondary_inbox_conflict");
                }

                if (Interlocked.Increment(ref _calls) == 1)
                {
                    return IntegrationEventConsumeResult.Poison(
                        "secondary_contract_mismatch");
                }

                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                return IntegrationEventConsumeResult.Processed;
            }
        }
    }

    private sealed class ZeroJitter : IOutboxRetryJitter
    {
        public double NextFraction() => 0;
    }

    private sealed class OwnedJobLock : IWorkerSessionLock
    {
        public WorkerJobIdentity Job => WorkerJobs.OutboxPublisher;

        public long LockId => 1;

        public ValueTask<bool> VerifyOwnershipAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record RecordedOperationalEvent(string Name, JsonElement Payload);

    private sealed record ReplayReceipt(EntityId MessageId, long EventSequence);
}
