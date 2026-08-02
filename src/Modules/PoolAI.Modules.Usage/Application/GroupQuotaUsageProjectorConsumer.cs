using System.Text.Json;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage.Abstractions;
using PoolAI.Modules.Usage.Application.Ports;

namespace PoolAI.Modules.Usage.Application;

internal sealed class GroupQuotaUsageProjectorConsumer : IIntegrationEventConsumer
{
    internal const string ProjectorName = "usage-hourly-v1";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IInboxReceiptAppender _inbox;
    private readonly IInboxReplayPredecessorVerifier _replayPredecessorVerifier;
    private readonly IGroupQuotaEventFactReader _eventFactReader;
    private readonly IAttemptSettlementHourFactReader _factReader;
    private readonly IAttemptSettlementFactExistenceReader _factExistenceReader;
    private readonly IUsageHourlyProjectionWriter _projectionWriter;
    private readonly IUsageAggregationCheckpoint _checkpoint;

    internal GroupQuotaUsageProjectorConsumer(
        IUnitOfWorkFactory unitOfWorkFactory,
        IInboxReceiptAppender inbox,
        IInboxReplayPredecessorVerifier replayPredecessorVerifier,
        IGroupQuotaEventFactReader eventFactReader,
        IAttemptSettlementHourFactReader factReader,
        IAttemptSettlementFactExistenceReader factExistenceReader,
        IUsageHourlyProjectionWriter projectionWriter,
        IUsageAggregationCheckpoint checkpoint)
    {
        _unitOfWorkFactory = unitOfWorkFactory
            ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _replayPredecessorVerifier = replayPredecessorVerifier
            ?? throw new ArgumentNullException(nameof(replayPredecessorVerifier));
        _eventFactReader = eventFactReader
            ?? throw new ArgumentNullException(nameof(eventFactReader));
        _factReader = factReader ?? throw new ArgumentNullException(nameof(factReader));
        _factExistenceReader = factExistenceReader
            ?? throw new ArgumentNullException(nameof(factExistenceReader));
        _projectionWriter = projectionWriter
            ?? throw new ArgumentNullException(nameof(projectionWriter));
        _checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
    }

    public IntegrationEventSubscription Subscription { get; } = new(
        ProjectorName,
        GroupQuotaEventV1Codec.Topic,
        GroupQuotaEventV1Codec.SchemaVersion);

