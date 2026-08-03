using System.Diagnostics.Metrics;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage;
using PoolAI.Modules.Usage.Application;
using PoolAI.Modules.Usage.Application.Ports;
using PoolAI.Modules.Usage.Infrastructure.Observability;
using PoolAI.Modules.Usage.Infrastructure.Workers;
using PoolAI.Modules.Usage.Worker;
using QuotaReconciliationWorkerService =
    PoolAI.Modules.Usage.Infrastructure.Workers.QuotaReconciliationService;

namespace PoolAI.UnitTests;

// Governing contract: Accepted ADR 0013, runtime ownership/scheduling,
// bounded metrics, alert classification, and telemetry-failure isolation.
public sealed class QuotaReconciliationWorkerTests
{
    private static readonly string[] AllowedMetricLabelKeys =
        ["group_tier", "kind", "worker"];

    private static readonly DateTimeOffset CheckedAt = new(
        2026,
        8,
        3,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void WorkerJobIdentityAndAdvisoryLockIdStayVersionedAndStable()
    {
        WorkerJobIdentity job = WorkerJobs.QuotaReconciliation;

        Assert.Equal(
            "poolai:r1:worker:quota-reconciliation:v1",
            job.Name);
        Assert.Equal(3_574_250_530_161_801_542L, WorkerSessionLockId.Derive(job));
        Assert.Equal(
            WorkerSessionLockId.Derive(
                new WorkerJobIdentity(
                    "poolai:r1:worker:quota-reconciliation:v1")),
            WorkerSessionLockId.Derive(job));
    }

    [Fact]
    public async Task SingleRoundStaysStandbyThenTakesOverWithTheFixedPageBound()
    {
        RecordingSessionLock ownedLock = new();
        QueueLockProvider lockProvider = new(null, ownedLock);
        int processCalls = 0;
        using QuotaReconciliationWorkerService service = new(
            lockProvider,
            (jobLock, pageSize, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Same(ownedLock, jobLock);
                Assert.Equal(100, pageSize);
                processCalls++;
                return ValueTask.FromResult(EmptyProcessResult());
            },
            new FakeTimeProvider(CheckedAt),
            NullLogger<QuotaReconciliationWorkerService>.Instance);

        await service.RunSingleRoundAsync(TestContext.Current.CancellationToken);
        await service.RunSingleRoundAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, lockProvider.RequestedJobs.Count);
        Assert.All(
            lockProvider.RequestedJobs,
            static job => Assert.Equal(WorkerJobs.QuotaReconciliation, job));
        Assert.Equal(1, processCalls);
        Assert.Equal(1, ownedLock.DisposeCalls);
        Assert.Equal(100, QuotaReconciliationWorkerService.PageSize);
        Assert.True(
            QuotaReconciliationWorkerService.RoundBudget
            < QuotaReconciliationWorkerService.ScanInterval);
    }

    [Fact]
    public void ApiModuleDoesNotRegisterTheLoopAndWorkerRegistrationIsExplicit()
    {
        ServiceCollection apiServices = new();

        apiServices.AddUsageModule();

        Assert.DoesNotContain(
            apiServices,
            static descriptor => descriptor.ServiceType.Equals(
                typeof(IHostedService)));
        Assert.DoesNotContain(
            apiServices,
            static descriptor => descriptor.ServiceType.Equals(
                typeof(QuotaReconciliationProcessor)));

        ServiceCollection workerServices = new();
        workerServices.AddUsageModule();
        IServiceCollection returned = workerServices
            .AddUsageQuotaReconciliationWorker();
        workerServices.AddUsageQuotaReconciliationWorker();

        Assert.Same(workerServices, returned);
        ServiceDescriptor loop = Assert.Single(
            workerServices,
            static descriptor => descriptor.ServiceType.Equals(
                typeof(IHostedService))
                && typeof(QuotaReconciliationWorkerService).Equals(
                    descriptor.ImplementationType));
        Assert.Equal(ServiceLifetime.Singleton, loop.Lifetime);
        Assert.Single(
            workerServices,
            static descriptor => descriptor.ServiceType.Equals(
                typeof(QuotaReconciliationProcessor)));
        Assert.Single(
            workerServices,
            static descriptor => descriptor.ServiceType.Equals(
                typeof(QuotaReconciliationMetrics)));
    }

    [Fact]
    public async Task EveryCrossContextReadUsesItsOwnCompletedShortUnitOfWork()
    {
        GroupQuotaReconciliationCandidate candidate = Candidate(1);
        List<string> operations = [];
        RecordingUnitOfWorkFactory units = new(operations);
        ScriptedReaders readers = new(units, operations, [[candidate]]);
        readers.Projections[candidate.GroupId] = Projection(candidate);
        readers.Facts[candidate.GroupId] = Fact(candidate);
        readers.Deliveries[candidate.GroupId] = HealthyDelivery();
        using QuotaReconciliationMetrics metrics = new();
        QuotaReconciliationProcessor processor = Processor(
            units,
            readers,
            NoOpOperationalEventWriter.Instance,
            metrics);

        QuotaReconciliationProcessResult result = await processor.ProcessAsync(
            new ScriptedSessionLock(true, true),
            pageSize: 100,
            TestContext.Current.CancellationToken);

        Assert.False(result.OwnershipLost);
        Assert.Equal(1, result.PageCount);
        Assert.Equal(1, result.ScannedCount);
        Assert.Equal(5, units.BeginCalls);
        Assert.Equal(5, units.CommitCalls);
        Assert.Equal(5, units.DisposeCalls);
        Assert.Equal(5, units.Contexts.Distinct().Count());
        Assert.Equal(
            [
                "begin:1", "page:1", "commit:1", "dispose:1",
                "begin:2", "projection:2", "commit:2", "dispose:2",
                "begin:3", "fact:3", "commit:3", "dispose:3",
                "begin:4", "sequences:4", "commit:4", "dispose:4",
                "begin:5", "delivery:5", "commit:5", "dispose:5",
            ],
            operations);
        Assert.Equal(0, units.ActiveCount);
        Assert.Equal(
            [null],
            readers.PageCalls.Select(static call => call.AfterGroupId));
        Assert.Equal(
            [100],
            readers.PageCalls.Select(static call => call.MaximumCount));
    }

    [Fact]
    public async Task SelectorCannotExceedTheRequestedPageBound()
    {
        GroupQuotaReconciliationCandidate first = Candidate(1);
        GroupQuotaReconciliationCandidate second = Candidate(2);
        RecordingUnitOfWorkFactory units = new([]);
        ScriptedReaders readers = new(units, [], [[first, second]]);
        using QuotaReconciliationMetrics metrics = new();
        QuotaReconciliationProcessor processor = Processor(
            units,
            readers,
            NoOpOperationalEventWriter.Instance,
            metrics);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => processor.ProcessAsync(
                new ScriptedSessionLock(true),
                pageSize: 1,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            "The quota reconciliation selector exceeded its page bound.",
            exception.Message);
        Assert.Empty(readers.ProjectionContexts);
    }

