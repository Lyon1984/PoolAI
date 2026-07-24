#pragma warning disable MA0051 // Exact SQL allowlists and lock sequences remain reviewable together.
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PoolAI.ArchitectureTests;

public sealed class CrossContextSqlBoundaryTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private const string Technical = "Technical";
    private const string Identity = "Identity";
    private const string SubscriptionAccess = "SubscriptionAccess";
    private const string GroupQuota = "GroupQuota";
    private const string Supply = "Supply";
    private const string Usage = "Usage";
    private const string Operations = "Operations";

    private static readonly Dictionary<string, string> TableOwners = new(StringComparer.Ordinal)
    {
        ["poolai_schema_migrations"] = Technical,
        ["users"] = Identity,
        ["roles"] = Identity,
        ["user_roles"] = Identity,
        ["refresh_sessions"] = Identity,
        ["one_time_tokens"] = Identity,
        ["email_outbox"] = Identity,
        ["api_keys"] = Identity,
        ["totp_recovery_codes"] = Identity,
        ["email_outbox_delivery_failures"] = Identity,
        ["subscription_templates"] = SubscriptionAccess,
        ["subscriptions"] = SubscriptionAccess,
        ["groups"] = GroupQuota,
        ["group_token_quotas"] = GroupQuota,
        ["group_quota_periods"] = GroupQuota,
        ["usage_requests"] = GroupQuota,
        ["group_token_reservations"] = GroupQuota,
        ["group_quota_events"] = GroupQuota,
        ["usage_attempts"] = GroupQuota,
        ["usage_attempt_adjustments"] = GroupQuota,
        ["accounts"] = Supply,
        ["channels"] = Supply,
        ["group_supply_configurations"] = Supply,
        ["group_accounts"] = Supply,
        ["group_usage_hourly"] = Usage,
        ["account_usage_hourly"] = Usage,
        ["aggregation_watermarks"] = Usage,
        ["idempotency_records"] = Operations,
        ["outbox_messages"] = Operations,
        ["inbox_messages"] = Operations,
        ["audit_logs"] = Operations,
    };

    private static readonly Dictionary<string, string> FunctionOwners = new(StringComparer.Ordinal)
    {
        ["poolai_guard_delivery_fence"] = Technical,
        ["poolai_guard_terminal_status"] = Technical,
        ["poolai_reject_fact_mutation"] = Technical,
        ["poolai_reject_api_key_group_change"] = Identity,
        ["poolai_bump_user_security_versions"] = Identity,
        ["poolai_bump_role_user_version"] = Identity,
        ["poolai_identity_update_user"] = Identity,
        ["poolai_api_key_ip_acl_is_canonical"] = Identity,
        ["poolai_api_key_text_is_valid"] = Identity,
        ["poolai_api_key_create"] = Identity,
        ["poolai_api_key_update"] = Identity,
        ["poolai_api_key_revoke"] = Identity,
        ["poolai_api_key_rotate"] = Identity,
        ["poolai_snapshot_subscription_template"] = SubscriptionAccess,
        ["poolai_subscription_template_create"] = SubscriptionAccess,
        ["poolai_subscription_template_update"] = SubscriptionAccess,
        ["poolai_subscription_template_retire"] = SubscriptionAccess,
        ["poolai_subscription_assign"] = SubscriptionAccess,
        ["poolai_subscription_update"] = SubscriptionAccess,
        ["poolai_reject_supply_provider_change"] = Supply,
        ["poolai_guard_supply_retirement"] = Supply,
        ["poolai_guard_group_supply_configuration"] = Supply,
        ["poolai_validate_group_account_binding"] = Supply,
        ["poolai_bump_group_supply_configuration_version"] = Supply,
        ["poolai_validate_group_activation"] = GroupQuota,
        ["poolai_business_error"] = GroupQuota,
        ["poolai_quota_remaining"] = GroupQuota,
        ["poolai_emit_quota_event"] = GroupQuota,
        ["poolai_quota_reset"] = GroupQuota,
        ["poolai_quota_adjust_total"] = GroupQuota,
        ["poolai_quota_end_pending"] = GroupQuota,
        ["poolai_quota_release"] = GroupQuota,
        ["poolai_quota_expire"] = GroupQuota,
        ["poolai_numeric78_max"] = GroupQuota,
        ["poolai_quota_settle"] = GroupQuota,
        ["poolai_quota_initialize"] = GroupQuota,
        ["poolai_quota_reserve"] = GroupQuota,
        ["poolai_quota_mark_dispatched"] = GroupQuota,
        ["poolai_quota_renew"] = GroupQuota,
        ["poolai_quota_adjust_usage"] = GroupQuota,
        ["poolai_group_create"] = GroupQuota,
        ["poolai_group_update"] = GroupQuota,
    };

    private static readonly Dictionary<string, RegisteredAccess> RegisteredBusinessAccesses =
        new(StringComparer.Ordinal)
        {
            ["poolai_quota_reserve->users"] =
                new("u", ["deleted_at", "id", "locked_until", "status"]),
            ["poolai_quota_reserve->user_roles"] = new("ur", ["user_id"]),
            ["poolai_quota_reserve->api_keys"] =
                new("k", ["expires_at", "group_id", "id", "status", "user_id"]),
            ["poolai_quota_reserve->subscriptions"] =
                new("s", ["expires_at", "group_id", "id", "starts_at", "status", "user_id"]),
            ["poolai_quota_reserve->group_supply_configurations"] =
                new("sc", ["channel_id", "group_id"]),
            ["poolai_quota_reserve->group_accounts"] =
                new("ga", ["account_id", "group_id", "is_enabled"]),
            ["poolai_quota_reserve->accounts"] = new(
                "a",
                ["deleted_at", "id", "last_health_status", "provider", "status", "upstream_rate_limited_until"]),
            ["poolai_quota_reserve->channels"] =
                new("c", ["deleted_at", "id", "provider", "status"]),
            ["poolai_quota_settle->accounts"] = new("a", ["id", "provider"]),
            ["poolai_quota_settle->channels"] = new("c", ["id", "provider"]),
            ["poolai_quota_mark_dispatched->accounts"] = new("a", ["id", "provider"]),
            ["poolai_quota_mark_dispatched->channels"] = new("c", ["id", "provider"]),
            ["poolai_quota_adjust_usage->accounts"] = new("a", ["id", "provider"]),
            ["poolai_quota_adjust_usage->channels"] = new("c", ["id", "provider"]),
            ["poolai_validate_group_activation->group_supply_configurations"] =
                new("sc", ["channel_id", "group_id"]),
            ["poolai_validate_group_activation->group_accounts"] =
                new("ga", ["account_id", "group_id", "is_enabled"]),
            ["poolai_validate_group_activation->accounts"] = new(
                "a",
                ["deleted_at", "id", "last_health_status", "provider", "status", "upstream_rate_limited_until"]),
            ["poolai_validate_group_activation->channels"] =
                new("c", ["deleted_at", "id", "model_rules", "provider", "status"]),
            ["poolai_group_update->subscriptions"] =
                new("subscription", ["expires_at", "group_id", "status"]),
            ["poolai_subscription_template_create->groups"] =
                new("current_group", ["id", "status"]),
            ["poolai_subscription_template_update->groups"] =
                new("current_group", ["id", "status"]),
            ["poolai_subscription_template_retire->groups"] =
                new("current_group", ["id", "status"]),
            ["poolai_subscription_assign->groups"] =
                new("current_group", ["id", "status"]),
            ["poolai_subscription_update->groups"] =
                new("current_group", ["id", "status"]),
        };

    // Operations outbox append/read dependencies are technical delivery edges,
    // not a fourth ADR 0006 business read family.
    private static readonly string[] RegisteredTechnicalEdges =
    [
        "poolai_emit_quota_event->outbox_messages",
        "poolai_quota_adjust_total->outbox_messages",
        "poolai_quota_adjust_usage->outbox_messages",
        "poolai_quota_end_pending->outbox_messages",
        "poolai_quota_initialize->outbox_messages",
        "poolai_quota_mark_dispatched->outbox_messages",
        "poolai_quota_renew->outbox_messages",
        "poolai_quota_reserve->outbox_messages",
        "poolai_quota_reset->outbox_messages",
        "poolai_quota_settle->outbox_messages",
    ];

    private static readonly string[] RegisteredFunctionCallBindings =
    [
        "poolai_api_key_create->poolai_api_key_ip_acl_is_canonical",
        "poolai_api_key_create->poolai_api_key_text_is_valid",
        "poolai_api_key_update->poolai_api_key_ip_acl_is_canonical",
        "poolai_api_key_update->poolai_api_key_text_is_valid",
        "poolai_api_key_revoke->poolai_api_key_text_is_valid",
        "poolai_api_key_rotate->poolai_api_key_text_is_valid",
        "poolai_emit_quota_event->poolai_business_error",
        "poolai_group_create->poolai_quota_initialize",
        "poolai_quota_adjust_total->poolai_business_error",
        "poolai_quota_adjust_total->poolai_emit_quota_event",
        "poolai_quota_adjust_total->poolai_quota_remaining",
        "poolai_quota_adjust_usage->poolai_business_error",
        "poolai_quota_adjust_usage->poolai_emit_quota_event",
        "poolai_quota_adjust_usage->poolai_numeric78_max",
        "poolai_quota_end_pending->poolai_business_error",
        "poolai_quota_end_pending->poolai_emit_quota_event",
        "poolai_quota_end_pending->poolai_quota_remaining",
        "poolai_quota_expire->poolai_quota_end_pending",
        "poolai_quota_initialize->poolai_business_error",
        "poolai_quota_initialize->poolai_emit_quota_event",
        "poolai_quota_initialize->poolai_quota_remaining",
        "poolai_quota_mark_dispatched->poolai_business_error",
        "poolai_quota_mark_dispatched->poolai_emit_quota_event",
        "poolai_quota_release->poolai_quota_end_pending",
        "poolai_quota_renew->poolai_business_error",
        "poolai_quota_renew->poolai_emit_quota_event",
        "poolai_quota_reserve->poolai_business_error",
        "poolai_quota_reserve->poolai_emit_quota_event",
        "poolai_quota_reserve->poolai_quota_remaining",
        "poolai_quota_reset->poolai_business_error",
        "poolai_quota_reset->poolai_emit_quota_event",
        "poolai_quota_reset->poolai_quota_remaining",
        "poolai_quota_settle->poolai_business_error",
        "poolai_quota_settle->poolai_emit_quota_event",
        "poolai_quota_settle->poolai_numeric78_max",
        "poolai_quota_settle->poolai_quota_remaining",
    ];

    private static readonly string[] PostgreSqlSystemColumns =
        ["cmax", "cmin", "ctid", "tableoid", "xmax", "xmin"];

    private static readonly string[] RegisteredDoBlocks =
    [
        "0003_runtime_permissions.sql:$permission$:2dce0072c425af639f31354ab557efc250c1de90effcbf1cc839e7551f051d85",
        "0003_runtime_permissions.sql:$permission_audit$:9d4ad1d7617aef45d5769994a66a4c7bb3f32045a4db532f87497dff7d177c47",
        "0005_identity_m1_e2.sql:$permission_audit$:623427a4c7007d454fb4010d29e46f463a32f6982dc968bc6d32c8cc90c6323d",
        "0006_identity_m1_e3.sql:$permission_audit$:e6ef176d5a1e31f3653a4f1360949586e638c8415eaad9615aca21ed8c298414",
        "0007_group_subscription_m1_e4.sql:$permission_audit$:92eec20f8aec97715ffdee2e4bb182d405558860e8f86d49abbf56eedd238641",
        "0008_identity_api_keys_m1_e5.sql:$permission_audit$:e537d1ed39ebfeeb040b34e330e0e395a48a07fd55ccff41639a95cd7750d6ad",
        "0009_identity_api_key_text_validation_m1_e5.sql:$permission_audit$:5da19374d5c80bbec7b4712436347b3ab255c471b4261f828ac63031edcbefed",
    ];

    private static readonly string[] RegisteredSetConfigStatements =
    [
        "poolai_identity_update_user:perform set_config('poolai.identity_role_user_id', '', true);",
        "poolai_identity_update_user:perform set_config('poolai.identity_role_user_id', '', true);",
        "poolai_identity_update_user:perform set_config('poolai.identity_role_user_id', p_user_id::text, true);",
    ];

    // Freeze the complete trigger registry, including the trigger name, so even a
    // same-owner function cannot silently gain a new invocation point.
    private static readonly string[] RegisteredTriggerStatements =
    [
        "create trigger tr_email_outbox_delivery_fence before update on email_outbox for each row execute function poolai_guard_delivery_fence('lock_owner', 'sent', 'attempts');",
        "create trigger tr_outbox_messages_delivery_fence before update on outbox_messages for each row execute function poolai_guard_delivery_fence('locked_by', 'published', 'publish_attempts');",
        "create trigger tr_api_keys_group_immutable before update of group_id on api_keys for each row execute function poolai_reject_api_key_group_change();",
        "create trigger tr_accounts_provider_immutable before update of provider on accounts for each row execute function poolai_reject_supply_provider_change();",
        "create trigger tr_channels_provider_immutable before update of provider on channels for each row execute function poolai_reject_supply_provider_change();",
        "create trigger tr_accounts_guard_retirement before update of status on accounts for each row execute function poolai_guard_supply_retirement();",
        "create trigger tr_channels_guard_retirement before update of status on channels for each row execute function poolai_guard_supply_retirement();",
        "create trigger tr_groups_terminal_status before update of status on groups for each row execute function poolai_guard_terminal_status();",
        "create trigger tr_accounts_terminal_status before update of status on accounts for each row execute function poolai_guard_terminal_status();",
        "create trigger tr_channels_terminal_status before update of status on channels for each row execute function poolai_guard_terminal_status();",
        "create trigger tr_subscription_templates_terminal_status before update of status on subscription_templates for each row execute function poolai_guard_terminal_status();",
        "create trigger tr_api_keys_terminal_status before update of status on api_keys for each row execute function poolai_guard_terminal_status();",
        "create trigger tr_users_bump_security_versions before update of status, password_hash, totp_secret_envelope, security_stamp, token_version on users for each row execute function poolai_bump_user_security_versions();",
        "create trigger tr_user_roles_bump_user_version after insert or delete or update of user_id, role_id on user_roles for each row execute function poolai_bump_role_user_version();",
        "create trigger tr_subscriptions_snapshot_template before insert or update of template_id, group_id, template_name_snapshot on subscriptions for each row execute function poolai_snapshot_subscription_template();",
        "create trigger tr_group_supply_configurations_guard before insert or update on group_supply_configurations for each row execute function poolai_guard_group_supply_configuration();",
        "create trigger tr_group_accounts_validate_binding before insert or update of group_id, account_id, is_enabled, priority_override, weight_override on group_accounts for each row execute function poolai_validate_group_account_binding();",
        "create trigger tr_group_accounts_bump_supply_configuration_version after insert or update or delete on group_accounts for each row execute function poolai_bump_group_supply_configuration_version();",
        "create trigger tr_groups_validate_activation before insert or update of status, activation_supply_readiness_token, activation_supply_observed_at on groups for each row execute function poolai_validate_group_activation();",
        "create trigger tr_group_quota_events_append_only before update or delete on group_quota_events for each row execute function poolai_reject_fact_mutation();",
        "create trigger tr_usage_attempts_append_only before update or delete on usage_attempts for each row execute function poolai_reject_fact_mutation();",
        "create trigger tr_usage_attempt_adjustments_append_only before update or delete on usage_attempt_adjustments for each row execute function poolai_reject_fact_mutation();",
        "create trigger tr_audit_logs_append_only before update or delete on audit_logs for each row execute function poolai_reject_fact_mutation();",
    ];

    private static readonly Dictionary<string, string[]> ExpectedForLockModes =
        new(StringComparer.Ordinal)
        {
            ["poolai_quota_reserve"] =
                ["share", "share", "share", "share", "update", "update", "update", "update"],
            ["poolai_quota_settle"] = ["share", "update", "update", "update"],
            ["poolai_quota_mark_dispatched"] = ["share", "update", "update", "update"],
            ["poolai_quota_adjust_usage"] = ["share", "update", "update", "update"],
            ["poolai_validate_group_activation"] = [],
            ["poolai_group_update"] = ["update", "update"],
            ["poolai_subscription_template_create"] = ["share"],
            ["poolai_subscription_template_update"] = ["update", "update"],
            ["poolai_subscription_template_retire"] = ["share", "update"],
            ["poolai_subscription_assign"] = ["share", "share"],
            ["poolai_subscription_update"] = ["share", "update"],
        };

    [Fact]
    public void EveryMigrationFunctionUsesKnownOwnersAndFrozenCrossContextBoundaries()
    {
        MigrationSql[] migrations = ReadMigrations();
        Dictionary<string, string[]> tableColumns = ExtractTableColumns(migrations);
        string[] createdTables = tableColumns.Keys.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(TableOwners.Keys.Order(StringComparer.Ordinal), createdTables);
        Assert.Empty(FindUnownedTables(createdTables));
        foreach (string[] columns in tableColumns.Values)
        {
            Assert.NotEmpty(columns);
        }

        FunctionDefinition[] functions = migrations
            .SelectMany(migration => ExtractFunctionDefinitions(migration.Name, migration.Sql))
            .ToArray();
        Assert.Equal(
            FunctionOwners.Keys.Order(StringComparer.Ordinal),
            functions.Select(static function => function.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

        foreach (FunctionDefinition function in functions)
        {
            Assert.False(
                HasExecutorEscape(function.Body),
                $"{function.Migration}:{function.Name} contains a dynamic SQL escape hatch.");
            Assert.False(
                HasQuotedIdentifier(function.Body),
                $"{function.Migration}:{function.Name} contains a quoted identifier escape hatch.");
            Assert.False(
                HasUnsupportedRuntimeSqlShape(function.Body),
                $"{function.Migration}:{function.Name} contains an unsupported runtime SQL shape.");
            Assert.False(
                HasSystemSchemaWrite(function.Body),
                $"{function.Migration}:{function.Name} writes a PostgreSQL system schema.");
            Assert.False(
                HasCommaSeparatedRelation(function.Body),
                $"{function.Migration}:{function.Name} contains a comma-separated relation.");
            Assert.Empty(FindInvalidRelations(function.Body));
            Assert.DoesNotContain(
                ExtractFunctionCalls(function.Body),
                call => !FunctionOwners.ContainsKey(call));
        }

        Assert.Equal(
            RegisteredFunctionCallBindings.Order(StringComparer.Ordinal),
            ExtractFunctionCallBindings(functions));
        Assert.Empty(FindInvalidFunctionCallBindings(functions));
        Assert.Equal(
            RegisteredSetConfigStatements.Order(StringComparer.Ordinal),
            ExtractSetConfigStatements(functions));

        TriggerDefinition[] triggers = migrations
            .SelectMany(migration => ExtractTriggerDefinitions(migration.Name, migration.Sql))
            .ToArray();
        Assert.Empty(FindInvalidTriggerBindings(triggers));
        Assert.Equal(
            RegisteredTriggerStatements.Order(StringComparer.Ordinal),
            ExtractTriggerStatements(triggers));

        DoBlockDefinition[] doBlocks = migrations
            .SelectMany(migration => ExtractDoBlockDefinitions(migration.Name, migration.Sql))
            .ToArray();
        Assert.DoesNotContain(doBlocks, block => HasExecutorEscape(block.Body));
        Assert.Equal(
            RegisteredDoBlocks.Order(StringComparer.Ordinal),
            doBlocks.Select(block => block.RegistryKey).Order(StringComparer.Ordinal));

        string[] actualEdges = functions
            .SelectMany(function => ExtractOwnedRelations(function.Body)
                .Where(table => !string.Equals(
                        TableOwners[table],
                        FunctionOwners[function.Name],
                        StringComparison.Ordinal))
                .Select(table => $"{function.Name}->{table}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] registeredEdges = RegisteredBusinessAccesses.Keys
            .Concat(RegisteredTechnicalEdges)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(registeredEdges, actualEdges);

        string[] actualCrossOwnerCalls = functions
            .SelectMany(function => FindCrossOwnerFunctionCalls(function.Name, function.Body))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(actualCrossOwnerCalls);

        string[] adrFunctions = RegisteredBusinessAccesses.Keys
            .Select(GetEdgeFunction)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedForLockModes.Keys.Order(StringComparer.Ordinal), adrFunctions);
        Dictionary<string, string> adrBodies = adrFunctions
            .ToDictionary(
                function => function,
                function => Assert.Single(functions, definition => string.Equals(
                    definition.Name,
                    function,
                    StringComparison.Ordinal)).Body,
                StringComparer.Ordinal);
        foreach ((string edge, RegisteredAccess access) in RegisteredBusinessAccesses)
        {
            int separator = edge.IndexOf("->", StringComparison.Ordinal);
            string function = edge[..separator];
            string table = edge[(separator + 2)..];
            string body = adrBodies[function];

            Assert.False(
                HasCrossContextWrite(body, table),
                $"{function} writes cross-context table {table}.");
            Assert.False(
                HasTableAliasColumnList(body, table),
                $"{function}->{table} contains a relation column-alias list.");
            string[] aliases = FindTableAliases(body, table);
            string[] scopes = FindTableReadScopes(body, table);
            Assert.NotEmpty(aliases);
            Assert.All(aliases, alias => Assert.Equal(access.Alias, alias));
            Assert.Equal(scopes.Length, aliases.Length);
            Assert.Equal(
                access.Fields.Order(StringComparer.Ordinal),
                FindFieldsForTableAlias(body, table, access.Alias));
            foreach (string scope in scopes)
            {
                Assert.False(HasBareStar(scope), $"{function}->{table} contains a bare * read.");
                Assert.Empty(FindBareTableColumns(scope, tableColumns[table]));
                Assert.False(
                    HasWholeRowAliasUsage(scope, table, access.Alias),
                    $"{function}->{table} uses {access.Alias} as a whole-row value.");
            }
        }

        foreach ((string function, string[] expectedModes) in ExpectedForLockModes)
        {
            string body = adrBodies[function];
            Assert.False(
                HasTableOrAdvisoryLock(body),
                $"{function} contains an unregistered table or advisory lock.");
            Assert.Equal(expectedModes, ExtractForLockModes(body));
        }

        string activation = adrBodies["poolai_validate_group_activation"];
        Assert.False(HasRowLock(activation), "Family B activation validation must remain lock-free.");

        string groupUpdate = adrBodies["poolai_group_update"];
        Assert.True(
            ReferencesTableOnlyInsideIfBlock(
                groupUpdate,
                "subscriptions",
                "if v_status = '' then"),
            "Family C may read subscriptions only in the exact archive-decision branch.");
        Assert.True(
            ReferencesTableOnlyInsideLiteralIfBlock(
                groupUpdate,
                "subscriptions",
                "if v_status = 'archived' then"),
            "Family C must retain the exact archived literal branch.");
    }

    [Fact]
    public void BoundaryScannerRejectsAliasStarUnknownOwnerWritesDynamicSqlAndLockDrift()
    {
        const string hostileSql = """
            SELECT rogue.*
            FROM public.accounts AS rogue
            FOR SHARE;
            UPDATE public.accounts AS rogue SET status = 'retired';
            EXECUTE dynamic_sql;
            SELECT unknown_relation.id
            FROM public.rogue_table AS unknown_relation;
            """;

        Assert.Equal(["rogue"], FindTableAliases(hostileSql, "accounts"));
        Assert.NotEqual(["a"], FindTableAliases(hostileSql, "accounts"));
        Assert.Equal(["*"], FindFieldsForTableAlias(hostileSql, "accounts", "rogue"));
        Assert.True(HasCrossContextWrite(hostileSql, "accounts"));
        foreach (string onlyWrite in new[]
                 {
                     "UPDATE ONLY public.accounts SET status = 'retired';",
                     "DELETE FROM ONLY public.accounts;",
                     "MERGE INTO ONLY public.accounts AS target USING public.accounts AS source ON false WHEN MATCHED THEN DELETE;",
                     "TRUNCATE ONLY public.accounts;",
                     "UPDATE ONLY (public.accounts) SET status = 'retired';",
                     "UPDATE ONLY(public.accounts) SET status = 'retired';",
                     "DELETE FROM ONLY (public.accounts);",
                     "MERGE INTO ONLY (public.accounts) AS target USING public.accounts AS source ON false WHEN MATCHED THEN DELETE;",
                     "TRUNCATE ONLY (public.accounts);",
                     "UPDATE ONLY(public . accounts) SET status = 'retired';",
                     "INSERT INTO public . accounts(id) VALUES (gen_random_uuid());",
                     "DELETE FROM public . accounts;",
                     "MERGE INTO public . accounts AS target USING (VALUES (1)) AS source(id) ON false WHEN MATCHED THEN DELETE;",
                     "TRUNCATE public . accounts;",
                 })
        {
            Assert.True(HasCrossContextWrite(onlyWrite, "accounts"));
            Assert.Contains("accounts", ExtractOwnedRelations(onlyWrite));
        }
        Assert.True(HasExecutorEscape(hostileSql));
        Assert.True(HasRowLock(hostileSql));
        Assert.True(HasRowLock("LOCK TABLE public.accounts IN ACCESS EXCLUSIVE MODE;"));
        Assert.True(HasRowLock("LOCK TABLE public . accounts IN ACCESS EXCLUSIVE MODE;"));
        Assert.True(HasRowLock("LOCK public.accounts;"));
        foreach (string advisoryLock in new[]
                 {
                     "pg_advisory_lock",
                     "pg_advisory_xact_lock",
                     "pg_try_advisory_lock",
                     "pg_try_advisory_xact_lock",
                     "pg_try_advisory_xact_lock_shared",
                 })
        {
            Assert.True(HasRowLock($"SELECT {advisoryLock}(42);"));
        }
        Assert.Equal(["rogue_table"], FindUnownedTables(["users", "rogue_table"]));
        Assert.Equal(["rogue_table"], FindInvalidRelations(hostileSql));
        Assert.True(HasQuotedIdentifier("UPDATE public.\"groups\" SET status = 'disabled';"));
        Assert.Equal(
            ["private.groups", "rogue_table"],
            FindInvalidRelations(
                "SELECT 1 FROM rogue_table AS rogue; SELECT 1 FROM private.groups AS secret;"));
        foreach (string threePartRelation in new[]
                 {
                     "SELECT account.id FROM current_database . public . accounts AS account;",
                     "UPDATE ONLY(current_database . public . accounts) SET status = 'retired';",
                 })
        {
            Assert.Equal(
                ["current_database.public.accounts"],
                FindInvalidRelations(threePartRelation));
            Assert.Empty(ExtractOwnedRelations(threePartRelation));
        }
        Assert.Empty(FindInvalidRelations("""
            WITH candidate AS (SELECT 1)
            SELECT * FROM candidate;
            SELECT * FROM pg_catalog.pg_class;
            SELECT * FROM information_schema.tables;
            SELECT * FROM jsonb_array_elements('[]'::jsonb) AS item;
            """));

        Assert.Equal(
            ["poolai_subscription_assign->poolai_group_update"],
            FindCrossOwnerFunctionCalls(
                "poolai_subscription_assign",
                "PERFORM public.poolai_group_update(NULL);"));
        Assert.Equal(
            ["poolai_subscription_assign->poolai_group_update"],
            FindCrossOwnerFunctionCalls(
                "poolai_subscription_assign",
                "PERFORM public . poolai_group_update(NULL);"));
        Assert.NotEmpty(
            FindInvalidFunctionCallBindings(
            [
                new FunctionDefinition(
                    "hostile.sql",
                    "poolai_quota_settle",
                    "PERFORM public.poolai_quota_reserve(NULL);")
            ]));
        Assert.NotEqual(
            ["share"],
            ExtractForLockModes(
                "SELECT 1 FROM groups FOR SHARE; SELECT 1 FROM groups FOR UPDATE;"));

        const string bareRead = "SELECT *, credential_envelope FROM public.accounts AS a;";
        string bareScope = Assert.Single(FindTableReadScopes(bareRead, "accounts"));
        Assert.True(HasBareStar(bareScope));
        Assert.Equal(
            ["credential_envelope"],
            FindBareTableColumns(
                bareScope,
                ExtractTableColumns(ReadMigrations())["accounts"]));

        const string fromOnlyRead =
            "SELECT rogue.credential_envelope FROM ONLY public.accounts AS rogue;";
        Assert.Equal(["rogue"], FindTableAliases(fromOnlyRead, "accounts"));
        Assert.Equal(
            ["credential_envelope"],
            FindFieldsForTableAlias(fromOnlyRead, "accounts", "rogue"));
        Assert.Single(FindTableReadScopes(fromOnlyRead, "accounts"));

        const string parenthesizedOnlyRead =
            "SELECT rogue.credential_envelope FROM ONLY (public.accounts) AS rogue;";
        Assert.Contains("accounts", ExtractOwnedRelations(parenthesizedOnlyRead));
        Assert.Equal(["rogue"], FindTableAliases(parenthesizedOnlyRead, "accounts"));
        Assert.Equal(
            ["credential_envelope"],
            FindFieldsForTableAlias(parenthesizedOnlyRead, "accounts", "rogue"));
        Assert.Single(FindTableReadScopes(parenthesizedOnlyRead, "accounts"));

        const string compactParenthesizedOnlyRead =
            "SELECT rogue.credential_envelope FROM ONLY(public.accounts) AS rogue;";
        Assert.Contains("accounts", ExtractOwnedRelations(compactParenthesizedOnlyRead));
        Assert.Equal(["rogue"], FindTableAliases(compactParenthesizedOnlyRead, "accounts"));
        Assert.Equal(
            ["credential_envelope"],
            FindFieldsForTableAlias(compactParenthesizedOnlyRead, "accounts", "rogue"));
        Assert.Single(FindTableReadScopes(compactParenthesizedOnlyRead, "accounts"));

        const string spacedQualifiedRead =
            "SELECT rogue . credential_envelope FROM ONLY(public . accounts) AS rogue;";
        Assert.True(ContainsTable(spacedQualifiedRead, "accounts"));
        Assert.Contains("accounts", ExtractOwnedRelations(spacedQualifiedRead));
        Assert.Equal(["rogue"], FindTableAliases(spacedQualifiedRead, "accounts"));
        Assert.Equal(
            ["credential_envelope"],
            FindFieldsForTableAlias(spacedQualifiedRead, "accounts", "rogue"));
        Assert.Single(FindTableReadScopes(spacedQualifiedRead, "accounts"));

        const string privateQualifiedRead =
            "SELECT hidden.id FROM ONLY(private . groups) AS hidden;";
        Assert.Equal(["private.groups"], FindInvalidRelations(privateQualifiedRead));

        foreach (string privateQualifiedWrite in new[]
                 {
                     "UPDATE private . accounts SET status = 'retired';",
                     "INSERT INTO private . accounts(id) VALUES (gen_random_uuid());",
                     "DELETE FROM private . accounts;",
                     "MERGE INTO private . accounts AS target USING (VALUES (1)) AS source(id) ON false WHEN MATCHED THEN DELETE;",
                     "TRUNCATE private . accounts;",
                 })
        {
            Assert.Contains("private.accounts", FindInvalidRelations(privateQualifiedWrite));
        }

        const string relationColumnAliases =
            "SELECT a.provider FROM public.accounts AS a(id, provider, credential_envelope);";
        Assert.True(HasTableAliasColumnList(relationColumnAliases, "accounts"));
        Assert.True(HasTableAliasColumnList(
            "SELECT a . provider FROM public . accounts AS a(id, provider);",
            "accounts"));

        Assert.True(HasWholeRowAliasUsage(
            "SELECT to_jsonb(a) FROM public.accounts AS a;",
            "accounts",
            "a"));
        Assert.True(HasWholeRowAliasUsage(
            "SELECT a FROM public.accounts AS a;",
            "accounts",
            "a"));

        const string unaliasedWholeRow = "SELECT accounts FROM public.accounts;";
        Assert.Empty(FindTableAliases(unaliasedWholeRow, "accounts"));
        Assert.Single(FindTableReadScopes(unaliasedWholeRow, "accounts"));

        string systemColumnScope = Assert.Single(
            FindTableReadScopes("SELECT ctid FROM public.accounts AS a;", "accounts"));
        Assert.Equal(
            ["ctid"],
            FindBareTableColumns(
                systemColumnScope,
                ExtractTableColumns(ReadMigrations())["accounts"]));

        foreach (string unsupportedRuntimeSql in new[]
                 {
                     "TABLE public.accounts;",
                     "TABLE public . accounts;",
                     "COPY public.accounts TO STDOUT;",
                     "TRUNCATE public.groups, public.accounts;",
                     "SELECT 1 FROM public.groups NATURAL JOIN public.accounts;",
                     "DELETE FROM public.groups USING public.accounts WHERE true;",
                     "MERGE INTO public.groups AS target USING public.accounts AS source ON false WHEN MATCHED THEN DELETE;",
                     "SELECT rogue.id FROM public.accounts* AS rogue;",
                     "WITH accounts AS (SELECT 1 AS id) SELECT id FROM accounts; SELECT rogue.id FROM public.accounts AS rogue;",
                     "ALTER TABLE public.accounts ADD COLUMN escape text;",
                     "GRANT SELECT ON public.accounts TO PUBLIC;",
                     "SET LOCAL search_path = pg_catalog;",
                     "RESET ROLE;",
                     "NOTIFY poolai_escape;",
                     "LOCK TABLE public.accounts IN ACCESS EXCLUSIVE MODE;",
                     "DO $$ BEGIN NULL; END $$;",
                 })
        {
            Assert.True(HasUnsupportedRuntimeSqlShape(unsupportedRuntimeSql));
        }
        string rogueSetConfig = Assert.Single(
            ExtractSetConfigStatements(
            [
                new FunctionDefinition(
                    "hostile.sql",
                    "poolai_quota_settle",
                    "PERFORM set_config('search_path', 'pg_catalog', true);")
            ]));
        Assert.DoesNotContain(rogueSetConfig, RegisteredSetConfigStatements);
        Assert.True(HasExecutorEscape("CALL public.poolai_escape();"));
        foreach (string systemSchemaWrite in new[]
                 {
                     "UPDATE pg_catalog.pg_class SET relname = 'escape';",
                     "UPDATE pg_catalog . pg_class SET relname = 'escape';",
                     "INSERT INTO information_schema.tables(table_name) VALUES ('escape');",
                     "CREATE VIEW pg_catalog.poolai_escape AS SELECT 1;",
                 })
        {
            Assert.True(HasSystemSchemaWrite(systemSchemaWrite));
        }
        Assert.False(HasSystemSchemaWrite(
            "SELECT relation.oid FROM pg_catalog.pg_class AS relation;"));
        Assert.True(HasCommaSeparatedRelation(
            "SELECT q.group_id FROM public.group_token_quotas AS q, public.accounts AS a;"));
        Assert.True(HasCommaSeparatedRelation(
            "SELECT q . group_id FROM public . group_token_quotas AS q, public . accounts AS a;"));

        const string archiveEscape = """
            IF v_status = 'archived' THEN
                SELECT subscription.status
                FROM public.subscriptions AS subscription;
            END IF;
            SELECT escaped.status
            FROM public.subscriptions AS escaped;
            """;
        Assert.False(
            ReferencesTableOnlyInsideIfBlock(
                archiveEscape,
                "subscriptions",
                "if v_status = '' then"));

        const string disabledEscape = """
            IF v_status = 'disabled' THEN
                SELECT subscription.status
                FROM public.subscriptions AS subscription;
            END IF;
            """;
        Assert.False(
            ReferencesTableOnlyInsideLiteralIfBlock(
                disabledEscape,
                "subscriptions",
                "if v_status = 'archived' then"));

        foreach (string unsupportedEntry in new[]
                 {
                     "CREATE PROCEDURE public.poolai_escape() LANGUAGE SQL AS 'SELECT * FROM public.accounts';",
                     "CREATE VIEW public.poolai_escape AS SELECT * FROM public.accounts;",
                     "CREATE MATERIALIZED VIEW public.poolai_escape AS SELECT * FROM public.accounts;",
                     "CREATE RULE poolai_escape AS ON UPDATE TO public.groups DO ALSO SELECT * FROM public.accounts;",
                     "CREATE POLICY poolai_escape ON public.groups USING (EXISTS (SELECT 1 FROM public.accounts));",
                     "CREATE EVENT TRIGGER poolai_escape ON ddl_command_end EXECUTE FUNCTION public.poolai_subscription_update();",
                 })
        {
            Assert.NotEmpty(FindUnsupportedPersistentSqlEntries(unsupportedEntry));
            Assert.ThrowsAny<Exception>(
                () => ExtractFunctionDefinitions("hostile.sql", unsupportedEntry));
        }

        const string quotedFunction = """
            CREATE FUNCTION public."poolai_escape"()
            RETURNS void
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                NULL;
            END;
            $function$;
            """;
        Assert.ThrowsAny<Exception>(
            () => ExtractFunctionDefinitions("quoted.sql", quotedFunction));

        const string spacedQualifiedFunction = """
            CREATE FUNCTION public . poolai_escape()
            RETURNS void
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                NULL;
            END;
            $function$;
            """;
        Assert.ThrowsAny<Exception>(
            () => ExtractFunctionDefinitions("spaced-qualified.sql", spacedQualifiedFunction));

        const string spacedQualifiedTriggerTable = """
            CREATE TRIGGER tr_escape
            BEFORE UPDATE ON public . groups
            FOR EACH ROW
            EXECUTE FUNCTION public.poolai_guard_terminal_status();
            """;
        Assert.ThrowsAny<Exception>(
            () => ExtractTriggerDefinitions("spaced-trigger-table.sql", spacedQualifiedTriggerTable));

        const string spacedQualifiedTriggerFunction = """
            CREATE TRIGGER tr_escape
            BEFORE UPDATE ON public.groups
            FOR EACH ROW
            EXECUTE FUNCTION public . poolai_guard_terminal_status();
            """;
        Assert.ThrowsAny<Exception>(
            () => ExtractTriggerDefinitions("spaced-trigger-function.sql", spacedQualifiedTriggerFunction));

        Dictionary<string, string[]> spacedQualifiedTables = ExtractTableColumns(
            [
                new MigrationSql(
                    "hostile.sql",
                    """
                    CREATE TABLE public . rogue_table (
                        id uuid
                    );
                    """)
            ]);
        Assert.Contains("rogue_table", spacedQualifiedTables.Keys);
        Assert.Equal(["rogue_table"], FindUnownedTables(spacedQualifiedTables.Keys));

        const string alternateDelimiterFunction = """
            CREATE FUNCTION public.poolai_escape()
            RETURNS void
            LANGUAGE plpgsql
            AS $$
            BEGIN
                NULL;
            END;
            $$;
            """;
        Assert.ThrowsAny<Exception>(
            () => ExtractFunctionDefinitions("alternate-delimiter.sql", alternateDelimiterFunction));

        const string crossOwnerTrigger = """
            CREATE TRIGGER tr_cross_owner
            BEFORE UPDATE ON public.groups
            FOR EACH ROW EXECUTE FUNCTION public.poolai_snapshot_subscription_template();
            """;
        Assert.NotEmpty(
            FindInvalidTriggerBindings(
                ExtractTriggerDefinitions("cross-owner.sql", crossOwnerTrigger)));

        const string unknownCalleeTrigger = """
            CREATE TRIGGER tr_unknown_callee
            BEFORE UPDATE ON public.groups
            FOR EACH ROW EXECUTE FUNCTION public.poolai_unknown_trigger();
            """;
        Assert.NotEmpty(
            FindInvalidTriggerBindings(
                ExtractTriggerDefinitions("unknown-callee.sql", unknownCalleeTrigger)));

        const string sameOwnerNewBinding = """
            CREATE TRIGGER tr_unregistered_group_quota_hook
            BEFORE UPDATE ON public.group_token_quotas
            FOR EACH ROW EXECUTE FUNCTION public.poolai_validate_group_activation();
            """;
        Assert.NotEmpty(
            FindInvalidTriggerBindings(
                ExtractTriggerDefinitions("same-owner-new-binding.sql", sameOwnerNewBinding)));

        foreach (string triggerDrift in new[]
                 {
                     "CREATE TRIGGER tr_groups_validate_activation AFTER INSERT OR UPDATE OF status ON public.groups FOR EACH ROW EXECUTE FUNCTION public.poolai_validate_group_activation();",
                     "CREATE TRIGGER tr_groups_validate_activation BEFORE DELETE ON public.groups FOR EACH ROW EXECUTE FUNCTION public.poolai_validate_group_activation();",
                     "CREATE TRIGGER tr_groups_validate_activation BEFORE INSERT OR UPDATE OF status, activation_supply_readiness_token, activation_supply_observed_at ON public.groups FOR EACH STATEMENT EXECUTE FUNCTION public.poolai_validate_group_activation();",
                     "CREATE TRIGGER tr_groups_validate_activation BEFORE INSERT OR UPDATE OF status, activation_supply_readiness_token, activation_supply_observed_at ON public.groups FOR EACH ROW WHEN (NEW.status = 'active') EXECUTE FUNCTION public.poolai_validate_group_activation();",
                     "CREATE CONSTRAINT TRIGGER tr_groups_validate_activation AFTER UPDATE ON public.groups DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.poolai_validate_group_activation();",
                     "CREATE TRIGGER tr_email_outbox_delivery_fence BEFORE UPDATE ON public.email_outbox FOR EACH ROW EXECUTE FUNCTION public.poolai_guard_delivery_fence('lock_owner', 'dead', 'attempts');",
                 })
        {
            Assert.NotEmpty(
                FindInvalidTriggerBindings(
                    ExtractTriggerDefinitions("trigger-drift.sql", triggerDrift)));
        }

        foreach (string triggerMutation in new[]
                 {
                     "DROP TRIGGER tr_groups_validate_activation ON public.groups;",
                     "ALTER TRIGGER tr_groups_validate_activation ON public.groups RENAME TO tr_escape;",
                     "ALTER TABLE public.groups DISABLE TRIGGER tr_groups_validate_activation;",
                 })
        {
            Assert.NotEmpty(FindUnsupportedTriggerMutations(triggerMutation));
        }

        const string dynamicDoBlock = """
            DO $probe$
            BEGIN
                EXECUTE format('CREATE VIEW public.poolai_escape AS SELECT 1');
            END;
            $probe$;
            """;
        DoBlockDefinition hostileDoBlock = Assert.Single(
            ExtractDoBlockDefinitions("hostile.sql", dynamicDoBlock));
        Assert.True(HasExecutorEscape(hostileDoBlock.Body));
        Assert.DoesNotContain(hostileDoBlock.RegistryKey, RegisteredDoBlocks);

        const string sameLineDynamicDoBlock =
            "SELECT 1; DO $probe$ BEGIN EXECUTE format('CREATE VIEW public.poolai_escape AS SELECT 1'); END; $probe$;";
        DoBlockDefinition sameLineHostileDoBlock = Assert.Single(
            ExtractDoBlockDefinitions("same-line-hostile.sql", sameLineDynamicDoBlock));
        Assert.True(HasExecutorEscape(sameLineHostileDoBlock.Body));
        Assert.DoesNotContain(sameLineHostileDoBlock.RegistryKey, RegisteredDoBlocks);
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

        Dictionary<string, string> apiKeys =
            ReadFunctions("0009_identity_api_key_text_validation_m1_e5.sql");
        AssertInOrder(
            NormalizeSql(apiKeys["poolai_api_key_create"]),
            "v_now := pg_catalog.clock_timestamp()",
            "insert into public.api_keys");
        foreach (string function in new[]
                 {
                     "poolai_api_key_update",
                     "poolai_api_key_revoke",
                     "poolai_api_key_rotate",
                 })
        {
            AssertInOrder(
                NormalizeSql(apiKeys[function]),
                "from public.api_keys as current_key",
                "for update",
                "v_now := pg_catalog.clock_timestamp()");
        }
    }

    private static Dictionary<string, string> ReadFunctions(string migration)
    {
        string path = Path.Combine(RepositoryRoot.Find(), "docs", "database", migration);
        return ExtractFunctions(File.ReadAllText(path));
    }

    private static MigrationSql[] ReadMigrations()
    {
        string directory = Path.Combine(RepositoryRoot.Find(), "docs", "database");
        return Directory.GetFiles(directory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(path => new MigrationSql(Path.GetFileName(path), File.ReadAllText(path)))
            .ToArray();
    }

    private static Dictionary<string, string> ExtractFunctions(string sql) =>
        ExtractFunctionDefinitions("inline", sql)
            .ToDictionary(
                static function => function.Name,
                static function => function.Body,
                StringComparer.Ordinal);

    private static FunctionDefinition[] ExtractFunctionDefinitions(string migration, string sql)
    {
        string maskedSql = MaskCommentsAndLiterals(sql);
        string[] unsupportedEntries = FindUnsupportedPersistentSqlEntries(maskedSql, alreadyMasked: true);
        Assert.True(
            unsupportedEntries.Length == 0,
            $"{migration} contains unsupported persistent SQL entries: {string.Join(", ", unsupportedEntries)}.");

        MatchCollection declaredHeaders = Regex.Matches(
            maskedSql,
            @"\bCREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        MatchCollection headers = Regex.Matches(
            maskedSql,
            @"\bCREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:public\.)?(?<name>[a-z_][a-z0-9_]*)\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        Assert.True(
            declaredHeaders.Count == headers.Count,
            $"{migration} contains {declaredHeaders.Count} CREATE FUNCTION header(s), "
            + $"but only {headers.Count} use the supported unquoted public function form.");

        List<FunctionDefinition> functions = [];
        for (int index = 0; index < headers.Count; index++)
        {
            Match header = headers[index];
            Assert.Equal(declaredHeaders[index].Index, header.Index);
            int declarationEnd = index + 1 < declaredHeaders.Count
                ? declaredHeaders[index + 1].Index
                : sql.Length;
            int bodyMarker = maskedSql.IndexOf(
                "AS $function$",
                header.Index + header.Length,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(
                bodyMarker >= 0 && bodyMarker < declarationEnd,
                $"Missing exact $function$ body for {migration}:{header.Groups["name"].Value}.");
            int bodyStart = bodyMarker + "AS $function$".Length;
            int bodyEnd = maskedSql.IndexOf("$function$;", bodyStart, StringComparison.Ordinal);
            Assert.True(
                bodyEnd >= 0 && bodyEnd < declarationEnd,
                $"Missing exact $function$ terminator for {migration}:{header.Groups["name"].Value}.");
            functions.Add(new FunctionDefinition(
                migration,
                header.Groups["name"].Value.ToLowerInvariant(),
                sql[bodyStart..bodyEnd]));
        }

        Assert.Equal(declaredHeaders.Count, functions.Count);
        return functions.ToArray();
    }

    private static TriggerDefinition[] ExtractTriggerDefinitions(string migration, string sql)
    {
        string maskedSql = MaskCommentsAndLiterals(sql);
        string[] unsupportedMutations = FindUnsupportedTriggerMutations(maskedSql, alreadyMasked: true);
        Assert.True(
            unsupportedMutations.Length == 0,
            $"{migration} contains unsupported trigger mutations: {string.Join(", ", unsupportedMutations)}.");
        MatchCollection declaredHeaders = Regex.Matches(
            maskedSql,
            @"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:CONSTRAINT\s+)?TRIGGER\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        MatchCollection definitions = Regex.Matches(
            maskedSql,
            @"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:CONSTRAINT\s+)?TRIGGER\s+(?<name>[a-z_][a-z0-9_]*)\b(?<body>.*?);",
            RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant
                | RegexOptions.Singleline,
            RegexTimeout);
        Assert.True(
            declaredHeaders.Count == definitions.Count,
            $"{migration} contains {declaredHeaders.Count} CREATE TRIGGER header(s), "
            + $"but only {definitions.Count} use the supported unquoted trigger form.");

        List<TriggerDefinition> triggers = [];
        for (int index = 0; index < definitions.Count; index++)
        {
            Match definition = definitions[index];
            Assert.Equal(declaredHeaders[index].Index, definition.Index);
            Match table = Assert.Single(Regex.Matches(
                definition.Groups["body"].Value,
                @"\bON\s+(?:ONLY\s+)?(?:public\.)?(?<name>[a-z_][a-z0-9_]*)\b(?!\s*\.)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout));
            Match function = Assert.Single(Regex.Matches(
                definition.Groups["body"].Value,
                @"\bEXECUTE\s+FUNCTION\s+(?:public\.)?(?<name>poolai_[a-z0-9_]+)\s*\(",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout));
            triggers.Add(new TriggerDefinition(
                migration,
                definition.Groups["name"].Value.ToLowerInvariant(),
                table.Groups["name"].Value.ToLowerInvariant(),
                function.Groups["name"].Value.ToLowerInvariant(),
                NormalizeSqlPreservingLiterals(sql.Substring(definition.Index, definition.Length))));
        }

        Assert.Equal(declaredHeaders.Count, triggers.Count);
        return triggers.ToArray();
    }

    private static DoBlockDefinition[] ExtractDoBlockDefinitions(string migration, string sql)
    {
        string maskedSql = MaskCommentsAndLiterals(sql);
        MatchCollection declaredHeaders = Regex.Matches(
            maskedSql,
            @"(?:\A|;)[ \t\r\n]*DO\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        MatchCollection headers = Regex.Matches(
            maskedSql,
            @"(?:\A|;)[ \t\r\n]*(?<do>DO)\s+(?<delimiter>\$(?:[a-z_][a-z0-9_]*)?\$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        Assert.True(
            declaredHeaders.Count == headers.Count,
            $"{migration} contains {declaredHeaders.Count} DO header(s), "
            + $"but only {headers.Count} use the supported dollar-delimited form.");

        List<DoBlockDefinition> blocks = [];
        for (int index = 0; index < headers.Count; index++)
        {
            Match header = headers[index];
            Assert.Equal(declaredHeaders[index].Index, header.Index);
            int declarationEnd = index + 1 < declaredHeaders.Count
                ? declaredHeaders[index + 1].Index
                : sql.Length;
            string delimiter = header.Groups["delimiter"].Value;
            int bodyStart = header.Index + header.Length;
            int bodyEnd = maskedSql.IndexOf($"{delimiter};", bodyStart, StringComparison.Ordinal);
            Assert.True(
                bodyEnd >= 0 && bodyEnd < declarationEnd,
                $"Missing exact DO terminator for {migration}:{delimiter}.");
            int statementStart = header.Groups["do"].Index;
            int statementEnd = bodyEnd + delimiter.Length + 1;
            string statement = sql[statementStart..statementEnd];
            string digest = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(statement)))
                .ToLowerInvariant();
            blocks.Add(new DoBlockDefinition(
                migration,
                delimiter.ToLowerInvariant(),
                sql[bodyStart..bodyEnd],
                $"{migration}:{delimiter.ToLowerInvariant()}:{digest}"));
        }

        Assert.Equal(declaredHeaders.Count, blocks.Count);
        return blocks.ToArray();
    }

    private static string[] FindInvalidTriggerBindings(IEnumerable<TriggerDefinition> triggers) => triggers
        .Where(IsInvalidTriggerBinding)
        .Select(trigger => $"{trigger.Migration}:{trigger.Name}->{trigger.Table}->{trigger.Function}")
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool IsInvalidTriggerBinding(TriggerDefinition trigger)
    {
        if (!TableOwners.TryGetValue(trigger.Table, out string? tableOwner)
            || !FunctionOwners.TryGetValue(trigger.Function, out string? functionOwner)
            || !RegisteredTriggerStatements.Contains(trigger.Statement, StringComparer.Ordinal))
        {
            return true;
        }

        return !string.Equals(tableOwner, functionOwner, StringComparison.Ordinal)
            && !string.Equals(functionOwner, Technical, StringComparison.Ordinal);
    }

    private static string[] ExtractTriggerStatements(IEnumerable<TriggerDefinition> triggers) => triggers
        .Select(trigger => trigger.Statement)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static Dictionary<string, string[]> ExtractTableColumns(MigrationSql[] migrations) =>
        migrations
            .SelectMany(migration => Regex.Matches(
                    StripCommentsAndLiterals(migration.Sql),
                    @"\bCREATE\s+(?:UNLOGGED\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:public\.)?(?<table>[a-z_][a-z0-9_]*)\s*\((?<body>.*?)^[ \t]*\);",
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant
                        | RegexOptions.Multiline
                        | RegexOptions.Singleline,
                    RegexTimeout)
                .Select(table => new
                {
                    Name = table.Groups["table"].Value.ToLowerInvariant(),
                    Columns = Regex.Matches(
                            table.Groups["body"].Value,
                            @"^ {4}(?!(?:CONSTRAINT|PRIMARY|UNIQUE|CHECK|FOREIGN|EXCLUDE|LIKE)\b)(?<column>[a-z_][a-z0-9_]*)\s+",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline,
                            RegexTimeout)
                        .Select(column => column.Groups["column"].Value.ToLowerInvariant())
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                }))
            .ToDictionary(table => table.Name, table => table.Columns, StringComparer.Ordinal);

    private static RelationReference[] ExtractRelations(string sql)
    {
        string normalized = NormalizeSql(sql);
        HashSet<string> commonTableExpressions = Regex.Matches(
                normalized,
                @"(?:\bwith|,)\s+(?:recursive\s+)?(?<name>[a-z_][a-z0-9_]*)\s+as\s+(?:(?:not\s+)?materialized\s+)?\(",
                RegexOptions.CultureInvariant,
                RegexTimeout)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
        return Regex.Matches(
                normalized,
                @"(?<!distinct )\b(?<verb>from|join|update|insert\s+into|delete\s+from|merge\s+into|truncate(?:\s+table)?)\s+"
                + @"(?:(?:only(?:(?<only_paren>\s*\(\s*)|\s+))|(?:lateral\s+))?"
                + @"(?:(?<schema>[a-z_][a-z0-9_]*(?:\.[a-z_][a-z0-9_]*)*)\.)?"
                + @"(?<name>[a-z_][a-z0-9_]*)\b(?!\s*\.)"
                + @"(?(only_paren)\s*\))",
                RegexOptions.CultureInvariant,
                RegexTimeout)
            .Where(match => !IsTableFunctionReference(normalized, match))
            .Select(match => new RelationReference(
                match.Groups["schema"].Value,
                match.Groups["name"].Value))
            .Where(relation => relation.Schema.Length > 0
                || !commonTableExpressions.Contains(relation.Name))
            .Distinct()
            .OrderBy(static relation => relation.Schema, StringComparer.Ordinal)
            .ThenBy(static relation => relation.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsTableFunctionReference(string sql, Match reference)
    {
        string verb = reference.Groups["verb"].Value;
        if (verb is not ("from" or "join"))
        {
            return false;
        }

        int suffix = reference.Index + reference.Length;
        while (suffix < sql.Length && char.IsWhiteSpace(sql[suffix]))
        {
            suffix++;
        }

        return suffix < sql.Length && sql[suffix] == '(';
    }

    private static string[] FindInvalidRelations(string sql) => ExtractRelations(sql)
        .Where(static relation => !IsAllowedRelation(relation))
        .Select(static relation => relation.Schema.Length == 0
            || string.Equals(relation.Schema, "public", StringComparison.Ordinal)
            ? relation.Name
            : $"{relation.Schema}.{relation.Name}")
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] ExtractOwnedRelations(string sql) => ExtractRelations(sql)
        .Where(static relation => (relation.Schema.Length == 0
                || string.Equals(relation.Schema, "public", StringComparison.Ordinal))
            && TableOwners.ContainsKey(relation.Name))
        .Select(static relation => relation.Name)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool IsAllowedRelation(RelationReference relation) =>
        relation.Schema is "pg_catalog" or "information_schema"
        || ((relation.Schema.Length == 0
                || string.Equals(relation.Schema, "public", StringComparison.Ordinal))
            && TableOwners.ContainsKey(relation.Name));

    private static bool HasQuotedIdentifier(string sql) =>
        StripCommentsAndLiterals(sql).Contains('"', StringComparison.Ordinal);

    private static string[] ExtractFunctionCalls(string sql) => Regex.Matches(
            StripCommentsAndLiterals(sql),
            @"\b(?:public\.)?(?<name>poolai_[a-z0-9_]+)\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout)
        .Select(match => match.Groups["name"].Value.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] ExtractFunctionCallBindings(IEnumerable<FunctionDefinition> functions) => functions
        .SelectMany(function => ExtractFunctionCalls(function.Body)
            .Select(callee => $"{function.Name}->{callee}"))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] FindInvalidFunctionCallBindings(IEnumerable<FunctionDefinition> functions) => functions
        .SelectMany(function => ExtractFunctionCalls(function.Body)
            .Where(callee => !FunctionOwners.TryGetValue(function.Name, out string? callerOwner)
                || !FunctionOwners.TryGetValue(callee, out string? calleeOwner)
                || !string.Equals(callerOwner, calleeOwner, StringComparison.Ordinal)
                || !RegisteredFunctionCallBindings.Contains(
                    $"{function.Name}->{callee}",
                    StringComparer.Ordinal))
            .Select(callee => $"{function.Migration}:{function.Name}->{callee}"))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] ExtractSetConfigStatements(IEnumerable<FunctionDefinition> functions)
    {
        List<string> statements = [];
        foreach (FunctionDefinition function in functions)
        {
            int callCount = Regex.Count(
                StripCommentsAndLiterals(function.Body),
                @"\bset_config\s*\(",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout);
            MatchCollection supportedStatements = Regex.Matches(
                NormalizeSqlPreservingLiterals(function.Body),
                @"\bperform\s+set_config\s*\([^;]*\);",
                RegexOptions.CultureInvariant,
                RegexTimeout);
            Assert.True(
                callCount == supportedStatements.Count,
                $"{function.Migration}:{function.Name} contains an unsupported set_config call shape.");
            statements.AddRange(supportedStatements.Select(
                statement => $"{function.Name}:{statement.Value}"));
        }

        return statements.Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] FindCrossOwnerFunctionCalls(string caller, string sql) =>
        ExtractFunctionCalls(sql)
            .Where(FunctionOwners.ContainsKey)
            .Where(callee => !string.Equals(
                FunctionOwners[caller],
                FunctionOwners[callee],
                StringComparison.Ordinal))
            .Select(callee => $"{caller}->{callee}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string GetEdgeFunction(string edge) =>
        edge[..edge.IndexOf("->", StringComparison.Ordinal)];

    private static string[] FindUnownedTables(IEnumerable<string> tables) => tables
        .Where(table => !TableOwners.ContainsKey(table))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool ContainsTable(string sql, string table) => Regex.IsMatch(
        StripCommentsAndLiterals(sql),
        $@"\b(?:public\.)?{Regex.Escape(table)}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static bool HasCrossContextWrite(string sql, string table) => Regex.IsMatch(
        StripCommentsAndLiterals(sql),
        $@"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO|TRUNCATE(?:\s+TABLE)?)\s+"
        + $@"(?:ONLY\s*\(\s*(?:public\.)?{Regex.Escape(table)}\b\s*\)"
        + $@"|(?:ONLY\s+)?(?:public\.)?{Regex.Escape(table)}\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static bool HasUnsupportedRuntimeSqlShape(string sql) => Regex.IsMatch(
        StripCommentsAndLiterals(sql),
        @"\b(?:ALTER|CREATE|DROP|GRANT|REVOKE|COMMENT|REINDEX|CLUSTER|VACUUM|ANALYZE|REFRESH|CHECKPOINT|DISCARD|LOAD|PREPARE|DEALLOCATE|LISTEN|UNLISTEN|NOTIFY|LOCK)\b"
        + @"|\b(?:COPY|TRUNCATE|MERGE|WITH)\b"
        + @"|\bSECURITY\s+LABEL\b"
        + @"|\bIMPORT\s+FOREIGN\s+SCHEMA\b"
        + @"|(?:\A|;|\bBEGIN|\bTHEN|\bELSE|\bLOOP)\s*(?:SET|RESET|DO)\b"
        + @"|\bTABLE\s+(?:ONLY\s*\(\s*(?:public\.)?[a-z_][a-z0-9_]*\b\s*\)|(?:ONLY\s+)?(?:public\.)?[a-z_][a-z0-9_]*\b)"
        + @"|\bNATURAL\s+(?:(?:INNER|LEFT|RIGHT|FULL)\s+(?:OUTER\s+)?)?JOIN\b"
        + @"|\bDELETE\s+FROM\b[^;]*\bUSING\b"
        + @"|\b(?:FROM|JOIN)\s+(?:ONLY\s*\(\s*(?:public\.)?[a-z_][a-z0-9_]*\b\s*\)|(?:ONLY\s+)?(?:public\.)?[a-z_][a-z0-9_]*\b)\s*\*",
        RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant
            | RegexOptions.Singleline,
        RegexTimeout);

    private static bool HasSystemSchemaWrite(string sql) => Regex.IsMatch(
        StripCommentsAndLiterals(sql),
        @"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO|TRUNCATE(?:\s+TABLE)?|COPY)\s+"
        + @"(?:(?:ONLY\s*\(\s*)|(?:ONLY\s+))?(?:pg_catalog|information_schema)\s*\."
        + @"|\b(?:ALTER|CREATE|DROP)\b[^;]*\b(?:pg_catalog|information_schema)\s*\.",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
        RegexTimeout);

    private static bool HasCommaSeparatedRelation(string sql) => Regex.IsMatch(
        StripCommentsAndLiterals(sql),
        @"\b(?:FROM|JOIN)\s+(?:ONLY\s*\(\s*(?:public\.)?[a-z_][a-z0-9_]*\b\s*\)|(?:ONLY\s+)?(?:public\.)?[a-z_][a-z0-9_]*\b)"
        + @"(?:\s+(?:AS\s+)?[a-z_][a-z0-9_]*)?\s*,"
        + @"|\)\s+(?:AS\s+)?[a-z_][a-z0-9_]*\s*,",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static string[] FindUnsupportedPersistentSqlEntries(
        string sql,
        bool alreadyMasked = false) => Regex.Matches(
            alreadyMasked ? sql : MaskCommentsAndLiterals(sql),
            @"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:TEMP|TEMPORARY)\s+)?(?:RECURSIVE\s+)?(?<kind>PROCEDURE|MATERIALIZED\s+VIEW|VIEW|RULE|POLICY|EVENT\s+TRIGGER)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout)
        .Select(match => CollapseWhitespace(match.Groups["kind"].Value).ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] FindUnsupportedTriggerMutations(
        string sql,
        bool alreadyMasked = false) => Regex.Matches(
            alreadyMasked ? sql : MaskCommentsAndLiterals(sql),
            @"\b(?:DROP|ALTER)\s+TRIGGER\b"
            + @"|\bALTER\s+TABLE\b[^;]*\b(?:ENABLE|DISABLE)\s+TRIGGER\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
            RegexTimeout)
        .Select(match => CollapseWhitespace(match.Value).ToLowerInvariant())
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool HasExecutorEscape(string sql) => Regex.IsMatch(
        StripCommentsAndLiterals(sql),
        @"\b(?:EXECUTE|CALL)\b|\bdblink(?:_[a-z0-9_]+)?\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static string[] ExtractForLockModes(string sql) => Regex.Matches(
            StripCommentsAndLiterals(sql),
            @"\bFOR\s+(?<mode>NO\s+KEY\s+UPDATE|KEY\s+SHARE|UPDATE|SHARE)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout)
        .Select(match => Regex.Replace(
            match.Groups["mode"].Value,
            @"\s+",
            " ",
            RegexOptions.CultureInvariant,
            RegexTimeout).ToLowerInvariant())
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool HasTableOrAdvisoryLock(string sql) => Regex.IsMatch(
        StripCommentsAndLiterals(sql),
        @"\bLOCK\s+(?:TABLE\s+)?(?:ONLY\s+)?(?:public\.)?[a-z_][a-z0-9_]*\b|\bpg_(?:try_)?advisory_(?:xact_)?lock(?:_shared)?\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static bool HasRowLock(string sql) =>
        ExtractForLockModes(sql).Length > 0 || HasTableOrAdvisoryLock(sql);

    private static bool HasBareStar(string sql) => Regex.IsMatch(
        StripCommentsAndLiterals(sql),
        @"(?<!\.)\*",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static string[] FindBareTableColumns(string sql, string[] tableColumns)
    {
        string stripped = StripCommentsAndLiterals(sql);
        return tableColumns
            .Concat(PostgreSqlSystemColumns)
            .Distinct(StringComparer.Ordinal)
            .Where(column => Regex.IsMatch(
                stripped,
                $@"(?<!\.)\b{Regex.Escape(column)}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] FindTableAliases(string sql, string table) => Regex.Matches(
            StripCommentsAndLiterals(sql),
            $@"\b(?:FROM|JOIN)\s+(?:ONLY\s*\(\s*(?:public\.)?{Regex.Escape(table)}\b\s*\)"
            + $@"|(?:ONLY\s+)?(?:public\.)?{Regex.Escape(table)}\b)"
            + @"\s+(?:AS\s+)?(?<alias>[a-z_][a-z0-9_]*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout)
        .Select(match => match.Groups["alias"].Value.ToLowerInvariant())
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool HasTableAliasColumnList(string sql, string table) => Regex.IsMatch(
        StripCommentsAndLiterals(sql),
        $@"\b(?:FROM|JOIN)\s+(?:ONLY\s*\(\s*(?:public\.)?{Regex.Escape(table)}\b\s*\)"
        + $@"|(?:ONLY\s+)?(?:public\.)?{Regex.Escape(table)}\b)\s+"
        + @"(?:AS\s+)?[a-z_][a-z0-9_]*\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static bool HasWholeRowAliasUsage(string sql, string table, string alias)
    {
        string stripped = StripCommentsAndLiterals(sql);
        stripped = Regex.Replace(
            stripped,
            $@"\b(?:FROM|JOIN)\s+(?:ONLY\s*\(\s*(?:public\.)?{Regex.Escape(table)}\b\s*\)"
            + $@"|(?:ONLY\s+)?(?:public\.)?{Regex.Escape(table)}\b)\s+"
            + $@"(?:AS\s+)?{Regex.Escape(alias)}\b",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        stripped = Regex.Replace(
            stripped,
            $@"\b{Regex.Escape(alias)}\s*\.",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        stripped = Regex.Replace(
            stripped,
            $@"\bFOR\s+(?:NO\s+KEY\s+UPDATE|KEY\s+SHARE|UPDATE|SHARE)\s+OF\s+"
            + $@"(?:[a-z_][a-z0-9_]*\s*,\s*)*{Regex.Escape(alias)}\b",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        return Regex.IsMatch(
            stripped,
            $@"\b{Regex.Escape(alias)}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
    }

    private static string[] FindFieldsForAlias(string sql, string alias) => Regex.Matches(
            StripCommentsAndLiterals(sql),
            $@"\b{Regex.Escape(alias)}\.(?<column>\*|[a-z_][a-z0-9_]*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout)
        .Select(match => match.Groups["column"].Value.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] FindFieldsForTableAlias(string sql, string table, string alias) =>
        FindTableReadScopes(sql, table)
            .SelectMany(scope => FindFieldsForAlias(scope, alias))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] FindTableReadScopes(string sql, string table)
    {
        string stripped = StripCommentsAndLiterals(sql);
        MatchCollection references = Regex.Matches(
            stripped,
            $@"\b(?:FROM|JOIN)\s+(?:ONLY\s*\(\s*(?:public\.)?{Regex.Escape(table)}\b\s*\)"
            + $@"|(?:ONLY\s+)?(?:public\.)?{Regex.Escape(table)}\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        return references
            .Select(reference =>
            {
                int start = stripped.LastIndexOf(';', reference.Index) + 1;
                int terminator = stripped.IndexOf(';', reference.Index);
                int end = terminator < 0 ? stripped.Length : terminator + 1;
                return stripped[start..end];
            })
            .ToArray();
    }

    private static bool ReferencesTableOnlyInsideIfBlock(
        string sql,
        string table,
        string normalizedCondition) => ReferencesTableOnlyInsideNormalizedIfBlock(
            NormalizeSql(sql),
            table,
            normalizedCondition);

    private static bool ReferencesTableOnlyInsideLiteralIfBlock(
        string sql,
        string table,
        string normalizedCondition) => ReferencesTableOnlyInsideNormalizedIfBlock(
            NormalizeSqlPreservingLiterals(sql),
            table,
            normalizedCondition);

    private static bool ReferencesTableOnlyInsideNormalizedIfBlock(
        string normalizedSql,
        string table,
        string normalizedCondition)
    {
        if (!TryExtractIfBlock(normalizedSql, normalizedCondition, out string block, out string outside))
        {
            return false;
        }

        return ContainsTable(block, table) && !ContainsTable(outside, table);
    }

    private static bool TryExtractIfBlock(
        string normalizedSql,
        string normalizedCondition,
        out string block,
        out string outside)
    {
        int start = normalizedSql.IndexOf(normalizedCondition, StringComparison.Ordinal);
        if (start < 0)
        {
            block = string.Empty;
            outside = normalizedSql;
            return false;
        }

        MatchCollection tokens = Regex.Matches(
            normalizedSql[start..],
            @"\bend\s+if\b|\bif\b",
            RegexOptions.CultureInvariant,
            RegexTimeout);
        int depth = 0;
        foreach (Match token in tokens)
        {
            if (token.Value.StartsWith("end", StringComparison.Ordinal))
            {
                depth--;
                if (depth == 0)
                {
                    int end = start + token.Index + token.Length;
                    block = normalizedSql[start..end];
                    outside = string.Concat(normalizedSql.AsSpan(0, start), " ", normalizedSql.AsSpan(end));
                    return true;
                }
            }
            else
            {
                depth++;
            }
        }

        block = string.Empty;
        outside = normalizedSql;
        return false;
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

    private static string NormalizeSql(string sql) =>
        CollapseWhitespace(StripCommentsAndLiterals(sql)).ToLowerInvariant();

    private static string NormalizeSqlPreservingLiterals(string sql)
    {
        string collapsed = CollapseWhitespace(StripComments(sql));
        return Regex.Replace(
            collapsed,
            @"'(?:''|[^'])*'|[^']+",
            static match => match.Value[0] == '\''
                ? match.Value
                : match.Value.ToLowerInvariant(),
            RegexOptions.CultureInvariant,
            RegexTimeout);
    }

    private static string CollapseWhitespace(string sql) => Regex.Replace(
        sql,
        @"\s+",
        " ",
        RegexOptions.CultureInvariant,
        RegexTimeout).Trim();

    private static string StripCommentsAndLiterals(string sql)
    {
        string stripped = Regex.Replace(
            StripComments(sql),
            @"'(?:''|[^'])*'",
            "''",
            RegexOptions.CultureInvariant | RegexOptions.Singleline,
            RegexTimeout);
        string normalizedIdentifiers = Regex.Replace(
            stripped,
            @"\b[a-z_][a-z0-9_]*(?:\s*\.\s*[a-z_][a-z0-9_]*)+\b",
            static match => Regex.Replace(
                match.Value,
                @"\s*\.\s*",
                ".",
                RegexOptions.CultureInvariant,
                RegexTimeout),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        return Regex.Replace(
            normalizedIdentifiers,
            @"\b(?<qualifier>[a-z_][a-z0-9_]*(?:\.[a-z_][a-z0-9_]*)*)\s*\.\s*\*",
            "${qualifier}.*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
    }

    private static string MaskCommentsAndLiterals(string sql)
    {
        string masked = Regex.Replace(
            sql,
            @"/\*.*?\*/",
            static match => new string(' ', match.Length),
            RegexOptions.CultureInvariant | RegexOptions.Singleline,
            RegexTimeout);
        masked = Regex.Replace(
            masked,
            @"--.*?$",
            static match => new string(' ', match.Length),
            RegexOptions.CultureInvariant | RegexOptions.Multiline,
            RegexTimeout);
        return Regex.Replace(
            masked,
            @"'(?:''|[^'])*'",
            static match => new string(' ', match.Length),
            RegexOptions.CultureInvariant | RegexOptions.Singleline,
            RegexTimeout);
    }

    private static string StripComments(string sql)
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
        return withoutLines;
    }

    private sealed record RegisteredAccess(string Alias, string[] Fields);

    private sealed record MigrationSql(string Name, string Sql);

    private sealed record FunctionDefinition(string Migration, string Name, string Body);

    private sealed record TriggerDefinition(
        string Migration,
        string Name,
        string Table,
        string Function,
        string Statement);

    private sealed record DoBlockDefinition(
        string Migration,
        string Delimiter,
        string Body,
        string RegistryKey);

    private sealed record RelationReference(string Schema, string Name);
}
#pragma warning restore MA0051
