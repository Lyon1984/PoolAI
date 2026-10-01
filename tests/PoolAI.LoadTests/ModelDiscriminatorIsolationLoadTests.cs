using System.Text;
using PoolAI.Modules.Gateway.Application;

namespace PoolAI.LoadTests;

// ADR 0017's bounded pre-admission guard never borrows a business partition.
public sealed class ModelDiscriminatorIsolationLoadTests
{
    [Fact]
    public async Task EightCompleteSpoolsStayBoundedAndBusinessPartitionsRemainIndependentUnderPressure()
    {
        using GatewayAdmissionMetrics metrics = new();
        using GatewayModelDiscriminator discriminator = new(metrics, TimeProvider.System);
        using GatewayAdmissionController admission = new(new GatewayAdmissionOptions());
        List<GatewayModelDiscriminatorLease> guards = [];
        byte[] body = Encoding.UTF8.GetBytes("{\"input\":\"" + new string('x', 1_000_000) + "\",\"stream\":true}");
        try
        {
            for (int index = 0; index < 8; index++)
            {
                GatewayModelDiscriminatorLease lease = discriminator.TryAcquire(TestContext.Current.CancellationToken).Value;
                guards.Add(lease);
                using MemoryStream source = new(body, writable: false);
                await lease.SpoolAsync(source, null, 1_048_576, true);
                Assert.Equal(GatewayModelClassification.DeclaredTrue, lease.Classification);
                Assert.Equal(body.Length, lease.Replay.Length);
            }
            await Parallel.ForEachAsync(Enumerable.Range(0, 1000), TestContext.Current.CancellationToken,
                async (_, token) =>
                {
                    Assert.Equal("gateway_overloaded", discriminator.TryAcquire(token).Error.Code);
                    foreach (GatewayAdmissionKind kind in Enum.GetValues<GatewayAdmissionKind>())
                    {
                        using GatewayAdmissionLease independent = (await admission.AcquireAsync(kind, token).ConfigureAwait(false)).Value;
                    }
                });
            Assert.Equal(8, guards.Select(lease => lease.Replay).Distinct().Count());
        }
        finally { foreach (var guard in guards) { guard.Dispose(); } }
        using GatewayModelDiscriminatorLease restored = discriminator.TryAcquire(TestContext.Current.CancellationToken).Value;
    }
}
