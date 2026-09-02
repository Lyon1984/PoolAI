using System.Text.RegularExpressions;

namespace PoolAI.ArchitectureTests;

public sealed class DatabasePermissionCorrectionBoundaryTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public void M4E1CredentialRevisionCorrectionContainsOneColumnReadGrantOnly()
    {
        // Governing contract: Proposed ADR 0016. The correction may add only
        // accounts.credential_revision SELECT to poolai_api; signed SQL stays
        // immutable and no table, function, role, membership, or write surface
        // may be smuggled into this forward migration.
        string migration = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "docs",
            "database",
            "0020_gateway_credential_revision_permission_m4_e1.sql"));

        int grantStart = migration.IndexOf("GRANT SELECT", StringComparison.Ordinal);
        int auditStart = migration.IndexOf("DO $permission_audit$", StringComparison.Ordinal);
        Assert.True(grantStart >= 0, "The exact credential-revision grant is missing.");
        Assert.True(auditStart > grantStart, "The permission audit must follow the grant.");
        AssertSingleGrantAndAuditEnvelope(migration, grantStart, auditStart);
        Assert.Empty(FindForbiddenStatements(migration));
        Assert.Contains(
            "relation.relacl",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "privilege.grantee IN (0, v_api_role_oid)",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "LEFT JOIN pg_catalog.pg_roles AS role",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "pg_catalog.has_any_column_privilege(\n            'poolai_api', 'public.accounts', 'SELECT WITH GRANT OPTION')",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "poolai_m4_e1_credential_revision_grantee_drifted",
            migration,
            StringComparison.Ordinal);
    }

    private static void AssertSingleGrantAndAuditEnvelope(
        string migration,
        int grantStart,
        int auditStart)
    {
        Assert.Equal(
            "GRANT SELECT (credential_revision) ON public.accounts TO poolai_api;",
            Regex.Replace(
                StripSqlComments(migration[..auditStart]),
                @"\s+",
                " ",
                RegexOptions.CultureInvariant,
                RegexTimeout).Trim());
        int auditEnd = migration.IndexOf(
            "$permission_audit$;",
            auditStart,
            StringComparison.Ordinal);
        Assert.True(auditEnd > auditStart, "The permission audit terminator is missing.");
        auditEnd += "$permission_audit$;".Length;
        Assert.True(
            string.IsNullOrWhiteSpace(StripSqlComments(migration[auditEnd..])),
            "No statement may follow the permission audit.");
        int grantEnd = migration.IndexOf(';', grantStart) + 1;
        Assert.Equal(
            "GRANT SELECT (credential_revision) ON public.accounts TO poolai_api;",
            Regex.Replace(
                migration[grantStart..grantEnd],
                @"\s+",
                " ",
                RegexOptions.CultureInvariant,
                RegexTimeout).Trim());

        Assert.Equal(
            1,
            Regex.Count(
                migration,
                @"(?im)^\s*GRANT\s+",
                RegexOptions.CultureInvariant,
                RegexTimeout));
        Assert.Equal(
            1,
            Regex.Count(
                migration,
                @"(?im)^\s*DO\s+\$permission_audit\$",
                RegexOptions.CultureInvariant,
                RegexTimeout));
    }

    [Fact]
    public void M4E1CredentialRevisionDecisionRemainsProposedAndNarrow()
    {
        string root = RepositoryRoot.Find();
        string decision = File.ReadAllText(Path.Combine(
            root,
            "docs",
            "architecture",
            "adr",
            "0016-add-gateway-credential-revision-column-read.md"));
        string manifest = File.ReadAllText(Path.Combine(
            root,
            "docs",
            "release-manifest-v1.json"));

        Assert.Contains("- Status: **Proposed**", decision, StringComparison.Ordinal);
        Assert.Contains(
            "Amends: only ADR 0015's statement",
            decision,
            StringComparison.Ordinal);
        Assert.Contains(
            "GRANT SELECT (credential_revision)",
            decision,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"minimum_compatible_version\": 20",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"maximum_compatible_version\": 20",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "0020_gateway_credential_revision_permission_m4_e1.sql",
            manifest,
            StringComparison.Ordinal);
    }

    private static string[] FindForbiddenStatements(string migration)
    {
        const string pattern =
            @"(?im)^\s*(?:"
            + @"(?:CREATE|ALTER|DROP|COMMENT|SECURITY\s+LABEL)\b"
            + @"|REVOKE\b"
            + @"|(?:INSERT|UPDATE|DELETE|TRUNCATE|MERGE|CALL|SET\s+ROLE|RESET\s+ROLE)\b"
            + @"|GRANT\s+(?:INSERT|UPDATE|DELETE|TRUNCATE|REFERENCES|TRIGGER|EXECUTE|CREATE|USAGE)\b"
            + @"|GRANT\s+[a-z_][a-z0-9_]*\s+TO\b"
            + @")";
        return Regex.Matches(
                migration,
                pattern,
                RegexOptions.CultureInvariant,
                RegexTimeout)
            .Select(static match => match.Value.Trim())
            .ToArray();
    }

    private static string StripSqlComments(string sql) => Regex.Replace(
        sql,
        @"--[^\r\n]*|/\*.*?\*/",
        " ",
        RegexOptions.CultureInvariant | RegexOptions.Singleline,
        RegexTimeout);
}
