using System.Text;
using Microsoft.Extensions.Time.Testing;
using PoolAI.Modules.Gateway.Application;

namespace PoolAI.UnitTests;

// ADR 0017: exact mode, fixed resource guard, full-lifecycle deadline and cleanup.
public sealed class GatewayModelDiscriminatorTests
{
    [Theory]
    [InlineData("{}", GatewayModelClassification.Absent)]
    [InlineData("{\"stream\":true}", GatewayModelClassification.DeclaredTrue)]
    [InlineData("{\"stream\":false}", GatewayModelClassification.DeclaredFalse)]
    [InlineData("{\"input\":{\"stream\":true},\"stream\":false}", GatewayModelClassification.DeclaredFalse)]
    [InlineData("{\"input\":\"a\\\"b\",\"str\\u0065am\":true}", GatewayModelClassification.DeclaredTrue)]
    [InlineData("{\"input\":[null,1,true,{\"a\":[]}],\"stream\":true}", GatewayModelClassification.DeclaredTrue)]
    [InlineData("{\"stream\":true,\"stream\":false}", GatewayModelClassification.DeclaredTrue)]
    [InlineData("{\"stream\":null}", GatewayModelClassification.Quarantine)]
    [InlineData("{\"stream\":1}", GatewayModelClassification.Quarantine)]
    [InlineData("{\"stream\":truE}", GatewayModelClassification.Quarantine)]
    [InlineData("{\"stream\":trueX}", GatewayModelClassification.Quarantine)]
    [InlineData("{\"stream\":\"true\"}", GatewayModelClassification.Quarantine)]
    [InlineData("[]", GatewayModelClassification.Quarantine)]
    [InlineData("", GatewayModelClassification.Quarantine)]
    [InlineData("{\"a\":\"unterminated", GatewayModelClassification.Quarantine)]
    [InlineData("{\"a\":1} true", GatewayModelClassification.Quarantine)]
    public void ChunkBoundariesAndNestedNamesDoNotChangeMode(string body, GatewayModelClassification expected)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        foreach (int size in new[] { 1, 2, 7, 32_768 })
        {
            GatewayStreamDiscriminator scanner = new();
            for (int index = 0; index < bytes.Length; index += size)
            {
                scanner.Feed(bytes.AsSpan(index, Math.Min(size, bytes.Length - index)));
            }

            Assert.Equal(expected, scanner.Finish());
        }
    }

    [Fact]
    public async Task CompleteReplayPreservesLargeValuesEscapesAndOwnerOnlyStorage()
    {
        string body = " {\"input\":\"" + new string('x', 80_000) + "\", \"stream\":true} \n";
        using GatewayAdmissionMetrics metrics = new();
        using GatewayModelDiscriminator guard = new(metrics, TimeProvider.System);
        using GatewayModelDiscriminatorLease lease = guard.TryAcquire(TestContext.Current.CancellationToken).Value;
        using MemoryStream source = new(Encoding.UTF8.GetBytes(body));
        await lease.SpoolAsync(source, null, 1_048_576, true);
        Assert.Equal(GatewayModelClassification.DeclaredTrue, lease.Classification);
        Assert.Equal(GatewayAdmissionKind.Sse, lease.SelectedKind);
        Assert.Equal(source.Length, lease.StoredBytes);
        using StreamReader reader = new(lease.Replay, leaveOpen: true);
        Assert.Equal(body, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(lease.FilePath));
        }

        string path = lease.FilePath;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await lease.SpoolAsync(source, null, 1_048_576, true).ConfigureAwait(false));
        lease.Dispose();
        lease.Dispose();
        Assert.False(File.Exists(path));
        Assert.Throws<ObjectDisposedException>(() => lease.Replay);
    }

    [Fact]
    public async Task OversizeAndContentEncodingOnlyCreateQuarantineBeforeAuthoritativeValidation()
    {
        using GatewayAdmissionMetrics metrics = new();
        using GatewayModelDiscriminator guard = new(metrics, TimeProvider.System);
        using GatewayModelDiscriminatorLease known = guard.TryAcquire(TestContext.Current.CancellationToken).Value;
        using MemoryStream empty = new();
        await known.SpoolAsync(empty, 1_048_577, 1_048_576, true);
        Assert.True(known.Oversized);
        Assert.False(File.Exists(known.FilePath));
        using GatewayModelDiscriminatorLease chunked = guard.TryAcquire(TestContext.Current.CancellationToken).Value;
        using MemoryStream large = new(new byte[1_100_000]);
        await chunked.SpoolAsync(large, null, 1_048_576, true);
        Assert.True(chunked.Oversized);
        Assert.Equal(1_048_577, chunked.StoredBytes);
        Assert.Equal(1_048_577, large.Position);
        using GatewayModelDiscriminatorLease encoded = guard.TryAcquire(TestContext.Current.CancellationToken).Value;
        using MemoryStream valid = new("{\"stream\":true}"u8.ToArray());
        await encoded.SpoolAsync(valid, valid.Length, 1_048_576, false);
        Assert.Equal(GatewayModelClassification.Quarantine, encoded.Classification);
    }

    [Fact]
    public async Task SaturationDeadlineAndGuardBeforeDataCleanupRestoreAllCapacity()
    {
        FakeTimeProvider clock = new();
        using GatewayAdmissionMetrics metrics = new();
        using GatewayModelDiscriminator guard = new(metrics, clock);
        List<GatewayModelDiscriminatorLease> leases = [];
        try
        {
            for (int index = 0; index < 8; index++) { leases.Add(guard.TryAcquire(TestContext.Current.CancellationToken).Value); }
            Assert.Equal("gateway_overloaded", guard.TryAcquire(TestContext.Current.CancellationToken).Error.Code);
            GatewayModelDiscriminatorLease lease = leases[0];
            using MemoryStream source = new("{\"stream\":true}"u8.ToArray());
            await lease.SpoolAsync(source, null, 1_048_576, true);
            bool released = false;
            Assert.True(lease.BindSelected(new GatewayAdmissionLease(GatewayAdmissionKind.Sse, () =>
            {
                Assert.False(File.Exists(lease.FilePath));
                using GatewayModelDiscriminatorLease restored = guard.TryAcquire(CancellationToken.None).Value;
                released = true;
            })));
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.True(lease.CancellationToken.IsCancellationRequested);
            Assert.Throws<OperationCanceledException>(() => lease.CompletePreparation(true));
            lease.RecordRejection("deadline");
            lease.RecordRejection("deadline");
            lease.Dispose();
            Assert.True(released);
            Assert.False(lease.BindSelected(new GatewayAdmissionLease(GatewayAdmissionKind.Sse, () => { })));
        }
        finally { foreach (var lease in leases) { lease.Dispose(); } }
        using GatewayModelDiscriminatorLease reusable = guard.TryAcquire(TestContext.Current.CancellationToken).Value;
    }

    [Fact]
    public async Task InconsistentOrDuplicateSelectedLeasesCannotEnterExecution()
    {
        using GatewayAdmissionMetrics metrics = new();
        using GatewayModelDiscriminator guard = new(metrics, TimeProvider.System);
        using GatewayModelDiscriminatorLease lease = guard.TryAcquire(TestContext.Current.CancellationToken).Value;
        using MemoryStream source = new("{\"stream\":false}"u8.ToArray());
        await lease.SpoolAsync(source, null, 1_048_576, true);
        bool wrongReleased = false;
        Assert.Throws<InvalidOperationException>(() => lease.BindSelected(new GatewayAdmissionLease(GatewayAdmissionKind.Sse, () => wrongReleased = true)));
        Assert.True(wrongReleased);
        GatewayAdmissionLease selected = new(GatewayAdmissionKind.NonStream, () => { });
        Assert.True(lease.BindSelected(selected));
        using GatewayAdmissionLease duplicate = new(GatewayAdmissionKind.NonStream, () => { });
        Assert.Throws<InvalidOperationException>(() => lease.BindSelected(duplicate));
        Assert.Throws<InvalidOperationException>(() => lease.CompletePreparation(true));
        using GatewayAdmissionLease transferred = lease.CompletePreparation(false);
        Assert.Same(selected, transferred);
        Assert.False(File.Exists(lease.FilePath));
        Assert.Throws<InvalidOperationException>(() => lease.CompletePreparation(false));
    }

    [Fact]
    public async Task InvalidLimitsCancellationAndStorageFailuresDoNotLeakGuardPermitsOrDetails()
    {
        using GatewayAdmissionMetrics metrics = new();
        using GatewayModelDiscriminator guard = new(metrics, TimeProvider.System);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => guard.TryAcquire(cancelled.Token));
        using GatewayModelDiscriminatorLease lease = guard.TryAcquire(TestContext.Current.CancellationToken).Value;
        using MemoryStream source = new("{}"u8.ToArray());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await lease.SpoolAsync(source, null, 1, true).ConfigureAwait(false));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await lease.SpoolAsync(source, null, 33_554_433, true).ConfigureAwait(false));
        using BrokenBody broken = new();
        GatewayReplayStorageException exception = await Assert.ThrowsAsync<GatewayReplayStorageException>(async () =>
            await lease.SpoolAsync(broken, null, 1_048_576, true).ConfigureAwait(false));
        Assert.DoesNotContain("private-body", exception.ToString(), StringComparison.Ordinal);
        lease.Dispose();
        Assert.False(File.Exists(lease.FilePath));
        using GatewayModelDiscriminatorLease restored = guard.TryAcquire(TestContext.Current.CancellationToken).Value;
        Assert.Throws<ArgumentOutOfRangeException>(() => metrics.RecordDiscriminatorRejection("high-cardinality"));
    }

    private sealed class BrokenBody : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("private-body"));
    }
}
