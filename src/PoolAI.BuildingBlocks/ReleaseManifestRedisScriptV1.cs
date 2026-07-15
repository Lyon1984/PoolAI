using System.Text.Json.Serialization;

namespace PoolAI.BuildingBlocks;

public sealed record ReleaseManifestRedisScriptV1(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("logical_version")] int LogicalVersion,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("body_sha256")] string BodySha256);
