using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure;
using PoolAI.Modules.Operations.Infrastructure.Observability;
using PoolAI.Modules.Operations.Infrastructure.Workers;
using PoolAI.Modules.Operations.Worker;

namespace PoolAI.UnitTests;

public sealed class OperationsOutboxPublisherTests
{
    private const string QuotaTopic = "poolai.quota.v1";
    private static readonly IIntegrationEventConsumerExceptionClassifier ExceptionClassifier =
        new PostgresIntegrationEventConsumerExceptionClassifier();

    [Fact]
    public void InboxReplayProofRejectsEveryNonCanonicalIdentityBoundary()
    {
        byte[] hash = new byte[32];

        Assert.Throws<ArgumentException>(() => new InboxReplayPredecessorProof(
            " usage-hourly-v1",
            EntityId.New(),
            QuotaTopic,
            schemaVersion: 1,
            hash));
        Assert.Throws<ArgumentException>(() => new InboxReplayPredecessorProof(
            "usage-hourly-v1",
            EntityId.New(),
            new string('q', 129),
            schemaVersion: 1,
            hash));
        Assert.Throws<ArgumentException>(() => new InboxReplayPredecessorProof(
            "usage-hourly-v1",
            default,
            QuotaTopic,
            schemaVersion: 1,
            hash));
        Assert.Throws<ArgumentException>(() => new InboxReplayPredecessorProof(
            "usage-hourly-v1",
            EntityId.New(),
            QuotaTopic,
            schemaVersion: 1,
            new byte[31]));
    }

