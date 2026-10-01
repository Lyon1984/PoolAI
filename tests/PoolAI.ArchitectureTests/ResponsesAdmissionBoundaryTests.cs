namespace PoolAI.ArchitectureTests;

public sealed class ResponsesAdmissionBoundaryTests
{
    [Fact]
    public void ResponsesEndpointOwnsOnlyHttpProjectionAndCallsTheFencedApplicationBoundary()
    {
        string root = RepositoryRoot.Find();
        string endpoint = File.ReadAllText(Path.Combine(root, "src", "PoolAI.Api", "ResponsesEndpoint.cs"));
        Assert.DoesNotContain("DbContext", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Redis", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("GatewaySingleAttemptProcessManager", endpoint, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(guard.CancellationToken)", endpoint, StringComparison.Ordinal);
        Assert.Contains("guard.CompletePreparation(prepared.Value.Stream)", endpoint, StringComparison.Ordinal);
        Assert.Contains("process.ExecuteInitialAttemptAsync", endpoint, StringComparison.Ordinal);
        string output = File.ReadAllText(Path.Combine(root, "src", "Modules", "PoolAI.Modules.Gateway", "GatewayAttemptOutput.cs"));
        Assert.True(output.IndexOf("MarkBusinessOutputStarted", StringComparison.Ordinal)
            < output.IndexOf("_output.WriteEventAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void ModeGuardKeepsFixedResourcesAndDoesNotOwnBusinessInfrastructure()
    {
        string root = Path.Combine(RepositoryRoot.Find(), "src", "Modules", "PoolAI.Modules.Gateway");
        string guard = File.ReadAllText(Path.Combine(root, "GatewayModelDiscriminator.cs"));
        string lease = File.ReadAllText(Path.Combine(root, "GatewayModelDiscriminatorLease.cs"));
        string scanner = File.ReadAllText(Path.Combine(root, "GatewayStreamDiscriminator.cs"));
        Assert.Contains("PermitLimit = 8", guard, StringComparison.Ordinal);
        Assert.Contains("_permits.Wait(0", guard, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(30)", guard, StringComparison.Ordinal);
        Assert.Contains("FileOptions.DeleteOnClose", lease, StringComparison.Ordinal);
        Assert.Contains("maximumBytes + 1 - stored", lease, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonDocument", scanner, StringComparison.Ordinal);
        foreach (string source in new[] { guard, lease, scanner })
        {
            Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Redis", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpContext", source, StringComparison.Ordinal);
        }
    }
}
