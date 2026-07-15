using System.Text;
using PoolAI.BuildingBlocks;

namespace PoolAI.UnitTests;

public sealed class ReleaseManifestV1LoaderTests
{
    private const string ApiSha =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MigrationOneSha =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string MigrationTwoSha =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string RedisContractSha =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string ScriptSha =
        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    [Fact]
    public void ValidManifestWithNoRedisScriptsLoads()
    {
        ReleaseManifestV1 manifest = Load(ValidManifestJson());

        Assert.Equal(1, manifest.ManifestVersion);
        Assert.Equal("r1.1", manifest.ReleaseId);
        Assert.Equal("docs/contracts/openapi-v1.yaml", manifest.PublicApi.Source);
        Assert.Equal(18, manifest.Postgres.RequiredServerMajor);
        Assert.Equal(2, manifest.Postgres.MinimumCompatibleVersion);
        Assert.Equal(2, manifest.Postgres.MaximumCompatibleVersion);
        Assert.Collection(
            manifest.Postgres.Migrations,
            migration => Assert.Equal("0001_baseline.sql", migration.Name),
            migration => Assert.Equal("0002_quota_functions.sql", migration.Name));
        Assert.Equal("r1", manifest.Redis.KeySchemaVersion);
        Assert.Empty(manifest.Redis.Scripts);
    }

    [Fact]
    public void ScriptNameDoesNotNeedToContainItsLogicalVersion()
    {
        string scripts = $$"""
            [
              {
                "name": "lease_acquire",
                "logical_version": 1,
                "source": "docs/runtime/scripts/lease_acquire_v1.lua",
                "body_sha256": "{{ScriptSha}}"
              }
            ]
            """;

        ReleaseManifestV1 manifest = Load(ValidManifestJson(scripts));

        ReleaseManifestRedisScriptV1 script = Assert.Single(manifest.Redis.Scripts);
        Assert.Equal("lease_acquire", script.Name);
        Assert.Equal(1, script.LogicalVersion);
    }

    [Theory]
    [InlineData("\"manifest_version\": 1,", "\"manifest_version\": 1,\n  \"unknown_root\": true,")]
    [InlineData("\"schema_version\": 1,", "\"schema_version\": 1,\n    \"unknown_api\": true,")]
    [InlineData("\"required_server_major\": 18,", "\"required_server_major\": 18,\n    \"unknown_postgres\": true,")]
    [InlineData("\"contract_version\": 1,", "\"contract_version\": 1,\n    \"unknown_redis\": true,")]
    public void UnknownFieldsAreRejectedAtEveryManifestLevel(
        string existing,
        string replacement)
    {
        string json = ReplaceOnce(ValidManifestJson(), existing, replacement);

        _ = Assert.Throws<ReleaseManifestValidationException>(() => Load(json));
    }

    [Theory]
    [InlineData("\"manifest_version\"", "\"ManifestVersion\"")]
    [InlineData("\"public_api\"", "\"publicApi\"")]
    [InlineData("\"required_server_major\"", "\"requiredServerMajor\"")]
    [InlineData("\"key_schema_version\"", "\"KeySchemaVersion\"")]
    public void NonSnakeCasePropertyNamesAreRejected(string existing, string replacement)
    {
        string json = ReplaceOnce(ValidManifestJson(), existing, replacement);

        _ = Assert.Throws<ReleaseManifestValidationException>(() => Load(json));
    }

    [Fact]
    public void MissingRequiredFieldIsRejected()
    {
        string json = ReplaceOnce(ValidManifestJson(), "  \"release_id\": \"r1.1\",\n", string.Empty);

        _ = Assert.Throws<ReleaseManifestValidationException>(() => Load(json));
    }

