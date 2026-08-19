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

    private static readonly string[] DataTransferTypeMarkers =
    [
        "Actor",
        "Binding",
        "Candidate",
        "Command",
        "Configuration",
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

    private static readonly string[] QuotaBoundaryDatabaseTables =
    [
        "users",
        "api_keys",
        "subscriptions",
        "accounts",
        "group_token_quotas",
        "group_quota_periods",
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

        foreach (string personalTable in new[]
        {
            "users",
            "api_keys",
            "subscriptions",
            "accounts",
        })
        {
            HashSet<string> columns = RequireDatabaseTable(tables, personalTable);
            foreach (string column in columns
                .Where(static column => IsDatabaseQuotaAuthorityColumn(column))
                .Order(StringComparer.Ordinal))
            {
                violations.Add($"PostgreSQL table::{personalTable}::{column}");
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
        Dictionary<string, HashSet<string>> schemas = ReadOpenApiSchemaProperties(
            File.ReadAllText(openApiPath));

        Assert.True(
            schemas.TryGetValue("GroupQuota", out HashSet<string>? groupQuotaProperties),
            "The authoritative OpenAPI contract must define GroupQuota.");
        Assert.NotNull(groupQuotaProperties);
        AssertCanonicalAuthority(
            groupQuotaProperties,
            CanonicalOpenApiAuthorityProperties,
            "OpenAPI GroupQuota");

        foreach ((string schema, HashSet<string> properties) in schemas
            .Where(static pair => IsPersonalQuotaSubject(pair.Key)))
        {
            AddAuthorityViolations(
                violations,
                "OpenAPI schema",
                schema,
                properties);
        }
    }

    private static void AssertProductionDtoBoundary(
        string root,
        List<string> violations)
    {
        CSharpTypeShape[] productionShapes = ProductionSourceFiles(root)
            .SelectMany(path => ReadCSharpTypeShapes(
                root,
                path,
                File.ReadAllText(path)))
            .ToArray();
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
                    StringComparison.Ordinal));
        Assert.NotNull(generatedGroupQuota);
        AssertCanonicalAuthority(
            generatedGroupQuota.Members,
            CanonicalCSharpAuthorityMembers,
            "generated GroupQuota DTO");

        foreach (CSharpTypeShape shape in productionShapes.Where(
            static shape => shape.IsDataTransferType
                && IsPersonalQuotaSubject(shape.Name)
                && !IsApprovedGroupQuotaProjection(shape)))
        {
            AddAuthorityViolations(
                violations,
                "production DTO",
                $"{shape.RelativePath}::{shape.Name}",
                shape.Members);
        }
    }

    private static void AssertConfigurationBoundary(
        string root,
        List<string> violations)
    {
        string[] configurationKeys = ProductionSourceFiles(root)
            .SelectMany(path => ReadConfigurationKeys(File.ReadAllText(path)))
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
        IReadOnlySet<string> actual,
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
        if (words.Contains("quota", StringComparer.Ordinal))
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

        return words.Any(static word => word is
            "allocated" or
            "allowance" or
            "available" or
            "balance" or
            "budget" or
            "cap" or
            "consumed" or
            "limit" or
            "overage" or
            "remaining" or
            "reserved");
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

    private static bool IsPersonalQuotaSubject(string name)
    {
        string[] words = IdentifierWords(name);
        return words.Contains("user", StringComparer.Ordinal)
            || words.Contains("subscription", StringComparer.Ordinal)
            || words.Contains("account", StringComparer.Ordinal)
            || (words.Contains("api", StringComparer.Ordinal)
                && words.Contains("key", StringComparer.Ordinal));
    }

    private static bool IsApprovedGroupQuotaProjection(CSharpTypeShape shape) =>
        string.Equals(shape.Name, "UserGroupPoolView", StringComparison.Ordinal)
        && shape.Members.Contains("GroupId")
        && CanonicalCSharpAuthorityMembers
            .Where(static member => !string.Equals(
                member,
                "OverageTokens",
                StringComparison.Ordinal))
            .All(shape.Members.Contains);

    private static bool IsPersonalQuotaConfigurationKey(string key)
    {
        string[] segments = key.Split(
            [":", "__"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool isPersonalSubject = segments.Any(static segment =>
            string.Equals(segment, "User", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "Users", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "ApiKey", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "ApiKeys", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "Subscription", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "Subscriptions", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "Account", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "Accounts", StringComparison.OrdinalIgnoreCase));
        return isPersonalSubject
            && segments.Any(static segment => IsCumulativeQuotaAuthorityMember(segment));
    }

    private static string[] IdentifierWords(string identifier) =>
        IdentifierWord()
            .Matches(identifier.Replace('_', ' '))
            .Select(static match => match.Value.ToLowerInvariant())
            .ToArray();

    private static Dictionary<string, HashSet<string>> ReadOpenApiSchemaProperties(
        string source)
    {
        Dictionary<string, HashSet<string>> schemas = new(StringComparer.Ordinal);
        string? currentSchema = null;
        bool inSchemas = false;
        bool inProperties = false;

        foreach (string rawLine in source.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();
            if (!inSchemas)
            {
                inSchemas = string.Equals(line, "  schemas:", StringComparison.Ordinal);
                continue;
            }

            if (trimmed.Length != 0
                && !trimmed.StartsWith('#')
                && LeadingSpaces(line) <= 2)
            {
                break;
            }

            if (LeadingSpaces(line) == 4
                && TryReadYamlKey(trimmed, out string schema))
            {
                currentSchema = schema;
                schemas.TryAdd(schema, new HashSet<string>(StringComparer.Ordinal));
                inProperties = false;
                continue;
            }

            if (currentSchema is null)
            {
                continue;
            }

            if (LeadingSpaces(line) == 6)
            {
                inProperties = string.Equals(trimmed, "properties:", StringComparison.Ordinal);
                continue;
            }

            if (inProperties
                && LeadingSpaces(line) == 8
                && TryReadYamlKey(trimmed, out string property))
            {
                schemas[currentSchema].Add(property);
            }
        }

        return schemas;
    }

    private static bool TryReadYamlKey(string line, out string key)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        key = colon > 0 ? line[..colon] : string.Empty;
        return key.Length > 0
            && key.All(static character => char.IsAsciiLetterOrDigit(character)
                || character is '_');
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
            SearchOption.TopDirectoryOnly))
        {
            string sql = MaskSqlCommentsAndLiterals(File.ReadAllText(migration));
            AddCreateTableColumns(sql, tables);
            AddAlterTableColumns(sql, tables);
        }

        return tables;
    }

    private static void AddCreateTableColumns(
        string sql,
        Dictionary<string, HashSet<string>> tables)
    {
        foreach (Match create in CreateTable().Matches(sql))
        {
            string table = create.Groups["table"].Value.ToLowerInvariant();
            if (!QuotaBoundaryDatabaseTables.Contains(table, StringComparer.Ordinal))
            {
                continue;
            }

            int bodyStart = create.Index + create.Length - 1;
            int bodyEnd = FindMatching(sql, bodyStart, '(', ')');
            Assert.True(bodyEnd >= 0, $"CREATE TABLE {table} has no closing delimiter.");
            HashSet<string> columns = DatabaseTable(tables, table);
            foreach (string definition in SplitSqlTopLevel(
                sql[(bodyStart + 1)..bodyEnd],
                ','))
            {
                AddSqlColumnDefinition(definition, columns);
            }
        }
    }

    private static void AddAlterTableColumns(
        string sql,
        Dictionary<string, HashSet<string>> tables)
    {
        foreach (Match alter in AlterTable().Matches(sql))
        {
            string table = alter.Groups["table"].Value.ToLowerInvariant();
            if (!QuotaBoundaryDatabaseTables.Contains(table, StringComparer.Ordinal))
            {
                continue;
            }

            int statementEnd = sql.IndexOf(';', alter.Index + alter.Length);
            string actions = statementEnd < 0
                ? sql[(alter.Index + alter.Length)..]
                : sql[(alter.Index + alter.Length)..statementEnd];
            HashSet<string> columns = DatabaseTable(tables, table);
            foreach (string action in SplitSqlTopLevel(actions, ','))
            {
                Match add = AlterAddColumn().Match(action);
                if (!add.Success)
                {
                    continue;
                }

                string column = add.Groups["column"].Value.ToLowerInvariant();
                if (column is not "constraint"
                    and not "primary"
                    and not "foreign"
                    and not "unique"
                    and not "check")
                {
                    columns.Add(column);
                }
            }
        }
    }

    private static HashSet<string> DatabaseTable(
        Dictionary<string, HashSet<string>> tables,
        string table)
    {
        if (!tables.TryGetValue(table, out HashSet<string>? columns))
        {
            columns = new HashSet<string>(StringComparer.Ordinal);
            tables.Add(table, columns);
        }

        return columns;
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
        string relativePath = Path.GetRelativePath(root, path).Replace(
            Path.DirectorySeparatorChar,
            '/');
        foreach (Match declaration in TypeDeclaration().Matches(code))
        {
            string kind = declaration.Groups["kind"].Value;
            string name = declaration.Groups["name"].Value;
            HashSet<string> members = new(StringComparer.Ordinal);
            int cursor = SkipWhitespace(code, declaration.Index + declaration.Length);

            if (cursor < code.Length && code[cursor] == '<')
            {
                int genericEnd = FindMatching(code, cursor, '<', '>');
                if (genericEnd < 0)
                {
                    continue;
                }

                cursor = SkipWhitespace(code, genericEnd + 1);
            }

            if (cursor < code.Length && code[cursor] == '(')
            {
                int parametersEnd = FindMatching(code, cursor, '(', ')');
                if (parametersEnd < 0)
                {
                    continue;
                }

                AddParameterMembers(code[(cursor + 1)..parametersEnd], members);
                cursor = parametersEnd + 1;
            }

            int bodyStart = FindTypeBodyStart(code, cursor);
            if (bodyStart >= 0 && code[bodyStart] == '{')
            {
                int bodyEnd = FindMatching(code, bodyStart, '{', '}');
                if (bodyEnd >= 0)
                {
                    AddDirectBodyMembers(code, bodyStart, bodyEnd, members);
                }
            }

            yield return new CSharpTypeShape(
                relativePath,
                name,
                IsDataTransferType(relativePath, kind, name),
                members);
        }
    }

    private static bool IsDataTransferType(string relativePath, string kind, string name) =>
        kind.StartsWith("record", StringComparison.Ordinal)
        || relativePath.Contains("/Generated/", StringComparison.Ordinal)
        || DataTransferTypeMarkers.Any(marker => name.Contains(
            marker,
            StringComparison.Ordinal))
        || name is "User" or "ApiKey" or "Subscription" or "Account";

    private static void AddParameterMembers(
        string parameters,
        HashSet<string> members)
    {
        foreach (string parameter in SplitTopLevel(parameters, ','))
        {
            string declaration = TruncateInitializer(parameter);
            MatchCollection identifiers = Identifier().Matches(declaration);
            if (identifiers.Count > 0)
            {
                members.Add(identifiers[^1].Value.TrimStart('@'));
            }
        }
    }

    private static void AddDirectBodyMembers(
        string source,
        int bodyStart,
        int bodyEnd,
        HashSet<string> members)
    {
        int depth = 0;
        int statementStart = bodyStart + 1;
        for (int index = bodyStart + 1; index < bodyEnd; index++)
        {
            switch (source[index])
            {
                case '{':
                    if (depth == 0
                        && IsAccessorAt(source, SkipWhitespace(source, index + 1)))
                    {
                        AddDeclaredMember(source[statementStart..index], members);
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
                    AddDeclaredMember(source[statementStart..index], members);
                    statementStart = index + 1;
                    break;
            }
        }
    }

    private static bool IsAccessorAt(string source, int index) =>
        IsIdentifierAt(source, index, "get")
        || IsIdentifierAt(source, index, "init");

    private static bool IsIdentifierAt(string source, int index, string identifier) =>
        index + identifier.Length <= source.Length
        && source.AsSpan(index, identifier.Length).SequenceEqual(identifier)
        && (index + identifier.Length == source.Length
            || !IsIdentifierCharacter(source[index + identifier.Length]));

    private static bool IsIdentifierCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character)
        || character is '_';

    private static void AddDeclaredMember(string declaration, HashSet<string> members)
    {
        string candidate = TruncateInitializer(declaration);
        int expressionBody = candidate.IndexOf("=>", StringComparison.Ordinal);
        if (expressionBody >= 0)
        {
            candidate = candidate[..expressionBody];
        }

        MatchCollection identifiers = Identifier().Matches(candidate);
        if (identifiers.Count == 0)
        {
            return;
        }

        string member = identifiers[^1].Value.TrimStart('@');
        if (member is not "get" and not "init" and not "set")
        {
            members.Add(member);
        }
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
                default:
                    if (source[index] == separator
                        && round == 0
                        && square == 0
                        && angle == 0)
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

    private static IEnumerable<string> ReadConfigurationKeys(string source) =>
        ConfigurationKey()
            .Matches(MaskComments(source))
            .Select(static match => match.Groups["key"].Value);

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
        char[] buffer = source.ToCharArray();
        for (int index = 0; index < source.Length; index++)
        {
            if (source[index] == '-'
                && index + 1 < source.Length
                && source[index + 1] == '-')
            {
                int end = FindLineEnd(source, index + 2);
                MaskRange(buffer, index, end);
                index = end - 1;
                continue;
            }

            if (source[index] == '/'
                && index + 1 < source.Length
                && source[index + 1] == '*')
            {
                int end = FindBlockCommentEnd(source, index + 2);
                MaskRange(buffer, index, end);
                index = end - 1;
                continue;
            }

            if (source[index] is '\'' or '"')
            {
                int end = FindSqlQuotedEnd(source, index, source[index]);
                MaskRange(buffer, index, end);
                index = end - 1;
                continue;
            }

            if (source[index] == '$'
                && TryFindDollarQuoteEnd(source, index, out int dollarEnd))
            {
                MaskRange(buffer, index, dollarEnd);
                index = dollarEnd - 1;
            }
        }

        return new string(buffer);
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

    [GeneratedRegex(
        @"\b(?:(?:public|internal|private|protected|file|sealed|abstract|static|partial|readonly|ref)\s+)*(?<kind>record(?:\s+(?:class|struct))?|class|struct)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex TypeDeclaration();

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
        "\"(?<key>[A-Za-z][A-Za-z0-9]*(?:(?::|__)[A-Za-z][A-Za-z0-9]*)+)\"",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex ConfigurationKey();

    [GeneratedRegex(
        @"\bCREATE\s+(?:UNLOGGED\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?:[a-z_][a-z0-9_$]*)\.)?(?<table>[a-z_][a-z0-9_$]*)\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex CreateTable();

    [GeneratedRegex(
        @"\bALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?(?:(?:[a-z_][a-z0-9_$]*)\.)?(?<table>[a-z_][a-z0-9_$]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex AlterTable();

    [GeneratedRegex(
        @"^\s*ADD\s+(?:COLUMN\s+)?(?:IF\s+NOT\s+EXISTS\s+)?(?<column>[a-z_][a-z0-9_$]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex AlterAddColumn();

    [GeneratedRegex(
        @"^\s*(?<identifier>[a-z_][a-z0-9_$]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex SqlLeadingIdentifier();

    private sealed record CSharpTypeShape(
        string RelativePath,
        string Name,
        bool IsDataTransferType,
        IReadOnlySet<string> Members);
}
