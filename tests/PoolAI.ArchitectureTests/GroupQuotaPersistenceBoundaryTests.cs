namespace PoolAI.ArchitectureTests;

public sealed class GroupQuotaPersistenceBoundaryTests
{
    [Fact]
    public void ActivationPreflightStaysUnlockedAndFinalFunctionOwnsTheGroupFence()
    {
        // Governing contract: design-pattern-baseline sections 2.1 and 2.2.
        // poolai_api has no direct Group UPDATE privilege; the preflight is an
        // early projection, while the SECURITY DEFINER mutation owns CAS/locks.
        string root = RepositoryRoot.Find();
        string repository = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.GroupQuota",
            "Infrastructure",
            "Persistence",
            "PostgresGroupRepository.cs"));
        string preflight = Slice(
            repository,
            "private static readonly string GetForActivationSql",
            "private static readonly string ListFirstSql");

        Assert.Contains("WHERE g.id = $1;", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain("FOR UPDATE", preflight, StringComparison.OrdinalIgnoreCase);

        string migration = File.ReadAllText(Path.Combine(
            root,
            "docs",
            "database",
            "0007_group_subscription_m1_e4.sql"));
        string groupMutation = Slice(
            migration,
            "CREATE OR REPLACE FUNCTION public.poolai_group_update(",
            "CREATE OR REPLACE FUNCTION public.poolai_subscription_template_create(");
        Assert.Contains("FOR UPDATE", groupMutation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("v_group.version <> p_expected_version", groupMutation, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}