    [Fact]
    public void DuplicateJsonPropertyIsRejected()
    {
        string json = ReplaceOnce(
            ValidManifestJson(),
            "\"manifest_version\": 1,",
            "\"manifest_version\": 1,\n  \"manifest_version\": 1,");

        ReleaseManifestValidationException exception =
            Assert.Throws<ReleaseManifestValidationException>(() => Load(json));

        Assert.Contains("duplicate property", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "Aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Sha256MustBeExactlySixtyFourLowercaseHexCharacters(
        string existing,
        string replacement)
    {
        string json = ReplaceOnce(ValidManifestJson(), existing, replacement);

        _ = Assert.Throws<ReleaseManifestValidationException>(() => Load(json));
    }

    [Theory]
    [InlineData("\"manifest_version\": 1", "\"manifest_version\": 2")]
    [InlineData("\"required_server_major\": 18", "\"required_server_major\": 0")]
    [InlineData("\"minimum_compatible_version\": 2", "\"minimum_compatible_version\": 0")]
    [InlineData("\"maximum_compatible_version\": 2", "\"maximum_compatible_version\": 1")]
    [InlineData("\"version\": 2,\n        \"name\": \"0002", "\"version\": 3,\n        \"name\": \"0002")]
    [InlineData("\"contract_version\": 1", "\"contract_version\": 0")]
    [InlineData("\"key_schema_version\": \"r1\"", "\"key_schema_version\": \"r0\"")]
    public void InvalidVersionShapesAreRejected(string existing, string replacement)
    {
        string json = ReplaceOnce(ValidManifestJson(), existing, replacement);

        _ = Assert.Throws<ReleaseManifestValidationException>(() => Load(json));
    }

    [Theory]
    [InlineData("0001_baseline.sql", "0002_baseline.sql")]
    [InlineData("docs/database/0001_baseline.sql", "docs/database/../0001_baseline.sql")]
    [InlineData("docs/database/0001_baseline.sql", "docs/database/0002_quota_functions.sql")]
    public void MigrationNameAndSourceMustMatchTheContiguousVersionPrefix(
        string existing,
        string replacement)
    {
        string json = ReplaceOnce(ValidManifestJson(), existing, replacement);

        _ = Assert.Throws<ReleaseManifestValidationException>(() => Load(json));
    }

    [Fact]
    public void DuplicateRedisScriptIdentityIsRejected()
    {
        string scripts = $$"""
            [
              {
                "name": "lease_acquire",
                "logical_version": 1,
                "source": "docs/runtime/scripts/lease_acquire_v1.lua",
                "body_sha256": "{{ScriptSha}}"
              },
              {
                "name": "lease_acquire",
                "logical_version": 1,
                "source": "docs/runtime/scripts/lease_acquire_compatible.lua",
                "body_sha256": "{{ScriptSha}}"
              }
            ]
            """;

        _ = Assert.Throws<ReleaseManifestValidationException>(() =>
            Load(ValidManifestJson(scripts)));
    }

    [Theory]
    [InlineData("LeaseAcquire")]
    [InlineData("lease__acquire")]
    [InlineData("lease-acquire")]
    public void RedisScriptLogicalNameMustBeLowerSnakeCase(string name)
    {
        string scripts = $$"""
            [
              {
                "name": "{{name}}",
                "logical_version": 1,
                "source": "docs/runtime/scripts/lease_acquire_v1.lua",
                "body_sha256": "{{ScriptSha}}"
              }
            ]
            """;

        _ = Assert.Throws<ReleaseManifestValidationException>(() =>
            Load(ValidManifestJson(scripts)));
    }

    [Theory]
    [InlineData("docs/runtime/lease_acquire_v1.lua")]
    [InlineData("docs/runtime/scripts/nested/lease_acquire_v1.lua")]
    [InlineData("docs/runtime/scripts/lease_acquire_v1.txt")]
    public void RedisScriptSourceMustBeADirectLuaFileInTheContractDirectory(string source)
    {
        string scripts = $$"""
            [
              {
                "name": "lease_acquire",
                "logical_version": 1,
                "source": "{{source}}",
                "body_sha256": "{{ScriptSha}}"
              }
            ]
            """;

        _ = Assert.Throws<ReleaseManifestValidationException>(() =>
            Load(ValidManifestJson(scripts)));
    }

    [Fact]
    public void EmbeddedLoaderRequiresExactlyOneMatchingResource()
    {
        _ = Assert.Throws<ReleaseManifestValidationException>(() =>
            ReleaseManifestV1Loader.LoadEmbedded(
                typeof(ReleaseManifestV1LoaderTests).Assembly,
                ".missing-release-manifest-v1.json"));
    }

    private static ReleaseManifestV1 Load(string json) =>
        ReleaseManifestV1Loader.Load(Encoding.UTF8.GetBytes(json));

    private static string ReplaceOnce(string value, string existing, string replacement)
    {
        int index = value.IndexOf(existing, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Test input did not contain the expected text: {existing}");
        return string.Concat(value.AsSpan(0, index), replacement, value.AsSpan(index + existing.Length));
    }

    private static string ValidManifestJson(string scripts = "[]") => $$"""
        {
          "manifest_version": 1,
          "release_id": "r1.1",
          "public_api": {
            "schema_version": 1,
            "source": "docs/contracts/openapi-v1.yaml",
            "sha256": "{{ApiSha}}"
          },
          "postgres": {
            "required_server_major": 18,
            "minimum_compatible_version": 2,
            "maximum_compatible_version": 2,
            "migrations": [
              {
                "version": 1,
                "name": "0001_baseline.sql",
                "source": "docs/database/0001_baseline.sql",
                "sha256": "{{MigrationOneSha}}"
              },
              {
                "version": 2,
                "name": "0002_quota_functions.sql",
                "source": "docs/database/0002_quota_functions.sql",
                "sha256": "{{MigrationTwoSha}}"
              }
            ]
          },
          "redis": {
            "required_server_major": 8,
            "contract_version": 1,
            "key_schema_version": "r1",
            "contract_source": "docs/runtime/redis-contract.md",
            "contract_sha256": "{{RedisContractSha}}",
            "scripts": {{scripts}}
          }
        }
        """;
}
