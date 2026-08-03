using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage;
using PoolAI.Modules.Usage.Infrastructure.Workers;
using PoolAI.Modules.Usage.Worker;

namespace PoolAI.UnitTests;

public sealed class UsageProjectionRebuildOneShotWorkerTests
{
    private static readonly EntityId GroupId = new(Guid.Parse(
        "91000000-0000-0000-0000-000000000001"));
    private static readonly EntityId PeriodId = new(Guid.Parse(
        "92000000-0000-0000-0000-000000000001"));
    private static readonly DateTimeOffset FirstHour = new(
        2030,
        1,
        1,
        0,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void OneShotWorkerIsAbsentByDefaultAndRegisteredOnlyWhenEnabled()
    {
        ServiceCollection disabled = new();
        disabled.AddUsageProjectionRebuildWorker(Configuration(enabled: false));

        Assert.DoesNotContain(
            disabled,
            static descriptor => descriptor.ServiceType == typeof(IHostedService));

        ServiceCollection enabled = new();
        enabled.AddUsageProjectionRebuildWorker(Configuration(enabled: true));
        enabled.AddUsageProjectionRebuildWorker(Configuration(enabled: true));

        Assert.Single(
            enabled,
            static descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType
                    == typeof(UsageProjectionRebuildOneShotService));
        Assert.Single(
            enabled,
            static descriptor => descriptor.ServiceType
                == typeof(UsageProjectionRebuildWorkerOptions));
    }

    [Theory]
    [InlineData("group")]
    [InlineData("period")]
    [InlineData("first")]
    [InlineData("last")]
    [InlineData("range")]
    public void EnabledOneShotWorkerRejectsMissingMalformedOrUnboundedInput(
        string corruption)
    {
        IConfiguration configuration = Configuration(enabled: true);
        configuration[corruption switch
        {
            "group" => "WorkerJobs:UsageRebuild:GroupId",
            "period" => "WorkerJobs:UsageRebuild:PeriodId",
            "first" => "WorkerJobs:UsageRebuild:FirstBucketStart",
            _ => "WorkerJobs:UsageRebuild:LastBucketStart",
        }] = corruption switch
        {
            "group" or "period" => Guid.Empty.ToString("D"),
            "first" or "last" => "2030-01-01T00:00:01Z",
            "range" => "2030-02-01T00:00:00Z",
            _ => throw new InvalidOperationException(),
        };

        Assert.Throws<InvalidOperationException>(() =>
            UsageProjectionRebuildWorkerOptions.FromConfiguration(configuration));
    }

    [Fact]
    public async Task EnabledEntryAcquiresTheDedicatedLockRunsExactlyOnceAndStopsHost()
    {
        RecordingJobLock jobLock = new();
        RecordingLockProvider lockProvider = new(jobLock);
        RecordingLifetime lifetime = new();
        List<int> exitCodes = [];
        int rebuildCalls = 0;
        UsageProjectionRebuildOneShotService service = new(
            lockProvider,
            (ownedLock, request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Same(jobLock, ownedLock);
                Assert.Equal(GroupId, request.GroupId);
                Assert.Equal(PeriodId, request.PeriodId);
                Assert.Equal(FirstHour, request.FirstBucketStart);
                Assert.Equal(FirstHour.AddHours(1), request.LastBucketStart);
                rebuildCalls++;
                return ValueTask.FromResult(new BoundedUsagePeriodRebuildResult(
                    BoundedUsagePeriodRebuildDisposition.Completed,
                    CheckpointSourceEventSequence: 17,
                    RebuiltBucketCount: 2,
                    RemainingProjectionVariance: System.Numerics.BigInteger.Zero));
            },
            UsageProjectionRebuildWorkerOptions.FromConfiguration(
                Configuration(enabled: true)),
            lifetime,
            exitCodes.Add,
            NullLogger<UsageProjectionRebuildOneShotService>.Instance);

        await service.StartAsync(TestToken).ConfigureAwait(true);
        await lifetime.Stopped.Task.WaitAsync(TestToken).ConfigureAwait(true);
        await service.StopAsync(TestToken).ConfigureAwait(true);

        Assert.Equal(WorkerJobs.UsageRebuild, Assert.Single(lockProvider.Jobs));
        Assert.Equal(1, rebuildCalls);
        Assert.True(jobLock.Disposed);
        Assert.Equal(0, Assert.Single(exitCodes));
        Assert.Equal(1, lifetime.StopCalls);
    }

    [Fact]
    public async Task BusyOneShotEntryDoesNotRetryOrInvokeTheRebuilder()
    {
        RecordingLockProvider lockProvider = new(jobLock: null);
        RecordingLifetime lifetime = new();
        List<int> exitCodes = [];
        int rebuildCalls = 0;
        UsageProjectionRebuildOneShotService service = new(
            lockProvider,
            (_, _, _) =>
            {
                rebuildCalls++;
                throw new InvalidOperationException("must not run");
            },
            UsageProjectionRebuildWorkerOptions.FromConfiguration(
                Configuration(enabled: true)),
            lifetime,
            exitCodes.Add,
            NullLogger<UsageProjectionRebuildOneShotService>.Instance);

        await service.StartAsync(TestToken).ConfigureAwait(true);
        await lifetime.Stopped.Task.WaitAsync(TestToken).ConfigureAwait(true);
        await service.StopAsync(TestToken).ConfigureAwait(true);

        Assert.Equal(WorkerJobs.UsageRebuild, Assert.Single(lockProvider.Jobs));
        Assert.Equal(0, rebuildCalls);
        Assert.Equal(1, Assert.Single(exitCodes));
        Assert.Equal(1, lifetime.StopCalls);
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static IConfiguration Configuration(bool enabled)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            ["WorkerJobs:UsageRebuild:Enabled"] = enabled.ToString(),
            ["WorkerJobs:UsageRebuild:GroupId"] = GroupId.Value.ToString("D"),
            ["WorkerJobs:UsageRebuild:PeriodId"] = PeriodId.Value.ToString("D"),
            ["WorkerJobs:UsageRebuild:FirstBucketStart"] =
                "2030-01-01T00:00:00Z",
            ["WorkerJobs:UsageRebuild:LastBucketStart"] =
                "2030-01-01T01:00:00Z",
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class RecordingLockProvider(IWorkerSessionLock? jobLock) :
        IWorkerSessionLockProvider
    {
        internal List<WorkerJobIdentity> Jobs { get; } = [];

        public ValueTask<IWorkerSessionLock?> TryAcquireAsync(
            WorkerJobIdentity job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Jobs.Add(job);
            return ValueTask.FromResult(jobLock);
        }
    }

    private sealed class RecordingJobLock : IWorkerSessionLock
    {
        public WorkerJobIdentity Job => WorkerJobs.UsageRebuild;

        public long LockId => WorkerSessionLockId.Derive(Job);

        internal bool Disposed { get; private set; }

        public ValueTask<bool> VerifyOwnershipAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        internal TaskCompletionSource Stopped { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int StopCalls { get; private set; }

        public void StopApplication()
        {
            StopCalls++;
            Stopped.TrySetResult();
        }
    }
}
