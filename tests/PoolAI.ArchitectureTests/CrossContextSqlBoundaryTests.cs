#pragma warning disable MA0051 // Exact SQL allowlists and lock sequences remain reviewable together.
using System.Text.RegularExpressions;

namespace PoolAI.ArchitectureTests;

public sealed class CrossContextSqlBoundaryTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly string[] AdmissionTables =
    [
        "users",
        "user_roles",
        "api_keys",
        "subscriptions",
        "group_supply_configurations",
        "group_accounts",
        "accounts",
        "channels",
    ];

    private static readonly string[] SupplyTables =
    [
        "group_supply_configurations",
        "group_accounts",
        "accounts",
        "channels",
    ];

    private static readonly string[] ExpectedQuotaEdges =
    [
        "poolai_quota_adjust_usage->accounts",
        "poolai_quota_adjust_usage->channels",
        "poolai_quota_mark_dispatched->accounts",
        "poolai_quota_mark_dispatched->channels",
        "poolai_quota_reserve->accounts",
        "poolai_quota_reserve->api_keys",
        "poolai_quota_reserve->channels",
        "poolai_quota_reserve->group_accounts",
        "poolai_quota_reserve->group_supply_configurations",
        "poolai_quota_reserve->subscriptions",
        "poolai_quota_reserve->user_roles",
        "poolai_quota_reserve->users",
        "poolai_quota_settle->accounts",
        "poolai_quota_settle->channels",
    ];

    private static readonly string[] ExpectedLifecycleEdges =
    [
        "poolai_group_update->subscriptions",
        "poolai_subscription_assign->groups",
        "poolai_subscription_template_create->groups",
        "poolai_subscription_template_retire->groups",
        "poolai_subscription_template_update->groups",
        "poolai_subscription_update->groups",
    ];

    [Fact]
    public void CrossContextDatabaseReadsMatchTheThreeRegisteredFamilies()
    {
        Dictionary<string, string> quota = ReadFunctions("0002_quota_functions.sql");
        Assert.Equal(
            ExpectedQuotaEdges,
            FindEdges(quota, "poolai_quota_", AdmissionTables));

        Dictionary<string, string> baseline = ReadFunctions("0001_baseline.sql");
        Assert.Equal(
            SupplyTables.Select(static table => $"poolai_validate_group_activation->{table}")
                .Order(StringComparer.Ordinal),
            FindEdges(
                SelectFunctions(baseline, "poolai_validate_group_activation"),
                "poolai_validate_group_activation",
                SupplyTables));

        Dictionary<string, string> controlPlane =
            ReadFunctions("0007_group_subscription_m1_e4.sql");
        string[] lifecycleEdges =
        [
            .. FindEdges(controlPlane, "poolai_group_", ["subscriptions"]),
            .. FindEdges(controlPlane, "poolai_subscription_", ["groups"]),
        ];
        Assert.Equal(
            ExpectedLifecycleEdges,
            lifecycleEdges.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RegisteredCrossContextSqlIsReadOnlyStaticAndUsesFrozenFields()
    {
        Dictionary<string, string> quota = ReadFunctions("0002_quota_functions.sql");
        Dictionary<string, string> baseline = ReadFunctions("0001_baseline.sql");
        Dictionary<string, string> controlPlane =
            ReadFunctions("0007_group_subscription_m1_e4.sql");

        Dictionary<string, string[]> allowlist = new(StringComparer.Ordinal)
        {
            ["poolai_quota_reserve"] = AdmissionTables,
            ["poolai_quota_settle"] = ["accounts", "channels"],
            ["poolai_quota_mark_dispatched"] = ["accounts", "channels"],
            ["poolai_quota_adjust_usage"] = ["accounts", "channels"],
            ["poolai_validate_group_activation"] = SupplyTables,
            ["poolai_group_update"] = ["subscriptions"],
            ["poolai_subscription_template_create"] = ["groups"],
            ["poolai_subscription_template_update"] = ["groups"],
            ["poolai_subscription_template_retire"] = ["groups"],
            ["poolai_subscription_assign"] = ["groups"],
            ["poolai_subscription_update"] = ["groups"],
        };
        Dictionary<string, string> bodies = quota
            .Concat(baseline)
            .Concat(controlPlane)
            .Where(pair => allowlist.ContainsKey(pair.Key))
            .ToDictionary(StringComparer.Ordinal);

        Assert.Equal(allowlist.Keys.Order(StringComparer.Ordinal), bodies.Keys.Order(StringComparer.Ordinal));
        foreach ((string function, string[] tables) in allowlist)
        {
            string body = bodies[function];
            Assert.False(HasExecutorEscape(body), $"{function} contains a dynamic SQL escape hatch.");
            foreach (string table in tables)
            {
                Assert.False(
                    HasCrossContextWrite(body, table),
                    $"{function} writes cross-context table {table}.");
            }
        }

        string reserve = quota["poolai_quota_reserve"];
        AssertFields(reserve, "u", ["deleted_at", "id", "locked_until", "status"]);
        AssertFields(reserve, "ur", ["user_id"]);
        AssertFields(reserve, "k", ["expires_at", "group_id", "id", "status", "user_id"]);
        AssertFields(reserve, "s", ["expires_at", "group_id", "id", "starts_at", "status", "user_id"]);
        AssertFields(reserve, "sc", ["channel_id", "group_id"]);
        AssertFields(reserve, "ga", ["account_id", "group_id", "is_enabled"]);
        AssertFields(
            reserve,
            "a",
            ["deleted_at", "id", "last_health_status", "provider", "status", "upstream_rate_limited_until"]);
        AssertFields(reserve, "c", ["deleted_at", "id", "provider", "status"]);

        string activation = baseline["poolai_validate_group_activation"];
        AssertFields(activation, "sc", ["channel_id", "group_id"]);
        AssertFields(activation, "ga", ["account_id", "group_id", "is_enabled"]);
        AssertFields(
            activation,
            "a",
            ["deleted_at", "id", "last_health_status", "provider", "status", "upstream_rate_limited_until"]);
        AssertFields(activation, "c", ["deleted_at", "id", "model_rules", "provider", "status"]);

        foreach (string function in new[]
                 {
                     "poolai_quota_settle",
                     "poolai_quota_mark_dispatched",
                     "poolai_quota_adjust_usage",
                 })
        {
            string routeLock = ExtractRouteProviderLock(quota[function]);
            AssertFields(routeLock, "a", ["id", "provider"]);
            AssertFields(routeLock, "c", ["id", "provider"]);
        }

        AssertFields(controlPlane["poolai_group_update"], "subscription", ["expires_at", "group_id", "status"]);
        foreach (string function in new[]
                 {
                     "poolai_subscription_template_create",
                     "poolai_subscription_template_update",
                     "poolai_subscription_template_retire",
                     "poolai_subscription_assign",
                     "poolai_subscription_update",
                 })
        {
            AssertFields(controlPlane[function], "current_group", ["id", "status"]);
        }

        Assert.True(HasCrossContextWrite("UPDATE public.accounts SET status = 'disabled';", "accounts"));
        Assert.False(HasCrossContextWrite("SELECT * FROM public.accounts FOR UPDATE;", "accounts"));
        Assert.True(HasExecutorEscape("EXECUTE dynamic_sql;"));
    }

    [Fact]
    public void RegisteredCrossContextSqlPreservesLockAndPostWaitClockOrder()
    {
        Dictionary<string, string> quota = ReadFunctions("0002_quota_functions.sql");
        string reserve = NormalizeSql(quota["poolai_quota_reserve"]);
        AssertInOrder(
            reserve,
            "from group_token_quotas q where q.group_id = p_group_id for update",
            "from usage_requests r join users u",
            "for share of r, u, ur, k, s, g",
            "from group_supply_configurations sc where sc.group_id = p_group_id for share",
            "from group_accounts ga join accounts a",
            "from channels c where c.id = v_configured_channel_id for share",
            "from group_quota_periods p where p.id = v_quota.current_period_id",
            "v_now := clock_timestamp()");

        foreach (string function in new[]
                 {
                     "poolai_quota_settle",
                     "poolai_quota_mark_dispatched",
                     "poolai_quota_adjust_usage",
                 })
        {
            string body = NormalizeSql(quota[function]);
            AssertInOrder(
                body,
                "from group_token_quotas q",
                "from group_quota_periods p",
                "from group_token_reservations r",
                "from accounts a join channels c",
                "for share of a, c",
                "v_now := clock_timestamp()");
        }

        Dictionary<string, string> controlPlane =
            ReadFunctions("0007_group_subscription_m1_e4.sql");
        AssertInOrder(
            NormalizeSql(controlPlane["poolai_group_update"]),
            "from public.group_token_quotas as quota",
            "from public.groups as current_group",
            "v_now := clock_timestamp()",
            "from public.subscriptions as subscription");
        AssertInOrder(
            NormalizeSql(controlPlane["poolai_subscription_template_create"]),
            "from public.groups as current_group",
            "for share",
            "v_now := clock_timestamp()");
        AssertInOrder(
            NormalizeSql(controlPlane["poolai_subscription_template_update"]),
            "from public.groups as current_group",
            "for update",
            "from public.subscription_templates as current_template",
            "for update",
            "v_now := clock_timestamp()");
        AssertInOrder(
            NormalizeSql(controlPlane["poolai_subscription_template_retire"]),
            "from public.groups as current_group",
            "for share",
            "from public.subscription_templates as current_template",
            "for update",
            "v_now := clock_timestamp()");
        AssertInOrder(
            NormalizeSql(controlPlane["poolai_subscription_assign"]),
            "from public.groups as current_group",
            "for share",
            "from public.subscription_templates as current_template",
            "for share",
            "v_now := clock_timestamp()");
        AssertInOrder(
            NormalizeSql(controlPlane["poolai_subscription_update"]),
            "from public.groups as current_group",
            "for share",
            "from public.subscriptions as current_subscription",
            "for update",
            "v_now := clock_timestamp()");
    }

    private static Dictionary<string, string> ReadFunctions(string migration)
    {
        string path = Path.Combine(RepositoryRoot.Find(), "docs", "database", migration);
        return ExtractFunctions(File.ReadAllText(path));
    }

    private static Dictionary<string, string> ExtractFunctions(string sql)
    {
        MatchCollection headers = Regex.Matches(
            sql,
            @"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+(?:public\.)?(?<name>poolai_[a-z0-9_]+)\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        Dictionary<string, string> functions = new(StringComparer.Ordinal);
        foreach (Match header in headers)
        {
            int bodyMarker = sql.IndexOf("AS $function$", header.Index + header.Length, StringComparison.OrdinalIgnoreCase);
            Assert.True(bodyMarker >= 0, $"Missing $function$ body for {header.Groups["name"].Value}.");
            int bodyStart = bodyMarker + "AS $function$".Length;
            int bodyEnd = sql.IndexOf("$function$;", bodyStart, StringComparison.Ordinal);
            Assert.True(bodyEnd >= 0, $"Missing $function$ terminator for {header.Groups["name"].Value}.");
            functions[header.Groups["name"].Value.ToLowerInvariant()] = sql[bodyStart..bodyEnd];
        }

        return functions;
    }

    private static Dictionary<string, string> SelectFunctions(
        Dictionary<string, string> functions,
        string name) => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [name] = functions[name],
        };

    private static string[] FindEdges(
        Dictionary<string, string> functions,
        string namePrefix,
        IReadOnlyList<string> candidateTables) => functions
        .Where(pair => pair.Key.StartsWith(namePrefix, StringComparison.Ordinal))
        .SelectMany(pair => candidateTables
            .Where(table => ContainsTable(pair.Value, table))
            .Select(table => $"{pair.Key}->{table}"))
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool ContainsTable(string sql, string table) => Regex.IsMatch(
        StripCommentsAndLiterals(sql),
        $@"\b(?:public\.)?{Regex.Escape(table)}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static bool HasCrossContextWrite(string sql, string table) => Regex.IsMatch(
        StripCommentsAndLiterals(sql),
        $@"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO|TRUNCATE(?:\s+TABLE)?)\s+(?:public\.)?{Regex.Escape(table)}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static bool HasExecutorEscape(string sql) => Regex.IsMatch(
        StripCommentsAndLiterals(sql),
        @"\b(?:EXECUTE|CALL)\b|\bdblink(?:_[a-z0-9_]+)?\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static void AssertFields(string sql, string alias, string[] expected)
    {
        string[] actual = Regex.Matches(
                StripCommentsAndLiterals(sql),
                $@"\b{Regex.Escape(alias)}\.(?<column>[a-z_][a-z0-9_]*)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout)
            .Select(match => match.Groups["column"].Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static string ExtractRouteProviderLock(string body)
    {
        Match match = Regex.Match(
            body,
            @"FROM\s+(?:public\.)?accounts\s+a\s+JOIN\s+(?:public\.)?channels\s+c\b.*?FOR\s+SHARE\s+OF\s+a\s*,\s*c\s*;",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
            RegexTimeout);
        Assert.True(match.Success, "Missing the frozen Account/Channel provider row lock.");
        return match.Value;
    }

    private static void AssertInOrder(string sql, params string[] fragments)
    {
        int position = 0;
        foreach (string fragment in fragments)
        {
            int next = sql.IndexOf(fragment, position, StringComparison.Ordinal);
            Assert.True(next >= position, $"Missing or out-of-order SQL fragment: {fragment}");
            position = next + fragment.Length;
        }
    }

    private static string NormalizeSql(string sql) => Regex.Replace(
        StripCommentsAndLiterals(sql),
        @"\s+",
        " ",
        RegexOptions.CultureInvariant,
        RegexTimeout).Trim().ToLowerInvariant();

    private static string StripCommentsAndLiterals(string sql)
    {
        string withoutBlocks = Regex.Replace(
            sql,
            @"/\*.*?\*/",
            " ",
            RegexOptions.CultureInvariant | RegexOptions.Singleline,
            RegexTimeout);
        string withoutLines = Regex.Replace(
            withoutBlocks,
            @"--.*?$",
            " ",
            RegexOptions.CultureInvariant | RegexOptions.Multiline,
            RegexTimeout);
        return Regex.Replace(
            withoutLines,
            @"'(?:''|[^'])*'",
            "''",
            RegexOptions.CultureInvariant | RegexOptions.Singleline,
            RegexTimeout);
    }
}
#pragma warning restore MA0051
