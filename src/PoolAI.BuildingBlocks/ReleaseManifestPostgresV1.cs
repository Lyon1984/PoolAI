using System.Text.Json.Serialization;

namespace PoolAI.BuildingBlocks;

public sealed record ReleaseManifestPostgresV1(
    [property: JsonPropertyName("required_server_major")] int RequiredServerMajor,
    [property: JsonPropertyName("minimum_compatible_version")] long MinimumCompatibleVersion,
    [property: JsonPropertyName("maximum_compatible_version")] long MaximumCompatibleVersion,
    [property: JsonPropertyName("migrations")] IReadOnlyList<ReleaseManifestPostgresMigrationV1> Migrations);
