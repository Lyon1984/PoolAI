using System.Diagnostics.Metrics;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Application;

namespace PoolAI.UnitTests;

// Governing contracts: DEC D-029 and AC-043.
public sealed class GatewayAdmissionControllerTests
{
    [Fact]
    public void OptionsExposeFrozenDefaultsAndRejectInvalidCapacity()
    {
        GatewayAdmissionOptions options = new();

        Assert.Equal(200, options.DataNonStreamPermits);
        Assert.Equal(600, options.DataStreamPermits);
        Assert.Equal(0, options.DataQueueLimit);
        Assert.Equal(100, options.ControlPermits);
        Assert.Equal(50, options.ControlQueueLimit);
        Assert.Equal(100, options.UsagePermits);
        Assert.Equal(20, options.UsageQueueLimit);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GatewayAdmissionOptions(dataNonStreamPermits: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GatewayAdmissionOptions(dataStreamPermits: 10_001));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GatewayAdmissionOptions(dataQueueLimit: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GatewayAdmissionOptions(controlQueueLimit: 51));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GatewayAdmissionOptions(controlPermits: 1_001));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GatewayAdmissionOptions(usageQueueLimit: 21));
    }

    [Fact]
    public async Task ControlQueueIsFifoAndCancellationRemovesOnlyItsWaiter()
    {
        using GatewayAdmissionController controller = SmallController(controlQueueLimit: 3);
        Result<GatewayAdmissionLease> held = await controller.AcquireAsync(
            GatewayAdmissionKind.Control,
            TestContext.Current.CancellationToken);
        using CancellationTokenSource canceledWaiter = new();
        Task<Result<GatewayAdmissionLease>> first = controller
            .AcquireAsync(
                GatewayAdmissionKind.Control,
                TestContext.Current.CancellationToken)
            .AsTask();
        Task<Result<GatewayAdmissionLease>> canceled = controller
            .AcquireAsync(GatewayAdmissionKind.Control, canceledWaiter.Token)
            .AsTask();
        Task<Result<GatewayAdmissionLease>> third = controller
            .AcquireAsync(
                GatewayAdmissionKind.Control,
                TestContext.Current.CancellationToken)
            .AsTask();

        canceledWaiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await canceled.ConfigureAwait(false));
        await held.Value.DisposeAsync().ConfigureAwait(true);

        Result<GatewayAdmissionLease> firstLease = await first.ConfigureAwait(true);
        Assert.True(firstLease.IsSuccess);
        Assert.False(third.IsCompleted);
        await firstLease.Value.DisposeAsync().ConfigureAwait(true);

        Result<GatewayAdmissionLease> thirdLease = await third.ConfigureAwait(true);
        Assert.True(thirdLease.IsSuccess);
        await thirdLease.Value.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task ExecuteAndIdempotentLeaseDisposalReleasePermitsOnEveryPath()
    {
        using GatewayAdmissionController controller = SmallController();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await controller.ExecuteAsync<string>(
                    GatewayAdmissionKind.NonStream,
                    _ => ValueTask.FromException<string>(
                        new InvalidOperationException("injected")),
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(false));

        Result<GatewayAdmissionLease> first = await controller.AcquireAsync(
            GatewayAdmissionKind.NonStream,
            TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess);
        first.Value.Dispose();
        first.Value.Dispose();

        Result<string> executed = await controller.ExecuteAsync(
            GatewayAdmissionKind.NonStream,
            _ => ValueTask.FromResult("complete"),
            TestContext.Current.CancellationToken);
        Assert.True(executed.IsSuccess);
        Assert.Equal("complete", executed.Value);
    }

    [Fact]
    public async Task SaturatedExecuteReturnsTheFrozenFailureWithoutRunningWork()
    {
        using GatewayAdmissionController controller = SmallController();
        Result<GatewayAdmissionLease> held = await controller.AcquireAsync(
            GatewayAdmissionKind.Sse,
            TestContext.Current.CancellationToken);
        int executions = 0;

        Result<int> rejected = await controller.ExecuteAsync(
            GatewayAdmissionKind.Sse,
            _ => ValueTask.FromResult(Interlocked.Increment(ref executions)),
            TestContext.Current.CancellationToken);

        Assert.True(rejected.IsFailure);
        Assert.Equal("gateway_overloaded", rejected.Error.Code);
        Assert.Equal(1, rejected.Error.RetryAfterSeconds);
        Assert.Equal(0, executions);
        await held.Value.DisposeAsync().ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await controller.AcquireAsync(
                    (GatewayAdmissionKind)999,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(false));
    }

    [Fact]
    public async Task ServerWaitBudgetMapsToOverloadButClientCancellationDoesNot()
    {
        using GatewayAdmissionController controller = SmallController(controlQueueLimit: 1);
        Result<GatewayAdmissionLease> held = await controller.AcquireAsync(
            GatewayAdmissionKind.Control,
            TestContext.Current.CancellationToken);
        using CancellationTokenSource clientCancellation = new();
        using CancellationTokenSource serverWaitCancellation = new();
        Task<Result<GatewayAdmissionLease>> serverTimedOut = controller.AcquireAsync(
                GatewayAdmissionKind.Control,
                clientCancellation.Token,
                serverWaitCancellation.Token)
            .AsTask();

        serverWaitCancellation.Cancel();
        Result<GatewayAdmissionLease> overload = await serverTimedOut.ConfigureAwait(true);

        Assert.True(overload.IsFailure);
        Assert.Equal("gateway_overloaded", overload.Error.Code);
        Assert.Equal(1, overload.Error.RetryAfterSeconds);

        using CancellationTokenSource canceledClient = new();
        Task<Result<GatewayAdmissionLease>> disconnected = controller.AcquireAsync(
                GatewayAdmissionKind.Control,
                canceledClient.Token,
                CancellationToken.None)
            .AsTask();
        canceledClient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await disconnected.ConfigureAwait(false))
            .ConfigureAwait(true);

        await held.Value.DisposeAsync().ConfigureAwait(true);
        Result<GatewayAdmissionLease> restored = await controller.AcquireAsync(
                GatewayAdmissionKind.Control,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(restored.IsSuccess);
        await restored.Value.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task ClientCancellationWinsAnAlreadyCanceledDeadlineRace()
    {
        using GatewayAdmissionController controller = SmallController(controlQueueLimit: 1);
        using CancellationTokenSource clientCancellation = new();
        using CancellationTokenSource serverWaitCancellation = new();
        clientCancellation.Cancel();
        serverWaitCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await controller.AcquireAsync(
                        GatewayAdmissionKind.Control,
                        clientCancellation.Token,
                        serverWaitCancellation.Token)
                    .ConfigureAwait(false))
            .ConfigureAwait(true);
    }

