using System.Text.Json.Serialization;

namespace PoolAI.BuildingBlocks;

public sealed record ReleaseManifestV1(
    [property: JsonPropertyName("manifest_version")] int ManifestVersion,
    [property: JsonPropertyName("release_id")] string ReleaseId,
    [property: JsonPropertyName("public_api")] ReleaseManifestPublicApiV1 PublicApi,
    [property: JsonPropertyName("postgres")] ReleaseManifestPostgresV1 Postgres,
    [property: JsonPropertyName("redis")] ReleaseManifestRedisV1 Redis);
