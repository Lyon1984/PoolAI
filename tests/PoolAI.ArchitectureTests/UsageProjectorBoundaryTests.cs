using System.Xml.Linq;

namespace PoolAI.ArchitectureTests;

public sealed class UsageProjectorBoundaryTests
{
    [Fact]
    public void UsageDependsOnlyOnPublishedCrossModuleAbstractions()
    {
        string root = RepositoryRoot.Find();
        string projectPath = Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Usage",
            "PoolAI.Modules.Usage.csproj");
        XDocument project = XDocument.Load(projectPath, LoadOptions.None);
        string[] moduleReferences = project
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
            .Where(static value => value.Contains("PoolAI.Modules.", StringComparison.Ordinal))
            .Select(static value => Path.GetFileNameWithoutExtension(value)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "PoolAI.Modules.GroupQuota.Abstractions",
                "PoolAI.Modules.Operations.Abstractions",
                "PoolAI.Modules.Usage.Abstractions",
            ],
            moduleReferences);
    }

    [Fact]
    public void UsageNeverReadsGroupQuotaOrOutboxTablesDirectly()
    {
        string usageRoot = Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Modules",
            "PoolAI.Modules.Usage");
        string objectPath = Path.DirectorySeparatorChar
            + "obj"
            + Path.DirectorySeparatorChar;
        string source = string.Join(
            Environment.NewLine,
            Directory.GetFiles(usageRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(objectPath, StringComparison.Ordinal))
                .Select(File.ReadAllText));

        foreach (string forbiddenTable in new[]
        {
            "usage_attempts",
            "usage_attempt_adjustments",
            "group_quota_events",
            "outbox_messages",
        })
        {
            Assert.DoesNotContain(forbiddenTable, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GroupQuotaFactPortsAreNarrowAndImplementedOnlyByInfrastructure()
    {
        string root = RepositoryRoot.Find();
        string abstractions = Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.GroupQuota.Abstractions");
        foreach (string portName in new[]
        {
            "IGroupQuotaEventFactReader.cs",
            "IAttemptSettlementHourFactReader.cs",
            "IAttemptSettlementFactExistenceReader.cs",
        })
        {
            string source = File.ReadAllText(Path.Combine(abstractions, portName));
            Assert.Contains("IUnitOfWorkContext unitOfWorkContext", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
            Assert.DoesNotContain("EntityFrameworkCore", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IQueryable", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Repository", source, StringComparison.Ordinal);
        }

        AssertInfrastructureAdapter(root, "PostgresGroupQuotaEventFactReader.cs");
        AssertInfrastructureAdapter(root, "PostgresAttemptSettlementHourFactReader.cs");
        AssertInfrastructureAdapter(root, "PostgresAttemptSettlementFactExistenceReader.cs");
    }

    [Fact]
    public void ConsumerUsesLogicalLedgerPositionAndVerifiesFactBeforeProjection()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Modules",
            "PoolAI.Modules.Usage",
            "Application",
            "GroupQuotaUsageProjectorConsumer.cs"));
        int ledgerRead = source.IndexOf("VerifyEventFactAsync(\n            envelope", StringComparison.Ordinal);
        int projection = source.IndexOf(
            "ProjectIfRequiredAsync(\n            envelope.Payload",
            StringComparison.Ordinal);

        Assert.True(ledgerRead >= 0);
        Assert.True(projection > ledgerRead);
        Assert.Contains("envelope.SourceEventSequence", source, StringComparison.Ordinal);
        Assert.Contains("envelope.EventSequence", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
        Assert.Contains("IInboxReplayPredecessorVerifier", source, StringComparison.Ordinal);
        Assert.Contains("quota_event_fact_mismatch", source, StringComparison.Ordinal);
    }

    private static void AssertInfrastructureAdapter(string root, string fileName)
    {
        string path = Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.GroupQuota",
            "Infrastructure",
            "Persistence",
            fileName);
        string source = File.ReadAllText(path);
        Assert.Contains(
            "namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;",
            source,
            StringComparison.Ordinal);
    }
}