    [Fact]
    public void OutboxClaimRejectsNonCanonicalOwnerAndTopics()
    {
        Assert.Throws<ArgumentException>(() => new OutboxClaimRequest(
            default,
            [QuotaTopic],
            maximumCount: 1,
            TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentException>(() => new OutboxClaimRequest(
            EntityId.New(),
            [" " + QuotaTopic],
            maximumCount: 1,
            TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentException>(() => new OutboxClaimRequest(
            EntityId.New(),
            [new string('q', 129)],
            maximumCount: 1,
            TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void OutboxDeliveryRejectsNonCanonicalPartitionIdentity()
    {
        OutboxMessageEnvelope envelope = CreateMessage().Envelope;

        Assert.Throws<ArgumentNullException>(() => new OutboxDeliveryMessage(
            null!,
            $"{QuotaTopic}:group:test",
            partitionSequence: 1));
        Assert.Throws<ArgumentException>(() => new OutboxDeliveryMessage(
            envelope,
            $" {QuotaTopic}:group:test",
            partitionSequence: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxDeliveryMessage(
            envelope,
            $"{QuotaTopic}:group:test",
            partitionSequence: 0));
    }

    [Theory]
    [InlineData("Upper_case")]
    [InlineData("hyphen-not-allowed")]
    public void ConsumerFailureReasonRejectsNonCanonicalVocabulary(string reason)
    {
        Assert.Throws<ArgumentException>(
            () => IntegrationEventConsumeResult.RetryableFailure(reason));
        Assert.Throws<ArgumentException>(
            () => IntegrationEventConsumeResult.Poison(reason));
    }

    [Fact]
    public void IntegrationSubscriptionRejectsNonCanonicalNames()
    {
        Assert.Throws<ArgumentException>(() => new IntegrationEventSubscription(
            " usage-hourly-v1",
            QuotaTopic,
            schemaVersion: 1));
        Assert.Throws<ArgumentException>(() => new IntegrationEventSubscription(
            "usage-hourly-v1",
            new string('q', 129),
            schemaVersion: 1));
    }

    [Fact]
    public async Task DispatcherRequiresExplicitUniqueRoutesAndRejectsUnknownSchema()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new IntegrationEventDispatcher([], ExceptionClassifier));
        Assert.Throws<InvalidOperationException>(() => new IntegrationEventDispatcher(
        [
            new ScriptedConsumer("usage-hourly-v1", QuotaTopic, 1),
            new ScriptedConsumer("usage-hourly-v1", QuotaTopic, 1),
        ], ExceptionClassifier));

        ScriptedConsumer consumer = new("usage-hourly-v1", QuotaTopic, 1);
        IntegrationEventDispatcher dispatcher = new([consumer], ExceptionClassifier);

        IntegrationEventConsumeResult unknownSchema = await dispatcher.DispatchAsync(
            CreateMessage(schemaVersion: 2),
            TestContext.Current.CancellationToken);
        IntegrationEventConsumeResult unregisteredTopic = await dispatcher.DispatchAsync(
            CreateMessage(topic: "poolai.identity.v1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            IntegrationEventConsumeDisposition.Poison,
            unknownSchema.Disposition);
        Assert.Equal("unsupported_schema_version", unknownSchema.Reason);
        Assert.Equal(
            IntegrationEventConsumeDisposition.Poison,
            unregisteredTopic.Disposition);
        Assert.Equal("unregistered_topic", unregisteredTopic.Reason);
        Assert.Empty(consumer.Messages);
    }

    [Fact]
    public async Task DispatcherRunsMatchingConsumersInOrdinalOrderAndStopsOnFailure()
    {
        List<string> order = [];
        ScriptedConsumer second = new(
            "usage-z",
            QuotaTopic,
            1,
            _ =>
            {
                order.Add("z");
                return IntegrationEventConsumeResult.RetryableFailure("dependency_unavailable");
            });
        ScriptedConsumer first = new(
            "usage-a",
            QuotaTopic,
            1,
            _ =>
            {
                order.Add("a");
                return IntegrationEventConsumeResult.Processed;
            });
        ScriptedConsumer never = new(
            "usage-zz",
            QuotaTopic,
            1,
            _ =>
            {
                order.Add("zz");
                return IntegrationEventConsumeResult.Processed;
            });
        IntegrationEventDispatcher dispatcher = new(
            [second, never, first],
            ExceptionClassifier);

        IntegrationEventConsumeResult result = await dispatcher.DispatchAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            IntegrationEventConsumeDisposition.RetryableFailure,
            result.Disposition);
        Assert.Equal(["a", "z"], order);
        Assert.Empty(never.Messages);
    }

    [Fact]
    public async Task DispatcherRetriesOnlyExplicitTransientExceptions()
    {
        IntegrationEventConsumeResult timeout = await new IntegrationEventDispatcher(
        [
            new ScriptedConsumer(
                "usage-hourly-v1",
                QuotaTopic,
                1,
                _ => throw new TimeoutException("sensitive timeout detail")),
        ], ExceptionClassifier).DispatchAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);
        IntegrationEventConsumeResult transientPostgres = await new IntegrationEventDispatcher(
        [
            new ScriptedConsumer(
                "usage-hourly-v1",
                QuotaTopic,
                1,
                _ => throw new NpgsqlException(
                    "sensitive transport detail",
                    new TimeoutException())),
        ], ExceptionClassifier).DispatchAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);
        IntegrationEventConsumeResult unexpected = await new IntegrationEventDispatcher(
        [
            new ScriptedConsumer(
                "usage-hourly-v1",
                QuotaTopic,
                1,
                _ => throw new InvalidOperationException("sensitive invariant detail")),
        ], ExceptionClassifier).DispatchAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);

        Assert.Equal(IntegrationEventConsumeDisposition.RetryableFailure, timeout.Disposition);
        Assert.Equal("dependency_unavailable", timeout.Reason);
        Assert.Equal(
            IntegrationEventConsumeDisposition.RetryableFailure,
            transientPostgres.Disposition);
        Assert.Equal("dependency_unavailable", transientPostgres.Reason);
        Assert.Equal(IntegrationEventConsumeDisposition.Poison, unexpected.Disposition);
        Assert.Equal("consumer_exception", unexpected.Reason);
    }

    [Fact]
    public async Task PublisherClaimsOnlyExplicitTopicAfterClaimTransactionAndPublishes()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingOutboxStore store = new(CreateMessage());
        ScriptedConsumer consumer = new(
            "usage-hourly-v1",
            QuotaTopic,
            1,
            _ =>
            {
                Assert.Equal(0, unitOfWorkFactory.ActiveCount);
                return IntegrationEventConsumeResult.Processed;
            });
        OutboxPublisherProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            consumer);

        OutboxPublishProcessResult result = await processor.ProcessNextAsync(
            new OwnedJobLock(),
            TestContext.Current.CancellationToken);

        Assert.Equal(OutboxPublishProcessResult.Processed, result);
        Assert.Equal([QuotaTopic], Assert.Single(store.ClaimRequests).Topics);
        Assert.Equal(1, store.PublishedCount);
        Assert.Equal(0, store.RetryCount);
        Assert.Equal(0, store.DeadCount);
        Assert.Equal(0, unitOfWorkFactory.ActiveCount);
        OutboxDeliveryMessage consumed = Assert.Single(consumer.Messages);
        Assert.Equal(7, consumed.PartitionSequence);
        Assert.Equal(
            $"{QuotaTopic}:group:{consumed.Envelope.AggregateId.Value:D}",
            consumed.PartitionKey);
    }

    [Fact]
    public async Task PublisherPassesExactCompletedReplayThroughConsumerBeforeConvergence()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingOutboxStore store = new(CreateMessage(lineageAlreadyPublished: true));
        ScriptedConsumer consumer = new(
            "usage-hourly-v1",
            QuotaTopic,
            1,
            message => message.LineageAlreadyPublished
                ? IntegrationEventConsumeResult.Duplicate
                : IntegrationEventConsumeResult.Poison("missing_lineage_proof"));
        OutboxPublisherProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            consumer);

