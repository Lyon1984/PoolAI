using PoolAI.BuildingBlocks;

namespace PoolAI.IntegrationTests;

internal static class M3E2EntityIdExtensions
{
    internal static EntityId AsEntityId(this Guid value) => new(value);
}