    [Fact]
    public async Task SelectorMustAdvanceStrictlyBeyondThePriorKeysetCursor()
    {
        GroupQuotaReconciliationCandidate candidate = Candidate(1);
        RecordingUnitOfWorkFactory units = new([]);
        ScriptedReaders readers = new(
            units,
            [],
            [[candidate], [candidate]]);
        readers.Projections[candidate.GroupId] = Projection(candidate);
        readers.Facts[candidate.GroupId] = Fact(candidate);
        readers.Deliveries[candidate.GroupId] = HealthyDelivery();
        using QuotaReconciliationMetrics metrics = new();
        QuotaReconciliationProcessor processor = Processor(
            units,
            readers,
            NoOpOperationalEventWriter.Instance,
            metrics);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => processor.ProcessAsync(
                new ScriptedSessionLock(true, true, true),
                pageSize: 1,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            "The quota reconciliation selector returned an invalid keyset page.",
            exception.Message);
        Assert.Equal(2, readers.PageCalls.Count);
        Assert.Null(readers.PageCalls[0].AfterGroupId);
        Assert.Equal(candidate.GroupId, readers.PageCalls[1].AfterGroupId);
        Assert.Single(readers.ProjectionContexts);
    }

    [Fact]
    public async Task OwnershipLossDoesNotPublishAPartialRoundSnapshot()
    {
        GroupQuotaReconciliationCandidate candidate = Candidate(1);
        using QuotaReconciliationMetrics metrics = new();
        metrics.Publish(SentinelMetrics());
        QuotaReconciliationProcessResult partial = await RunOneCandidateAsync(
            candidate,
            metrics,
            pages: [[candidate]],
            ownership: [true, true, false],
            pageSize: 1);

        Assert.True(partial.OwnershipLost);
        Assert.Equal(1, partial.PageCount);
        Assert.Equal(1, partial.ScannedCount);
        Assert.NotEqual(
            SentinelMetrics().ReconciliationDeltaTokens,
            partial.Metrics.ReconciliationDeltaTokens);
        IReadOnlyList<MetricReading> afterPartial = ObserveMetrics(metrics);
        Assert.Equal(
            777d,
            Metric(afterPartial, "poolai_quota_reconciliation_delta_tokens").Value);

        QuotaReconciliationProcessResult completed = await RunOneCandidateAsync(
            candidate,
            metrics,
            pages: [[candidate], []],
            ownership: [true, true, true],
            pageSize: 1);

        Assert.False(completed.OwnershipLost);
        Assert.Equal(2, completed.PageCount);
        Assert.Equal(
            (double)completed.Metrics.ReconciliationDeltaTokens,
            Metric(
                ObserveMetrics(metrics),
                "poolai_quota_reconciliation_delta_tokens").Value);
    }

    [Fact]
    public async Task CompleteScanPublishesAbsoluteBoundedMetricsWithoutGroupLabels()
    {
        GroupQuotaReconciliationCandidate first = Candidate(1);
        GroupQuotaReconciliationCandidate second = Candidate(2);
        RecordingUnitOfWorkFactory units = new([]);
        ScriptedReaders readers = new(units, [], [[first, second], []]);
        ConfigureMetricScenario(readers, first, second);
        using QuotaReconciliationMetrics metrics = new();
        QuotaReconciliationProcessor processor = Processor(
            units,
            readers,
            NoOpOperationalEventWriter.Instance,
            metrics);

        QuotaReconciliationProcessResult result = await processor.ProcessAsync(
            new ScriptedSessionLock(),
            pageSize: 2,
            TestContext.Current.CancellationToken);
        IReadOnlyList<MetricReading> readings = ObserveMetrics(metrics);

        Assert.False(result.OwnershipLost);
        Assert.Equal(2, result.ScannedCount);
        Assert.Equal(95d, Metric(
            readings,
            "poolai_quota_reconciliation_delta_tokens").Value);
        Assert.Equal(2d, KindMetric(
            readings,
            "poolai_quota_reconciliation_mismatched_groups",
            "authoritative").Value);
        Assert.Equal(0d, KindMetric(
            readings,
            "poolai_quota_reconciliation_mismatched_groups",
            "projection").Value);
        Assert.Equal(1d, KindMetric(
            readings,
            "poolai_quota_reconciliation_mismatched_groups",
            "delivery").Value);
        Assert.Equal(2d, KindMetric(
            readings,
            "poolai_quota_reservation_leak_candidates",
            "counter_variance").Value);
        Assert.Equal(5d, KindMetric(
            readings,
            "poolai_quota_reservation_leak_candidates",
            "overdue").Value);
        Assert.Equal(180d, Metric(
            readings,
            "poolai_quota_reservation_oldest_overdue_seconds").Value);
        Assert.Equal(18d, Metric(readings, "poolai_quota_overage_tokens").Value);
        Assert.Equal(60d, Metric(readings, "poolai_quota_reserved_tokens").Value);
        Assert.Equal(300d, Metric(
            readings,
            "poolai_usage_aggregation_lag_seconds").Value);
        AssertFixedMetricLabels(readings, first, second);
    }

    [Fact]
    public async Task IncompleteLineageBlocksLaterCandidatesAndResumesOnePagePerRound()
    {
        GroupQuotaReconciliationCandidate first = Candidate(20);
        GroupQuotaReconciliationCandidate second = Candidate(21);
        RecordingUnitOfWorkFactory units = new([]);
        ScriptedReaders readers = new(
            units,
            [],
            [[first, second], [first, second], []]);
        ConfigureMultiPageCandidate(readers, first, 1000, 500);
        ConfigureHealthyCandidate(readers, second);
        using QuotaReconciliationMetrics metrics = new();
        metrics.Publish(SentinelMetrics());
        QuotaReconciliationProcessor processor = Processor(
            units,
            readers,
            NoOpOperationalEventWriter.Instance,
            metrics);

        QuotaReconciliationProcessResult incomplete = await processor.ProcessAsync(
            new ScriptedSessionLock(),
            pageSize: 2,
            TestContext.Current.CancellationToken);

        Assert.False(incomplete.OwnershipLost);
        Assert.Equal(1, incomplete.PageCount);
        Assert.Equal(1, incomplete.ScannedCount);
        Assert.Single(readers.ProjectionContexts);
        Assert.Single(readers.SequencePageCalls);
        Assert.Equal(0, readers.SequencePageCalls[0].AfterSourceEventSequence);
        Assert.Equal(1000, readers.DeliveryCalls[0].ExpectedSourceEventSequences.Count);
        AssertPublishedDelta(metrics, 777d);

        QuotaReconciliationProcessResult completed = await processor.ProcessAsync(
            new ScriptedSessionLock(),
            pageSize: 2,
            TestContext.Current.CancellationToken);

        Assert.False(completed.OwnershipLost);
        Assert.Equal(2, completed.PageCount);
        Assert.Equal(2, completed.ScannedCount);
        Assert.Equal(
            [null, null, second.GroupId],
            readers.PageCalls.Select(static call => call.AfterGroupId));
        Assert.Equal(
            [0L, 1000L, 0L],
            readers.SequencePageCalls.Select(
                static call => call.AfterSourceEventSequence));
        Assert.Equal(
            [1000, 500, 5],
            readers.DeliveryCalls.Select(
                static call => call.ExpectedSourceEventSequences.Count));
        Assert.Equal(
            [first.GroupId, first.GroupId, second.GroupId],
            readers.DeliveryCalls.Select(static call => call.GroupId));
        Assert.Equal(
            (double)completed.Metrics.ReconciliationDeltaTokens,
            Metric(
                ObserveMetrics(metrics),
                "poolai_quota_reconciliation_delta_tokens").Value);
    }

