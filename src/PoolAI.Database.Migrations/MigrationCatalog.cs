using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoolAI.Database.Migrations;

public sealed class MigrationCatalog
{
    private const int MaximumManifestBytes = 1_048_576;

    private MigrationCatalog(
        int requiredPostgresServerMajor,
        IReadOnlyList<MigrationAsset> assets)
    {
        RequiredPostgresServerMajor = requiredPostgresServerMajor;
        Assets = assets;
    }

    public int RequiredPostgresServerMajor { get; }

    public IReadOnlyList<MigrationAsset> Assets { get; }

    public static async ValueTask<MigrationCatalog> LoadAsync(CancellationToken cancellationToken)
    {
        Assembly assembly = typeof(MigrationCatalog).Assembly;
        MigrationManifestProjection releaseManifest = await LoadManifestAsync(
            assembly,
            cancellationToken).ConfigureAwait(false);
        string[] resourceNames = assembly.GetManifestResourceNames();
        List<MigrationAsset> assets = new(releaseManifest.Migrations.Count);

        foreach (MigrationManifestEntry migration in releaseManifest.Migrations)
        {
            string resourceName = resourceNames.Single(candidate =>
                candidate.EndsWith($".Migrations.{migration.Name}", StringComparison.Ordinal));

            using Stream resource = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded migration resource not found: {migration.Name}.");
            using MemoryStream buffer = new();
            await resource.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            byte[] bytes = buffer.ToArray();
            string checksum = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(checksum, migration.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "An embedded migration does not match its release manifest checksum.");
            }

            string sql = new UTF8Encoding(false, true).GetString(bytes);
            assets.Add(new MigrationAsset(
                migration.Version,
                migration.Name,
                checksum,
                sql));
        }

