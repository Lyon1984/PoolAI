using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresOutboxDeliveryTests
{
    private readonly PostgresRuntimeFixture _fixture;

    public PostgresOutboxDeliveryTests(PostgresRuntimeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ConcurrentWorkersSkipLockedRowsAndDoNotClaimFutureMessages()
    {
        // Governing contract: docs/database/README.md section 8 and AC-041.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await VerifyConcurrentClaimsAsync(cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask VerifyConcurrentClaimsAsync(CancellationToken cancellationToken)
    {
        IntegrationEvent[] events = Enumerable.Range(0, 5)
            .Select(CreateEvent)
            .ToArray();
        Guid[] messageIds = await PrepareOutboxBatchAsync(events, cancellationToken)
            .ConfigureAwait(false);
        EntityId[] expectedDue = events.Take(4).Select(item => item.MessageId).ToArray();
        ConcurrentClaims claims = await ClaimConcurrentlyAsync(expectedDue, cancellationToken)
            .ConfigureAwait(false);
        await AssertConcurrentStatesAsync(events, messageIds, claims, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<Guid[]> PrepareOutboxBatchAsync(
        IntegrationEvent[] events,
        CancellationToken cancellationToken)
    {
        await AppendEventsAsync(events, cancellationToken).ConfigureAwait(false);
        Guid[] messageIds = events.Select(item => item.MessageId.Value).ToArray();
        await _fixture.DeferOtherPendingOutboxAsync(messageIds, cancellationToken)
            .ConfigureAwait(false);
        await _fixture.SetOutboxNotDueAsync(messageIds[^1], cancellationToken)
            .ConfigureAwait(false);
        return messageIds;
    }

    private async ValueTask<ConcurrentClaims> ClaimConcurrentlyAsync(
        EntityId[] expectedDue,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IOutboxDeliveryStore store = _fixture.WorkerServices
            .GetRequiredService<IOutboxDeliveryStore>();
        EntityId firstOwner = EntityId.New();
        EntityId secondOwner = EntityId.New();
        IUnitOfWork firstUnitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var firstUnitOfWorkScope = firstUnitOfWork.ConfigureAwait(false);
        await AssertWorkerRoleAsync(firstUnitOfWork, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<OutboxMessageEnvelope> first = await store.ClaimDueAsync(
            firstOwner,
            2,
            TimeSpan.FromMinutes(5),
            firstUnitOfWork.Context,
            cancellationToken).ConfigureAwait(false);

        IUnitOfWork secondUnitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var secondUnitOfWorkScope = secondUnitOfWork.ConfigureAwait(false);
        IReadOnlyList<OutboxMessageEnvelope> second = await store.ClaimDueAsync(
            secondOwner,
            10,
            TimeSpan.FromMinutes(5),
            secondUnitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        AssertConcurrentClaims(first, second, firstOwner, secondOwner, expectedDue);

        Assert.True(await store.HeartbeatAsync(
            first[0].Lease,
            TimeSpan.FromMinutes(5),
            firstUnitOfWork.Context,
            cancellationToken).ConfigureAwait(false));
        Assert.True(await store.MarkPublishedAsync(
            first[0].Lease,
            firstUnitOfWork.Context,
            cancellationToken).ConfigureAwait(false));
        await firstUnitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        await secondUnitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConcurrentClaims(first, second);
    }

    private static void AssertConcurrentClaims(
        IReadOnlyList<OutboxMessageEnvelope> first,
        IReadOnlyList<OutboxMessageEnvelope> second,
        EntityId firstOwner,
        EntityId secondOwner,
        EntityId[] expectedDue)
    {
        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Empty(first.Select(item => item.Lease.MessageId)
            .Intersect(second.Select(item => item.Lease.MessageId)));
        Assert.All(first, item => AssertInitialLease(item.Lease, firstOwner));
        Assert.All(second, item => AssertInitialLease(item.Lease, secondOwner));
        HashSet<EntityId> actuallyClaimed = first.Concat(second)
            .Select(item => item.Lease.MessageId)
            .ToHashSet();
        Assert.True(expectedDue.ToHashSet().SetEquals(actuallyClaimed));
    }

    private static async ValueTask AssertWorkerRoleAsync(
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand currentUser = PostgresUnitOfWorkAccessor
            .Require(unitOfWork.Context)
            .CreateCommand("SELECT current_user;");
        Assert.Equal(
            "poolai_worker",
            Assert.IsType<string>(await currentUser
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false)));
    }

    private async ValueTask AssertConcurrentStatesAsync(
        IntegrationEvent[] events,
        Guid[] messageIds,
        ConcurrentClaims claims,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<EntityId, OutboxState> states = await ReadOutboxStatesAsync(
            messageIds,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal("published", states[claims.First[0].Lease.MessageId].Status);
        Assert.Equal("processing", states[claims.First[1].Lease.MessageId].Status);
        Assert.All(
            claims.Second,
            item => Assert.Equal("processing", states[item.Lease.MessageId].Status));
        Assert.Equal("pending", states[events[^1].MessageId].Status);
        Assert.Equal(0, states[events[^1].MessageId].PublishAttempts);
        Assert.Equal(0, states[events[^1].MessageId].LockGeneration);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task TakeoverFencesStaleOwnerAndDeadReplayIsAtomicWithAudit()
    {
        // Governing contract: docs/database/README.md sections 3 and 8 plus AC-041.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await VerifyTakeoverAndReplayAsync(cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask VerifyTakeoverAndReplayAsync(CancellationToken cancellationToken)
    {
        IntegrationEvent sourceEvent = CreateEvent(20);
        await PrepareDeadLetterSourceAsync(sourceEvent, cancellationToken).ConfigureAwait(false);
        IOutboxDeliveryStore store = _fixture.WorkerServices
            .GetRequiredService<IOutboxDeliveryStore>();
        TakeoverClaims claims = await ClaimExpiredLeaseAsync(sourceEvent, cancellationToken)
            .ConfigureAwait(false);
        await AssertStaleLeaseIsFencedAsync(store, claims.First, cancellationToken)
            .ConfigureAwait(false);
        OutboxState dead = await RetryThenDeadLetterAsync(
            store,
            sourceEvent,
            claims.Takeover,
            cancellationToken).ConfigureAwait(false);
        await ReplayDeadWithAuditAsync(sourceEvent, dead, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask PrepareDeadLetterSourceAsync(
        IntegrationEvent sourceEvent,
        CancellationToken cancellationToken)
    {
        await AppendEventsAsync([sourceEvent], cancellationToken).ConfigureAwait(false);
        await _fixture.DeferOtherPendingOutboxAsync(
            [sourceEvent.MessageId.Value],
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TakeoverClaims> ClaimExpiredLeaseAsync(
        IntegrationEvent sourceEvent,
        CancellationToken cancellationToken)
    {
        EntityId firstOwner = EntityId.New();
        OutboxMessageEnvelope firstClaim = Assert.Single(await ClaimAndCommitAsync(
            firstOwner,
            cancellationToken).ConfigureAwait(false));
        AssertInitialLease(firstClaim.Lease, firstOwner);
        await _fixture.ForceOutboxLeaseExpiredAsync(
            sourceEvent.MessageId.Value,
            cancellationToken).ConfigureAwait(false);

        EntityId takeoverOwner = EntityId.New();
        OutboxMessageEnvelope takeover = Assert.Single(await ClaimAndCommitAsync(
            takeoverOwner,
            cancellationToken).ConfigureAwait(false));
        Assert.Equal(2, takeover.Lease.Generation);
        Assert.Equal(2, takeover.Lease.Attempt);
        Assert.Equal(takeoverOwner, takeover.Lease.Owner);
        Assert.Equal(sourceEvent.MessageId, takeover.Lease.MessageId);
        return new TakeoverClaims(firstClaim, takeover);
    }

    private async ValueTask AssertStaleLeaseIsFencedAsync(
        IOutboxDeliveryStore store,
        OutboxMessageEnvelope firstClaim,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork staleUnitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (staleUnitOfWork.ConfigureAwait(false))
        {
            Assert.False(await store.HeartbeatAsync(
                firstClaim.Lease,
                TimeSpan.FromMinutes(1),
                staleUnitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            Assert.False(await store.MarkPublishedAsync(
                firstClaim.Lease,
                staleUnitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            Assert.False(await store.ReleaseForRetryAsync(
                firstClaim.Lease,
                TimeSpan.FromMinutes(1),
                "stale retry",
                staleUnitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            Assert.False(await store.MarkDeadAsync(
                firstClaim.Lease,
                "stale dead",
                staleUnitOfWork.Context,
                cancellationToken).ConfigureAwait(false));
            await staleUnitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<OutboxState> RetryThenDeadLetterAsync(
        IOutboxDeliveryStore store,
        IntegrationEvent sourceEvent,
        OutboxMessageEnvelope takeover,
        CancellationToken cancellationToken)
    {
        OutboxState processing = Assert.Single(await ReadOutboxStatesAsync(
            [sourceEvent.MessageId.Value],
            cancellationToken).ConfigureAwait(false)).Value;
        Assert.Equal("processing", processing.Status);
        Assert.Equal(takeover.Lease.Owner.Value, processing.LockedBy);
        Assert.Equal(2, processing.LockGeneration);
        Assert.Equal(2, processing.PublishAttempts);

        Assert.True(await ExecuteAndCommitAsync(
            (context, token) => store.ReleaseForRetryAsync(
                takeover.Lease,
                TimeSpan.FromHours(1),
                "temporary broker failure",
                context,
                token),
            cancellationToken).ConfigureAwait(false));
        Assert.Empty(await ClaimAndCommitAsync(
            EntityId.New(),
            cancellationToken).ConfigureAwait(false));

        await _fixture.ForceOutboxDueAsync(
            sourceEvent.MessageId.Value,
            cancellationToken).ConfigureAwait(false);
        EntityId finalOwner = EntityId.New();
        OutboxMessageEnvelope finalClaim = Assert.Single(await ClaimAndCommitAsync(
            finalOwner,
            cancellationToken).ConfigureAwait(false));
        Assert.Equal(3, finalClaim.Lease.Generation);
        Assert.Equal(3, finalClaim.Lease.Attempt);
        Assert.True(await ExecuteAndCommitAsync(
            (context, token) => store.MarkDeadAsync(
                finalClaim.Lease,
                "permanent broker failure",
                context,
                token),
            cancellationToken).ConfigureAwait(false));

        OutboxState dead = Assert.Single(await ReadOutboxStatesAsync(
            [sourceEvent.MessageId.Value],
            cancellationToken).ConfigureAwait(false)).Value;
        Assert.Equal("dead", dead.Status);
        Assert.Equal("permanent broker failure", dead.LastError);
        Assert.Equal(3, dead.PublishAttempts);
        Assert.Equal(3, dead.LockGeneration);
        return dead;
    }

    private async ValueTask ReplayDeadWithAuditAsync(
        IntegrationEvent sourceEvent,
        OutboxState dead,
        CancellationToken cancellationToken)
    {
        EntityId replayMessageId = EntityId.New();
        EntityId auditId = EntityId.New();
        OutboxReplayRequest replayRequest = new(
            sourceEvent.MessageId,
            replayMessageId,
            $"integration:replay:{replayMessageId}");
        AuditEntry replayAudit = CreateReplayAudit(
            auditId,
            sourceEvent.MessageId,
            replayMessageId);
        await ReplayWithAuditAsync(
            replayRequest,
            replayAudit,
            commit: false,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(
            (0, 0),
            await ReadReplayCountsAsync(
                replayMessageId,
                auditId,
                cancellationToken).ConfigureAwait(false));

        OutboxReplayReceipt receipt = Assert.IsType<OutboxReplayReceipt>(await ReplayWithAuditAsync(
            replayRequest,
            replayAudit,
            commit: true,
            cancellationToken).ConfigureAwait(false));
        Assert.Equal(replayMessageId, receipt.MessageId);
        IReadOnlyDictionary<EntityId, OutboxState> replayedStates = await ReadOutboxStatesAsync(
            [sourceEvent.MessageId.Value, replayMessageId.Value],
            cancellationToken).ConfigureAwait(false);
        OutboxState unchangedSource = replayedStates[sourceEvent.MessageId];
        OutboxState replay = replayedStates[replayMessageId];
        AssertReplayPreservedEnvelope(
            sourceEvent,
            dead,
            receipt,
            unchangedSource,
            replay);
        Assert.Equal(
            (1, 1),
            await ReadReplayCountsAsync(
                replayMessageId,
                auditId,
                cancellationToken).ConfigureAwait(false));
    }

    private static void AssertReplayPreservedEnvelope(
        IntegrationEvent sourceEvent,
        OutboxState dead,
        OutboxReplayReceipt receipt,
        OutboxState unchangedSource,
        OutboxState replay)
    {
        Assert.Equal("dead", unchangedSource.Status);
        Assert.Equal(dead.EventSequence, unchangedSource.EventSequence);
        Assert.Equal("pending", replay.Status);
        Assert.Equal(sourceEvent.MessageId.Value, replay.ReplayOf);
        Assert.Equal(receipt.EventSequence, replay.EventSequence);
        Assert.True(replay.EventSequence > unchangedSource.EventSequence);
        Assert.NotEqual(unchangedSource.DeduplicationKey, replay.DeduplicationKey);
        Assert.Equal(unchangedSource.Topic, replay.Topic);
        Assert.Equal(unchangedSource.SchemaVersion, replay.SchemaVersion);
        Assert.Equal(unchangedSource.AggregateType, replay.AggregateType);
        Assert.Equal(unchangedSource.AggregateId, replay.AggregateId);
        Assert.Equal(unchangedSource.AggregateVersion, replay.AggregateVersion);
        Assert.Equal(unchangedSource.EventType, replay.EventType);
        Assert.Equal(unchangedSource.SourceEventSequence, replay.SourceEventSequence);
        Assert.Equal(unchangedSource.CorrelationId, replay.CorrelationId);
        Assert.Equal(unchangedSource.CausationId, replay.CausationId);
        Assert.Equal(unchangedSource.Payload, replay.Payload);
        Assert.Equal(unchangedSource.OccurredAt, replay.OccurredAt);
        Assert.Equal(0, replay.PublishAttempts);
        Assert.Equal(0, replay.LockGeneration);
    }

    private static void AssertInitialLease(OutboxDeliveryLease lease, EntityId owner)
    {
        Assert.Equal(owner, lease.Owner);
        Assert.Equal(1, lease.Generation);
        Assert.Equal(1, lease.Attempt);
    }

    private async ValueTask AppendEventsAsync(
        IReadOnlyCollection<IntegrationEvent> events,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.ApiServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IOutboxAppender appender = _fixture.ApiServices.GetRequiredService<IOutboxAppender>();
        IUnitOfWork unitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var unitOfWorkScope = unitOfWork.ConfigureAwait(false);
        foreach (IntegrationEvent integrationEvent in events)
        {
            await appender.AppendAsync(
                integrationEvent,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<OutboxMessageEnvelope>> ClaimAndCommitAsync(
        EntityId owner,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IOutboxDeliveryStore store = _fixture.WorkerServices
            .GetRequiredService<IOutboxDeliveryStore>();
        IUnitOfWork unitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var unitOfWorkScope = unitOfWork.ConfigureAwait(false);
        IReadOnlyList<OutboxMessageEnvelope> claimed = await store.ClaimDueAsync(
            owner,
            10,
            TimeSpan.FromMinutes(5),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    private async ValueTask<bool> ExecuteAndCommitAsync(
        Func<IUnitOfWorkContext, CancellationToken, ValueTask<bool>> operation,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var unitOfWorkScope = unitOfWork.ConfigureAwait(false);
        bool result = await operation(unitOfWork.Context, cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<OutboxReplayReceipt?> ReplayWithAuditAsync(
        OutboxReplayRequest request,
        AuditEntry audit,
        bool commit,
        CancellationToken cancellationToken)
    {
        IUnitOfWorkFactory factory = _fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IOutboxDeliveryStore store = _fixture.WorkerServices
            .GetRequiredService<IOutboxDeliveryStore>();
        IAuditAppender auditAppender = _fixture.WorkerServices
            .GetRequiredService<IAuditAppender>();
        IUnitOfWork unitOfWork = await factory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var unitOfWorkScope = unitOfWork.ConfigureAwait(false);
        OutboxReplayReceipt? receipt = await store.ReplayDeadAsync(
            request,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Assert.NotNull(receipt);
        await auditAppender.AppendAsync(
            audit,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (commit)
        {
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return receipt;
    }

    private async ValueTask<IReadOnlyDictionary<EntityId, OutboxState>> ReadOutboxStatesAsync(
        Guid[] messageIds,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT id, event_sequence, deduplication_key, topic, schema_version,
                   aggregate_type, aggregate_id, aggregate_version, event_type,
                   source_event_sequence, correlation_id, causation_id, payload::text,
                   occurred_at, status, publish_attempts, lock_generation, locked_by,
                   replay_of, last_error
            FROM public.outbox_messages
            WHERE id = ANY($1);
            """);
        command.Parameters.AddWithValue(messageIds);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<EntityId, OutboxState> states = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            EntityId id = new(reader.GetGuid(0));
            states.Add(id, new OutboxState(
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetGuid(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.GetGuid(10),
                reader.IsDBNull(11) ? null : reader.GetGuid(11),
                reader.GetString(12),
                new DateTimeOffset(reader.GetFieldValue<DateTime>(13).ToUniversalTime()),
                reader.GetString(14),
                reader.GetInt32(15),
                reader.GetInt64(16),
                reader.IsDBNull(17) ? null : reader.GetGuid(17),
                reader.IsDBNull(18) ? null : reader.GetGuid(18),
                reader.IsDBNull(19) ? null : reader.GetString(19)));
        }

        Assert.Equal(messageIds.Length, states.Count);
        return states;
    }

    private async ValueTask<(int Replays, int Audits)> ReadReplayCountsAsync(
        EntityId replayMessageId,
        EntityId auditId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*)::integer FROM public.outbox_messages WHERE id = $1),
                (SELECT count(*)::integer FROM public.audit_logs WHERE id = $2);
            """);
        command.Parameters.AddWithValue(replayMessageId.Value);
        command.Parameters.AddWithValue(auditId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static IntegrationEvent CreateEvent(int index)
    {
        EntityId messageId = EntityId.New();
        EntityId aggregateId = EntityId.New();
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            message_id = messageId.ToString(),
            ordinal = index,
        });
        return new IntegrationEvent(
            messageId,
            $"integration:outbox:{messageId}",
            "integration.delivery.v1",
            1,
            "integration_probe",
            aggregateId,
            index + 1,
            "integration.probe.created",
            null,
            EntityId.New(),
            EntityId.New(),
            payload,
            TimeProvider.System.GetUtcNow());
    }

    private static AuditEntry CreateReplayAudit(
        EntityId auditId,
        EntityId deadMessageId,
        EntityId replayMessageId) => new(
        auditId,
        AuditActorType.Service,
        null,
        "outbox.dead.replayed",
        "outbox_message",
        replayMessageId,
        null,
        "integration replay probe",
        null,
        "PoolAI.IntegrationTests",
        null,
        JsonSerializer.SerializeToElement(new { replay_message_id = replayMessageId.ToString() }),
        JsonSerializer.SerializeToElement(new
        {
            dead_message_id = deadMessageId.ToString(),
            replay_message_id = replayMessageId.ToString(),
        }));

    private sealed record ConcurrentClaims(
        IReadOnlyList<OutboxMessageEnvelope> First,
        IReadOnlyList<OutboxMessageEnvelope> Second);

    private sealed record TakeoverClaims(
        OutboxMessageEnvelope First,
        OutboxMessageEnvelope Takeover);

    private sealed record OutboxState(
        long EventSequence,
        string DeduplicationKey,
        string Topic,
        int SchemaVersion,
        string AggregateType,
        Guid AggregateId,
        long? AggregateVersion,
        string EventType,
        long? SourceEventSequence,
        Guid CorrelationId,
        Guid? CausationId,
        string Payload,
        DateTimeOffset OccurredAt,
        string Status,
        int PublishAttempts,
        long LockGeneration,
        Guid? LockedBy,
        Guid? ReplayOf,
        string? LastError);
}