    [Theory]
    [InlineData("fact")]
    [InlineData("checkpoint")]
    [InlineData("period")]
    public async Task ActiveLineageRestartsWhenCandidateIdentityChanges(
        string change)
    {
        GroupQuotaReconciliationCandidate original = Candidate(22);
        GroupQuotaReconciliationCandidate current = string.Equals(
            change,
            "period",
            StringComparison.Ordinal)
            ? new(
                original.GroupId,
                Id("20000000-0000-0000-0000-999999999999"))
            : original;
        RecordingUnitOfWorkFactory units = new([]);
        ScriptedReaders readers = new(
            units,
            [],
            [[original], [current]]);
        ConfigureMultiPageCandidate(readers, original, 1000, 1000);
        using QuotaReconciliationMetrics metrics = new();
        QuotaReconciliationProcessor processor = Processor(
            units,
            readers,
            NoOpOperationalEventWriter.Instance,
            metrics);

        _ = await processor.ProcessAsync(
            new ScriptedSessionLock(),
            pageSize: 1,
            TestContext.Current.CancellationToken);

        ApplyIdentityChange(readers, original, current, change);

        _ = await processor.ProcessAsync(
            new ScriptedSessionLock(),
            pageSize: 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [0L, 0L],
            readers.SequencePageCalls.Select(
                static call => call.AfterSourceEventSequence));
        Assert.Equal(
            [null, null],
            readers.PageCalls.Select(static call => call.AfterGroupId));
        Assert.Equal(current.PeriodId, readers.SequencePageCalls[^1].PeriodId);
    }

    [Fact]
    public async Task OwnershipLossDiscardsLineageCursorAndPartialPass()
    {
        GroupQuotaReconciliationCandidate candidate = Candidate(23);
        RecordingUnitOfWorkFactory units = new([]);
        ScriptedReaders readers = new(
            units,
            [],
            [[candidate], [candidate]]);
        readers.Projections[candidate.GroupId] = Projection(candidate);
        readers.Facts[candidate.GroupId] = Fact(
            candidate,
            latestPeriodEventSequence: 1500,
            periodEventCount: 1500);
        readers.Deliveries[candidate.GroupId] = HealthyDelivery(1500);
        readers.DeliveryPages[candidate.GroupId] = new(
            [HealthyDelivery(1000), HealthyDelivery(1000)]);
        using QuotaReconciliationMetrics metrics = new();
        metrics.Publish(SentinelMetrics());
        QuotaReconciliationProcessor processor = Processor(
            units,
            readers,
            NoOpOperationalEventWriter.Instance,
            metrics);

        _ = await processor.ProcessAsync(
            new ScriptedSessionLock(),
            pageSize: 1,
            TestContext.Current.CancellationToken);
        QuotaReconciliationProcessResult lost = await processor.ProcessAsync(
            new ScriptedSessionLock(false),
            pageSize: 1,
            TestContext.Current.CancellationToken);
        _ = await processor.ProcessAsync(
            new ScriptedSessionLock(),
            pageSize: 1,
            TestContext.Current.CancellationToken);

        Assert.True(lost.OwnershipLost);
        Assert.Equal(0, lost.PageCount);
        Assert.Equal(
            [0L, 0L],
            readers.SequencePageCalls.Select(
                static call => call.AfterSourceEventSequence));
        Assert.Equal(
            [null, null],
            readers.PageCalls.Select(static call => call.AfterGroupId));
        Assert.Equal(
            777d,
            Metric(
                ObserveMetrics(metrics),
                "poolai_quota_reconciliation_delta_tokens").Value);
    }

    [Fact]
    public async Task AuthoritativeSequenceReaderFailureDoesNotBecomeDeliveryFailure()
    {
        GroupQuotaReconciliationCandidate failed = Candidate(24);
        GroupQuotaReconciliationCandidate survivor = Candidate(25);
        RecordingUnitOfWorkFactory units = new([]);
        ScriptedReaders readers = new(
            units,
            [],
            [[failed, survivor], []]);
        readers.Projections[failed.GroupId] = Projection(failed);
        readers.Facts[failed.GroupId] = Fact(failed);
        readers.Deliveries[failed.GroupId] = HealthyDelivery();
        readers.Projections[survivor.GroupId] = Projection(survivor);
        readers.Facts[survivor.GroupId] = Fact(survivor);
        readers.Deliveries[survivor.GroupId] = HealthyDelivery();
        readers.SequenceFailure = new InvalidOperationException(
            "must-not-leak-sequence-secret");
        RecordingOperationalEventWriter events = new();
        using QuotaReconciliationMetrics metrics = new();
        QuotaReconciliationProcessor processor = Processor(
            units,
            readers,
            events,
            metrics);

        QuotaReconciliationProcessResult result = await processor.ProcessAsync(
            new ScriptedSessionLock(),
            pageSize: 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ScannedCount);
        Assert.Equal(1, result.Metrics.AuthoritativeMismatchedGroups);
        Assert.Equal(0, result.Metrics.DeliveryMismatchedGroups);
        OperationalEvent failure = Assert.Single(events.Events);
        Assert.Equal(
            "usage.quota_reconciliation_authoritative_failure",
            failure.Name);
        Assert.Equal(
            "authoritative_sequence_enumeration_invalid",
            failure.Payload.GetProperty("classification").GetString());
        Assert.DoesNotContain(
            "must-not-leak-sequence-secret",
            failure.Payload.GetRawText(),
            StringComparison.Ordinal);
        Assert.Single(readers.DeliveryCalls);
        Assert.Equal(survivor.GroupId, readers.DeliveryCalls[0].GroupId);
    }