#pragma warning disable MA0051 // One listener lifetime must observe acquire, reject, and release.
    [Fact]
    public async Task MetricsExposeOnlyFrozenLowCardinalityAdmissionLabels()
    {
        using Meter meter = new($"PoolAI.Gateway.Tests.{Guid.NewGuid():N}");
        using GatewayAdmissionMetrics metrics = new(meter);
        using GatewayAdmissionController controller = new(
            new GatewayAdmissionOptions(
                dataNonStreamPermits: 1,
                dataStreamPermits: 1,
                controlPermits: 1,
                controlQueueLimit: 0,
                usagePermits: 1,
                usageQueueLimit: 0),
            metrics);
        List<MetricReading> readings = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            readings.Add(new MetricReading(
                instrument.Name,
                value,
                FindTag(tags, "bulkhead"),
                FindTag(tags, "outcome"))));
        listener.Start();

        Result<GatewayAdmissionLease> held = await controller.AcquireAsync(
            GatewayAdmissionKind.Control,
            TestContext.Current.CancellationToken);
        Result<GatewayAdmissionLease> rejected = await controller.AcquireAsync(
            GatewayAdmissionKind.Control,
            TestContext.Current.CancellationToken);
        Assert.True(rejected.IsFailure);
        listener.RecordObservableInstruments();

        Assert.Contains(
            readings,
            reading => reading is
            {
                Instrument: GatewayAdmissionMetrics.ActiveInstrumentName,
                Value: 1,
                Bulkhead: "control",
                Outcome: "active",
            });
        Assert.Contains(
            readings,
            reading => reading is
            {
                Instrument: GatewayAdmissionMetrics.RejectedInstrumentName,
                Value: 1,
                Bulkhead: "control",
                Outcome: "capacity_exhausted",
            });
        Assert.All(
            readings,
            reading => Assert.True(
                reading.Bulkhead is
                    "data-nonstream" or "data-stream" or "control" or "usage"));

        await held.Value.DisposeAsync().ConfigureAwait(true);
        readings.Clear();
        listener.RecordObservableInstruments();
        Assert.Contains(
            readings,
            reading => reading is
            {
                Instrument: GatewayAdmissionMetrics.ActiveInstrumentName,
                Value: 0,
                Bulkhead: "control",
                Outcome: "active",
            });
    }
#pragma warning restore MA0051

    private static GatewayAdmissionController SmallController(
        int controlQueueLimit = 0) =>
        new(new GatewayAdmissionOptions(
            dataNonStreamPermits: 1,
            dataStreamPermits: 1,
            controlPermits: 1,
            controlQueueLimit: controlQueueLimit,
            usagePermits: 1,
            usageQueueLimit: 0));

    private static string FindTag(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string name)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (string.Equals(tag.Key, name, StringComparison.Ordinal))
            {
                return Assert.IsType<string>(tag.Value);
            }
        }

        throw new Xunit.Sdk.XunitException($"Metric tag '{name}' was not present.");
    }

    private sealed record MetricReading(
        string Instrument,
        long Value,
        string Bulkhead,
        string Outcome);
}
