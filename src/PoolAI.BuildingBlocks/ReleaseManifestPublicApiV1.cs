using System.Text.Json.Serialization;

namespace PoolAI.BuildingBlocks;

public sealed record ReleaseManifestPublicApiV1(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("sha256")] string Sha256);