        OutboxPublishProcessResult result = await processor.ProcessNextAsync(
            new OwnedJobLock(),
            TestContext.Current.CancellationToken);

        Assert.Equal(OutboxPublishProcessResult.Processed, result);
        Assert.True(Assert.Single(consumer.Messages).LineageAlreadyPublished);
        Assert.Equal(1, store.PublishedCount);
        Assert.Equal(0, store.RetryCount);
        Assert.Equal(0, store.DeadCount);
        Assert.Equal(0, unitOfWorkFactory.ActiveCount);
    }

    [Fact]
    public async Task PoisonGoesDirectlyDeadAndEmitsOnlyBoundedDiagnostics()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingOutboxStore store = new(CreateMessage(
            payload: JsonSerializer.SerializeToElement(new { secret = "must-not-leak" })));
        RecordingOperationalEventWriter events = new(unitOfWorkFactory);
        OutboxPublisherProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            new ScriptedConsumer(
                "usage-hourly-v1",
                QuotaTopic,
                1,
                _ => IntegrationEventConsumeResult.Poison("envelope_payload_mismatch")),
            events);

        OutboxPublishProcessResult result = await processor.ProcessNextAsync(
            new OwnedJobLock(),
            TestContext.Current.CancellationToken);

        Assert.Equal(OutboxPublishProcessResult.Processed, result);
        Assert.Equal(1, store.DeadCount);
        Assert.Equal("envelope_payload_mismatch", store.LastError);
        Assert.Equal(0, store.RetryCount);
        RecordedOperationalEvent dead = Assert.Single(events.Events);
        Assert.Equal("outbox_poison_dead", dead.Name);
        Assert.Equal("P0", dead.Payload.GetProperty("severity").GetString());
        Assert.Equal(
            ["attempt", "event_type", "reason", "schema_version", "severity", "topic"],
            dead.Payload.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain(
            "must-not-leak",
            dead.Payload.GetRawText(),
            StringComparison.Ordinal);
        Assert.Equal(QuotaTopic, dead.Payload.GetProperty("topic").GetString());
        Assert.Equal("settled", dead.Payload.GetProperty("event_type").GetString());
        Assert.Equal("unknown", dead.Payload.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task TransientFailureRetriesThenMaximumAttemptGoesDead()
    {
        TrackingUnitOfWorkFactory retryUnitOfWorkFactory = new();
        RecordingOutboxStore retryStore = new(CreateMessage(attempt: 1));
        OutboxPublisherProcessor retryProcessor = CreateProcessor(
            retryUnitOfWorkFactory,
            retryStore,
            new ScriptedConsumer(
                "usage-hourly-v1",
                QuotaTopic,
                1,
                _ => IntegrationEventConsumeResult.RetryableFailure(
                    "dependency_unavailable")));

        Assert.Equal(
            OutboxPublishProcessResult.Processed,
            await retryProcessor.ProcessNextAsync(
                new OwnedJobLock(),
                TestContext.Current.CancellationToken));
        Assert.Equal(1, retryStore.RetryCount);
        Assert.Equal(TimeSpan.FromSeconds(1), retryStore.LastRetryDelay);
        Assert.Equal("dependency_unavailable", retryStore.LastError);
        Assert.Equal(0, retryStore.DeadCount);

        TrackingUnitOfWorkFactory deadUnitOfWorkFactory = new();
        RecordingOutboxStore deadStore = new(CreateMessage(attempt: 3));
        RecordingOperationalEventWriter events = new(deadUnitOfWorkFactory);
        OutboxPublisherProcessor deadProcessor = CreateProcessor(
            deadUnitOfWorkFactory,
            deadStore,
            new ScriptedConsumer(
                "usage-hourly-v1",
                QuotaTopic,
                1,
                _ => IntegrationEventConsumeResult.RetryableFailure(
                    "dependency_unavailable")),
            events);

        Assert.Equal(
            OutboxPublishProcessResult.Processed,
            await deadProcessor.ProcessNextAsync(
                new OwnedJobLock(),
                TestContext.Current.CancellationToken));
        Assert.Equal(1, deadStore.DeadCount);
        Assert.Equal("maximum_attempts", deadStore.LastError);
        Assert.Equal(
            "outbox_max_attempts_dead",
            Assert.Single(events.Events).Name);
    }

    [Fact]
    public async Task LostLeaseHeartbeatCancelsConsumerAndForbidsTerminalWrite()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingOutboxStore store = new(CreateMessage())
        {
            HeartbeatResult = false,
        };
        ScriptedConsumer consumer = ScriptedConsumer.WaitForCancellation(
            "usage-hourly-v1",
            QuotaTopic,
            1);
        OutboxPublisherProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            consumer,
            claimDuration: TimeSpan.FromMilliseconds(15));

        OutboxPublishProcessResult result = await processor.ProcessNextAsync(
            new OwnedJobLock(),
            TestContext.Current.CancellationToken);

        Assert.Equal(OutboxPublishProcessResult.OwnershipLost, result);
        Assert.True(consumer.WasCancelled);
        Assert.Equal(1, store.HeartbeatCount);
        Assert.Equal(0, store.TerminalTransitionCount);
        Assert.Equal(0, unitOfWorkFactory.ActiveCount);
    }

    [Fact]
    public async Task ConsumerCommitBeforeTerminalFailureRedeliversAsExactDuplicate()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        OutboxDeliveryMessage first = CreateMessage(attempt: 1);
        OutboxDeliveryMessage takeover = CreateMessage(
            messageId: first.Envelope.Lease.MessageId,
            aggregateId: first.Envelope.AggregateId,
            attempt: 2,
            generation: 2);
        RecordingOutboxStore store = new(first, takeover)
        {
            PublishedExceptionCount = 1,
        };
        int calls = 0;
        ScriptedConsumer consumer = new(
            "usage-hourly-v1",
            QuotaTopic,
            1,
            _ => Interlocked.Increment(ref calls) == 1
                ? IntegrationEventConsumeResult.Processed
                : IntegrationEventConsumeResult.Duplicate);
        OutboxPublisherProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            store,
            consumer);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await processor.ProcessNextAsync(
                new OwnedJobLock(),
                TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(
            OutboxPublishProcessResult.Processed,
            await processor.ProcessNextAsync(
                new OwnedJobLock(),
                TestContext.Current.CancellationToken));

        Assert.Equal(2, calls);
        Assert.Equal(1, store.PublishedCount);
        Assert.Equal(0, store.DeadCount);
        Assert.Equal(0, store.RetryCount);
    }

    [Fact]
    public async Task DurableMetricsUseFrozenNamesAndBoundedLabels()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingObservabilityStore store = new(new OutboxObservabilitySnapshot(
            [new OutboxBacklogMetric("settled", 3, 42.5)],
            [new OutboxTerminalMetric(
                QuotaTopic,
                "settled",
                "envelope_payload_mismatch",
                1)],
            [new OutboxTerminalMetric(QuotaTopic, "settled", "created", 2)]));
        using OutboxPublisherMetrics metrics = new(unitOfWorkFactory, store);
        List<MetricReading> readings = await ObserveMetricsAsync(metrics);

        Assert.Contains(readings, static reading =>
            string.Equals(reading.Name, "poolai_outbox_pending", StringComparison.Ordinal)
            && reading.Value == 3);
        Assert.Contains(readings, static reading =>
            string.Equals(
                reading.Name,
                "poolai_outbox_oldest_age_seconds",
                StringComparison.Ordinal)
            && reading.Value == 42.5);
        MetricReading dead = Assert.Single(readings, static reading =>
            string.Equals(reading.Name, "poolai_outbox_dead_total", StringComparison.Ordinal));
        MetricReading replay = Assert.Single(readings, static reading =>
            string.Equals(reading.Name, "poolai_outbox_replay_total", StringComparison.Ordinal));
        Assert.Equal(["topic", "event_type", "reason"], dead.Tags.Select(static tag => tag.Key));
        Assert.Equal(
            [QuotaTopic, "settled", "unknown"],
            dead.Tags.Select(static tag => tag.Value));
        Assert.Equal([QuotaTopic, "settled", "created"], replay.Tags.Select(static tag => tag.Value));
    }

    [Fact]
    public async Task OperationalLoggerNeverEmitsUnknownClassifierValues()
    {
        const string forgedTopic = "poolai.forged-sensitive.v1";
        const string forgedEvent = "forged_sensitive_event";
        const string forgedReason = "forged_sensitive_reason";
        RecordingLogger<LoggingOperationalEventWriter> logger = new();
        LoggingOperationalEventWriter writer = new(logger);

        await writer.WriteAsync(
            "outbox_poison_dead",
            JsonSerializer.SerializeToElement(new
            {
                topic = forgedTopic,
                event_type = forgedEvent,
                reason = forgedReason,
                nested = new { topic = forgedTopic },
            }),
            TestContext.Current.CancellationToken);

        string message = Assert.Single(logger.Messages);
        Assert.DoesNotContain(forgedTopic, message, StringComparison.Ordinal);
        Assert.DoesNotContain(forgedEvent, message, StringComparison.Ordinal);
        Assert.DoesNotContain(forgedReason, message, StringComparison.Ordinal);
        Assert.Contains("\"topic\":\"other\"", message, StringComparison.Ordinal);
        Assert.Contains("\"event_type\":\"other\"", message, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"unknown\"", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricsAggregateMoreThanOneHundredTwentyEightUnknownLabels()
    {
        OutboxBacklogMetric[] backlog = Enumerable.Range(0, 200)
            .Select(static value => new OutboxBacklogMetric(
                $"forged_event_{value}",
                PendingCount: 1,
                OldestAgeSeconds: value))
            .ToArray();
        OutboxTerminalMetric[] dead = Enumerable.Range(0, 200)
            .Select(static value => new OutboxTerminalMetric(
                $"poolai.forged-{value}.v1",
                $"forged_event_{value}",
                $"forged_reason_{value}",
                Count: 1))
            .ToArray();
        using OutboxPublisherMetrics metrics = new(
            new TrackingUnitOfWorkFactory(),
            new RecordingObservabilityStore(new OutboxObservabilitySnapshot(
                backlog,
                dead,
                [])));

        List<MetricReading> readings = await ObserveMetricsAsync(metrics);

        MetricReading pending = Assert.Single(readings, static reading =>
            string.Equals(reading.Name, "poolai_outbox_pending", StringComparison.Ordinal));
        MetricReading oldest = Assert.Single(readings, static reading =>
            string.Equals(
                reading.Name,
                "poolai_outbox_oldest_age_seconds",
                StringComparison.Ordinal));
        MetricReading terminal = Assert.Single(readings, static reading =>
            string.Equals(reading.Name, "poolai_outbox_dead_total", StringComparison.Ordinal));
        Assert.Equal(200, pending.Value);
        Assert.Equal("other", Assert.Single(pending.Tags).Value);
        Assert.Equal(199, oldest.Value);
        Assert.Equal(["other", "other", "unknown"],
            terminal.Tags.Select(static tag => tag.Value));
        Assert.Equal(200, terminal.Value);
    }

    [Fact]
    public void WorkerRegistrationIsIdempotentAndCoreRegistrationHasNoHostedLoop()
    {
        ServiceCollection services = new();
        IConfiguration configuration = WorkerConfiguration();

        services.AddOperationsModule(configuration, "Development");
        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(IHostedService));

        services.AddOperationsOutboxPublisher(configuration);
        services.AddOperationsOutboxPublisher(configuration);

        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(OutboxPublisherService));
        Assert.Contains(services, static descriptor =>
            descriptor.ServiceType == typeof(IntegrationEventDispatcher));
        Assert.Contains(services, static descriptor =>
            descriptor.ServiceType == typeof(IOutboxObservabilityStore));

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        Assert.Same(
            serviceProvider.GetRequiredService<IAuditAppender>(),
            serviceProvider.GetRequiredService<IIdempotentAuditAppender>());
        Assert.Same(
            serviceProvider.GetRequiredService<IInboxReceiptAppender>(),
            serviceProvider.GetRequiredService<IInboxReplayPredecessorVerifier>());
    }

    [Fact]
    public async Task HostedPublisherStartsClaimsItsWorkerJobAndStopsCleanly()
    {
        TrackingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingOutboxStore deliveryStore = new();
        OutboxPublisherProcessor processor = CreateProcessor(
            unitOfWorkFactory,
            deliveryStore,
            new ScriptedConsumer("usage-hourly-v1", QuotaTopic, 1));
        using OutboxPublisherMetrics metrics = new(
            unitOfWorkFactory,
            new RecordingObservabilityStore(OutboxObservabilitySnapshot.Empty));
        RecordingLockProvider lockProvider = new();
        using OutboxPublisherService service = new(
            lockProvider,
            processor,
            metrics,
            new OutboxPublisherOptions(
                maximumAttempts: 3,
                pollInterval: TimeSpan.FromMilliseconds(10),
                claimDuration: TimeSpan.FromSeconds(30),
                retryBaseDelay: TimeSpan.FromSeconds(1),
                retryMaximumDelay: TimeSpan.FromMinutes(1)),
            NullLogger<OutboxPublisherService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await deliveryStore.ClaimObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(lockProvider.AcquireCount >= 1);
        Assert.Equal(WorkerJobs.OutboxPublisher, lockProvider.RequestedJob);
        Assert.Equal(0, unitOfWorkFactory.ActiveCount);
    }

    [Theory]
    [InlineData("poll", "0")]
    [InlineData("claim", "9")]
    [InlineData("max-attempts", "51")]
    [InlineData("retry-order", "1")]
    public void WorkerOptionsFailClosedAtConfigurationBoundaries(
        string scenario,
        string value)
    {
        Dictionary<string, string?> overrides = scenario switch
        {
            "poll" => new(StringComparer.Ordinal) { ["Outbox:PollSeconds"] = value },
            "claim" => new(StringComparer.Ordinal) { ["Outbox:ClaimSeconds"] = value },
            "max-attempts" => new(StringComparer.Ordinal) { ["Outbox:MaxAttempts"] = value },
            "retry-order" => new(StringComparer.Ordinal)
            {
                ["Outbox:RetryBaseSeconds"] = "2",
                ["Outbox:RetryMaxSeconds"] = value,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Throws<InvalidOperationException>(() =>
            OutboxPublisherOptions.FromConfiguration(WorkerConfiguration(overrides)));
    }

    private static OutboxPublisherProcessor CreateProcessor(
        TrackingUnitOfWorkFactory unitOfWorkFactory,
        RecordingOutboxStore store,
        ScriptedConsumer consumer,
        RecordingOperationalEventWriter? events = null,
        TimeSpan? claimDuration = null) => new(
        unitOfWorkFactory,
        store,
        new IntegrationEventDispatcher([consumer], ExceptionClassifier),
        new ZeroJitter(),
        events ?? new RecordingOperationalEventWriter(unitOfWorkFactory),
        new OutboxPublisherOptions(
            maximumAttempts: 3,
            pollInterval: TimeSpan.FromMilliseconds(10),
            claimDuration ?? TimeSpan.FromSeconds(30),
            retryBaseDelay: TimeSpan.FromSeconds(1),
            retryMaximumDelay: TimeSpan.FromMinutes(5)));

    private static OutboxDeliveryMessage CreateMessage(
        EntityId? messageId = null,
        EntityId? aggregateId = null,
        int attempt = 1,
        long generation = 1,
        string topic = QuotaTopic,
        int schemaVersion = 1,
        JsonElement? payload = null,
        bool lineageAlreadyPublished = false)
    {
        EntityId owner = EntityId.New();
        EntityId groupId = aggregateId ?? EntityId.New();
        OutboxMessageEnvelope envelope = new(
            new OutboxDeliveryLease(messageId ?? EntityId.New(), owner, generation, attempt),
            EventSequence: 20 + attempt,
            DeduplicationKey: $"quota:test:{EntityId.New()}",
            Topic: topic,
            SchemaVersion: schemaVersion,
            AggregateType: "group",
            AggregateId: groupId,
            AggregateVersion: null,
            EventType: "settled",
            SourceEventSequence: 7,
            CorrelationId: EntityId.New(),
            CausationId: null,
            Payload: payload ?? JsonSerializer.SerializeToElement(new { event_type = "settled" }),
            OccurredAt: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            ReplayOf: null);
        return new OutboxDeliveryMessage(
            envelope,
            $"{topic}:group:{groupId.Value:D}",
            partitionSequence: 7,
            lineageAlreadyPublished: lineageAlreadyPublished);
    }

    private static IConfiguration WorkerConfiguration(
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            ["Data:Redis:ConnectionString"] = "localhost:6379,abortConnect=false",
            ["Data:Redis:KeyPrefix"] = "poolai:r1:test",
            ["Health:Ntp:Server"] = "ntp.example.test",
            ["Outbox:MaxAttempts"] = "12",
            ["Outbox:PollSeconds"] = "1",
            ["Outbox:ClaimSeconds"] = "30",
            ["Outbox:RetryBaseSeconds"] = "1",
            ["Outbox:RetryMaxSeconds"] = "300",
        };
        if (overrides is not null)
        {
            foreach ((string key, string? item) in overrides)
            {
                values[key] = item;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async ValueTask<List<MetricReading>> ObserveMetricsAsync(
        OutboxPublisherMetrics metrics)
    {
        using MeterListener listener = new();
        List<MetricReading> readings = [];
        listener.InstrumentPublished = (instrument, candidate) =>
        {
            if (string.Equals(
                    instrument.Meter.Name,
                    OutboxPublisherMetrics.MeterName,
                    StringComparison.Ordinal))
            {
                candidate.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            readings.Add(new MetricReading(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            readings.Add(new MetricReading(instrument.Name, value, tags.ToArray())));
        listener.Start();
        await metrics.RefreshIfDueAsync(
            force: true,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        listener.RecordObservableInstruments();
        return readings;
    }

    private sealed class TrackingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        private int _activeCount;

        internal int ActiveCount => Volatile.Read(ref _activeCount);

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _activeCount);
            return ValueTask.FromResult<IUnitOfWork>(new TrackingUnitOfWork(this));
        }

        private sealed class TrackingUnitOfWork(TrackingUnitOfWorkFactory owner) : IUnitOfWork
        {
            private int _disposed;

            public IUnitOfWorkContext Context { get; } = new TrackingContext();

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _ = Interlocked.Decrement(ref owner._activeCount);
                }

                return ValueTask.CompletedTask;
            }
        }

        private sealed class TrackingContext : IUnitOfWorkContext;
    }

    private sealed class RecordingOutboxStore(params OutboxDeliveryMessage[] messages)
        : IOutboxDeliveryStore
    {
        private readonly Queue<OutboxDeliveryMessage> _messages = new(messages);

        internal List<OutboxClaimRequest> ClaimRequests { get; } = [];

        internal TaskCompletionSource<bool> ClaimObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool HeartbeatResult { get; init; } = true;

        internal int PublishedExceptionCount { get; init; }

        private int PublishedCalls { get; set; }

        internal int HeartbeatCount { get; private set; }

        internal int PublishedCount { get; private set; }

        internal int RetryCount { get; private set; }

        internal int DeadCount { get; private set; }

        internal int TerminalTransitionCount => PublishedCount + RetryCount + DeadCount;

        internal string? LastError { get; private set; }

        internal TimeSpan LastRetryDelay { get; private set; }

        public ValueTask<IReadOnlyList<OutboxDeliveryMessage>> ClaimDueAsync(
            OutboxClaimRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClaimRequests.Add(request);
            ClaimObserved.TrySetResult(true);
            if (_messages.Count == 0)
            {
                return ValueTask.FromResult<IReadOnlyList<OutboxDeliveryMessage>>([]);
            }

            OutboxDeliveryMessage queued = _messages.Dequeue();
            OutboxDeliveryMessage claimed = new(
                queued.Envelope with
                {
                    Lease = queued.Envelope.Lease with { Owner = request.Owner },
                },
                queued.PartitionKey,
                queued.PartitionSequence,
                queued.LineageAlreadyPublished);
            return ValueTask.FromResult<IReadOnlyList<OutboxDeliveryMessage>>([claimed]);
        }

        public ValueTask<IReadOnlyList<OutboxMessageEnvelope>> ClaimDueAsync(
            EntityId owner,
            int maximumCount,
            TimeSpan leaseDuration,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<bool> HeartbeatAsync(
            OutboxDeliveryLease lease,
            TimeSpan leaseDuration,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HeartbeatCount++;
            return ValueTask.FromResult(HeartbeatResult);
        }

        public ValueTask<bool> MarkPublishedAsync(
            OutboxDeliveryLease lease,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishedCalls++;
            if (PublishedCalls <= PublishedExceptionCount)
            {
                throw new TimeoutException("terminal acknowledgement lost");
            }

            PublishedCount++;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ReleaseForRetryAsync(
            OutboxDeliveryLease lease,
            TimeSpan retryDelay,
            string errorSummary,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RetryCount++;
            LastRetryDelay = retryDelay;
            LastError = errorSummary;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> MarkDeadAsync(
            OutboxDeliveryLease lease,
            string errorSummary,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeadCount++;
            LastError = errorSummary;
            return ValueTask.FromResult(true);
        }

    }

    private sealed class ScriptedConsumer : IIntegrationEventConsumer
    {
        private readonly Func<OutboxDeliveryMessage, IntegrationEventConsumeResult>? _handler;
        private readonly bool _waitForCancellation;

        internal ScriptedConsumer(
            string consumerName,
            string topic,
            int schemaVersion,
            Func<OutboxDeliveryMessage, IntegrationEventConsumeResult>? handler = null,
            bool waitForCancellation = false)
        {
            Subscription = new IntegrationEventSubscription(
                consumerName,
                topic,
                schemaVersion);
            _handler = handler;
            _waitForCancellation = waitForCancellation;
        }

        public IntegrationEventSubscription Subscription { get; }

        internal List<OutboxDeliveryMessage> Messages { get; } = [];

        internal bool WasCancelled { get; private set; }

        internal static ScriptedConsumer WaitForCancellation(
            string consumerName,
            string topic,
            int schemaVersion) => new(
                consumerName,
                topic,
                schemaVersion,
                handler: null,
                waitForCancellation: true);

        public async ValueTask<IntegrationEventConsumeResult> ConsumeAsync(
            OutboxDeliveryMessage message,
            CancellationToken cancellationToken)
        {
            Messages.Add(message);
            if (!_waitForCancellation)
            {
                return _handler?.Invoke(message) ?? IntegrationEventConsumeResult.Processed;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WasCancelled = true;
                throw;
            }

            throw new InvalidOperationException("The cancellation-only consumer resumed.");
        }
    }

    private sealed class ZeroJitter : IOutboxRetryJitter
    {
        public double NextFraction() => 0;
    }

    private sealed class RecordingOperationalEventWriter(
        TrackingUnitOfWorkFactory unitOfWorkFactory) : IOperationalEventWriter
    {
        internal List<RecordedOperationalEvent> Events { get; } = [];

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, unitOfWorkFactory.ActiveCount);
            Events.Add(new RecordedOperationalEvent(eventName, payload.Clone()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingObservabilityStore(OutboxObservabilitySnapshot snapshot)
        : IOutboxObservabilityStore
    {
        public ValueTask<OutboxObservabilitySnapshot> ReadAsync(
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshot);
        }
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

    private sealed class RecordingLockProvider : IWorkerSessionLockProvider
    {
        internal int AcquireCount { get; private set; }

        internal WorkerJobIdentity? RequestedJob { get; private set; }

        public ValueTask<IWorkerSessionLock?> TryAcquireAsync(
            WorkerJobIdentity job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            RequestedJob = job;
            return ValueTask.FromResult<IWorkerSessionLock?>(new OwnedJobLock());
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class NoopScope : IDisposable
    {
        internal static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed record RecordedOperationalEvent(string Name, JsonElement Payload);

    private sealed record MetricReading(
        string Name,
        double Value,
        KeyValuePair<string, object?>[] Tags);
}
