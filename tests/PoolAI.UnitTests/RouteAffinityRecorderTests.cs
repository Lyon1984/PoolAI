using PoolAI.BuildingBlocks;
using PoolAI.Modules.Routing.Application;

namespace PoolAI.UnitTests;

public sealed class RouteAffinityRecorderTests
{
    [Fact]
    public async Task RecorderUsesTheExistingAdvisoryStoreAndRejectsInvalidIdentities()
    {
        RecordingStore store = new();
        RouteAffinityRecorder recorder = new(store);
        EntityId group = EntityId.New();
        EntityId account = EntityId.New();
        string hash = new('a', 32);
        await recorder.StoreAsync(group, hash, account, 2, 3, TestContext.Current.CancellationToken);
        Assert.Equal(1, store.Calls);
        Assert.Equal(new RouteAffinity(account, 2, 3), store.Value);
        foreach (string invalid in new[] { "", new string('A', 32), new string('x', 31), new string('g', 32) })
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await recorder.StoreAsync(group, invalid, account, 2, 3, TestContext.Current.CancellationToken).ConfigureAwait(false));
        }
        await Assert.ThrowsAsync<ArgumentException>(async () => await recorder.StoreAsync(
            new EntityId(Guid.Empty), hash, account, 2, 3, TestContext.Current.CancellationToken).ConfigureAwait(false));
        await Assert.ThrowsAsync<ArgumentException>(async () => await recorder.StoreAsync(
            group, hash, new EntityId(Guid.Empty), 2, 3, TestContext.Current.CancellationToken).ConfigureAwait(false));
        await Assert.ThrowsAsync<ArgumentException>(async () => await recorder.StoreAsync(
            group, hash, account, 0, 3, TestContext.Current.CancellationToken).ConfigureAwait(false));
        await Assert.ThrowsAsync<ArgumentException>(async () => await recorder.StoreAsync(
            group, hash, account, 2, 0, TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(1, store.Calls);
    }

    private sealed class RecordingStore : IRouteAffinityStore
    {
        internal int Calls { get; private set; }
        internal RouteAffinity? Value { get; private set; }
        public ValueTask<RouteAffinity?> GetAsync(EntityId groupId, string sessionHash, CancellationToken cancellationToken) => ValueTask.FromResult(Value);
        public ValueTask SetAsync(EntityId groupId, string sessionHash, RouteAffinity affinity, CancellationToken cancellationToken)
        {
            Calls++; Value = affinity;
            return ValueTask.CompletedTask;
        }
    }
}
