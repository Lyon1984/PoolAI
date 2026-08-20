using System.Collections.Frozen;
using System.Text;
using Npgsql;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresCumulativeTokenQuotaCatalogTests(
    PostgresRuntimeFixture fixture)
{
    private const string GroupOwnershipConstraintsSql = """
        SELECT namespace.nspname,
               relation.relname,
               constraint_record.conname,
               constraint_record.contype::text,
               pg_catalog.array_to_string(
                   ARRAY(
                       SELECT attribute.attname
                       FROM pg_catalog.unnest(constraint_record.conkey)
                           WITH ORDINALITY AS constrained(attnum, position)
                       JOIN pg_catalog.pg_attribute AS attribute
                         ON attribute.attrelid = constraint_record.conrelid
                        AND attribute.attnum = constrained.attnum
                       ORDER BY constrained.position
                   ),
                   ','),
               referenced_namespace.nspname,
               referenced_relation.relname,
               CASE WHEN constraint_record.contype = 'f' THEN
                   pg_catalog.array_to_string(
                       ARRAY(
                           SELECT attribute.attname
                           FROM pg_catalog.unnest(constraint_record.confkey)
                               WITH ORDINALITY AS referenced(attnum, position)
                           JOIN pg_catalog.pg_attribute AS attribute
                             ON attribute.attrelid = constraint_record.confrelid
                            AND attribute.attnum = referenced.attnum
                           ORDER BY referenced.position
                       ),
                       ',')
               END,
               CASE WHEN constraint_record.contype = 'f'
                   THEN constraint_record.confdeltype::text
               END,
               CASE WHEN constraint_record.contype = 'f'
                   THEN constraint_record.confupdtype::text
               END,
               constraint_record.condeferrable,
               constraint_record.condeferred,
               constraint_record.convalidated
        FROM pg_catalog.pg_constraint AS constraint_record
        JOIN pg_catalog.pg_class AS relation
          ON relation.oid = constraint_record.conrelid
        JOIN pg_catalog.pg_namespace AS namespace
          ON namespace.oid = relation.relnamespace
        LEFT JOIN pg_catalog.pg_class AS referenced_relation
          ON referenced_relation.oid = constraint_record.confrelid
        LEFT JOIN pg_catalog.pg_namespace AS referenced_namespace
          ON referenced_namespace.oid = referenced_relation.relnamespace
        WHERE namespace.nspname = 'public'
          AND relation.relname IN ('group_token_quotas', 'group_quota_periods')
          AND constraint_record.contype IN ('p', 'u', 'f')
        ORDER BY relation.relname, constraint_record.conname;
        """;

    private const string GroupOwnershipUniqueIndexesSql = """
        SELECT namespace.nspname,
               relation.relname,
               index_relation.relname,
               index_record.indisprimary,
               pg_catalog.array_to_string(
                   ARRAY(
                       SELECT pg_catalog.pg_get_indexdef(
                           index_record.indexrelid,
                           key_position,
                           true)
                       FROM pg_catalog.generate_series(
                           1,
                           index_record.indnkeyatts::integer) AS key_position
                   ),
                   ','),
               pg_catalog.pg_get_expr(
                   index_record.indpred,
                   index_record.indrelid,
                   true),
               index_record.indisvalid,
               index_record.indisready,
               index_record.indislive
        FROM pg_catalog.pg_index AS index_record
        JOIN pg_catalog.pg_class AS relation
          ON relation.oid = index_record.indrelid
        JOIN pg_catalog.pg_namespace AS namespace
          ON namespace.oid = relation.relnamespace
        JOIN pg_catalog.pg_class AS index_relation
          ON index_relation.oid = index_record.indexrelid
        WHERE namespace.nspname = 'public'
          AND relation.relname IN ('group_token_quotas', 'group_quota_periods')
          AND index_record.indisunique
        ORDER BY relation.relname, index_relation.relname;
        """;

    private static readonly FrozenSet<string> CumulativeCounterWords = new[]
    {
        "allocated",
        "allocation",
        "allowance",
        "allowances",
        "annual",
        "available",
        "balance",
        "balances",
        "budget",
        "budgets",
        "cap",
        "caps",
        "capacity",
        "ceiling",
        "ceilings",
        "consumed",
        "consumption",
        "cumulative",
        "lifetime",
        "limit",
        "limits",
        "max",
        "maximum",
        "month",
        "monthly",
        "overage",
        "remaining",
        "reserved",
        "spent",
        "total",
        "totals",
        "used",
        "yearly",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<CatalogIdentity> GroupCumulativeQuotaColumns = new[]
    {
        Identity("group_quota_periods", "total_tokens"),
        Identity("group_quota_periods", "consumed_tokens"),
        Identity("group_quota_periods", "reserved_tokens"),
        Identity("group_quota_events", "delta_total_tokens"),
        Identity("group_quota_events", "delta_consumed_tokens"),
        Identity("group_quota_events", "delta_reserved_tokens"),
        Identity("group_quota_events", "total_tokens_after"),
        Identity("group_quota_events", "consumed_tokens_after"),
        Identity("group_quota_events", "reserved_tokens_after"),
    }.ToFrozenSet();

    private static readonly FrozenSet<CatalogIdentity> UsageStatisticColumns = new[]
    {
        Identity("usage_attempts", "total_tokens", isNotNull: false),
        Identity("usage_attempt_adjustments", "previous_total_tokens"),
        Identity(
            "usage_attempt_adjustments",
            "corrected_total_tokens",
            isNotNull: false),
        Identity("group_usage_hourly", "total_tokens"),
        Identity("account_usage_hourly", "total_tokens"),
    }.ToFrozenSet();

    private static readonly FrozenSet<CatalogIdentity> ReviewedNumeric78Columns = new[]
    {
        Identity("account_usage_hourly", "input_tokens"),
        Identity("account_usage_hourly", "output_tokens"),
        Identity("account_usage_hourly", "cache_creation_tokens"),
        Identity("account_usage_hourly", "cache_read_tokens"),
        Identity("account_usage_hourly", "thinking_tokens"),
        Identity("account_usage_hourly", "total_tokens"),
        Identity("group_quota_events", "delta_total_tokens"),
        Identity("group_quota_events", "delta_consumed_tokens"),
        Identity("group_quota_events", "delta_reserved_tokens"),
        Identity("group_quota_events", "total_tokens_after"),
        Identity("group_quota_events", "consumed_tokens_after"),
        Identity("group_quota_events", "reserved_tokens_after"),
        Identity("group_quota_periods", "total_tokens"),
        Identity("group_quota_periods", "consumed_tokens"),
        Identity("group_quota_periods", "reserved_tokens"),
        Identity("group_token_reservations", "estimated_tokens"),
        Identity("group_token_reservations", "actual_tokens", isNotNull: false),
        Identity(
            "group_token_reservations",
            "estimated_input_tokens",
            isNotNull: false),
        Identity(
            "group_token_reservations",
            "estimated_output_tokens",
            isNotNull: false),
        Identity("group_usage_hourly", "input_tokens"),
        Identity("group_usage_hourly", "output_tokens"),
        Identity("group_usage_hourly", "cache_creation_tokens"),
        Identity("group_usage_hourly", "cache_read_tokens"),
        Identity("group_usage_hourly", "thinking_tokens"),
        Identity("group_usage_hourly", "total_tokens"),
        Identity("usage_attempt_adjustments", "previous_total_tokens"),
        Identity("usage_attempt_adjustments", "corrected_input_tokens"),
        Identity("usage_attempt_adjustments", "corrected_output_tokens"),
        Identity(
            "usage_attempt_adjustments",
            "corrected_total_tokens",
            isNotNull: false),
        Identity("usage_attempt_adjustments", "corrected_cache_read_tokens"),
        Identity("usage_attempt_adjustments", "corrected_cache_creation_tokens"),
        Identity("usage_attempt_adjustments", "corrected_thinking_tokens"),
        Identity("usage_attempt_adjustments", "delta_tokens", isNotNull: false),
        Identity("usage_attempts", "input_tokens"),
        Identity("usage_attempts", "output_tokens"),
        Identity("usage_attempts", "total_tokens", isNotNull: false),
        Identity("usage_attempts", "cache_read_tokens"),
        Identity("usage_attempts", "cache_creation_tokens"),
        Identity("usage_attempts", "thinking_tokens"),
    }.ToFrozenSet();

    private static readonly FrozenSet<CatalogIdentity> ReviewedPersonalJsonColumns = new[]
    {
        JsonIdentity("users", "totp_secret_envelope", isNotNull: false),
        JsonIdentity("refresh_sessions", "metadata"),
        JsonIdentity("one_time_tokens", "secret_envelope", isNotNull: false),
        JsonIdentity(
            "one_time_tokens",
            "response_body_envelope",
            isNotNull: false),
        JsonIdentity("email_outbox", "recipient_envelope", isNotNull: false),
        JsonIdentity("email_outbox", "template_payload"),
        JsonIdentity(
            "email_outbox",
            "delivery_secret_envelope",
            isNotNull: false),
        JsonIdentity("accounts", "credential_envelope"),
        JsonIdentity("accounts", "settings"),
        JsonIdentity("api_keys", "ip_acl"),
        JsonIdentity("usage_requests", "metadata"),
        JsonIdentity("group_quota_events", "metadata"),
        JsonIdentity("usage_attempts", "raw_upstream_usage", isNotNull: false),
        JsonIdentity("audit_logs", "before_state", isNotNull: false),
        JsonIdentity("audit_logs", "after_state", isNotNull: false),
        JsonIdentity("audit_logs", "metadata"),
    }.ToFrozenSet();

    private static readonly FrozenSet<CatalogConstraint> GroupOwnershipConstraints = new[]
    {
        CatalogConstraint.PrimaryKey(
            "group_token_quotas",
            "group_token_quotas_pkey",
            "group_id"),
        CatalogConstraint.ForeignKey(
            "group_token_quotas",
            "fk_group_token_quotas_group",
            "group_id",
            "groups",
            "id"),
        CatalogConstraint.ForeignKey(
            "group_token_quotas",
            "fk_group_token_quotas_current_period",
            "current_period_id,group_id",
            "group_quota_periods",
            "id,group_id",
            isDeferrable: true,
            isInitiallyDeferred: true),
        CatalogConstraint.PrimaryKey(
            "group_quota_periods",
            "group_quota_periods_pkey",
            "id"),
        CatalogConstraint.Unique(
            "group_quota_periods",
            "uq_group_quota_periods_group_number",
            "group_id,period_number"),
        CatalogConstraint.Unique(
            "group_quota_periods",
            "uq_group_quota_periods_id_group",
            "id,group_id"),
        CatalogConstraint.ForeignKey(
            "group_quota_periods",
            "fk_group_quota_periods_quota",
            "group_id",
            "group_token_quotas",
            "group_id"),
    }.ToFrozenSet();

    private static readonly FrozenSet<CatalogUniqueIndex> GroupOwnershipUniqueIndexes =
        new[]
        {
            CatalogUniqueIndex.Create(
                "group_token_quotas",
                "group_token_quotas_pkey",
                "group_id",
                isPrimary: true),
            CatalogUniqueIndex.Create(
                "group_quota_periods",
                "group_quota_periods_pkey",
                "id",
                isPrimary: true),
            CatalogUniqueIndex.Create(
                "group_quota_periods",
                "uq_group_quota_periods_group_number",
                "group_id,period_number"),
            CatalogUniqueIndex.Create(
                "group_quota_periods",
                "uq_group_quota_periods_id_group",
                "id,group_id"),
            CatalogUniqueIndex.Create(
                "group_quota_periods",
                "uq_group_quota_periods_one_current",
                "group_id",
                predicate: "status = 'current'::text"),
        }.ToFrozenSet();

    private static readonly FrozenSet<string> PersonalSubjectWords = new[]
    {
        "account",
        "accounts",
        "customer",
        "customers",
        "key",
        "keys",
        "member",
        "members",
        "profile",
        "profiles",
        "subscription",
        "subscriptions",
        "user",
        "users",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> EavKeyTerminalWords = new[]
    {
        "code",
        "key",
        "name",
        "path",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> EavValueTerminalWords = new[]
    {
        "amount",
        "number",
        "quantity",
        "value",
        "values",
    }.ToFrozenSet(StringComparer.Ordinal);

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PostgreSql18FinalCatalogOnlyAllowsGroupCumulativeTokenQuota()
    {
        // Governing contracts: DEC-009, D-001/D-002, ADR 0014, and the database
        // contract require PostgreSQL to keep cumulative quota Group-owned while
        // preserving explicitly reviewed immutable and rebuildable usage facts.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await AssertPostgreSql18Async(cancellationToken).ConfigureAwait(true);
        IReadOnlyList<CatalogColumn> columns = await ReadCatalogColumnsAsync(cancellationToken)
            .ConfigureAwait(true);
        IReadOnlyList<CatalogConstraint> constraints = await ReadGroupOwnershipConstraintsAsync(
            cancellationToken).ConfigureAwait(true);
        IReadOnlyList<CatalogUniqueIndex> uniqueIndexes =
            await ReadGroupOwnershipUniqueIndexesAsync(cancellationToken).ConfigureAwait(true);

        HashSet<CatalogIdentity> catalogIdentities = columns
            .Select(static column => column.Identity)
            .ToHashSet();
        HashSet<CatalogIdentity> numeric78Identities = columns
            .Where(static column => string.Equals(
                column.StoreType,
                "numeric(78,0)",
                StringComparison.Ordinal))
            .Select(static column => column.Identity)
            .ToHashSet();
        AssertCatalogMatchesAllowlist(
            ReviewedNumeric78Columns,
            numeric78Identities,
            "numeric(78,0) Token");
        AssertAllowlistIsCurrent(GroupCumulativeQuotaColumns, catalogIdentities, "Group quota");
        AssertAllowlistIsCurrent(UsageStatisticColumns, catalogIdentities, "usage statistic");

        CatalogColumn[] violations = columns
            .Where(IsCumulativeTokenQuotaColumn)
            .Where(column => !GroupCumulativeQuotaColumns.Contains(column.Identity))
            .Where(column => !UsageStatisticColumns.Contains(column.Identity))
            .OrderBy(static column => column.SchemaName, StringComparer.Ordinal)
            .ThenBy(static column => column.RelationName, StringComparer.Ordinal)
            .ThenBy(static column => column.ColumnName, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "DEC-009 forbids non-reviewed cumulative Token quota columns outside the " +
            "exact Group authority and usage-statistic catalog allowlists: " +
            string.Join(", ", violations.Select(static column => column.Identity.DisplayName)));

        AssertCanonicalGroupQuotaCounters(columns);
        AssertGroupQuotaRelationsHaveNoPersonalDimension(columns);
        AssertExactCatalog(
            GroupOwnershipConstraints,
            constraints,
            "Group quota PK/FK/UNIQUE constraints");
        AssertExactCatalog(
            GroupOwnershipUniqueIndexes,
            uniqueIndexes,
            "Group quota unique indexes");
        AssertReviewedPersonalJsonAndEavSurfaces(columns);
    }

    private async Task AssertPostgreSql18Async(CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT pg_catalog.current_setting('server_version_num')::integer / 10000;
            """);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(true);
        Assert.Equal(18, Assert.IsType<int>(result));
    }

    private async Task<IReadOnlyList<CatalogColumn>> ReadCatalogColumnsAsync(
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT namespace.nspname,
                   relation.relname,
                   relation.relkind::text,
                   attribute.attname,
                   pg_catalog.format_type(attribute.atttypid, attribute.atttypmod),
                   attribute.attnotnull
            FROM pg_catalog.pg_namespace AS namespace
            JOIN pg_catalog.pg_class AS relation
              ON relation.relnamespace = namespace.oid
            JOIN pg_catalog.pg_attribute AS attribute
              ON attribute.attrelid = relation.oid
            WHERE relation.relkind IN ('r', 'p', 'v', 'm', 'f', 'c')
              AND attribute.attnum > 0
              AND NOT attribute.attisdropped
              AND namespace.nspname <> 'information_schema'
              AND pg_catalog.substr(namespace.nspname, 1, 3) <> 'pg_'
            ORDER BY namespace.nspname,
                     relation.relname,
                     relation.relkind,
                     attribute.attnum;
            """);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        List<CatalogColumn> columns = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(true))
        {
            columns.Add(new CatalogColumn(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5)));
        }

        return columns;
    }

    private async Task<IReadOnlyList<CatalogConstraint>>
        ReadGroupOwnershipConstraintsAsync(CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand(
            GroupOwnershipConstraintsSql);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        List<CatalogConstraint> constraints = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(true))
        {
            constraints.Add(new CatalogConstraint(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetBoolean(10),
                reader.GetBoolean(11),
                reader.GetBoolean(12)));
        }

        return constraints;
    }

    private async Task<IReadOnlyList<CatalogUniqueIndex>>
        ReadGroupOwnershipUniqueIndexesAsync(CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand(
            GroupOwnershipUniqueIndexesSql);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(true);
        List<CatalogUniqueIndex> indexes = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(true))
        {
            indexes.Add(new CatalogUniqueIndex(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8)));
        }

        return indexes;
    }

    private static void AssertCanonicalGroupQuotaCounters(
        IReadOnlyList<CatalogColumn> columns)
    {
        CatalogColumn[] counters = columns
            .Where(static column =>
                string.Equals(column.SchemaName, "public", StringComparison.Ordinal)
                && string.Equals(
                    column.RelationName,
                    "group_quota_periods",
                    StringComparison.Ordinal)
                && column.ColumnName is "total_tokens" or "consumed_tokens" or "reserved_tokens")
            .OrderBy(static column => column.ColumnName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["consumed_tokens", "reserved_tokens", "total_tokens"],
            counters.Select(static column => column.ColumnName));
        Assert.All(counters, static counter =>
        {
            Assert.Equal("r", counter.RelationKind);
            Assert.Equal("numeric(78,0)", counter.StoreType);
            Assert.True(counter.IsNotNull);
        });
    }

    private static void AssertGroupQuotaRelationsHaveNoPersonalDimension(
        IReadOnlyList<CatalogColumn> columns)
    {
        string[] personalDimensions =
        [
            "account_id",
            "api_key_id",
            "subscription_id",
            "user_id",
        ];
        foreach (string relation in new[] { "group_token_quotas", "group_quota_periods" })
        {
            CatalogColumn[] relationColumns = columns
                .Where(column =>
                    string.Equals(column.SchemaName, "public", StringComparison.Ordinal)
                    && string.Equals(
                        column.RelationName,
                        relation,
                        StringComparison.Ordinal))
                .ToArray();
            Assert.Contains(
                relationColumns,
                static column => string.Equals(
                    column.ColumnName,
                    "group_id",
                    StringComparison.Ordinal)
                    && column.IsNotNull);
            Assert.DoesNotContain(
                relationColumns,
                column => personalDimensions.Contains(
                    column.ColumnName,
                    StringComparer.Ordinal));
        }
    }

    private static void AssertReviewedPersonalJsonAndEavSurfaces(
        IReadOnlyList<CatalogColumn> columns)
    {
        // This is deliberately a catalog-shape assertion. It freezes every JSON
        // column reachable from a personal-subject relation, but neither reads
        // nor makes claims about JSON values stored in application rows.
        CatalogRelation[] personalRelations = columns
            .GroupBy(static column => column.Relation)
            .Where(IsPersonalSubjectRelation)
            .Select(static relation => relation.Key)
            .OrderBy(static relation => relation.SchemaName, StringComparer.Ordinal)
            .ThenBy(static relation => relation.RelationName, StringComparer.Ordinal)
            .ThenBy(static relation => relation.RelationKind, StringComparer.Ordinal)
            .ToArray();
        HashSet<CatalogRelation> personalRelationSet = personalRelations.ToHashSet();
        HashSet<CatalogIdentity> personalJsonColumns = columns
            .Where(column => personalRelationSet.Contains(column.Relation))
            .Where(static column => column.StoreType is "json" or "jsonb")
            .Select(static column => column.Identity)
            .ToHashSet();

        AssertCatalogMatchesAllowlist(
            ReviewedPersonalJsonColumns,
            personalJsonColumns,
            "personal-subject JSON surface");

        string[] eavViolations = columns
            .Where(column => personalRelationSet.Contains(column.Relation))
            .Where(static column => column.StoreType is not ("json" or "jsonb"))
            .GroupBy(static column => column.Relation)
            .Select(static relation => new
            {
                relation.Key,
                KeyColumns = relation
                    .Where(static column => IsEavKeyColumn(column.ColumnName))
                    .Select(static column => column.ColumnName)
                    .OrderBy(static column => column, StringComparer.Ordinal)
                    .ToArray(),
                ValueColumns = relation
                    .Where(static column => IsEavValueColumn(column.ColumnName))
                    .Select(static column => column.ColumnName)
                    .OrderBy(static column => column, StringComparer.Ordinal)
                    .ToArray(),
            })
            .Where(static candidate =>
                candidate.KeyColumns.Length > 0 && candidate.ValueColumns.Length > 0)
            .Select(static candidate =>
                $"{candidate.Key.DisplayName}: " +
                $"keys=[{string.Join(",", candidate.KeyColumns)}], " +
                $"values=[{string.Join(",", candidate.ValueColumns)}]")
            .OrderBy(static violation => violation, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            eavViolations.Length == 0,
            "DEC-009 forbids an unreviewed personal-subject EAV key/value " +
            "authority surface: " + string.Join("; ", eavViolations));
    }

    private static bool IsPersonalSubjectRelation(
        IGrouping<CatalogRelation, CatalogColumn> relation)
    {
        if (IdentifierWords(relation.Key.RelationName).Overlaps(PersonalSubjectWords))
        {
            return true;
        }

        return relation.Any(static column =>
        {
            FrozenSet<string> words = IdentifierWords(column.ColumnName);
            return words.Contains("id") && words.Overlaps(PersonalSubjectWords);
        });
    }

    private static bool IsEavKeyColumn(string columnName)
    {
        return EavKeyTerminalWords.Contains(IdentifierTerminalWord(columnName));
    }

    private static bool IsEavValueColumn(string columnName)
    {
        return EavValueTerminalWords.Contains(IdentifierTerminalWord(columnName));
    }

    private static void AssertExactCatalog<T>(
        IReadOnlySet<T> expected,
        IEnumerable<T> actual,
        string description)
        where T : notnull
    {
        HashSet<T> actualSet = actual.ToHashSet();
        T[] missing = expected
            .Where(item => !actualSet.Contains(item))
            .OrderBy(static item => item.ToString(), StringComparer.Ordinal)
            .ToArray();
        T[] unexpected = actualSet
            .Where(item => !expected.Contains(item))
            .OrderBy(static item => item.ToString(), StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0 && unexpected.Length == 0,
            $"The final PostgreSQL 18 {description} differ from the exact reviewed " +
            $"catalog. Missing=[{string.Join("; ", missing)}]; " +
            $"unexpected=[{string.Join("; ", unexpected)}].");
    }

    private static void AssertCatalogMatchesAllowlist(
        IReadOnlySet<CatalogIdentity> allowlist,
        IReadOnlySet<CatalogIdentity> actual,
        string description)
    {
        AssertAllowlistIsCurrent(allowlist, actual, description);
        CatalogIdentity[] unexpected = actual
            .Where(identity => !allowlist.Contains(identity))
            .OrderBy(static identity => identity.DisplayName, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            unexpected.Length == 0,
            $"The final {description} catalog contains unreviewed identities: " +
            string.Join(", ", unexpected.Select(static identity => identity.DisplayName)));
    }

    private static void AssertAllowlistIsCurrent(
        IReadOnlySet<CatalogIdentity> allowlist,
        IReadOnlySet<CatalogIdentity> catalogIdentities,
        string description)
    {
        CatalogIdentity[] staleEntries = allowlist
            .Where(identity => !catalogIdentities.Contains(identity))
            .OrderBy(static identity => identity.DisplayName, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            staleEntries.Length == 0,
            $"The exact {description} catalog allowlist contains stale identities: " +
            string.Join(", ", staleEntries.Select(static identity => identity.DisplayName)));
    }

    private static bool IsCumulativeTokenQuotaColumn(CatalogColumn column)
    {
        FrozenSet<string> relationWords = IdentifierWords(column.RelationName);
        FrozenSet<string> schemaWords = IdentifierWords(column.SchemaName);
        FrozenSet<string> columnWords = IdentifierWords(column.ColumnName);
        bool schemaHasQuota = HasWord(schemaWords, "quota", "quotas");
        bool relationHasQuota = HasWord(relationWords, "quota", "quotas");
        bool relationHasToken = HasWord(relationWords, "token", "tokens");
        bool columnHasQuota = HasWord(columnWords, "quota", "quotas");
        bool columnHasToken = HasWord(columnWords, "token", "tokens");
        bool columnHasCumulativeCounter = columnWords.Overlaps(CumulativeCounterWords);

        return (columnHasToken && (columnHasQuota || columnHasCumulativeCounter))
            || (schemaHasQuota && columnHasCumulativeCounter)
            || (relationHasQuota && columnHasCumulativeCounter)
            || (relationHasQuota && relationHasToken && columnHasToken);
    }

    private static FrozenSet<string> IdentifierWords(string identifier)
    {
        HashSet<string> words = new(StringComparer.Ordinal);
        StringBuilder current = new();
        char previous = '\0';
        foreach (char character in identifier)
        {
            if (!char.IsLetterOrDigit(character))
            {
                AddCurrentWord(words, current);
                previous = '\0';
                continue;
            }

            if (current.Length > 0
                && char.IsUpper(character)
                && (char.IsLower(previous) || char.IsDigit(previous)))
            {
                AddCurrentWord(words, current);
            }

            current.Append(char.ToLowerInvariant(character));
            previous = character;
        }

        AddCurrentWord(words, current);
        return words.ToFrozenSet(StringComparer.Ordinal);
    }

    private static void AddCurrentWord(HashSet<string> words, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        _ = words.Add(current.ToString());
        current.Clear();
    }

    private static string IdentifierTerminalWord(string identifier)
    {
        StringBuilder current = new();
        char previous = '\0';
        foreach (char character in identifier)
        {
            if (!char.IsLetterOrDigit(character))
            {
                current.Clear();
                previous = '\0';
                continue;
            }

            if (current.Length > 0
                && char.IsUpper(character)
                && (char.IsLower(previous) || char.IsDigit(previous)))
            {
                current.Clear();
            }

            current.Append(char.ToLowerInvariant(character));
            previous = character;
        }

        return current.ToString();
    }

    private static bool HasWord(
        FrozenSet<string> words,
        string singular,
        string plural) => words.Contains(singular) || words.Contains(plural);

    private static CatalogIdentity Identity(
        string relationName,
        string columnName,
        bool isNotNull = true) => new(
            "public",
            relationName,
            "r",
            columnName,
            "numeric(78,0)",
            isNotNull);

    private static CatalogIdentity JsonIdentity(
        string relationName,
        string columnName,
        bool isNotNull = true) => new(
            "public",
            relationName,
            "r",
            columnName,
            "jsonb",
            isNotNull);

    private sealed record CatalogColumn(
        string SchemaName,
        string RelationName,
        string RelationKind,
        string ColumnName,
        string StoreType,
        bool IsNotNull)
    {
        public CatalogIdentity Identity => new(
            SchemaName,
            RelationName,
            RelationKind,
            ColumnName,
            StoreType,
            IsNotNull);

        public CatalogRelation Relation => new(
            SchemaName,
            RelationName,
            RelationKind);
    }

    private readonly record struct CatalogRelation(
        string SchemaName,
        string RelationName,
        string RelationKind)
    {
        public string DisplayName =>
            $"{SchemaName}.{RelationName}[{RelationKind}]";
    }

    private readonly record struct CatalogIdentity(
        string SchemaName,
        string RelationName,
        string RelationKind,
        string ColumnName,
        string StoreType,
        bool IsNotNull)
    {
        public string DisplayName =>
            $"{SchemaName}.{RelationName}[{RelationKind}].{ColumnName}:{StoreType}:" +
            $"notnull={IsNotNull}";
    }

    private readonly record struct CatalogConstraint(
        string SchemaName,
        string RelationName,
        string ConstraintName,
        string ConstraintType,
        string Columns,
        string? ReferencedSchemaName,
        string? ReferencedRelationName,
        string? ReferencedColumns,
        string? DeleteAction,
        string? UpdateAction,
        bool IsDeferrable,
        bool IsInitiallyDeferred,
        bool IsValidated)
    {
        public static CatalogConstraint PrimaryKey(
            string relationName,
            string constraintName,
            string columns) => new(
                "public",
                relationName,
                constraintName,
                "p",
                columns,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                true);

        public static CatalogConstraint Unique(
            string relationName,
            string constraintName,
            string columns) => new(
                "public",
                relationName,
                constraintName,
                "u",
                columns,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                true);

        public static CatalogConstraint ForeignKey(
            string relationName,
            string constraintName,
            string columns,
            string referencedRelationName,
            string referencedColumns,
            bool isDeferrable = false,
            bool isInitiallyDeferred = false) => new(
                "public",
                relationName,
                constraintName,
                "f",
                columns,
                "public",
                referencedRelationName,
                referencedColumns,
                "r",
                "a",
                isDeferrable,
                isInitiallyDeferred,
                true);

        public override string ToString() =>
            $"{SchemaName}.{RelationName}.{ConstraintName}[{ConstraintType}]" +
            $"({Columns})->{ReferencedSchemaName}.{ReferencedRelationName}" +
            $"({ReferencedColumns});delete={DeleteAction};update={UpdateAction};" +
            $"deferrable={IsDeferrable};deferred={IsInitiallyDeferred};" +
            $"validated={IsValidated}";
    }

    private readonly record struct CatalogUniqueIndex(
        string SchemaName,
        string RelationName,
        string IndexName,
        bool IsPrimary,
        string Columns,
        string? Predicate,
        bool IsValid,
        bool IsReady,
        bool IsLive)
    {
        public static CatalogUniqueIndex Create(
            string relationName,
            string indexName,
            string columns,
            bool isPrimary = false,
            string? predicate = null) => new(
                "public",
                relationName,
                indexName,
                isPrimary,
                columns,
                predicate,
                true,
                true,
                true);

        public override string ToString() =>
            $"{SchemaName}.{RelationName}.{IndexName}({Columns});" +
            $"primary={IsPrimary};predicate={Predicate};valid={IsValid};" +
            $"ready={IsReady};live={IsLive}";
    }
}