    public async ValueTask<IntegrationEventConsumeResult> ConsumeAsync(
        OutboxDeliveryMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        GroupQuotaEventV1DecodeResult decoded = Decode(message.Envelope);
        if (!decoded.IsSuccess)
        {
            return IntegrationEventConsumeResult.Poison("invalid_quota_event");
        }

        GroupQuotaEventEnvelopeV1 envelope = decoded.Envelope!;
        string expectedPartition = Partition(envelope.AggregateId);
        if (!string.Equals(message.PartitionKey, expectedPartition, StringComparison.Ordinal)
            || message.PartitionSequence != envelope.SourceEventSequence)
        {
            return IntegrationEventConsumeResult.Poison("quota_partition_mismatch");
        }

        byte[] payloadHash;
        try
        {
            payloadHash = CanonicalJsonHash.Compute(message.Envelope.Payload);
        }
        catch (InvalidOperationException)
        {
            return IntegrationEventConsumeResult.Poison("invalid_quota_event");
        }

        try
        {
            return await ConsumeValidatedAsync(
                envelope,
                expectedPartition,
                payloadHash,
                message.LineageAlreadyPublished,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private async ValueTask<IntegrationEventConsumeResult> ConsumeValidatedAsync(
        GroupQuotaEventEnvelopeV1 envelope,
        string partition,
        byte[] payloadHash,
        bool lineageAlreadyPublished,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            InboxReceiptAppendResult receipt = await _inbox.AppendAsync(
                Receipt(envelope, payloadHash),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            IntegrationEventConsumeResult? receiptResult = MapReceipt(receipt.Disposition);
            if (receiptResult is not null)
            {
                return receiptResult;
            }

            return await ProcessInsertedAsync(
                envelope,
                partition,
                payloadHash,
                lineageAlreadyPublished,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<IntegrationEventConsumeResult> ProcessInsertedAsync(
        GroupQuotaEventEnvelopeV1 envelope,
        string partition,
        byte[] payloadHash,
        bool lineageAlreadyPublished,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        UsageAggregationClaimResult claim = await _checkpoint.ClaimAsync(
            new UsageAggregationClaimRequest(
                ProjectorName,
                partition,
                Owner(envelope.MessageId),
                LeaseDuration),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (claim.Disposition != UsageAggregationClaimDisposition.Acquired
            || claim.Lease is not { } lease)
        {
            return IntegrationEventConsumeResult.RetryableFailure("checkpoint_busy");
        }

        if (envelope.SourceEventSequence <= lease.LastEventSequence)
        {
            bool provenReplay = await HasExactReplayPredecessorAsync(
                envelope,
                payloadHash,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            return lineageAlreadyPublished || provenReplay
                ? await CommitCompletedLineageAsync(
                    lease,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false)
                : IntegrationEventConsumeResult.Poison("source_sequence_stale");
        }

        if (lineageAlreadyPublished)
        {
            return IntegrationEventConsumeResult.Poison("lineage_checkpoint_mismatch");
        }

        EventFactVerification verification = await VerifyEventFactAsync(
            envelope,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (verification.Failure is { } eventFactFailure)
        {
            return eventFactFailure;
        }

        IntegrationEventConsumeResult? projectionFailure = await ProjectIfRequiredAsync(
            envelope.Payload,
            verification.Fact!,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (projectionFailure is not null)
        {
            return projectionFailure;
        }

        return await AdvanceAndCommitAsync(
            envelope,
            lease,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<bool> HasExactReplayPredecessorAsync(
        GroupQuotaEventEnvelopeV1 envelope,
        byte[] payloadHash,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) => envelope.ReplayOf is { } replayOf
            ? _replayPredecessorVerifier.HasExactReceiptAsync(
                new InboxReplayPredecessorProof(
                    ProjectorName,
                    replayOf,
                    envelope.Topic,
                    envelope.SchemaVersion,
                    payloadHash),
                unitOfWorkContext,
                cancellationToken)
            : ValueTask.FromResult(false);

    private async ValueTask<EventFactVerification> VerifyEventFactAsync(
        GroupQuotaEventEnvelopeV1 envelope,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        GroupQuotaEventFactSnapshot? fact;
        try
        {
            fact = await _eventFactReader.ReadAsync(
                envelope.AggregateId,
                envelope.SourceEventSequence,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return EventFactVerification.Failed("quota_event_fact_contract_invalid");
        }

        return fact is not null && EventMatchesLedger(envelope.Payload, fact)
            ? new EventFactVerification(fact, Failure: null)
            : EventFactVerification.Failed("quota_event_fact_mismatch");
    }

    private async ValueTask<IntegrationEventConsumeResult> CommitCompletedLineageAsync(
        UsageAggregationLease lease,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        if (!await _checkpoint.ReleaseAsync(
                lease,
                unitOfWork.Context,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return IntegrationEventConsumeResult.RetryableFailure("checkpoint_cas_lost");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return IntegrationEventConsumeResult.Duplicate;
    }

    private async ValueTask<IntegrationEventConsumeResult> AdvanceAndCommitAsync(
        GroupQuotaEventEnvelopeV1 envelope,
        UsageAggregationLease lease,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        DateTimeOffset completedThrough = lease.CompletedThrough is { } prior
            && prior > envelope.OccurredAt
                ? prior
                : envelope.OccurredAt;
        UsageAggregationLease? advanced = await _checkpoint.AdvanceAsync(
            new UsageAggregationAdvanceRequest(
                lease,
                envelope.SourceEventSequence,
                completedThrough),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (advanced is null
            || !await _checkpoint.ReleaseAsync(
                    advanced,
                    unitOfWork.Context,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return IntegrationEventConsumeResult.RetryableFailure("checkpoint_cas_lost");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return IntegrationEventConsumeResult.Processed;
    }

    private async ValueTask<IntegrationEventConsumeResult?> ProjectIfRequiredAsync(
        GroupQuotaEventV1 quotaEvent,
        GroupQuotaEventFactSnapshot eventFact,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        if (RequiresNoAttemptFact(quotaEvent))
        {
            if (eventFact.ReservationId is not { } nonUsageReservationId)
            {
                return IntegrationEventConsumeResult.Poison("quota_fact_reference_missing");
            }

            return await AssertNoAttemptFactAsync(
                eventFact,
                nonUsageReservationId,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
        }

        if (quotaEvent.UsageProjection == GroupQuotaUsageProjectionDisposition.None)
        {
            return null;
        }

        if (eventFact.AttemptId is not { } attemptId
            || eventFact.ReservationId is not { } reservationId)
        {
            return IntegrationEventConsumeResult.Poison("quota_fact_reference_missing");
        }

        AttemptSettlementHourSnapshot? snapshot;
        try
        {
            snapshot = await _factReader.ReadForAttemptAsync(
                eventFact.GroupId,
                eventFact.PeriodId,
                attemptId,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return IntegrationEventConsumeResult.Poison("quota_fact_contract_invalid");
        }

        if (snapshot is null
            || !TryFindTarget(snapshot, attemptId, out AttemptSettlementFact target)
            || target.ReservationId != reservationId
            || !EventMatchesFact(quotaEvent, target))
        {
            return IntegrationEventConsumeResult.Poison("quota_fact_mismatch");
        }

        UsageHourProjection? projection = UsageHourlyProjectionCalculator.TryCreate(snapshot);
        if (projection is null)
        {
            return IntegrationEventConsumeResult.Poison("usage_projection_invalid");
        }

        await _projectionWriter.ReplaceAsync(
            projection,
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async ValueTask<IntegrationEventConsumeResult?> AssertNoAttemptFactAsync(
        GroupQuotaEventFactSnapshot eventFact,
        EntityId reservationId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        bool exists = await _factExistenceReader.ExistsForReservationAsync(
            eventFact.GroupId,
            eventFact.PeriodId,
            reservationId,
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);
        return exists
            ? IntegrationEventConsumeResult.Poison("quota_fact_mismatch")
            : null;
    }

    private static bool TryFindTarget(
        AttemptSettlementHourSnapshot snapshot,
        EntityId attemptId,
        out AttemptSettlementFact target)
    {
        target = null!;
        foreach (AttemptSettlementFact fact in snapshot.Facts)
        {
            if (fact.AttemptId != attemptId)
            {
                continue;
            }

            if (target is not null)
            {
                return false;
            }

            target = fact;
        }

        return target is not null;
    }

    private static bool EventMatchesFact(
        GroupQuotaEventV1 quotaEvent,
        AttemptSettlementFact fact) => quotaEvent switch
        {
            GroupQuotaSettledEventV1 =>
                fact.Usage.Tokens.TotalTokens == quotaEvent.Data.DeltaConsumedTokens,
            GroupQuotaExpiredEventV1 { ConservativeExpiry: true } =>
                fact.Usage.Source == SettlementUsageSource.ConservativeEstimate
                && fact.Usage.Tokens.TotalTokens == quotaEvent.Data.DeltaConsumedTokens,
            GroupQuotaUsageAdjustedEventV1 =>
                fact.Adjustment is { } adjustment
                && adjustment.QuotaEventId == quotaEvent.Data.EventId
                && adjustment.DeltaTokens == quotaEvent.Data.DeltaConsumedTokens,
            _ => false,
        };

    private static bool EventMatchesLedger(
        GroupQuotaEventV1 quotaEvent,
        GroupQuotaEventFactSnapshot fact)
    {
        GroupQuotaEventV1Data data = quotaEvent.Data;
        return data.EventId == fact.EventId
            && data.SourceEventSequence == fact.SourceEventSequence
            && data.CorrelationId == fact.CorrelationId
            && data.CausationId == fact.CausationId
            && data.GroupId == fact.GroupId
            && data.PeriodId == fact.PeriodId
            && OptionalReferenceMatches(data.ReservationId, fact.ReservationId)
            && OptionalReferenceMatches(data.AttemptId, fact.AttemptId)
            && string.Equals(quotaEvent.EventType, fact.EventType, StringComparison.Ordinal)
            && data.DeltaTotalTokens == fact.DeltaTotalTokens
            && data.DeltaConsumedTokens == fact.DeltaConsumedTokens
            && data.DeltaReservedTokens == fact.DeltaReservedTokens
            && data.TotalTokens == fact.TotalTokens
            && data.ConsumedTokens == fact.ConsumedTokens
            && data.ReservedTokens == fact.ReservedTokens
            && data.OccurredAt == fact.OccurredAt
            && MetadataMatches(data.Metadata, fact.Metadata);
    }

    private static bool OptionalReferenceMatches(EntityId? claimed, EntityId? fact) =>
        claimed is null || claimed == fact;

    private static bool MetadataMatches(JsonElement claimed, JsonElement fact) =>
        CanonicalJsonHash.Compute(claimed).AsSpan().SequenceEqual(
            CanonicalJsonHash.Compute(fact));

    private static bool RequiresNoAttemptFact(GroupQuotaEventV1 quotaEvent) =>
        quotaEvent is GroupQuotaReleasedEventV1
            or GroupQuotaExpiredEventV1 { ConservativeExpiry: false };

    private static GroupQuotaEventV1DecodeResult Decode(OutboxMessageEnvelope envelope)
    {
        try
        {
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            }))
            {
                WriteEnvelope(writer, envelope);
            }

            using JsonDocument document = JsonDocument.Parse(stream.GetBuffer().AsMemory(
                0,
                checked((int)stream.Length)));
            return GroupQuotaEventV1Codec.Decode(document.RootElement);
        }
        catch (JsonException)
        {
            return new GroupQuotaEventV1DecodeResult(
                Envelope: null,
                new GroupQuotaEventV1DecodeFailure(
                    GroupQuotaEventV1DecodeFailureCode.MalformedJson,
                    "$"));
        }
        catch (InvalidOperationException)
        {
            return new GroupQuotaEventV1DecodeResult(
                Envelope: null,
                new GroupQuotaEventV1DecodeFailure(
                    GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
                    "$"));
        }
    }

    private static void WriteEnvelope(
        Utf8JsonWriter writer,
        OutboxMessageEnvelope envelope)
    {
        writer.WriteStartObject();
        writer.WriteString("message_id", envelope.Lease.MessageId.ToString());
        writer.WriteString("topic", envelope.Topic);
        writer.WriteString("event_type", envelope.EventType);
        writer.WriteNumber("schema_version", envelope.SchemaVersion);
        writer.WriteNumber("event_sequence", envelope.EventSequence);
        WriteNullableNumber(writer, "source_event_sequence", envelope.SourceEventSequence);
        writer.WriteString("aggregate_type", envelope.AggregateType);
        writer.WriteString("aggregate_id", envelope.AggregateId.ToString());
        WriteNullableNumber(writer, "aggregate_version", envelope.AggregateVersion);
        writer.WriteString("deduplication_key", envelope.DeduplicationKey);
        writer.WriteString("occurred_at", envelope.OccurredAt);
        writer.WriteString("correlation_id", envelope.CorrelationId.ToString());
        WriteNullableId(writer, "causation_id", envelope.CausationId);
        WriteNullableId(writer, "replay_of", envelope.ReplayOf);
        writer.WritePropertyName("payload");
        envelope.Payload.WriteTo(writer);
        writer.WriteEndObject();
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(propertyName, number);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static InboxReceipt Receipt(
        GroupQuotaEventEnvelopeV1 envelope,
        byte[] payloadHash) => new(
            ProjectorName,
            envelope.MessageId,
            envelope.Topic,
            envelope.EventSequence,
            envelope.SchemaVersion,
            payloadHash);

    private static IntegrationEventConsumeResult? MapReceipt(
        InboxReceiptDisposition disposition) => disposition switch
        {
            InboxReceiptDisposition.Inserted => null,
            InboxReceiptDisposition.Duplicate => IntegrationEventConsumeResult.Duplicate,
            InboxReceiptDisposition.MessageConflict =>
                IntegrationEventConsumeResult.Poison("inbox_message_conflict"),
            InboxReceiptDisposition.SequenceConflict =>
                IntegrationEventConsumeResult.Poison("inbox_sequence_conflict"),
            _ => IntegrationEventConsumeResult.Poison("invalid_inbox_result"),
        };

    private static void WriteNullableId(
        Utf8JsonWriter writer,
        string propertyName,
        EntityId? value)
    {
        if (value is { } identity)
        {
            writer.WriteString(propertyName, identity.ToString());
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static string Partition(EntityId groupId) =>
        $"{GroupQuotaEventV1Codec.Topic}:group:{groupId}";

    private static string Owner(EntityId messageId) =>
        $"{ProjectorName}-{messageId.Value:N}";

    private sealed record EventFactVerification(
        GroupQuotaEventFactSnapshot? Fact,
        IntegrationEventConsumeResult? Failure)
    {
        internal static EventFactVerification Failed(string reason) => new(
            Fact: null,
            IntegrationEventConsumeResult.Poison(reason));
    }
}
