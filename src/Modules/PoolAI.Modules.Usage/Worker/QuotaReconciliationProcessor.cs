using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage.Application;
using PoolAI.Modules.Usage.Application.Ports;
using PoolAI.Modules.Usage.Infrastructure.Observability;

namespace PoolAI.Modules.Usage.Worker;

internal sealed partial class QuotaReconciliationProcessor(
    IUnitOfWorkFactory unitOfWorkFactory,
    IGroupQuotaReconciliationFactReader factReader,
    IUsageReconciliationProjectionReader projectionReader,
    IQuotaDeliveryHealthReader deliveryHealthReader,
    IOperationalEventWriter operationalEvents,
    QuotaReconciliationMetrics metrics,
    ILogger<QuotaReconciliationProcessor> logger)
{
    private const int SourceEventSequencePageSize = 1000;

    private readonly IUnitOfWorkFactory _unitOfWorkFactory = unitOfWorkFactory
        ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
    private readonly IGroupQuotaReconciliationFactReader _factReader = factReader
        ?? throw new ArgumentNullException(nameof(factReader));
    private readonly IUsageReconciliationProjectionReader _projectionReader = projectionReader
        ?? throw new ArgumentNullException(nameof(projectionReader));
    private readonly IQuotaDeliveryHealthReader _deliveryHealthReader = deliveryHealthReader
        ?? throw new ArgumentNullException(nameof(deliveryHealthReader));
    private readonly IOperationalEventWriter _operationalEvents = operationalEvents
        ?? throw new ArgumentNullException(nameof(operationalEvents));
    private readonly QuotaReconciliationMetrics _metrics = metrics
        ?? throw new ArgumentNullException(nameof(metrics));
    private readonly ILogger<QuotaReconciliationProcessor> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));
    private EntityId? _candidateCursor;
    private DeliveryScanContinuation? _deliveryContinuation;
    private MutableMetrics _passMetrics = new();
    private QuotaReconciliationMetricSnapshot _publishedSnapshot =
        QuotaReconciliationMetricSnapshot.Empty;

    internal async ValueTask<QuotaReconciliationProcessResult> ProcessAsync(
        IWorkerSessionLock jobLock,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobLock);
        if (pageSize is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        int pageCount = 0;
        int scannedCount = 0;
        while (true)
        {
            if (!await HasOwnershipAsync(jobLock, cancellationToken).ConfigureAwait(false))
            {
                return LoseOwnership(pageCount, scannedCount);
            }

            EntityId? pageCursor = _candidateCursor;
            IReadOnlyList<GroupQuotaReconciliationCandidate> page =
                await ReadPageAsync(pageCursor, pageSize, cancellationToken)
                    .ConfigureAwait(false);
            pageCount++;
            if (DiscardContinuationMissingFromPage(page))
                return CurrentResult(pageCount, scannedCount);

            foreach (GroupQuotaReconciliationCandidate candidate in page)
            {
                if (!await HasOwnershipAsync(jobLock, cancellationToken).ConfigureAwait(false))
                {
                    return LoseOwnership(pageCount, scannedCount);
                }

                QuotaReconciliationMetricSnapshot? observation =
                    await ProcessCandidateAsync(candidate, cancellationToken)
                        .ConfigureAwait(false);
                scannedCount++;
                if (!await HasOwnershipAsync(jobLock, cancellationToken).ConfigureAwait(false))
                {
                    return LoseOwnership(pageCount, scannedCount);
                }

                if (observation is null)
                {
                    return CurrentResult(pageCount, scannedCount);
                }

                _passMetrics.Add(observation);
                _candidateCursor = candidate.GroupId;
            }

            if (page.Count < pageSize)
            {
                if (!await HasOwnershipAsync(jobLock, cancellationToken).ConfigureAwait(false))
                {
                    return LoseOwnership(pageCount, scannedCount);
                }

                CompleteCandidatePass();
                return CurrentResult(pageCount, scannedCount);
            }
        }
    }

    private static ValueTask<bool> HasOwnershipAsync(
        IWorkerSessionLock jobLock,
        CancellationToken cancellationToken) =>
        jobLock.VerifyOwnershipAsync(cancellationToken);

    private QuotaReconciliationProcessResult CurrentResult(
        int pageCount,
        int scannedCount) => new(
        pageCount,
        scannedCount,
        OwnershipLost: false,
        _publishedSnapshot);

    private async ValueTask<QuotaReconciliationMetricSnapshot?>
        ProcessCandidateAsync(
        GroupQuotaReconciliationCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ProcessCandidateCoreAsync(
                candidate,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ReconciliationScanInvariantException exception)
        {
            bool restartPass = _deliveryContinuation is not null;
            if (restartPass)
            {
                RestartCandidatePass();
            }
            else
            {
                _deliveryContinuation = null;
            }

            MutableMetrics failure = new();
            failure.RecordInvariantFailure(exception.Layer);
            await EmitScanInvariantFailureAsync(candidate, exception)
                .ConfigureAwait(false);
            return restartPass ? null : failure.Snapshot();
        }
    }

    private async ValueTask<QuotaReconciliationMetricSnapshot?>
        ProcessCandidateCoreAsync(
        GroupQuotaReconciliationCandidate candidate,
        CancellationToken cancellationToken)
    {
        DeliveryScanContinuation? continuation = _deliveryContinuation;
        if (continuation is null)
        {
            CandidateSnapshots? initial = await ReadCandidateSnapshotsAsync(
                candidate,
                cancellationToken).ConfigureAwait(false);
            if (initial is null)
            {
                return await HandleMissingFactAsync(
                    candidate,
                    restartPass: false).ConfigureAwait(false);
            }

            continuation = new(
                CandidateScanIdentity.Create(initial.Projection, initial.Fact),
                initial.Projection,
                initial.Fact);
            _deliveryContinuation = continuation;
        }

        bool deliveryComplete = await ScanOneDeliveryPageAsync(
            continuation,
            cancellationToken).ConfigureAwait(false);
        if (!deliveryComplete)
        {
            return null;
        }

        return await CompleteCandidateAsync(
            candidate,
            continuation,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<QuotaReconciliationMetricSnapshot?> CompleteCandidateAsync(
        GroupQuotaReconciliationCandidate candidate,
        DeliveryScanContinuation continuation,
        CancellationToken cancellationToken)
    {
        CandidateSnapshots? current;
        try
        {
            current = await ReadCandidateSnapshotsAsync(
                candidate,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ReconciliationScanInvariantException)
        {
            throw;
        }
        catch (Exception)
        {
            RestartCandidatePass();
            throw;
        }

        if (current is null)
        {
            return await HandleMissingFactAsync(
                candidate,
                restartPass: true).ConfigureAwait(false);
        }

        CandidateScanIdentity currentIdentity = CandidateScanIdentity.Create(
            current.Projection,
            current.Fact);
        if (currentIdentity != continuation.Identity)
        {
            RestartCandidatePass();
            return null;
        }

        continuation.UpdateSnapshots(current.Projection, current.Fact);
        _deliveryContinuation = null;
        QuotaDeliveryHealthSnapshot delivery = continuation.Delivery.Snapshot();
        QuotaReconciliationView view = CalculateInvariantChecked(
            continuation.Fact,
            continuation.Projection);
        MutableMetrics completed = new();
        completed.Add(view, continuation.Projection, delivery);
        await EmitOperationalEventsAsync(view, delivery).ConfigureAwait(false);
        return completed.Snapshot();
    }

    private async ValueTask<CandidateSnapshots?> ReadCandidateSnapshotsAsync(
        GroupQuotaReconciliationCandidate candidate,
        CancellationToken cancellationToken)
    {
        UsageReconciliationProjectionSnapshot projection =
            await InvariantCheckedAsync(
                ReconciliationLayer.Projection,
                "projection_snapshot_invalid",
                () => ReadProjectionAsync(candidate, cancellationToken))
            .ConfigureAwait(false);
        GroupQuotaReconciliationFactSnapshot? fact = await InvariantCheckedAsync(
            ReconciliationLayer.Authoritative,
            "authoritative_snapshot_invalid",
            () => ReadFactAsync(
                candidate,
                projection.CheckpointSourceEventSequence,
                cancellationToken)).ConfigureAwait(false);
        return fact is null ? null : new(projection, fact);
    }

    private async ValueTask<QuotaReconciliationMetricSnapshot?> HandleMissingFactAsync(
        GroupQuotaReconciliationCandidate candidate,
        bool restartPass)
    {
        if (restartPass)
        {
            RestartCandidatePass();
        }
        else
        {
            _deliveryContinuation = null;
        }

        MutableMetrics missingFact = new();
        missingFact.RecordMissingFact();
        await TryWriteOperationalEventAsync(
            "usage.quota_reconciliation_authoritative_failure",
            JsonSerializer.SerializeToElement(new
            {
                severity = "P0",
                layer = "authoritative",
                classification = "candidate_fact_missing",
                group_id = candidate.GroupId.Value,
                period_id = candidate.PeriodId.Value,
            })).ConfigureAwait(false);
        return restartPass ? null : missingFact.Snapshot();
    }

    private static async ValueTask<T> InvariantCheckedAsync<T>(
        string layer,
        string classification,
        Func<ValueTask<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsInvariantFailure(exception))
        {
            throw new ReconciliationScanInvariantException(
                layer,
                classification,
                exception);
        }
    }

    private static void InvariantChecked(
        string layer,
        string classification,
        Action operation)
    {
        try
        {
            operation();
        }
        catch (Exception exception) when (IsInvariantFailure(exception))
        {
            throw new ReconciliationScanInvariantException(
                layer,
                classification,
                exception);
        }
    }

    private static QuotaReconciliationView CalculateInvariantChecked(
        GroupQuotaReconciliationFactSnapshot fact,
        UsageReconciliationProjectionSnapshot projection)
    {
        try
        {
            return QuotaReconciliationCalculator.Calculate(fact, projection);
        }
        catch (Exception exception) when (IsInvariantFailure(exception))
        {
            throw new ReconciliationScanInvariantException(
                ReconciliationLayer.Projection,
                "snapshot_alignment_invalid",
                exception);
        }
    }

    private static bool IsInvariantFailure(Exception exception) => exception is
        InvalidOperationException or ArgumentException or OverflowException;

    private QuotaReconciliationProcessResult LoseOwnership(
        int pageCount,
        int scannedCount)
    {
        _candidateCursor = null;
        _deliveryContinuation = null;
        _passMetrics = new();
        return new(
            pageCount,
            scannedCount,
            OwnershipLost: true,
            _publishedSnapshot);
    }

    private bool DiscardContinuationMissingFromPage(
        IReadOnlyList<GroupQuotaReconciliationCandidate> page)
    {
        if (_deliveryContinuation is not { } continuation)
        {
            return false;
        }

        if (page.Count == 0
            || page[0].GroupId != continuation.Fact.GroupId
            || page[0].PeriodId != continuation.Fact.PeriodId)
        {
            RestartCandidatePass();
            return true;
        }

        return false;
    }

    private void RestartCandidatePass()
    {
        _candidateCursor = null;
        _deliveryContinuation = null;
        _passMetrics = new();
    }

    private void CompleteCandidatePass()
    {
        _publishedSnapshot = _passMetrics.Snapshot();
        _metrics.Publish(_publishedSnapshot);
        _candidateCursor = null;
        _deliveryContinuation = null;
        _passMetrics = new();
    }

    private async ValueTask<IReadOnlyList<GroupQuotaReconciliationCandidate>>
        ReadPageAsync(
            EntityId? afterGroupId,
            int pageSize,
            CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        IReadOnlyList<GroupQuotaReconciliationCandidate> page = await _factReader
            .ListCurrentCandidatesAsync(
                afterGroupId,
                pageSize,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        ValidatePage(page, afterGroupId, pageSize);
        return page;
    }

    private async ValueTask<UsageReconciliationProjectionSnapshot> ReadProjectionAsync(
        GroupQuotaReconciliationCandidate candidate,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        UsageReconciliationProjectionSnapshot snapshot = await _projectionReader
            .ReadAsync(
                candidate.GroupId,
                candidate.PeriodId,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async ValueTask<GroupQuotaReconciliationFactSnapshot?> ReadFactAsync(
        GroupQuotaReconciliationCandidate candidate,
        long checkpointSourceEventSequence,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        GroupQuotaReconciliationFactSnapshot? snapshot = await _factReader.ReadAsync(
            candidate.GroupId,
            candidate.PeriodId,
            checkpointSourceEventSequence,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async ValueTask<bool> ScanOneDeliveryPageAsync(
        DeliveryScanContinuation continuation,
        CancellationToken cancellationToken)
    {
        GroupQuotaReconciliationFactSnapshot fact = continuation.Fact;
        IReadOnlyList<long> sourceEventSequences = await InvariantCheckedAsync(
            ReconciliationLayer.Authoritative,
            "authoritative_sequence_enumeration_invalid",
            () => ReadSourceEventSequencePageAsync(
                fact,
                continuation.AfterSourceEventSequence,
                cancellationToken)).ConfigureAwait(false);
        InvariantChecked(
            ReconciliationLayer.Authoritative,
            "authoritative_sequence_enumeration_invalid",
            () => ValidateSourceEventSequencePage(
                sourceEventSequences,
                continuation.AfterSourceEventSequence,
                fact.LatestPeriodEventSequence));
        if (sourceEventSequences.Count == 0)
        {
            throw new ReconciliationScanInvariantException(
                ReconciliationLayer.Authoritative,
                "authoritative_sequence_enumeration_invalid",
                new InvalidOperationException(
                    "The Group quota event sequence page ended before its frozen upper bound."));
        }

        QuotaDeliveryHealthSnapshot delivery = await InvariantCheckedAsync(
            ReconciliationLayer.Delivery,
            "delivery_snapshot_invalid",
            () => ReadDeliveryPageAsync(
                fact.GroupId,
                sourceEventSequences,
                continuation.Projection.CheckpointSourceEventSequence,
                cancellationToken)).ConfigureAwait(false);
        InvariantChecked(
            ReconciliationLayer.Delivery,
            "delivery_snapshot_invalid",
            () => continuation.Delivery.Add(delivery));
        InvariantChecked(
            ReconciliationLayer.Authoritative,
            "authoritative_sequence_enumeration_invalid",
            () => continuation.Advance(sourceEventSequences));
        if (continuation.AfterSourceEventSequence
            < fact.LatestPeriodEventSequence)
        {
            return false;
        }

        InvariantChecked(
            ReconciliationLayer.Authoritative,
            "authoritative_sequence_metadata_invalid",
            continuation.ValidateComplete);
        return true;
    }

    private async ValueTask<IReadOnlyList<long>> ReadSourceEventSequencePageAsync(
        GroupQuotaReconciliationFactSnapshot fact,
        long afterSourceEventSequence,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        IReadOnlyList<long> sourceEventSequences = await _factReader
            .ListPeriodSourceEventSequencesAsync(
                fact.GroupId,
                fact.PeriodId,
                fact.LatestPeriodEventSequence,
                afterSourceEventSequence,
                SourceEventSequencePageSize,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return sourceEventSequences;
    }

    private async ValueTask<QuotaDeliveryHealthSnapshot> ReadDeliveryPageAsync(
        EntityId groupId,
        IReadOnlyList<long> sourceEventSequences,
        long checkpointSourceEventSequence,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        QuotaDeliveryHealthSnapshot snapshot = await _deliveryHealthReader.ReadAsync(
            groupId,
            sourceEventSequences,
            checkpointSourceEventSequence,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private static void ValidateSourceEventSequencePage(
        IReadOnlyList<long> sourceEventSequences,
        long afterSourceEventSequence,
        long throughSourceEventSequence)
    {
        ArgumentNullException.ThrowIfNull(sourceEventSequences);
        if (sourceEventSequences.Count > SourceEventSequencePageSize)
        {
            throw new InvalidOperationException(
                "The Group quota event sequence page exceeded its bound.");
        }

        long prior = afterSourceEventSequence;
        foreach (long sourceEventSequence in sourceEventSequences)
        {
            if (sourceEventSequence <= prior
                || sourceEventSequence > throughSourceEventSequence)
            {
                throw new InvalidOperationException(
                    "The Group quota event sequences were not a strict keyset page.");
            }

            prior = sourceEventSequence;
        }
    }

    private async ValueTask EmitOperationalEventsAsync(
        QuotaReconciliationView view,
        QuotaDeliveryHealthSnapshot delivery)
    {
        GroupQuotaReconciliationFactSnapshot fact = view.Authoritative;
        bool missingOriginal = delivery.MissingOriginalCount > 0;
        bool duplicateOriginal = delivery.DuplicateOriginalCount > 0;
        bool authoritativeFailure = !view.ConsumedVariance.IsZero
            || !view.ReservedVariance.IsZero
            || !fact.EventChainConsistent
            || !fact.FactEventCoverageConsistent
            || !fact.LatestEventMatchesLedger
            || missingOriginal
            || duplicateOriginal;
        if (authoritativeFailure)
        {
            await EmitAuthoritativeFailureAsync(view, delivery)
                .ConfigureAwait(false);
        }

        bool deliveryUnhealthy = delivery.MissingOriginalCount > 0
            || delivery.DuplicateOriginalCount > 0
            || delivery.PendingLineageCount > 0
            || delivery.ProcessingLineageCount > 0
            || delivery.DeadLineageCount > 0
            || delivery.MissingInboxReceiptCount > 0
            || delivery.ConflictingInboxReceiptCount > 0;
        if (deliveryUnhealthy)
        {
            await EmitDeliveryFailureAsync(
                fact,
                delivery,
                missingOriginal
                    || duplicateOriginal
                    || delivery.MissingInboxReceiptCount > 0
                    || delivery.ConflictingInboxReceiptCount > 0)
                .ConfigureAwait(false);
        }

        if (fact.OverdueReservationCount > 0)
        {
            await EmitOverdueReservationAsync(fact).ConfigureAwait(false);
        }

        if (fact.OverageTokens > BigInteger.Zero)
        {
            await EmitOverageAsync(fact).ConfigureAwait(false);
        }
    }

    private ValueTask EmitScanInvariantFailureAsync(
        GroupQuotaReconciliationCandidate candidate,
        ReconciliationScanInvariantException exception) =>
        TryWriteOperationalEventAsync(
            exception.Layer switch
            {
                ReconciliationLayer.Authoritative =>
                    "usage.quota_reconciliation_authoritative_failure",
                ReconciliationLayer.Projection =>
                    "usage.quota_reconciliation_projection_failure",
                ReconciliationLayer.Delivery =>
                    "usage.quota_reconciliation_delivery_unhealthy",
                _ => throw new InvalidOperationException(
                    "The reconciliation failure layer is invalid."),
            },
            JsonSerializer.SerializeToElement(new
            {
                severity = "P0",
                layer = exception.Layer,
                classification = exception.Classification,
                group_id = candidate.GroupId.Value,
                period_id = candidate.PeriodId.Value,
            }));

    private ValueTask EmitAuthoritativeFailureAsync(
        QuotaReconciliationView view,
        QuotaDeliveryHealthSnapshot delivery) => TryWriteOperationalEventAsync(
            "usage.quota_reconciliation_authoritative_failure",
            JsonSerializer.SerializeToElement(new
            {
                severity = "P0",
                layer = "authoritative",
                group_id = view.Authoritative.GroupId.Value,
                period_id = view.Authoritative.PeriodId.Value,
                consumed_variance = view.ConsumedVariance.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                reserved_variance = view.ReservedVariance.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                event_chain_consistent = view.Authoritative.EventChainConsistent,
                fact_event_coverage_consistent =
                    view.Authoritative.FactEventCoverageConsistent,
                checkpoint_belongs_to_group =
                    view.Authoritative.CheckpointBelongsToGroup,
                latest_event_matches_ledger =
                    view.Authoritative.LatestEventMatchesLedger,
                missing_original_count = delivery.MissingOriginalCount,
                duplicate_original_count = delivery.DuplicateOriginalCount,
            }));

    private ValueTask EmitDeliveryFailureAsync(
        GroupQuotaReconciliationFactSnapshot fact,
        QuotaDeliveryHealthSnapshot delivery,
        bool missingOriginal) => TryWriteOperationalEventAsync(
            "usage.quota_reconciliation_delivery_unhealthy",
            JsonSerializer.SerializeToElement(new
            {
                severity = missingOriginal ? "P0" : "P1",
                layer = "delivery",
                group_id = fact.GroupId.Value,
                period_id = fact.PeriodId.Value,
                missing_original_count = delivery.MissingOriginalCount,
                duplicate_original_count = delivery.DuplicateOriginalCount,
                pending_lineage_count = delivery.PendingLineageCount,
                processing_lineage_count = delivery.ProcessingLineageCount,
                dead_lineage_count = delivery.DeadLineageCount,
                expected_inbox_receipt_count =
                    delivery.ExpectedInboxReceiptCount,
                missing_inbox_receipt_count =
                    delivery.MissingInboxReceiptCount,
                conflicting_inbox_receipt_count =
                    delivery.ConflictingInboxReceiptCount,
                blocking_source_event_sequence =
                    delivery.BlockingSourceEventSequence,
            }));

    private ValueTask EmitOverdueReservationAsync(
        GroupQuotaReconciliationFactSnapshot fact) =>
        TryWriteOperationalEventAsync(
            "usage.quota_reservation_recovery_slo_violation",
            JsonSerializer.SerializeToElement(new
            {
                severity = "critical",
                layer = "authoritative",
                group_id = fact.GroupId.Value,
                period_id = fact.PeriodId.Value,
                overdue_reservation_count = fact.OverdueReservationCount,
            }));

    private ValueTask EmitOverageAsync(
        GroupQuotaReconciliationFactSnapshot fact) =>
        TryWriteOperationalEventAsync(
            "usage.quota_overage_observed",
            JsonSerializer.SerializeToElement(new
            {
                severity = "warning",
                layer = "authoritative",
                classification = "capacity",
                group_id = fact.GroupId.Value,
                period_id = fact.PeriodId.Value,
                overage_tokens = fact.OverageTokens.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            }));

    private async ValueTask TryWriteOperationalEventAsync(
        string eventName,
        JsonElement payload)
    {
        try
        {
            await _operationalEvents.WriteAsync(
                eventName,
                payload,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogOperationalEventFailure(
                _logger,
                eventName,
                exception.GetType().Name);
        }
    }

    private static void ValidatePage(
        IReadOnlyList<GroupQuotaReconciliationCandidate> page,
        EntityId? after,
        int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Count > maximumCount)
        {
            throw new InvalidOperationException(
                "The quota reconciliation selector exceeded its page bound.");
        }

        EntityId? prior = after;
        foreach (GroupQuotaReconciliationCandidate candidate in page)
        {
            if (candidate.GroupId.Value == Guid.Empty
                || candidate.PeriodId.Value == Guid.Empty
                || prior is { } priorId
                    && Compare(candidate.GroupId, priorId) <= 0)
            {
                throw new InvalidOperationException(
                    "The quota reconciliation selector returned an invalid keyset page.");
            }

            prior = candidate.GroupId;
        }
    }

    private static int Compare(EntityId left, EntityId right) =>
        StringComparer.Ordinal.Compare(
            left.Value.ToString("N"),
            right.Value.ToString("N"));

    [LoggerMessage(
        EventId = 2501,
        Level = LogLevel.Warning,
        Message = "Quota reconciliation operational event {EventName} failed with {FailureType}.")]
    private static partial void LogOperationalEventFailure(
        ILogger logger,
        string eventName,
        string failureType);

    private sealed record CandidateScanIdentity(
        UsageReconciliationProjectionSnapshot Projection,
        GroupQuotaReconciliationFactSnapshot Fact)
    {
        internal static CandidateScanIdentity Create(
            UsageReconciliationProjectionSnapshot projection,
            GroupQuotaReconciliationFactSnapshot fact)
        {
            ArgumentNullException.ThrowIfNull(projection);
            ArgumentNullException.ThrowIfNull(fact);
            return new(
                projection with { CheckedAt = DateTimeOffset.UnixEpoch },
                fact with { CheckedAt = DateTimeOffset.UnixEpoch });
        }
    }

    private sealed record CandidateSnapshots(
        UsageReconciliationProjectionSnapshot Projection,
        GroupQuotaReconciliationFactSnapshot Fact);

    private sealed class DeliveryScanContinuation(
        CandidateScanIdentity identity,
        UsageReconciliationProjectionSnapshot projection,
        GroupQuotaReconciliationFactSnapshot fact)
    {
        internal CandidateScanIdentity Identity { get; } = identity;

        internal UsageReconciliationProjectionSnapshot Projection { get; private set; }
            = projection;

        internal GroupQuotaReconciliationFactSnapshot Fact { get; private set; } = fact;

        internal DeliveryHealthAccumulator Delivery { get; } = new();

        internal long AfterSourceEventSequence { get; private set; }

        internal long EnumeratedCount { get; private set; }

        internal long? FirstSourceEventSequence { get; private set; }

        internal void UpdateSnapshots(
            UsageReconciliationProjectionSnapshot currentProjection,
            GroupQuotaReconciliationFactSnapshot currentFact)
        {
            Projection = currentProjection;
            Fact = currentFact;
        }

        internal void Advance(IReadOnlyList<long> sourceEventSequences)
        {
            ArgumentNullException.ThrowIfNull(sourceEventSequences);
            if (sourceEventSequences.Count == 0)
            {
                throw new InvalidOperationException(
                    "A delivery continuation cannot advance with an empty page.");
            }

            FirstSourceEventSequence ??= sourceEventSequences[0];
            EnumeratedCount = checked(
                EnumeratedCount + sourceEventSequences.Count);
            AfterSourceEventSequence = sourceEventSequences[^1];
        }

        internal void ValidateComplete()
        {
            if (EnumeratedCount != Fact.PeriodEventCount
                || FirstSourceEventSequence != Fact.FirstPeriodEventSequence
                || AfterSourceEventSequence != Fact.LatestPeriodEventSequence)
            {
                throw new InvalidOperationException(
                    "The exact period event sequence enumeration contradicted the authoritative fact metadata.");
            }
        }
    }

    private sealed class MutableMetrics
    {
        private BigInteger _deltaTokens;
        private long _authoritativeMismatchedGroups;
        private long _projectionMismatchedGroups;
        private long _deliveryMismatchedGroups;
        private long _counterVarianceLeakCandidates;
        private long _overdueLeakCandidates;
        private double _oldestOverdueSeconds;
        private BigInteger _overageTokens;
        private BigInteger _reservedTokens;
        private double _usageAggregationLagSeconds;

        internal void RecordMissingFact() => _authoritativeMismatchedGroups++;

        internal void RecordInvariantFailure(string layer)
        {
            switch (layer)
            {
                case ReconciliationLayer.Authoritative:
                    _authoritativeMismatchedGroups++;
                    break;
                case ReconciliationLayer.Projection:
                    _projectionMismatchedGroups++;
                    break;
                case ReconciliationLayer.Delivery:
                    _deliveryMismatchedGroups++;
                    break;
                default:
                    throw new InvalidOperationException(
                        "The reconciliation failure layer is invalid.");
            }
        }

        internal void Add(
            QuotaReconciliationView view,
            UsageReconciliationProjectionSnapshot projection,
            QuotaDeliveryHealthSnapshot delivery)
        {
            GroupQuotaReconciliationFactSnapshot fact = view.Authoritative;
            _deltaTokens += BigInteger.Abs(view.ConsumedVariance)
                + BigInteger.Abs(view.ReservedVariance)
                + BigInteger.Abs(view.UsageProjection.ConsumedVariance);
            bool authoritativeMismatch = !view.ConsumedVariance.IsZero
                || !view.ReservedVariance.IsZero
                || !fact.EventChainConsistent
                || !fact.FactEventCoverageConsistent
                || !fact.LatestEventMatchesLedger
                || delivery.MissingOriginalCount > 0
                || delivery.DuplicateOriginalCount > 0;
            _authoritativeMismatchedGroups += authoritativeMismatch ? 1 : 0;
            _projectionMismatchedGroups +=
                view.UsageProjection.Status
                    == UsageProjectionReconciliationStatus.Mismatched
                    ? 1
                    : 0;
            bool deliveryMismatch = delivery.MissingOriginalCount > 0
                || delivery.DuplicateOriginalCount > 0
                || delivery.PendingLineageCount > 0
                || delivery.ProcessingLineageCount > 0
                || delivery.DeadLineageCount > 0
                || delivery.MissingInboxReceiptCount > 0
                || delivery.ConflictingInboxReceiptCount > 0;
            _deliveryMismatchedGroups += deliveryMismatch ? 1 : 0;
            _counterVarianceLeakCandidates += view.ReservedVariance.IsZero ? 0 : 1;
            _overdueLeakCandidates += fact.OverdueReservationCount;
            _overageTokens += fact.OverageTokens;
            _reservedTokens += fact.LedgerReservedTokens;

            if (fact.OldestOverdueAt is { } oldestOverdueAt)
            {
                _oldestOverdueSeconds = Math.Max(
                    _oldestOverdueSeconds,
                    Math.Max(
                        (fact.CheckedAt - oldestOverdueAt).TotalSeconds,
                        0));
            }

            if (projection.DataThrough is { } dataThrough)
            {
                _usageAggregationLagSeconds = Math.Max(
                    _usageAggregationLagSeconds,
                    Math.Max(
                        (projection.CheckedAt - dataThrough).TotalSeconds,
                        0));
            }
        }

        internal void Add(QuotaReconciliationMetricSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            _deltaTokens += snapshot.ReconciliationDeltaTokens;
            _authoritativeMismatchedGroups = checked(
                _authoritativeMismatchedGroups
                + snapshot.AuthoritativeMismatchedGroups);
            _projectionMismatchedGroups = checked(
                _projectionMismatchedGroups
                + snapshot.ProjectionMismatchedGroups);
            _deliveryMismatchedGroups = checked(
                _deliveryMismatchedGroups
                + snapshot.DeliveryMismatchedGroups);
            _counterVarianceLeakCandidates = checked(
                _counterVarianceLeakCandidates
                + snapshot.CounterVarianceLeakCandidates);
            _overdueLeakCandidates = checked(
                _overdueLeakCandidates
                + snapshot.OverdueLeakCandidates);
            _oldestOverdueSeconds = Math.Max(
                _oldestOverdueSeconds,
                snapshot.OldestOverdueSeconds);
            _overageTokens += snapshot.OverageTokens;
            _reservedTokens += snapshot.ReservedTokens;
            _usageAggregationLagSeconds = Math.Max(
                _usageAggregationLagSeconds,
                snapshot.UsageAggregationLagSeconds);
        }

        internal QuotaReconciliationMetricSnapshot Snapshot() => new(
            _deltaTokens,
            _authoritativeMismatchedGroups,
            _projectionMismatchedGroups,
            _deliveryMismatchedGroups,
            _counterVarianceLeakCandidates,
            _overdueLeakCandidates,
            _oldestOverdueSeconds,
            _overageTokens,
            _reservedTokens,
            _usageAggregationLagSeconds);
    }

    private sealed class DeliveryHealthAccumulator
    {
        private long _originalCount;
        private long _missingOriginalCount;
        private long _duplicateOriginalCount;
        private long _pendingLineageCount;
        private long _processingLineageCount;
        private long _deadLineageCount;
        private long _expectedInboxReceiptCount;
        private long _missingInboxReceiptCount;
        private long _conflictingInboxReceiptCount;
        private double _oldestUnresolvedAgeSeconds;
        private long? _blockingSourceEventSequence;
        private DateTimeOffset? _checkedAt;

        internal void Add(QuotaDeliveryHealthSnapshot page)
        {
            ArgumentNullException.ThrowIfNull(page);
            _originalCount = checked(_originalCount + page.OriginalCount);
            _missingOriginalCount = checked(
                _missingOriginalCount + page.MissingOriginalCount);
            _duplicateOriginalCount = checked(
                _duplicateOriginalCount + page.DuplicateOriginalCount);
            _pendingLineageCount = checked(
                _pendingLineageCount + page.PendingLineageCount);
            _processingLineageCount = checked(
                _processingLineageCount + page.ProcessingLineageCount);
            _deadLineageCount = checked(
                _deadLineageCount + page.DeadLineageCount);
            _expectedInboxReceiptCount = checked(
                _expectedInboxReceiptCount + page.ExpectedInboxReceiptCount);
            _missingInboxReceiptCount = checked(
                _missingInboxReceiptCount + page.MissingInboxReceiptCount);
            _conflictingInboxReceiptCount = checked(
                _conflictingInboxReceiptCount
                + page.ConflictingInboxReceiptCount);
            _oldestUnresolvedAgeSeconds = Math.Max(
                _oldestUnresolvedAgeSeconds,
                page.OldestUnresolvedAgeSeconds);
            if (page.BlockingSourceEventSequence is { } blocking
                && (_blockingSourceEventSequence is null
                    || blocking < _blockingSourceEventSequence))
            {
                _blockingSourceEventSequence = blocking;
            }

            if (_checkedAt is null || page.CheckedAt > _checkedAt)
            {
                _checkedAt = page.CheckedAt;
            }
        }

        internal QuotaDeliveryHealthSnapshot Snapshot() => new(
            _originalCount,
            _missingOriginalCount,
            _duplicateOriginalCount,
            _pendingLineageCount,
            _processingLineageCount,
            _deadLineageCount,
            _expectedInboxReceiptCount,
            _missingInboxReceiptCount,
            _conflictingInboxReceiptCount,
            _oldestUnresolvedAgeSeconds,
            _blockingSourceEventSequence,
            _checkedAt ?? throw new InvalidOperationException(
                "At least one delivery-health page is required."));
    }

    private static class ReconciliationLayer
    {
        internal const string Authoritative = "authoritative";
        internal const string Projection = "projection";
        internal const string Delivery = "delivery";
    }

    private sealed class ReconciliationScanInvariantException(
        string layer,
        string classification,
        Exception innerException) : InvalidOperationException(
            "The quota reconciliation scan encountered a classified invariant failure.",
            innerException)
    {
        internal string Layer { get; } = layer;

        internal string Classification { get; } = classification;
    }
}
