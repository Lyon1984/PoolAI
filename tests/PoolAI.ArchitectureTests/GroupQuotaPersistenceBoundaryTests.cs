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

    [Fact]
    public void FutureQuotaManagementEntryPointsPreserveQuotaBeforeGroupLockOrder()
    {
        // Governing contracts: ADR 0006 and docs/database/README.md section 10.
        // Archive already takes Quota -> Group, so future reset/adjust wrappers
        // must keep the same order instead of recreating the retired inversion.
        string databaseContract = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "docs",
            "database",
            "README.md"));
        string migrationSevenContract = Slice(
            databaseContract,
            "0007 撤销 0003 曾授予",
            "## 11. 最低数据库验收");

        Assert.Contains(
            "保持 Quota → Group 锁序",
            migrationSevenContract,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "保持 Group→Quota 锁序",
            migrationSevenContract,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "保持 Group → Quota 锁序",
            migrationSevenContract,
            StringComparison.Ordinal);
    }

    [Fact]
    public void M4E1RuntimePolicyMigrationKeepsTheExistingGroupStorageBoundary()
    {
        // Governing contract: docs/database/README.md section 2 and ADR 0015.
        // M4-E1 canonicalizes the existing Group-owned jsonb value and adds
        // narrow v2 functions; it must not grow a parallel policy store.
        string migration = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "docs",
            "database",
            "0019_group_runtime_policy_m4_e1.sql"));

        Assert.Contains(
            "'{\"schema_version\":1,\"requests_per_minute\":6000}'::jsonb",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE OR REPLACE FUNCTION public.poolai_group_create_v2(",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE OR REPLACE FUNCTION public.poolai_group_update_v2(",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "'requests_per_minute', v_requests_per_minute",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "version = target.version + 1",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "LOCK TABLE public.groups IN ACCESS EXCLUSIVE MODE;",
            migration,
            StringComparison.Ordinal);
        Assert.True(
            migration.IndexOf(
                "LOCK TABLE public.groups IN ACCESS EXCLUSIVE MODE;",
                StringComparison.Ordinal)
            < migration.IndexOf("DO $runtime_policy_preflight$", StringComparison.Ordinal),
            "The schema-18 policy preflight must run under the migration's table lock.");
        Assert.DoesNotContain("ADD COLUMN", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE INDEX", migration, StringComparison.OrdinalIgnoreCase);
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
