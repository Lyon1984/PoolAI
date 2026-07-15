using System.Text.Json.Serialization;

namespace PoolAI.BuildingBlocks;

public sealed record ReleaseManifestRedisV1(
    [property: JsonPropertyName("required_server_major")] int RequiredServerMajor,
    [property: JsonPropertyName("contract_version")] int ContractVersion,
    [property: JsonPropertyName("key_schema_version")] string KeySchemaVersion,
    [property: JsonPropertyName("contract_source")] string ContractSource,
    [property: JsonPropertyName("contract_sha256")] string ContractSha256,
    [property: JsonPropertyName("scripts")] IReadOnlyList<ReleaseManifestRedisScriptV1> Scripts);