        return new MigrationCatalog(releaseManifest.RequiredServerMajor, assets);
    }

    private static async ValueTask<MigrationManifestProjection> LoadManifestAsync(
        Assembly assembly,
        CancellationToken cancellationToken)
    {
        string[] matches = assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(
                "poolai-release-manifest-v1.json",
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw InvalidManifest();
        }

        using Stream resource = assembly.GetManifestResourceStream(matches[0])
            ?? throw InvalidManifest();
        if (!resource.CanSeek
            || resource.Length is <= 0 or > MaximumManifestBytes)
        {
            throw InvalidManifest();
        }

        using JsonDocument document = await JsonDocument
            .ParseAsync(
                resource,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                },
                cancellationToken)
            .ConfigureAwait(false);
        return ParseManifest(document.RootElement);
    }

    private static MigrationManifestProjection ParseManifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                root,
                "manifest_version",
                "release_id",
                "public_api",
                "postgres",
                "redis")
            || !root.TryGetProperty("manifest_version", out JsonElement manifestVersion)
            || !manifestVersion.TryGetInt32(out int version)
            || version != 1
            || !TryGetString(root, "release_id", out string releaseId)
            || !IsReleaseId(releaseId)
            || !root.TryGetProperty("public_api", out JsonElement publicApi)
            || publicApi.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("postgres", out JsonElement postgres)
            || postgres.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("redis", out JsonElement redis)
            || redis.ValueKind != JsonValueKind.Object)
        {
            throw InvalidManifest();
        }

        ValidatePublicApi(publicApi);
        ValidateRedis(redis);
        return ParsePostgresManifest(postgres);
    }

    private static MigrationManifestProjection ParsePostgresManifest(JsonElement postgres)
    {
        if (!HasExactProperties(
                postgres,
                "required_server_major",
                "minimum_compatible_version",
                "maximum_compatible_version",
                "migrations")
            || !postgres.TryGetProperty("required_server_major", out JsonElement serverMajor)
            || !serverMajor.TryGetInt32(out int requiredServerMajor)
            || requiredServerMajor <= 0
            || !postgres.TryGetProperty("minimum_compatible_version", out JsonElement minimum)
            || !minimum.TryGetInt64(out long minimumCompatibleVersion)
            || !postgres.TryGetProperty("maximum_compatible_version", out JsonElement maximum)
            || !maximum.TryGetInt64(out long maximumCompatibleVersion)
            || !postgres.TryGetProperty("migrations", out JsonElement migrations)
            || migrations.ValueKind != JsonValueKind.Array
            || migrations.GetArrayLength() == 0)
        {
            throw InvalidManifest();
        }

        List<MigrationManifestEntry> entries = new(migrations.GetArrayLength());
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonElement migration in migrations.EnumerateArray())
        {
            entries.Add(ParseMigration(migration, entries.Count + 1L, names));
        }

        long lastVersion = entries[^1].Version;
        if (minimumCompatibleVersion < 1
            || minimumCompatibleVersion > maximumCompatibleVersion
            || maximumCompatibleVersion != lastVersion)
        {
            throw InvalidManifest();
        }

        return new MigrationManifestProjection(requiredServerMajor, entries);
    }

    private static MigrationManifestEntry ParseMigration(
        JsonElement migration,
        long expectedVersion,
        HashSet<string> names)
    {
        if (migration.ValueKind != JsonValueKind.Object
            || !HasExactProperties(migration, "version", "name", "source", "sha256")
            || !migration.TryGetProperty("version", out JsonElement migrationVersion)
            || !migrationVersion.TryGetInt64(out long actualVersion)
            || actualVersion != expectedVersion
            || !TryGetString(migration, "name", out string name)
            || !IsMigrationName(name, actualVersion)
            || !names.Add(name)
            || !TryGetString(migration, "source", out string source)
            || !string.Equals(source, $"docs/database/{name}", StringComparison.Ordinal)
            || !TryGetString(migration, "sha256", out string sha256)
            || !IsLowerSha256(sha256))
        {
            throw InvalidManifest();
        }

        return new MigrationManifestEntry(actualVersion, name, sha256);
    }

    private static void ValidatePublicApi(JsonElement publicApi)
    {
        if (!HasExactProperties(publicApi, "schema_version", "source", "sha256")
            || !publicApi.TryGetProperty("schema_version", out JsonElement schemaVersionProperty)
            || !schemaVersionProperty.TryGetInt32(out int schemaVersion)
            || schemaVersion <= 0)
        {
            throw InvalidManifest();
        }

        string expectedSource = string.Create(
            CultureInfo.InvariantCulture,
            $"docs/contracts/openapi-v{schemaVersion}.yaml");
        if (!TryGetString(publicApi, "source", out string source)
            || !string.Equals(source, expectedSource, StringComparison.Ordinal)
            || !IsCanonicalRepositoryPath(source)
            || !TryGetString(publicApi, "sha256", out string sha256)
            || !IsLowerSha256(sha256))
        {
            throw InvalidManifest();
        }
    }

    private static void ValidateRedis(JsonElement redis)
    {
        if (!HasExactProperties(
                redis,
                "required_server_major",
                "contract_version",
                "key_schema_version",
                "contract_source",
                "contract_sha256",
                "scripts")
            || !redis.TryGetProperty("required_server_major", out JsonElement serverMajor)
            || !serverMajor.TryGetInt32(out int requiredServerMajor)
            || requiredServerMajor <= 0
            || !redis.TryGetProperty("contract_version", out JsonElement contractVersion)
            || !contractVersion.TryGetInt32(out int actualContractVersion)
            || actualContractVersion <= 0
            || !TryGetString(redis, "key_schema_version", out string keySchemaVersion)
            || !IsKeySchemaVersion(keySchemaVersion)
            || !TryGetString(redis, "contract_source", out string contractSource)
            || !IsCanonicalRepositoryPath(contractSource)
            || !contractSource.StartsWith("docs/runtime/", StringComparison.Ordinal)
            || !contractSource.EndsWith(".md", StringComparison.Ordinal)
            || !TryGetString(redis, "contract_sha256", out string contractSha256)
            || !IsLowerSha256(contractSha256)
            || !redis.TryGetProperty("scripts", out JsonElement scripts)
            || scripts.ValueKind != JsonValueKind.Array)
        {
            throw InvalidManifest();
        }

        HashSet<(string Name, int Version)> identities = [];
        foreach (JsonElement script in scripts.EnumerateArray())
        {
            ValidateRedisScript(script, identities);
        }
    }

    private static void ValidateRedisScript(
        JsonElement script,
        HashSet<(string Name, int Version)> identities)
    {
        if (script.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                script,
                "name",
                "logical_version",
                "source",
                "body_sha256")
            || !TryGetString(script, "name", out string name)
            || !IsLowerSnakeCase(name.AsSpan())
            || !script.TryGetProperty("logical_version", out JsonElement versionProperty)
            || !versionProperty.TryGetInt32(out int logicalVersion)
            || logicalVersion <= 0
            || !identities.Add((name, logicalVersion))
            || !TryGetString(script, "source", out string source)
            || !IsRedisScriptSource(source)
            || !TryGetString(script, "body_sha256", out string bodySha256)
            || !IsLowerSha256(bodySha256))
        {
            throw InvalidManifest();
        }
    }

    private static bool HasExactProperties(JsonElement element, params string[] expectedNames)
    {
        HashSet<string> expected = new(expectedNames, StringComparer.Ordinal);
        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !observed.Add(property.Name))
            {
                return false;
            }
        }

        return observed.SetEquals(expected);
    }

    private static bool TryGetString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        if (element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } actual)
        {
            value = actual;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsMigrationName(string value, long version)
    {
        if (string.IsNullOrEmpty(value) || version is < 1 or > 9_999)
        {
            return false;
        }

        string prefix = version.ToString("D4", CultureInfo.InvariantCulture);
        return value.StartsWith($"{prefix}_", StringComparison.Ordinal)
            && value.EndsWith(".sql", StringComparison.Ordinal)
            && IsLowerSnakeCase(value.AsSpan(prefix.Length + 1, value.Length - prefix.Length - 5));
    }

    private static bool IsRedisScriptSource(string value)
    {
        const string Prefix = "docs/runtime/scripts/";
        if (!IsCanonicalRepositoryPath(value)
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || !value.EndsWith(".lua", StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> fileName = value.AsSpan(Prefix.Length);
        return fileName.Length > 4 && !fileName.Contains('/');
    }

    private static bool IsCanonicalRepositoryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value[0] == '/'
            || value[^1] == '/'
            || value.Contains('\\')
            || value.Contains(':'))
        {
            return false;
        }

        string[] segments = value.Split('/');
        return segments.All(segment =>
            segment.Length > 0
            && segment is not "." and not ".."
            && segment.All(IsRepositoryPathCharacter));
    }

    private static bool IsRepositoryPathCharacter(char value) =>
        value is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9'
        or '.'
        or '-'
        or '_';

    private static bool IsReleaseId(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != 'r')
        {
            return false;
        }

        int separator = value.IndexOf('.');
        return separator > 1
            && separator == value.LastIndexOf('.')
            && separator < value.Length - 1
            && IsPositiveDecimal(value.AsSpan(1, separator - 1))
            && IsDecimal(value.AsSpan(separator + 1));
    }

    private static bool IsKeySchemaVersion(string value) =>
        !string.IsNullOrEmpty(value)
        && value[0] == 'r'
        && IsPositiveDecimal(value.AsSpan(1));

    private static bool IsLowerSnakeCase(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty
            || value[0] is not (>= 'a' and <= 'z')
            || value[^1] == '_')
        {
            return false;
        }

        bool previousUnderscore = false;
        foreach (char character in value)
        {
            bool isUnderscore = character == '_';
            if (character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9')
                && !isUnderscore)
            {
                return false;
            }

            if (isUnderscore && previousUnderscore)
            {
                return false;
            }

            previousUnderscore = isUnderscore;
        }

        return true;
    }

    private static bool IsPositiveDecimal(ReadOnlySpan<char> value) =>
        !value.IsEmpty && value[0] != '0' && IsDecimal(value);

    private static bool IsDecimal(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerSha256(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static InvalidOperationException InvalidManifest() =>
        new("The embedded release manifest is invalid.");

    private sealed record MigrationManifestProjection(
        int RequiredServerMajor,
        IReadOnlyList<MigrationManifestEntry> Migrations);

    private sealed record MigrationManifestEntry(
        long Version,
        string Name,
        string Sha256);
}