    [Fact]
    public async Task ProjectionMismatchMetricExcludesLayerOneBlockedCandidates()
    {
        GroupQuotaReconciliationCandidate blocked = Candidate(26);
        GroupQuotaReconciliationCandidate mismatched = Candidate(27);
        RecordingUnitOfWorkFactory units = new([]);
        ScriptedReaders readers = new(
            units,
            [],
            [[blocked, mismatched], []]);
        readers.Projections[blocked.GroupId] = Projection(
            blocked,
            projectedConsumedTokens: 80);
        readers.Facts[blocked.GroupId] = Fact(
            blocked,
            factConsumedTokens: 90);
        readers.Deliveries[blocked.GroupId] = HealthyDelivery();
        readers.Projections[mismatched.GroupId] = Projection(
            mismatched,
            projectedConsumedTokens: 80);
        readers.Facts[mismatched.GroupId] = Fact(mismatched);
        readers.Deliveries[mismatched.GroupId] = HealthyDelivery();
        using QuotaReconciliationMetrics metrics = new();
        QuotaReconciliationProcessor processor = Processor(
            units,
            readers,
            NoOpOperationalEventWriter.Instance,
            metrics);

        QuotaReconciliationProcessResult result = await processor.ProcessAsync(
            new ScriptedSessionLock(),
            pageSize: 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Metrics.AuthoritativeMismatchedGroups);
        Assert.Equal(1, result.Metrics.ProjectionMismatchedGroups);
        Assert.Equal(0, result.Metrics.DeliveryMismatchedGroups);
    }

