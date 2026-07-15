using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoolAI.BuildingBlocks;

public static class ReleaseManifestV1Loader
{
    private const int MaximumManifestBytes = 1_048_576;
    private const int MaximumJsonDepth = 16;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = MaximumJsonDepth,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static ReleaseManifestV1 Load(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaximumManifestBytes)
        {
            throw new ReleaseManifestValidationException(
                "Release manifest v1 JSON size is invalid.");
        }

        byte[] payload = utf8Json.ToArray();
        ValidateNoDuplicateProperties(payload);

        ReleaseManifestV1 manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ReleaseManifestV1>(payload, SerializerOptions)
                ?? throw new ReleaseManifestValidationException(
                    "Release manifest v1 JSON must contain an object.");
        }
        catch (JsonException exception)
        {
            throw new ReleaseManifestValidationException(
                "Release manifest v1 JSON is invalid.",
                exception);
        }

        Validate(manifest);
        return manifest;
    }

    public static ReleaseManifestV1 LoadEmbedded(Assembly assembly, string resourceNameSuffix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceNameSuffix);

        string[] matches = assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(resourceNameSuffix, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ReleaseManifestValidationException(
                "Exactly one embedded release manifest v1 resource must match the requested suffix.");
        }

        using Stream resource = assembly.GetManifestResourceStream(matches[0])
            ?? throw new ReleaseManifestValidationException(
                "The embedded release manifest v1 resource could not be opened.");
        using MemoryStream buffer = new();
        resource.CopyTo(buffer);
        return Load(buffer.ToArray());
    }

    private static void Validate(ReleaseManifestV1 manifest)
    {
        if (manifest.ManifestVersion != 1)
        {
            Invalid("manifest_version must be 1");
        }

        if (!IsReleaseId(manifest.ReleaseId))
        {
            Invalid("release_id is invalid");
        }

        ValidatePublicApi(manifest.PublicApi);
        ValidatePostgres(manifest.Postgres);
        ValidateRedis(manifest.Redis);
    }

    private static void ValidatePublicApi(ReleaseManifestPublicApiV1 publicApi)
    {
        if (publicApi is null)
        {
            Invalid("public_api is required");
        }

        if (publicApi.SchemaVersion <= 0)
        {
            Invalid("public_api.schema_version must be positive");
        }

        string expectedSource = string.Create(
            CultureInfo.InvariantCulture,
            $"docs/contracts/openapi-v{publicApi.SchemaVersion}.yaml");
        if (!IsCanonicalRepositoryPath(publicApi.Source)
            || !string.Equals(publicApi.Source, expectedSource, StringComparison.Ordinal))
        {
            Invalid("public_api.source does not match schema_version");
        }

        ValidateSha256(publicApi.Sha256, "public_api.sha256");
    }

    private static void ValidatePostgres(ReleaseManifestPostgresV1 postgres)
    {
        if (postgres is null)
        {
            Invalid("postgres is required");
        }

        if (postgres.RequiredServerMajor <= 0)
        {
            Invalid("postgres.required_server_major must be positive");
        }

        if (postgres.Migrations is null || postgres.Migrations.Count == 0)
        {
            Invalid("postgres.migrations must not be empty");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        for (int index = 0; index < postgres.Migrations.Count; index++)
        {
            ReleaseManifestPostgresMigrationV1 migration = postgres.Migrations[index];
            long expectedVersion = index + 1L;
            if (migration is null || migration.Version != expectedVersion)
            {
                Invalid("postgres.migrations versions must form a contiguous prefix starting at 1");
            }

            if (!IsMigrationName(migration.Name, migration.Version))
            {
                Invalid("postgres.migrations name does not match its version prefix");
            }

            if (!names.Add(migration.Name))
            {
                Invalid("postgres.migrations names must be unique");
            }

            string expectedSource = $"docs/database/{migration.Name}";
            if (!IsCanonicalRepositoryPath(migration.Source)
                || !string.Equals(migration.Source, expectedSource, StringComparison.Ordinal))
            {
                Invalid("postgres.migrations source does not match its name");
            }

            ValidateSha256(migration.Sha256, "postgres.migrations.sha256");
        }

        long lastVersion = postgres.Migrations[^1].Version;
        if (postgres.MaximumCompatibleVersion != lastVersion)
        {
            Invalid("postgres.maximum_compatible_version must equal the last migration version");
        }

        if (postgres.MinimumCompatibleVersion < 1
            || postgres.MinimumCompatibleVersion > postgres.MaximumCompatibleVersion)
        {
            Invalid("postgres.minimum_compatible_version is outside the supported range");
        }
    }

    private static void ValidateRedis(ReleaseManifestRedisV1 redis)
    {
        if (redis is null)
        {
            Invalid("redis is required");
        }

        if (redis.RequiredServerMajor <= 0)
        {
            Invalid("redis.required_server_major must be positive");
        }

        if (redis.ContractVersion <= 0)
        {
            Invalid("redis.contract_version must be positive");
        }

        if (!IsKeySchemaVersion(redis.KeySchemaVersion))
        {
            Invalid("redis.key_schema_version is invalid");
        }

        if (!IsCanonicalRepositoryPath(redis.ContractSource)
            || !redis.ContractSource.StartsWith("docs/runtime/", StringComparison.Ordinal)
            || !redis.ContractSource.EndsWith(".md", StringComparison.Ordinal))
        {
            Invalid("redis.contract_source must identify a runtime Markdown contract");
        }

        ValidateSha256(redis.ContractSha256, "redis.contract_sha256");
        if (redis.Scripts is null)
        {
            Invalid("redis.scripts is required");
        }

        HashSet<(string Name, int Version)> identities = [];
        foreach (ReleaseManifestRedisScriptV1 script in redis.Scripts)
        {
            if (script is null || !IsLogicalName(script.Name))
            {
                Invalid("redis.scripts name is invalid");
            }

            if (script.LogicalVersion <= 0)
            {
                Invalid("redis.scripts logical_version must be positive");
            }

            if (!identities.Add((script.Name, script.LogicalVersion)))
            {
                Invalid("redis.scripts name and logical_version must be unique");
            }

            if (!IsRedisScriptSource(script.Source))
            {
                Invalid("redis.scripts source must identify a direct Lua file under docs/runtime/scripts");
            }

            ValidateSha256(script.BodySha256, "redis.scripts.body_sha256");
        }
    }

    private static void ValidateNoDuplicateProperties(byte[] payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            ValidateNoDuplicateProperties(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ReleaseManifestValidationException(
                "Release manifest v1 JSON is invalid.",
                exception);
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    Invalid("Release manifest v1 JSON contains a duplicate property");
                }

                ValidateNoDuplicateProperties(property.Value);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in element.EnumerateArray())
        {
            ValidateNoDuplicateProperties(item);
        }
    }

    private static bool IsMigrationName(string value, long version)
    {
        if (string.IsNullOrEmpty(value) || version is < 1 or > 9_999)
        {
            return false;
        }

        string prefix = version.ToString("D4", CultureInfo.InvariantCulture);
        if (!value.StartsWith($"{prefix}_", StringComparison.Ordinal)
            || !value.EndsWith(".sql", StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> slug = value.AsSpan(prefix.Length + 1, value.Length - prefix.Length - 5);
        return IsLowerSnakeCase(slug);
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

    private static bool IsLogicalName(string value) =>
        !string.IsNullOrEmpty(value)
        && IsLowerSnakeCase(value.AsSpan());

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

    private static void ValidateSha256(string value, string path)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64)
        {
            Invalid($"{path} must be a lowercase SHA-256 digest");
        }

        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9')
                && character is not (>= 'a' and <= 'f'))
            {
                Invalid($"{path} must be a lowercase SHA-256 digest");
            }
        }
    }

    [DoesNotReturn]
    private static void Invalid(string message) =>
        throw new ReleaseManifestValidationException(message);
}
