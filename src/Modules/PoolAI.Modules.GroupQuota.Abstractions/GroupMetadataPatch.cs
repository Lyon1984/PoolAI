namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupMetadataPatch(
    bool HasName,
    string? Name,
    bool HasDescription,
    string? Description,
    bool HasRequestsPerMinute = false,
    int? RequestsPerMinute = null);
