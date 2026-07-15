using System.Text.Json.Serialization;

namespace PoolAI.BuildingBlocks;

public sealed record ReleaseManifestPostgresMigrationV1(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("sha256")] string Sha256);
