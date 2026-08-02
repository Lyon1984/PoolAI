namespace PoolAI.ArchitectureTests;

public sealed class OperationsOutboxReplayBoundaryTests
{
    [Fact]
    public void AdminReplayIsApiOnlyAndCallsOnlyTheSignedFunction()
    {
        string root = RepositoryRoot.Find();
        string apiProgram = Read(root, "src", "PoolAI.Api", "Program.cs");
        string workerProgram = Read(root, "src", "PoolAI.Worker", "Program.cs");
        string repository = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "Infrastructure",
            "Persistence",
            "PostgresOutboxReplayRepository.cs");
        string service = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "Application",
            "OutboxReplayService.cs");
        string deliveryContract = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations.Abstractions",
            "IOutboxDeliveryStore.cs");
        string deliveryStore = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "Infrastructure",
            "Persistence",
            "PostgresOutboxDeliveryStore.cs");

        Assert.Equal(1, Count(apiProgram, ".AddOperationsAdminControlPlane("));
        Assert.Equal(1, Count(apiProgram, ".MapOutboxReplayEndpoints("));
        Assert.DoesNotContain(
            "AddOperationsAdminControlPlane",
            workerProgram,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MapOutboxReplayEndpoints",
            workerProgram,
            StringComparison.Ordinal);

        Assert.Equal(
            1,
            Count(repository, "public.poolai_operations_replay_dead_outbox($1, $2, $3)"));
        Assert.DoesNotContain("outbox_messages", repository, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", repository, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IOutboxDeliveryStore", service, StringComparison.Ordinal);
        Assert.DoesNotContain("PostgresOutboxDeliveryStore", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplayDeadAsync", deliveryContract, StringComparison.Ordinal);
        Assert.DoesNotContain("OutboxReplay", deliveryContract, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplayDeadAsync", deliveryStore, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "INSERT INTO public.outbox_messages",
            deliveryStore,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplayApplicationLayerHasNoTransportOrPersistenceDependency()
    {
        string applicationRoot = Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "Application");
        string[] sources = Directory.GetFiles(
            applicationRoot,
            "*.cs",
            SearchOption.AllDirectories);

        Assert.NotEmpty(sources);
        foreach (string path in sources)
        {
            string source = File.ReadAllText(path);
            Assert.DoesNotContain("Microsoft.AspNetCore", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "PoolAI.Infrastructure.Postgres",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain("StackExchange.Redis", source, StringComparison.Ordinal);
        }
    }

    private static string Read(string root, params string[] path) =>
        File.ReadAllText(Path.Combine([root, .. path]));

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
