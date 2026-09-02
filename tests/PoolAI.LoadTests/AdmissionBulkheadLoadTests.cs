using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Application;

namespace PoolAI.LoadTests;

// Governing contracts: DEC D-029 and AC-043.
public sealed class AdmissionBulkheadLoadTests
{
    [Fact]
    public async Task AdmissionBulkheadsRemainIsolatedAtFrozenCapacities()
    {
        using GatewayAdmissionController controller = new(new GatewayAdmissionOptions());

        await AssertPartitionIsolationAsync(
                controller,
                GatewayAdmissionKind.NonStream,
                permits: 200,
                queueLimit: 0)
            .ConfigureAwait(true);
        await AssertPartitionIsolationAsync(
                controller,
                GatewayAdmissionKind.Sse,
                permits: 600,
                queueLimit: 0)
            .ConfigureAwait(true);
        await AssertPartitionIsolationAsync(
                controller,
                GatewayAdmissionKind.Control,
                permits: 100,
                queueLimit: 50)
            .ConfigureAwait(true);
        await AssertPartitionIsolationAsync(
                controller,
                GatewayAdmissionKind.Usage,
                permits: 100,
                queueLimit: 20)
            .ConfigureAwait(true);
    }

    private static async Task AssertPartitionIsolationAsync(
        GatewayAdmissionController controller,
        GatewayAdmissionKind saturatedKind,
        int permits,
        int queueLimit)
    {
        List<GatewayAdmissionLease> held = await AcquirePermitsConcurrentlyAsync(
                controller,
                saturatedKind,
                permits)
            .ConfigureAwait(false);
        using CancellationTokenSource queuedCancellation = new();
        List<Task<Result<GatewayAdmissionLease>>> queued = FillQueue(
            controller,
            saturatedKind,
            queueLimit,
            queuedCancellation.Token);

        try
        {
            Result<GatewayAdmissionLease> rejected = await controller.AcquireAsync(
                    saturatedKind,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            AssertOverloaded(rejected);
            await AssertOtherPartitionsAvailableAsync(controller, saturatedKind)
                .ConfigureAwait(false);
        }
        finally
        {
            queuedCancellation.Cancel();
            await AssertQueueCanceledAsync(queued).ConfigureAwait(false);
            await ReleaseAllAsync(held).ConfigureAwait(false);
        }

        Result<GatewayAdmissionLease> restored = await controller.AcquireAsync(
                saturatedKind,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        Assert.True(restored.IsSuccess);
        await restored.Value.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<List<GatewayAdmissionLease>> AcquirePermitsConcurrentlyAsync(
        GatewayAdmissionController controller,
        GatewayAdmissionKind kind,
        int permits)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Result<GatewayAdmissionLease>>[] acquisitions = Enumerable
            .Range(0, permits)
            .Select(_ => Task.Run(
                async () =>
                {
                    await start.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return await controller.AcquireAsync(kind, cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken))
            .ToArray();
        start.SetResult();

        Result<GatewayAdmissionLease>[] acquired = await Task.WhenAll(acquisitions)
            .ConfigureAwait(false);
        Assert.All(acquired, result => Assert.True(result.IsSuccess));
        return acquired.Select(result => result.Value).ToList();
    }

    private static List<Task<Result<GatewayAdmissionLease>>> FillQueue(
        GatewayAdmissionController controller,
        GatewayAdmissionKind kind,
        int queueLimit,
        CancellationToken cancellationToken)
    {
        List<Task<Result<GatewayAdmissionLease>>> queued = new(queueLimit);
        for (int index = 0; index < queueLimit; index++)
        {
            Task<Result<GatewayAdmissionLease>> pending = controller
                .AcquireAsync(kind, cancellationToken)
                .AsTask();
            Assert.False(pending.IsCompleted);
            queued.Add(pending);
        }

        return queued;
    }

    private static void AssertOverloaded(Result<GatewayAdmissionLease> rejected)
    {
        Assert.True(rejected.IsFailure);
        Assert.Equal("gateway_overloaded", rejected.Error.Code);
        Assert.Equal(1, rejected.Error.RetryAfterSeconds);
        Assert.Equal(429, rejected.Error.Presentation?.Status);
        Assert.True(rejected.Error.Presentation?.Retryable is true);
    }

    private static async Task AssertOtherPartitionsAvailableAsync(
        GatewayAdmissionController controller,
        GatewayAdmissionKind saturatedKind)
    {
        foreach (GatewayAdmissionKind isolatedKind in Enum
                     .GetValues<GatewayAdmissionKind>()
                     .Where(kind => kind != saturatedKind))
        {
            Result<GatewayAdmissionLease> isolated = await controller
                .AcquireAsync(isolatedKind, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            Assert.True(isolated.IsSuccess);
            await isolated.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task AssertQueueCanceledAsync(
        IEnumerable<Task<Result<GatewayAdmissionLease>>> queued)
    {
        foreach (Task<Result<GatewayAdmissionLease>> pending in queued)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    async () => await pending.ConfigureAwait(false))
                .ConfigureAwait(false);
        }
    }

    private static async Task ReleaseAllAsync(
        IEnumerable<GatewayAdmissionLease> held)
    {
        foreach (GatewayAdmissionLease lease in held)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
