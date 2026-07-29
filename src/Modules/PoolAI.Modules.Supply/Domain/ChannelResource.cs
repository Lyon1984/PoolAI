#pragma warning disable MA0048 // The immutable Channel value types form one aggregate snapshot.
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Supply.Domain;

internal enum ChannelResourceStatus
{
    Active,
    Disabled,
    Retired,
}

internal sealed record ChannelCapabilitiesValue(
    bool Responses,
    bool ChatCompletions,
    bool FunctionTools,
    bool Streaming);

internal sealed record ChannelModelMappingValue(
    string ClientModel,
    string UpstreamModel);

internal sealed record ChannelResource(
    EntityId Id,
    UpstreamProvider Provider,
    string Name,
    ChannelResourceStatus Status,
    ChannelCapabilitiesValue Capabilities,
    IReadOnlyList<ChannelModelMappingValue> ModelMappings,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
#pragma warning restore MA0048
