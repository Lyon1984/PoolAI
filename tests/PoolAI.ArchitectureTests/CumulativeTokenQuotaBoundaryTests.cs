using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoolAI.ArchitectureTests;

public sealed partial class CumulativeTokenQuotaBoundaryTests
{
    private static readonly string[] CanonicalOpenApiAuthorityProperties =
    [
        "total_tokens",
        "consumed_tokens",
        "reserved_tokens",
        "remaining_tokens",
        "overage_tokens",
    ];

    private static readonly string[] CanonicalCSharpAuthorityMembers =
    [
        "TotalTokens",
        "ConsumedTokens",
        "ReservedTokens",
        "RemainingTokens",
        "OverageTokens",
    ];

    private static readonly string[] ApprovedGroupProjectionAuthorityMembers =
    [
        "QuotaStatus",
        "TotalTokens",
        "ConsumedTokens",
        "ReservedTokens",
        "RemainingTokens",
    ];

    private static readonly string[] ApprovedDatabaseStatisticShape =
    [
        "group_id",
        "account_id",
        "period_id",
        "bucket_start",
        "input_tokens",
        "output_tokens",
        "total_tokens",
    ];

    private static readonly string[] ApprovedOpenApiStatisticShape =
    [
        "starts_at",
        "input_tokens",
        "output_tokens",
        "total_tokens",
    ];

    private static readonly string[] ApprovedOpenApiReportShape =
    [
        "account_id",
        "from",
        "to",
        "granularity",
        "data",
    ];

    private static readonly string[] ApprovedProductionStatisticShape =
    [
        "StartsAt",
        "InputTokens",
        "OutputTokens",
        "TotalTokens",
    ];

    private static readonly string[] ApprovedProductionReportShape =
    [
        "AccountId",
        "From",
        "To",
        "Granularity",
        "Data",
    ];

    private static readonly string[] ApprovedAccountUsageProjectionShape =
    [
        "AccountId",
        "Aggregate",
    ];

    private static readonly string[] ApprovedUsageHourProjectionShape =
    [
        "GroupId",
        "PeriodId",
        "BucketStart",
        "Group",
        "Accounts",
        "_accounts",
    ];

    private static readonly string[] ApprovedPoolUsageQuotaPaths =
    [
        "quota:total_tokens",
        "quota:consumed_tokens",
        "quota:reserved_tokens",
        "quota:remaining_tokens",
        "quota:overage_tokens",
    ];

    private static readonly string[] DataTransferTypeMarkers =
    [
        "Actor",
        "Binding",
        "Candidate",
        "Command",
        "Configuration",
        "Dto",
        "Envelope",
        "Options",
        "Outcome",
        "Page",
        "Payload",
        "Point",
        "Profile",
        "Report",
        "Request",
        "Resource",
        "Response",
        "Result",
        "Route",
        "Snapshot",
        "State",
        "Transition",
        "View",
        "Wire",
        "Write",
    ];

