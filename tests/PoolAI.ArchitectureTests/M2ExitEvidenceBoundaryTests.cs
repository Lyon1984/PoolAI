namespace PoolAI.ArchitectureTests;

public sealed class M2ExitEvidenceBoundaryTests
{
    [Fact]
    public void M2ExitJourneyCannotReintroduceDirectBusinessTableWrites()
    {
        string repositoryRoot = FindRepositoryRoot();
        string journey = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tests",
            "PoolAI.EndToEndTests",
            "M2ExitPublicApiEndToEndTests.cs"));
        string environment = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tests",
            "PoolAI.EndToEndTests",
            "PasswordResetHttpEndToEndEnvironment.cs"));

        foreach (string forbidden in new[]
                 {
                     "INSERT INTO public.",
                     "UPDATE public.",
                     "DELETE FROM public.",
                     "MERGE INTO public.",
                 })
        {
            Assert.DoesNotContain(forbidden, journey, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(
            "M1ExitPostgresSupplyReadiness",
            environment,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RemoveAll<IGroupSupplyReadiness>",
            environment,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "useM1ExitSupplyReadiness",
            environment,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PoolAI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The PoolAI repository root was not found.");
    }
}