    [Fact]
    public void BigIntegerTelemetryConversionIsFiniteAndSaturating()
    {
        BigInteger beyondDouble = BigInteger.One << 4096;

        double saturated = QuotaReconciliationMetrics.ToFiniteDouble(beyondDouble);

        Assert.True(double.IsFinite(saturated));
        Assert.Equal(double.MaxValue, saturated);
        Assert.Equal(42d, QuotaReconciliationMetrics.ToFiniteDouble(42));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => QuotaReconciliationMetrics.ToFiniteDouble(-BigInteger.One));
    }

    [Fact]
    public async Task EmitsBoundedP0DeliveryOverdueAndOverageEvents()
    {
        RecordingOperationalEventWriter events = new();

        QuotaReconciliationProcessResult result = await RunEventScenarioAsync(events);

        Assert.False(result.OwnershipLost);
        Assert.Equal(1, result.ScannedCount);
        Assert.Equal(4, events.Events.Count);
        OperationalEvent authoritative = Event(
            events.Events,
            "usage.quota_reconciliation_authoritative_failure");
        Assert.Equal("P0", authoritative.Payload.GetProperty("severity").GetString());
        Assert.Equal(
            "authoritative",
            authoritative.Payload.GetProperty("layer").GetString());
        OperationalEvent delivery = Event(
            events.Events,
            "usage.quota_reconciliation_delivery_unhealthy");
        Assert.Equal("P0", delivery.Payload.GetProperty("severity").GetString());
        Assert.Equal("delivery", delivery.Payload.GetProperty("layer").GetString());
        Assert.Equal(
            1,
            delivery.Payload.GetProperty("missing_inbox_receipt_count").GetInt64());
        Assert.Equal(
            1,
            delivery.Payload.GetProperty("conflicting_inbox_receipt_count").GetInt64());
        OperationalEvent overdue = Event(
            events.Events,
            "usage.quota_reservation_recovery_slo_violation");
        Assert.Equal("critical", overdue.Payload.GetProperty("severity").GetString());
        OperationalEvent overage = Event(
            events.Events,
            "usage.quota_overage_observed");
        Assert.Equal("warning", overage.Payload.GetProperty("severity").GetString());
        Assert.Equal(
            "capacity",
            overage.Payload.GetProperty("classification").GetString());
        Assert.All(events.Events, static item => Assert.False(item.Token.CanBeCanceled));
    }

    [Fact]
    public async Task OperationalEventSinkFailureCannotChangeReconciliationTruth()
    {
        RecordingOperationalEventWriter successfulEvents = new();
        RecordingOperationalEventWriter throwingEvents = new(throwOnWrite: true);

        QuotaReconciliationProcessResult expected = await RunEventScenarioAsync(
            successfulEvents);
        QuotaReconciliationProcessResult actual = await RunEventScenarioAsync(
            throwingEvents);

        Assert.Equal(expected, actual);
        Assert.Equal(4, throwingEvents.Events.Count);
    }

    [Theory]
    [InlineData("projection", "invalid_operation")]
    [InlineData("projection", "argument")]
    [InlineData("projection", "overflow")]
    [InlineData("authoritative", "invalid_operation")]
    [InlineData("authoritative", "argument")]
    [InlineData("authoritative", "overflow")]
    [InlineData("delivery", "invalid_operation")]
    [InlineData("delivery", "argument")]
    [InlineData("delivery", "overflow")]
    public async Task InvariantReaderFailureIsIsolatedAsOneBoundedP0(
        string layer,
        string exceptionKind)
    {
        GroupQuotaReconciliationCandidate candidate = Candidate(10);
        GroupQuotaReconciliationCandidate survivor = Candidate(11);
        RecordingUnitOfWorkFactory units = new([]);
        ScriptedReaders readers = new(units, [], [[candidate, survivor], []]);
        readers.Projections[candidate.GroupId] = Projection(candidate);
        readers.Facts[candidate.GroupId] = Fact(candidate);
        readers.Deliveries[candidate.GroupId] = HealthyDelivery();
        readers.Projections[survivor.GroupId] = Projection(survivor);
        readers.Facts[survivor.GroupId] = Fact(survivor);
        readers.Deliveries[survivor.GroupId] = HealthyDelivery();
        ConfigureInvariantFailure(readers, layer, exceptionKind);
        RecordingOperationalEventWriter events = new();
        using QuotaReconciliationMetrics metrics = new();
        QuotaReconciliationProcessor processor = Processor(
            units,
            readers,
            events,
            metrics);

        QuotaReconciliationProcessResult result = await processor.ProcessAsync(
            new ScriptedSessionLock(),
            pageSize: 2,
            TestContext.Current.CancellationToken);

        Assert.False(result.OwnershipLost);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(2, result.ScannedCount);
        AssertInvariantFailureMetrics(result.Metrics, metrics, layer);
        OperationalEvent failure = Assert.Single(events.Events);
        Assert.Equal(InvariantFailureEventName(layer), failure.Name);
        Assert.False(failure.Token.CanBeCanceled);
        Assert.Equal("P0", failure.Payload.GetProperty("severity").GetString());
        Assert.Equal(layer, failure.Payload.GetProperty("layer").GetString());
        Assert.Equal(
            InvariantFailureClassification(layer),
            failure.Payload.GetProperty("classification").GetString());
        Assert.Equal(
            ["classification", "group_id", "layer", "period_id", "severity"],
            failure.Payload.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain(
            "must-not-leak-secret",
            failure.Payload.GetRawText(),
            StringComparison.Ordinal);
    }

    private static void ConfigureInvariantFailure(
        ScriptedReaders readers,
        string layer,
        string exceptionKind)
    {
        Exception failure = exceptionKind switch
        {
            "invalid_operation" => new InvalidOperationException(
                "must-not-leak-secret invalid operation"),
            "argument" => new ArgumentException(
                "must-not-leak-secret invalid argument",
                nameof(exceptionKind)),
            "overflow" => new OverflowException(
                "must-not-leak-secret overflow"),
            _ => throw new InvalidOperationException("Unknown exception kind."),
        };
        switch (layer)
        {
            case "projection":
                readers.ProjectionFailure = failure;
                break;
            case "authoritative":
                readers.FactFailure = failure;
                break;
            case "delivery":
                readers.DeliveryFailure = failure;
                break;
            default:
                throw new InvalidOperationException("Unknown reconciliation layer.");
        }
    }

    private static void AssertInvariantFailureMetrics(
        QuotaReconciliationMetricSnapshot snapshot,
        QuotaReconciliationMetrics metrics,
        string layer)
    {
        Assert.Equal(
            string.Equals(layer, "authoritative", StringComparison.Ordinal) ? 1 : 0,
            snapshot.AuthoritativeMismatchedGroups);
        Assert.Equal(
            string.Equals(layer, "projection", StringComparison.Ordinal) ? 1 : 0,
            snapshot.ProjectionMismatchedGroups);
        Assert.Equal(
            string.Equals(layer, "delivery", StringComparison.Ordinal) ? 1 : 0,
            snapshot.DeliveryMismatchedGroups);
        IReadOnlyList<MetricReading> readings = ObserveMetrics(metrics);
        Assert.Equal(1d, KindMetric(
            readings,
            "poolai_quota_reconciliation_mismatched_groups",
            layer).Value);
        Assert.Equal(
            1,
            readings.Count(reading => string.Equals(
                reading.Name,
                "poolai_quota_reconciliation_mismatched_groups",
                StringComparison.Ordinal)
                && reading.Value > 0));
    }

    private static string InvariantFailureEventName(string layer) => layer switch
    {
        "projection" => "usage.quota_reconciliation_projection_failure",
        "authoritative" => "usage.quota_reconciliation_authoritative_failure",
        "delivery" => "usage.quota_reconciliation_delivery_unhealthy",
        _ => throw new InvalidOperationException("Unknown reconciliation layer."),
    };

    private static string InvariantFailureClassification(string layer) => layer switch
    {
        "projection" => "projection_snapshot_invalid",
        "authoritative" => "authoritative_snapshot_invalid",
        "delivery" => "delivery_snapshot_invalid",
        _ => throw new InvalidOperationException("Unknown reconciliation layer."),
    };

    private static async Task<QuotaReconciliationProcessResult> RunOneCandidateAsync(
        GroupQuotaReconciliationCandidate candidate,
        QuotaReconciliationMetrics metrics,
        IReadOnlyList<IReadOnlyList<GroupQuotaReconciliationCandidate>> pages,
        IReadOnlyList<bool> ownership,
        int pageSize)
    {
        RecordingUnitOfWorkFactory units = new([]);
        ScriptedReaders readers = new(units, [], pages);
        readers.Projections[candidate.GroupId] = Projection(
            candidate,
            projectedConsumedTokens: 80);
        readers.Facts[candidate.GroupId] = Fact(
            candidate,
            ledgerConsumedTokens: 100,
            factConsumedTokens: 90,
            expectedConsumedAtCheckpoint: 100);
        readers.Deliveries[candidate.GroupId] = HealthyDelivery();
        QuotaReconciliationProcessor processor = Processor(
            units,
            readers,
            NoOpOperationalEventWriter.Instance,
            metrics);
        return await processor.ProcessAsync(
            new ScriptedSessionLock([.. ownership]),
            pageSize,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private static async Task<QuotaReconciliationProcessResult> RunEventScenarioAsync(
        RecordingOperationalEventWriter events)
    {
        GroupQuotaReconciliationCandidate candidate = Candidate(9);
        RecordingUnitOfWorkFactory units = new([]);
        ScriptedReaders readers = new(units, [], [[candidate]]);
        readers.Projections[candidate.GroupId] = Projection(candidate);
        readers.Facts[candidate.GroupId] = Fact(
            candidate,
            ledgerTotalTokens: 90,
            ledgerConsumedTokens: 100,
            factConsumedTokens: 99,
            overdueReservationCount: 1,
            oldestOverdueAt: CheckedAt.AddSeconds(-61),
            eventChainConsistent: false,
            overageTokens: 10);
        readers.Deliveries[candidate.GroupId] = new QuotaDeliveryHealthSnapshot(
            originalCount: 4,
            missingOriginalCount: 1,
            duplicateOriginalCount: 0,
            pendingLineageCount: 1,
            processingLineageCount: 0,
            deadLineageCount: 0,
            expectedInboxReceiptCount: 5,
            missingInboxReceiptCount: 1,
            conflictingInboxReceiptCount: 1,
            oldestUnresolvedAgeSeconds: 30,
            blockingSourceEventSequence: 3,
            CheckedAt);
        using QuotaReconciliationMetrics metrics = new();
        QuotaReconciliationProcessor processor = Processor(
            units,
            readers,
            events,
            metrics);
        return await processor.ProcessAsync(
            new ScriptedSessionLock(),
            pageSize: 100,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private static void ConfigureMetricScenario(
        ScriptedReaders readers,
        GroupQuotaReconciliationCandidate first,
        GroupQuotaReconciliationCandidate second)
    {
        readers.Projections[first.GroupId] = Projection(
            first,
            projectedConsumedTokens: 180,
            dataThrough: CheckedAt.AddSeconds(-120));
        readers.Facts[first.GroupId] = Fact(
            first,
            ledgerTotalTokens: 93,
            ledgerConsumedTokens: 100,
            ledgerReservedTokens: 20,
            factConsumedTokens: 110,
            pendingReservationTokens: 25,
            expectedConsumedAtCheckpoint: 200,
            overdueReservationCount: 2,
            oldestOverdueAt: CheckedAt.AddSeconds(-120),
            overageTokens: 7);
        readers.Deliveries[first.GroupId] = new QuotaDeliveryHealthSnapshot(
            originalCount: 5,
            missingOriginalCount: 0,
            duplicateOriginalCount: 0,
            pendingLineageCount: 1,
            processingLineageCount: 0,
            deadLineageCount: 1,
            oldestUnresolvedAgeSeconds: 30,
            blockingSourceEventSequence: 2,
            CheckedAt);

        readers.Projections[second.GroupId] = Projection(
            second,
            projectedConsumedTokens: 70,
            dataThrough: CheckedAt.AddSeconds(-300));
        readers.Facts[second.GroupId] = Fact(
            second,
            ledgerTotalTokens: 119,
            ledgerConsumedTokens: 130,
            ledgerReservedTokens: 40,
            factConsumedTokens: 100,
            pendingReservationTokens: 30,
            expectedConsumedAtCheckpoint: 50,
            overdueReservationCount: 3,
            oldestOverdueAt: CheckedAt.AddSeconds(-180),
            overageTokens: 11);
        readers.Deliveries[second.GroupId] = HealthyDelivery();
    }

    private static void ConfigureMultiPageCandidate(
        ScriptedReaders readers,
        GroupQuotaReconciliationCandidate candidate,
        params int[] deliveryPageCounts)
    {
        readers.Projections[candidate.GroupId] = Projection(candidate);
        readers.Facts[candidate.GroupId] = Fact(
            candidate,
            latestPeriodEventSequence: 1500,
            periodEventCount: 1500);
        readers.Deliveries[candidate.GroupId] = HealthyDelivery(1500);
        readers.DeliveryPages[candidate.GroupId] = new(
            deliveryPageCounts.Select(
                static count => HealthyDelivery(count)));
    }

    private static void ConfigureHealthyCandidate(
        ScriptedReaders readers,
        GroupQuotaReconciliationCandidate candidate)
    {
        readers.Projections[candidate.GroupId] = Projection(candidate);
        readers.Facts[candidate.GroupId] = Fact(candidate);
        readers.Deliveries[candidate.GroupId] = HealthyDelivery();
    }

    private static void ApplyIdentityChange(
        ScriptedReaders readers,
        GroupQuotaReconciliationCandidate original,
        GroupQuotaReconciliationCandidate current,
        string change)
    {
        switch (change)
        {
            case "fact":
                readers.Facts[original.GroupId] = Fact(
                    current,
                    ledgerTotalTokens: 1001,
                    latestPeriodEventSequence: 1500,
                    periodEventCount: 1500);
                break;
            case "checkpoint":
                readers.Projections[original.GroupId] = Projection(
                    current,
                    checkpointSourceEventSequence: 6);
                readers.Facts[original.GroupId] = Fact(
                    current,
                    checkpointSourceEventSequence: 6,
                    latestPeriodEventSequence: 1500,
                    periodEventCount: 1500);
                break;
            case "period":
                readers.Projections[original.GroupId] = Projection(current);
                readers.Facts[original.GroupId] = Fact(
                    current,
                    latestPeriodEventSequence: 1500,
                    periodEventCount: 1500);
                break;
            default:
                throw new InvalidOperationException("Unknown identity change.");
        }
    }

    private static void AssertPublishedDelta(
        QuotaReconciliationMetrics metrics,
        double expected) => Assert.Equal(
        expected,
        Metric(
            ObserveMetrics(metrics),
            "poolai_quota_reconciliation_delta_tokens").Value);

    private static void AssertFixedMetricLabels(
        IReadOnlyList<MetricReading> readings,
        GroupQuotaReconciliationCandidate first,
        GroupQuotaReconciliationCandidate second)
    {
        string[] mismatchKinds = readings
            .Where(static reading => string.Equals(
                reading.Name,
                "poolai_quota_reconciliation_mismatched_groups",
                StringComparison.Ordinal))
            .Select(static reading => Assert.Single(reading.Tags).Value as string)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
        Assert.Equal(["authoritative", "delivery", "projection"], mismatchKinds);
        Assert.All(
            readings.SelectMany(static reading => reading.Tags),
            static tag => Assert.Contains(
                tag.Key,
                AllowedMetricLabelKeys));
        Assert.DoesNotContain(
            readings.SelectMany(static reading => reading.Tags),
            static tag => string.Equals(
                tag.Key,
                "group_id",
                StringComparison.Ordinal)
                || string.Equals(tag.Key, "period_id", StringComparison.Ordinal));
        string[] values = readings
            .SelectMany(static reading => reading.Tags)
            .Select(static tag => tag.Value?.ToString() ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(first.GroupId.Value.ToString(), values);
        Assert.DoesNotContain(second.GroupId.Value.ToString(), values);
        Assert.Equal(
            "default",
            Assert.Single(Metric(
                readings,
                "poolai_quota_reconciliation_delta_tokens").Tags).Value);
    }

    private static List<MetricReading> ObserveMetrics(
        QuotaReconciliationMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        using MeterListener listener = new();
        List<MetricReading> readings = [];
        listener.InstrumentPublished = (instrument, candidate) =>
        {
            if (string.Equals(
                    instrument.Meter.Name,
                    QuotaReconciliationMetrics.MeterName,
                    StringComparison.Ordinal))
            {
                candidate.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            readings.Add(new(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            readings.Add(new(instrument.Name, value, tags.ToArray())));
        listener.Start();
        listener.RecordObservableInstruments();
        return readings;
    }

    private static MetricReading Metric(
        IEnumerable<MetricReading> readings,
        string name) => Assert.Single(
            readings,
            reading => string.Equals(reading.Name, name, StringComparison.Ordinal));

    private static MetricReading KindMetric(
        IEnumerable<MetricReading> readings,
        string name,
        string kind) => Assert.Single(
            readings,
            reading => string.Equals(reading.Name, name, StringComparison.Ordinal)
                && reading.Tags.Any(tag => string.Equals(
                    tag.Key,
                    "kind",
                    StringComparison.Ordinal)
                    && string.Equals(tag.Value as string, kind, StringComparison.Ordinal)));

    private static OperationalEvent Event(
        IEnumerable<OperationalEvent> events,
        string name) => Assert.Single(
            events,
            item => string.Equals(item.Name, name, StringComparison.Ordinal));

    private static QuotaReconciliationProcessor Processor(
        IUnitOfWorkFactory units,
        ScriptedReaders readers,
        IOperationalEventWriter operationalEvents,
        QuotaReconciliationMetrics metrics) => new(
            units,
            readers,
            readers,
            readers,
            operationalEvents,
            metrics,
            NullLogger<QuotaReconciliationProcessor>.Instance);

    private static GroupQuotaReconciliationCandidate Candidate(int suffix) => new(
        Id($"10000000-0000-0000-0000-{suffix:D12}"),
        Id($"20000000-0000-0000-0000-{suffix:D12}"));

    private static GroupQuotaReconciliationFactSnapshot Fact(
        GroupQuotaReconciliationCandidate candidate,
        BigInteger? ledgerTotalTokens = null,
        BigInteger? ledgerConsumedTokens = null,
        BigInteger? ledgerReservedTokens = null,
        BigInteger? factConsumedTokens = null,
        BigInteger? pendingReservationTokens = null,
        BigInteger? expectedConsumedAtCheckpoint = null,
        long overdueReservationCount = 0,
        DateTimeOffset? oldestOverdueAt = null,
        bool eventChainConsistent = true,
        bool latestEventMatchesLedger = true,
        BigInteger? overageTokens = null,
        long firstPeriodEventSequence = 1,
        long latestPeriodEventSequence = 5,
        long periodEventCount = 5,
        long checkpointSourceEventSequence = 5)
    {
        BigInteger consumed = ledgerConsumedTokens ?? 100;
        BigInteger reserved = ledgerReservedTokens ?? 20;
        return new GroupQuotaReconciliationFactSnapshot(
            candidate.GroupId,
            candidate.PeriodId,
            CheckpointSourceEventSequence: checkpointSourceEventSequence,
            LedgerTotalTokens: ledgerTotalTokens ?? 1_000,
            LedgerConsumedTokens: consumed,
            LedgerReservedTokens: reserved,
            FactConsumedTokens: factConsumedTokens ?? consumed,
            PendingReservationTokens: pendingReservationTokens ?? reserved,
            PendingReservationCount: reserved.IsZero ? 0 : 1,
            OverdueReservationCount: overdueReservationCount,
            OldestOverdueAt: oldestOverdueAt,
            ExpectedConsumedAtCheckpoint: expectedConsumedAtCheckpoint ?? consumed,
            CheckpointBelongsToGroup: true,
            LatestPeriodEventSequence: latestPeriodEventSequence,
            LatestPeriodEventOccurredAt: CheckedAt.AddMinutes(-1),
            EventChainConsistent: eventChainConsistent,
            FactEventCoverageConsistent: true,
            LatestEventMatchesLedger: latestEventMatchesLedger,
            OverageTokens: overageTokens ?? BigInteger.Zero,
            CheckedAt,
            IsCurrentPeriod: true,
            FirstPeriodEventSequence: firstPeriodEventSequence,
            LatestGroupEventSequence: latestPeriodEventSequence,
            PeriodEventCount: periodEventCount);
    }

    private static UsageReconciliationProjectionSnapshot Projection(
        GroupQuotaReconciliationCandidate candidate,
        BigInteger? projectedConsumedTokens = null,
        DateTimeOffset? dataThrough = null,
        long checkpointSourceEventSequence = 5) => new(
        candidate.GroupId,
        candidate.PeriodId,
        projectedConsumedTokens ?? 100,
        CheckpointSourceEventSequence: checkpointSourceEventSequence,
            dataThrough ?? CheckedAt.AddMinutes(-1),
            CheckedAt);

    private static QuotaDeliveryHealthSnapshot HealthyDelivery(
        long originalCount = 5) => new(
        originalCount,
        missingOriginalCount: 0,
        duplicateOriginalCount: 0,
        pendingLineageCount: 0,
        processingLineageCount: 0,
        deadLineageCount: 0,
        oldestUnresolvedAgeSeconds: 0,
        blockingSourceEventSequence: null,
        CheckedAt);

    private static EntityId Id(string value) => new(Guid.Parse(value));

    private static QuotaReconciliationProcessResult EmptyProcessResult() => new(
        0,
        0,
        OwnershipLost: false,
        QuotaReconciliationMetricSnapshot.Empty);

    private static QuotaReconciliationMetricSnapshot SentinelMetrics() => new(
        ReconciliationDeltaTokens: 777,
        AuthoritativeMismatchedGroups: 7,
        ProjectionMismatchedGroups: 7,
        DeliveryMismatchedGroups: 7,
        CounterVarianceLeakCandidates: 7,
        OverdueLeakCandidates: 7,
        OldestOverdueSeconds: 7,
        OverageTokens: 777,
        ReservedTokens: 777,
        UsageAggregationLagSeconds: 7);

    private sealed record MetricReading(
        string Name,
        double Value,
        KeyValuePair<string, object?>[] Tags);

    private sealed record OperationalEvent(
        string Name,
        JsonElement Payload,
        CancellationToken Token);

    private sealed record PageCall(EntityId? AfterGroupId, int MaximumCount);

    private sealed record SequencePageCall(
        EntityId GroupId,
        EntityId PeriodId,
        long ThroughSourceEventSequence,
        long AfterSourceEventSequence,
        int MaximumCount);

    private sealed record DeliveryCall(
        EntityId GroupId,
        IReadOnlyList<long> ExpectedSourceEventSequences,
        long CheckpointSourceEventSequence);

    private sealed class ScriptedReaders :
        IGroupQuotaReconciliationFactReader,
        IUsageReconciliationProjectionReader,
        IQuotaDeliveryHealthReader
    {
        private readonly RecordingUnitOfWorkFactory _units;
        private readonly ICollection<string> _operations;
        private readonly Queue<IReadOnlyList<GroupQuotaReconciliationCandidate>>
            _pages;

        internal ScriptedReaders(
            RecordingUnitOfWorkFactory units,
            ICollection<string> operations,
            IEnumerable<IReadOnlyList<GroupQuotaReconciliationCandidate>> pages)
        {
            _units = units;
            _operations = operations;
            _pages = new(pages);
        }

        internal Dictionary<EntityId, UsageReconciliationProjectionSnapshot>
            Projections
        { get; } = [];

        internal Dictionary<EntityId, GroupQuotaReconciliationFactSnapshot?>
            Facts
        { get; } = [];

        internal Dictionary<EntityId, QuotaDeliveryHealthSnapshot> Deliveries
        { get; } = [];

        internal Dictionary<EntityId, Queue<QuotaDeliveryHealthSnapshot>>
            DeliveryPages
        { get; } = [];

        internal Exception? ProjectionFailure { get; set; }

        internal Exception? FactFailure { get; set; }

        internal Exception? SequenceFailure { get; set; }

        internal Exception? DeliveryFailure { get; set; }

        internal Dictionary<EntityId, IReadOnlyList<long>> SourceEventSequences
        { get; } = [];

        internal List<PageCall> PageCalls { get; } = [];

        internal List<IUnitOfWorkContext> ProjectionContexts { get; } = [];

        internal List<IUnitOfWorkContext> FactContexts { get; } = [];

        internal List<IUnitOfWorkContext> SequenceContexts { get; } = [];

        internal List<IUnitOfWorkContext> DeliveryContexts { get; } = [];

        internal List<SequencePageCall> SequencePageCalls { get; } = [];

        internal List<DeliveryCall> DeliveryCalls { get; } = [];

        public ValueTask<EntityId?> ResolvePeriodAsync(
            EntityId groupId,
            EntityId? periodId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<GroupQuotaReconciliationCandidate>>
            ListCurrentCandidatesAsync(
                EntityId? afterGroupId,
                int maximumCount,
                IUnitOfWorkContext unitOfWorkContext,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestUnitOfWorkContext context = _units.AssertActive(unitOfWorkContext);
            _operations.Add($"page:{context.Sequence}");
            PageCalls.Add(new(afterGroupId, maximumCount));
            if (_pages.Count == 0)
            {
                throw new InvalidOperationException("No selector page was configured.");
            }

            return ValueTask.FromResult(_pages.Dequeue());
        }

        public ValueTask<UsageReconciliationProjectionSnapshot> ReadAsync(
            EntityId groupId,
            EntityId periodId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestUnitOfWorkContext context = _units.AssertActive(unitOfWorkContext);
            _operations.Add($"projection:{context.Sequence}");
            ProjectionContexts.Add(unitOfWorkContext);
            if (ProjectionFailure is { } projectionFailure)
            {
                ProjectionFailure = null;
                throw projectionFailure;
            }

            UsageReconciliationProjectionSnapshot projection = Projections[groupId];
            Assert.Equal(periodId, projection.PeriodId);
            return ValueTask.FromResult(projection);
        }

        public ValueTask<GroupQuotaReconciliationFactSnapshot?> ReadAsync(
            EntityId groupId,
            EntityId? periodId,
            long checkpointSourceEventSequence,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestUnitOfWorkContext context = _units.AssertActive(unitOfWorkContext);
            _operations.Add($"fact:{context.Sequence}");
            FactContexts.Add(unitOfWorkContext);
            if (FactFailure is { } factFailure)
            {
                FactFailure = null;
                throw factFailure;
            }

            GroupQuotaReconciliationFactSnapshot? fact = Facts[groupId];
            Assert.NotNull(fact);
            Assert.Equal(periodId, fact.PeriodId);
            Assert.Equal(
                checkpointSourceEventSequence,
                fact.CheckpointSourceEventSequence);
            return ValueTask.FromResult<GroupQuotaReconciliationFactSnapshot?>(fact);
        }

        public ValueTask<IReadOnlyList<long>>
            ListPeriodSourceEventSequencesAsync(
                EntityId groupId,
                EntityId periodId,
                long throughSourceEventSequence,
                long afterSourceEventSequence,
                int maximumCount,
                IUnitOfWorkContext unitOfWorkContext,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestUnitOfWorkContext context = _units.AssertActive(unitOfWorkContext);
            _operations.Add($"sequences:{context.Sequence}");
            SequenceContexts.Add(unitOfWorkContext);
            SequencePageCalls.Add(new(
                groupId,
                periodId,
                throughSourceEventSequence,
                afterSourceEventSequence,
                maximumCount));
            if (SequenceFailure is { } sequenceFailure)
            {
                SequenceFailure = null;
                throw sequenceFailure;
            }

            GroupQuotaReconciliationFactSnapshot fact = Assert.IsType<
                GroupQuotaReconciliationFactSnapshot>(Facts[groupId]);
            Assert.Equal(fact.PeriodId, periodId);
            Assert.Equal(fact.LatestPeriodEventSequence, throughSourceEventSequence);
            IReadOnlyList<long> allSequences = SourceEventSequences.TryGetValue(
                groupId,
                out IReadOnlyList<long>? configured)
                ? configured
                : Enumerable.Range(0, checked((int)fact.PeriodEventCount))
                    .Select(index => checked(
                        fact.FirstPeriodEventSequence + index))
                    .ToArray();
            IReadOnlyList<long> page = allSequences
                .Where(sequence => sequence > afterSourceEventSequence
                    && sequence <= throughSourceEventSequence)
                .Take(maximumCount)
                .ToArray();
            return ValueTask.FromResult(page);
        }

        public ValueTask<QuotaDeliveryHealthSnapshot> ReadAsync(
            EntityId groupId,
            IReadOnlyList<long> expectedSourceEventSequences,
            long checkpointSourceEventSequence,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestUnitOfWorkContext context = _units.AssertActive(unitOfWorkContext);
            _operations.Add($"delivery:{context.Sequence}");
            DeliveryContexts.Add(unitOfWorkContext);
            Assert.Equal(
                Projections[groupId].CheckpointSourceEventSequence,
                checkpointSourceEventSequence);
            DeliveryCalls.Add(new(
                groupId,
                expectedSourceEventSequences.ToArray(),
                checkpointSourceEventSequence));
            if (DeliveryFailure is { } deliveryFailure)
            {
                DeliveryFailure = null;
                throw deliveryFailure;
            }

            QuotaDeliveryHealthSnapshot delivery =
                DeliveryPages.TryGetValue(
                    groupId,
                    out Queue<QuotaDeliveryHealthSnapshot>? pages)
                    && pages.Count > 0
                    ? pages.Dequeue()
                    : Deliveries[groupId];
            return ValueTask.FromResult(delivery);
        }
    }

    private sealed class RecordingUnitOfWorkFactory(
        ICollection<string> operations) : IUnitOfWorkFactory
    {
        private IUnitOfWorkContext? _activeContext;

        internal int ActiveCount { get; private set; }

        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        internal List<IUnitOfWorkContext> Contexts { get; } = [];

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, ActiveCount);
            TestUnitOfWorkContext context = new(++BeginCalls);
            ActiveCount++;
            _activeContext = context;
            Contexts.Add(context);
            operations.Add($"begin:{context.Sequence}");
            return ValueTask.FromResult<IUnitOfWork>(new UnitOfWork(
                this,
                operations,
                context));
        }

        internal TestUnitOfWorkContext AssertActive(
            IUnitOfWorkContext unitOfWorkContext)
        {
            Assert.Equal(1, ActiveCount);
            Assert.Same(_activeContext, unitOfWorkContext);
            return Assert.IsType<TestUnitOfWorkContext>(unitOfWorkContext);
        }

        private sealed class UnitOfWork(
            RecordingUnitOfWorkFactory owner,
            ICollection<string> operations,
            TestUnitOfWorkContext context) : IUnitOfWork
        {
            private bool _committed;
            private bool _disposed;

            public IUnitOfWorkContext Context => context;

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.False(_committed);
                Assert.False(_disposed);
                _ = owner.AssertActive(context);
                _committed = true;
                owner.CommitCalls++;
                operations.Add($"commit:{context.Sequence}");
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                Assert.False(_disposed);
                _ = owner.AssertActive(context);
                _disposed = true;
                owner.DisposeCalls++;
                owner.ActiveCount--;
                owner._activeContext = null;
                operations.Add($"dispose:{context.Sequence}");
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed record TestUnitOfWorkContext(int Sequence) : IUnitOfWorkContext;

    private sealed class ScriptedSessionLock(params bool[] ownership)
        : IWorkerSessionLock
    {
        private readonly Queue<bool> _ownership = new(ownership);

        public WorkerJobIdentity Job => WorkerJobs.QuotaReconciliation;

        public long LockId => WorkerSessionLockId.Derive(Job);

        public ValueTask<bool> VerifyOwnershipAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                _ownership.Count == 0 || _ownership.Dequeue());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSessionLock : IWorkerSessionLock
    {
        internal int DisposeCalls { get; private set; }

        public WorkerJobIdentity Job => WorkerJobs.QuotaReconciliation;

        public long LockId => WorkerSessionLockId.Derive(Job);

        public ValueTask<bool> VerifyOwnershipAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class QueueLockProvider(params IWorkerSessionLock?[] locks)
        : IWorkerSessionLockProvider
    {
        private readonly Queue<IWorkerSessionLock?> _locks = new(locks);

        internal List<WorkerJobIdentity> RequestedJobs { get; } = [];

        public ValueTask<IWorkerSessionLock?> TryAcquireAsync(
            WorkerJobIdentity job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedJobs.Add(job);
            return ValueTask.FromResult(_locks.Dequeue());
        }
    }

    private sealed class RecordingOperationalEventWriter(bool throwOnWrite = false)
        : IOperationalEventWriter
    {
        internal List<OperationalEvent> Events { get; } = [];

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            Events.Add(new(eventName, payload, cancellationToken));
            if (throwOnWrite)
            {
                throw new InvalidOperationException("sensitive sink failure");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpOperationalEventWriter : IOperationalEventWriter
    {
        internal static NoOpOperationalEventWriter Instance { get; } = new();

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