    [Fact]
    public void OnlyGroupDefinesCumulativeTokenQuota()
    {
        string root = RepositoryRoot.Find();
        List<string> violations = [];

        AssertOpenApiBoundary(root, violations);
        AssertPostgresCatalogBoundary(root, violations);
        AssertProductionDtoBoundary(root, violations);
        AssertConfigurationBoundary(root, violations);

        Assert.True(
            violations.Count == 0,
            "Only GroupQuota may define cumulative Token quota authority. "
            + "Token statistics, authentication tokens, Group identifiers and request safety "
            + "limits are deliberately ignored. Violations:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("TotalTokens")]
    [InlineData("total_tokens")]
    [InlineData("UserTotalTokens")]
    [InlineData("MaxTokens")]
    [InlineData("MaximumTokens")]
    [InlineData("MonthlyTokens")]
    [InlineData("CumulativeTokens")]
    [InlineData("UsedTokens")]
    [InlineData("TokensPerMonth")]
    [InlineData("TokenCapacity")]
    [InlineData("UserQuota")]
    public void CumulativeQuotaAuthorityClassifierRejectsTotalTokenAliases(
        string identifier)
    {
        Assert.True(IsCumulativeQuotaAuthorityMember(identifier));
    }

    [Theory]
    [InlineData("InputTokens")]
    [InlineData("OutputTokens")]
    [InlineData("ThinkingTokens")]
    [InlineData("TokenHash")]
    [InlineData("TokenVersion")]
    [InlineData("UserRefreshTokenCount")]
    [InlineData("InvalidUserToken")]
    [InlineData("OwnerToken")]
    [InlineData("MaxConcurrency")]
    [InlineData("GroupQuotaId")]
    public void CumulativeQuotaAuthorityClassifierAllowsNonAuthorityTokenTerms(
        string identifier)
    {
        Assert.False(IsCumulativeQuotaAuthorityMember(identifier));
    }

    [Fact]
    public void GroupContextCannotHidePersonalOrAlternateQuotaAuthorities()
    {
        Assert.True(IsForbiddenPersonalAuthorityMember("BudgetState", "TotalTokens"));
        Assert.True(IsForbiddenPersonalAuthorityMember("RuntimeLimits", "TotalTokens"));
        Assert.True(IsForbiddenPersonalAuthorityMember("NeutralState", "TotalTokens"));
        Assert.True(IsForbiddenPersonalAuthorityMember("Entitlement", "TokenCapacity"));
        Assert.True(IsForbiddenPersonalAuthorityMember("Settings", "TotalTokens"));
        Assert.True(IsForbiddenPersonalAuthorityMember("UserGroupQuota", "TotalTokens"));
        Assert.True(IsForbiddenPersonalAuthorityMember(
            "Group:UserQuota",
            "TotalTokens"));
        Assert.False(IsForbiddenPersonalAuthorityMember("GroupQuota", "TotalTokens"));
        Assert.False(IsForbiddenPersonalAuthorityMember("UsageAttempt", "TotalTokens"));

        OpenApiSchemaShape poolUsage = new(
            new HashSet<string>(["user_total_tokens"], StringComparer.Ordinal),
            new HashSet<string>(["user_total_tokens"], StringComparer.Ordinal));
        Assert.True(IsForbiddenOpenApiPersonalAuthorityPath(
            "PoolUsage",
            "user_total_tokens"));
        Assert.False(IsApprovedOpenApiGroupQuotaAuthority(
            "PoolUsage",
            "user_total_tokens",
            poolUsage));

        Assert.True(IsPersonalQuotaConfigurationKey("Quota:TotalTokens"));
        Assert.True(IsPersonalQuotaConfigurationKey("Quota:BudgetTokens"));
        Assert.True(IsPersonalQuotaConfigurationKey("Quota:UserTotalTokens"));
        Assert.False(IsPersonalQuotaConfigurationKey("Quota:MaxTotalTokens"));
    }

    [Fact]
    public void DatabaseCatalogParserDoesNotIgnoreNewPersonalQuotaTables()
    {
        Dictionary<string, HashSet<string>> tables = new(StringComparer.Ordinal);
        const string sql = """
            CREATE TABLE "public"."user_token_quotas" (
                "user_id" uuid PRIMARY KEY,
                "total_tokens" numeric(78, 0) NOT NULL
            );
            """;

        AddCreateTableColumns(MaskSqlCommentsAndLiterals(sql), tables);

        Assert.Contains("user_token_quotas", tables.Keys);
        Assert.Contains("total_tokens", tables["user_token_quotas"]);
        Assert.True(IsPersonalQuotaSubject("user_token_quotas"));
        Assert.True(IsCumulativeQuotaAuthorityMember("user_token_quotas"));
        Assert.True(IsPersonalQuotaSubject("users"));
        Assert.True(IsPersonalQuotaSubject("accounts"));
        Assert.True(IsPersonalQuotaSubject("subscriptions"));
        Assert.True(IsPersonalQuotaSubject("api_keys"));
        Assert.True(IsForbiddenPersonalAuthorityMember("settings", "user_total_tokens"));
    }

    [Fact]
    public void DatabaseCatalogParserAppliesColumnRenameAndDropInMigrationOrder()
    {
        Dictionary<string, HashSet<string>> tables = new(StringComparer.Ordinal)
        {
            ["accounts"] = new HashSet<string>(["id", "settings"], StringComparer.Ordinal),
            ["account_usage_hourly"] = new HashSet<string>(
                ApprovedDatabaseStatisticShape,
                StringComparer.Ordinal),
        };
        const string sql = """
            ALTER TABLE "public"."accounts"
                RENAME COLUMN "settings" TO "total_tokens";
            ALTER TABLE public.account_usage_hourly
                DROP COLUMN input_tokens;
            """;

        AddAlterTableColumns(MaskSqlCommentsAndLiterals(sql), tables);

        Assert.DoesNotContain("settings", tables["accounts"]);
        Assert.Contains("total_tokens", tables["accounts"]);
        Assert.True(IsForbiddenPersonalAuthorityMember("accounts", "total_tokens"));
        Assert.DoesNotContain("input_tokens", tables["account_usage_hourly"]);
        Assert.False(IsApprovedDatabaseTokenStatistic(
            "account_usage_hourly",
            "total_tokens",
            tables["account_usage_hourly"]));
    }

    [Fact]
    public void DatabaseCatalogParserPreservesShapeForKnownColumnDefaultChanges()
    {
        Dictionary<string, HashSet<string>> tables = new(StringComparer.Ordinal)
        {
            ["groups"] = new HashSet<string>(["id", "runtime_policy"], StringComparer.Ordinal),
        };
        const string sql = """
            ALTER TABLE public.groups
                ALTER COLUMN runtime_policy
                SET DEFAULT '{"schema_version":1,"requests_per_minute":6000}'::jsonb;
            ALTER TABLE public.groups
                VALIDATE CONSTRAINT ck_groups_runtime_policy_m4_e1;
            """;

        AddAlterTableColumns(MaskSqlCommentsAndLiterals(sql), tables);

        Assert.Equal(
            ["id", "runtime_policy"],
            tables["groups"].Order(StringComparer.Ordinal));
        Assert.ThrowsAny<Exception>(() => AddAlterTableColumns(
            "ALTER TABLE public.groups ALTER COLUMN unknown_policy SET DEFAULT 1;",
            tables));
    }

    [Fact]
    public void DatabaseCatalogParserPreservesSchemaStatementOrderAndQuotedNames()
    {
        Dictionary<string, HashSet<string>> tables = new(StringComparer.Ordinal)
        {
            ["accounts"] = new HashSet<string>(["id", "settings"], StringComparer.Ordinal),
        };
        const string sql = """
            ALTER TABLE accounts RENAME settings TO "TotalTokens";
            ALTER TABLE accounts RENAME TO legacy_identity;
            CREATE TABLE accounts ("TotalTokens" numeric(78, 0));
            CREATE TABLE shadow.account_usage_hourly (total_tokens numeric(78, 0));
            CREATE TABLE "UserTokenQuotas" (id uuid);
            """;

        AddDatabaseTableColumns(MaskSqlCommentsAndLiterals(sql), tables);

        Assert.Contains("quoted__total_tokens", tables["legacy_identity"]);
        Assert.Contains("quoted__total_tokens", tables["accounts"]);
        Assert.Contains("shadow.account_usage_hourly", tables.Keys);
        Assert.False(IsApprovedDatabaseTokenStatistic(
            "shadow.account_usage_hourly",
            "total_tokens",
            tables["shadow.account_usage_hourly"]));
        Assert.Contains("quoted__user_token_quotas", tables.Keys);
        Assert.True(IsPersonalQuotaSubject("quoted__user_token_quotas"));
    }

    [Fact]
    public void DatabaseCatalogParserFailsClosedOnUnmodeledPersistentDdl()
    {
        const string ctas = "CREATE TABLE user_token_quotas AS SELECT 1 AS total_tokens;";
        const string view = "CREATE VIEW user_quota AS SELECT 1 AS total_tokens;";
        const string drop = "DROP TABLE account_usage_hourly;";
        const string like =
            "CREATE TABLE account_budget (LIKE group_quota_periods INCLUDING ALL);";

        Assert.ThrowsAny<Exception>(() => AddDatabaseTableColumns(
            MaskSqlCommentsAndLiterals(ctas),
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)));
        Assert.ThrowsAny<Exception>(() => AddDatabaseTableColumns(
            MaskSqlCommentsAndLiterals(view),
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)));
        Assert.ThrowsAny<Exception>(() => AddDatabaseTableColumns(
            MaskSqlCommentsAndLiterals(drop),
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)));
        Assert.ThrowsAny<Exception>(() => AddDatabaseTableColumns(
            MaskSqlCommentsAndLiterals(like),
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)));
    }

    [Fact]
    public void ProductionTypeParserIncludesInterfacesAndDtoSuffixes()
    {
        CSharpTypeShape[] shapes = ReadCSharpBoundaryProbe();

        CSharpTypeShape quotaInterface = FindShape(shapes, "IUserQuota");
        Assert.True(quotaInterface.IsDataTransferType);
        Assert.Contains("TotalTokens", quotaInterface.Members);
        Assert.True(IsPersonalQuotaSubject(quotaInterface.Name));

        CSharpTypeShape dto = FindShape(shapes, "UserDto");
        Assert.True(dto.IsDataTransferType);
        Assert.Contains("TotalTokens", dto.Members);
        Assert.Contains("Limits:MaximumTokens", dto.MemberPaths);
        Assert.Contains("Limits:TotalTokens", dto.MemberPaths);
        Assert.Contains("Limits:TokenCapacity", dto.MemberPaths);
        Assert.True(IsPersonalQuotaSubject(dto.Name));
        Assert.True(IsForbiddenPersonalAuthorityMember("RuntimeOptions", "UserTotalTokens"));

        Assert.Contains("MaximumTokens", FindShape(shapes, "InheritedUserDto").Members);
        Assert.Contains("MaximumTokens", FindShape(shapes, "PositionalUserDto").Members);
        Assert.Contains("UserTotalTokens", dto.Members);
        Assert.Contains("TupleMaximumTokens", dto.Members);
        Assert.Contains("SpentTokens", dto.Members);
        Assert.Contains("UsedTokens", dto.Members);
        Assert.Contains("CumulativeTokens", FindShape(shapes, "User").Members);
        CSharpTypeShape runtimeLimits = FindShape(shapes, "RuntimeLimitsDto");
        Assert.Contains("TotalTokens", runtimeLimits.Members);
        Assert.True(IsForbiddenPersonalAuthorityMember(
            runtimeLimits.Name,
            "TotalTokens"));
    }

    [Fact]
    public void ProductionTypeParserCapturesTupleMembersAndRequiresGroupIdentityProof()
    {
        CSharpTypeShape[] shapes = ReadCSharpBoundaryProbe();
        CSharpTypeShape positionalTuple = FindShape(shapes, "PositionalTupleDto");
        Assert.Contains("TotalTokens", positionalTuple.Members);
        CSharpTypeShape genericTuple = FindShape(shapes, "GenericTupleDto");
        Assert.Contains("TotalTokens", genericTuple.Members);

        CSharpTypeShape nestedGroupQuota = FindShape(shapes, "NestedGroupQuotaDto");
        Assert.Contains("Identity:GroupId", nestedGroupQuota.MemberPaths);
        Assert.True(HasDirectOrNestedGroupIdentity(nestedGroupQuota));
        Assert.False(IsForbiddenPersonalAuthorityMember(
            $"Group:{nestedGroupQuota.Name}",
            "TotalTokens"));

        CSharpTypeShape userGroupQuota = FindShape(shapes, "UserGroupQuota");
        Assert.False(HasDirectOrNestedGroupIdentity(userGroupQuota));
        Assert.True(IsForbiddenPersonalAuthorityMember(
            userGroupQuota.Name,
            "TotalTokens"));
    }

    private static CSharpTypeShape[] ReadCSharpBoundaryProbe()
    {
        const string source = """
            public interface IUserQuota
            {
                long TotalTokens { get; }
            }

            public sealed class UserDto
            {
                public long TotalTokens { get; init; }
                public QuotaFields Limits { get; init; }
                public long Marker = 0, UserTotalTokens = 100;
                public (long Value, bool Enabled) TupleMaximumTokens { get; init; }
                public long SpentTokens { private get; set; }
                public long UsedTokens { [System.Obsolete] get; init; }
            }

            public class QuotaFields
            {
                public long MaximumTokens { get; init; }
                public long TotalTokens { set; }
                public long TokenCapacity, Marker;
            }

            public sealed class InheritedUserDto : QuotaFields
            {
            }

            public sealed record PositionalUserDto(string Name) : QuotaFields(Name)
            {
            }

            public sealed class @User
            {
                public long CumulativeTokens { get; init; }
            }

            public sealed class RuntimeLimitsDto
            {
                public (long TotalTokens, bool Enabled) Values { get; init; }
            }

            public sealed record PositionalTupleDto((long TotalTokens, bool Enabled) Snapshot);
            public sealed record GenericTupleDto(IReadOnlyList<(long TotalTokens, bool Enabled)> Snapshots);
            public sealed record GroupIdentity(Guid GroupId);
            public sealed record NestedGroupQuotaDto(GroupIdentity Identity, long TotalTokens);
            public sealed record UserGroupQuota(Guid GroupId, long TotalTokens);
            """;

        return ResolveCSharpInheritedMembers(
            ReadCSharpTypeShapes(
                "/repository",
                "/repository/src/QuotaBoundaryProbe.cs",
                source).ToArray());
    }

    [Fact]
    public void ProductionTypeParserPreservesQualifiedAndAliasedReferenceTargets()
    {
        const string ownerSource = """
            using AliasQuota = Quota.Other.QuotaFields;
            namespace Quota.Owner;

            public sealed class UserDto
            {
                public AliasQuota Aliased { get; init; }
                public Quota.Other.QuotaFields Qualified { get; init; }
            }

            public sealed class QuotaFields
            {
                public string DisplayName { get; init; }
            }
            """;
        const string quotaSource = """
            namespace Quota.Other;

            public sealed class QuotaFields
            {
                public long TotalTokens { get; init; }
            }
            """;
        CSharpTypeShape[] shapes = ResolveCSharpInheritedMembers(
            ReadCSharpTypeShapes("/repo", "/repo/src/Owner.cs", ownerSource)
                .Concat(ReadCSharpTypeShapes(
                    "/repo",
                    "/repo/src/Quota.cs",
                    quotaSource))
                .ToArray());

        CSharpTypeShape owner = FindShape(shapes, "UserDto");
        Assert.Contains("Aliased:TotalTokens", owner.MemberPaths);
        Assert.Contains("Qualified:TotalTokens", owner.MemberPaths);
    }

    [Fact]
    public void ProductionTypeParserFailsClosedOnUnsupportedAliasAndEscapedIdentifiers()
    {
        const string globalAlias = "global using Quota = Other.QuotaFields;";
        const string constructedAlias = "using Quota = Other.QuotaFields<long>;";
        const string escapedIdentifier =
            "public sealed class User { public long Total\\u0054okens { get; init; } }";

        Assert.ThrowsAny<Exception>(() => ReadCSharpTypeShapes(
            "/repo",
            "/repo/src/GlobalAlias.cs",
            globalAlias).ToArray());
        Assert.ThrowsAny<Exception>(() => ReadCSharpTypeShapes(
            "/repo",
            "/repo/src/ConstructedAlias.cs",
            constructedAlias).ToArray());
        Assert.ThrowsAny<Exception>(() => ReadCSharpTypeShapes(
            "/repo",
            "/repo/src/EscapedIdentifier.cs",
            escapedIdentifier).ToArray());
    }

    private static CSharpTypeShape FindShape(
        CSharpTypeShape[] shapes,
        string name) =>
        Assert.Single(
            shapes,
            shape => string.Equals(
                shape.Name,
                name,
                StringComparison.Ordinal));

    [Fact]
    public void OpenApiParserIncludesComposedSchemaProperties()
    {
        const string openApi = """
            components:
              schemas:
                User:
                  allOf:
                    - $ref: '#/components/schemas/UserBase'
                    - type: object
                      properties:
                        total_tokens:
                          type: integer
            """;

        Dictionary<string, OpenApiSchemaShape> schemas =
            ReadOpenApiSchemaProperties(openApi);

        Assert.Contains("total_tokens", schemas["User"].Properties);
        Assert.True(IsCumulativeQuotaAuthorityMember("total_tokens"));
    }

    [Fact]
    public void OpenApiParserIncludesAllOfReferencedAndNestedProperties()
    {
        const string openApi = """
            components:
              schemas:
                UserQuotaFields:
                  type: object
                  properties:
                    maximum_tokens:
                      type: integer
                User:
                  allOf:
                    - $ref: '#/components/schemas/UserQuotaFields'
                    - type: object
                      properties:
                        profile:
                          type: object
                          properties:
                            remaining_tokens:
                              type: integer
                        budget_tokens:
                          type: integer
            """;

        Dictionary<string, OpenApiSchemaShape> schemas =
            ReadOpenApiSchemaProperties(openApi);

        Assert.Contains("maximum_tokens", schemas["User"].Properties);
        Assert.Contains("remaining_tokens", schemas["User"].Properties);
        Assert.Contains("budget_tokens", schemas["User"].Properties);
        Assert.Contains("maximum_tokens", schemas["User"].TopLevelProperties);
        Assert.Contains("budget_tokens", schemas["User"].TopLevelProperties);
        Assert.DoesNotContain("remaining_tokens", schemas["User"].TopLevelProperties);
    }

    [Fact]
    public void OpenApiParserResolvesDirectSchemaReferencesWithSiblingsAndCycles()
    {
        const string openApi = """
            components:
              schemas:
                UserProfile:
                  $ref: '#/components/schemas/TokenBudget'
                  properties:
                    display_name:
                      type: string
                TokenBudget:
                  $ref: '#/components/schemas/UserProfile'
                  properties:
                    maximum_tokens:
                      type: integer
            """;

        Dictionary<string, OpenApiSchemaShape> schemas =
            ReadOpenApiSchemaProperties(openApi);

        Assert.Contains("maximum_tokens", schemas["UserProfile"].Properties);
        Assert.Contains("maximum_tokens", schemas["UserProfile"].TopLevelProperties);
        Assert.Contains("display_name", schemas["TokenBudget"].Properties);
        Assert.Contains("display_name", schemas["TokenBudget"].TopLevelProperties);
    }

    [Fact]
    public void OpenApiParserSupportsQuotedHyphenatedKeysAndRejectsFlowMappings()
    {
        const string quoted = """
            components:
              schemas:
                "User-Profile":
                  type: object
                  properties:
                    "maximum-tokens":
                      type: integer
            """;

        Dictionary<string, OpenApiSchemaShape> schemas =
            ReadOpenApiSchemaProperties(quoted);
        Assert.Contains("maximum-tokens", schemas["User-Profile"].PropertyPaths);
        Assert.True(IsForbiddenOpenApiPersonalAuthorityPath(
            "User-Profile",
            "maximum-tokens"));

        const string flow = """
            components:
              schemas:
                User: { type: object, properties: { total_tokens: { type: integer } } }
            """;
        Assert.ThrowsAny<Exception>(() => ReadOpenApiSchemaProperties(flow));
    }

    [Fact]
    public void OpenApiParserDoesNotTreatNegativeReferencesAsStatisticShape()
    {
        const string source = """
            components:
              schemas:
                UsageStatisticFields:
                  type: object
                  properties:
                    starts_at: { type: string }
                    input_tokens: { type: string }
                    output_tokens: { type: string }
                AccountUsagePoint:
                  type: object
                  not:
                    $ref: '#/components/schemas/UsageStatisticFields'
                  properties:
                    total_tokens:
                      type: string # $ref: '#/components/schemas/UsageStatisticFields'
            """;

        OpenApiSchemaShape shape = ReadOpenApiSchemaProperties(source)["AccountUsagePoint"];

        Assert.DoesNotContain("starts_at", shape.TopLevelProperties);
        Assert.False(IsApprovedOpenApiTokenStatistic(
            "AccountUsagePoint",
            "total_tokens",
            shape));
    }

    [Fact]
    public void OpenApiParserHandlesQuotedRefsAndRejectsShapeIntroducingRefs()
    {
        const string quotedLocalReference = """
            components:
              schemas:
                TokenBudget:
                  type: object
                  properties:
                    total_tokens:
                      type: integer
                User:
                  '$ref': '#/components/schemas/TokenBudget'
            """;
        Assert.Contains(
            "total_tokens",
            ReadOpenApiSchemaProperties(quotedLocalReference)["User"].PropertyPaths);

        foreach (string rejected in new[]
        {
            "'$ref': 'https://example.invalid/schema.yaml#/TokenBudget'",
            "'patternProperties':",
            "'$dynamicRef': '#/components/schemas/TokenBudget'",
        })
        {
            string source = $$"""
                components:
                  schemas:
                    User:
                      {{rejected}}
                """;
            Assert.ThrowsAny<Exception>(() => ReadOpenApiSchemaProperties(source));
        }

        foreach (string scope in new[] { "then", "else", "additionalProperties" })
        {
            string source = $$"""
                components:
                  schemas:
                    TokenBudget:
                      type: object
                    User:
                      {{scope}}:
                        $ref: '#/components/schemas/TokenBudget'
                """;
            Assert.ThrowsAny<Exception>(() => ReadOpenApiSchemaProperties(source));
        }
    }

    [Fact]
    public void OpenApiRequestSafetyAndQuotaMutationBindingsAreExact()
    {
        OpenApiSchemaShape responseRequest = new(
            new HashSet<string>(["max_output_tokens"], StringComparer.Ordinal),
            new HashSet<string>(["max_output_tokens"], StringComparer.Ordinal))
        {
            References =
            [
                new OpenApiReferenceEdge("max_output_tokens", "PositiveSafeTokenCount"),
            ],
        };
        Assert.True(IsApprovedOpenApiRequestSafetyLimit(
            "ResponseCreateRequest",
            "max_output_tokens",
            responseRequest));
        Assert.False(IsApprovedOpenApiRequestSafetyLimit(
            "UserRequest",
            "max_output_tokens",
            responseRequest));
        Assert.False(IsApprovedOpenApiRequestSafetyLimit(
            "ResponseCreateRequest",
            "max_output_tokens",
            responseRequest with
            {
                References =
                [
                    new OpenApiReferenceEdge("max_output_tokens", "PositiveSafeTokenCount"),
                    new OpenApiReferenceEdge("max_output_tokens", "BudgetValue"),
                ],
            }));

        const string validBindings = """
              /api/v1/admin/groups/{groupId}/quota/adjust:
                post:
                  requestBody:
                    content:
                      application/json:
                        schema:
                          $ref: '#/components/schemas/QuotaAdjustRequest'
              /api/v1/admin/groups/{groupId}/quota/reset:
                post:
                  requestBody:
                    content:
                      application/json:
                        schema:
                          $ref: '#/components/schemas/QuotaResetRequest'
            """;
        AssertQuotaMutationRequestBindings(validBindings);
        Assert.ThrowsAny<Exception>(() => AssertQuotaMutationRequestBindings(
            validBindings.Replace("    post:", "    put:", StringComparison.Ordinal)));
        Assert.ThrowsAny<Exception>(() => AssertQuotaMutationRequestBindings(
            validBindings.Replace("/quota/adjust", "/quota/wrong", StringComparison.Ordinal)));
        Assert.ThrowsAny<Exception>(() => AssertQuotaMutationRequestBindings(
            validBindings.Replace(
                "QuotaAdjustRequest",
                "QuotaResetRequest",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void GatewayPerAttemptEstimatesAreExactRequestSafetyExceptions()
    {
        const string gatewayNamespace =
            "PoolAI.Modules.Gateway.Application";
        CSharpTypeShape options = new(
            "src/Modules/PoolAI.Modules.Gateway/GatewayEstimationOptions.cs",
            "GatewayEstimationOptions",
            false,
            new HashSet<string>(StringComparer.Ordinal))
        {
            Namespace = gatewayNamespace,
        };
        CSharpTypeShape estimator = new(
            "src/Modules/PoolAI.Modules.Gateway/ConservativeTokenEstimator.cs",
            "ConservativeTokenEstimator",
            false,
            new HashSet<string>(StringComparer.Ordinal))
        {
            Namespace = gatewayNamespace,
        };
        CSharpTypeShape estimate = new(
            "src/Modules/PoolAI.Modules.Gateway/GatewayTokenEstimate.cs",
            "GatewayTokenEstimate",
            true,
            new HashSet<string>(StringComparer.Ordinal))
        {
            Namespace = gatewayNamespace,
        };

        Assert.True(IsApprovedProductionRequestSafetyLimit(
            options,
            "MaximumEstimatedTokensPerAttempt"));
        Assert.True(IsApprovedProductionRequestSafetyLimit(
            estimator,
            "_options:DefaultMaxOutputTokens"));
        Assert.True(IsApprovedProductionRequestSafetyLimit(
            estimate,
            "TotalTokens"));
        Assert.False(IsApprovedProductionRequestSafetyLimit(
            options with { RelativePath = "src/Modules/Other/Options.cs" },
            "MaximumEstimatedTokensPerAttempt"));
        Assert.False(IsApprovedProductionRequestSafetyLimit(
            estimate,
            "UserTotalTokens"));
    }

    [Fact]
    public void OpenApiParserIncludesRequiredOnlyPersonalAuthorityMembers()
    {
        const string source = """
            components:
              schemas:
                User:
                  type: object
                  additionalProperties: true
                  required: [total_tokens]
            """;

        OpenApiSchemaShape shape = ReadOpenApiSchemaProperties(source)["User"];

        Assert.Contains("total_tokens", shape.PropertyPaths);
        Assert.True(IsForbiddenOpenApiPersonalAuthorityPath("User", "total_tokens"));
    }

    [Fact]
    public void OpenApiStatisticReportAllowlistBindsItsDataTarget()
    {
        const string source = """
            components:
              schemas:
                BudgetValue:
                  type: object
                  properties:
                    total_tokens: { type: string }
                AccountUsageReport:
                  type: object
                  required: [account_id, from, to, granularity, data]
                  properties:
                    account_id: { type: string }
                    from: { type: string }
                    to: { type: string }
                    granularity: { type: string }
                    data:
                      type: array
                      items:
                        $ref: '#/components/schemas/BudgetValue'
            """;

        OpenApiSchemaShape shape = ReadOpenApiSchemaProperties(source)["AccountUsageReport"];

        Assert.Contains("data:total_tokens", shape.PropertyPaths);
        Assert.False(IsApprovedOpenApiTokenStatistic(
            "AccountUsageReport",
            "data:total_tokens",
            shape));
        Assert.False(HasExactOpenApiReference(
            shape with
            {
                References =
                [
                    new OpenApiReferenceEdge("data", "AccountUsagePoint"),
                    new OpenApiReferenceEdge("data", "BudgetValue"),
                ],
            },
            "data",
            "AccountUsagePoint"));
    }

    [Fact]
    public void ConfigurationParserIncludesUnquotedEnvironmentKeys()
    {
        string[] keys = ReadConfigurationKeys(
            "User__TotalTokens=100\nAccount:RemainingTokens=20\n"
            + "configuration[\"UserTotalTokens\"]\n"
            + "_configuration.GetSection(\"User\").GetValue<long>(\"TotalTokens\")\n"
            + "configuration.GetSection(\"Account\")[\"MaximumTokens\"]\n"
            + "configuration.GetSection(\"User\").GetSection(\"Quota\")"
            + ".GetValue<long>(\"TotalTokens\")\n"
            + "configuration.GetSection(\"User\").GetSection(\"Settings\")"
            + ".GetSection(\"Runtime\").GetValue<long>(\"TotalTokens\")\n"
            + "configuration.GetSection(\"Account\").GetSection(\"Quota\")"
            + "[\"MaximumTokens\"]\n"
            + "--User:BudgetTokens=100\n"
            + "--User:MaximumTokens 100\n"
            + "Environment.GetEnvironmentVariable(\"USER_TOTAL_TOKENS\")")
            .ToArray();

        Assert.Contains("User__TotalTokens", keys);
        Assert.Contains("Account:RemainingTokens", keys);
        Assert.Contains("UserTotalTokens", keys);
        Assert.Contains("User:TotalTokens", keys);
        Assert.Contains("Account:MaximumTokens", keys);
        Assert.Contains("User:Quota:TotalTokens", keys);
        Assert.Contains("User:Settings:Runtime:TotalTokens", keys);
        Assert.Contains("Account:Quota:MaximumTokens", keys);
        Assert.Contains("User:BudgetTokens", keys);
        Assert.Contains("User:MaximumTokens", keys);
        Assert.Contains("USER_TOTAL_TOKENS", keys);
        Assert.True(IsPersonalQuotaConfigurationKey("User__TotalTokens"));
        Assert.True(IsPersonalQuotaConfigurationKey("Account:RemainingTokens"));
        Assert.True(IsPersonalQuotaConfigurationKey("UserTotalTokens"));
        Assert.True(IsPersonalQuotaConfigurationKey("Settings:UserTotalTokens"));
        Assert.True(IsPersonalQuotaConfigurationKey("UserQuota:TotalTokens"));
        Assert.True(IsPersonalQuotaConfigurationKey("Personal:TotalTokens"));
        Assert.True(IsPersonalQuotaConfigurationKey("Users__0__TotalTokens"));
        Assert.False(IsPersonalQuotaConfigurationKey("Quota:MaxTotalTokens"));
        Assert.False(IsPersonalQuotaConfigurationKey("Gateway:DefaultMaxOutputTokens"));
        Assert.False(IsPersonalQuotaConfigurationKey(
            "Gateway:MaxEstimatedTokensPerAttempt"));
        Assert.True(IsPersonalQuotaConfigurationKey(
            "RuntimeBudget:Quota:MaxTotalTokens"));
        Assert.False(IsPersonalQuotaConfigurationKey(
            "x-common-app-environment:Quota:MaxTotalTokens"));
        Assert.False(IsPersonalQuotaConfigurationKey(
            "services:api:environment:Quota:MaxTotalTokens"));
        Assert.False(IsPersonalQuotaConfigurationKey(
            "services:worker:environment:Quota:MaxTotalTokens"));

        string[] approvedAccess = ReadConfigurationKeys(
            "configuration.GetSection(\"Quota\")"
            + ".GetValue<long>(\"MaxTotalTokens\")")
            .ToArray();
        Assert.Equal(["Quota:MaxTotalTokens"], approvedAccess);
    }

    [Fact]
    public void JsonConfigurationParserIncludesNestedPersonalAuthorityKeys()
    {
        const string source = """
            {
              "User": {
                "TotalTokens": 100
              }
            }
            """;

        Assert.Contains("User:TotalTokens", ReadJsonConfigurationKeys(source));
        Assert.True(IsPersonalQuotaConfigurationKey("User:TotalTokens"));
        Assert.DoesNotContain(
            "User:MaximumTokens",
            ReadJsonConfigurationKeys("{ \"message\": \"User:MaximumTokens\" }"));

        const string kubernetes = """
            {
              "env": [
                { "name": "User__TotalTokens", "value": "100" }
              ],
              "args": ["--User:MaximumTokens", "100"]
            }
            """;
        Assert.Contains("User__TotalTokens", ReadJsonConfigurationKeys(kubernetes));
        Assert.Contains("User:MaximumTokens", ReadJsonConfigurationKeys(kubernetes));
    }

    [Fact]
    public void YamlConfigurationParserIncludesNestedPersonalAuthorityKeys()
    {
        const string source = """
            services:
              api:
                environment:
                  User:
                    TotalTokens: 100
            """;

        string key = Assert.Single(
            ReadYamlConfigurationKeys(source),
            static candidate => candidate.EndsWith(
                "User:TotalTokens",
                StringComparison.Ordinal));
        Assert.True(IsPersonalQuotaConfigurationKey(key));

        const string anchored = """
            User: &user_defaults
              MaximumTokens: 100
            """;
        Assert.Contains("User:MaximumTokens", ReadYamlConfigurationKeys(anchored));

        const string flow = "environment: { User: { TotalTokens: 100 } }";
        Assert.Contains(
            ReadYamlConfigurationKeys(flow),
            static candidate => IsPersonalQuotaConfigurationKey(candidate));

        const string listEnvironment = "- Users__0__TotalTokens=100";
        Assert.Contains("Users__0__TotalTokens", ReadYamlConfigurationKeys(listEnvironment));

        const string flowEnvironment = "environment: [User__TotalTokens=100]";
        Assert.Contains("User__TotalTokens", ReadYamlConfigurationKeys(flowEnvironment));

        const string merged = """
            defaults: &defaults
              TotalTokens: 100
            User:
              <<: *defaults
            """;
        Assert.Contains("User:TotalTokens", ReadYamlConfigurationKeys(merged));

        const string unrelatedFlow =
            "settings: { UserMode: true, GatewayMaxConcurrency: 100 }";
        Assert.DoesNotContain(
            ReadYamlConfigurationKeys(unrelatedFlow),
            static candidate => IsPersonalQuotaConfigurationKey(candidate));

        const string kubernetes = """
            env:
              - name: User__TotalTokens
                value: "100"
            """;
        Assert.Contains("User__TotalTokens", ReadYamlConfigurationKeys(kubernetes));

        const string commentOnly = "# User__TotalTokens=100";
        Assert.Empty(ReadYamlConfigurationKeys(commentOnly));
    }

    [Fact]
    public void YamlConfigurationParserIncludesFlowAnchoredAndCommandLineKeys()
    {
        const string flowKubernetes =
            "env: [{ name: User__TotalTokens, value: '100' }]";
        Assert.Contains("User__TotalTokens", ReadYamlConfigurationKeys(flowKubernetes));

        const string anchoredEnvironmentName = "name: &quota_key User__TotalTokens";
        Assert.Contains(
            "User__TotalTokens",
            ReadYamlConfigurationKeys(anchoredEnvironmentName));

        const string commandLine = "args: [--User:TotalTokens=100]";
        Assert.Contains("User:TotalTokens", ReadYamlConfigurationKeys(commandLine));

        const string multipleMerge = """
            totals: &totals
              TotalTokens: 100
            limits: &limits
              MaximumTokens: 100
            User:
              <<: [*totals, *limits]
            """;
        string[] mergedKeys = ReadYamlConfigurationKeys(multipleMerge);
        Assert.Contains("User:TotalTokens", mergedKeys);
        Assert.Contains("User:MaximumTokens", mergedKeys);

        const string directAlias = """
            totals: &totals
              TotalTokens: 100
            User: *totals
            """;
        Assert.Contains("User:TotalTokens", ReadYamlConfigurationKeys(directAlias));

        const string scalarAlias = """
            quota_key: &quota_key User__TotalTokens
            env:
              - name: *quota_key
            """;
        Assert.Contains("User__TotalTokens", ReadYamlConfigurationKeys(scalarAlias));
        Assert.ThrowsAny<Exception>(() => ReadYamlConfigurationKeys(
            "env:\n  - name: *missing_quota_key"));

        const string separatedArguments = """
            args:
              - --User:MaximumTokens
              - "100"
            flow_args: [--Account:MaximumTokens, "100"]
            """;
        string[] separatedKeys = ReadYamlConfigurationKeys(separatedArguments);
        Assert.Contains("User:MaximumTokens", separatedKeys);
        Assert.Contains("Account:MaximumTokens", separatedKeys);
    }

    [Fact]
    public void EnvironmentAndDockerfileParsersIncludePersonalAuthorityKeys()
    {
        Assert.Contains(
            "USER_TOTAL_TOKENS",
            ReadEnvironmentConfigurationKeys("export USER_TOTAL_TOKENS=100"));
        Assert.Contains(
            "User__TotalTokens",
            ReadDockerfileConfigurationKeys("ENV User__TotalTokens=100"));
        Assert.DoesNotContain(
            "User__TotalTokens",
            ReadDockerfileConfigurationKeys("RUN echo User__TotalTokens=100"));
        Assert.Contains(
            "User:TotalTokens",
            ReadDockerfileConfigurationKeys("ENTRYPOINT app --User:TotalTokens=100"));
        Assert.Contains(
            "User:MaximumTokens",
            ReadDockerfileConfigurationKeys("ENTRYPOINT app --User:MaximumTokens 100"));
    }

    [Fact]
    public void ApprovedGroupProjectionRejectsUnexpectedAuthorityMembers()
    {
        HashSet<string> members = new(
            ApprovedGroupProjectionAuthorityMembers,
            StringComparer.Ordinal)
        {
            "GroupId",
            "UserTokenLimit",
        };
        CSharpTypeShape shape = new(
            "src/PoolAI.Application.Orchestration/UserGroupPoolView.cs",
            "UserGroupPoolView",
            true,
            members)
        {
            Namespace = "PoolAI.Application.Orchestration",
        };

        Assert.False(IsApprovedGroupQuotaProjection(shape));
        Assert.False(IsApprovedGroupQuotaProjectionMember(shape, "UserTokenLimit"));
    }

    [Fact]
    public void GroupProjectionAllowlistIsExactByPathShapeAndCurrentMembers()
    {
        HashSet<string> members = new(
            ApprovedGroupProjectionAuthorityMembers,
            StringComparer.Ordinal)
        {
            "GroupId",
        };
        CSharpTypeShape exact = new(
            "src/PoolAI.Application.Orchestration/UserGroupPoolView.cs",
            "UserGroupPoolView",
            true,
            members)
        {
            Namespace = "PoolAI.Application.Orchestration",
        };
        CSharpTypeShape wrongPath = exact with
        {
            RelativePath = "src/Another/UserGroupPoolView.cs",
        };

        Assert.True(IsApprovedGroupQuotaProjection(exact));
        Assert.True(IsApprovedGroupQuotaProjectionMember(exact, "TotalTokens"));
        Assert.False(IsApprovedGroupQuotaProjectionMember(exact, "OverageTokens"));
        Assert.False(IsApprovedGroupQuotaProjection(wrongPath));
        Assert.False(IsApprovedGroupQuotaProjectionMember(wrongPath, "TotalTokens"));
        Assert.False(IsApprovedGroupQuotaProjectionMember(
            exact with { Namespace = "Another.Namespace" },
            "TotalTokens"));

        CSharpTypeShape incomplete = exact with
        {
            Members = new HashSet<string>(["GroupId", "TotalTokens"], StringComparer.Ordinal),
        };
        Assert.False(IsApprovedGroupQuotaProjection(incomplete));
        Assert.False(IsApprovedGroupQuotaProjectionMember(incomplete, "TotalTokens"));
    }

    [Fact]
    public void StatisticalAllowlistsRejectNearMisses()
    {
        HashSet<string> databaseColumns =
        [
            "group_id",
            "account_id",
            "period_id",
            "bucket_start",
            "input_tokens",
            "output_tokens",
            "total_tokens",
        ];
        Assert.True(IsApprovedDatabaseTokenStatistic(
            "account_usage_hourly",
            "total_tokens",
            databaseColumns));
        Assert.False(IsApprovedDatabaseTokenStatistic(
            "account_usage_hourly",
            "remaining_tokens",
            databaseColumns));

        OpenApiSchemaShape openApiShape = new(
            new HashSet<string>(ApprovedOpenApiStatisticShape, StringComparer.Ordinal),
            new HashSet<string>(ApprovedOpenApiStatisticShape, StringComparer.Ordinal));
        Assert.True(IsApprovedOpenApiTokenStatistic(
            "AccountUsagePoint",
            "total_tokens",
            openApiShape));
        Assert.False(IsApprovedOpenApiTokenStatistic(
            "AccountUsagePoint",
            "remaining_tokens",
            openApiShape));
        OpenApiSchemaShape nestedNearMiss = new(
            new HashSet<string>(ApprovedOpenApiStatisticShape, StringComparer.Ordinal),
            new HashSet<string>(["total_tokens", "details"], StringComparer.Ordinal));
        Assert.False(IsApprovedOpenApiTokenStatistic(
            "AccountUsagePoint",
            "total_tokens",
            nestedNearMiss));

        CSharpTypeShape exact = new(
            "src/PoolAI.Contracts/Generated/OpenApiV1.g.cs",
            "AccountUsagePoint",
            true,
            new HashSet<string>(
                ["StartsAt", "InputTokens", "OutputTokens", "TotalTokens"],
                StringComparer.Ordinal))
        {
            Namespace = "PoolAI.Contracts.Generated",
        };
        Assert.True(IsApprovedProductionTokenStatistic(exact, "TotalTokens"));
        Assert.False(IsApprovedProductionTokenStatistic(
            exact with { RelativePath = "src/Other/AccountUsagePoint.cs" },
            "TotalTokens"));
        Assert.False(IsApprovedProductionTokenStatistic(
            exact with { Namespace = "Another.Namespace" },
            "TotalTokens"));

    }

    [Fact]
    public void ProductionStatisticReportAllowlistBindsItsDataTarget()
    {
        CSharpTypeShape report = new(
            "src/PoolAI.Contracts/Generated/OpenApiV1.g.cs",
            "AccountUsageReport",
            true,
            new HashSet<string>(ApprovedProductionReportShape, StringComparer.Ordinal))
        {
            Namespace = "PoolAI.Contracts.Generated",
            References = [new CSharpReferenceEdge("Data", "AccountUsagePoint")],
        };
        Assert.True(IsApprovedProductionTokenStatistic(report, "Data:TotalTokens"));
        Assert.False(IsApprovedProductionTokenStatistic(
            report with
            {
                References = [new CSharpReferenceEdge("Data", "BudgetValue")],
            },
            "Data:TotalTokens"));
        Assert.False(IsApprovedProductionTokenStatistic(
            report with
            {
                References =
                [
                    new CSharpReferenceEdge("Data", "AccountUsagePoint"),
                    new CSharpReferenceEdge("Data", "BudgetValue"),
                ],
            },
            "Data:TotalTokens"));
    }

    private static void AssertPostgresCatalogBoundary(
        string root,
        List<string> violations)
    {
        Dictionary<string, HashSet<string>> tables = ReadDatabaseTableColumns(root);
        HashSet<string> groupQuota = RequireDatabaseTable(tables, "group_token_quotas");
        HashSet<string> groupQuotaPeriods = RequireDatabaseTable(
            tables,
            "group_quota_periods");
        AssertCanonicalAuthority(
            groupQuota,
            ["group_id", "current_period_id", "enabled", "version"],
            "PostgreSQL group_token_quotas");
        AssertCanonicalAuthority(
            groupQuotaPeriods,
            ["group_id", "total_tokens", "consumed_tokens", "reserved_tokens"],
            "PostgreSQL group_quota_periods");

        foreach ((string table, HashSet<string> columns) in tables
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (IsForbiddenNonGroupQuotaOwner(table))
            {
                violations.Add($"PostgreSQL table::{table}::<authority-owner>");
            }

            foreach (string column in columns
                .Where(column =>
                    IsForbiddenPersonalAuthorityMember(table, column)
                    && IsDatabaseQuotaAuthorityColumn(column)
                    && !IsApprovedDatabaseTokenStatistic(table, column, columns))
                .Order(StringComparer.Ordinal))
            {
                violations.Add($"PostgreSQL table::{table}::{column}");
            }
        }
    }

    private static HashSet<string> RequireDatabaseTable(
        Dictionary<string, HashSet<string>> tables,
        string table)
    {
        Assert.True(
            tables.TryGetValue(table, out HashSet<string>? columns),
            $"The migration catalog must define the quota-boundary table '{table}'.");
        Assert.NotNull(columns);
        return columns;
    }

    private static void AssertOpenApiBoundary(
        string root,
        List<string> violations)
    {
        string openApiPath = Path.Combine(
            root,
            "docs",
            "contracts",
            "openapi-v1.yaml");
        string openApiSource = File.ReadAllText(openApiPath);
        Dictionary<string, OpenApiSchemaShape> schemas = ReadOpenApiSchemaProperties(
            openApiSource);
        AssertQuotaMutationRequestBindings(openApiSource);

        Assert.True(
            schemas.TryGetValue("GroupQuota", out OpenApiSchemaShape? groupQuotaShape),
            "The authoritative OpenAPI contract must define GroupQuota.");
        Assert.NotNull(groupQuotaShape);
        AssertCanonicalAuthority(
            groupQuotaShape.TopLevelProperties,
            CanonicalOpenApiAuthorityProperties,
            "OpenAPI GroupQuota");

        foreach ((string schema, OpenApiSchemaShape shape) in schemas
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (string propertyPath in shape.PropertyPaths
                .Where(propertyPath =>
                    IsForbiddenOpenApiPersonalAuthorityPath(schema, propertyPath)
                    && !IsApprovedOpenApiGroupQuotaAuthority(
                        schema,
                        propertyPath,
                        shape)
                    && !IsApprovedOpenApiTokenStatistic(schema, propertyPath, shape)
                    && !IsApprovedOpenApiRequestSafetyLimit(
                        schema,
                        propertyPath,
                        shape))
                .Order(StringComparer.Ordinal))
            {
                violations.Add($"OpenAPI schema::{schema}::{propertyPath}");
            }
        }
    }

    private static void AssertQuotaMutationRequestBindings(string openApiSource)
    {
        OpenApiRequestSchemaBinding[] expected =
        [
            new(
                "/api/v1/admin/groups/{groupId}/quota/adjust",
                "post",
                "QuotaAdjustRequest",
                true),
            new(
                "/api/v1/admin/groups/{groupId}/quota/reset",
                "post",
                "QuotaResetRequest",
                true),
        ];
        OpenApiRequestSchemaBinding[] actual = ReadOpenApiRequestSchemaBindings(
                openApiSource)
            .Where(static binding => binding.Schema is
                "QuotaAdjustRequest" or "QuotaResetRequest")
            .OrderBy(static binding => binding.Path, StringComparer.Ordinal)
            .ThenBy(static binding => binding.Method, StringComparer.Ordinal)
            .ThenBy(static binding => binding.Schema, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expected.OrderBy(static binding => binding.Path, StringComparer.Ordinal),
            actual);
    }

    private static IEnumerable<OpenApiRequestSchemaBinding>
        ReadOpenApiRequestSchemaBindings(string source)
    {
        string? currentPath = null;
        string? currentMethod = null;
        int requestBodyIndentation = -1;
        foreach (string rawLine in source.Split('\n'))
        {
            string line = StripYamlComment(rawLine.TrimEnd('\r'));
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            int indentation = LeadingSpaces(line);
            if (indentation == 2
                && trimmed.EndsWith(':')
                && trimmed.TrimStart('\'', '"').StartsWith('/'))
            {
                currentPath = trimmed[..^1].Trim('\'', '"');
                currentMethod = null;
                requestBodyIndentation = -1;
            }
            else if (indentation == 4
                && TryReadYamlMapping(trimmed, out string method, out _)
                && method is "get" or "post" or "put" or "patch" or "delete")
            {
                currentMethod = method;
                requestBodyIndentation = -1;
            }

            if (requestBodyIndentation >= 0
                && indentation <= requestBodyIndentation
                && !(indentation == requestBodyIndentation
                    && TryReadYamlMapping(trimmed, out string boundaryKey, out _)
                    && string.Equals(
                        boundaryKey,
                        "requestBody",
                        StringComparison.Ordinal)))
            {
                requestBodyIndentation = -1;
            }

            if (currentPath is not null
                && currentMethod is not null
                && TryReadYamlMapping(trimmed, out string key, out _)
                && string.Equals(key, "requestBody", StringComparison.Ordinal))
            {
                requestBodyIndentation = indentation;
            }

            if (TryReadQuotaMutationBinding(
                trimmed,
                currentPath,
                currentMethod,
                requestBodyIndentation >= 0,
                out OpenApiRequestSchemaBinding? binding))
            {
                yield return Assert.IsType<OpenApiRequestSchemaBinding>(binding);
            }
        }
    }

    private static bool TryReadQuotaMutationBinding(
        string line,
        string? path,
        string? method,
        bool isRequestBody,
        out OpenApiRequestSchemaBinding? binding)
    {
        Match? reference = LocalOpenApiSchemaReference()
            .Matches(line)
            .FirstOrDefault(static candidate => candidate.Groups["schema"].Value is
                "QuotaAdjustRequest" or "QuotaResetRequest");
        binding = reference is null
            ? null
            : new OpenApiRequestSchemaBinding(
                path ?? string.Empty,
                method ?? string.Empty,
                reference.Groups["schema"].Value,
                isRequestBody);
        return binding is not null;
    }

    private static void AssertProductionDtoBoundary(
        string root,
        List<string> violations)
    {
        CSharpTypeShape[] productionShapes = ResolveCSharpInheritedMembers(
            ProductionSourceFiles(root)
                .SelectMany(path => ReadCSharpTypeShapes(
                    root,
                    path,
                    File.ReadAllText(path)))
                .ToArray());
        foreach (string requiredSubject in new[] { "User", "ApiKey", "Subscription", "Account" })
        {
            Assert.Contains(
                productionShapes,
                shape => string.Equals(shape.Name, requiredSubject, StringComparison.Ordinal));
        }

        CSharpTypeShape? generatedGroupQuota = productionShapes.SingleOrDefault(
            static shape => string.Equals(shape.Name, "GroupQuota", StringComparison.Ordinal)
                && string.Equals(
                    shape.RelativePath,
                    "src/PoolAI.Contracts/Generated/OpenApiV1.g.cs",
                    StringComparison.Ordinal)
                && string.Equals(
                    shape.Namespace,
                    "PoolAI.Contracts.Generated",
                    StringComparison.Ordinal));
        Assert.NotNull(generatedGroupQuota);
        AssertCanonicalAuthority(
            generatedGroupQuota.Members,
            CanonicalCSharpAuthorityMembers,
            "generated GroupQuota DTO");
        AssertApprovedStatisticTypeIdentities(productionShapes);

        foreach (CSharpTypeShape shape in productionShapes
            .OrderBy(static shape => shape.RelativePath, StringComparer.Ordinal)
            .ThenBy(static shape => shape.Name, StringComparer.Ordinal))
        {
            foreach (string memberPath in shape.MemberPaths
                .Where(memberPath =>
                    IsForbiddenCSharpPersonalAuthorityPath(
                        HasDirectOrNestedGroupIdentity(shape)
                            || HasExactImplicitGroupQuotaOwnership(shape)
                            ? $"Group:{shape.Name}"
                            : shape.Name,
                        memberPath)
                    && !IsApprovedGroupQuotaProjectionMember(shape, memberPath)
                    && !IsApprovedProductionGroupQuotaAuthority(shape, memberPath)
                    && !IsApprovedProductionTokenStatistic(shape, memberPath)
                    && !IsApprovedProductionRequestSafetyLimit(shape, memberPath)
                    && !IsApprovedProductionNonAuthorityTokenMember(shape, memberPath))
                .Order(StringComparer.Ordinal))
            {
                violations.Add(
                    $"production type::{shape.RelativePath}::{shape.Name}::{memberPath}");
            }
        }
    }

    private static void AssertApprovedStatisticTypeIdentities(
        CSharpTypeShape[] shapes)
    {
        AssertExactProductionType(
            shapes,
            "AccountUsagePoint",
            "src/PoolAI.Contracts/Generated/OpenApiV1.g.cs",
            "PoolAI.Contracts.Generated");
        AssertExactProductionType(
            shapes,
            "AccountUsageReport",
            "src/PoolAI.Contracts/Generated/OpenApiV1.g.cs",
            "PoolAI.Contracts.Generated");
        AssertExactProductionType(
            shapes,
            "UsageHourlyAggregate",
            "src/Modules/PoolAI.Modules.Usage/Application/UsageHourlyAggregate.cs",
            "PoolAI.Modules.Usage.Application");
        AssertExactProductionType(
            shapes,
            "AccountUsageHourProjection",
            "src/Modules/PoolAI.Modules.Usage/Application/AccountUsageHourProjection.cs",
            "PoolAI.Modules.Usage.Application");
    }

    private static void AssertExactProductionType(
        CSharpTypeShape[] shapes,
        string name,
        string path,
        string expectedNamespace)
    {
        CSharpTypeShape shape = Assert.Single(
            shapes,
            candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        Assert.Equal(path, shape.RelativePath);
        Assert.Equal(expectedNamespace, shape.Namespace);
    }

    private static void AssertConfigurationBoundary(
        string root,
        List<string> violations)
    {
        string[] configurationKeys = ProductionConfigurationFiles(root)
            .SelectMany(path => ReadConfigurationKeysFromFile(path))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("Quota:MaxTotalTokens", configurationKeys);
        foreach (string configurationKey in configurationKeys.Where(
            static key => IsPersonalQuotaConfigurationKey(key)))
        {
            violations.Add($"production configuration::{configurationKey}");
        }
    }

    private static void AssertCanonicalAuthority(
        HashSet<string> actual,
        IEnumerable<string> expected,
        string owner)
    {
        foreach (string member in expected)
        {
            Assert.True(
                actual.Contains(member),
                $"{owner} must define the canonical authority member '{member}'.");
        }
    }

    private static void AddAuthorityViolations(
        List<string> violations,
        string kind,
        string owner,
        IEnumerable<string> members)
    {
        foreach (string member in members
            .Where(static member => IsCumulativeQuotaAuthorityMember(member))
            .Order(StringComparer.Ordinal))
        {
            violations.Add($"{kind}::{owner}::{member}");
        }
    }

    private static bool IsCumulativeQuotaAuthorityMember(string identifier)
    {
        string[] words = IdentifierWords(identifier);
        if (words.Contains("quota", StringComparer.Ordinal)
            || words.Contains("quotas", StringComparer.Ordinal))
        {
            bool isGroupQuotaReference = words.Contains("group", StringComparer.Ordinal)
                && words.LastOrDefault() is "id";
            return !isGroupQuotaReference;
        }

        if (!words.Contains("token", StringComparer.Ordinal)
            && !words.Contains("tokens", StringComparer.Ordinal))
        {
            return false;
        }

        return words.Any(IsTokenAuthorityWord);
    }

    private static bool IsDatabaseQuotaAuthorityColumn(string identifier)
    {
        if (IsCumulativeQuotaAuthorityMember(identifier))
        {
            return true;
        }

        string[] words = IdentifierWords(identifier);
        return words.Contains("total", StringComparer.Ordinal)
            && (words.Contains("token", StringComparer.Ordinal)
                || words.Contains("tokens", StringComparer.Ordinal));
    }

    private static bool IsTokenAuthorityWord(string word) => word is
        "allocated" or
        "allowance" or
        "allocation" or
        "available" or
        "balance" or
        "budget" or
        "cap" or
        "capacity" or
        "ceiling" or
        "consumed" or
        "cumulative" or
        "limit" or
        "max" or
        "maximum" or
        "month" or
        "monthly" or
        "overage" or
        "remaining" or
        "reserved" or
        "spent" or
        "used" or
        "total";

    private static bool IsApprovedDatabaseTokenStatistic(
        string table,
        string member,
        HashSet<string> columns) =>
        string.Equals(table, "account_usage_hourly", StringComparison.Ordinal)
        && string.Equals(member, "total_tokens", StringComparison.Ordinal)
        && ApprovedDatabaseStatisticShape.All(columns.Contains);

    private static bool IsApprovedOpenApiTokenStatistic(
        string schema,
        string propertyPath,
        OpenApiSchemaShape shape) =>
        (string.Equals(schema, "AccountUsagePoint", StringComparison.Ordinal)
            && string.Equals(propertyPath, "total_tokens", StringComparison.Ordinal)
            && ApprovedOpenApiStatisticShape.All(shape.TopLevelProperties.Contains))
        || (string.Equals(schema, "AccountUsageReport", StringComparison.Ordinal)
            && string.Equals(propertyPath, "data:total_tokens", StringComparison.Ordinal)
            && ApprovedOpenApiReportShape.All(shape.TopLevelProperties.Contains)
            && HasExactOpenApiReference(shape, "data", "AccountUsagePoint"));

    private static bool IsApprovedOpenApiRequestSafetyLimit(
        string schema,
        string propertyPath,
        OpenApiSchemaShape shape)
    {
        bool exactIdentity =
            (string.Equals(schema, "ResponseCreateRequest", StringComparison.Ordinal)
                && string.Equals(
                    propertyPath,
                    "max_output_tokens",
                    StringComparison.Ordinal))
            || (string.Equals(schema, "ChatCompletionRequest", StringComparison.Ordinal)
                && string.Equals(
                    propertyPath,
                    "max_completion_tokens",
                    StringComparison.Ordinal));
        return exactIdentity
            && HasExactOpenApiReference(
                shape,
                propertyPath,
                "PositiveSafeTokenCount");
    }

    private static bool IsApprovedOpenApiGroupQuotaAuthority(
        string schema,
        string propertyPath,
        OpenApiSchemaShape shape)
    {
        if (string.Equals(schema, "PoolUsage", StringComparison.Ordinal)
            && ApprovedPoolUsageQuotaPaths.Contains(propertyPath, StringComparer.Ordinal)
            && ApprovedPoolUsageQuotaPaths.All(shape.PropertyPaths.Contains))
        {
            return true;
        }

        if (string.Equals(schema, "QuotaAdjustRequest", StringComparison.Ordinal)
            && string.Equals(propertyPath, "new_total_tokens", StringComparison.Ordinal))
        {
            return shape.TopLevelProperties.SetEquals(["new_total_tokens", "reason"]);
        }

        return string.Equals(schema, "QuotaResetRequest", StringComparison.Ordinal)
            && string.Equals(propertyPath, "total_tokens", StringComparison.Ordinal)
            && shape.TopLevelProperties.SetEquals(["total_tokens", "reason"]);
    }

    private static bool HasExactOpenApiReference(
        OpenApiSchemaShape shape,
        string prefix,
        string target) =>
        shape.References
            .Where(edge => string.Equals(edge.Prefix, prefix, StringComparison.Ordinal))
            .Select(static edge => edge.Target)
            .Distinct(StringComparer.Ordinal)
            .SequenceEqual([target], StringComparer.Ordinal);

    private static bool IsForbiddenOpenApiPersonalAuthorityPath(
        string schema,
        string propertyPath)
    {
        string[] segments = propertyPath.Split(
            ':',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        string member = segments[^1];
        string owner = segments.Length == 1
            ? schema
            : $"{schema}:{string.Join(':', segments[..^1])}";
        return IsForbiddenPersonalAuthorityMember(owner, member);
    }

    private static bool IsApprovedProductionTokenStatistic(
        CSharpTypeShape shape,
        string memberPath)
    {
        bool generatedContract = string.Equals(
            shape.RelativePath,
            "src/PoolAI.Contracts/Generated/OpenApiV1.g.cs",
            StringComparison.Ordinal)
            && string.Equals(
                shape.Namespace,
                "PoolAI.Contracts.Generated",
                StringComparison.Ordinal);
        if (generatedContract
            && ((string.Equals(shape.Name, "AccountUsagePoint", StringComparison.Ordinal)
                && string.Equals(memberPath, "TotalTokens", StringComparison.Ordinal)
                && ApprovedProductionStatisticShape.All(shape.Members.Contains))
            || (string.Equals(shape.Name, "AccountUsageReport", StringComparison.Ordinal)
                && string.Equals(memberPath, "Data:TotalTokens", StringComparison.Ordinal)
                && ApprovedProductionReportShape.All(shape.Members.Contains)
                && HasCSharpReference(shape, "Data", "AccountUsagePoint"))))
        {
            return true;
        }

        if (string.Equals(
                shape.RelativePath,
                "src/Modules/PoolAI.Modules.Usage/Application/AccountUsageHourProjection.cs",
                StringComparison.Ordinal)
            && string.Equals(
                shape.Namespace,
                "PoolAI.Modules.Usage.Application",
                StringComparison.Ordinal)
            && string.Equals(shape.Name, "AccountUsageHourProjection", StringComparison.Ordinal)
            && string.Equals(memberPath, "Aggregate:TotalTokens", StringComparison.Ordinal)
            && HasCSharpReference(shape, "Aggregate", "UsageHourlyAggregate"))
        {
            return ApprovedAccountUsageProjectionShape.All(shape.Members.Contains);
        }

        return string.Equals(
                shape.RelativePath,
                "src/Modules/PoolAI.Modules.Usage/Application/UsageHourProjection.cs",
                StringComparison.Ordinal)
            && string.Equals(
                shape.Namespace,
                "PoolAI.Modules.Usage.Application",
                StringComparison.Ordinal)
            && string.Equals(shape.Name, "UsageHourProjection", StringComparison.Ordinal)
            && memberPath is "Accounts:Aggregate:TotalTokens"
                or "_accounts:Aggregate:TotalTokens"
            && ApprovedUsageHourProjectionShape.All(shape.Members.Contains)
            && HasCSharpReference(
                shape,
                memberPath.StartsWith("Accounts:", StringComparison.Ordinal)
                    ? "Accounts"
                    : "_accounts",
                "AccountUsageHourProjection");
    }

    private static bool IsApprovedProductionGroupQuotaAuthority(
        CSharpTypeShape shape,
        string memberPath)
    {
        bool generatedContract = string.Equals(
            shape.RelativePath,
            "src/PoolAI.Contracts/Generated/OpenApiV1.g.cs",
            StringComparison.Ordinal)
            && string.Equals(
                shape.Namespace,
                "PoolAI.Contracts.Generated",
                StringComparison.Ordinal);
        if (!generatedContract)
        {
            return false;
        }

        if (string.Equals(shape.Name, "QuotaAdjustRequest", StringComparison.Ordinal)
            && string.Equals(memberPath, "NewTotalTokens", StringComparison.Ordinal))
        {
            return shape.Members.SetEquals(["NewTotalTokens", "Reason"]);
        }

        if (string.Equals(shape.Name, "QuotaResetRequest", StringComparison.Ordinal)
            && string.Equals(memberPath, "TotalTokens", StringComparison.Ordinal))
        {
            return shape.Members.SetEquals(["TotalTokens", "Reason"]);
        }

        if (string.Equals(shape.Name, "PoolUsageQuota", StringComparison.Ordinal)
            && CanonicalCSharpAuthorityMembers.Contains(memberPath, StringComparer.Ordinal))
        {
            return CanonicalCSharpAuthorityMembers.All(shape.Members.Contains)
                && shape.Members.Contains("Status");
        }

        return string.Equals(shape.Name, "PoolUsage", StringComparison.Ordinal)
            && memberPath.StartsWith("Quota:", StringComparison.Ordinal)
            && CanonicalCSharpAuthorityMembers.Contains(
                memberPath["Quota:".Length..],
                StringComparer.Ordinal)
            && HasCSharpReference(shape, "Quota", "PoolUsageQuota");
    }

    private static bool IsApprovedProductionRequestSafetyLimit(
        CSharpTypeShape shape,
        string memberPath)
    {
        bool generatedRequestLimit = string.Equals(
            shape.RelativePath,
            "src/PoolAI.Contracts/Generated/OpenApiV1.g.cs",
            StringComparison.Ordinal)
        && string.Equals(
            shape.Namespace,
            "PoolAI.Contracts.Generated",
            StringComparison.Ordinal)
        && ((string.Equals(shape.Name, "ResponseCreateRequest", StringComparison.Ordinal)
                && string.Equals(memberPath, "MaxOutputTokens", StringComparison.Ordinal))
            || (string.Equals(
                    shape.Name,
                    "ChatCompletionRequest",
                    StringComparison.Ordinal)
                && string.Equals(
                    memberPath,
                    "MaxCompletionTokens",
                    StringComparison.Ordinal)));
        if (generatedRequestLimit)
        {
            return true;
        }

        return IsApprovedGatewayRequestSafetyLimit(shape, memberPath);
    }

    private static bool IsApprovedGatewayRequestSafetyLimit(
        CSharpTypeShape shape,
        string memberPath)
    {
        if (!string.Equals(
            shape.Namespace,
            "PoolAI.Modules.Gateway.Application",
            StringComparison.Ordinal))
        {
            return false;
        }

        return (string.Equals(
                    shape.RelativePath,
                    "src/Modules/PoolAI.Modules.Gateway/GatewayEstimationOptions.cs",
                    StringComparison.Ordinal)
                && string.Equals(
                    shape.Name,
                    "GatewayEstimationOptions",
                    StringComparison.Ordinal)
                && memberPath is "DefaultMaxOutputTokens"
                    or "DefaultMaximumEstimatedTokens"
                    or "MaximumEstimatedTokensPerAttempt")
            || (string.Equals(
                    shape.RelativePath,
                    "src/Modules/PoolAI.Modules.Gateway/ConservativeTokenEstimator.cs",
                    StringComparison.Ordinal)
                && string.Equals(
                    shape.Name,
                    "ConservativeTokenEstimator",
                    StringComparison.Ordinal)
                && memberPath is "_options:DefaultMaxOutputTokens"
                    or "_options:DefaultMaximumEstimatedTokens"
                    or "_options:MaximumEstimatedTokensPerAttempt")
            || (string.Equals(
                    shape.RelativePath,
                    "src/Modules/PoolAI.Modules.Gateway/GatewayTokenEstimate.cs",
                    StringComparison.Ordinal)
                && string.Equals(
                    shape.Name,
                    "GatewayTokenEstimate",
                    StringComparison.Ordinal)
                && string.Equals(
                    memberPath,
                    "TotalTokens",
                    StringComparison.Ordinal));
    }

    private static bool HasDirectOrNestedGroupIdentity(CSharpTypeShape shape) =>
        !IsPersonalQuotaSubject(shape.Name)
        && shape.MemberPaths.Any(static path =>
            string.Equals(
                path.Split(':')[^1],
                "GroupId",
                StringComparison.Ordinal));

    private static bool HasExactImplicitGroupQuotaOwnership(CSharpTypeShape shape) =>
        IsExactCSharpShape(
            shape,
            "src/Modules/PoolAI.Modules.GroupQuota.Abstractions/GroupQuotaEventV1Codec.cs",
            "PoolAI.Modules.GroupQuota.Abstractions",
            "PayloadState",
            [
                "DeltaTotalTokens", "DeltaConsumedTokens", "DeltaReservedTokens",
                "TotalTokens", "ConsumedTokens", "ReservedTokens", "OccurredAt", "Metadata",
            ])
        || IsExactCSharpShape(
            shape,
            "src/Modules/PoolAI.Modules.GroupQuota/Application/Ports/IQuotaLedgerRepository.cs",
            "PoolAI.Modules.GroupQuota.Application.Ports",
            "QuotaReservationRow",
            [
                "ReservationId", "PeriodId", "Status", "TotalTokens", "ConsumedTokens",
                "ReservedTokens", "RemainingTokens", "LeaseExpiresAt", "MaxExpiresAt",
            ])
        || IsExactCSharpShape(
            shape,
            "src/Modules/PoolAI.Modules.GroupQuota/Application/Ports/IQuotaLedgerRepository.cs",
            "PoolAI.Modules.GroupQuota.Application.Ports",
            "QuotaTransitionRow",
            [
                "ReservationId", "PeriodId", "Status", "TotalTokens", "ConsumedTokens",
                "ReservedTokens", "RemainingTokens",
            ])
        || IsExactCSharpShape(
            shape,
            "src/Modules/PoolAI.Modules.GroupQuota/Endpoints/GroupQuotaHttp.cs",
            "PoolAI.Modules.GroupQuota.Endpoints",
            "ParsedQuotaMutationRequest",
            ["TotalTokens", "Reason"])
        || IsExactCSharpShape(
            shape,
            "src/Modules/PoolAI.Modules.GroupQuota/Endpoints/GroupQuotaHttp.cs",
            "PoolAI.Modules.GroupQuota.Endpoints",
            "QuotaMutationRequestReadResult",
            ["Request", "Failure"])
        || IsExactCSharpShape(
            shape,
            "src/Modules/PoolAI.Modules.GroupQuota/Infrastructure/Persistence/PostgresQuotaAbiContract.cs",
            "PoolAI.Modules.GroupQuota.Infrastructure.Persistence",
            "PostgresQuotaAdjustFunctionRow",
            [
                "PeriodId", "TotalTokens", "ConsumedTokens", "ReservedTokens",
                "RemainingTokens", "QuotaVersion", "BeforeState",
            ])
        || IsExactCSharpShape(
            shape,
            "src/Modules/PoolAI.Modules.GroupQuota/Infrastructure/Persistence/PostgresQuotaAbiContract.cs",
            "PoolAI.Modules.GroupQuota.Infrastructure.Persistence",
            "PostgresQuotaResetFunctionRow",
            [
                "PeriodId", "PeriodNumber", "TotalTokens", "ConsumedTokens", "ReservedTokens",
                "RemainingTokens", "QuotaVersion", "BeforeState",
            ]);

    private static bool IsExactCSharpShape(
        CSharpTypeShape shape,
        string relativePath,
        string @namespace,
        string name,
        string[] members) =>
        string.Equals(shape.RelativePath, relativePath, StringComparison.Ordinal)
        && string.Equals(shape.Namespace, @namespace, StringComparison.Ordinal)
        && string.Equals(shape.Name, name, StringComparison.Ordinal)
        && shape.Members.SetEquals(members);

    private static bool IsApprovedProductionNonAuthorityTokenMember(
        CSharpTypeShape shape,
        string memberPath) =>
        (string.Equals(
                shape.RelativePath,
                "src/Modules/PoolAI.Modules.GroupQuota/Application/QuotaControlPlaneService.cs",
                StringComparison.Ordinal)
            && string.Equals(
                shape.Namespace,
                "PoolAI.Modules.GroupQuota.Application",
                StringComparison.Ordinal)
            && string.Equals(shape.Name, "QuotaControlPlaneService", StringComparison.Ordinal)
            && memberPath is "MaximumSafeTokenCount" or "MaximumSafeTokenCountValue")
        || (string.Equals(
                shape.RelativePath,
                "src/Modules/PoolAI.Modules.GroupQuota/Application/QuotaLedgerValidation.cs",
                StringComparison.Ordinal)
            && string.Equals(
                shape.Namespace,
                "PoolAI.Modules.GroupQuota.Application",
                StringComparison.Ordinal)
            && string.Equals(shape.Name, "QuotaLedgerValidation", StringComparison.Ordinal)
            && string.Equals(memberPath, "MaximumSafeTokenCount", StringComparison.Ordinal))
        || (string.Equals(
                shape.RelativePath,
                "src/Modules/PoolAI.Modules.GroupQuota/Infrastructure/Persistence/PostgresQuotaAbiContract.cs",
                StringComparison.Ordinal)
            && string.Equals(
                shape.Namespace,
                "PoolAI.Modules.GroupQuota.Infrastructure.Persistence",
                StringComparison.Ordinal)
            && string.Equals(shape.Name, "PostgresQuotaAbiContract", StringComparison.Ordinal)
            && string.Equals(memberPath, "MaximumSafeTokenCount", StringComparison.Ordinal))
        || (string.Equals(
                shape.RelativePath,
                "src/PoolAI.Database.Migrations/AdminBootstrapWriter.cs",
                StringComparison.Ordinal)
            && string.Equals(
                shape.Namespace,
                "PoolAI.Database.Migrations",
                StringComparison.Ordinal)
            && string.Equals(shape.Name, "AdminBootstrapWriter", StringComparison.Ordinal)
            && string.Equals(memberPath, "TokenConsumedSql", StringComparison.Ordinal));

    private static bool HasCSharpReference(
        CSharpTypeShape shape,
        string prefix,
        string target) =>
        shape.References
            .Where(edge => string.Equals(edge.Prefix, prefix, StringComparison.Ordinal))
            .Select(static edge => edge.Target)
            .Distinct(StringComparer.Ordinal)
            .SequenceEqual([target], StringComparer.Ordinal);

    private static bool IsForbiddenCSharpPersonalAuthorityPath(
        string type,
        string memberPath)
    {
        string[] segments = memberPath.Split(
            ':',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        string member = segments[^1];
        string owner = segments.Length == 1
            ? type
            : $"{type}:{string.Join(':', segments[..^1])}";
        return IsForbiddenPersonalAuthorityMember(owner, member);
    }

    private static bool IsForbiddenPersonalAuthorityMember(
        string owner,
        string member)
    {
        if (!IsCumulativeQuotaAuthorityMember(member))
        {
            return false;
        }

        if (IsPersonalQuotaSubject(owner)
            || IsPersonalQuotaSubject(member))
        {
            return true;
        }

        if (IsGroupQuotaSubject(owner)
            || IsUsageObservationOwner(owner))
        {
            return false;
        }

        string[] memberWords = IdentifierWords(member);
        bool cumulativeTokenAuthority =
            (memberWords.Contains("token", StringComparer.Ordinal)
                || memberWords.Contains("tokens", StringComparer.Ordinal))
            && memberWords.Any(IsTokenAuthorityWord);
        if (cumulativeTokenAuthority)
        {
            return true;
        }

        return false;
    }

    private static bool IsUsageObservationOwner(string owner)
    {
        if (IsPotentialQuotaOwner(owner))
        {
            return false;
        }

        string[] words = IdentifierWords(owner);
        return words.Any(static word => word is
            "aggregate" or
            "aggregates" or
            "attempt" or
            "attempts" or
            "fact" or
            "facts" or
            "metric" or
            "metrics" or
            "projection" or
            "projections" or
            "reconciliation" or
            "report" or
            "reports" or
            "statistic" or
            "statistics" or
            "usage");
    }

    private static bool IsForbiddenNonGroupQuotaOwner(string owner) =>
        IsPotentialQuotaOwner(owner) && !IsGroupQuotaSubject(owner);

    private static bool IsGroupQuotaSubject(string owner)
    {
        string[] words = IdentifierWords(owner);
        return words.Contains("group", StringComparer.Ordinal)
            && !IsPersonalQuotaSubject(owner);
    }

    private static bool IsPotentialQuotaOwner(string owner)
    {
        string[] words = IdentifierWords(owner);
        if (words.Any(static word => word is
            "budget" or
            "budgets" or
            "limit" or
            "limits" or
            "quota" or
            "quotas"))
        {
            return !words.Any(static word => word is
                "delivery" or
                "event" or
                "health" or
                "metric" or
                "metrics" or
                "period" or
                "reconciliation");
        }

        return false;
    }

    private static bool IsPersonalQuotaSubject(string name)
    {
        string[] words = IdentifierWords(name);
        return words.Contains("user", StringComparer.Ordinal)
            || words.Contains("users", StringComparer.Ordinal)
            || words.Contains("personal", StringComparer.Ordinal)
            || words.Contains("customer", StringComparer.Ordinal)
            || words.Contains("customers", StringComparer.Ordinal)
            || words.Contains("member", StringComparer.Ordinal)
            || words.Contains("members", StringComparer.Ordinal)
            || words.Contains("organization", StringComparer.Ordinal)
            || words.Contains("organizations", StringComparer.Ordinal)
            || words.Contains("platform", StringComparer.Ordinal)
            || words.Contains("profile", StringComparer.Ordinal)
            || words.Contains("profiles", StringComparer.Ordinal)
            || words.Contains("project", StringComparer.Ordinal)
            || words.Contains("projects", StringComparer.Ordinal)
            || words.Contains("subscription", StringComparer.Ordinal)
            || words.Contains("subscriptions", StringComparer.Ordinal)
            || words.Contains("team", StringComparer.Ordinal)
            || words.Contains("teams", StringComparer.Ordinal)
            || words.Contains("tenant", StringComparer.Ordinal)
            || words.Contains("tenants", StringComparer.Ordinal)
            || words.Contains("workspace", StringComparer.Ordinal)
            || words.Contains("workspaces", StringComparer.Ordinal)
            || words.Contains("account", StringComparer.Ordinal)
            || words.Contains("accounts", StringComparer.Ordinal)
            || (words.Contains("api", StringComparer.Ordinal)
                && (words.Contains("key", StringComparer.Ordinal)
                    || words.Contains("keys", StringComparer.Ordinal)));
    }

    private static bool IsApprovedGroupQuotaProjection(CSharpTypeShape shape) =>
        HasApprovedGroupQuotaProjectionShape(shape)
        && shape.Members
            .Where(static member => IsCumulativeQuotaAuthorityMember(member))
            .All(member => IsApprovedGroupQuotaProjectionMember(shape, member));

    private static bool IsApprovedGroupQuotaProjectionMember(
        CSharpTypeShape shape,
        string member) =>
        HasApprovedGroupQuotaProjectionShape(shape)
        && ApprovedGroupProjectionAuthorityMembers.Contains(member, StringComparer.Ordinal);

    private static bool HasApprovedGroupQuotaProjectionShape(CSharpTypeShape shape) =>
        IsExactGroupQuotaProjection(shape)
        && shape.Members.Contains("GroupId")
        && ApprovedGroupProjectionAuthorityMembers.All(shape.Members.Contains);

    private static bool IsExactGroupQuotaProjection(CSharpTypeShape shape) =>
        string.Equals(
            shape.RelativePath,
            "src/PoolAI.Application.Orchestration/UserGroupPoolView.cs",
            StringComparison.Ordinal)
        && string.Equals(shape.Name, "UserGroupPoolView", StringComparison.Ordinal)
        && string.Equals(
            shape.Namespace,
            "PoolAI.Application.Orchestration",
            StringComparison.Ordinal);

    private static bool IsPersonalQuotaConfigurationKey(string key)
    {
        string normalized = key.Replace("__", ":", StringComparison.Ordinal);
        string[] segments = normalized.Split(
            ':',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        string member = segments[^1];
        string owner = segments.Length == 1
            ? member
            : string.Join(':', segments[..^1]);
        return !IsApprovedRequestSafetyConfigurationKey(key)
            && IsForbiddenPersonalAuthorityMember(owner, member);
    }

    private static bool IsApprovedRequestSafetyConfigurationKey(string key)
    {
        string normalized = key.Replace("__", ":", StringComparison.Ordinal);
        if (IsPersonalQuotaSubject(normalized))
        {
            return false;
        }

        string[] exactKeys =
        [
            "Quota:MaxTotalTokens",
            "Gateway:DefaultMaxOutputTokens",
            "Gateway:MaxEstimatedTokensPerAttempt",
        ];
        if (exactKeys.Contains(normalized, StringComparer.Ordinal))
        {
            return true;
        }

        string[] reviewedDeploymentPrefixes =
        [
            "x-common-app-environment:",
            "services:api:environment:",
            "services:worker:environment:",
        ];
        return reviewedDeploymentPrefixes.Any(prefix => exactKeys.Any(
            exactKey => string.Equals(
                normalized,
                $"{prefix}{exactKey}",
                StringComparison.Ordinal)));
    }

    private static string[] IdentifierWords(string identifier) =>
        IdentifierWord()
            .Matches(identifier.Replace('_', ' '))
            .Select(static match => match.Value.ToLowerInvariant())
            .ToArray();

    private static Dictionary<string, OpenApiSchemaShape> ReadOpenApiSchemaProperties(
        string source)
    {
        Dictionary<string, OpenApiSchemaShape> directSchemas = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<OpenApiReferenceEdge>> references =
            new(StringComparer.Ordinal);
        string? currentSchema = null;
        bool inSchemas = false;
        List<OpenApiPropertiesBlock> propertiesBlocks = [];
        List<OpenApiPropertyFrame> propertyFrames = [];
        List<OpenApiReferenceScope> ignoredReferenceScopes = [];
        OpenApiRequiredBlock? requiredBlock = null;

        foreach (string rawLine in source.Split('\n'))
        {
            string line = StripYamlComment(rawLine.TrimEnd('\r'));
            if (!inSchemas)
            {
                inSchemas = string.Equals(line, "  schemas:", StringComparison.Ordinal);
                continue;
            }

            string trimmed = line.Trim();
            if (trimmed.Length != 0
                && !trimmed.StartsWith('#')
                && LeadingSpaces(line) <= 2)
            {
                break;
            }

            int indentation = LeadingSpaces(line);
            if (trimmed.Length > 0)
            {
                ignoredReferenceScopes.RemoveAll(
                    scope => scope.Indentation >= indentation);
                if (TryReadNonCompositionalReferenceScope(
                    trimmed,
                    out bool rejectReferences))
                {
                    ignoredReferenceScopes.Add(new OpenApiReferenceScope(
                        indentation,
                        rejectReferences));
                }
            }

            AddOpenApiSchemaLine(
                line,
                directSchemas,
                references,
                ref currentSchema,
                propertiesBlocks,
                propertyFrames,
                ref requiredBlock,
                ignoredReferenceScopes.Count == 0,
                ignoredReferenceScopes.Any(static scope => scope.RejectReferences));
        }

        return ResolveOpenApiSchemaProperties(directSchemas, references);
    }

    private static void AddOpenApiSchemaLine(
        string line,
        Dictionary<string, OpenApiSchemaShape> schemas,
        Dictionary<string, HashSet<OpenApiReferenceEdge>> references,
        ref string? currentSchema,
        List<OpenApiPropertiesBlock> propertiesBlocks,
        List<OpenApiPropertyFrame> propertyFrames,
        ref OpenApiRequiredBlock? requiredBlock,
        bool allowReferences,
        bool rejectReferences)
    {
        string trimmed = line.Trim();
        int indentation = LeadingSpaces(line);
        Assert.DoesNotMatch(YamlAnchorOrAlias(), trimmed);
        AssertSupportedOpenApiStructuralKey(trimmed);
        if (TryBeginOpenApiSchema(
            trimmed,
            indentation,
            schemas,
            references,
            ref currentSchema,
            propertiesBlocks,
            propertyFrames))
        {
            requiredBlock = null;
            return;
        }

        if (currentSchema is null || trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return;
        }

        propertiesBlocks.RemoveAll(block => block.Indentation >= indentation);
        propertyFrames.RemoveAll(frame => frame.Indentation >= indentation);
        if (requiredBlock is not null && indentation <= requiredBlock.Indentation)
        {
            requiredBlock = null;
        }

        OpenApiPropertiesBlock? owner = propertiesBlocks
            .LastOrDefault(block => indentation == block.Indentation + 2);

        if (AddOpenApiRequiredProperties(
            trimmed,
            indentation,
            currentSchema,
            schemas,
            propertyFrames,
            owner is not null,
            ref requiredBlock))
        {
            return;
        }

        AddOpenApiPropertyOrReference(
            trimmed,
            indentation,
            currentSchema,
            schemas,
            references,
            propertiesBlocks,
            propertyFrames,
            owner,
            allowReferences,
            rejectReferences);
    }

    private static void AssertSupportedOpenApiStructuralKey(string line)
    {
        string structuralLine = line.TrimStart('-').TrimStart();
        if (!TryReadYamlMapping(structuralLine, out string structuralKey, out _))
        {
            return;
        }

        Assert.NotEqual("patternProperties", structuralKey);
        Assert.NotEqual("$dynamicRef", structuralKey);
    }

    private static void AddOpenApiPropertyOrReference(
        string line,
        int indentation,
        string schema,
        Dictionary<string, OpenApiSchemaShape> schemas,
        Dictionary<string, HashSet<OpenApiReferenceEdge>> references,
        List<OpenApiPropertiesBlock> propertiesBlocks,
        List<OpenApiPropertyFrame> propertyFrames,
        OpenApiPropertiesBlock? owner,
        bool allowReferences,
        bool rejectReferences)
    {
        if (owner is null
            && TryReadYamlMapping(line, out string structuralKey, out string structuralValue)
            && string.Equals(structuralKey, "properties", StringComparison.Ordinal))
        {
            Assert.Equal(string.Empty, structuralValue);
            string prefix = propertyFrames.LastOrDefault()?.Path ?? string.Empty;
            propertiesBlocks.Add(new OpenApiPropertiesBlock(indentation, prefix));
            return;
        }

        string? propertyPath = null;
        if (owner is not null)
        {
            Assert.False(
                line.Contains("properties:", StringComparison.Ordinal),
                $"Nested OpenAPI flow properties are unsupported: {line}");
            Assert.True(
                TryReadYamlKey(line, out string property),
                $"Unsupported OpenAPI property key syntax: {line}");
            propertyPath = owner.Prefix.Length == 0
                ? property
                : $"{owner.Prefix}:{property}";
            OpenApiSchemaShape shape = schemas[schema];
            shape.PropertyPaths.Add(propertyPath);
            if (!propertyPath.Contains(':', StringComparison.Ordinal))
            {
                shape.TopLevelProperties.Add(property);
            }

            propertyFrames.Add(new OpenApiPropertyFrame(indentation, propertyPath));
        }

        string referencePrefix = propertyPath
            ?? propertyFrames.LastOrDefault()?.Path
            ?? string.Empty;
        if (rejectReferences && IsOpenApiReferenceLine(line))
        {
            Assert.Fail($"OpenAPI shape-introducing scope cannot contain $ref: {line}");
        }

        if (allowReferences)
        {
            AddOpenApiReferences(line, referencePrefix, references[schema]);
        }
    }

    private static bool AddOpenApiRequiredProperties(
        string line,
        int indentation,
        string schema,
        Dictionary<string, OpenApiSchemaShape> schemas,
        List<OpenApiPropertyFrame> propertyFrames,
        bool isPropertyDeclaration,
        ref OpenApiRequiredBlock? block)
    {
        string normalized = line.TrimStart('-').TrimStart();
        if (!isPropertyDeclaration
            && TryReadYamlMapping(
                normalized,
                out string structuralKey,
                out string structuralValue)
            && string.Equals(structuralKey, "required", StringComparison.Ordinal))
        {
            string prefix = propertyFrames.LastOrDefault()?.Path ?? string.Empty;
            string value = structuralValue;
            if (value.Length == 0
                || string.Equals(value, "[", StringComparison.Ordinal))
            {
                block = new OpenApiRequiredBlock(indentation, prefix);
                return true;
            }

            if (value[0] == '['
                && !value.Contains(']'))
            {
                AddRequiredNames(value, prefix, schemas[schema]);
                block = new OpenApiRequiredBlock(indentation, prefix);
                return true;
            }

            AddRequiredNames(value, prefix, schemas[schema]);
            return true;
        }

        if (block is null || indentation <= block.Indentation)
        {
            return false;
        }

        AddRequiredNames(line, block.Prefix, schemas[schema]);
        return true;
    }

    private static void AddRequiredNames(
        string source,
        string prefix,
        OpenApiSchemaShape shape)
    {
        string candidate = source.Trim().TrimStart('-').Trim();
        candidate = candidate.TrimStart('[').TrimEnd(']').TrimEnd(',');
        foreach (string part in candidate.Split(',', StringSplitOptions.TrimEntries))
        {
            string name = part.Trim().Trim('\'', '"', '[', ']', ' ');
            if (name.Length == 0)
            {
                continue;
            }

            Assert.True(
                name.All(static character => char.IsAsciiLetterOrDigit(character)
                    || character is '_' or '-'),
                $"Unsupported OpenAPI required member syntax: {source}");
            string path = prefix.Length == 0 ? name : $"{prefix}:{name}";
            shape.PropertyPaths.Add(path);
            if (prefix.Length == 0)
            {
                shape.TopLevelProperties.Add(name);
            }
        }
    }

    private static bool TryReadNonCompositionalReferenceScope(
        string line,
        out bool rejectReferences)
    {
        rejectReferences = false;
        string normalized = line.TrimStart('-').TrimStart();
        if (!TryReadYamlMapping(normalized, out string key, out _)
            || key is not (
                "not" or
                "if" or
                "then" or
                "else" or
                "discriminator" or
                "additionalProperties" or
                "example" or
                "examples" or
                "default"))
        {
            return false;
        }

        rejectReferences = key is "then" or "else" or "additionalProperties";
        return true;
    }

    private static bool IsOpenApiReferenceLine(string source)
        => OpenApiReferenceKey().IsMatch(source);

    private static void AddOpenApiReferences(
        string source,
        string prefix,
        HashSet<OpenApiReferenceEdge> references)
    {
        MatchCollection referenceKeys = OpenApiReferenceKey().Matches(source);
        MatchCollection matches = LocalOpenApiSchemaReference().Matches(source);
        if (referenceKeys.Count > 0)
        {
            Assert.Equal(referenceKeys.Count, matches.Count);
        }

        foreach (Match match in matches)
        {
            references.Add(new OpenApiReferenceEdge(
                prefix,
                match.Groups["schema"].Value));
        }
    }

    private static bool TryBeginOpenApiSchema(
        string trimmed,
        int indentation,
        Dictionary<string, OpenApiSchemaShape> schemas,
        Dictionary<string, HashSet<OpenApiReferenceEdge>> references,
        ref string? currentSchema,
        List<OpenApiPropertiesBlock> propertiesBlocks,
        List<OpenApiPropertyFrame> propertyFrames)
    {
        if (indentation != 4)
        {
            return false;
        }

        Assert.True(
            TryReadYamlKey(trimmed, out string schema),
            $"Unsupported OpenAPI schema key syntax: {trimmed}");

        Assert.DoesNotContain('{', trimmed);
        Assert.DoesNotContain('[', trimmed);
        currentSchema = schema;
        schemas.TryAdd(schema, OpenApiSchemaShape.Empty());
        references.TryAdd(schema, []);
        propertiesBlocks.Clear();
        propertyFrames.Clear();
        return true;
    }

    private static Dictionary<string, OpenApiSchemaShape> ResolveOpenApiSchemaProperties(
        IReadOnlyDictionary<string, OpenApiSchemaShape> directSchemas,
        IReadOnlyDictionary<string, HashSet<OpenApiReferenceEdge>> references)
    {
        Dictionary<string, OpenApiSchemaShape> resolved = new(StringComparer.Ordinal);
        foreach (string schema in directSchemas.Keys)
        {
            resolved.Add(
                schema,
                ResolveOpenApiSchemaProperties(
                    schema,
                    directSchemas,
                    references,
                    new HashSet<string>(StringComparer.Ordinal)));
        }

        return resolved;
    }

    private static OpenApiSchemaShape ResolveOpenApiSchemaProperties(
        string schema,
        IReadOnlyDictionary<string, OpenApiSchemaShape> directSchemas,
        IReadOnlyDictionary<string, HashSet<OpenApiReferenceEdge>> references,
        HashSet<string> visiting)
    {
        if (!visiting.Add(schema)
            || !directSchemas.TryGetValue(schema, out OpenApiSchemaShape? direct))
        {
            return OpenApiSchemaShape.Empty();
        }

        OpenApiSchemaShape resolved = new(
            new HashSet<string>(direct.PropertyPaths, StringComparer.Ordinal),
            new HashSet<string>(direct.TopLevelProperties, StringComparer.Ordinal))
        {
            References = references.TryGetValue(
                schema,
                out HashSet<OpenApiReferenceEdge>? directEdges)
                ? new HashSet<OpenApiReferenceEdge>(directEdges)
                : [],
        };
        if (references.TryGetValue(schema, out HashSet<OpenApiReferenceEdge>? edges))
        {
            foreach (OpenApiReferenceEdge edge in edges)
            {
                OpenApiSchemaShape target = ResolveOpenApiSchemaProperties(
                    edge.Target,
                    directSchemas,
                    references,
                    visiting);
                foreach (string targetPath in target.PropertyPaths)
                {
                    resolved.PropertyPaths.Add(edge.Prefix.Length == 0
                        ? targetPath
                        : $"{edge.Prefix}:{targetPath}");
                }

                if (edge.Prefix.Length == 0)
                {
                    resolved.TopLevelProperties.UnionWith(target.TopLevelProperties);
                }
            }
        }

        visiting.Remove(schema);
        return resolved;
    }

    private static bool TryReadYamlKey(string line, out string key)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        key = colon > 0 ? line[..colon].Trim() : string.Empty;
        if (key.Length >= 2
            && key[0] is '\'' or '"'
            && key[^1] == key[0])
        {
            key = key[1..^1]
                .Replace(new string(key[0], 2), key[0].ToString(), StringComparison.Ordinal);
        }

        return key.Length > 0
            && key.All(static character => char.IsAsciiLetterOrDigit(character)
                || character is '_' or '-' or '$');
    }

    private static bool TryReadYamlMapping(
        string line,
        out string key,
        out string value)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !TryReadYamlKey(line, out key))
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        value = line[(colon + 1)..].Trim();
        return true;
    }

    private static string StripYamlComment(string line)
    {
        char quote = '\0';
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] is '\'' or '"')
            {
                if (quote == '\0')
                {
                    quote = line[index];
                }
                else if (quote == line[index])
                {
                    bool escaped = quote == '"'
                        && index > 0
                        && line[index - 1] == '\\';
                    if (!escaped)
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (line[index] == '#' && quote == '\0')
            {
                return line[..index].TrimEnd();
            }
        }

        return line;
    }

    private static int LeadingSpaces(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static Dictionary<string, HashSet<string>> ReadDatabaseTableColumns(
        string root)
    {
        Dictionary<string, HashSet<string>> tables = new(StringComparer.Ordinal);
        string databaseRoot = Path.Combine(root, "docs", "database");
        foreach (string migration in Directory.GetFiles(
            databaseRoot,
            "*.sql",
            SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            string source = File.ReadAllText(migration);
            Assert.DoesNotMatch(SqlUnicodeQuotedIdentifier(), source);
            string sql = MaskSqlCommentsAndLiterals(source);
            AddDatabaseTableColumns(sql, tables);
        }

        return tables;
    }

    private static void AddDatabaseTableColumns(
        string sql,
        Dictionary<string, HashSet<string>> tables)
    {
        AssertSupportedDatabaseDdl(sql);
        IEnumerable<SqlDdlStatement> creates = CreateTable()
            .Matches(sql)
            .Select(static match => new SqlDdlStatement(match.Index, true, match));
        IEnumerable<SqlDdlStatement> alters = AlterTable()
            .Matches(sql)
            .Select(static match => new SqlDdlStatement(match.Index, false, match));
        foreach (SqlDdlStatement statement in creates.Concat(alters)
            .OrderBy(static statement => statement.Index))
        {
            if (statement.IsCreate)
            {
                ApplyCreateTable(sql, statement.Match, tables);
            }
            else
            {
                ApplyAlterTable(sql, statement.Match, tables);
            }
        }
    }

    private static void AddCreateTableColumns(
        string sql,
        Dictionary<string, HashSet<string>> tables)
    {
        AssertSupportedDatabaseDdl(sql);
        foreach (Match create in CreateTable().Matches(sql))
        {
            ApplyCreateTable(sql, create, tables);
        }
    }

    private static void AddAlterTableColumns(
        string sql,
        Dictionary<string, HashSet<string>> tables)
    {
        foreach (Match alter in AlterTable().Matches(sql))
        {
            ApplyAlterTable(sql, alter, tables);
        }
    }

    private static void AssertSupportedDatabaseDdl(string sql)
    {
        HashSet<int> modeledCreates = CreateTable()
            .Matches(sql)
            .Select(static match => match.Index)
            .ToHashSet();
        foreach (Match create in CreateTableStart().Matches(sql))
        {
            Assert.True(
                modeledCreates.Contains(create.Index),
                $"Unsupported CREATE TABLE syntax at offset {create.Index}.");
        }

        Assert.DoesNotMatch(CreateView(), sql);
        Assert.DoesNotMatch(DropTable(), sql);
        Assert.DoesNotMatch(CreateTableInheritance(), sql);
    }

    private static void ApplyCreateTable(
        string sql,
        Match create,
        Dictionary<string, HashSet<string>> tables)
    {
        string table = DatabaseObjectKey(create);
        int bodyStart = create.Index + create.Length - 1;
        int bodyEnd = FindMatching(sql, bodyStart, '(', ')');
        Assert.True(bodyEnd >= 0, $"CREATE TABLE {table} has no closing delimiter.");
        Assert.False(
            tables.ContainsKey(table),
            $"CREATE TABLE redefines the existing modeled relation {table}.");
        HashSet<string> columns = new(StringComparer.Ordinal);
        tables.Add(table, columns);
        foreach (string definition in SplitSqlTopLevel(
            sql[(bodyStart + 1)..bodyEnd],
            ','))
        {
            AddSqlColumnDefinition(definition, columns);
        }
    }

    private static void ApplyAlterTable(
        string sql,
        Match alter,
        Dictionary<string, HashSet<string>> tables)
    {
        string table = DatabaseObjectKey(alter);
        int statementEnd = sql.IndexOf(';', alter.Index + alter.Length);
        string actions = statementEnd < 0
            ? sql[(alter.Index + alter.Length)..]
            : sql[(alter.Index + alter.Length)..statementEnd];
        Assert.True(
            tables.TryGetValue(table, out HashSet<string>? columns),
            $"ALTER TABLE targets the unmodeled relation {table}.");
        Assert.NotNull(columns);
        foreach (string action in SplitSqlTopLevel(actions, ','))
        {
            ApplyAlterTableAction(action, table, columns, tables);
        }
    }

    private static void ApplyAlterTableAction(
        string action,
        string table,
        HashSet<string> columns,
        Dictionary<string, HashSet<string>> tables)
    {
        Match add = AlterAddColumn().Match(action);
        if (add.Success)
        {
            AddSqlColumnDefinition(add.Groups["column"].Value, columns);
            return;
        }

        Match drop = AlterDropColumn().Match(action);
        if (drop.Success)
        {
            columns.Remove(drop.Groups["column"].Value.ToLowerInvariant());
            return;
        }

        Match renameColumn = AlterRenameColumn().Match(action);
        if (renameColumn.Success)
        {
            columns.Remove(renameColumn.Groups["old"].Value.ToLowerInvariant());
            columns.Add(renameColumn.Groups["new"].Value.ToLowerInvariant());
            return;
        }

        Match renameTable = AlterRenameTable().Match(action);
        if (renameTable.Success)
        {
            string newTable = RenamedDatabaseObjectKey(
                table,
                renameTable.Groups["new"].Value.ToLowerInvariant());
            tables.Remove(table);
            if (!tables.TryAdd(newTable, columns))
            {
                Assert.Fail($"ALTER TABLE renames {table} onto existing relation {newTable}.");
            }

            return;
        }

        Match setDefault = AlterSetColumnDefault().Match(action);
        if (setDefault.Success)
        {
            Assert.Contains(
                setDefault.Groups["column"].Value.ToLowerInvariant(),
                columns);
            return;
        }

        if (AlterValidateConstraint().IsMatch(action))
        {
            return;
        }

        Assert.Fail($"Unsupported ALTER TABLE action: {action.Trim()}");
    }

    private static string DatabaseObjectKey(Match statement)
    {
        string schema = statement.Groups["schema"].Value.ToLowerInvariant();
        string table = statement.Groups["table"].Value.ToLowerInvariant();
        return schema.Length == 0 || string.Equals(schema, "public", StringComparison.Ordinal)
            ? table
            : $"{schema}.{table}";
    }

    private static string RenamedDatabaseObjectKey(string table, string newName)
    {
        int separator = table.IndexOf('.', StringComparison.Ordinal);
        return separator < 0 ? newName : $"{table[..separator]}.{newName}";
    }

    private static void AddSqlColumnDefinition(
        string definition,
        HashSet<string> columns)
    {
        Match identifier = SqlLeadingIdentifier().Match(definition);
        if (!identifier.Success)
        {
            return;
        }

        string column = identifier.Groups["identifier"].Value.ToLowerInvariant();
        if (column is not "constraint"
            and not "primary"
            and not "foreign"
            and not "unique"
            and not "check"
            and not "exclude"
            and not "like")
        {
            columns.Add(column);
        }
    }

    private static IEnumerable<string> SplitSqlTopLevel(string source, char separator)
    {
        int start = 0;
        int round = 0;
        int square = 0;
        for (int index = 0; index < source.Length; index++)
        {
            round += source[index] == '(' ? 1 : source[index] == ')' ? -1 : 0;
            square += source[index] == '[' ? 1 : source[index] == ']' ? -1 : 0;
            if (source[index] == separator && round == 0 && square == 0)
            {
                yield return source[start..index];
                start = index + 1;
            }
        }

        yield return source[start..];
    }

    private static IEnumerable<CSharpTypeShape> ReadCSharpTypeShapes(
        string root,
        string path,
        string source)
    {
        string code = MaskCommentsAndLiterals(source);
        Assert.DoesNotMatch(CSharpIdentifierEscape(), code);
        MatchCollection aliasDirectives = CSharpUsingAliasDirective().Matches(code);
        MatchCollection supportedAliases = UsingAlias().Matches(code);
        Assert.Equal(aliasDirectives.Count, supportedAliases.Count);
        Dictionary<string, string> aliases = supportedAliases
            .ToDictionary(
                static match => match.Groups["alias"].Value.TrimStart('@'),
                static match => match.Groups["target"].Value.TrimStart('@'),
                StringComparer.Ordinal);
        string relativePath = Path.GetRelativePath(root, path).Replace(
            Path.DirectorySeparatorChar,
            '/');
        Match namespaceDeclaration = NamespaceDeclaration().Match(code);
        string namespaceName = namespaceDeclaration.Success
            ? namespaceDeclaration.Groups["namespace"].Value
            : string.Empty;
        foreach (Match declaration in TypeDeclaration().Matches(code))
        {
            CSharpTypeShape? shape = ReadCSharpTypeShape(
                relativePath,
                namespaceName,
                code,
                declaration,
                aliases);
            if (shape is not null)
            {
                yield return shape;
            }
        }
    }

    private static CSharpTypeShape? ReadCSharpTypeShape(
        string relativePath,
        string namespaceName,
        string code,
        Match declaration,
        IReadOnlyDictionary<string, string> aliases)
    {
        string kind = declaration.Groups["kind"].Value;
        string name = declaration.Groups["name"].Value;
        HashSet<string> members = new(StringComparer.Ordinal);
        HashSet<CSharpReferenceEdge> references = [];
        int cursor = SkipWhitespace(code, declaration.Index + declaration.Length);
        if (!TryReadCSharpDeclarationHeader(
            code,
            ref cursor,
            members,
            references))
        {
            return null;
        }

        int bodyStart = FindTypeBodyStart(code, cursor);
        HashSet<string> baseTypes = bodyStart < 0
            ? []
            : ReadBaseTypeNames(code[cursor..bodyStart])
                .Select(type => ResolveCSharpAlias(type, aliases))
                .ToHashSet(StringComparer.Ordinal);
        if (bodyStart >= 0 && code[bodyStart] == '{')
        {
            int bodyEnd = FindMatching(code, bodyStart, '{', '}');
            if (bodyEnd >= 0)
            {
                AddDirectBodyMembers(code, bodyStart, bodyEnd, members, references);
            }
        }

        return new CSharpTypeShape(
            relativePath,
            name,
            IsDataTransferType(relativePath, kind, name),
            members)
        {
            BaseTypes = baseTypes,
            MemberPaths = new HashSet<string>(members, StringComparer.Ordinal),
            Namespace = namespaceName,
            References = references
                .Select(edge => edge with
                {
                    Target = ResolveCSharpAlias(edge.Target, aliases),
                })
                .ToHashSet(),
        };
    }

    private static string ResolveCSharpAlias(
        string type,
        IReadOnlyDictionary<string, string> aliases) =>
        aliases.TryGetValue(type, out string? target) ? target : type;

    private static bool TryReadCSharpDeclarationHeader(
        string code,
        ref int cursor,
        HashSet<string> members,
        HashSet<CSharpReferenceEdge> references)
    {
        if (cursor < code.Length && code[cursor] == '<')
        {
            int genericEnd = FindMatching(code, cursor, '<', '>');
            if (genericEnd < 0)
            {
                return false;
            }

            cursor = SkipWhitespace(code, genericEnd + 1);
        }

        if (cursor >= code.Length || code[cursor] != '(')
        {
            return true;
        }

        int parametersEnd = FindMatching(code, cursor, '(', ')');
        if (parametersEnd < 0)
        {
            return false;
        }

        AddParameterMembers(code[(cursor + 1)..parametersEnd], members, references);
        cursor = parametersEnd + 1;
        return true;
    }

    private static HashSet<string> ReadBaseTypeNames(string declarationTail)
    {
        int colon = declarationTail.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return [];
        }

        string baseList = declarationTail[(colon + 1)..];
        int constraints = baseList.IndexOf(" where ", StringComparison.Ordinal);
        if (constraints >= 0)
        {
            baseList = baseList[..constraints];
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string candidate in SplitTopLevel(baseList, ','))
        {
            string typeName = candidate;
            int angleDepth = 0;
            for (int index = 0; index < typeName.Length; index++)
            {
                angleDepth += typeName[index] == '<' ? 1 : typeName[index] == '>' ? -1 : 0;
                if (typeName[index] == '(' && angleDepth == 0)
                {
                    typeName = typeName[..index];
                    break;
                }
            }

            int generic = typeName.IndexOf('<', StringComparison.Ordinal);
            typeName = generic < 0 ? typeName : typeName[..generic];
            MatchCollection identifiers = Identifier().Matches(typeName);
            if (identifiers.Count > 0)
            {
                names.Add(identifiers[^1].Value);
            }
        }

        return names;
    }

    private static CSharpTypeShape[] ResolveCSharpInheritedMembers(
        CSharpTypeShape[] shapes)
    {
        ILookup<string, CSharpTypeShape> byName = shapes.ToLookup(
            static shape => shape.Name,
            StringComparer.Ordinal);
        return shapes
            .Select(shape =>
            {
                CSharpResolution resolution = ResolveCSharpMembers(
                    shape,
                    byName,
                    new HashSet<string>(StringComparer.Ordinal));
                return shape with
                {
                    Members = resolution.Members,
                    MemberPaths = resolution.MemberPaths,
                };
            })
            .ToArray();
    }

    private static CSharpResolution ResolveCSharpMembers(
        CSharpTypeShape shape,
        ILookup<string, CSharpTypeShape> byName,
        HashSet<string> visiting)
    {
        string identity = $"{shape.RelativePath}::{shape.Name}";
        if (!visiting.Add(identity))
        {
            return CSharpResolution.Empty();
        }

        HashSet<string> members = new(shape.Members, StringComparer.Ordinal);
        HashSet<string> memberPaths = new(shape.MemberPaths, StringComparer.Ordinal);
        foreach (string baseType in shape.BaseTypes)
        {
            foreach (CSharpTypeShape candidate in ResolveCSharpTypeCandidates(
                shape,
                baseType,
                byName))
            {
                CSharpResolution inherited = ResolveCSharpMembers(
                    candidate,
                    byName,
                    visiting);
                members.UnionWith(inherited.Members);
                memberPaths.UnionWith(inherited.MemberPaths);
            }
        }

        foreach (CSharpReferenceEdge reference in shape.References)
        {
            foreach (CSharpTypeShape candidate in ResolveCSharpTypeCandidates(
                shape,
                reference.Target,
                byName))
            {
                CSharpResolution nested = ResolveCSharpMembers(
                    candidate,
                    byName,
                    visiting);
                foreach (string nestedPath in nested.MemberPaths)
                {
                    memberPaths.Add($"{reference.Prefix}:{nestedPath}");
                }
            }
        }

        visiting.Remove(identity);
        return new CSharpResolution(members, memberPaths);
    }

    private static IEnumerable<CSharpTypeShape> ResolveCSharpTypeCandidates(
        CSharpTypeShape _,
        string target,
        ILookup<string, CSharpTypeShape> byName) =>
        byName[target];

    private static bool IsDataTransferType(string relativePath, string kind, string name) =>
        kind.StartsWith("record", StringComparison.Ordinal)
        || string.Equals(kind, "interface", StringComparison.Ordinal)
        || relativePath.Contains("/Generated/", StringComparison.Ordinal)
        || DataTransferTypeMarkers.Any(marker => name.Contains(
            marker,
            StringComparison.Ordinal))
        || name is "User" or "ApiKey" or "Subscription" or "Account";

    private static void AddParameterMembers(
        string parameters,
        HashSet<string> members,
        HashSet<CSharpReferenceEdge> references)
    {
        foreach (string parameter in SplitTopLevel(parameters, ','))
        {
            string declaration = TruncateInitializer(parameter);
            MatchCollection identifiers = Identifier().Matches(declaration);
            if (identifiers.Count > 0)
            {
                string member = identifiers[^1].Value.TrimStart('@');
                members.Add(member);
                AddCSharpTypeReferences(identifiers, member, references);
                AddTupleElementMembers(declaration, members);
            }
        }
    }

    private static void AddDirectBodyMembers(
        string source,
        int bodyStart,
        int bodyEnd,
        HashSet<string> members,
        HashSet<CSharpReferenceEdge> references)
    {
        int depth = 0;
        int statementStart = bodyStart + 1;
        for (int index = bodyStart + 1; index < bodyEnd; index++)
        {
            switch (source[index])
            {
                case '{':
                    if (depth == 0)
                    {
                        AddDeclaredMember(
                            source[statementStart..index],
                            members,
                            references);
                    }

                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        statementStart = index + 1;
                    }

                    break;
                case ';' when depth == 0:
                    AddDeclaredMember(
                        source[statementStart..index],
                        members,
                        references);
                    statementStart = index + 1;
                    break;
            }
        }
    }

    private static bool IsIdentifierCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character)
        || character is '_';

    private static void AddDeclaredMember(
        string declaration,
        HashSet<string> members,
        HashSet<CSharpReferenceEdge> references)
    {
        string candidate = declaration;
        int expressionBody = candidate.IndexOf("=>", StringComparison.Ordinal);
        if (expressionBody >= 0)
        {
            candidate = candidate[..expressionBody];
        }

        if (IsMethodDeclaration(TruncateInitializer(candidate)))
        {
            return;
        }

        MatchCollection? declaredType = null;
        foreach (string declarationPart in SplitTopLevel(candidate, ','))
        {
            string withoutInitializer = TruncateInitializer(declarationPart);
            MatchCollection identifiers = Identifier().Matches(withoutInitializer);
            if (identifiers.Count == 0)
            {
                continue;
            }

            string member = identifiers[^1].Value.TrimStart('@');
            if (member is not "get" and not "init" and not "set")
            {
                members.Add(member);
                declaredType ??= identifiers;
                AddCSharpTypeReferences(declaredType, member, references);
                AddTupleElementMembers(withoutInitializer, members);
            }
        }
    }

    private static void AddTupleElementMembers(
        string declaration,
        HashSet<string> members)
    {
        int tupleStart = FindTopLevelTupleStart(declaration);
        if (tupleStart < 0)
        {
            return;
        }

        int tupleEnd = FindMatching(declaration, tupleStart, '(', ')');
        if (tupleEnd < 0
            || !declaration[tupleStart..tupleEnd].Contains(','))
        {
            return;
        }

        foreach (string element in SplitTopLevel(
            declaration[(tupleStart + 1)..tupleEnd],
            ','))
        {
            MatchCollection identifiers = Identifier().Matches(element);
            if (identifiers.Count >= 2)
            {
                members.Add(identifiers[^1].Value.TrimStart('@'));
            }
        }
    }

    private static int FindTopLevelTupleStart(string declaration)
    {
        int square = 0;
        for (int index = 0; index < declaration.Length; index++)
        {
            square += declaration[index] == '[' ? 1 : declaration[index] == ']' ? -1 : 0;
            if (declaration[index] == '(' && square == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static void AddCSharpTypeReferences(
        MatchCollection identifiers,
        string member,
        HashSet<CSharpReferenceEdge> references)
    {
        if (identifiers.Count < 2)
        {
            return;
        }

        string target = identifiers[^2].Value.TrimStart('@');
        references.Add(new CSharpReferenceEdge(member, target));
    }

    private static bool IsMethodDeclaration(string source)
    {
        int square = 0;
        int angle = 0;
        for (int index = 0; index < source.Length; index++)
        {
            char character = source[index];
            square += character == '[' ? 1 : character == ']' ? -1 : 0;
            angle += character == '<' ? 1 : character == '>' ? -1 : 0;
            if (character != '(' || square != 0 || angle != 0)
            {
                continue;
            }

            int closing = FindMatching(source, index, '(', ')');
            if (closing < 0)
            {
                return true;
            }

            string remaining = source[(closing + 1)..].Trim();
            return remaining.Length == 0
                || remaining.StartsWith("where ", StringComparison.Ordinal)
                || remaining.Contains('(', StringComparison.Ordinal);
        }

        return false;
    }

    private static string TruncateInitializer(string declaration)
    {
        int initializer = declaration.IndexOf('=');
        return initializer < 0 ? declaration : declaration[..initializer];
    }

    private static IEnumerable<string> SplitTopLevel(string source, char separator)
    {
        int start = 0;
        int round = 0;
        int square = 0;
        int angle = 0;
        int curly = 0;
        for (int index = 0; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '(':
                    round++;
                    break;
                case ')':
                    round--;
                    break;
                case '[':
                    square++;
                    break;
                case ']':
                    square--;
                    break;
                case '<':
                    angle++;
                    break;
                case '>':
                    angle--;
                    break;
                case '{':
                    curly++;
                    break;
                case '}':
                    curly--;
                    break;
                default:
                    if (source[index] == separator
                        && round == 0
                        && square == 0
                        && angle == 0
                        && curly == 0)
                    {
                        yield return source[start..index];
                        start = index + 1;
                    }

                    break;
            }
        }

        yield return source[start..];
    }

    private static int FindTypeBodyStart(string source, int start)
    {
        for (int index = start; index < source.Length; index++)
        {
            if (source[index] is '{' or ';')
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindMatching(
        string source,
        int start,
        char opening,
        char closing)
    {
        int depth = 0;
        for (int index = start; index < source.Length; index++)
        {
            if (source[index] == opening)
            {
                depth++;
            }
            else if (source[index] == closing && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int SkipWhitespace(string source, int start)
    {
        int index = start;
        while (index < source.Length && char.IsWhiteSpace(source[index]))
        {
            index++;
        }

        return index;
    }

    private static IEnumerable<string> ReadConfigurationKeys(string source)
    {
        string commentsMasked = MaskComments(source);
        ConfigurationAccessMatch[] sectionAccesses =
            ReadSectionConfigurationAccesses(commentsMasked).ToArray();
        bool InsideSectionAccess(Match match) => sectionAccesses.Any(access =>
            match.Index >= access.Index
            && match.Index + match.Length <= access.Index + access.Length);

        return ConfigurationKey()
            .Matches(commentsMasked)
            .Where(match => !InsideSectionAccess(match))
            .Select(static match => match.Groups["key"].Value)
            .Concat(FlatConfigurationAccessKey()
                .Matches(commentsMasked)
                .Where(match => !InsideSectionAccess(match))
                .Select(static match => match.Groups["key"].Value))
            .Concat(sectionAccesses.Select(static access => access.Key))
            .Concat(EnvironmentVariableAccessKey()
                .Matches(commentsMasked)
                .Select(static match => match.Groups["key"].Value))
            .Concat(CommandLineConfigurationAssignment()
                .Matches(commentsMasked)
                .Select(static match => match.Groups["key"].Value))
            .Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<ConfigurationAccessMatch>
        ReadSectionConfigurationAccesses(string source)
    {
        foreach (Regex regex in new[]
        {
            SectionValueConfigurationAccessChain(),
            SectionIndexerConfigurationAccessChain(),
        })
        {
            foreach (Match match in regex.Matches(source))
            {
                string[] sections = match.Groups["section"].Captures
                    .Select(static capture => capture.Value)
                    .ToArray();
                Assert.NotEmpty(sections);
                yield return new ConfigurationAccessMatch(
                    string.Join(':', sections.Append(match.Groups["key"].Value)),
                    match.Index,
                    match.Length);
            }
        }
    }

    private static IEnumerable<string> ReadConfigurationKeysFromFile(string path)
    {
        string source = File.ReadAllText(path);
        string extension = Path.GetExtension(path);
        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return ReadJsonConfigurationKeys(source);
        }

        if (string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase))
        {
            return ReadYamlConfigurationKeys(source);
        }

        if (Path.GetFileName(path).Contains("Dockerfile", StringComparison.OrdinalIgnoreCase))
        {
            return ReadDockerfileConfigurationKeys(source);
        }

        return Path.GetFileName(path).Contains(".env", StringComparison.OrdinalIgnoreCase)
            ? ReadEnvironmentConfigurationKeys(source)
            : ReadConfigurationKeys(source);
    }

    private static string[] ReadJsonConfigurationKeys(string source)
    {
        using JsonDocument document = JsonDocument.Parse(
            source,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        List<string> keys = [];
        AddJsonConfigurationKeys(document.RootElement, prefix: null, keys);
        return keys.ToArray();
    }

    private static void AddJsonConfigurationKeys(
        JsonElement element,
        string? prefix,
        List<string> keys)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string key = prefix is null
                    ? property.Name
                    : $"{prefix}:{property.Name}";
                keys.Add(key);
                if ((string.Equals(
                        property.Name,
                        "name",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        property.Name,
                        "target",
                        StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { } name
                    && IsEnvironmentKeyName(name))
                {
                    keys.Add(name);
                }

                AddJsonConfigurationKeys(property.Value, key, keys);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && item.GetString() is { } assignment)
                {
                    Match environment = EnvironmentAssignment().Match(assignment);
                    if (environment.Success)
                    {
                        keys.Add(environment.Groups["key"].Value);
                    }

                    keys.AddRange(CommandLineConfigurationAssignment()
                        .Matches(assignment)
                        .Select(static match => match.Groups["key"].Value));
                }

                AddJsonConfigurationKeys(item, prefix, keys);
            }
        }
    }

    private static string[] ReadYamlConfigurationKeys(string source)
    {
        YamlConfigurationState state = new();
        foreach (string rawLine in source.Split('\n'))
        {
            AddYamlConfigurationLine(rawLine, state);
        }

        AddMergedYamlConfigurationKeys(state.Merges, state.AnchorPaths, state.Keys);

        return state.Keys.ToArray();
    }

    private static void AddYamlConfigurationLine(
        string rawLine,
        YamlConfigurationState state)
    {
        string line = StripYamlComment(rawLine.TrimEnd('\r'));
        string trimmed = line.TrimStart();
        if (trimmed.Length == 0)
        {
            return;
        }

        Match environment = EnvironmentAssignment().Match(trimmed);
        if (environment.Success)
        {
            state.Keys.Add(environment.Groups["key"].Value);
            return;
        }

        AddInlineConfigurationKeys(trimmed, state.Keys);
        int indentation = LeadingSpaces(line);
        while (state.Ancestors.Count > 0
            && state.Ancestors[^1].Indentation >= indentation)
        {
            state.Ancestors.RemoveAt(state.Ancestors.Count - 1);
        }

        state.AnchorFrames.RemoveAll(frame => frame.Indentation >= indentation);
        Match merge = YamlMergeAssignment().Match(trimmed);
        if (merge.Success)
        {
            state.Merges.Add(new YamlMergeReference(
                string.Join(':', state.Ancestors.Select(static ancestor => ancestor.Key)),
                merge.Groups["anchor"].Value));
            return;
        }

        Match mergeList = YamlMergeListAssignment().Match(trimmed);
        if (mergeList.Success)
        {
            string parent = string.Join(
                ':',
                state.Ancestors.Select(static ancestor => ancestor.Key));
            foreach (Match alias in YamlAliasReference().Matches(
                mergeList.Groups["aliases"].Value))
            {
                state.Merges.Add(new YamlMergeReference(
                    parent,
                    alias.Groups["anchor"].Value));
            }

            return;
        }

        Match mapping = YamlMappingKey().Match(line);
        if (mapping.Success)
        {
            AddYamlMapping(line, indentation, mapping, state);
        }
    }

    private static void AddInlineConfigurationKeys(string line, List<string> keys)
    {
        keys.AddRange(CommandLineConfigurationAssignment()
            .Matches(line)
            .Select(static match => match.Groups["key"].Value));
        keys.AddRange(FlowEnvironmentAssignment()
            .Matches(line)
            .Select(static match => match.Groups["key"].Value));
    }

    private static void AddYamlMapping(
        string line,
        int indentation,
        Match mapping,
        YamlConfigurationState state)
    {
        string key = mapping.Groups["key"].Value;
        string path = string.Join(
            ':',
            state.Ancestors.Select(static ancestor => ancestor.Key).Append(key));
        state.Keys.Add(path);
        string value = line[(mapping.Index + mapping.Length)..].Trim();
        AddYamlAnchorRelativePaths(path, state.AnchorFrames, state.AnchorPaths);
        if ((string.Equals(key, "name", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "target", StringComparison.OrdinalIgnoreCase))
            && TryReadYamlScalar(value, out string scalarName)
            && TryResolveYamlEnvironmentName(scalarName, state, out string environmentName))
        {
            state.Keys.Add(environmentName);
        }

        state.Keys.AddRange(CommandLineConfigurationAssignment()
            .Matches(value)
            .Select(static match => match.Groups["key"].Value));

        Match anchor = YamlAnchorDefinition().Match(value);
        bool scalarAnchor = false;
        if (anchor.Success)
        {
            string anchorName = anchor.Groups["anchor"].Value;
            if (TryReadYamlScalar(value, out string anchoredScalar))
            {
                Assert.True(
                    state.AnchorScalars.TryAdd(anchorName, anchoredScalar),
                    $"Duplicate YAML scalar anchor '{anchorName}'.");
                scalarAnchor = true;
            }
            else
            {
                state.AnchorPaths.TryAdd(anchorName, []);
                state.AnchorFrames.Add(new YamlAnchorFrame(indentation, anchorName, path));
            }
        }

        Match aliasValue = YamlAliasOnly().Match(value);
        if (aliasValue.Success
            && !string.Equals(key, "name", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(key, "target", StringComparison.OrdinalIgnoreCase))
        {
            state.Merges.Add(new YamlMergeReference(
                path,
                aliasValue.Groups["anchor"].Value));
        }

        if (value.Length == 0 || (anchor.Success && !scalarAnchor))
        {
            state.Ancestors.Add((indentation, key));
        }
        else if (value.StartsWith('{') || value.StartsWith('['))
        {
            int cursor = 0;
            AddFlowYamlValue(value, ref cursor, path, state);
        }
    }

    private static bool TryResolveYamlEnvironmentName(
        string scalar,
        YamlConfigurationState state,
        out string environmentName)
    {
        Match alias = YamlAliasOnly().Match(scalar);
        if (alias.Success)
        {
            Assert.True(
                state.AnchorScalars.TryGetValue(
                    alias.Groups["anchor"].Value,
                    out string? anchoredScalar),
                $"Unknown YAML scalar anchor '{alias.Groups["anchor"].Value}'.");
            environmentName = Assert.IsType<string>(anchoredScalar);
        }
        else
        {
            environmentName = scalar;
        }

        return IsEnvironmentKeyName(environmentName);
    }

    private static void AddYamlAnchorRelativePaths(
        string path,
        List<YamlAnchorFrame> frames,
        Dictionary<string, List<string>> anchorPaths)
    {
        foreach (YamlAnchorFrame frame in frames)
        {
            if (!path.StartsWith($"{frame.RootPath}:", StringComparison.Ordinal))
            {
                continue;
            }

            string relative = path[(frame.RootPath.Length + 1)..];
            anchorPaths[frame.Name].Add(relative);
        }
    }

    private static void AddMergedYamlConfigurationKeys(
        IEnumerable<YamlMergeReference> merges,
        Dictionary<string, List<string>> anchorPaths,
        List<string> keys)
    {
        foreach (YamlMergeReference merge in merges)
        {
            Assert.True(
                anchorPaths.TryGetValue(merge.Anchor, out List<string>? relativePaths),
                $"Unknown YAML configuration anchor '{merge.Anchor}'.");
            Assert.NotNull(relativePaths);
            foreach (string relativePath in relativePaths)
            {
                keys.Add(merge.ParentPath.Length == 0
                    ? relativePath
                    : $"{merge.ParentPath}:{relativePath}");
            }
        }
    }

    private static bool TryReadYamlScalar(string value, out string scalar)
    {
        scalar = value.Trim();
        Match anchor = YamlAnchorDefinition().Match(scalar);
        if (anchor.Success)
        {
            scalar = scalar[anchor.Length..].Trim();
        }

        if (scalar.Length >= 2
            && scalar[0] is '\'' or '"'
            && scalar[^1] == scalar[0])
        {
            scalar = scalar[1..^1];
        }

        return scalar.Length > 0 && scalar.IndexOfAny(['{', '[']) < 0;
    }

    private static void AddFlowYamlValue(
        string source,
        ref int cursor,
        string prefix,
        YamlConfigurationState state)
    {
        SkipFlowWhitespace(source, ref cursor);
        if (cursor >= source.Length)
        {
            return;
        }

        if (source[cursor] == '{')
        {
            AddFlowYamlMap(source, ref cursor, prefix, state);
        }
        else if (source[cursor] == '[')
        {
            AddFlowYamlSequence(source, ref cursor, prefix, state);
        }
        else
        {
            SkipFlowScalar(source, ref cursor);
        }
    }

    private static void AddFlowYamlMap(
        string source,
        ref int cursor,
        string prefix,
        YamlConfigurationState state)
    {
        cursor++;
        while (cursor < source.Length)
        {
            SkipFlowWhitespaceAndCommas(source, ref cursor);
            if (cursor >= source.Length || source[cursor] == '}')
            {
                cursor++;
                return;
            }

            string key = ReadFlowYamlKey(source, ref cursor);
            SkipFlowWhitespace(source, ref cursor);
            Assert.True(cursor < source.Length && source[cursor] == ':');
            cursor++;
            string path = prefix.Length == 0 ? key : $"{prefix}:{key}";
            state.Keys.Add(path);
            int valueStart = cursor;
            AddFlowYamlValue(source, ref cursor, path, state);
            if ((string.Equals(key, "name", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "target", StringComparison.OrdinalIgnoreCase))
                && TryReadYamlScalar(
                    source[valueStart..cursor].Trim().TrimEnd(','),
                    out string scalarName)
                && TryResolveYamlEnvironmentName(
                    scalarName,
                    state,
                    out string environmentName))
            {
                state.Keys.Add(environmentName);
            }
        }
    }

    private static void AddFlowYamlSequence(
        string source,
        ref int cursor,
        string prefix,
        YamlConfigurationState state)
    {
        cursor++;
        while (cursor < source.Length)
        {
            SkipFlowWhitespaceAndCommas(source, ref cursor);
            if (cursor >= source.Length || source[cursor] == ']')
            {
                cursor++;
                return;
            }

            int start = cursor;
            AddFlowYamlValue(source, ref cursor, prefix, state);
            string scalar = source[start..cursor].Trim().Trim('\'', '"');
            Match assignment = EnvironmentAssignment().Match(scalar);
            if (assignment.Success)
            {
                state.Keys.Add(assignment.Groups["key"].Value);
            }


            state.Keys.AddRange(CommandLineConfigurationAssignment()
                .Matches(scalar)
                .Select(static match => match.Groups["key"].Value));
        }
    }

    private static string ReadFlowYamlKey(string source, ref int cursor)
    {
        SkipFlowWhitespace(source, ref cursor);
        char quote = source[cursor] is '\'' or '"' ? source[cursor++] : '\0';
        int start = cursor;
        while (cursor < source.Length
            && (quote == '\0' ? source[cursor] != ':' : source[cursor] != quote))
        {
            cursor++;
        }

        string key = source[start..cursor].Trim();
        if (quote != '\0')
        {
            cursor++;
        }

        Assert.True(key.Length > 0);
        return key;
    }

    private static void SkipFlowScalar(string source, ref int cursor)
    {
        char quote = source[cursor] is '\'' or '"' ? source[cursor++] : '\0';
        while (cursor < source.Length)
        {
            if (quote == '\0' && source[cursor] is ',' or '}' or ']')
            {
                return;
            }

            if (quote != '\0' && source[cursor] == quote)
            {
                cursor++;
                return;
            }

            cursor++;
        }
    }

    private static void SkipFlowWhitespaceAndCommas(string source, ref int cursor)
    {
        while (cursor < source.Length
            && (char.IsWhiteSpace(source[cursor]) || source[cursor] == ','))
        {
            cursor++;
        }
    }

    private static void SkipFlowWhitespace(string source, ref int cursor)
    {
        while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
        {
            cursor++;
        }
    }

    private static string[] ReadEnvironmentConfigurationKeys(string source) => source
        .Split('\n')
        .Select(static line => line.Trim())
        .Where(static line => line.Length > 0 && !line.StartsWith('#'))
        .Select(static line => EnvironmentAssignment().Match(line))
        .Where(static match => match.Success)
        .Select(static match => match.Groups["key"].Value)
        .ToArray();

    private static string[] ReadDockerfileConfigurationKeys(string source)
    {
        string logicalSource = source
            .Replace("\\\r\n", " ", StringComparison.Ordinal)
            .Replace("\\\n", " ", StringComparison.Ordinal);
        List<string> keys = [];
        foreach (string line in logicalSource.Split('\n'))
        {
            string trimmed = line.Trim();
            keys.AddRange(CommandLineConfigurationAssignment()
                .Matches(trimmed)
                .Select(static match => match.Groups["key"].Value));
            if (!trimmed.StartsWith("ENV ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string assignments = trimmed["ENV ".Length..].Trim();
            MatchCollection equalsAssignments = DockerEnvironmentAssignment()
                .Matches(assignments);
            if (equalsAssignments.Count > 0)
            {
                keys.AddRange(equalsAssignments.Select(
                    static match => match.Groups["key"].Value));
                continue;
            }

            Match legacy = DockerLegacyEnvironmentAssignment().Match(assignments);
            if (legacy.Success)
            {
                keys.Add(legacy.Groups["key"].Value);
            }
        }

        return keys.ToArray();
    }

    private static bool IsEnvironmentKeyName(string candidate) =>
        EnvironmentKeyName().IsMatch(candidate);

    private static string MaskCommentsAndLiterals(string source) =>
        MaskSource(source, maskLiterals: true);

    private static string MaskComments(string source) =>
        MaskSource(source, maskLiterals: false);

    private static string MaskSource(string source, bool maskLiterals)
    {
        char[] buffer = source.ToCharArray();
        for (int index = 0; index < source.Length; index++)
        {
            if (source[index] == '/'
                && index + 1 < source.Length
                && source[index + 1] is '/' or '*')
            {
                int end = source[index + 1] == '/'
                    ? FindLineEnd(source, index + 2)
                    : FindBlockCommentEnd(source, index + 2);
                MaskRange(buffer, index, end);
                index = end - 1;
                continue;
            }

            if (source[index] == '"')
            {
                int end = FindStringEnd(source, index);
                if (maskLiterals)
                {
                    MaskRange(buffer, index, end);
                }

                index = end - 1;
                continue;
            }

            if (source[index] == '\'')
            {
                int end = FindCharacterEnd(source, index);
                if (maskLiterals)
                {
                    MaskRange(buffer, index, end);
                }

                index = end - 1;
            }
        }

        return new string(buffer);
    }

    private static int FindLineEnd(string source, int start)
    {
        int newline = source.IndexOf('\n', start);
        return newline < 0 ? source.Length : newline;
    }

    private static int FindBlockCommentEnd(string source, int start)
    {
        int end = source.IndexOf("*/", start, StringComparison.Ordinal);
        return end < 0 ? source.Length : end + 2;
    }

    private static int FindStringEnd(string source, int start)
    {
        int quoteCount = 1;
        while (start + quoteCount < source.Length
            && source[start + quoteCount] == '"')
        {
            quoteCount++;
        }

        if (quoteCount >= 3)
        {
            string delimiter = new('"', quoteCount);
            int rawEnd = source.IndexOf(
                delimiter,
                start + quoteCount,
                StringComparison.Ordinal);
            return rawEnd < 0 ? source.Length : rawEnd + quoteCount;
        }

        bool verbatim = start > 0 && source[start - 1] == '@';
        for (int index = start + 1; index < source.Length; index++)
        {
            if (!verbatim && source[index] == '\\')
            {
                index++;
                continue;
            }

            if (source[index] != '"')
            {
                continue;
            }

            if (verbatim
                && index + 1 < source.Length
                && source[index + 1] == '"')
            {
                index++;
                continue;
            }

            return index + 1;
        }

        return source.Length;
    }

    private static int FindCharacterEnd(string source, int start)
    {
        for (int index = start + 1; index < source.Length; index++)
        {
            if (source[index] == '\\')
            {
                index++;
                continue;
            }

            if (source[index] == '\'')
            {
                return index + 1;
            }
        }

        return source.Length;
    }

    private static void MaskRange(char[] buffer, int start, int end)
    {
        for (int index = start; index < end; index++)
        {
            if (buffer[index] is not '\r' and not '\n')
            {
                buffer[index] = ' ';
            }
        }
    }

    private static string MaskSqlCommentsAndLiterals(string source)
    {
        StringBuilder buffer = new(source.Length);
        for (int index = 0; index < source.Length; index++)
        {
            if (source[index] == '-'
                && index + 1 < source.Length
                && source[index + 1] == '-')
            {
                int end = FindLineEnd(source, index + 2);
                AppendSqlMasked(buffer, source, index, end);
                index = end - 1;
                continue;
            }

            if (source[index] == '/'
                && index + 1 < source.Length
                && source[index + 1] == '*')
            {
                int end = FindBlockCommentEnd(source, index + 2);
                AppendSqlMasked(buffer, source, index, end);
                index = end - 1;
                continue;
            }

            if (source[index] == '\'')
            {
                int end = FindSqlQuotedEnd(source, index, '\'');
                AppendSqlMasked(buffer, source, index, end);
                index = end - 1;
                continue;
            }

            if (source[index] == '"')
            {
                AppendSqlQuotedIdentifier(buffer, source, ref index);
                continue;
            }

            if (source[index] == '$'
                && TryFindDollarQuoteEnd(source, index, out int dollarEnd))
            {
                AppendSqlMasked(buffer, source, index, dollarEnd);
                index = dollarEnd - 1;
                continue;
            }

            buffer.Append(source[index]);
        }

        return buffer.ToString();
    }

    private static void AppendSqlQuotedIdentifier(
        StringBuilder buffer,
        string source,
        ref int index)
    {
        StringBuilder identifier = new();
        for (index++; index < source.Length; index++)
        {
            if (source[index] != '"')
            {
                identifier.Append(char.IsAsciiLetterOrDigit(source[index])
                    || source[index] is '_' or '$'
                    ? source[index]
                    : '_');
                continue;
            }

            if (index + 1 < source.Length && source[index + 1] == '"')
            {
                identifier.Append('_');
                index++;
                continue;
            }

            buffer.Append(NormalizeQuotedSqlIdentifier(identifier.ToString()));
            return;
        }

        buffer.Append(NormalizeQuotedSqlIdentifier(identifier.ToString()));
    }

    private static string NormalizeQuotedSqlIdentifier(string identifier)
    {
        bool requiresQuotedIdentity = identifier.Any(char.IsAsciiLetterUpper);
        StringBuilder normalized = new(identifier.Length + 4);
        for (int index = 0; index < identifier.Length; index++)
        {
            char current = identifier[index];
            bool wordBoundary = char.IsAsciiLetterUpper(current)
                && index > 0
                && (char.IsAsciiLetterLower(identifier[index - 1])
                    || char.IsAsciiDigit(identifier[index - 1])
                    || (index + 1 < identifier.Length
                        && char.IsAsciiLetterLower(identifier[index + 1])));
            if (wordBoundary && normalized[^1] != '_')
            {
                normalized.Append('_');
            }

            normalized.Append(char.ToLowerInvariant(current));
        }

        return requiresQuotedIdentity
            ? $"quoted__{normalized}"
            : normalized.ToString();
    }

    private static void AppendSqlMasked(
        StringBuilder buffer,
        string source,
        int start,
        int end)
    {
        for (int index = start; index < end; index++)
        {
            buffer.Append(source[index] is '\r' or '\n' ? source[index] : ' ');
        }
    }

    private static int FindSqlQuotedEnd(string source, int start, char quote)
    {
        for (int index = start + 1; index < source.Length; index++)
        {
            if (source[index] == '\\')
            {
                index++;
                continue;
            }

            if (source[index] != quote)
            {
                continue;
            }

            if (index + 1 < source.Length && source[index + 1] == quote)
            {
                index++;
                continue;
            }

            return index + 1;
        }

        return source.Length;
    }

    private static bool TryFindDollarQuoteEnd(
        string source,
        int start,
        out int end)
    {
        end = start;
        if (start > 0 && IsIdentifierCharacter(source[start - 1]))
        {
            return false;
        }

        int delimiterEnd = start + 1;
        while (delimiterEnd < source.Length
            && IsIdentifierCharacter(source[delimiterEnd]))
        {
            delimiterEnd++;
        }

        if (delimiterEnd >= source.Length || source[delimiterEnd] != '$')
        {
            return false;
        }

        string delimiter = source[start..(delimiterEnd + 1)];
        int closing = source.IndexOf(
            delimiter,
            delimiterEnd + 1,
            StringComparison.Ordinal);
        end = closing < 0 ? source.Length : closing + delimiter.Length;
        return true;
    }

    private static string[] ProductionSourceFiles(string root) =>
        Directory
            .GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Split(Path.DirectorySeparatorChar)
                .Any(static segment => segment is "bin" or "obj"))
            .ToArray();

    private static string[] ProductionConfigurationFiles(string root) =>
        ProductionSourceFiles(root)
            .Concat(Directory.GetFiles(
                Path.Combine(root, "deploy"),
                "*",
                SearchOption.AllDirectories)
                .Where(static path => IsDeploymentConfigurationFile(path)))
            .Concat(Directory.GetFiles(
                Path.Combine(root, "src"),
                "appsettings*.json",
                SearchOption.AllDirectories))
            .Where(static path => !path.Split(Path.DirectorySeparatorChar)
                .Any(static segment => segment is "bin" or "obj"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool IsDeploymentConfigurationFile(string path)
    {
        string extension = Path.GetExtension(path);
        string fileName = Path.GetFileName(path);
        return string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("Dockerfile", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        @"\b(?:(?:public|internal|private|protected|file|sealed|abstract|static|partial|readonly|ref)\s+)*(?<kind>record(?:\s+(?:class|struct))?|class|struct|interface)\s+@?(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(
        @"^\s*using\s+@?(?<alias>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:global::)?(?:@?[A-Za-z_][A-Za-z0-9_]*\.)*@?(?<target>[A-Za-z_][A-Za-z0-9_]*)\s*;",
        RegexOptions.CultureInvariant
            | RegexOptions.ExplicitCapture
            | RegexOptions.Multiline,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex UsingAlias();

    [GeneratedRegex(
        @"^\s*(?:global\s+)?using\s+@?[A-Za-z_][A-Za-z0-9_]*\s*=",
        RegexOptions.CultureInvariant | RegexOptions.Multiline,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex CSharpUsingAliasDirective();

    [GeneratedRegex(
        @"\\(?:u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8})",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex CSharpIdentifierEscape();

    [GeneratedRegex(
        @"\bnamespace\s+(?<namespace>[A-Za-z_][A-Za-z0-9_.]*)\s*;",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex NamespaceDeclaration();

    [GeneratedRegex(
        @"[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex Identifier();

    [GeneratedRegex(
        @"[A-Z]+(?=[A-Z][a-z]|[0-9]|\b)|[A-Z]?[a-z]+|[0-9]+",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex IdentifierWord();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?<key>[A-Za-z][A-Za-z0-9_]*(?:(?::|__)[A-Za-z0-9_]+)+)(?![A-Za-z0-9_])",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex ConfigurationKey();

    [GeneratedRegex(
        "(?:\\b(?:config|configuration|[A-Za-z_][A-Za-z0-9_]*(?:config|configuration)[A-Za-z0-9_]*)\\s*\\[\\s*|\\b(?:GetValue|GetSection|GetRequiredSection)(?:<[^>]+>)?\\s*\\(\\s*)[\\\"'](?<key>[A-Za-z][A-Za-z0-9_]*)[\\\"']",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex FlatConfigurationAccessKey();

    [GeneratedRegex(
        "(?:Get(?:Required)?Section\\s*\\(\\s*[\\\"'](?<section>[A-Za-z][A-Za-z0-9_]*(?:(?::|__)[A-Za-z0-9_]+)*)[\\\"']\\s*\\)\\s*\\.\\s*)+GetValue(?:<[^>]+>)?\\s*\\(\\s*[\\\"'](?<key>[A-Za-z][A-Za-z0-9_]*(?:(?::|__)[A-Za-z0-9_]+)*)[\\\"']",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex SectionValueConfigurationAccessChain();

    [GeneratedRegex(
        "(?:(?:Get(?:Required)?Section\\s*\\(\\s*[\\\"'](?<section>[A-Za-z][A-Za-z0-9_]*(?:(?::|__)[A-Za-z0-9_]+)*)[\\\"']\\s*\\)\\s*\\.\\s*)*Get(?:Required)?Section\\s*\\(\\s*[\\\"'](?<section>[A-Za-z][A-Za-z0-9_]*(?:(?::|__)[A-Za-z0-9_]+)*)[\\\"']\\s*\\))\\s*\\[\\s*[\\\"'](?<key>[A-Za-z][A-Za-z0-9_]*(?:(?::|__)[A-Za-z0-9_]+)*)[\\\"']\\s*\\]",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex SectionIndexerConfigurationAccessChain();

    [GeneratedRegex(
        "\\bEnvironment\\s*\\.\\s*GetEnvironmentVariable\\s*\\(\\s*[\\\"'](?<key>[A-Za-z][A-Za-z0-9_]*(?:(?::|__)[A-Za-z0-9_]+)*)[\\\"']",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex EnvironmentVariableAccessKey();

    [GeneratedRegex(
        "(?:^|[\\s\\[,\\\"'])--(?<key>[A-Za-z][A-Za-z0-9_]*(?:(?::|__)[A-Za-z0-9_]+)+)(?=\\s*=|\\s|[,\\]\\\"']|$)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex CommandLineConfigurationAssignment();

    [GeneratedRegex(
        "^\\s*(?:-\\s+)?(?:export\\s+)?(?:--)?[\\\"']?(?<key>[A-Za-z][A-Za-z0-9_]*(?:(?::|__)[A-Za-z0-9_]+)*)[\\\"']?\\s*=",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex EnvironmentAssignment();

    [GeneratedRegex(
        "(?:^|[\\[,])\\s*(?:--)?[\\\"']?(?<key>[A-Za-z][A-Za-z0-9_]*(?:(?::|__)[A-Za-z0-9_]+)*)[\\\"']?\\s*=",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex FlowEnvironmentAssignment();

    [GeneratedRegex(
        @"^(?<key>[A-Za-z_][A-Za-z0-9_]*(?:(?::|__)[A-Za-z0-9_]+)*)$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex EnvironmentKeyName();

    [GeneratedRegex(
        "^\\s*(?:-\\s+)?[\\\"']?(?<key>[A-Za-z][A-Za-z0-9_-]*)[\\\"']?\\s*:",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex YamlMappingKey();

    [GeneratedRegex(
        @"^&(?<anchor>[A-Za-z_][A-Za-z0-9_-]*)(?:\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex YamlAnchorDefinition();

    [GeneratedRegex(
        @"^<<\s*:\s*\*(?<anchor>[A-Za-z_][A-Za-z0-9_-]*)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex YamlMergeAssignment();

    [GeneratedRegex(
        @"^<<\s*:\s*\[(?<aliases>[^\]]+)\]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex YamlMergeListAssignment();

    [GeneratedRegex(
        @"\*(?<anchor>[A-Za-z_][A-Za-z0-9_-]*)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex YamlAliasReference();

    [GeneratedRegex(
        @"^\*(?<anchor>[A-Za-z_][A-Za-z0-9_-]*)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex YamlAliasOnly();

    [GeneratedRegex(
        @"^&[A-Za-z_][A-Za-z0-9_-]*(?:\s*#.*)?$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex YamlAnchorOnly();

    [GeneratedRegex(
        "(?:^|[,{])\\s*[\\\"']?(?<key>[A-Za-z][A-Za-z0-9_]*)[\\\"']?\\s*:",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex FlowYamlMappingKey();

    [GeneratedRegex(
        @"(?:^|\s)(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex DockerEnvironmentAssignment();

    [GeneratedRegex(
        @"^(?<key>[A-Za-z_][A-Za-z0-9_]*)\s+",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex DockerLegacyEnvironmentAssignment();

    [GeneratedRegex(
        @"(?:^|[-{,]\s*)(?:\$ref|['""]\$ref['""])\s*:\s*['""]#/components/schemas/(?<schema>[A-Za-z_][A-Za-z0-9_-]*)['""]",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex LocalOpenApiSchemaReference();

    [GeneratedRegex(
        @"(?:^|[-{,]\s*)(?:\$ref|['""]\$ref['""])\s*:",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex OpenApiReferenceKey();

    [GeneratedRegex(
        @"(?:^|\s)[&*][A-Za-z_][A-Za-z0-9_-]*",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex YamlAnchorOrAlias();

    [GeneratedRegex(
        @"\bCREATE\s+(?:UNLOGGED\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?<schema>[a-z_][a-z0-9_$]*)\.)?(?<table>[a-z_][a-z0-9_$]*)\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex CreateTable();

    [GeneratedRegex(
        @"\bCREATE\s+(?:UNLOGGED\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?<schema>[a-z_][a-z0-9_$]*)\.)?(?<table>[a-z_][a-z0-9_$]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex CreateTableStart();

    [GeneratedRegex(
        @"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:MATERIALIZED\s+)?VIEW\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex CreateView();

    [GeneratedRegex(
        @"\bDROP\s+TABLE\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex DropTable();

    [GeneratedRegex(
        @"(?:\(\s*LIKE\b|\bINHERITS\s*\()",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex CreateTableInheritance();

    [GeneratedRegex(
        @"\bU&\s*""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex SqlUnicodeQuotedIdentifier();

    [GeneratedRegex(
        @"\bALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?(?:(?<schema>[a-z_][a-z0-9_$]*)\.)?(?<table>[a-z_][a-z0-9_$]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex AlterTable();

    [GeneratedRegex(
        @"^\s*ADD\s+(?:COLUMN\s+)?(?:IF\s+NOT\s+EXISTS\s+)?(?<column>[a-z_][a-z0-9_$]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex AlterAddColumn();

    [GeneratedRegex(
        @"^\s*DROP\s+(?:COLUMN\s+)?(?:IF\s+EXISTS\s+)?(?<column>[a-z_][a-z0-9_$]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex AlterDropColumn();

    [GeneratedRegex(
        @"^\s*RENAME\s+(?:COLUMN\s+)?(?<old>[a-z_][a-z0-9_$]*)\s+TO\s+(?<new>[a-z_][a-z0-9_$]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex AlterRenameColumn();

    [GeneratedRegex(
        @"^\s*RENAME\s+TO\s+(?<new>[a-z_][a-z0-9_$]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex AlterRenameTable();

    [GeneratedRegex(
        @"^\s*ALTER\s+(?:COLUMN\s+)?(?<column>[a-z_][a-z0-9_$]*)\s+SET\s+DEFAULT\b[\s\S]*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex AlterSetColumnDefault();

    [GeneratedRegex(
        @"^\s*VALIDATE\s+CONSTRAINT\s+[a-z_][a-z0-9_$]*\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex AlterValidateConstraint();

    [GeneratedRegex(
        @"^\s*(?<identifier>[a-z_][a-z0-9_$]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex SqlLeadingIdentifier();

    private sealed record CSharpTypeShape(
        string RelativePath,
        string Name,
        bool IsDataTransferType,
        HashSet<string> Members)
    {
        internal HashSet<string> BaseTypes { get; init; } =
            new HashSet<string>(StringComparer.Ordinal);

        internal HashSet<string> MemberPaths { get; init; } =
            new HashSet<string>(StringComparer.Ordinal);

        internal string Namespace { get; init; } = string.Empty;

        internal HashSet<CSharpReferenceEdge> References { get; init; } = [];
    }

    private sealed record CSharpReferenceEdge(string Prefix, string Target);

    private sealed record ConfigurationAccessMatch(
        string Key,
        int Index,
        int Length);

    private sealed record CSharpResolution(
        HashSet<string> Members,
        HashSet<string> MemberPaths)
    {
        internal static CSharpResolution Empty() => new(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed record OpenApiSchemaShape(
        HashSet<string> PropertyPaths,
        HashSet<string> TopLevelProperties)
    {
        internal HashSet<OpenApiReferenceEdge> References { get; init; } = [];

        internal HashSet<string> Properties => PropertyPaths
            .Select(static path => path.Split(':')[^1])
            .ToHashSet(StringComparer.Ordinal);

        internal static OpenApiSchemaShape Empty() => new(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed record OpenApiReferenceEdge(string Prefix, string Target);

    private sealed record OpenApiRequestSchemaBinding(
        string Path,
        string Method,
        string Schema,
        bool IsRequestBody);

    private sealed record OpenApiReferenceScope(
        int Indentation,
        bool RejectReferences);

    private sealed record OpenApiPropertiesBlock(int Indentation, string Prefix);

    private sealed record OpenApiPropertyFrame(int Indentation, string Path);

    private sealed record OpenApiRequiredBlock(int Indentation, string Prefix);

    private sealed record YamlAnchorFrame(
        int Indentation,
        string Name,
        string RootPath);

    private sealed record YamlMergeReference(string ParentPath, string Anchor);

    private sealed class YamlConfigurationState
    {
        internal List<(int Indentation, string Key)> Ancestors { get; } = [];

        internal List<YamlAnchorFrame> AnchorFrames { get; } = [];

        internal Dictionary<string, List<string>> AnchorPaths { get; } =
            new(StringComparer.Ordinal);

        internal Dictionary<string, string> AnchorScalars { get; } =
            new(StringComparer.Ordinal);

        internal List<string> Keys { get; } = [];

        internal List<YamlMergeReference> Merges { get; } = [];
    }

    private sealed record SqlDdlStatement(int Index, bool IsCreate, Match Match);
}
