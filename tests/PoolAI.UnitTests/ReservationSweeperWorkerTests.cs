using System.Data.Common;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Infrastructure.Workers;
using PoolAI.Modules.GroupQuota.Worker;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class ReservationSweeperWorkerTests
{
    private static readonly DateTimeOffset SweepStart = new(
        2026,
        8,
        1,
        6,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void WorkerRegistrationRequiresServicesAndConfiguration()
    {
        IConfiguration configuration = WorkerConfiguration();
        ServiceCollection services = new();

        Assert.Throws<ArgumentNullException>(
            () => ReservationWorkerDependencyInjection
                .AddGroupQuotaReservationSweeper(null!, configuration));
        Assert.Throws<ArgumentNullException>(
            () => services.AddGroupQuotaReservationSweeper(null!));
        Assert.Empty(services);
    }

    [Fact]
    public void SweeperServiceRequiresEveryDependency()
    {
        RecordingLockProvider lockProvider = new(null);
        ReservationSweepRound round = static (_, _, _) =>
            ValueTask.CompletedTask;
        FakeTimeProvider timeProvider = new();
        RecordingLogger logger = new();

        Assert.Throws<ArgumentNullException>(
            () => new ReservationSweeperService(
                null!,
                round,
                timeProvider,
                logger));
        Assert.Throws<ArgumentNullException>(
            () => new ReservationSweeperService(
                lockProvider,
                (ReservationSweepRound)null!,
                timeProvider,
                logger));
        Assert.Throws<ArgumentNullException>(
            () => new ReservationSweeperService(
                lockProvider,
                round,
                null!,
                logger));
        Assert.Throws<ArgumentNullException>(
            () => new ReservationSweeperService(
                lockProvider,
                round,
                timeProvider,
                null!));
        Assert.Throws<ArgumentNullException>(
            () => new ReservationSweeperService(
                lockProvider,
                (ReservationSweeperProcessor)null!,
                timeProvider,
                logger));
    }

    [Fact]
    public void BaseGroupQuotaRegistrationDoesNotAddWorkerRuntime()
    {
        IConfiguration configuration = WorkerConfiguration();
        ServiceCollection services = new();
        services.AddSingleton(configuration);

        services.AddGroupQuotaModule();

        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(
            services,
            static descriptor =>
                descriptor.ServiceType == typeof(ReservationSweeperProcessor));
        Assert.DoesNotContain(
            services,
            static descriptor =>
                descriptor.ImplementationType
                    == typeof(ReservationSweeperService));
    }

    [Fact]
    public void ExplicitWorkerRegistrationIsSingletonAndIdempotent()
    {
        IConfiguration configuration = WorkerConfiguration();
        ServiceCollection services = new();

        IServiceCollection returned = services
            .AddGroupQuotaReservationSweeper(configuration);
        services.AddGroupQuotaReservationSweeper(configuration);

        Assert.Same(services, returned);
        ServiceDescriptor hostedService = Assert.Single(
            services,
            static descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType
                    == typeof(ReservationSweeperService));
        Assert.Equal(ServiceLifetime.Singleton, hostedService.Lifetime);
        ServiceDescriptor processor = Assert.Single(
            services,
            static descriptor =>
                descriptor.ServiceType == typeof(ReservationSweeperProcessor));
        Assert.Equal(ServiceLifetime.Singleton, processor.Lifetime);
    }

    [Fact]
    public void WorkerRegistrationRejectsAChangedSweepInterval()
    {
        ServiceCollection services = new();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddGroupQuotaReservationSweeper(
                WorkerConfiguration(sweepSeconds: 31)));

        Assert.Equal(
            "Reservation sweep interval must equal thirty seconds.",
            exception.Message);
        Assert.Empty(services);
    }

    [Fact]
    public async Task SingleRoundUsesTheReservationJobAndFixedBoundedPage()
    {
        RecordingSessionLock jobLock = new();
        RecordingLockProvider lockProvider = new(jobLock);
        int processCalls = 0;
        ReservationSweepRound processRound =
            (actualLock, pageSize, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Same(jobLock, actualLock);
                Assert.Equal(ReservationSweeperService.PageSize, pageSize);
                processCalls++;
                return ValueTask.CompletedTask;
            };
        using ReservationSweeperService service = new(
            lockProvider,
            processRound,
            new FakeTimeProvider(),
            NullLogger<ReservationSweeperService>.Instance);

        await service.RunSingleRoundAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, lockProvider.AcquireCount);
        Assert.Equal(WorkerJobs.ReservationSweeper, lockProvider.RequestedJob);
        Assert.Equal(1, processCalls);
        Assert.Equal(1, jobLock.DisposeCount);
        Assert.InRange(ReservationSweeperService.PageSize, 1, 1000);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            ReservationSweeperService.SweepInterval);
    }

    [Fact]
    public async Task SingleRoundStaysStandbyWhenAnotherWorkerOwnsTheJob()
    {
        RecordingLockProvider lockProvider = new(null);
        RecordingLogger logger = new();
        int processCalls = 0;
        using ReservationSweeperService service = new(
            lockProvider,
            (_, _, _) =>
            {
                processCalls++;
                return ValueTask.CompletedTask;
            },
            new FakeTimeProvider(),
            logger);

        await service.RunSingleRoundAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, lockProvider.AcquireCount);
        Assert.Equal(WorkerJobs.ReservationSweeper, lockProvider.RequestedJob);
        Assert.Equal(0, processCalls);
        LogRecord record = Assert.Single(logger.Records);
        Assert.Equal(2302, record.EventId);
        Assert.Equal(LogLevel.Debug, record.Level);
        Assert.Null(record.FailureCode);
    }

    [Fact]
    public async Task SingleRoundAlwaysDisposesAnOwnedLockWhenProcessingFails()
    {
        RecordingSessionLock jobLock = new();
        using ReservationSweeperService service = new(
            new RecordingLockProvider(jobLock),
            static (_, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("sensitive test detail");
            },
            new FakeTimeProvider(),
            NullLogger<ReservationSweeperService>.Instance);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RunSingleRoundAsync(
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, jobLock.DisposeCount);
    }

    [Fact]
    public async Task DisabledLoggingDoesNotChangeStandbyOrRetryControlFlow()
    {
        RecordingLockProvider standbyLockProvider = new(null);
        using (ReservationSweeperService standby = new(
            standbyLockProvider,
            static (_, _, _) => throw new InvalidOperationException(
                "The standby worker must not process a round."),
            new FakeTimeProvider(),
            NullLogger<ReservationSweeperService>.Instance))
        {
            await standby.RunSingleRoundAsync(
                TestContext.Current.CancellationToken);
        }

        TaskCompletionSource attempted = Completion();
        ReacquiringLockProvider retryLockProvider = new();
        using ReservationSweeperService retrying = new(
            retryLockProvider,
            (_, _, _) =>
            {
                attempted.TrySetResult();
                throw new TestUnexpectedException("sensitive retry detail");
            },
            new FakeTimeProvider(SweepStart),
            NullLogger<ReservationSweeperService>.Instance);

        await retrying.StartAsync(TestContext.Current.CancellationToken);
        await attempted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await PumpAsync();
        await retrying.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, standbyLockProvider.AcquireCount);
        Assert.Equal(1, retryLockProvider.AcquireCount);
        Assert.Equal(1, retryLockProvider.DisposeCount);
    }

    [Fact]
    public async Task ScheduledRoundsKeepAnAbsoluteThirtySecondCadence()
    {
        FakeTimeProvider timeProvider = new(SweepStart);
        TaskCompletionSource firstStarted = Completion();
        TaskCompletionSource releaseFirst = Completion();
        TaskCompletionSource secondStarted = Completion();
        List<DateTimeOffset> starts = [];
        int calls = 0;
        using ReservationSweeperService service = new(
            new ReacquiringLockProvider(),
            async (_, _, cancellationToken) =>
            {
                int call = Interlocked.Increment(ref calls);
                starts.Add(timeProvider.GetUtcNow());
                if (call == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (call == 2)
                {
                    secondStarted.TrySetResult();
                }
            },
            timeProvider,
            NullLogger<ReservationSweeperService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(20));
        releaseFirst.TrySetResult();
        await PumpAsync();

        timeProvider.Advance(TimeSpan.FromSeconds(9));
        await PumpAsync();
        Assert.Equal(1, Volatile.Read(ref calls));

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await secondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal([SweepStart, SweepStart.AddSeconds(30)], starts);
    }

    [Fact]
    public async Task RoundBudgetRestartsFromANewCadenceTick()
    {
        FakeTimeProvider timeProvider = new(SweepStart);
        RecordingLogger logger = new();
        TaskCompletionSource firstStarted = Completion();
        TaskCompletionSource firstCancelled = Completion();
        TaskCompletionSource secondStarted = Completion();
        int calls = 0;
        using ReservationSweeperService service = new(
            new ReacquiringLockProvider(),
            async (_, _, cancellationToken) =>
            {
                int call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        cancellationToken.IsCancellationRequested)
                    {
                        firstCancelled.TrySetResult();
                        throw;
                    }
                }
                else if (call == 2)
                {
                    secondStarted.TrySetResult();
                }
            },
            timeProvider,
            logger);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(ReservationSweeperService.RoundBudget);
        await firstCancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        LogRecord budgetRecord = await logger.WaitForAsync(
            eventId: 2301,
            TestContext.Current.CancellationToken);
        await PumpAsync();
        Assert.Equal(1, Volatile.Read(ref calls));

        timeProvider.Advance(
            ReservationSweeperService.SweepInterval
            - ReservationSweeperService.RoundBudget);
        await secondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.True(
            ReservationSweeperService.RoundBudget
            < ReservationSweeperService.SweepInterval);
        Assert.Equal("round_budget_exhausted", budgetRecord.FailureCode);
        Assert.DoesNotContain(
            "sensitive",
            budgetRecord.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reservation_dependency", "dependency_unavailable")]
    [InlineData("reservation_numeric", "quota_invariant_failure")]
    [InlineData("reservation_other", "round_failure")]
    [InlineData("database", "dependency_unavailable")]
    [InlineData("io", "transient_failure")]
    [InlineData("timeout", "transient_failure")]
    [InlineData("invalid_operation", "round_failure")]
    [InlineData("unexpected", "unexpected_failure")]
    public async Task ScheduledRoundMapsFailuresToSafeRetryClassifications(
        string failureKind,
        string expectedCode)
    {
        Exception failure = failureKind switch
        {
            "reservation_dependency" => new ReservationSweepFailureException(
                QuotaLedgerFailure.DependencyUnavailable),
            "reservation_numeric" => new ReservationSweepFailureException(
                QuotaLedgerFailure.TokenNumericOverflow),
            "reservation_other" => new ReservationSweepFailureException(
                QuotaLedgerFailure.Internal),
            "database" => new TestDbException("sensitive database detail"),
            "io" => new IOException("sensitive I/O detail"),
            "timeout" => new TimeoutException("sensitive timeout detail"),
            "invalid_operation" => new InvalidOperationException(
                "sensitive invariant detail"),
            _ => new TestUnexpectedException("sensitive unexpected detail"),
        };
        FakeTimeProvider timeProvider = new(SweepStart);
        RecordingLogger logger = new();
        ReacquiringLockProvider lockProvider = new();
        int processCalls = 0;
        using ReservationSweeperService service = new(
            lockProvider,
            (_, _, _) =>
            {
                processCalls++;
                throw failure;
            },
            timeProvider,
            logger);

        await service.StartAsync(TestContext.Current.CancellationToken);
        LogRecord record = await logger.WaitForAsync(
            eventId: 2301,
            TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, processCalls);
        Assert.Equal(1, lockProvider.AcquireCount);
        Assert.Equal(1, lockProvider.DisposeCount);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Equal(expectedCode, record.FailureCode);
        Assert.Null(record.Exception);
        Assert.DoesNotContain(
            "sensitive",
            record.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockAcquisitionFailureIsClassifiedWithoutProcessingARound()
    {
        RecordingLogger logger = new();
        ThrowingLockProvider lockProvider = new(
            new TestDbException("sensitive lock detail"));
        int processCalls = 0;
        using ReservationSweeperService service = new(
            lockProvider,
            (_, _, _) =>
            {
                processCalls++;
                return ValueTask.CompletedTask;
            },
            new FakeTimeProvider(SweepStart),
            logger);

        await service.StartAsync(TestContext.Current.CancellationToken);
        LogRecord record = await logger.WaitForAsync(
            eventId: 2301,
            TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, lockProvider.AcquireCount);
        Assert.Equal(0, processCalls);
        Assert.Equal("dependency_unavailable", record.FailureCode);
        Assert.DoesNotContain(
            "sensitive",
            record.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingAnActiveRoundCancelsAndDisposesWithoutRetryLogging()
    {
        FakeTimeProvider timeProvider = new(SweepStart);
        RecordingLogger logger = new();
        ReacquiringLockProvider lockProvider = new();
        TaskCompletionSource started = Completion();
        TaskCompletionSource cancelled = Completion();
        using ReservationSweeperService service = new(
            lockProvider,
            async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            },
            timeProvider,
            logger);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task stop = service.StopAsync(TestContext.Current.CancellationToken);
        await cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        await stop;

        Assert.Equal(1, lockProvider.AcquireCount);
        Assert.Equal(1, lockProvider.DisposeCount);
        Assert.DoesNotContain(
            logger.Records,
            static record => record.EventId == 2301);
    }

    [Fact]
    public async Task ReservationSweepRecoveryLatencyP99StaysWithinSixtySeconds()
    {
        FakeTimeProvider timeProvider = new(SweepStart);
        using ReservationSweeperService service = CreateScheduledSweeper(
            timeProvider,
            out ScheduledQuotaLedgerRepository repository,
            out ReacquiringLockProvider lockProvider);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await repository.WaitForRoundAsync(
            round: 1,
            TestContext.Current.CancellationToken);
        await PumpAsync();

        Assert.Equal(100, repository.Observations.Length);
        Assert.True(repository.DeferredCandidateWasBehindFirstRoundCursor);
        Assert.DoesNotContain(
            repository.Observations,
            static observation => observation.Suffix == 101);

        timeProvider.Advance(ReservationSweeperService.SweepInterval);
        await repository.WaitForRoundAsync(
            round: 2,
            TestContext.Current.CancellationToken);
        await PumpAsync();

        SweepObservation secondRound = Assert.Single(
            repository.Observations,
            static observation => observation.Suffix == 101);
        Assert.Equal(2, secondRound.Round);
        Assert.Equal(
            SweepStart.AddSeconds(30),
            secondRound.ProcessedAt);

        timeProvider.Advance(ReservationSweeperService.SweepInterval);
        await repository.WaitForRoundAsync(
            round: 3,
            TestContext.Current.CancellationToken);

        await service.StopAsync(TestContext.Current.CancellationToken);

        SweepObservation thirdRound = Assert.Single(
            repository.Observations,
            static observation => observation.Suffix == 102);
        Assert.Equal(3, thirdRound.Round);
        Assert.Equal(
            SweepStart.AddSeconds(60),
            thirdRound.ProcessedAt);
        Assert.Equal(102, repository.Observations.Length);
        Assert.Equal(3, lockProvider.AcquireCount);
        Assert.Equal(3, lockProvider.DisposeCount);

        AssertRecoveryP99(repository.Observations);
    }

    private static IConfiguration WorkerConfiguration(int sweepSeconds = 30) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Quota:ReservationSweepSeconds"] =
                        sweepSeconds.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                })
            .Build();

    private static QuotaExpiryCandidate Candidate(
        int suffix,
        DateTimeOffset leaseExpiresAt) => new(
            Id(1000 + suffix),
            Id(2000 + suffix),
            Id(3000 + suffix),
            Id(4000 + suffix),
            leaseExpiresAt);

    private static EntityId Id(int suffix) => new(
        Guid.Parse($"018f3a4b-5c6d-7e8f-9123-{suffix:D12}"));

    private static ReservationSweeperService CreateScheduledSweeper(
        FakeTimeProvider timeProvider,
        out ScheduledQuotaLedgerRepository repository,
        out ReacquiringLockProvider lockProvider)
    {
        List<QuotaExpiryCandidate> candidates = Enumerable
            .Range(1, ReservationSweeperService.PageSize)
            .Select(suffix => Candidate(suffix, SweepStart))
            .Append(Candidate(101, SweepStart.AddSeconds(1)))
            .Append(Candidate(102, SweepStart.AddSeconds(31)))
            .ToList();
        repository = new(timeProvider, candidates);
        ReservationSweeperProcessor processor = new(
            repository,
            new CompletingUnitOfWorkFactory(),
            NoOpOperationalEventWriter.Instance);
        lockProvider = new();
        return new(
            lockProvider,
            processor,
            timeProvider,
            NullLogger<ReservationSweeperService>.Instance);
    }

    private static void AssertRecoveryP99(SweepObservation[] observations)
    {
        TimeSpan p99 = NearestRankP99(
            observations.Select(
                static observation => observation.RecoveryLatency));
        Assert.All(
            observations,
            static observation => Assert.True(
                observation.RecoveryLatency >= TimeSpan.Zero));
        Assert.True(
            p99 <= TimeSpan.FromSeconds(60),
            $"Observed reservation recovery p99 was {p99.TotalSeconds:F3}s.");
    }

    private static TimeSpan NearestRankP99(IEnumerable<TimeSpan> samples)
    {
        long[] orderedTicks = samples
            .Select(static sample => sample.Ticks)
            .Order()
            .ToArray();
        Assert.NotEmpty(orderedTicks);
        int rank = (int)Math.Ceiling(orderedTicks.Length * 0.99d);
        return TimeSpan.FromTicks(orderedTicks[rank - 1]);
    }

    private static async Task PumpAsync()
    {
        for (int iteration = 0; iteration < 10; iteration++)
        {
            await Task.Yield();
        }
    }

    private static TaskCompletionSource Completion() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RecordingLogger : ILogger<ReservationSweeperService>
    {
        private readonly Lock _gate = new();
        private readonly List<LogRecord> _records = [];
        private readonly Dictionary<int, TaskCompletionSource<LogRecord>> _waiters = [];

        internal LogRecord[] Records
        {
            get
            {
                lock (_gate)
                {
                    return _records.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NoOpScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string? failureCode = state is IEnumerable<
                KeyValuePair<string, object?>> properties
                    ? properties.FirstOrDefault(
                        static property => string.Equals(
                            property.Key,
                            "FailureCode",
                            StringComparison.Ordinal)).Value
                        as string
                    : null;
            LogRecord record = new(
                eventId.Id,
                logLevel,
                formatter(state, exception),
                failureCode,
                exception);
            lock (_gate)
            {
                _records.Add(record);
                if (_waiters.TryGetValue(
                        eventId.Id,
                        out TaskCompletionSource<LogRecord>? waiter))
                {
                    waiter.TrySetResult(record);
                }
            }
        }

        internal Task<LogRecord> WaitForAsync(
            int eventId,
            CancellationToken cancellationToken)
        {
            Task<LogRecord> task;
            lock (_gate)
            {
                LogRecord? existing = _records.FirstOrDefault(
                    record => record.EventId == eventId);
                if (existing is not null)
                {
                    return Task.FromResult(existing);
                }

                if (!_waiters.TryGetValue(
                        eventId,
                        out TaskCompletionSource<LogRecord>? waiter))
                {
                    waiter = new(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Add(eventId, waiter);
                }

                task = waiter.Task;
            }

            return task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        private sealed class NoOpScope : IDisposable
        {
            internal static NoOpScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record LogRecord(
        int EventId,
        LogLevel Level,
        string Message,
        string? FailureCode,
        Exception? Exception);

    private sealed class TestDbException(string message) : DbException(message);

    private sealed class TestUnexpectedException(string message) : Exception(message);

    private sealed class RecordingLockProvider(IWorkerSessionLock? jobLock)
        : IWorkerSessionLockProvider
    {
        private IWorkerSessionLock? _jobLock = jobLock;

        internal int AcquireCount { get; private set; }

        internal WorkerJobIdentity? RequestedJob { get; private set; }

        public ValueTask<IWorkerSessionLock?> TryAcquireAsync(
            WorkerJobIdentity job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            RequestedJob = job;
            return ValueTask.FromResult(
                Interlocked.Exchange(ref _jobLock, null));
        }
    }

    private sealed class ThrowingLockProvider(Exception failure)
        : IWorkerSessionLockProvider
    {
        internal int AcquireCount { get; private set; }

        public ValueTask<IWorkerSessionLock?> TryAcquireAsync(
            WorkerJobIdentity job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WorkerJobs.ReservationSweeper, job);
            AcquireCount++;
            throw failure;
        }
    }

    private sealed class RecordingSessionLock : IWorkerSessionLock
    {
        internal int DisposeCount { get; private set; }

        public WorkerJobIdentity Job => WorkerJobs.ReservationSweeper;

        public long LockId => 1;

        public ValueTask<bool> VerifyOwnershipAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReacquiringLockProvider : IWorkerSessionLockProvider
    {
        internal int AcquireCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public ValueTask<IWorkerSessionLock?> TryAcquireAsync(
            WorkerJobIdentity job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WorkerJobs.ReservationSweeper, job);
            AcquireCount++;
            return ValueTask.FromResult<IWorkerSessionLock?>(
                new ReacquiredSessionLock(this));
        }

        private sealed class ReacquiredSessionLock(ReacquiringLockProvider owner)
            : IWorkerSessionLock
        {
            public WorkerJobIdentity Job => WorkerJobs.ReservationSweeper;

            public long LockId => WorkerSessionLockId.Derive(Job);

            public ValueTask<bool> VerifyOwnershipAsync(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(true);
            }

            public ValueTask DisposeAsync()
            {
                owner.DisposeCount++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ScheduledQuotaLedgerRepository : IQuotaLedgerRepository
    {
        private readonly Lock _gate = new();
        private readonly FakeTimeProvider _timeProvider;
        private readonly Dictionary<EntityId, QuotaExpiryCandidate> _pending;
        private readonly Dictionary<int, TaskCompletionSource> _rounds = [];
        private readonly Dictionary<EntityId, int> _finalCandidateRounds = [];
        private readonly List<SweepObservation> _observations = [];
        private int _currentRound;

        internal ScheduledQuotaLedgerRepository(
            FakeTimeProvider timeProvider,
            IEnumerable<QuotaExpiryCandidate> candidates)
        {
            _timeProvider = timeProvider;
            _pending = candidates.ToDictionary(
                static candidate => candidate.ReservationId);
        }

        internal bool DeferredCandidateWasBehindFirstRoundCursor
        {
            get;
            private set;
        }

        internal SweepObservation[] Observations
        {
            get
            {
                lock (_gate)
                {
                    return _observations.ToArray();
                }
            }
        }

        internal Task WaitForRoundAsync(
            int round,
            CancellationToken cancellationToken)
        {
            Task task;
            lock (_gate)
            {
                task = RoundCompletion(round).Task;
            }

            return task.WaitAsync(
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }

        public ValueTask<IReadOnlyList<QuotaExpiryCandidate>>
            ListDueExpiryCandidatesAsync(
                QuotaExpiryCandidateKey? after,
                int pageSize,
                IUnitOfWorkContext unitOfWorkContext,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (after is null)
                {
                    _currentRound++;
                }

                DateTimeOffset now = _timeProvider.GetUtcNow();
                QuotaExpiryCandidate[] page = _pending.Values
                    .Where(candidate => candidate.LeaseExpiresAt <= now)
                    .Where(candidate =>
                        after is null || Compare(candidate.Key, after) > 0)
                    .OrderBy(static candidate => candidate.LeaseExpiresAt)
                    .ThenBy(
                        static candidate =>
                            candidate.ReservationId.Value.ToString("N"),
                        StringComparer.Ordinal)
                    .Take(pageSize)
                    .ToArray();

                if (_currentRound == 1 && after is not null)
                {
                    QuotaExpiryCandidate deferred = _pending[Id(1101)];
                    DeferredCandidateWasBehindFirstRoundCursor =
                        Compare(deferred.Key, after) > 0
                        && deferred.LeaseExpiresAt > now;
                }

                if (page.Length == 0)
                {
                    RoundCompletion(_currentRound).TrySetResult();
                }
                else if (page.Length < pageSize)
                {
                    _finalCandidateRounds[page[^1].ReservationId] =
                        _currentRound;
                }

                return ValueTask.FromResult<
                    IReadOnlyList<QuotaExpiryCandidate>>(page);
            }
        }

        public ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> ExpireAsync(
            ExpireReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                QuotaExpiryCandidate candidate = write.Candidate;
                Assert.True(_pending.Remove(candidate.ReservationId));
                DateTimeOffset processedAt = _timeProvider.GetUtcNow();
                _observations.Add(new(
                    Suffix(candidate.ReservationId),
                    candidate.LeaseExpiresAt,
                    processedAt,
                    processedAt - candidate.LeaseExpiresAt,
                    _currentRound));

                if (_finalCandidateRounds.Remove(
                        candidate.ReservationId,
                        out int round))
                {
                    RoundCompletion(round).TrySetResult();
                }

                return ValueTask.FromResult(
                    QuotaRepositoryResult<QuotaTransitionRow>.Success(new(
                        candidate.ReservationId,
                        candidate.PeriodId,
                        ReservationStatus.Expired,
                        TotalTokens: BigInteger.One,
                        ConsumedTokens: BigInteger.One,
                        ReservedTokens: BigInteger.Zero,
                        RemainingTokens: BigInteger.Zero)));
            }
        }

        public ValueTask<QuotaRepositoryResult<QuotaReservationRow>> ReserveAsync(
            ReserveQuotaWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(
                nameof(ReserveAsync));

        public ValueTask<QuotaRepositoryResult<QuotaDispatchRow>>
            MarkDispatchedAsync(
                MarkReservationDispatchedWrite write,
                IUnitOfWorkContext unitOfWorkContext,
                CancellationToken cancellationToken) => throw Unexpected(
                    nameof(MarkDispatchedAsync));

        public ValueTask<QuotaRepositoryResult<QuotaRenewalRow>> RenewAsync(
            RenewReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(
                nameof(RenewAsync));

        public ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> SettleAsync(
            SettleReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(
                nameof(SettleAsync));

        public ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> ReleaseAsync(
            ReleaseReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(
                nameof(ReleaseAsync));

        public ValueTask<QuotaRepositoryResult<UsageAdjustmentRow>> AdjustUsageAsync(
            AdjustAttemptUsageWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(
                nameof(AdjustUsageAsync));

        public ValueTask<AttemptSettlementFact?> GetAttemptSettlementFactAsync(
            EntityId attemptId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(
                nameof(GetAttemptSettlementFactAsync));

        private static int Compare(
            QuotaExpiryCandidateKey left,
            QuotaExpiryCandidateKey right)
        {
            int leaseComparison = left.LeaseExpiresAt.CompareTo(
                right.LeaseExpiresAt);
            return leaseComparison != 0
                ? leaseComparison
                : StringComparer.Ordinal.Compare(
                    left.ReservationId.Value.ToString("N"),
                    right.ReservationId.Value.ToString("N"));
        }

        private static int Suffix(EntityId reservationId) =>
            int.Parse(
                reservationId.Value.ToString("N")[^12..],
                System.Globalization.CultureInfo.InvariantCulture) - 1000;

        private TaskCompletionSource RoundCompletion(int round)
        {
            if (!_rounds.TryGetValue(round, out TaskCompletionSource? completion))
            {
                completion = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _rounds.Add(round, completion);
            }

            return completion;
        }

        private static InvalidOperationException Unexpected(string operation) =>
            new($"The {operation} repository method is outside the sweeper SLO test.");
    }

    private sealed class CompletingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        public ValueTask<IUnitOfWork> BeginAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IUnitOfWork>(new CompletingUnitOfWork());
        }

        private sealed class CompletingUnitOfWork : IUnitOfWork
        {
            public IUnitOfWorkContext Context { get; } =
                new CompletingUnitOfWorkContext();

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private sealed class CompletingUnitOfWorkContext : IUnitOfWorkContext;
        }
    }

    private sealed class NoOpOperationalEventWriter : IOperationalEventWriter
    {
        internal static NoOpOperationalEventWriter Instance { get; } = new();

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SweepObservation(
        int Suffix,
        DateTimeOffset DueAt,
        DateTimeOffset ProcessedAt,
        TimeSpan RecoveryLatency,
        int Round);
}
