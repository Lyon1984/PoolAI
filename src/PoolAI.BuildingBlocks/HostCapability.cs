namespace PoolAI.BuildingBlocks;

[Flags]
public enum HostCapability
{
    None = 0,
    Api = 1,
    Worker = 2,
    Migrator = 4,
}
